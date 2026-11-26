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
//        alert_level = 1  -> 'Fire' (nếu AI thấy lửa), 'Smoke' (nếu MQ-2 hoặc camera thấy khói)
//                            hoặc 'HighTemperature'                  mức Medium
//        alert_level = 0  -> không cảnh báo
//   2) Message KHÔNG có "alert_level" -> dùng xác thực kép như cũ:
//        AiResult == true AND (SmokeDetected OR Temperature > TempThreshold)
//
// TƯƠNG THÍCH main_iot.py CŨ (không cần sửa Pi): subscriber nghe cả 2 topic
//     firealert/sensor-data/+            (định dạng mới: device_id, temperature, ...)
//     pbl4/sensors/fire_warning          (định dạng cũ : nhiet_do, do_am, co_khoi, co_lua, muc_do)
// Message cũ không có device_id -> dùng Legacy:DefaultDeviceId trong appsettings.json (mặc định PI-01).
// Trường tùy chọn camera_ok, khoi_camera và bản tin heartbeat (muc_do 0, nhiet_do/do_am null) đều nhận được;
// nhiệt độ/độ ẩm null ghi NULL. Heartbeat không in mỗi bản tin một dòng (chỉ khi trạng thái đổi, tóm tắt mỗi 60 s).
// Topic trạng thái pbl4/status/{id} ("online"/"offline") không được subscribe.
//
// Chống trùng: cùng (thiết bị + loại + mức độ) chỉ ghi 1 dòng AlertHistory mỗi
// AlertThresholds:CooldownSeconds giây (mặc định 60). Mức độ TĂNG (Medium -> High) luôn được ghi;
// mức thấp hơn mức vừa ghi trong cooldown thì bỏ qua. Dữ liệu cảm biến vẫn ghi đầy đủ.
//
// Broker chưa chạy hoặc bị ngắt: tự thử kết nối lại mãi (chờ 2, 4, 8... tối đa 30 giây).
//
// Khi có cảnh báo:
//   1. Insert vào AlertHistory (bản tin không kèm ảnh thì dùng ảnh gần nhất của cùng đợt cảnh báo)
//   2. Publish lên topic firealert/alarm/{device_id} để web-app nhận
//
// Chạy:  dotnet run        (hoặc docker compose, xem infra/docker-compose.yml)
// ============================================================
 
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using MQTTnet;
using MQTTnet.Client;
using MySqlConnector;
 
Console.OutputEncoding = Encoding.UTF8;
 
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Local.json", optional: true)   // KHÔNG commit: mật khẩu MySQL / IP broker riêng từng máy
    .AddEnvironmentVariables()
    .Build();
 
string mqttHost = config["MqttBroker:Host"] ?? "localhost";
int mqttPort = int.Parse(config["MqttBroker:Port"] ?? "1883");
string connectionString = config["Database:ConnectionString"]
    ?? throw new InvalidOperationException("Thiếu Database:ConnectionString trong appsettings.json");
double tempThreshold = double.Parse(config["AlertThresholds:TemperatureC"] ?? "45");
int cooldownSeconds = int.Parse(config["AlertThresholds:CooldownSeconds"] ?? "60");
string defaultDeviceId = config["Legacy:DefaultDeviceId"] ?? "PI-01";   // thiết bị gán cho message cũ không có device_id
 
const string TopicSensorData = "firealert/sensor-data/+";
const string TopicLegacy = "pbl4/sensors/fire_warning";   // topic main_iot.py đang dùng
const string TopicAlarmPrefix = "firealert/alarm";
 
// device_id <-> DeviceId, smoke_detected <-> SmokeDetected, alert_level <-> AlertLevel ...
var jsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
};
 
// Lần cuối đã ghi cảnh báo cho (thiết bị|loại|mức độ) -> để chống ghi trùng
var lastAlertAt = new ConcurrentDictionary<string, DateTime>();
string[] severityOrder = ["Low", "Medium", "High"];

