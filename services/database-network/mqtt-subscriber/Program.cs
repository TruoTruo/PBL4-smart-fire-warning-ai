// ============================================================
// Program.cs  (mqtt-subscriber)
// ĐẶT TẠI: services\database-network\mqtt-subscriber\Program.cs   (GHI ĐÈ file cũ)
//
// Lắng nghe dữ liệu cảm biến từ các node Raspberry Pi (iot-edge) qua MQTT,
// ghi vào bảng EnvironmentLogs (MySQL) và quyết định có tạo cảnh báo (AlertHistory) hay không.
//
// Quyết định cảnh báo (2 cách, tự chọn theo nội dung message):
//   1) Message CÓ "alert_level" (Pi đã tự đánh giá bằng sensor fusion)  -> TIN THEO PI:
//        alert_level = 2  -> cảnh báo 'Fire'                         mức High
//        alert_level = 1  -> 'Smoke' (nếu smoke_detected) hoặc
//                            'HighTemperature'                       mức Medium
//        alert_level = 0  -> không cảnh báo
//   2) Message KHÔNG có "alert_level" -> dùng xác thực kép như cũ:
//        AiResult == true AND (GasPpm > GasThreshold OR Temperature > TempThreshold)
//
// TƯƠNG THÍCH main_iot.py CŨ (không cần sửa Pi): subscriber nghe cả 2 topic
//     firealert/sensor-data/+            (định dạng mới: device_id, temperature, ...)
//     pbl4/sensors/fire_warning          (định dạng cũ : nhiet_do, do_am, co_khoi, co_lua, muc_do)
// Message cũ không có device_id -> dùng Legacy:DefaultDeviceId trong appsettings.json (mặc định PI-01).
//
// Chống trùng: cùng (thiết bị + loại cảnh báo) chỉ ghi 1 dòng AlertHistory mỗi
// AlertThresholds:CooldownSeconds giây (mặc định 60). Dữ liệu cảm biến vẫn ghi đầy đủ.
//
// Khi có cảnh báo:
//   1. Insert vào AlertHistory
//   2. Publish lên topic firealert/alarm/{device_id} để desktop-app nhận
//
// Chạy:  dotnet run
// ============================================================
 
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using MQTTnet;
using MQTTnet.Client;
using MySqlConnector;
 
Console.OutputEncoding = Encoding.UTF8;
 
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
int cooldownSeconds = int.Parse(config["AlertThresholds:CooldownSeconds"] ?? "60");
string defaultDeviceId = config["Legacy:DefaultDeviceId"] ?? "PI-01";   // thiết bị gán cho message cũ không có device_id
 
const string TopicSensorData = "firealert/sensor-data/+";
const string TopicLegacy = "pbl4/sensors/fire_warning";   // topic của main_iot.py cũ
const string TopicAlarmPrefix = "firealert/alarm";
 
// device_id <-> DeviceId, gas_ppm <-> GasPpm, alert_level <-> AlertLevel ...
var jsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
};
 
// Lần cuối đã ghi cảnh báo cho (thiết bị|loại) -> để chống ghi trùng
var lastAlertAt = new ConcurrentDictionary<string, DateTime>();
 
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
    await mqttClient.SubscribeAsync(
        new MqttTopicFilterBuilder().WithTopic(TopicLegacy).Build());
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Đã subscribe topic: {TopicSensorData} và {TopicLegacy}");
};
 
mqttClient.DisconnectedAsync += async e =>
{
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Mất kết nối MQTT, thử lại sau 5s...");
    while (!mqttClient.IsConnected)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        try { await mqttClient.ConnectAsync(mqttOptions); }
        catch (Exception ex) { Console.WriteLine($"Kết nối lại thất bại: {ex.Message}"); }
    }
};
 
