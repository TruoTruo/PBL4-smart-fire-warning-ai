using SmartFireApp.Models;

namespace SmartFireApp.Services;

/// <summary>
/// Supplies deterministic demo data until the MQTT and MySQL adapters are configured.
/// Its records mirror the fields emitted by firealert/alarm/{deviceId} and AlertHistory.
/// </summary>
public sealed class DashboardDataService
{
    private readonly Random _random = new(24);

    public SensorReading GetLatestReading() => new(
        "PI-01", "Xưởng Gia Công A - Block C2",
        Math.Round(31.2 + _random.NextDouble() * 3.6, 1),
        Math.Round(46.0 + _random.NextDouble() * 5.0, 1),
        Math.Round(180 + _random.NextDouble() * 28, 0),
        false, DateTime.Now);

    public KpiSnapshot GetKpiSnapshot() => new(
        SensorsOnline: 32, SensorsTotal: 32,
        FireDetected: 0,
        HeatWarnings: 1,
        ResponseMs: 120 + _random.Next(-12, 12));

    public IReadOnlyList<AlertRecord> GetRecentAlerts() =>
    [
        new(1048, "PI-01", "Xưởng Gia Công A", "Khói tăng đột biến",     "High",   DateTime.Today.AddHours(15).AddMinutes(28), true,  true),
        new(1047, "PI-04", "Phòng máy chủ",    "Nhiệt độ cao bất thường","Medium", DateTime.Today.AddHours(14).AddMinutes(50), true,  true),
        new(1046, "PI-02", "Kho vật tư",        "Thiết bị mất kết nối",  "Low",    DateTime.Today.AddHours(14).AddMinutes(10), true,  false),
        new(1045, "PI-03", "Zone C",             "Hiệu chuẩn hoàn tất",  "Low",    DateTime.Today.AddHours(14).AddMinutes(10), true,  false),
    ];
}

