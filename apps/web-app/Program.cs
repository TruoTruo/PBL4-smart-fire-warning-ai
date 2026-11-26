using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using SmartFireWeb.Services;
using SmartFireWeb.Hubs;

var builder = WebApplication.CreateBuilder(args);

// Cho phép truy cập từ localhost và các thiết bị trong mạng LAN (điện thoại, laptop khác)
builder.WebHost.UseUrls("http://0.0.0.0:5000");

// Cùng khóa cấu hình với mqtt-subscriber: appsettings.json (+ appsettings.Local.json, biến môi trường)
var settings = AppSettings.Load(builder.Environment.ContentRootPath);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(new DatabaseService(settings.ConnectionString));
builder.Services.AddSingleton<MqttAlertSubscriber>();
builder.Services.AddSingleton<DashboardBroadcaster>();
builder.Services.AddHostedService<MqttHostedService>();
builder.Services.AddSignalR();

// Đăng nhập bằng cookie (HttpOnly). Trang và API cùng một origin nên không cần CORS.
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "SmartFire.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;   // demo trong LAN chạy http
        options.ExpireTimeSpan = TimeSpan.FromHours(8);                   // một ca trực
        options.SlidingExpiration = true;

        // API trả 401/403 dạng JSON thay vì chuyển hướng sang trang đăng nhập
        options.Events.OnRedirectToLogin = ctx => WriteJsonError(ctx.Response, StatusCodes.Status401Unauthorized,
            "Chưa đăng nhập hoặc phiên đăng nhập đã hết hạn.");
        options.Events.OnRedirectToAccessDenied = ctx => WriteJsonError(ctx.Response, StatusCodes.Status403Forbidden,
            "Tài khoản không có quyền thực hiện thao tác này.");
        options.Events.OnValidatePrincipal = UserSessionValidator.ValidateAsync;
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Admin", policy => policy.RequireRole("Admin"));

// Chống dò mật khẩu: tối đa 20 lần đăng nhập / phút / địa chỉ IP
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("login", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.OnRejected = (ctx, _) => new ValueTask(WriteJsonError(ctx.HttpContext.Response,
        StatusCodes.Status429TooManyRequests, "Đăng nhập sai quá nhiều lần, vui lòng thử lại sau 1 phút."));
});

var app = builder.Build();

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "same-origin";
    await next();
});
app.UseDefaultFiles(); // Tự động phục vụ wwwroot/index.html tại "/" (màn hình đăng nhập nằm trong trang)
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// SignalR Hub cho real-time (chỉ người đã đăng nhập)
app.MapHub<FireAlertHub>("/hubs/firealert").RequireAuthorization();

// ============================================================
// REST API ENDPOINTS
// ============================================================

// 1. Đăng nhập: kiểm tra bảng Users rồi cấp cookie
app.MapPost("/api/auth/login", async (LoginRequest req, DatabaseService db, HttpContext http) =>
{
    var (user, dbError) = await db.TryLoginAsync(req.Username ?? "", req.Password ?? "");
    if (dbError != null)
        return Results.Json(new { success = false, error = dbError }, statusCode: StatusCodes.Status503ServiceUnavailable);
    if (user == null)
        return Results.Json(new { success = false, error = "Tên đăng nhập hoặc mật khẩu không đúng." },
            statusCode: StatusCodes.Status401Unauthorized);

    var identity = new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, user.UserId.ToString()),
        new Claim(ClaimTypes.Name, user.Username),
        new Claim(ClaimTypes.GivenName, user.FullName),
        new Claim(ClaimTypes.Role, user.Role),
    ], CookieAuthenticationDefaults.AuthenticationScheme);

    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
        new AuthenticationProperties { IsPersistent = false });

    return Results.Ok(new { success = true, user = new { user.UserId, user.Username, user.FullName, user.Role } });
}).RequireRateLimiting("login");

// 2. Đăng xuất: xoá cookie
app.MapPost("/api/auth/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok(new { success = true });
});

// 3. Người đang đăng nhập (trang gọi khi mở lại để biết còn phiên hay không)
app.MapGet("/api/auth/me", (ClaimsPrincipal user) =>
    Results.Ok(new { success = true, user = CurrentUser(user) }))
    .RequireAuthorization();

