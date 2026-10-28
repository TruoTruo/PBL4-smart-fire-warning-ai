using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;
using SmartFireApp.Models;

namespace SmartFireApp.Services;

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
    /// <param name="host">Broker host (mặc định test.mosquitto.org theo file main_iot.py của Pi 5)</param>
    /// <param name="port">Port MQTT (mặc định 1883)</param>
    public async Task StartAsync(string host = "test.mosquitto.org", int port = 1883)
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
        // Format trong main_iot.py: { "nhiet_do": ..., "do_am": ..., "co_khoi": ... }
        if (topic.Equals("pbl4/sensors/fire_warning", StringComparison.OrdinalIgnoreCase))
        {
            double temp = 0;
            double hum = 0;
            int smoke = 0;

            if (root.TryGetProperty("nhiet_do", out var pTemp) && pTemp.TryGetDouble(out var tVal))
                temp = tVal;
            if (root.TryGetProperty("do_am", out var pHum) && pHum.TryGetDouble(out var hVal))
                hum = hVal;
            if (root.TryGetProperty("co_khoi", out var pSmoke) && pSmoke.TryGetInt32(out var sVal))
                smoke = sVal;

            bool isFireDetected = smoke == 1 || temp > 45.0;

            var reading = new SensorReading(
                DeviceId: "PI-05",
                ZoneName: "Xưởng Gia Công A - Block C2",
                Temperature: Math.Round(temp, 1),
                Humidity: Math.Round(hum, 1),
                GasPpm: smoke == 1 ? 620 : 180, // Nếu phát hiện khói, giả lập nồng độ ppm tương ứng
                AiDetected: isFireDetected,
                Timestamp: DateTime.Now
            );

            SensorReadingReceived?.Invoke(this, reading);

            // Nếu phát hiện khói hoặc nhiệt độ nguy hiểm (>45°C) -> Kích hoạt AlertRecord
            if (isFireDetected)
            {
                var alert = new AlertRecord(
                    AlertId: DateTime.Now.Ticks % 10000,
                    DeviceId: "PI-05",
                    ZoneName: "Xưởng Gia Công A",
                    AlertType: smoke == 1 ? "Phát hiện khói (MQ-2)" : "Nhiệt độ cao bất thường",
                    Severity: "High",
                    AlertTime: DateTime.Now,
                    Acknowledged: false,
                    SmsSent: true
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
            string deviceId = root.TryGetProperty("deviceId", out var pDev) ? pDev.GetString() ?? "PI-??" : "PI-??";
            double temp = root.TryGetProperty("temperature", out var pTemp) && pTemp.TryGetDouble(out var tVal) ? tVal : 0;
            double hum = root.TryGetProperty("humidity", out var pHum) && pHum.TryGetDouble(out var hVal) ? hVal : 0;
            double gas = root.TryGetProperty("gasPpm", out var pGas) && pGas.TryGetDouble(out var gVal) ? gVal : 0;
            bool ai = root.TryGetProperty("aiResult", out var pAi) && pAi.GetBoolean();

            var reading = new SensorReading(
                DeviceId: deviceId,
                ZoneName: "Khu vực " + deviceId,
                Temperature: Math.Round(temp, 1),
                Humidity: Math.Round(hum, 1),
                GasPpm: Math.Round(gas, 0),
                AiDetected: ai,
                Timestamp: DateTime.Now
            );
            SensorReadingReceived?.Invoke(this, reading);
        }
    }

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
