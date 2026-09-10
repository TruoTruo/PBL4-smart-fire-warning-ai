// ============================================================
// Program.cs
// Lắng nghe dữ liệu cảm biến từ các node Raspberry Pi (iot-edge)
// qua MQTT, ghi vào bảng EnvironmentLogs (MySQL), và áp dụng cơ chế
// XÁC THỰC KÉP để quyết định có tạo cảnh báo (AlertHistory) hay không.
//
// Xác thực kép: chỉ tạo cảnh báo khi
//   AiResult == true  AND  (GasPpm > GasThreshold OR Temperature > TempThreshold)
//
// Khi có cảnh báo:
//   1. Insert vào AlertHistory
//   2. Publish lên topic firealert/alarm/{device_id} để desktop-app
//      subscribe và gọi API gửi SMS
//
// Chạy:
//   dotnet restore
//   dotnet run
// ============================================================

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using MQTTnet;
using MQTTnet.Client;
using MySqlConnector;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables()
    .Build();

string mqttHost = config["MqttBroker:Host"] ?? "localhost";
int mqttPort = int.Parse(config["MqttBroker:Port"] ?? "1883");
string connectionString = config["Database:ConnectionString"]
    ?? throw new InvalidOperationException("Thiếu Database:ConnectionString trong appsettings.json");
double gasThreshold = double.Parse(config["AlertThresholds:GasPpm"] ?? "500");
double tempThreshold = double.Parse(config["AlertThresholds:TemperatureC"] ?? "40");

const string TopicSensorData = "firealert/sensor-data/+";
const string TopicAlarmPrefix = "firealert/alarm";

var mqttFactory = new MqttFactory();
using var mqttClient = mqttFactory.CreateMqttClient();

var mqttOptions = new MqttClientOptionsBuilder()
    .WithTcpServer(mqttHost, mqttPort)
    .WithClientId($"db-subscriber-{Guid.NewGuid():N}")
    .Build();

mqttClient.ConnectedAsync += async e =>
{
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Đã kết nối MQTT Broker {mqttHost}:{mqttPort}");
    await mqttClient.SubscribeAsync(
        new MqttTopicFilterBuilder().WithTopic(TopicSensorData).Build());
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Đã subscribe topic: {TopicSensorData}");
};

mqttClient.DisconnectedAsync += async e =>
{
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Mất kết nối MQTT, thử lại sau 5s...");
    await Task.Delay(TimeSpan.FromSeconds(5));
    try { await mqttClient.ConnectAsync(mqttOptions); }
    catch (Exception ex) { Console.WriteLine($"Kết nối lại thất bại: {ex.Message}"); }
};