// 3b. Đổi mật khẩu của chính mình (tài khoản demo phải đổi sau lần đăng nhập đầu)
app.MapPost("/api/auth/change-password", async (ChangePasswordRequest req, ClaimsPrincipal user, DatabaseService db) =>
{
    var (success, error) = await db.ChangePasswordAsync(CurrentUserId(user), req.CurrentPassword ?? "", req.NewPassword ?? "");
    return success
        ? Results.Ok(new { success = true })
        : Results.BadRequest(new { success = false, error });
}).RequireAuthorization().RequireRateLimiting("login");

// 4. Lấy toàn bộ dữ liệu thực từ 5 bảng MySQL (lỗi CSDL -> isConnected = false + error)
app.MapGet("/api/dashboard", async (ClaimsPrincipal user, DatabaseService db, MqttAlertSubscriber mqtt) =>
{
    var data = await db.FetchRealDataAsync();
    return Results.Ok(DashboardPayload.Build(data, user.IsInRole("Admin"), mqtt));
}).RequireAuthorization();

// 5. Xác nhận đã xử lý sự cố cháy: người xác nhận lấy từ cookie, không tin userId gửi lên
app.MapPost("/api/alerts/acknowledge", async (
    AcknowledgeRequest req,
    ClaimsPrincipal user,
    DatabaseService db,
    DashboardBroadcaster broadcaster) =>
{
    if (req.AlertId <= 0)
        return Results.BadRequest(new { success = false, error = "Mã cảnh báo không hợp lệ." });

    var denied = await CheckAlertPermissionAsync(req.AlertId, user, db);
    if (denied != null) return denied;

    bool ok = await db.AcknowledgeAlertAsync(req.AlertId, CurrentUserId(user));
    if (!ok)
        return Results.Json(new { success = false, error = $"Không xác nhận được sự cố #{req.AlertId} (không tồn tại hoặc lỗi CSDL)." },
            statusCode: StatusCodes.Status409Conflict);

    broadcaster.Request();
    return Results.Ok(new { success = true });
}).RequireAuthorization();

