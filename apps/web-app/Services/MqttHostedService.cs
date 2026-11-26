using SmartFireWeb.Services;

namespace SmartFireWeb.Services;

/// <summary>
/// Background Service chạy ngầm cùng Web API để duy trì kết nối MQTT với Raspberry Pi 5
/// và đẩy sự kiện cập nhật xuống trình duyệt qua SignalR ngay khi có dữ liệu mới.
/// Broker chưa chạy lúc khởi động thì MqttAlertSubscriber tự thử lại, web vẫn chạy bình thường.
/// </summary>
public sealed class MqttHostedService : BackgroundService
{
    private readonly MqttAlertSubscriber _mqtt;
    private readonly DashboardBroadcaster _broadcaster;
    private readonly ILogger<MqttHostedService> _logger;
    private readonly AppSettings _settings;

    public MqttHostedService(
        MqttAlertSubscriber mqtt,
        DashboardBroadcaster broadcaster,
        ILogger<MqttHostedService> logger,
        AppSettings settings)
    {
        _settings = settings;
        _mqtt = mqtt;
        _broadcaster = broadcaster;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _mqtt.SensorReadingReceived += (_, _) => _broadcaster.Request();
        _mqtt.AlertReceived += (_, _) => _broadcaster.Request();

        _mqtt.CameraStatusChanged += (_, cam) =>
        {
            if (cam.CameraOk == false)
                _logger.LogWarning("Pi {DeviceId} báo mất tín hiệu camera", cam.DeviceId);
            else if (cam.CameraOk == true)
                _logger.LogInformation("Pi {DeviceId} báo camera hoạt động", cam.DeviceId);
            _broadcaster.Request();
        };

        _mqtt.PiStatusChanged += (_, pi) =>
        {
            if (pi.Online) _logger.LogInformation("Pi {DeviceId} trực tuyến", pi.DeviceId);
            else _logger.LogWarning("Pi {DeviceId} ngoại tuyến", pi.DeviceId);
            _broadcaster.Request();
        };

        _mqtt.ConnectionStatusChanged += (_, connected) =>
        {
            if (connected)
                _logger.LogInformation("Đã kết nối MQTT Broker {Host}:{Port}", _settings.MqttHost, _settings.MqttPort);
            else
                _logger.LogWarning("Mất kết nối / chưa kết nối được MQTT Broker {Host}:{Port} ({Error}), đang tự thử lại...",
                    _settings.MqttHost, _settings.MqttPort, _mqtt.LastError ?? "broker ngắt kết nối");
            _broadcaster.Request();
        };

        try
        {
            await _mqtt.StartAsync(_settings.MqttHost, _settings.MqttPort, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // web đang tắt
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _mqtt.DisposeAsync();
        await base.StopAsync(cancellationToken);
    }
}
