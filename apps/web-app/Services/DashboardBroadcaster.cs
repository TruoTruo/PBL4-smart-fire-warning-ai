using Microsoft.AspNetCore.SignalR;
using SmartFireWeb.Services;
using SmartFireWeb.Hubs;

namespace SmartFireWeb.Services;

/// <summary>
/// Đọc lại CSDL rồi đẩy dashboard xuống trình duyệt qua SignalR, tách 2 nhóm Admin / User
/// để người không phải Admin không nhận SĐT, email.
/// Nhiều yêu cầu đến dồn dập (Pi gửi 2,5 s/lần, cảnh báo, xác nhận...) chỉ chạy nối tiếp, không chồng lên nhau.
/// </summary>
public sealed class DashboardBroadcaster
{
    public const string AdminGroup = "admins";
    public const string UserGroup = "users";

    private readonly DatabaseService _db;
    private readonly MqttAlertSubscriber _mqtt;
    private readonly IHubContext<FireAlertHub> _hub;
    private readonly ILogger<DashboardBroadcaster> _logger;
    private int _running;
    private int _dirty;

    public DashboardBroadcaster(
        DatabaseService db,
        MqttAlertSubscriber mqtt,
        IHubContext<FireAlertHub> hub,
        ILogger<DashboardBroadcaster> logger)
    {
        _db = db;
        _mqtt = mqtt;
        _hub = hub;
        _logger = logger;
    }

    /// <summary>Yêu cầu gửi lại dashboard; không chờ, không ném lỗi.</summary>
    public void Request()
    {
        Volatile.Write(ref _dirty, 1);
        if (Interlocked.CompareExchange(ref _running, 1, 0) == 0)
            _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        try
        {
            while (Interlocked.Exchange(ref _dirty, 0) == 1)
            {
                try
                {
                    var data = await _db.FetchRealDataAsync();
                    await _hub.Clients.Group(AdminGroup).SendAsync("DashboardUpdated", DashboardPayload.Build(data, true, _mqtt));
                    await _hub.Clients.Group(UserGroup).SendAsync("DashboardUpdated", DashboardPayload.Build(data, false, _mqtt));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Không gửi được dashboard qua SignalR: {Message}", ex.Message);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }

        // Có yêu cầu mới đến đúng lúc vòng lặp vừa thoát
        if (Volatile.Read(ref _dirty) == 1) Request();
    }
}
