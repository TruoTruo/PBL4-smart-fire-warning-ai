using SmartFireApp.Models;

namespace SmartFireApp.Services;

/// <summary>
/// Boundary for the later MQTTnet implementation. The dashboard only depends on this event.
/// Expected topic: firealert/alarm/{deviceId}; QoS 1.
/// </summary>
public sealed class MqttAlertSubscriber
{
    public event EventHandler<AlertRecord>? AlertReceived;

    public void RaiseAlert(AlertRecord alert) => AlertReceived?.Invoke(this, alert);
}