mqttClient.ApplicationMessageReceivedAsync += async e =>
{
    string payloadJson = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
    SensorPayload? payload;

    try
    {
        payload = JsonSerializer.Deserialize<SensorPayload>(
            payloadJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
    catch (JsonException)
    {
        Console.WriteLine($"[CẢNH BÁO] Bỏ qua message không phải JSON hợp lệ trên topic {e.ApplicationMessage.Topic}");
        return;
    }

    if (payload is null || string.IsNullOrWhiteSpace(payload.DeviceId))
    {
        Console.WriteLine("[CẢNH BÁO] Message thiếu device_id, bỏ qua.");
        return;
    }

    try
    {
        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync();

        long logId = await InsertEnvironmentLogAsync(conn, payload);
        Console.WriteLine(
            $"[{DateTime.Now:HH:mm:ss}] Ghi log #{logId} cho thiết bị {payload.DeviceId} " +
            $"(temp={payload.Temperature}, gas={payload.GasPpm}, ai={payload.AiResult})");

        var (hasAlert, alertType, severity) = CheckAlertCondition(payload, gasThreshold, tempThreshold);
        if (hasAlert)
        {
            await InsertAlertAsync(conn, payload.DeviceId, logId, alertType, severity);
            Console.WriteLine($"🔥 [{DateTime.Now:HH:mm:ss}] CẢNH BÁO [{alertType}] thiết bị {payload.DeviceId} - mức độ {severity}");

            var alarmPayload = new AlarmPayload
            {
                DeviceId = payload.DeviceId,
                AlertType = alertType,
                Severity = severity,
                LogId = logId,
                Timestamp = DateTime.Now.ToString("O"),
            };
            string alarmJson = JsonSerializer.Serialize(alarmPayload);

            var alarmMessage = new MqttApplicationMessageBuilder()
                .WithTopic($"{TopicAlarmPrefix}/{payload.DeviceId}")
                .WithPayload(alarmJson)
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();
            await mqttClient.PublishAsync(alarmMessage);
        }
    }
    catch (MySqlException ex)
    {
        Console.WriteLine($"[LỖI] Ghi CSDL cho thiết bị {payload.DeviceId} thất bại: {ex.Message}");
    }
};

await mqttClient.ConnectAsync(mqttOptions, CancellationToken.None);

Console.WriteLine("Bắt đầu lắng nghe... (Ctrl+C để dừng)");
await Task.Delay(Timeout.Infinite);


// ------------------------------------------------------------
// Hàm phụ trợ
// ------------------------------------------------------------

static async Task<long> InsertEnvironmentLogAsync(MySqlConnection conn, SensorPayload payload)
{
    // MySQL không có OUTPUT INSERTED như SQL Server, nên phải insert
    // xong rồi lấy LAST_INSERT_ID() bằng 1 câu lệnh riêng, trong cùng
    // connection (KHÔNG dùng chung 1 command nhiều câu lệnh, vì
    // MySqlConnector mặc định không bật multi-statement).
    const string insertSql = """
        INSERT INTO EnvironmentLogs
            (DeviceId, Temperature, Humidity, GasPpm, AiResult, ConfidenceScore, RecordedAt)
        VALUES (@DeviceId, @Temperature, @Humidity, @GasPpm, @AiResult, @ConfidenceScore, @RecordedAt)
        """;

    await using (var insertCmd = new MySqlCommand(insertSql, conn))
    {
        insertCmd.Parameters.AddWithValue("@DeviceId", payload.DeviceId);
        insertCmd.Parameters.AddWithValue("@Temperature", (object?)payload.Temperature ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("@Humidity", (object?)payload.Humidity ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("@GasPpm", (object?)payload.GasPpm ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("@AiResult", payload.AiResult);
        insertCmd.Parameters.AddWithValue("@ConfidenceScore", (object?)payload.Confidence ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("@RecordedAt",
            payload.Timestamp is not null ? DateTime.Parse(payload.Timestamp) : DateTime.Now);
        await insertCmd.ExecuteNonQueryAsync();
    }

    await using var idCmd = new MySqlCommand("SELECT LAST_INSERT_ID();", conn);
    var result = await idCmd.ExecuteScalarAsync();
    return Convert.ToInt64(result);
}

static async Task InsertAlertAsync(MySqlConnection conn, string deviceId, long logId, string alertType, string severity)
{
    const string sql = """
        INSERT INTO AlertHistory
            (DeviceId, LogId, AlertType, Severity, AlertTime, SmsSent, Acknowledged)
        VALUES (@DeviceId, @LogId, @AlertType, @Severity, NOW(), FALSE, FALSE)
        """;

    await using var cmd = new MySqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("@DeviceId", deviceId);
    cmd.Parameters.AddWithValue("@LogId", logId);
    cmd.Parameters.AddWithValue("@AlertType", alertType);
    cmd.Parameters.AddWithValue("@Severity", severity);
    await cmd.ExecuteNonQueryAsync();
}

static (bool hasAlert, string alertType, string severity) CheckAlertCondition(
    SensorPayload payload, double gasThreshold, double tempThreshold)
{
    bool aiResult = payload.AiResult;
    double gas = payload.GasPpm ?? 0;
    double temp = payload.Temperature ?? 0;

    bool gasExceeded = gas > gasThreshold;
    bool tempExceeded = temp > tempThreshold;

    if (aiResult && (gasExceeded || tempExceeded))
    {
        string alertType = tempExceeded ? "Fire" : "Smoke";
        return (true, alertType, "High");
    }
    return (false, "", "");
}

// ------------------------------------------------------------
// Model dữ liệu JSON
// ------------------------------------------------------------

class SensorPayload
{
    public string DeviceId { get; set; } = string.Empty;
    public double? Temperature { get; set; }
    public double? Humidity { get; set; }
    public double? GasPpm { get; set; }
    public bool AiResult { get; set; }
    public double? Confidence { get; set; }
    public string? Timestamp { get; set; }
}

class AlarmPayload
{
    public string DeviceId { get; set; } = string.Empty;
    public string AlertType { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public long LogId { get; set; }
    public string Timestamp { get; set; } = string.Empty;
}