mqttClient.ApplicationMessageReceivedAsync += async e =>
{
    string payloadJson = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
    SensorPayload? payload;
 
    try
    {
        payload = JsonSerializer.Deserialize<SensorPayload>(payloadJson, jsonOptions);
    }
    catch (JsonException)
    {
        Console.WriteLine($"[CẢNH BÁO] Bỏ qua message không phải JSON hợp lệ trên topic {e.ApplicationMessage.Topic}");
        return;
    }
 
    if (payload is null)
    {
        Console.WriteLine($"[CẢNH BÁO] Message rỗng (topic {e.ApplicationMessage.Topic}), bỏ qua.");
        return;
    }
 
    // Quy message định dạng cũ (nhiet_do, co_lua, muc_do...) về định dạng chuẩn + tìm device_id
    NormalizePayload(payload, e.ApplicationMessage.Topic, defaultDeviceId);
 
    if (string.IsNullOrWhiteSpace(payload.DeviceId))
    {
        Console.WriteLine($"[CẢNH BÁO] Message thiếu device_id (topic {e.ApplicationMessage.Topic}), bỏ qua.");
        return;
    }
 
    try
    {
        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync();
 
        long logId = await InsertEnvironmentLogAsync(conn, payload);
        Console.WriteLine(
            $"[{DateTime.Now:HH:mm:ss}] Ghi log #{logId} cho thiết bị {payload.DeviceId} <- {e.ApplicationMessage.Topic} " +
            $"(temp={payload.Temperature}, gas={payload.GasPpm}, ai={payload.AiResult}, level={payload.AlertLevel})");
 
        var (hasAlert, alertType, severity) = CheckAlertCondition(payload, gasThreshold, tempThreshold);
        if (!hasAlert) return;
 
        // Chống ghi trùng: cùng thiết bị + cùng loại cảnh báo trong khoảng cooldown
        string key = $"{payload.DeviceId}|{alertType}";
        var now = DateTime.Now;
        if (lastAlertAt.TryGetValue(key, out var lastTime) && (now - lastTime).TotalSeconds < cooldownSeconds)
        {
            Console.WriteLine(
                $"[{now:HH:mm:ss}] {payload.DeviceId} vẫn đang ở trạng thái [{alertType}] " +
                $"- bỏ qua cảnh báo trùng (cooldown {cooldownSeconds}s)");
            return;
        }
        lastAlertAt[key] = now;
 
        await InsertAlertAsync(conn, payload.DeviceId, logId, alertType, severity);
        Console.WriteLine($"[{now:HH:mm:ss}] CẢNH BÁO [{alertType}] thiết bị {payload.DeviceId} - mức độ {severity}");
 
        var alarmPayload = new AlarmPayload
        {
            DeviceId = payload.DeviceId,
            AlertType = alertType,
            Severity = severity,
            LogId = logId,
            Timestamp = now.ToString("O"),
        };
        string alarmJson = JsonSerializer.Serialize(alarmPayload);
 
        var alarmMessage = new MqttApplicationMessageBuilder()
            .WithTopic($"{TopicAlarmPrefix}/{payload.DeviceId}")
            .WithPayload(alarmJson)
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
        await mqttClient.PublishAsync(alarmMessage);
    }
    catch (MySqlException ex)
    {
        Console.WriteLine($"[LỖI] Ghi CSDL cho thiết bị {payload.DeviceId} thất bại: {ex.Message}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[LỖI] Xử lý message của {payload.DeviceId} thất bại: {ex.Message}");
    }
};
 
try
{
    await mqttClient.ConnectAsync(mqttOptions, CancellationToken.None);
}
catch (Exception ex)
{
    Console.WriteLine($"[LỖI] Không kết nối được MQTT broker {mqttHost}:{mqttPort}: {ex.Message}");
    Console.WriteLine("      Kiểm tra MqttBroker:Host trong appsettings.json và kết nối mạng.");
    return;
}
 
