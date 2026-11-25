using System.Text.Json;

namespace SmartFireApp.Services;

/// <summary>
/// Cấu hình chung cho desktop-app và web-app, dùng ĐÚNG các khóa của mqtt-subscriber:
///   MqttBroker:Host, MqttBroker:Port, Database:ConnectionString
///
/// Thứ tự đọc (nguồn sau ghi đè nguồn trước):
///   1. Giá trị mặc định bên dưới (khớp infra/docker-compose.yml)
///   2. appsettings.json cạnh file chạy (có commit)
///   3. appsettings.Local.json (KHÔNG commit) — mỗi máy tự tạo nếu dùng mật khẩu MySQL / IP broker riêng
///   4. Biến môi trường: MqttBroker__Host, MqttBroker__Port, Database__ConnectionString
/// </summary>
public sealed class AppSettings
{
    public const string DefaultConnectionString =
        "Server=localhost;Port=3307;Database=SmartFireDB;Uid=root;Pwd=YourStrong@Passw0rd;CharSet=utf8mb4;";

    /// <summary>IP máy chạy Mosquitto. PHẢI trùng với BROKER trong main_iot.py và mqtt-subscriber.</summary>
    public const string DefaultMqttHost = "192.168.137.191";
    public const int DefaultMqttPort = 1883;

    public string ConnectionString { get; private set; } = DefaultConnectionString;
    public string MqttHost { get; private set; } = DefaultMqttHost;
    public int MqttPort { get; private set; } = DefaultMqttPort;

    public static AppSettings Load(string? baseDirectory = null)
    {
        var settings = new AppSettings();
        string dir = baseDirectory ?? AppContext.BaseDirectory;
        settings.ApplyJsonFile(Path.Combine(dir, "appsettings.json"));
        settings.ApplyJsonFile(Path.Combine(dir, "appsettings.Local.json"));
        settings.ApplyEnvironment();
        return settings;
    }

    private void ApplyJsonFile(string path)
    {
        if (!File.Exists(path)) return;

        using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        var root = doc.RootElement;

        if (root.TryGetProperty("MqttBroker", out var mqtt))
        {
            if (mqtt.TryGetProperty("Host", out var host) && host.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(host.GetString()))
                MqttHost = host.GetString()!;
            if (mqtt.TryGetProperty("Port", out var port) && port.ValueKind == JsonValueKind.Number
                && port.TryGetInt32(out var portValue))
                MqttPort = portValue;
        }

        if (root.TryGetProperty("Database", out var db)
            && db.TryGetProperty("ConnectionString", out var cs) && cs.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(cs.GetString()))
            ConnectionString = cs.GetString()!;
    }

    private void ApplyEnvironment()
    {
        string? host = Environment.GetEnvironmentVariable("MqttBroker__Host");
        if (!string.IsNullOrWhiteSpace(host)) MqttHost = host;

        if (int.TryParse(Environment.GetEnvironmentVariable("MqttBroker__Port"), out var port)) MqttPort = port;

        string? cs = Environment.GetEnvironmentVariable("Database__ConnectionString");
        if (!string.IsNullOrWhiteSpace(cs)) ConnectionString = cs;
    }
}