// Pi chỉ gửi ảnh khi mức tăng hoặc tối đa 10 s/lần: giữ ảnh gần nhất của đợt cảnh báo hiện tại
// (từ lúc muc_do rời 0) để dòng AlertHistory ghi sau đó vẫn có ảnh. muc_do về 0 thì xoá
var lastSnapshot = new ConcurrentDictionary<string, (string Data, DateTime ReceivedAt)>();
var episodeStart = new ConcurrentDictionary<string, DateTime>();

// Heartbeat 2,5 s/lần: không in mỗi bản tin một dòng, chỉ in khi trạng thái đổi hoặc tóm tắt mỗi 60 s
const int LogSummarySeconds = 60;
var logStates = new ConcurrentDictionary<string, DeviceLogState>();
 
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
 
// Chỉ 1 vòng kết nối chạy tại một thời điểm (lúc khởi động hoặc khi mất kết nối)
var connectLock = new SemaphoreSlim(1, 1);

async Task ConnectWithRetryAsync()
{
    await connectLock.WaitAsync();
    try
    {
        int attempt = 0;
        while (!mqttClient.IsConnected)
        {
            attempt++;
            try
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Kết nối MQTT broker {mqttHost}:{mqttPort} (lần {attempt})...");
                await mqttClient.ConnectAsync(mqttOptions, CancellationToken.None);
            }
            catch (Exception ex)
            {
                int waitSeconds = Math.Min(30, 1 << Math.Min(attempt, 5));   // 2, 4, 8, 16, 30, 30...
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [LỖI] Không kết nối được MQTT broker: {ex.Message.TrimEnd('.')}. Thử lại sau {waitSeconds}s");
                await Task.Delay(TimeSpan.FromSeconds(waitSeconds));
            }
        }
    }
    finally
    {
        connectLock.Release();
    }
}

