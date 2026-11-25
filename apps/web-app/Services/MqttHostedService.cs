using Microsoft.AspNetCore.SignalR;
using SmartFireApp.Services;
using SmartFireWeb.Hubs;

namespace SmartFireWeb.Services;

/// <summary>
/// Background Service chạy ngầm cùng Web API để duy trì kết nối MQTT với Raspberry Pi 5
/// và đẩy sự kiện cập nhật xuống trình duyệt qua SignalR ngay khi có dữ liệu mới.
/// </summary>
public sealed class MqttHostedService : BackgroundService
{
    private readonly MqttAlertSubscriber _mqtt;
    private readonly DatabaseService _db;
    private readonly IHubContext<FireAlertHub> _hub;
    private readonly ILogger<MqttHostedService> _logger;
    private readonly AppSettings _settings;

    public MqttHostedService(
        MqttAlertSubscriber mqtt,
        DatabaseService db,
        IHubContext<FireAlertHub> hub,
        ILogger<MqttHostedService> logger,
        AppSettings settings)
    {
        _settings = settings;
        _mqtt = mqtt;
        _db = db;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _mqtt.SensorReadingReceived += async (_, reading) =>
        {
            var data = await _db.FetchRealDataAsync();
            await _hub.Clients.All.SendAsync("DashboardUpdated", data, stoppingToken);
        };

        _mqtt.AlertReceived += async (_, alert) =>
        {
            var data = await _db.FetchRealDataAsync();
            await _hub.Clients.All.SendAsync("DashboardUpdated", data, stoppingToken);
        };

        try
        {
            await _mqtt.StartAsync(_settings.MqttHost, _settings.MqttPort);
            _logger.LogInformation("Đã kết nối MQTT Broker {Host}:{Port}", _settings.MqttHost, _settings.MqttPort);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Không thể kết nối MQTT Broker lúc khởi động: {Message}", ex.Message);
        }

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _mqtt.DisposeAsync();
        await base.StopAsync(cancellationToken);
    }
}