Console.WriteLine($"Bắt đầu lắng nghe... (Ctrl+C để dừng). Cooldown cảnh báo: {cooldownSeconds}s");
await Task.Delay(Timeout.Infinite);
 
 
// ------------------------------------------------------------
// Hàm phụ trợ
// ------------------------------------------------------------
 
static void NormalizePayload(SensorPayload p, string topic, string defaultDeviceId)
{
    // Định dạng cũ của main_iot.py -> định dạng chuẩn
    p.Temperature ??= p.NhietDo;
    p.Humidity ??= p.DoAm;
    if (p.CoLua is int lua) p.AiResult = p.AiResult || lua != 0;
    if (p.SmokeDetected is null && p.CoKhoi is int khoi) p.SmokeDetected = khoi != 0;
    p.AlertLevel ??= p.MucDo;
 
    // Không có device_id trong message: lấy từ đuôi topic (firealert/sensor-data/PI-02 -> PI-02),
    // nếu topic cũ (không có tên thiết bị) thì dùng thiết bị mặc định
    if (string.IsNullOrWhiteSpace(p.DeviceId))
    {
        p.DeviceId = topic.StartsWith("firealert/sensor-data/")
            ? topic[(topic.LastIndexOf('/') + 1)..]
            : defaultDeviceId;
    }
}
 
static async Task<long> InsertEnvironmentLogAsync(MySqlConnection conn, SensorPayload payload)
{
    // MySQL không có OUTPUT INSERTED như SQL Server, nên phải insert
    // xong rồi lấy LAST_INSERT_ID() bằng 1 câu lệnh riêng, trong cùng connection.
    const string insertSql = """
        INSERT INTO EnvironmentLogs
            (DeviceId, Temperature, Humidity, GasPpm, AiResult, ConfidenceScore, RecordedAt)
        VALUES (@DeviceId, @Temperature, @Humidity, @GasPpm, @AiResult, @ConfidenceScore, @RecordedAt)
        """;
 
    // Không có timestamp (hoặc sai định dạng) -> dùng giờ máy chạy subscriber
    DateTime recordedAt = DateTime.Now;
    if (payload.Timestamp is not null && DateTime.TryParse(payload.Timestamp, out var parsed))
        recordedAt = parsed;
 
    await using (var insertCmd = new MySqlCommand(insertSql, conn))
    {
        insertCmd.Parameters.AddWithValue("@DeviceId", payload.DeviceId);
        insertCmd.Parameters.AddWithValue("@Temperature", (object?)payload.Temperature ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("@Humidity", (object?)payload.Humidity ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("@GasPpm", (object?)payload.GasPpm ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("@AiResult", payload.AiResult);
        insertCmd.Parameters.AddWithValue("@ConfidenceScore", (object?)payload.Confidence ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("@RecordedAt", recordedAt);
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
    // Cách 1: Pi đã tự đánh giá (sensor fusion) -> tin theo Pi
    if (payload.AlertLevel is int level)
    {
        if (level >= 2) return (true, "Fire", "High");
        if (level == 1)
        {
            string type = (payload.SmokeDetected ?? false) ? "Smoke" : "HighTemperature";
            return (true, type, "Medium");
        }
        return (false, "", "");
    }
 
    // Cách 2 (dự phòng): xác thực kép như thiết kế ban đầu
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
 
    // Do Pi tính (main_iot.py): 0 = bình thường, 1 = cảnh báo sớm, 2 = cháy
    public int? AlertLevel { get; set; }
    public bool? SmokeDetected { get; set; }
 
    // --- Định dạng CŨ của main_iot.py (nhiet_do, do_am, co_khoi, co_lua, muc_do) ---
    public double? NhietDo { get; set; }
    public double? DoAm { get; set; }
    public int? CoKhoi { get; set; }
    public int? CoLua { get; set; }
    public int? MucDo { get; set; }
}
 
class AlarmPayload
{
    public string DeviceId { get; set; } = string.Empty;
    public string AlertType { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public long LogId { get; set; }
    public string Timestamp { get; set; } = string.Empty;
}