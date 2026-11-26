using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;
using SmartFireWeb.Models;

namespace SmartFireWeb.Services;

/// <summary>
/// Quản lý kết nối MQTT để nhận dữ liệu cảm biến trực tiếp từ Raspberry Pi 5
/// và các thông báo cảnh báo từ hệ thống.
/// </summary>
public sealed class MqttAlertSubscriber : IAsyncDisposable
{
    private IMqttClient? _mqttClient;
    private bool _isDisposed;

    /// <summary>Bắn ra khi nhận được cảnh báo cháy mới (AlertHistory/Alarm).</summary>
    public event EventHandler<AlertRecord>? AlertReceived;

    /// <summary>Bắn ra khi nhận được telemetry nhiệt độ, độ ẩm, khói từ Raspberry Pi 5.</summary>
    public event EventHandler<SensorReading>? SensorReadingReceived;

    /// <summary>Trạng thái kết nối MQTT (true = Đang kết nối, false = Mất kết nối).</summary>
    public event EventHandler<bool>? ConnectionStatusChanged;

    public bool IsConnected => _mqttClient?.IsConnected ?? false;

    /// <summary>
    /// Khởi động kết nối MQTT tới Broker và lắng nghe dữ liệu từ Raspberry Pi 5.
    /// </summary>
    /// <param name="host">Broker host (mặc định 192.168.137.191 - PHẢI trùng broker với Pi và mqtt-subscriber)</param>
    /// <param name="port">Port MQTT (mặc định 1883)</param>
    public async Task StartAsync(string host = "192.168.137.191", int port = 1883)
    {
        if (_mqttClient != null && _mqttClient.IsConnected)
            return;

        var factory = new MqttFactory();
        _mqttClient = factory.CreateMqttClient();

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithClientId($"SmartFireApp_WinForms_{Guid.NewGuid():N}")
            .WithCleanSession()
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(60))
            .Build();

        _mqttClient.ConnectedAsync += async _ =>
        {
            ConnectionStatusChanged?.Invoke(this, true);

            // 1. Subscribe topic từ Raspberry Pi 5 (main_iot.py)
            await _mqttClient.SubscribeAsync("pbl4/sensors/fire_warning");

            // 2. Subscribe các topic từ backend service (nếu có)
            await _mqttClient.SubscribeAsync("firealert/alarm/+");
            await _mqttClient.SubscribeAsync("firealert/sensor-data/+");
        };

        _mqttClient.DisconnectedAsync += _ =>
        {
            ConnectionStatusChanged?.Invoke(this, false);
            return Task.CompletedTask;
        };

