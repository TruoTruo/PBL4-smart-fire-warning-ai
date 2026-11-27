namespace SmartFireWeb.Services;

/// <summary>
/// Ảnh sơ đồ mặt bằng: chỉ nhận PNG / JPEG / WebP (kiểm tra theo byte đầu tệp, không tin phần khai báo),
/// không nhận SVG để tránh chèn mã vào trang.
/// </summary>
public readonly record struct FloorPlanImage(byte[] Bytes, string ContentType)
{
    public const int MaxBytes = 3 * 1024 * 1024;

    public string ToDataUrl() => $"data:{ContentType};base64,{Convert.ToBase64String(Bytes)}";

    /// <summary>Nhận data URL hoặc Base64 thuần; null nếu không phải ảnh hợp lệ.</summary>
    public static FloorPlanImage? Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        string b64 = input.Trim();
        if (b64.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int comma = b64.IndexOf(',');
            if (comma < 0 || !b64[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) return null;
            b64 = b64[(comma + 1)..];
        }
        if (b64.Length > (MaxBytes + 1024 * 1024) / 3 * 4) // quá lớn thì khỏi giải mã
            return new FloorPlanImage(new byte[MaxBytes + 1], "image/png");

        byte[] bytes;
        try { bytes = Convert.FromBase64String(b64); }
        catch (FormatException) { return null; }

        string? type = bytes switch
        {
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => "image/png",
            [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
            [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => "image/webp",
            _ => null,
        };
        return type == null ? null : new FloorPlanImage(bytes, type);
    }
}