mqttClient.DisconnectedAsync += e =>
{
    // Lần kết nối thất bại cũng gọi sự kiện này (ClientWasConnected = false): vòng thử lại đang lo
    if (!e.ClientWasConnected) return Task.CompletedTask;
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Mất kết nối MQTT, đang thử kết nối lại...");
    _ = Task.Run(ConnectWithRetryAsync);
    return Task.CompletedTask;
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
 
    // Một mốc giờ (giờ máy chạy subscriber) cho cả log và cảnh báo của cùng message,
    // không dùng NOW() của MySQL để tránh lệch múi giờ khi MySQL chạy trong Docker (UTC)
    DateTime messageTime = DateTime.Now;
    if (payload.Timestamp is not null && DateTime.TryParse(payload.Timestamp, out var parsedTime))
        messageTime = parsedTime;
    messageTime = messageTime.AddTicks(-(messageTime.Ticks % TimeSpan.TicksPerSecond));   // cột DATETIME chỉ lưu tới giây

    var (hasAlert, alertType, severity) = CheckAlertCondition(payload, tempThreshold);
    int level = payload.AlertLevel ?? (hasAlert ? (severity == "High" ? 2 : 1) : 0);
    var now = DateTime.Now;

    // Đợt cảnh báo: muc_do về 0 thì xoá ảnh cũ, đang > 0 thì giữ ảnh mới nhất Pi gửi
    if (level <= 0)
    {
        lastSnapshot.TryRemove(payload.DeviceId, out _);
        episodeStart.TryRemove(payload.DeviceId, out _);
    }
    else
    {
        episodeStart.TryAdd(payload.DeviceId, now);
        if (!string.IsNullOrEmpty(payload.Snapshot)) lastSnapshot[payload.DeviceId] = (payload.Snapshot, now);
    }

    var (printLog, skipped) = TakeLogTurn(payload, level, now);

    try
    {
        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync();

        long logId = await InsertEnvironmentLogAsync(conn, payload, messageTime);
        if (printLog)
        {
            Console.WriteLine(
                $"[{now:HH:mm:ss}] Ghi log #{logId} cho thiết bị {payload.DeviceId} <- {e.ApplicationMessage.Topic} " +
                $"(temp={payload.Temperature?.ToString() ?? "null"}, hum={payload.Humidity?.ToString() ?? "null"}, smoke={payload.SmokeDetected}, " +
                $"smoke_level={payload.SmokeLevel}, ai={payload.AiResult}, conf={payload.Confidence}, level={payload.AlertLevel}" +
                (payload.CameraOk is bool cam ? $", camera_ok={cam}" : "") +
                (payload.KhoiCamera is bool khoiCam ? $", khoi_camera={khoiCam}" : "") + ")" +
                (skipped > 0 ? $" [+{skipped} bản tin không đổi trạng thái đã ghi, không in]" : ""));
        }

        if (!hasAlert) return;

        // Chống ghi trùng theo (thiết bị|loại|mức độ): đã ghi mức này hoặc mức cao hơn trong cooldown thì bỏ qua,
        // mức tăng (Medium -> High) luôn được ghi
        int rank = Math.Max(0, Array.IndexOf(severityOrder, severity));
        bool recentlyAlerted = severityOrder.Skip(rank).Any(s =>
            lastAlertAt.TryGetValue($"{payload.DeviceId}|{alertType}|{s}", out var lastTime)
            && (now - lastTime).TotalSeconds < cooldownSeconds);
        if (recentlyAlerted)
        {
            if (printLog)
                Console.WriteLine(
                    $"[{now:HH:mm:ss}] {payload.DeviceId} vẫn đang ở trạng thái [{alertType}/{severity}] " +
                    $"- bỏ qua cảnh báo trùng (cooldown {cooldownSeconds}s)");
            return;
        }

        // Bản tin này không kèm ảnh thì dùng ảnh gần nhất của cùng đợt cảnh báo
        string? snapshot = payload.Snapshot;
        bool fromCache = false;
        if (string.IsNullOrEmpty(snapshot)
            && lastSnapshot.TryGetValue(payload.DeviceId, out var cached)
            && episodeStart.TryGetValue(payload.DeviceId, out var start)
            && cached.ReceivedAt >= start)
        {
            snapshot = cached.Data;
            fromCache = true;
        }

        await InsertAlertAsync(conn, payload.DeviceId, logId, alertType, severity, snapshot, messageTime);
        lastAlertAt[$"{payload.DeviceId}|{alertType}|{severity}"] = now;
        string snapshotNote = snapshot is null ? "không có ảnh" : fromCache ? "ảnh lấy từ bản tin trước cùng đợt" : "có ảnh";
        Console.WriteLine($"[{now:HH:mm:ss}] CẢNH BÁO [{alertType}] thiết bị {payload.DeviceId} - mức độ {severity} ({snapshotNote})");

        var alarmPayload = new AlarmPayload
        {
            DeviceId = payload.DeviceId,
            AlertType = alertType,
            Severity = severity,
            LogId = logId,
            Timestamp = messageTime.ToString("O"),
        };
        // web-app đọc JSON dạng camelCase (deviceId, alertType...) nên phải serialize đúng kiểu này
        string alarmJson = JsonSerializer.Serialize(alarmPayload,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
 
        var alarmMessage = new MqttApplicationMessageBuilder()
            .WithTopic($"{TopicAlarmPrefix}/{payload.DeviceId}")
            .WithPayload(alarmJson)
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
        await mqttClient.PublishAsync(alarmMessage);
    }
    catch (MySqlException ex)
    {
        PrintErrorThrottled($"[LỖI] Ghi CSDL cho thiết bị {payload.DeviceId} thất bại: {ex.Message}");
    }
    catch (Exception ex)
    {
        PrintErrorThrottled($"[LỖI] Xử lý message của {payload.DeviceId} thất bại: {ex.Message}");
    }
};

// In dòng log khi trạng thái đổi (mức, mất/có DHT, khói, lửa, camera) hoặc mỗi LogSummarySeconds giây
(bool print, int skipped) TakeLogTurn(SensorPayload p, int level, DateTime now)
{
    var st = logStates.GetOrAdd(p.DeviceId, _ => new DeviceLogState());
    lock (st)
    {
        string signature = $"{level}|{p.Temperature is null}|{p.Humidity is null}|{p.SmokeDetected}|{p.AiResult}|{p.CameraOk}|{p.KhoiCamera}";
        bool print = signature != st.Signature || (now - st.LastPrinted).TotalSeconds >= LogSummarySeconds;
        st.Signature = signature;
        if (!print)
        {
            st.Skipped++;
            return (false, 0);
        }
        int skipped = st.Skipped;
        st.Skipped = 0;
        st.LastPrinted = now;
        return (true, skipped);
    }
}

// Lỗi giống nhau (vd. MySQL tắt) chỉ in 1 lần mỗi 30 s, kèm số lần bị gộp
void PrintErrorThrottled(string message)
{
    var st = logStates.GetOrAdd("error|" + message, _ => new DeviceLogState());
    lock (st)
    {
        var now = DateTime.Now;
        if ((now - st.LastPrinted).TotalSeconds < 30)
        {
            st.Skipped++;
            return;
        }
        Console.WriteLine($"[{now:HH:mm:ss}] {message}" + (st.Skipped > 0 ? $" (+{st.Skipped} lần tương tự)" : ""));
        st.Skipped = 0;
        st.LastPrinted = now;
    }
}
 
// Broker chưa chạy thì không thoát: thử lại tới khi kết nối được
// (sai địa chỉ thì kiểm tra MqttBroker:Host trong appsettings.json / biến môi trường MqttBroker__Host)
await ConnectWithRetryAsync();

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
    if (p.CoLua is bool lua) p.AiResult = p.AiResult || lua;
    if (p.SmokeDetected is null && p.CoKhoi is bool khoi) p.SmokeDetected = khoi;
    p.AlertLevel ??= p.MucDo;
    p.SmokeLevel ??= p.MucKhoi;
    // Cột SmokeLevel là DECIMAL(5,2): ép về 0..100 để dữ liệu lạ không làm lỗi INSERT
    if (p.SmokeLevel is double lv) p.SmokeLevel = Math.Clamp(lv, 0, 100);
 
    // Không có device_id trong message: lấy từ đuôi topic (firealert/sensor-data/PI-02 -> PI-02),
    // nếu topic cũ (không có tên thiết bị) thì dùng thiết bị mặc định
    if (string.IsNullOrWhiteSpace(p.DeviceId))
    {
        p.DeviceId = topic.StartsWith("firealert/sensor-data/")
            ? topic[(topic.LastIndexOf('/') + 1)..]
            : defaultDeviceId;
    }
}
 