        _mqttClient.ApplicationMessageReceivedAsync += e =>
        {
            try
            {
                ProcessIncomingMessage(e.ApplicationMessage.Topic, e.ApplicationMessage.PayloadSegment);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MQTT Error] {ex.Message}");
            }
            return Task.CompletedTask;
        };

        await _mqttClient.ConnectAsync(options);
    }

    private void ProcessIncomingMessage(string topic, ArraySegment<byte> payloadSegment)
    {
        if (payloadSegment.Count == 0) return;
        string json = Encoding.UTF8.GetString(payloadSegment);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Trường hợp 1: Nhận trực tiếp từ Raspberry Pi 5 (Topic: pbl4/sensors/fire_warning)
        // Format từ main_iot.py: { "device_id", "nhiet_do", "do_am", "co_khoi", "co_lua", "muc_do", "confidence" }
        // Pi đã tự đánh giá (sensor fusion) nên app KHÔNG tự đặt ngưỡng: chỉ tin theo "muc_do".
        if (topic.Equals("pbl4/sensors/fire_warning", StringComparison.OrdinalIgnoreCase))
        {
            string deviceId = GetString(root, "device_id") ?? "PI-01";
            double temp = GetDouble(root, "nhiet_do") ?? 0;      // DHT11 lỗi -> Pi gửi null
            double hum = GetDouble(root, "do_am") ?? 0;
            bool smoke = GetInt(root, "co_khoi") == 1;            // MQ-2 chỉ cho biết CÓ / KHÔNG có khói
            double? smokeLevel = GetDouble(root, "muc_khoi");          // mức khói (%) nếu Pi đọc bằng ADC
            bool fire = GetInt(root, "co_lua") == 1;              // YOLO đã xác nhận lửa ổn định
            int level = GetInt(root, "muc_do") ?? 0;              // 0 bình thường, 1 cảnh báo sớm, 2 báo cháy
            double? confidence = fire ? GetDouble(root, "confidence") : null;   // độ tin cậy (%) của YOLO

            var reading = new SensorReading(
                DeviceId: deviceId,
                ZoneName: "Khu vực " + deviceId,
                Temperature: Math.Round(temp, 1),
                Humidity: Math.Round(hum, 1),
                SmokeDetected: smoke,
                AiDetected: fire,
                Timestamp: DateTime.Now,
                ConfidenceScore: confidence,
                AlertLevel: level,
                SmokeLevel: smokeLevel
            );
            SensorReadingReceived?.Invoke(this, reading);

            // Chỉ báo cháy khi Pi báo mức 2
            if (level >= 2)
            {
                var alert = new AlertRecord(
                    AlertId: DateTime.Now.Ticks % 10000,
                    DeviceId: deviceId,
                    ZoneName: "Khu vực " + deviceId,
                    AlertType: "Fire",
                    Severity: "High",
                    AlertTime: DateTime.Now,
                    Acknowledged: false,
                    SmsSent: false
                );
                AlertReceived?.Invoke(this, alert);
            }
        }
        // Trường hợp 2: Nhận cảnh báo từ backend topic: firealert/alarm/{deviceId}
        else if (topic.StartsWith("firealert/alarm", StringComparison.OrdinalIgnoreCase))
        {
            string deviceId = root.TryGetProperty("deviceId", out var pDev) ? pDev.GetString() ?? "PI-??" : "PI-??";
            string alertType = root.TryGetProperty("alertType", out var pType) ? pType.GetString() ?? "Cảnh báo cháy" : "Cảnh báo cháy";
            string severity = root.TryGetProperty("severity", out var pSev) ? pSev.GetString() ?? "High" : "High";
            long logId = root.TryGetProperty("logId", out var pLog) && pLog.TryGetInt64(out var lVal) ? lVal : (DateTime.Now.Ticks % 10000);

            var alert = new AlertRecord(
                AlertId: logId,
                DeviceId: deviceId,
                ZoneName: "Khu vực cảm biến",
                AlertType: alertType,
                Severity: severity,
                AlertTime: DateTime.Now,
                Acknowledged: false,
                SmsSent: true
            );
            AlertReceived?.Invoke(this, alert);
        }
        // Trường hợp 3: Nhận sensor data chuẩn từ backend: firealert/sensor-data/{deviceId}
        else if (topic.StartsWith("firealert/sensor-data", StringComparison.OrdinalIgnoreCase))
        {
            string deviceId = GetString(root, "device_id") ?? GetString(root, "deviceId") ?? "PI-??";
            double temp = GetDouble(root, "temperature") ?? 0;
            double hum = GetDouble(root, "humidity") ?? 0;
            bool smoke = GetBool(root, "smoke_detected") ?? GetBool(root, "smokeDetected") ?? false;
            bool ai = GetBool(root, "ai_result") ?? GetBool(root, "aiResult") ?? false;

            var reading = new SensorReading(
                DeviceId: deviceId,
                ZoneName: "Khu vực " + deviceId,
                Temperature: Math.Round(temp, 1),
                Humidity: Math.Round(hum, 1),
                SmokeDetected: smoke,
                AiDetected: ai,
                Timestamp: DateTime.Now,
                ConfidenceScore: GetDouble(root, "confidence"),
                SmokeLevel: GetDouble(root, "smoke_level") ?? GetDouble(root, "muc_khoi")
            );
            SensorReadingReceived?.Invoke(this, reading);
        }
    }

    // Các hàm đọc JSON an toàn: giá trị null / sai kiểu thì trả về null thay vì ném lỗi
    // (DHT11 lỗi -> Pi gửi "nhiet_do": null, nếu dùng TryGetDouble trực tiếp sẽ bị exception và mất cả message)
    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static double? GetDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var v)
            ? v : (double?)null;

    private static int? GetInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v)
            ? v : (int?)null;

    private static bool? GetBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var p) && (p.ValueKind == JsonValueKind.True || p.ValueKind == JsonValueKind.False)
            ? p.GetBoolean() : (bool?)null;

    public async Task StopAsync()
    {
        if (_mqttClient != null && _mqttClient.IsConnected)
        {
            await _mqttClient.DisconnectAsync();
        }
    }

    public void RaiseAlert(AlertRecord alert) => AlertReceived?.Invoke(this, alert);

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_mqttClient != null)
        {
            if (_mqttClient.IsConnected)
            {
                await _mqttClient.DisconnectAsync();
            }
            _mqttClient.Dispose();
            _mqttClient = null;
        }
    }
}



