using MySqlConnector;
using SmartFireApp.Models;

namespace SmartFireApp.Services;

/// <summary>
/// Quản lý toàn bộ truy vấn CSDL SmartFireDB trực tiếp từ MySQL (XAMPP / Docker).
/// Không dùng bất kỳ dữ liệu mẫu nào. Bao gồm trọn vẹn 5 bảng chuẩn:
/// Users, Zones, Devices, EnvironmentLogs, AlertHistory.
/// </summary>
public sealed class DatabaseService
{
    private string _connectionString;
    private string _activeDbName = "smartfiredb";

    public DatabaseService(string? connectionString = null)
    {
        // Ưu tiên smartfiredb — database thật của dự án trên XAMPP
        _connectionString = connectionString 
            ?? "Server=localhost;Port=3306;Database=smartfiredb;Uid=root;Pwd=;CharSet=utf8mb4;";
    }

    private async Task<MySqlConnection> OpenConnectionAsync()
    {
        // Thứ tự ưu tiên: smartfiredb → SmartFireDB → smart_fire_warning
        string[] candidateDbs = { "smartfiredb", "SmartFireDB", "smart_fire_warning" };
        string[] candidatePasswords = { "", "YourStrong@Passw0rd" };

        foreach (var db in candidateDbs)
        {
            foreach (var pwd in candidatePasswords)
            {
                var connStr = $"Server=localhost;Port=3306;Database={db};Uid=root;Pwd={pwd};CharSet=utf8mb4;Connect Timeout=2;";
                try
                {
                    var conn = new MySqlConnection(connStr);
                    await conn.OpenAsync();
                    _connectionString = connStr;
                    _activeDbName = db;
                    return conn;
                }
                catch
                {
                    // Thử tiếp ứng viên khác
                }
            }
        }

        // Fallback cuối: không chỉ định DB
        var defaultConn = new MySqlConnection("Server=localhost;Port=3306;Uid=root;Pwd=;CharSet=utf8mb4;");
        await defaultConn.OpenAsync();
        return defaultConn;
    }

