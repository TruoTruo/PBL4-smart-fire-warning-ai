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
    // ⚠ SỬA CHO ĐÚNG MÁY CỦA BẠN (cổng, mật khẩu). Hoặc đặt biến môi trường SMARTFIRE_DB
    // để khỏi ghi mật khẩu vào code (đừng commit mật khẩu thật lên Git).
    private const string DefaultConnectionString =
        "Server=127.0.0.1;Port=3307;Database=SmartFireDB;Uid=root;Pwd=28040528;CharSet=utf8mb4;Connect Timeout=5;";

    private readonly string _connectionString;
    private readonly string _serverHost;
    private string _activeDbName = "SmartFireDB";

    public DatabaseService(string? connectionString = null)
    {
        _connectionString = connectionString
            ?? Environment.GetEnvironmentVariable("SMARTFIRE_DB")
            ?? DefaultConnectionString;

        var b = new MySqlConnectionStringBuilder(_connectionString);
        _serverHost = $"{b.Server}:{b.Port}";
    }

    private async Task<MySqlConnection> OpenConnectionAsync()
    {
        // Dùng ĐÚNG 1 chuỗi kết nối (bản cũ tự đoán cổng 3306 + vài mật khẩu mặc định nên
        // không bao giờ kết nối được tới MySQL cổng 3307 của bạn, và mỗi lần thử mất ~12 giây).
        var conn = new MySqlConnection(_connectionString);
        try
        {
            await conn.OpenAsync();
            _activeDbName = conn.Database;
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
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

            // 5. Số liệu tổng thật (không bị giới hạn bởi LIMIT 20 ở trên)
            await using (var cmd = new MySqlCommand(
                @"SELECT (SELECT COUNT(*) FROM EnvironmentLogs),
                         (SELECT COUNT(*) FROM AlertHistory WHERE Acknowledged = FALSE);", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
            {
                if (await r.ReadAsync())
                {
                    totalLogs = Convert.ToInt32(r.GetValue(0));
                    pendingAlerts = Convert.ToInt32(r.GetValue(1));
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
            ServerHost: _serverHost
        );

        return new RealDashboardData(kpis, users, devices, alerts, logs, isConnected);
    }

    public static string HashPassword(string password)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(password);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool VerifyPassword(string inputPassword, string storedHash)
    {
        if (string.IsNullOrEmpty(storedHash)) return false;

        // 1. Kiểm tra khớp SHA-256
        var hash = HashPassword(inputPassword);
        if (string.Equals(hash, storedHash, StringComparison.OrdinalIgnoreCase))
            return true;

        // 2. Hỗ trợ password cho các tài khoản seed ban đầu ($2a$placeholder)
        if (storedHash.StartsWith("$2a$") || storedHash.Contains("placeholder"))
        {
            if (inputPassword == "admin" || inputPassword == "123456" || inputPassword == "admin123" || inputPassword == "supervisor1")
                return true;
        }

        // 3. Fallback so sánh trực tiếp
        return string.Equals(inputPassword, storedHash, StringComparison.Ordinal);
    }

    /// <summary>
    /// Đăng nhập tài khoản bằng Username và Password từ bảng Users.
    /// </summary>
    public async Task<UserModel?> LoginUserAsync(string username, string password)
    {
        const string sql = @"
            SELECT UserId, Username, PasswordHash, FullName, PhoneNumber, Email, Role, CreatedAt
            FROM Users
            WHERE Username = @Username
            LIMIT 1;";

        try
        {
            await using var conn = await OpenConnectionAsync();
            await using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Username", username.Trim());

            await using var r = await cmd.ExecuteReaderAsync();
            if (await r.ReadAsync())
            {
                string storedHash = r.GetString("PasswordHash");
                if (VerifyPassword(password, storedHash))
                {
                    return new UserModel(
                        UserId: r.GetInt32("UserId"),
                        Username: r.GetString("Username"),
                        FullName: r.GetString("FullName"),
                        PhoneNumber: r.IsDBNull(r.GetOrdinal("PhoneNumber")) ? null : r.GetString("PhoneNumber"),
                        Email: r.IsDBNull(r.GetOrdinal("Email")) ? null : r.GetString("Email"),
                        Role: r.GetString("Role"),
                        CreatedAt: r.GetDateTime("CreatedAt")
                    );
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MySQL LoginUser Error] {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Đăng ký tài khoản mới và lưu trực tiếp vào bảng Users.
    /// </summary>
    public async Task<(bool Success, UserModel? User, string? Error)> RegisterUserAsync(
        string username, string password, string fullName, string? phoneNumber, string? email, string role = "User")
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(fullName))
        {
            return (false, null, "Vui lòng nhập đầy đủ tên đăng nhập, mật khẩu và họ tên.");
        }

        username = username.Trim();
        fullName = fullName.Trim();
        phoneNumber = string.IsNullOrWhiteSpace(phoneNumber) ? null : phoneNumber.Trim();
        email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        role = string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase) ? "Admin" : "User";

        string passwordHash = HashPassword(password);

        try
        {
            await using var conn = await OpenConnectionAsync();

            // 1. Kiểm tra trùng username
            const string checkSql = "SELECT COUNT(*) FROM Users WHERE Username = @Username;";
            await using (var checkCmd = new MySqlCommand(checkSql, conn))
            {
                checkCmd.Parameters.AddWithValue("@Username", username);
                long exists = Convert.ToInt64(await checkCmd.ExecuteScalarAsync());
                if (exists > 0)
                {
                    return (false, null, $"Tên đăng nhập '{username}' đã tồn tại trong hệ thống.");
                }
            }

            // 2. Chèn tài khoản mới
            const string insertSql = @"
                INSERT INTO Users (Username, PasswordHash, FullName, PhoneNumber, Email, Role, CreatedAt)
                VALUES (@Username, @PasswordHash, @FullName, @PhoneNumber, @Email, @Role, NOW());";

            await using (var insertCmd = new MySqlCommand(insertSql, conn))
            {
                insertCmd.Parameters.AddWithValue("@Username", username);
                insertCmd.Parameters.AddWithValue("@PasswordHash", passwordHash);
                insertCmd.Parameters.AddWithValue("@FullName", fullName);
                insertCmd.Parameters.AddWithValue("@PhoneNumber", (object?)phoneNumber ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("@Email", (object?)email ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("@Role", role);

                await insertCmd.ExecuteNonQueryAsync();
                int newId = (int)insertCmd.LastInsertedId;

                var newUser = new UserModel(
                    UserId: newId,
                    Username: username,
                    FullName: fullName,
                    PhoneNumber: phoneNumber,
                    Email: email,
                    Role: role,
                    CreatedAt: DateTime.Now
                );

                return (true, newUser, null);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MySQL RegisterUser Error] {ex.Message}");
            return (false, null, "Lỗi CSDL khi đăng ký: " + ex.Message);
        }
    }

    /// <summary>
    /// Xóa tài khoản khỏi bảng Users theo UserId.
    /// </summary>
    public async Task<bool> DeleteUserAsync(int userId)
    {
        const string sql = "DELETE FROM Users WHERE UserId = @UserId;";
        try
        {
            await using var conn = await OpenConnectionAsync();
            await using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@UserId", userId);
            int rows = await cmd.ExecuteNonQueryAsync();
            return rows > 0;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MySQL DeleteUser Error] {ex.Message}");
            return false;
        }
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