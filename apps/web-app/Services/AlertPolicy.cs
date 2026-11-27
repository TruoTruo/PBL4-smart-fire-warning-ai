namespace SmartFireWeb.Services;

/// <summary>
/// Quy định quá hạn / leo thang cảnh báo:
///   - Mức 2 (High) quá 5 phút, Mức 1 (Medium) quá 15 phút chưa ai xác nhận thì cảnh báo "quá hạn".
///   - Cảnh báo quá hạn được leo thang: mọi cán bộ đều xác nhận / ghi phiếu được, không chỉ người phụ trách khu vực.
/// Giờ so sánh là giờ máy chủ web (cùng mốc với mqtt-subscriber ghi AlertTime).
/// </summary>
public static class AlertPolicy
{
    public const int HighOverdueSeconds = 5 * 60;
    public const int MediumOverdueSeconds = 15 * 60;

    public static int? OverdueAfterSeconds(string? severity) => severity switch
    {
        "High" => HighOverdueSeconds,
        "Medium" => MediumOverdueSeconds,
        _ => null,
    };

    public static int PendingSeconds(DateTime alertTime) =>
        (int)Math.Max(0, (DateTime.Now - alertTime).TotalSeconds);

    public static bool IsOverdue(string? severity, DateTime alertTime, bool acknowledged) =>
        !acknowledged && OverdueAfterSeconds(severity) is int limit && PendingSeconds(alertTime) > limit;
}
