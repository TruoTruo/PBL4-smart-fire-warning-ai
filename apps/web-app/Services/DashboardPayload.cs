using SmartFireWeb.Models;
using SmartFireWeb.Services;

namespace SmartFireWeb.Services;

/// <summary>
/// Dữ liệu dashboard gửi cho trình duyệt (REST và SignalR dùng chung):
///   - tài khoản không phải Admin không thấy SĐT / email / tên đăng nhập của cán bộ, IP trạm, tên máy chủ CSDL;
///   - danh sách cảnh báo chỉ có cờ hasSnapshot, ảnh tải riêng qua /api/alerts/{id}/snapshot;
///   - thêm trạng thái MQTT, trạng thái camera và online/offline do Pi báo về;
///   - khu vực kèm tên và SĐT cán bộ phụ trách (số liên hệ khi có sự cố, mọi cán bộ đều thấy).
/// </summary>
public static class DashboardPayload
{
    public static object Build(RealDashboardData data, bool isAdmin, MqttAlertSubscriber mqtt) => new
    {
        kpis = isAdmin ? data.Kpis : data.Kpis with { DatabaseName = "", ServerHost = "" },
        users = isAdmin
            ? data.Users
            : data.Users.Select(u => u with { PhoneNumber = null, Email = null, Username = "" }).ToList(),
        devices = isAdmin ? data.Devices : data.Devices.Select(d => d with { IpAddress = null }).ToList(),
        zones = data.Zones ?? [],
        alerts = data.Alerts,
        logs = data.Logs,
        isConnected = data.IsConnected,
        error = data.Error,
        mqttConnected = mqtt.IsConnected,
        mqttError = mqtt.IsConnected ? null : mqtt.LastError,
        cameras = mqtt.CameraStatuses.Values.OrderBy(c => c.DeviceId).ToList(),
        piStatuses = mqtt.PiStatuses.Values.OrderBy(p => p.DeviceId).ToList(),
    };
}
