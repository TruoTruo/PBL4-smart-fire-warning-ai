using SmartFireWeb.Models;
using SmartFireWeb.Services;

namespace SmartFireWeb.Services;

/// <summary>
/// Dữ liệu dashboard gửi cho trình duyệt (REST và SignalR dùng chung):
///   - tài khoản không phải Admin không thấy SĐT / email của cán bộ;
///   - danh sách cảnh báo chỉ có cờ hasSnapshot, ảnh tải riêng qua /api/alerts/{id}/snapshot;
///   - thêm trạng thái MQTT, trạng thái camera và online/offline do Pi báo về.
/// </summary>
public static class DashboardPayload
{
    public static object Build(RealDashboardData data, bool isAdmin, MqttAlertSubscriber mqtt) => new
    {
        kpis = data.Kpis,
        users = isAdmin
            ? data.Users
            : data.Users.Select(u => u with { PhoneNumber = null, Email = null }).ToList(),
        devices = data.Devices,
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