// 6. Ảnh bằng chứng của một cảnh báo (Pi gửi Base64 JPEG, lưu ở AlertHistory.SnapshotBase64)
app.MapGet("/api/alerts/{alertId:long}/snapshot", async (long alertId, DatabaseService db, HttpContext http) =>
{
    string? b64;
    try
    {
        b64 = await db.GetAlertSnapshotAsync(alertId);
    }
    catch (Exception ex)
    {
        return Results.Json(new { success = false, error = "Lỗi CSDL khi đọc ảnh: " + ex.Message },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    int comma = b64?.IndexOf(',') ?? -1;
    if (b64 != null && b64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
        b64 = b64[(comma + 1)..];

    byte[] bytes;
    try
    {
        bytes = string.IsNullOrEmpty(b64) ? [] : Convert.FromBase64String(b64);
    }
    catch (FormatException)
    {
        bytes = [];
    }
    if (bytes.Length == 0)
        return Results.Json(new { success = false, error = $"Cảnh báo #{alertId} không có ảnh." }, statusCode: StatusCodes.Status404NotFound);

    string contentType = bytes is [0x89, 0x50, 0x4E, 0x47, ..] ? "image/png" : "image/jpeg";
    http.Response.Headers.CacheControl = "private, max-age=3600";
    return Results.File(bytes, contentType);
}).RequireAuthorization();

// 6b. Lịch sử cảnh báo + thống kê theo ngày (mặc định 7 ngày gần nhất), lọc thêm loại / mức / trạng thái
app.MapGet("/api/alerts/history", async (
    DateOnly? fromDate, DateOnly? toDate, string? alertType, string? severity, string? status, DatabaseService db) =>
{
    var today = DateOnly.FromDateTime(DateTime.Now);
    var from = fromDate ?? today.AddDays(-6);
    var to = toDate ?? today;
    if (to < from) (from, to) = (to, from);
    if (to.DayNumber - from.DayNumber > 366)
        return Results.BadRequest(new { success = false, error = "Khoảng thời gian tối đa 1 năm." });

    var start = from.ToDateTime(TimeOnly.MinValue);
    var end = to.ToDateTime(TimeOnly.MaxValue);
    bool? ack = status switch { "pending" => false, "done" => true, _ => null };
    try
    {
        var alerts = await db.GetAlertsByDateRangeAsync(start, end, NullIfEmpty(alertType), NullIfEmpty(severity), ack);
        var stats = await db.GetAlertStatsAsync(start, end);
        return Results.Ok(new
        {
            success = true,
            alerts,
            stats,
            fromDate = from.ToString("yyyy-MM-dd"),
            toDate = to.ToString("yyyy-MM-dd"),
            limited = alerts.Count >= 500,
        });
    }
    catch (Exception ex)
    {
        return Results.Json(new { success = false, error = "Lỗi CSDL khi lọc lịch sử: " + ex.Message },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).RequireAuthorization();

// 6c. Chuỗi nhiệt độ / độ ẩm của một thiết bị cho biểu đồ (mặc định 60 phút gần nhất)
app.MapGet("/api/environment/{deviceId}", async (string deviceId, int? minutes, DatabaseService db) =>
{
    int span = Math.Clamp(minutes ?? 60, 5, 24 * 60);
    try
    {
        var points = await db.GetEnvironmentSeriesAsync(deviceId, DateTime.Now.AddMinutes(-span));
        // serverNow: trục thời gian theo đồng hồ máy chủ, không lệch theo giờ máy trình duyệt
        return Results.Ok(new { success = true, deviceId, minutes = span, serverNow = DateTime.Now, points });
    }
    catch (Exception ex)
    {
        return Results.Json(new { success = false, error = "Lỗi CSDL khi đọc số đo: " + ex.Message },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).RequireAuthorization();

// 6d. Thêm / sửa trạm giám sát (chỉ Admin)
app.MapPost("/api/devices", async (UpsertDeviceRequest req, DatabaseService db, DashboardBroadcaster broadcaster) =>
{
    string deviceId = (req.DeviceId ?? "").Trim();
    string deviceName = (req.DeviceName ?? "").Trim();
    string zoneName = (req.ZoneName ?? "").Trim();
    string? ip = string.IsNullOrWhiteSpace(req.IpAddress) ? null : req.IpAddress.Trim();

    string? error = null;
    if (deviceId.Length is 0 or > 20 || !deviceId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        error = "Mã thiết bị 1–20 ký tự, chỉ gồm chữ, số, '-' hoặc '_' (ví dụ PI-01).";
    else if (deviceName.Length is 0 or > 100 || zoneName.Length is 0 or > 100)
        error = "Tên trạm và khu vực bắt buộc, tối đa 100 ký tự.";
    else if (req.Status is not ("Active" or "Inactive" or "Maintenance"))
        error = "Trạng thái chỉ được là Active, Inactive hoặc Maintenance.";
    else if (ip != null && (ip.Length > 15 || ip.Count(c => c == '.') != 3
             || !System.Net.IPAddress.TryParse(ip, out var addr)
             || addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork))
        error = "Địa chỉ IP không hợp lệ (IPv4, ví dụ 192.168.1.101).";
    else if ((req.DeviceType ?? "").Length > 50)
        error = "Loại thiết bị tối đa 50 ký tự.";
    if (error != null)
        return Results.BadRequest(new { success = false, error });

    var (success, dbError) = await db.UpsertDeviceAsync(
        deviceId, deviceName, zoneName, req.DeviceType ?? "", req.Status!, ip, req.IsEdit);
    if (!success)
        return Results.BadRequest(new { success, error = dbError });

    broadcaster.Request();
    return Results.Ok(new { success = true });
}).RequireAuthorization("Admin");

// 6e. Xoá trạm giám sát (chỉ Admin; xoá kèm nhật ký và cảnh báo của trạm)
app.MapDelete("/api/devices/{deviceId}", async (string deviceId, DatabaseService db, DashboardBroadcaster broadcaster) =>
{
    var (success, error) = await db.DeleteDeviceAsync(deviceId);
    if (!success)
        return Results.Json(new { success, error }, statusCode: error?.StartsWith("Không tìm thấy") == true
            ? StatusCodes.Status404NotFound : StatusCodes.Status503ServiceUnavailable);

    broadcaster.Request();
    return Results.Ok(new { success = true });
}).RequireAuthorization("Admin");

// 6f. Phiếu sự cố: cảnh báo + số đo gốc + khu vực + người phụ trách + kết quả xử lý
app.MapGet("/api/alerts/{alertId:long}", async (long alertId, ClaimsPrincipal user, DatabaseService db) =>
{
    try
    {
        var incident = await db.GetIncidentAsync(alertId);
        if (incident == null)
            return Results.NotFound(new { success = false, error = $"Không tìm thấy cảnh báo #{alertId}." });

        // canHandle: trang dùng để hiện hoặc ẩn nút xử lý (máy chủ vẫn kiểm tra lại khi ghi)
        bool canHandle = CanHandleZone(user, incident.ManagerUserId);
        return Results.Ok(new { success = true, incident, canHandle });
    }
    catch (Exception ex)
    {
        return Results.Json(new { success = false, error = "Lỗi CSDL khi đọc phiếu sự cố: " + ex.Message },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).RequireAuthorization();

// 6g. Ghi kết quả xử lý vào phiếu sự cố (chỉ Admin hoặc cán bộ phụ trách khu vực)
app.MapPost("/api/alerts/{alertId:long}/resolve", async (
    long alertId,
    ResolveAlertRequest req,
    ClaimsPrincipal user,
    DatabaseService db,
    DashboardBroadcaster broadcaster) =>
{
    if (req.Resolution is not ("Handled" or "FalseAlarm" or "Test"))
        return Results.BadRequest(new { success = false, error = "Kết quả xử lý chỉ được là Đã xử lý, Báo nhầm hoặc Diễn tập." });
    string? note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
    if (note is { Length: > 500 })
        return Results.BadRequest(new { success = false, error = "Ghi chú tối đa 500 ký tự." });

    var denied = await CheckAlertPermissionAsync(alertId, user, db);
    if (denied != null) return denied;

    if (!await db.ResolveAlertAsync(alertId, CurrentUserId(user), req.Resolution, note))
        return Results.Json(new { success = false, error = $"Không ghi được phiếu sự cố #{alertId} (lỗi CSDL)." },
            statusCode: StatusCodes.Status503ServiceUnavailable);

    broadcaster.Request();
    return Results.Ok(new { success = true });
}).RequireAuthorization();

// 6h. Thêm / sửa khu vực và cán bộ phụ trách (chỉ Admin)
app.MapPost("/api/zones", async (UpsertZoneRequest req, DatabaseService db, DashboardBroadcaster broadcaster) =>
{
    string name = (req.ZoneName ?? "").Trim();
    string? desc = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim();
    if (name.Length is 0 or > 100)
        return Results.BadRequest(new { success = false, error = "Tên khu vực bắt buộc, tối đa 100 ký tự." });
    if (desc is { Length: > 255 })
        return Results.BadRequest(new { success = false, error = "Mô tả tối đa 255 ký tự." });

    var (success, error) = await db.UpsertZoneAsync(req.ZoneId, name, desc, req.ManagerUserId);
    if (!success)
        return Results.BadRequest(new { success, error });

    broadcaster.Request();
    return Results.Ok(new { success = true });
}).RequireAuthorization("Admin");

// 6i. Xoá khu vực (chỉ Admin, chỉ khi khu vực không còn trạm)
app.MapDelete("/api/zones/{zoneId:int}", async (int zoneId, DatabaseService db, DashboardBroadcaster broadcaster) =>
{
    var (success, error) = await db.DeleteZoneAsync(zoneId);
    if (!success)
        return Results.BadRequest(new { success, error });

    broadcaster.Request();
    return Results.Ok(new { success = true });
}).RequireAuthorization("Admin");

// 7. Đăng ký tài khoản mới (chỉ Admin; chỉ Admin mới được chọn vai trò)
app.MapPost("/api/auth/register", async (
    RegisterRequest req,
    DatabaseService db,
    DashboardBroadcaster broadcaster) =>
{
    if ((req.Password ?? "").Length < 6)
        return Results.BadRequest(new { success = false, user = (object?)null, error = "Mật khẩu phải có ít nhất 6 ký tự." });

    var (success, user, error) = await db.RegisterUserAsync(
        req.Username ?? "", req.Password!, req.FullName ?? "", req.Phone, req.Email, req.Role ?? "User");

    if (!success)
        return Results.BadRequest(new { success, user, error });

    broadcaster.Request();
    return Results.Ok(new { success, user, error });
}).RequireAuthorization("Admin");

// 8. Xóa tài khoản khỏi bảng Users (chỉ Admin, không tự xoá chính mình)
app.MapDelete("/api/users/{userId:int}", async (
    int userId,
    ClaimsPrincipal user,
    DatabaseService db,
    DashboardBroadcaster broadcaster) =>
{
    if (userId == CurrentUserId(user))
        return Results.BadRequest(new { success = false, error = "Không thể xóa tài khoản đang đăng nhập." });

    bool ok = await db.DeleteUserAsync(userId);
    if (!ok)
        return Results.NotFound(new { success = false, error = $"Không xóa được tài khoản #{userId} (không tồn tại hoặc lỗi CSDL)." });

    UserSessionValidator.Forget(userId);
    broadcaster.Request();
    return Results.Ok(new { success = true });
}).RequireAuthorization("Admin");

app.Run();

static int CurrentUserId(ClaimsPrincipal user) =>
    int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out int id) ? id : 0;

// Admin xử lý mọi khu vực; cán bộ thường chỉ xử lý khu vực mình phụ trách hoặc khu vực chưa có người phụ trách
static bool CanHandleZone(ClaimsPrincipal user, int? managerUserId) =>
    user.IsInRole("Admin") || managerUserId is null || managerUserId == CurrentUserId(user);

// null = được phép; khác null = phản hồi lỗi trả thẳng cho trình duyệt
static async Task<IResult?> CheckAlertPermissionAsync(long alertId, ClaimsPrincipal user, DatabaseService db)
{
    try
    {
        var (exists, managerUserId) = await db.GetAlertManagerAsync(alertId);
        if (!exists)
            return Results.NotFound(new { success = false, error = $"Không tìm thấy cảnh báo #{alertId}." });
        if (!CanHandleZone(user, managerUserId))
            return Results.Json(new { success = false, error = "Chỉ cán bộ phụ trách khu vực này hoặc quản trị viên mới được xử lý cảnh báo." },
                statusCode: StatusCodes.Status403Forbidden);
        return null;
    }
    catch (Exception ex)
    {
        return Results.Json(new { success = false, error = "Lỗi CSDL khi kiểm tra quyền: " + ex.Message },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}

static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

static object CurrentUser(ClaimsPrincipal user) => new
{
    userId = CurrentUserId(user),
    username = user.FindFirstValue(ClaimTypes.Name),
    fullName = user.FindFirstValue(ClaimTypes.GivenName),
    role = user.FindFirstValue(ClaimTypes.Role),
};

static Task WriteJsonError(HttpResponse response, int statusCode, string message)
{
    response.StatusCode = statusCode;
    return response.WriteAsJsonAsync(new { success = false, error = message });
}

// Request DTOs
public sealed record AcknowledgeRequest(long AlertId);
public sealed record LoginRequest(string? Username, string? Password);
public sealed record UpsertDeviceRequest(
    string? DeviceId,
    string? DeviceName,
    string? ZoneName,
    string? DeviceType,
    string? Status,
    string? IpAddress,
    bool IsEdit = false);
public sealed record ResolveAlertRequest(string? Resolution, string? Note);
public sealed record UpsertZoneRequest(int? ZoneId, string? ZoneName, string? Description, int? ManagerUserId);
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
public sealed record RegisterRequest(
    string? Username,
    string? Password,
    string? FullName,
    string? Phone,
    string? Email,
    string? Role = "User");
