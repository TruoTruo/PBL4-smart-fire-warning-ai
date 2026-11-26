﻿using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;
using SmartFireWeb.Models;

namespace SmartFireWeb.Services;

/// <summary>
/// Quản lý kết nối MQTT để nhận dữ liệu cảm biến trực tiếp từ Raspberry Pi 5
/// và các thông báo cảnh báo từ hệ thống.
/// Broker chưa chạy hoặc bị ngắt giữa chừng thì tự thử kết nối lại (1 s, 2 s, 4 s... tối đa 30 s).
/// </summary>
public sealed class MqttAlertSubscriber : IAsyncDisposable
{
    private static readonly TimeSpan MinRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly ConcurrentDictionary<string, CameraStatusInfo> _cameraStatuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PiStatusInfo> _piStatuses = new(StringComparer.OrdinalIgnoreCase);
    private IMqttClient? _mqttClient;
    private MqttClientOptions? _options;
    private volatile bool _autoReconnect;
    private int _reconnectScheduled;
    private int _lastStatus = -1;   // -1 chưa biết, 0 mất kết nối, 1 đang kết nối
    private bool _isDisposed;

    /// <summary>Bắn ra khi nhận được cảnh báo cháy mới (AlertHistory/Alarm).</summary>
    public event EventHandler<AlertRecord>? AlertReceived;

    /// <summary>Bắn ra khi nhận được telemetry nhiệt độ, độ ẩm, khói từ Raspberry Pi 5 (kể cả bản tin heartbeat).</summary>
    public event EventHandler<SensorReading>? SensorReadingReceived;

    /// <summary>Trạng thái kết nối MQTT (true = Đang kết nối, false = Mất kết nối). Chỉ bắn khi trạng thái đổi.</summary>
    public event EventHandler<bool>? ConnectionStatusChanged;

    /// <summary>Bắn ra khi Pi báo camera_ok đổi giá trị (mất / có lại tín hiệu camera).</summary>
    public event EventHandler<CameraStatusInfo>? CameraStatusChanged;

    /// <summary>Bắn ra khi Pi đổi trạng thái online / offline (topic pbl4/status/{DeviceId}, có LWT).</summary>
    public event EventHandler<PiStatusInfo>? PiStatusChanged;

    public bool IsConnected => _mqttClient?.IsConnected ?? false;

    /// <summary>Lỗi của lần kết nối thất bại gần nhất (null khi đang kết nối tốt).</summary>
    public string? LastError { get; private set; }

    /// <summary>Trạng thái camera mới nhất theo từng thiết bị (chỉ có khi Pi gửi camera_ok / khoi_camera).</summary>
    public IReadOnlyDictionary<string, CameraStatusInfo> CameraStatuses => _cameraStatuses;

    /// <summary>Trạng thái online / offline mới nhất của từng Pi.</summary>
    public IReadOnlyDictionary<string, PiStatusInfo> PiStatuses => _piStatuses;

    /// <summary>
    /// Khởi động kết nối MQTT tới Broker và lắng nghe dữ liệu từ Raspberry Pi 5.
    /// Chỉ trả về khi đã kết nối được; broker chưa chạy thì tự thử lại, không bỏ cuộc.
    /// </summary>
    /// <param name="host">Broker host (mặc định 192.168.137.191 - PHẢI trùng broker với Pi và mqtt-subscriber)</param>
    /// <param name="port">Port MQTT (mặc định 1883)</param>
    public Task StartAsync(string host = AppSettings.DefaultMqttHost, int port = AppSettings.DefaultMqttPort) =>
        StartAsync(host, port, CancellationToken.None);

    /// <inheritdoc cref="StartAsync(string, int)"/>
    public async Task StartAsync(string host, int port, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_mqttClient != null && _mqttClient.IsConnected)
            return;

        if (_mqttClient == null)
        {
            var factory = new MqttFactory();
            _mqttClient = factory.CreateMqttClient();

            _mqttClient.ConnectedAsync += _ =>
            {
                LastError = null;
                SetStatus(true);
                return Task.CompletedTask;
            };

            _mqttClient.DisconnectedAsync += e =>
            {
                SetStatus(false);
                // Đang chạy mà bị ngắt (broker khởi động lại, mất mạng) -> tự kết nối lại ở luồng nền
                if (e.ClientWasConnected && _autoReconnect && !_isDisposed)
                    ScheduleReconnect();
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
        }

        _options = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithClientId($"SmartFireWeb_{Guid.NewGuid():N}")
            .WithCleanSession()
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .Build();

        _autoReconnect = true;
        await ConnectWithRetryAsync(cancellationToken);
    }

