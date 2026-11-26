namespace SmartFireWeb.Models;

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
    double? Temperature,          // null = DHT11 lỗi (mất cảm biến) hoặc chưa có số đo
    double? Humidity,
    bool SmokeDetected,
    double? SmokeLevel,
    int AlertLevel,
    bool AiResult,
    double? ConfidenceScore,
    DateTime? RecordedAt,
    int? ZoneId = null,
    string? ManagerName = null,   // cán bộ phụ trách khu vực của trạm
    string? ManagerPhone = null);

public sealed record AlertModel(
    long AlertId,
    string DeviceId,
    string DeviceName,
    string ZoneName,
    string AlertType,
    string Severity,
    DateTime AlertTime,
    bool SmsSent,
    bool Acknowledged,
    bool HasSnapshot = false,           // có ảnh bằng chứng; ảnh tải riêng theo AlertId (không kéo Base64 mỗi lần quét)
    string? AcknowledgedByName = null,  // cán bộ đã xác nhận
    DateTime? AcknowledgedAt = null,
    string? Resolution = null,          // kết quả phiếu sự cố: Handled / FalseAlarm / Test
    int? ManagerUserId = null);         // cán bộ phụ trách khu vực (để trang biết ai được xử lý)

/// <summary>Khu vực giám sát và cán bộ phụ trách (bảng Zones).</summary>
public sealed record ZoneModel(
    int ZoneId,
    string ZoneName,
    string? Description,
    int? ManagerUserId,
    string? ManagerName,
    string? ManagerPhone,
    int DeviceCount,
    int PendingAlerts);

/// <summary>Phiếu sự cố: một cảnh báo kèm số đo gốc (LogId), khu vực, người phụ trách và kết quả xử lý.</summary>
public sealed record IncidentModel(
    long AlertId,
    string DeviceId,
    string DeviceName,
    int? ZoneId,
    string ZoneName,
    string? ZoneDescription,
    int? ManagerUserId,
    string? ManagerName,
    string? ManagerPhone,
    string AlertType,
    string Severity,
    DateTime AlertTime,
    bool SmsSent,
    bool HasSnapshot,
    bool Acknowledged,
    string? AcknowledgedByName,
    DateTime? AcknowledgedAt,
    int? ResponseSeconds,         // từ lúc báo đến lúc xác nhận
    string? Resolution,
    string? ResolutionNote,
    long? LogId,                  // số đo cảm biến đã kích hoạt cảnh báo
    double? Temperature,
    double? Humidity,
    bool? SmokeDetected,
    double? SmokeLevel,
    int? AlertLevel,
    bool? AiResult,
    double? ConfidenceScore,
    DateTime? RecordedAt);

public sealed record EnvironmentLogModel(
    long LogId,
    string DeviceId,
    string DeviceName,
    double? Temperature,          // null = DHT11 lỗi lúc ghi
    double? Humidity,
    bool SmokeDetected,
    double? SmokeLevel,
    int AlertLevel,
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
    bool IsConnected,
    string? Error = null,         // thông báo lỗi CSDL khi IsConnected = false
    IReadOnlyList<ZoneModel>? Zones = null);

// Backward compatibility models
public sealed record SensorReading(
    string DeviceId,
    string ZoneName,
    double? Temperature,          // null = DHT11 lỗi
    double? Humidity,
    bool SmokeDetected,
    bool AiDetected,
    DateTime Timestamp,
    double? ConfidenceScore = null,
    int AlertLevel = 0,
    double? SmokeLevel = null,
    bool? CameraOk = null,        // Pi gửi "camera_ok"; null = bản Pi cũ chưa gửi
    bool? CameraSmoke = null);    // Pi gửi "khoi_camera": camera AI thấy khói

/// <summary>Pi trực tuyến / ngoại tuyến theo topic giữ lại (retained) pbl4/status/{DeviceId}.</summary>
public sealed record PiStatusInfo(
    string DeviceId,
    bool Online,
    DateTime UpdatedAt);

/// <summary>Trạng thái camera mới nhất mà Pi báo qua MQTT.</summary>
public sealed record CameraStatusInfo(
    string DeviceId,
    bool? CameraOk,
    bool? CameraSmoke,
    DateTime UpdatedAt);

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

public sealed record AlertStatItem(string DateLabel, string AlertType, int Count);

/// <summary>Một điểm trên biểu đồ nhiệt độ / độ ẩm (null = DHT11 lỗi lúc đo).</summary>
public sealed record EnvironmentPoint(DateTime RecordedAt, double? Temperature, double? Humidity, int AlertLevel);

/// <summary>Tổng hợp hiệu quả xử lý cảnh báo trong một khoảng thời gian (thời gian tính bằng giây).</summary>
public sealed record AlertReportSummary(
    int Total,
    int HighSeverity,
    int Acknowledged,
    int Pending,
    double? AvgResponseSeconds,
    int? MaxResponseSeconds,
    int AckWithin5Min,            // xác nhận trong 5 phút đầu
    int Handled,
    int FalseAlarm,
    int Test,
    int NoResolution);            // đã xác nhận nhưng chưa ghi kết quả phiếu

/// <summary>Một dòng thống kê theo khu vực.</summary>
public sealed record ZoneReportRow(
    int? ZoneId,
    string ZoneName,
    string? ManagerName,
    int Total,
    int HighSeverity,
    int Pending,
    double? AvgResponseSeconds,
    int FalseAlarm,
    int Resolved);                // số phiếu đã ghi kết quả

/// <summary>Một dòng thống kê theo cán bộ đã xác nhận cảnh báo.</summary>
public sealed record UserReportRow(
    int UserId,
    string FullName,
    int Acknowledged,
    double? AvgResponseSeconds,
    int AckWithin5Min,
    int Resolved);