    /// <summary>
    /// Lấy toàn bộ dữ liệu thật từ các bảng trong SmartFireDB để render trực tiếp lên View.
    /// </summary>
    public async Task<RealDashboardData> FetchRealDataAsync()
    {
        var users = new List<UserModel>();
        var devices = new List<DeviceCardModel>();
        var alerts = new List<AlertModel>();
        var logs = new List<EnvironmentLogModel>();
        int totalDevices = 0;
        int activeDevices = 0;
        int pendingAlerts = 0;
        int totalLogs = 0;
        int totalUsers = 0;
        bool isConnected = false;

        try
        {
            await using var conn = await OpenConnectionAsync();
            isConnected = true;

            // 1. Lấy danh sách cán bộ / người dùng từ bảng Users
            const string sqlUsers = @"
                SELECT UserId, Username, FullName, PhoneNumber, Email, Role, CreatedAt
                FROM Users
                ORDER BY UserId ASC;";
            await using (var cmd = new MySqlCommand(sqlUsers, conn))
            await using (var r = await cmd.ExecuteReaderAsync())
            {
                while (await r.ReadAsync())
                {
                    totalUsers++;
                    users.Add(new UserModel(
                        UserId: r.GetInt32("UserId"),
                        Username: r.GetString("Username"),
                        FullName: r.GetString("FullName"),
                        PhoneNumber: r.IsDBNull(r.GetOrdinal("PhoneNumber")) ? null : r.GetString("PhoneNumber"),
                        Email: r.IsDBNull(r.GetOrdinal("Email")) ? null : r.GetString("Email"),
                        Role: r.GetString("Role"),
                        CreatedAt: r.GetDateTime("CreatedAt")
                    ));
                }
            }

            // 2. Lấy danh sách thiết bị và số đo mới nhất của từng thiết bị
            const string sqlDevices = @"
                SELECT d.DeviceId, d.DeviceName, COALESCE(z.ZoneName, 'Chưa phân khu') AS ZoneName,
                       d.DeviceType, d.Status, d.IpAddress,
                       COALESCE(latest.Temperature, 0) AS Temperature,
                       COALESCE(latest.Humidity, 0) AS Humidity,
                       COALESCE(latest.GasPpm, 0) AS GasPpm,
                       COALESCE(latest.AiResult, FALSE) AS AiResult,
                       latest.ConfidenceScore,
                       latest.RecordedAt
                FROM Devices d
                LEFT JOIN Zones z ON d.ZoneId = z.ZoneId
                LEFT JOIN (
                    SELECT e1.*
                    FROM EnvironmentLogs e1
                    INNER JOIN (
                        SELECT DeviceId, MAX(LogId) AS MaxLogId
                        FROM EnvironmentLogs
                        GROUP BY DeviceId
                    ) e2 ON e1.LogId = e2.MaxLogId
                ) latest ON d.DeviceId = latest.DeviceId
                ORDER BY d.DeviceId ASC;";

            await using (var cmd = new MySqlCommand(sqlDevices, conn))
            await using (var r = await cmd.ExecuteReaderAsync())
            {
                while (await r.ReadAsync())
                {
                    totalDevices++;
                    string status = r.GetString("Status");
                    if (status.Equals("Active", StringComparison.OrdinalIgnoreCase))
                        activeDevices++;

                    devices.Add(new DeviceCardModel(
                        DeviceId: r.GetString("DeviceId"),
                        DeviceName: r.GetString("DeviceName"),
                        ZoneName: r.GetString("ZoneName"),
                        DeviceType: r.GetString("DeviceType"),
                        Status: status,
                        IpAddress: r.IsDBNull(r.GetOrdinal("IpAddress")) ? null : r.GetString("IpAddress"),
                        Temperature: Convert.ToDouble(r["Temperature"]),
                        Humidity: Convert.ToDouble(r["Humidity"]),
                        GasPpm: Convert.ToDouble(r["GasPpm"]),
                        AiResult: Convert.ToBoolean(r["AiResult"]),
                        ConfidenceScore: r.IsDBNull(r.GetOrdinal("ConfidenceScore")) ? null : Convert.ToDouble(r["ConfidenceScore"]),
                        RecordedAt: r.IsDBNull(r.GetOrdinal("RecordedAt")) ? null : r.GetDateTime("RecordedAt")
                    ));
                }
            }

            // 3. Lấy danh sách cảnh báo từ AlertHistory
            const string sqlAlerts = @"
                SELECT a.AlertId, a.DeviceId, COALESCE(d.DeviceName, a.DeviceId) AS DeviceName,
                       COALESCE(z.ZoneName, 'Khu vực') AS ZoneName,
                       a.AlertType, a.Severity, a.AlertTime, a.SmsSent, a.Acknowledged
                FROM AlertHistory a
                LEFT JOIN Devices d ON a.DeviceId = d.DeviceId
                LEFT JOIN Zones z ON d.ZoneId = z.ZoneId
                ORDER BY a.AlertTime DESC
                LIMIT 20;";

            await using (var cmd = new MySqlCommand(sqlAlerts, conn))
            await using (var r = await cmd.ExecuteReaderAsync())
            {
                while (await r.ReadAsync())
                {
                    bool ack = r.GetBoolean("Acknowledged");
                    if (!ack) pendingAlerts++;

                    alerts.Add(new AlertModel(
                        AlertId: r.GetInt64("AlertId"),
                        DeviceId: r.GetString("DeviceId"),
                        DeviceName: r.GetString("DeviceName"),
                        ZoneName: r.GetString("ZoneName"),
                        AlertType: r.GetString("AlertType"),
                        Severity: r.GetString("Severity"),
                        AlertTime: r.GetDateTime("AlertTime"),
                        SmsSent: r.GetBoolean("SmsSent"),
                        Acknowledged: ack
                    ));
                }
            }

            // 4. Lấy 20 bản ghi mới nhất từ EnvironmentLogs
            const string sqlLogs = @"
                SELECT e.LogId, e.DeviceId, COALESCE(d.DeviceName, e.DeviceId) AS DeviceName,
                       COALESCE(e.Temperature, 0) AS Temperature,
                       COALESCE(e.Humidity, 0) AS Humidity,
                       COALESCE(e.GasPpm, 0) AS GasPpm,
                       e.AiResult, e.ConfidenceScore, e.RecordedAt
                FROM EnvironmentLogs e
                LEFT JOIN Devices d ON e.DeviceId = d.DeviceId
                ORDER BY e.RecordedAt DESC
                LIMIT 20;";

            await using (var cmd = new MySqlCommand(sqlLogs, conn))
            await using (var r = await cmd.ExecuteReaderAsync())
            {
                while (await r.ReadAsync())
                {
                    totalLogs++;
                    logs.Add(new EnvironmentLogModel(
                        LogId: r.GetInt64("LogId"),
                        DeviceId: r.GetString("DeviceId"),
                        DeviceName: r.GetString("DeviceName"),
                        Temperature: Convert.ToDouble(r["Temperature"]),
                        Humidity: Convert.ToDouble(r["Humidity"]),
                        GasPpm: Convert.ToDouble(r["GasPpm"]),
                        AiResult: r.GetBoolean("AiResult"),
                        ConfidenceScore: r.IsDBNull(r.GetOrdinal("ConfidenceScore")) ? null : Convert.ToDouble(r["ConfidenceScore"]),
                        RecordedAt: r.GetDateTime("RecordedAt")
                    ));
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MySQL FetchRealData Error] {ex.Message}");
        }

        var kpis = new DatabaseKpis(
            TotalDevices: totalDevices,
            ActiveDevices: activeDevices,
            PendingAlerts: pendingAlerts,
            TotalLogs: totalLogs,
            TotalUsers: totalUsers,
            DatabaseName: _activeDbName,
            ServerHost: "localhost:3306 (XAMPP)"
        );

        return new RealDashboardData(kpis, users, devices, alerts, logs, isConnected);
    }

    /// <summary>
    /// Cập nhật trạng thái sự cố đã được xử lý (Acknowledged = TRUE).
    /// </summary>
    public async Task<bool> AcknowledgeAlertAsync(long alertId, int userId = 1)
    {
        const string sql = @"
            UPDATE AlertHistory 
            SET Acknowledged = TRUE, AcknowledgedByUserId = @UserId, AcknowledgedAt = NOW()
            WHERE AlertId = @AlertId;";

        try
        {
            await using var conn = await OpenConnectionAsync();
            await using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@AlertId", alertId);
            cmd.Parameters.AddWithValue("@UserId", userId);
            int rows = await cmd.ExecuteNonQueryAsync();
            return rows > 0;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MySQL AcknowledgeAlert Error] {ex.Message}");
            return false;
        }
    }
}
