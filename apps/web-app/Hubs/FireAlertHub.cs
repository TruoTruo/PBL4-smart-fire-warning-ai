using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using SmartFireWeb.Services;

namespace SmartFireWeb.Hubs;

/// <summary>
/// SignalR Hub đẩy thông báo thời gian thực tới các trình duyệt đã đăng nhập.
/// Mỗi kết nối vào nhóm Admin hoặc User để nhận đúng phần dữ liệu được phép xem.
/// </summary>
[Authorize]
public sealed class FireAlertHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        string group = Context.User?.IsInRole("Admin") == true
            ? DashboardBroadcaster.AdminGroup
            : DashboardBroadcaster.UserGroup;
        await Groups.AddToGroupAsync(Context.ConnectionId, group);
        await base.OnConnectedAsync();
    }
}
