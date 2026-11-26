using Microsoft.AspNetCore.SignalR;
using SmartFireWeb.Hubs;

namespace SmartFireWeb.Services;

/// <summary>
/// Background Service cháº¡y ngáº§m cÃ¹ng Web API Ä‘á»ƒ duy trÃ¬ káº¿t ná»‘i MQTT vá»›i Raspberry Pi 5
/// vÃ  Ä‘áº©y sá»± kiá»‡n cáº­p nháº­t xuá»‘ng trÃ¬nh duyá»‡t qua SignalR ngay khi cÃ³ dá»¯ liá»‡u má»›i.
/// </summary>
public sealed class MqttHostedService : BackgroundService
{
    private readonly MqttAlertSubscriber _mqtt;
    private readonly DatabaseService _db;
    private readonly IHubContext<FireAlertHub> _hub;
    private readonly ILogger<MqttHostedService> _logger;

    public MqttHostedService(
        MqttAlertSubscriber mqtt,
        DatabaseService db,
        IHubContext<FireAlertHub> hub,
        ILogger<MqttHostedService> logger)
    {
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
            await _mqtt.StartAsync("192.168.137.191", 1883);
            _logger.LogInformation("ÄÃ£ káº¿t ná»‘i MQTT Broker 192.168.137.191:1883");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("KhÃ´ng thá»ƒ káº¿t ná»‘i MQTT Broker lÃºc khá»Ÿi Ä‘á»™ng: {Message}", ex.Message);
        }

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _mqtt.DisposeAsync();
        await base.StopAsync(cancellationToken);
    }
}