static async Task<long> InsertEnvironmentLogAsync(MySqlConnection conn, SensorPayload payload, DateTime recordedAt)
{
    // MySQL không có OUTPUT INSERTED như SQL Server, nên phải insert
    // xong rồi lấy LAST_INSERT_ID() bằng 1 câu lệnh riêng, trong cùng connection.
    const string insertSql = """
        INSERT INTO EnvironmentLogs
            (DeviceId, Temperature, Humidity, SmokeDetected, SmokeLevel, AlertLevel, AiResult, ConfidenceScore, RecordedAt)
        VALUES (@DeviceId, @Temperature, @Humidity, @SmokeDetected, @SmokeLevel, @AlertLevel, @AiResult, @ConfidenceScore, @RecordedAt)
        """;
 
    await using (var insertCmd = new MySqlCommand(insertSql, conn))
    {
        insertCmd.Parameters.AddWithValue("@DeviceId", payload.DeviceId);
        insertCmd.Parameters.AddWithValue("@Temperature", (object?)payload.Temperature ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("@Humidity", (object?)payload.Humidity ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("@SmokeDetected", payload.SmokeDetected ?? false);
        insertCmd.Parameters.AddWithValue("@SmokeLevel", (object?)payload.SmokeLevel ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("@AlertLevel", payload.AlertLevel ?? 0);
        insertCmd.Parameters.AddWithValue("@AiResult", payload.AiResult);
        insertCmd.Parameters.AddWithValue("@ConfidenceScore", (object?)payload.Confidence ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("@RecordedAt", recordedAt);
        await insertCmd.ExecuteNonQueryAsync();
    }
 
    await using var idCmd = new MySqlCommand("SELECT LAST_INSERT_ID();", conn);
    var result = await idCmd.ExecuteScalarAsync();
    return Convert.ToInt64(result);
}
 
static async Task InsertAlertAsync(MySqlConnection conn, string deviceId, long logId, string alertType, string severity, string? snapshotBase64, DateTime alertTime)
{
    const string sql = """
        INSERT INTO AlertHistory
            (DeviceId, LogId, AlertType, Severity, SnapshotBase64, AlertTime, SmsSent, Acknowledged)
        VALUES (@DeviceId, @LogId, @AlertType, @Severity, @SnapshotBase64, @AlertTime, FALSE, FALSE)
        """;
 
    await using var cmd = new MySqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("@DeviceId", deviceId);
    cmd.Parameters.AddWithValue("@LogId", logId);
    cmd.Parameters.AddWithValue("@AlertType", alertType);
    cmd.Parameters.AddWithValue("@Severity", severity);
    cmd.Parameters.AddWithValue("@SnapshotBase64", (object?)snapshotBase64 ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@AlertTime", alertTime);
    await cmd.ExecuteNonQueryAsync();
}
 
static (bool hasAlert, string alertType, string severity) CheckAlertCondition(
    SensorPayload payload, double tempThreshold)
{
    // Cách 1: Pi đã tự đánh giá (sensor fusion) -> tin theo Pi
    if (payload.AlertLevel is int level)
    {
        if (level >= 2) return (true, "Fire", "High");
        if (level == 1)
        {
            // Mức 1 = chỉ có 1 tín hiệu: AI thấy lửa, hoặc có khói (MQ-2 / camera), hoặc nhiệt cao
            if (payload.AiResult) return (true, "Fire", "Medium");
            bool anySmoke = (payload.SmokeDetected ?? false) || (payload.KhoiCamera ?? false);
            string type = anySmoke ? "Smoke" : "HighTemperature";
            return (true, type, "Medium");
        }
        return (false, "", "");
    }

    // Cách 2 (dự phòng, message không có alert_level): xác thực kép
    bool smoke = payload.SmokeDetected ?? false;
    bool tempExceeded = (payload.Temperature ?? 0) > tempThreshold;

    if (payload.AiResult && (smoke || tempExceeded))
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
    public bool AiResult { get; set; }
    public double? Confidence { get; set; }
    public string? Timestamp { get; set; }
 
    // Do Pi tính (main_iot.py): 0 = bình thường, 1 = cảnh báo sớm, 2 = cháy
    public int? AlertLevel { get; set; }
    public bool? SmokeDetected { get; set; }
    public double? SmokeLevel { get; set; }   // mức khói/gas (%) từ MQ-2 qua ADC; null nếu Pi chỉ đọc chân DO
 
    // --- Định dạng CŨ của main_iot.py (nhiet_do, do_am, co_khoi, co_lua, muc_do) ---
    public double? NhietDo { get; set; }
    public double? DoAm { get; set; }
    [JsonConverter(typeof(FlagConverter))] public bool? CoKhoi { get; set; }
    public double? MucKhoi { get; set; }
    [JsonConverter(typeof(FlagConverter))] public bool? CoLua { get; set; }
    public int? MucDo { get; set; }
    public string? Snapshot { get; set; }

    // Tùy chọn (Pi bản mới): camera còn chạy không, camera có thấy khói không. Không lưu CSDL
    [JsonConverter(typeof(FlagConverter))] public bool? CameraOk { get; set; }
    [JsonConverter(typeof(FlagConverter))] public bool? KhoiCamera { get; set; }
}

// Cờ nhận cả true/false lẫn 0/1; kiểu lạ thì coi như không có (không bỏ cả message)
class FlagConverter : JsonConverter<bool?>
{
    public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True: return true;
            case JsonTokenType.False: return false;
            case JsonTokenType.Number: return reader.GetDouble() != 0;
            default: reader.Skip(); return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
    {
        if (value is bool b) writer.WriteBooleanValue(b);
        else writer.WriteNullValue();
    }
}
 
class DeviceLogState
{
    public string Signature { get; set; } = string.Empty;
    public DateTime LastPrinted { get; set; } = DateTime.MinValue;
    public int Skipped { get; set; }
}
 
class AlarmPayload
{
    public string DeviceId { get; set; } = string.Empty;
    public string AlertType { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public long LogId { get; set; }
    public string Timestamp { get; set; } = string.Empty;
}