    private void ScheduleReconnect()
    {
        if (Interlocked.Exchange(ref _reconnectScheduled, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try { await ConnectWithRetryAsync(CancellationToken.None); }
            catch (OperationCanceledException) { /* đang tắt */ }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MQTT Reconnect Error] {ex.Message}"); }
            finally { Interlocked.Exchange(ref _reconnectScheduled, 0); }
        });
    }

    private async Task ConnectWithRetryAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = linked.Token;

        await _connectLock.WaitAsync(token);
        try
        {
            var delay = MinRetryDelay;
            while (_autoReconnect && _mqttClient is { IsConnected: false } client)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(ConnectTimeout);
                    await client.ConnectAsync(_options!, timeout.Token);
                    await SubscribeTopicsAsync(client, token);
                    return;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    System.Diagnostics.Debug.WriteLine($"[MQTT] Chưa kết nối được broker, thử lại sau {delay.TotalSeconds:0} s: {ex.Message}");
                    // Kết nối được nhưng subscribe lỗi: ngắt để lần sau làm lại từ đầu
                    if (client.IsConnected)
                    {
                        try { await client.DisconnectAsync(); } catch { /* bỏ qua */ }
                    }
                    SetStatus(false);
                }

                await Task.Delay(delay, token);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetryDelay.Ticks));
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private static async Task SubscribeTopicsAsync(IMqttClient client, CancellationToken token)
    {
        // 1. Topic từ Raspberry Pi 5 (main_iot.py)
        // 2. Các topic từ backend service (mqtt-subscriber)
        var subscribe = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter("pbl4/sensors/fire_warning")
            .WithTopicFilter("firealert/alarm/+")
            .WithTopicFilter("firealert/sensor-data/+")
            .WithTopicFilter("pbl4/status/+")
            .Build();
        await client.SubscribeAsync(subscribe, token);
    }

    private void SetStatus(bool connected)
    {
        int value = connected ? 1 : 0;
        if (Interlocked.Exchange(ref _lastStatus, value) != value)
            ConnectionStatusChanged?.Invoke(this, connected);
    }

    private void ProcessIncomingMessage(string topic, ArraySegment<byte> payloadSegment)
    {
        if (payloadSegment.Count == 0) return;
        string json = Encoding.UTF8.GetString(payloadSegment);

        // Trạng thái Pi: "online" / "offline" (chuỗi thường hoặc JSON {"status": "..."}), không phải bản tin cảm biến
        if (topic.StartsWith("pbl4/status/", StringComparison.OrdinalIgnoreCase))
        {
            ProcessStatusMessage(topic["pbl4/status/".Length..], json);
            return;
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;

        // Trường hợp 1: Nhận trực tiếp từ Raspberry Pi 5 (Topic: pbl4/sensors/fire_warning)
        // Format từ main_iot.py: { "device_id", "nhiet_do", "do_am", "co_khoi", "co_lua", "muc_do", "confidence",
        //                          "camera_ok" (tùy chọn), "khoi_camera" (tùy chọn) }
        // Heartbeat: muc_do = 0, nhiet_do có thể null.
        // Pi đã tự đánh giá (sensor fusion) nên app KHÔNG tự đặt ngưỡng: chỉ tin theo "muc_do".
        if (topic.Equals("pbl4/sensors/fire_warning", StringComparison.OrdinalIgnoreCase))
        {
            string deviceId = GetString(root, "device_id") ?? "PI-01";
            double? temp = GetDouble(root, "nhiet_do");           // DHT11 lỗi -> Pi gửi null, giữ nguyên null
            double? hum = GetDouble(root, "do_am");
            bool smoke = GetFlag(root, "co_khoi") == true;        // MQ-2 chỉ cho biết CÓ / KHÔNG có khói
            double? smokeLevel = GetDouble(root, "muc_khoi");     // mức khói (%) nếu Pi đọc bằng ADC
            bool fire = GetFlag(root, "co_lua") == true;          // YOLO đã xác nhận lửa ổn định
            int level = GetInt(root, "muc_do") ?? 0;              // 0 bình thường, 1 cảnh báo sớm, 2 báo cháy
            double? confidence = fire ? GetDouble(root, "confidence") : null;   // độ tin cậy (%) của YOLO
            bool? cameraOk = GetFlag(root, "camera_ok");
            bool? cameraSmoke = GetFlag(root, "khoi_camera");

            if (cameraOk != null || cameraSmoke != null)
                UpdateCameraStatus(deviceId, cameraOk, cameraSmoke);

            var reading = new SensorReading(
                DeviceId: deviceId,
                ZoneName: "Khu vực " + deviceId,
                Temperature: temp is double t ? Math.Round(t, 1) : null,
                Humidity: hum is double h ? Math.Round(h, 1) : null,
                SmokeDetected: smoke,
                AiDetected: fire,
                Timestamp: DateTime.Now,
                ConfidenceScore: confidence,
                AlertLevel: level,
                SmokeLevel: smokeLevel,
                CameraOk: cameraOk,
                CameraSmoke: cameraSmoke
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
            string deviceId = GetString(root, "deviceId") ?? "PI-??";
            string alertType = GetString(root, "alertType") ?? "Cảnh báo cháy";
            string severity = GetString(root, "severity") ?? "High";
            long logId = root.TryGetProperty("logId", out var pLog) && pLog.ValueKind == JsonValueKind.Number
                         && pLog.TryGetInt64(out var lVal) ? lVal : (DateTime.Now.Ticks % 10000);

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
            double? temp = GetDouble(root, "temperature");
            double? hum = GetDouble(root, "humidity");
            bool smoke = GetBool(root, "smoke_detected") ?? GetBool(root, "smokeDetected") ?? false;
            bool ai = GetBool(root, "ai_result") ?? GetBool(root, "aiResult") ?? false;

            var reading = new SensorReading(
                DeviceId: deviceId,
                ZoneName: "Khu vực " + deviceId,
                Temperature: temp is double t ? Math.Round(t, 1) : null,
                Humidity: hum is double h ? Math.Round(h, 1) : null,
                SmokeDetected: smoke,
                AiDetected: ai,
                Timestamp: DateTime.Now,
                ConfidenceScore: GetDouble(root, "confidence"),
                SmokeLevel: GetDouble(root, "smoke_level") ?? GetDouble(root, "muc_khoi")
            );
            SensorReadingReceived?.Invoke(this, reading);
        }
    }

    private void ProcessStatusMessage(string deviceId, string payload)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return;
        string text = payload.Trim().Trim('"');
        if (text.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                text = GetString(doc.RootElement, "status") ?? GetString(doc.RootElement, "trang_thai") ?? "";
            }
            catch (JsonException)
            {
                return;
            }
        }

        bool online;
        if (text.Equals("online", StringComparison.OrdinalIgnoreCase)) online = true;
        else if (text.Equals("offline", StringComparison.OrdinalIgnoreCase)) online = false;
        else return;

        var info = new PiStatusInfo(deviceId, online, DateTime.Now);
        bool changed = !_piStatuses.TryGetValue(deviceId, out var old) || old.Online != online;
        _piStatuses[deviceId] = info;
        if (changed) PiStatusChanged?.Invoke(this, info);
    }

    private void UpdateCameraStatus(string deviceId, bool? cameraOk, bool? cameraSmoke)
    {
        var now = DateTime.Now;
        bool changed = true;
        var info = _cameraStatuses.AddOrUpdate(deviceId,
            _ => new CameraStatusInfo(deviceId, cameraOk, cameraSmoke, now),
            (_, old) =>
            {
                // Bản tin thiếu trường nào thì giữ giá trị cũ của trường đó
                var ok = cameraOk ?? old.CameraOk;
                changed = ok != old.CameraOk;
                return new CameraStatusInfo(deviceId, ok, cameraSmoke ?? old.CameraSmoke, now);
            });
        if (changed) CameraStatusChanged?.Invoke(this, info);
    }

    // Các hàm đọc JSON an toàn: giá trị null / sai kiểu thì trả về null thay vì ném lỗi
    // (DHT11 lỗi -> Pi gửi "nhiet_do": null, nếu dùng TryGetDouble trực tiếp sẽ bị exception và mất cả message)
    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static double? GetDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var v)
            && double.IsFinite(v) ? v : (double?)null;

    // Nhận cả 1 lẫn 1.0
    private static int? GetInt(JsonElement root, string name) =>
        GetDouble(root, name) is double v && v >= int.MinValue && v <= int.MaxValue ? (int)Math.Round(v) : null;

    private static bool? GetBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var p) && (p.ValueKind == JsonValueKind.True || p.ValueKind == JsonValueKind.False)
            ? p.GetBoolean() : (bool?)null;

    // Cờ 0/1 hoặc true/false (Python có thể gửi kiểu nào cũng được)
    private static bool? GetFlag(JsonElement root, string name) =>
        GetBool(root, name) ?? (GetDouble(root, name) is double v ? v != 0 : null);

    public async Task StopAsync()
    {
        _autoReconnect = false;
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
        _autoReconnect = false;
        _lifetime.Cancel();

        if (_mqttClient != null)
        {
            try
            {
                if (_mqttClient.IsConnected)
                {
                    await _mqttClient.DisconnectAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MQTT Dispose Error] {ex.Message}");
            }
            _mqttClient.Dispose();
            _mqttClient = null;
        }
    }
}
