using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MySqlConnector;
using SmartFireWeb.Models;

namespace SmartFireWeb.Services;

/// <summary>
/// Quản lý toàn bộ truy vấn CSDL SmartFireDB trực tiếp từ MySQL (XAMPP / Docker).
/// Không dùng bất kỳ dữ liệu mẫu nào. Bao gồm trọn vẹn 5 bảng chuẩn:
/// Users, Zones, Devices, EnvironmentLogs, AlertHistory.
/// </summary>
public sealed class DatabaseService
{
    private readonly string _connectionString;
    private readonly string _activeDbName;
    private readonly string _serverHost;

    /// <param name="connectionString">
    /// Chuỗi kết nối MySQL. Bỏ trống thì đọc từ appsettings.json / appsettings.Local.json /
    /// biến môi trường Database__ConnectionString (xem AppSettings.cs).
    /// </param>
    public DatabaseService(string? connectionString = null)
    {
        _connectionString = connectionString ?? AppSettings.Load().ConnectionString;

        var csb = new MySqlConnectionStringBuilder(_connectionString);
        _activeDbName = string.IsNullOrEmpty(csb.Database) ? "(chưa chọn DB)" : csb.Database;
        _serverHost = $"{csb.Server}:{csb.Port}";
    }

    private async Task<MySqlConnection> OpenConnectionAsync()
    {
        var conn = new MySqlConnection(_connectionString);
        try
        {
            await conn.OpenAsync();
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
        string? error = null;

        try
        {
            await using var conn = await OpenConnectionAsync();

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
                       latest.Temperature, latest.Humidity,
                       COALESCE(latest.SmokeDetected, FALSE) AS SmokeDetected,
                       latest.SmokeLevel,
                       COALESCE(latest.AlertLevel, 0) AS AlertLevel,
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
                        Temperature: GetNullableDouble(r, "Temperature"),
                        Humidity: GetNullableDouble(r, "Humidity"),
                        SmokeDetected: Convert.ToBoolean(r["SmokeDetected"]),
                        SmokeLevel: GetNullableDouble(r, "SmokeLevel"),
                        AlertLevel: Convert.ToInt32(r["AlertLevel"]),
                        AiResult: Convert.ToBoolean(r["AiResult"]),
                        ConfidenceScore: r.IsDBNull(r.GetOrdinal("ConfidenceScore")) ? null : Convert.ToDouble(r["ConfidenceScore"]),
                        RecordedAt: r.IsDBNull(r.GetOrdinal("RecordedAt")) ? null : r.GetDateTime("RecordedAt")
                    ));
                }
            }

            // 3. 20 cảnh báo mới nhất (chỉ cờ có ảnh, không kéo ảnh Base64) + tổng số chưa xử lý
            const string sqlAlerts = AlertSelectSql + @"
                ORDER BY a.AlertTime DESC, a.AlertId DESC
                LIMIT 20;";

            await using (var cmd = new MySqlCommand(sqlAlerts, conn))
            await using (var r = await cmd.ExecuteReaderAsync())
            {
                while (await r.ReadAsync())
                    alerts.Add(ReadAlert(r));
            }

            await using (var cmd = new MySqlCommand("SELECT COUNT(*) FROM AlertHistory WHERE Acknowledged = FALSE;", conn))
                pendingAlerts = Convert.ToInt32(await cmd.ExecuteScalarAsync());

            // 4. Lấy 20 bản ghi mới nhất từ EnvironmentLogs
            const string sqlLogs = @"
                SELECT e.LogId, e.DeviceId, COALESCE(d.DeviceName, e.DeviceId) AS DeviceName,
                       e.Temperature, e.Humidity,
                       COALESCE(e.SmokeDetected, FALSE) AS SmokeDetected,
                       e.SmokeLevel,
                       COALESCE(e.AlertLevel, 0) AS AlertLevel,
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
                        Temperature: GetNullableDouble(r, "Temperature"),
                        Humidity: GetNullableDouble(r, "Humidity"),
                        SmokeDetected: Convert.ToBoolean(r["SmokeDetected"]),
                        SmokeLevel: GetNullableDouble(r, "SmokeLevel"),
                        AlertLevel: Convert.ToInt32(r["AlertLevel"]),
                        AiResult: Convert.ToBoolean(r["AiResult"]),
                        ConfidenceScore: r.IsDBNull(r.GetOrdinal("ConfidenceScore")) ? null : Convert.ToDouble(r["ConfidenceScore"]),
                        RecordedAt: r.GetDateTime("RecordedAt")
                    ));
                }
            }

            // Chỉ coi là kết nối tốt khi cả 4 truy vấn đều chạy xong
            isConnected = true;
        }
        catch (Exception ex)
        {
            error = DescribeDbError(ex);
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

        return new RealDashboardData(kpis, users, devices, alerts, logs, isConnected, error);
    }

    private static double? GetNullableDouble(MySqlDataReader r, string column)
    {
        int i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture);
    }

    private string DescribeDbError(Exception ex) => ex is MySqlException { ErrorCode: MySqlErrorCode.UnableToConnectToHost }
        ? $"Không kết nối được MySQL tại {_serverHost}: {ex.Message}"
        : $"Lỗi truy vấn CSDL {_activeDbName}: {ex.Message}";

    // Cột dùng chung cho mọi truy vấn danh sách cảnh báo
    private const string AlertSelectSql = @"
        SELECT a.AlertId, a.DeviceId, COALESCE(d.DeviceName, a.DeviceId) AS DeviceName,
               COALESCE(z.ZoneName, 'Khu vực') AS ZoneName,
               a.AlertType, a.Severity, a.AlertTime, a.SmsSent, a.Acknowledged,
               (a.SnapshotBase64 IS NOT NULL AND a.SnapshotBase64 <> '') AS HasSnapshot,
               u.FullName AS AcknowledgedByName, a.AcknowledgedAt
        FROM AlertHistory a
        LEFT JOIN Devices d ON a.DeviceId = d.DeviceId
        LEFT JOIN Zones z ON d.ZoneId = z.ZoneId
        LEFT JOIN Users u ON a.AcknowledgedByUserId = u.UserId";

    private static AlertModel ReadAlert(MySqlDataReader r) => new(
        AlertId: r.GetInt64("AlertId"),
        DeviceId: r.GetString("DeviceId"),
        DeviceName: r.GetString("DeviceName"),
        ZoneName: r.GetString("ZoneName"),
        AlertType: r.GetString("AlertType"),
        Severity: r.GetString("Severity"),
        AlertTime: r.GetDateTime("AlertTime"),
        SmsSent: r.GetBoolean("SmsSent"),
        Acknowledged: r.GetBoolean("Acknowledged"),
        HasSnapshot: Convert.ToBoolean(r["HasSnapshot"]),
        AcknowledgedByName: r.IsDBNull(r.GetOrdinal("AcknowledgedByName")) ? null : r.GetString("AcknowledgedByName"),
        AcknowledgedAt: r.IsDBNull(r.GetOrdinal("AcknowledgedAt")) ? null : r.GetDateTime("AcknowledgedAt"));

    /// <summary>
    /// Lọc lịch sử cảnh báo theo khoảng thời gian, loại, mức độ, trạng thái xử lý (tối đa 500 dòng mới nhất).
    /// Lỗi CSDL được ném ra cho nơi gọi xử lý.
    /// </summary>
    public async Task<List<AlertModel>> GetAlertsByDateRangeAsync(
        DateTime? fromDate, DateTime? toDate, string? alertType = null, string? severity = null, bool? acknowledged = null)
    {
        // Chỉ thêm điều kiện cho bộ lọc có giá trị: so sánh thẳng AlertTime để MySQL dùng được chỉ mục IX_AlertHistory_Time
        var where = new List<string>();
        await using var conn = await OpenConnectionAsync();
        await using var cmd = new MySqlCommand { Connection = conn };
        if (fromDate is DateTime from) { where.Add("a.AlertTime >= @FromDate"); cmd.Parameters.AddWithValue("@FromDate", from); }
        if (toDate is DateTime to) { where.Add("a.AlertTime <= @ToDate"); cmd.Parameters.AddWithValue("@ToDate", to); }
        if (!string.IsNullOrWhiteSpace(alertType)) { where.Add("a.AlertType = @AlertType"); cmd.Parameters.AddWithValue("@AlertType", alertType); }
        if (!string.IsNullOrWhiteSpace(severity)) { where.Add("a.Severity = @Severity"); cmd.Parameters.AddWithValue("@Severity", severity); }
        if (acknowledged is bool ack) { where.Add("a.Acknowledged = @Ack"); cmd.Parameters.AddWithValue("@Ack", ack); }

        cmd.CommandText = AlertSelectSql
            + (where.Count > 0 ? "\n        WHERE " + string.Join(" AND ", where) : "")
            + "\n        ORDER BY a.AlertTime DESC, a.AlertId DESC\n        LIMIT 500;";

        var list = new List<AlertModel>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(ReadAlert(r));
        return list;
    }

    /// <summary>
    /// Thống kê số lượng cảnh báo theo từng ngày và loại cảnh báo trong khoảng thời gian đã chọn.
    /// Lỗi CSDL được ném ra cho nơi gọi xử lý.
    /// </summary>
    public async Task<List<AlertStatItem>> GetAlertStatsAsync(DateTime fromDate, DateTime toDate)
    {
        var stats = new List<AlertStatItem>();
        const string sql = @"
            SELECT DATE_FORMAT(AlertTime, '%Y-%m-%d') AS DateLabel, AlertType, COUNT(*) AS Cnt
            FROM AlertHistory
            WHERE AlertTime BETWEEN @FromDate AND @ToDate
            GROUP BY DATE(AlertTime), DATE_FORMAT(AlertTime, '%Y-%m-%d'), AlertType
            ORDER BY DATE(AlertTime) ASC;";

        await using var conn = await OpenConnectionAsync();
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@FromDate", fromDate);
        cmd.Parameters.AddWithValue("@ToDate", toDate);

        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            stats.Add(new AlertStatItem(
                DateLabel: r.GetString("DateLabel"),
                AlertType: r.GetString("AlertType"),
                Count: Convert.ToInt32(r["Cnt"])
            ));
        }
        return stats;
    }

    /// <summary>
    /// Chuỗi nhiệt độ / độ ẩm của một thiết bị từ thời điểm cho trước (trung bình theo khung, tối đa ~maxPoints điểm, tăng dần).
    /// Lỗi CSDL được ném ra cho nơi gọi xử lý.
    /// </summary>
    public async Task<List<EnvironmentPoint>> GetEnvironmentSeriesAsync(string deviceId, DateTime fromTime, int maxPoints = 720)
    {
        var points = new List<EnvironmentPoint>();
        // Gộp theo khung thời gian để khoảng dài (24 giờ) vẫn trả về tối đa ~maxPoints điểm
        maxPoints = Math.Clamp(maxPoints, 10, 5000);
        int spanSeconds = (int)Math.Max(1, (DateTime.Now - fromTime).TotalSeconds);
        int bucket = Math.Max(1, (int)Math.Ceiling(spanSeconds / (double)maxPoints));
        const string sql = @"
            SELECT MIN(RecordedAt) AS StartAt, AVG(Temperature) AS Temperature,
                   AVG(Humidity) AS Humidity, MAX(AlertLevel) AS AlertLevel
            FROM EnvironmentLogs
            WHERE DeviceId = @DeviceId AND RecordedAt >= @From
            GROUP BY TIMESTAMPDIFF(SECOND, @From, RecordedAt) DIV @Bucket
            ORDER BY StartAt ASC
            LIMIT @Max;";

        await using var conn = await OpenConnectionAsync();
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@DeviceId", deviceId);
        cmd.Parameters.AddWithValue("@From", fromTime);
        cmd.Parameters.AddWithValue("@Bucket", bucket);
        cmd.Parameters.AddWithValue("@Max", maxPoints + 10);

        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            points.Add(new EnvironmentPoint(
                RecordedAt: r.GetDateTime("StartAt"),
                Temperature: GetNullableDouble(r, "Temperature"),
                Humidity: GetNullableDouble(r, "Humidity"),
                AlertLevel: Convert.ToInt32(r["AlertLevel"])));
        }
        return points;
    }

    // PBKDF2-SHA256, salt ngẫu nhiên 16 byte. Lưu dạng: pbkdf2$<số vòng>$<salt Base64>$<hash Base64>
    private const string Pbkdf2Prefix = "pbkdf2$";
    private const int Pbkdf2Iterations = 210_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    // Hash giả để so sánh khi sai tên đăng nhập, tránh đoán tài khoản qua thời gian phản hồi
    private static readonly Lazy<string> DummyHash = new(() => HashPassword(Guid.NewGuid().ToString("N")));

    public static string HashPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt,
            Pbkdf2Iterations, HashAlgorithmName.SHA256, HashSize);
        return Pbkdf2Prefix + Pbkdf2Iterations.ToString(CultureInfo.InvariantCulture)
            + "$" + Convert.ToBase64String(salt) + "$" + Convert.ToBase64String(hash);
    }

    public static bool VerifyPassword(string inputPassword, string storedHash)
    {
        if (string.IsNullOrEmpty(inputPassword) || string.IsNullOrEmpty(storedHash)) return false;

        if (storedHash.StartsWith(Pbkdf2Prefix, StringComparison.Ordinal))
        {
            if (!TryParsePbkdf2(storedHash, out int iterations, out byte[] salt, out byte[] expected)) return false;
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(inputPassword), salt,
                iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }

        // Hash cũ: SHA-256 hex không salt (tài khoản tạo bằng bản trước). Đăng nhập đúng sẽ được băm lại bằng PBKDF2.
        if (IsLegacySha256(storedHash))
        {
            byte[] actual = SHA256.HashData(Encoding.UTF8.GetBytes(inputPassword));
            return CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(storedHash));
        }

        // Mọi dạng khác (chuỗi placeholder, mật khẩu thô...) đều bị từ chối
        return false;
    }

    /// <summary>Hash cũ (SHA-256) hoặc PBKDF2 ít vòng hơn hiện tại thì nên băm lại.</summary>
    public static bool NeedsRehash(string storedHash) =>
        !TryParsePbkdf2(storedHash, out int iterations, out _, out _) || iterations < Pbkdf2Iterations;

    private static bool IsLegacySha256(string storedHash) =>
        storedHash.Length == 64 && storedHash.All(Uri.IsHexDigit);

    private static bool TryParsePbkdf2(string storedHash, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0;
        salt = hash = Array.Empty<byte>();
        if (!storedHash.StartsWith(Pbkdf2Prefix, StringComparison.Ordinal)) return false;

        string[] parts = storedHash.Split('$');
        if (parts.Length != 4
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out iterations)
            || iterations < 10_000 || iterations > 5_000_000)
            return false;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            hash = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }
        return salt.Length >= 8 && hash.Length >= 16;
    }

    /// <summary>
    /// Đăng nhập tài khoản bằng Username và Password từ bảng Users.
    /// </summary>
    public async Task<UserModel?> LoginUserAsync(string username, string password) =>
        (await TryLoginAsync(username, password)).User;

    /// <summary>
    /// Như LoginUserAsync nhưng phân biệt "sai mật khẩu" (User = null, Error = null) với lỗi CSDL (Error khác null).
    /// </summary>
    public async Task<(UserModel? User, string? Error)> TryLoginAsync(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password)) return (null, null);

        const string sql = @"
            SELECT UserId, Username, PasswordHash, FullName, PhoneNumber, Email, Role, CreatedAt
            FROM Users
            WHERE Username = @Username
            LIMIT 1;";

        try
        {
            await using var conn = await OpenConnectionAsync();
            UserModel? user = null;
            string? storedHash = null;

            await using (var cmd = new MySqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@Username", username.Trim());
                await using var r = await cmd.ExecuteReaderAsync();
                if (await r.ReadAsync())
                {
                    storedHash = r.GetString("PasswordHash");
                    user = ReadUser(r);
                }
            }

            if (user == null || storedHash == null)
            {
                VerifyPassword(password, DummyHash.Value);
                return (null, null);
            }

            if (!VerifyPassword(password, storedHash)) return (null, null);

            if (NeedsRehash(storedHash))
            {
                try
                {
                    await using var up = new MySqlCommand("UPDATE Users SET PasswordHash = @Hash WHERE UserId = @UserId;", conn);
                    up.Parameters.AddWithValue("@Hash", HashPassword(password));
                    up.Parameters.AddWithValue("@UserId", user.UserId);
                    await up.ExecuteNonQueryAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MySQL Rehash Error] {ex.Message}");
                }
            }

            return (user, null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MySQL LoginUser Error] {ex.Message}");
            return (null, DescribeDbError(ex));
        }
    }

    /// <summary>Đổi mật khẩu: phải nhập đúng mật khẩu hiện tại.</summary>
    public async Task<(bool Success, string? Error)> ChangePasswordAsync(int userId, string currentPassword, string newPassword)
    {
        if (string.IsNullOrEmpty(newPassword) || newPassword.Length < 6)
            return (false, "Mật khẩu mới phải có ít nhất 6 ký tự.");

        try
        {
            await using var conn = await OpenConnectionAsync();
            string? storedHash;
            await using (var cmd = new MySqlCommand("SELECT PasswordHash FROM Users WHERE UserId = @UserId LIMIT 1;", conn))
            {
                cmd.Parameters.AddWithValue("@UserId", userId);
                storedHash = await cmd.ExecuteScalarAsync() as string;
            }
            if (storedHash == null) return (false, "Tài khoản không còn tồn tại.");
            if (!VerifyPassword(currentPassword, storedHash)) return (false, "Mật khẩu hiện tại không đúng.");

            await using var up = new MySqlCommand("UPDATE Users SET PasswordHash = @Hash WHERE UserId = @UserId;", conn);
            up.Parameters.AddWithValue("@Hash", HashPassword(newPassword));
            up.Parameters.AddWithValue("@UserId", userId);
            await up.ExecuteNonQueryAsync();
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, DescribeDbError(ex));
        }
    }

    /// <summary>Lấy một tài khoản theo UserId (null nếu không còn). Lỗi CSDL được ném ra cho nơi gọi xử lý.</summary>
    public async Task<UserModel?> GetUserByIdAsync(int userId)
    {
        const string sql = @"
            SELECT UserId, Username, FullName, PhoneNumber, Email, Role, CreatedAt
            FROM Users
            WHERE UserId = @UserId
            LIMIT 1;";

        await using var conn = await OpenConnectionAsync();
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@UserId", userId);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? ReadUser(r) : null;
    }

    private static UserModel ReadUser(MySqlDataReader r) => new(
        UserId: r.GetInt32("UserId"),
        Username: r.GetString("Username"),
        FullName: r.GetString("FullName"),
        PhoneNumber: r.IsDBNull(r.GetOrdinal("PhoneNumber")) ? null : r.GetString("PhoneNumber"),
        Email: r.IsDBNull(r.GetOrdinal("Email")) ? null : r.GetString("Email"),
        Role: r.GetString("Role"),
        CreatedAt: r.GetDateTime("CreatedAt"));

    /// <summary>Ảnh bằng chứng (Base64) của một cảnh báo, null nếu không có. Lỗi CSDL được ném ra.</summary>
    public async Task<string?> GetAlertSnapshotAsync(long alertId)
    {
        const string sql = "SELECT SnapshotBase64 FROM AlertHistory WHERE AlertId = @AlertId LIMIT 1;";
        await using var conn = await OpenConnectionAsync();
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@AlertId", alertId);
        var value = await cmd.ExecuteScalarAsync();
        return value is string b64 && b64.Length > 0 ? b64 : null;
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

    /// <summary>
    /// Thêm mới hoặc cập nhật thông tin trạm giám sát (Devices + tự tạo Zone nếu chưa có).
    /// </summary>
    public async Task<(bool Success, string? Error)> UpsertDeviceAsync(
        string deviceId, string deviceName, string zoneName, string deviceType, string status, string? ipAddress, bool isEdit)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(deviceName) || string.IsNullOrWhiteSpace(zoneName))
            return (false, "Vui lòng nhập đầy đủ Mã thiết bị, Tên trạm và Khu vực.");

        deviceId = deviceId.Trim().ToUpperInvariant();
        deviceName = deviceName.Trim();
        zoneName = zoneName.Trim();
        deviceType = string.IsNullOrWhiteSpace(deviceType) ? "RaspberryPi-Node" : deviceType.Trim();
        status = NormalizeDeviceStatus(status);
        ipAddress = string.IsNullOrWhiteSpace(ipAddress) ? null : ipAddress.Trim();

        try
        {
            await using var conn = await OpenConnectionAsync();

            // 1. Tìm hoặc tạo mới Zone theo tên nhập vào
            int zoneId;
            const string findZoneSql = "SELECT ZoneId FROM Zones WHERE ZoneName = @ZoneName LIMIT 1;";
            await using (var findCmd = new MySqlCommand(findZoneSql, conn))
            {
                findCmd.Parameters.AddWithValue("@ZoneName", zoneName);
                var existingZone = await findCmd.ExecuteScalarAsync();
                if (existingZone != null && existingZone != DBNull.Value)
                {
                    zoneId = Convert.ToInt32(existingZone);
                }
                else
                {
                    const string insZoneSql = "INSERT INTO Zones (ZoneName) VALUES (@ZoneName); SELECT LAST_INSERT_ID();";
                    await using var insZoneCmd = new MySqlCommand(insZoneSql, conn);
                    insZoneCmd.Parameters.AddWithValue("@ZoneName", zoneName);
                    zoneId = Convert.ToInt32(await insZoneCmd.ExecuteScalarAsync());
                }
            }

            // 2. Thêm mới hoặc cập nhật Devices
            if (!isEdit)
            {
                const string checkSql = "SELECT COUNT(*) FROM Devices WHERE DeviceId = @DeviceId;";
                await using var chkCmd = new MySqlCommand(checkSql, conn);
                chkCmd.Parameters.AddWithValue("@DeviceId", deviceId);
                if (Convert.ToInt64(await chkCmd.ExecuteScalarAsync()) > 0)
                    return (false, $"Mã thiết bị '{deviceId}' đã tồn tại!");

                const string insertSql = """
                    INSERT INTO Devices (DeviceId, DeviceName, ZoneId, DeviceType, Status, IpAddress)
                    VALUES (@DeviceId, @DeviceName, @ZoneId, @DeviceType, @Status, @IpAddress);
                    """;
                await using var cmd = new MySqlCommand(insertSql, conn);
                cmd.Parameters.AddWithValue("@DeviceId", deviceId);
                cmd.Parameters.AddWithValue("@DeviceName", deviceName);
                cmd.Parameters.AddWithValue("@ZoneId", zoneId);
                cmd.Parameters.AddWithValue("@DeviceType", deviceType);
                cmd.Parameters.AddWithValue("@Status", status);
                cmd.Parameters.AddWithValue("@IpAddress", (object?)ipAddress ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync();
            }
            else
            {
                const string updateSql = """
                    UPDATE Devices
                    SET DeviceName = @DeviceName, ZoneId = @ZoneId, DeviceType = @DeviceType, Status = @Status, IpAddress = @IpAddress
                    WHERE DeviceId = @DeviceId;
                    """;
                await using var cmd = new MySqlCommand(updateSql, conn);
                cmd.Parameters.AddWithValue("@DeviceId", deviceId);
                cmd.Parameters.AddWithValue("@DeviceName", deviceName);
                cmd.Parameters.AddWithValue("@ZoneId", zoneId);
                cmd.Parameters.AddWithValue("@DeviceType", deviceType);
                cmd.Parameters.AddWithValue("@Status", status);
                cmd.Parameters.AddWithValue("@IpAddress", (object?)ipAddress ?? DBNull.Value);
                if (await cmd.ExecuteNonQueryAsync() == 0)
                    return (false, $"Không tìm thấy thiết bị '{deviceId}' để sửa.");
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, "Lỗi CSDL: " + ex.Message);
        }
    }

    // CSDL chỉ nhận Active / Inactive / Maintenance (CK_Devices_Status); "Offline" của giao diện cũ = Inactive
    private static string NormalizeDeviceStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "inactive" or "offline" => "Inactive",
        "maintenance" => "Maintenance",
        _ => "Active",
    };

    /// <summary>
    /// Xóa trạm giám sát (xóa kèm log/cảnh báo liên quan để tránh lỗi khóa ngoại).
    /// </summary>
    public async Task<(bool Success, string? Error)> DeleteDeviceAsync(string deviceId)
    {
        try
        {
            await using var conn = await OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            const string checkSql = "SELECT COUNT(*) FROM Devices WHERE DeviceId = @DeviceId FOR UPDATE;";
            await using (var chk = new MySqlCommand(checkSql, conn, tx))
            {
                chk.Parameters.AddWithValue("@DeviceId", deviceId);
                if (Convert.ToInt64(await chk.ExecuteScalarAsync()) == 0)
                    return (false, "Không tìm thấy thiết bị để xóa.");
            }

            // Xoá cả ba bảng trong một giao dịch để không còn log / cảnh báo mồ côi
            const string sql = """
                DELETE FROM AlertHistory WHERE DeviceId = @DeviceId;
                DELETE FROM EnvironmentLogs WHERE DeviceId = @DeviceId;
                DELETE FROM Devices WHERE DeviceId = @DeviceId;
                """;
            await using (var cmd = new MySqlCommand(sql, conn, tx))
            {
                cmd.Parameters.AddWithValue("@DeviceId", deviceId);
                await cmd.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, "Không thể xóa thiết bị: " + ex.Message);
        }
    }
}