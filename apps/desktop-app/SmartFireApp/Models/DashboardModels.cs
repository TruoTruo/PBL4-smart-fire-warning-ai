namespace SmartFireApp.Models;

public sealed record UserModel(
    int UserId,
    string Username,
    string FullName,
    string? PhoneNumber,
    string? Email,
    string Role,
    DateTime CreatedAt);

public sealed record DeviceCardModel(
    string DeviceId,
    string DeviceName,
    string ZoneName,
    string DeviceType,
    string Status,
    string? IpAddress,
    double Temperature,
    double Humidity,
    double GasPpm,
    bool AiResult,
    double? ConfidenceScore,
    DateTime? RecordedAt);

public sealed record AlertModel(
    long AlertId,
    string DeviceId,
    string DeviceName,
    string ZoneName,
    string AlertType,
    string Severity,
    DateTime AlertTime,
    bool SmsSent,
    bool Acknowledged);

public sealed record EnvironmentLogModel(
    long LogId,
    string DeviceId,
    string DeviceName,
    double Temperature,
    double Humidity,
    double GasPpm,
    bool AiResult,
    double? ConfidenceScore,
    DateTime RecordedAt);

public sealed record DatabaseKpis(
    int TotalDevices,
    int ActiveDevices,
    int PendingAlerts,
    int TotalLogs,
    int TotalUsers,
    string DatabaseName,
    string ServerHost);

public sealed record RealDashboardData(
    DatabaseKpis Kpis,
    IReadOnlyList<UserModel> Users,
    IReadOnlyList<DeviceCardModel> Devices,
    IReadOnlyList<AlertModel> Alerts,
    IReadOnlyList<EnvironmentLogModel> Logs,
    bool IsConnected);

// Backward compatibility models
public sealed record SensorReading(
    string DeviceId,
    string ZoneName,
    double Temperature,
    double Humidity,
    double GasPpm,
    bool AiDetected,
    DateTime Timestamp,
    double? ConfidenceScore = null);

public sealed record AlertRecord(
    long AlertId,
    string DeviceId,
    string ZoneName,
    string AlertType,
    string Severity,
    DateTime AlertTime,
    bool Acknowledged,
    bool SmsSent);

public sealed record KpiSnapshot(
    int SensorsOnline,
    int SensorsTotal,
    int FireDetected,
    int HeatWarnings,
    int ResponseMs);
