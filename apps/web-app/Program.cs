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
    string ip = http.Connection.RemoteIpAddress?.ToString() ?? "";
    if (user == null)
    {
        await db.LogActivityAsync(null, (req.Username ?? "").Trim(), "login.failed", "user", null,
            "Đăng nhập sai tên hoặc mật khẩu", ip);
        return Results.Json(new { success = false, error = "Tên đăng nhập hoặc mật khẩu không đúng." },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    var identity = new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, user.UserId.ToString()),
        new Claim(ClaimTypes.Name, user.Username),
        new Claim(ClaimTypes.GivenName, user.FullName),
        new Claim(ClaimTypes.Role, user.Role),
        // thời điểm đăng nhập chính xác (cookie chỉ lưu IssuedUtc tới giây), dùng để huỷ phiên khi đặt lại mật khẩu
        new Claim(UserSessionValidator.LoginTicksClaim, DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)),
    ], CookieAuthenticationDefaults.AuthenticationScheme);

    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
        new AuthenticationProperties { IsPersistent = false });
    await db.LogActivityAsync(user.UserId, user.FullName, "login", "user", user.UserId.ToString(), null, ip);

    return Results.Ok(new { success = true, user = new { user.UserId, user.Username, user.FullName, user.Role } });
}).RequireRateLimiting("login");

// 2. Đăng xuất: xoá cookie
app.MapPost("/api/auth/logout", async (HttpContext http) =>
{
    if (http.User.Identity?.IsAuthenticated == true)
        await Audit(http, "logout", "user", CurrentUserId(http.User).ToString());
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok(new { success = true });
});

// 3. Người đang đăng nhập (trang gọi khi mở lại để biết còn phiên hay không)
app.MapGet("/api/auth/me", (ClaimsPrincipal user) =>
    Results.Ok(new { success = true, user = CurrentUser(user) }))
    .RequireAuthorization();

// 3b. Đổi mật khẩu của chính mình (tài khoản demo phải đổi sau lần đăng nhập đầu)
app.MapPost("/api/auth/change-password", async (ChangePasswordRequest req, ClaimsPrincipal user, DatabaseService db, HttpContext http) =>
{
    var (success, error) = await db.ChangePasswordAsync(CurrentUserId(user), req.CurrentPassword ?? "", req.NewPassword ?? "");
    if (success) await Audit(http, "password.change", "user", CurrentUserId(user).ToString());
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
    DashboardBroadcaster broadcaster,
    HttpContext http) =>
{
    if (req.AlertId <= 0)
        return Results.BadRequest(new { success = false, error = "Mã cảnh báo không hợp lệ." });

    var denied = await CheckAlertPermissionAsync(req.AlertId, user, db, http);
    if (denied != null) return denied;

    bool ok = await db.AcknowledgeAlertAsync(req.AlertId, CurrentUserId(user));
    if (!ok)
        return Results.Json(new { success = false, error = $"Không xác nhận được sự cố #{req.AlertId} (không tồn tại hoặc lỗi CSDL)." },
            statusCode: StatusCodes.Status409Conflict);

    await Audit(http, "alert.ack", "alert", req.AlertId.ToString(), (string?)http.Items["escalated"]);
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

// 6b2. Báo cáo hiệu quả xử lý trong khoảng ngày (mặc định 7 ngày gần nhất)
app.MapGet("/api/alerts/report", async (DateOnly? fromDate, DateOnly? toDate, DatabaseService db) =>
{
    var today = DateOnly.FromDateTime(DateTime.Now);
    var from = fromDate ?? today.AddDays(-6);
    var to = toDate ?? today;
    if (to < from) (from, to) = (to, from);
    if (to.DayNumber - from.DayNumber > 366)
        return Results.BadRequest(new { success = false, error = "Khoảng thời gian tối đa 1 năm." });
    try
    {
        var (summary, byZone, byUser) = await db.GetAlertReportAsync(
            from.ToDateTime(TimeOnly.MinValue), to.ToDateTime(TimeOnly.MaxValue));
        var byDevice = await db.GetDeviceAlertStatsAsync(
            from.ToDateTime(TimeOnly.MinValue), to.ToDateTime(TimeOnly.MaxValue));
        return Results.Ok(new
        {
            success = true,
            fromDate = from.ToString("yyyy-MM-dd"),
            toDate = to.ToString("yyyy-MM-dd"),
            summary,
            byZone,
            byUser,
            byDevice,
        });
    }
    catch (Exception ex)
    {
        return Results.Json(new { success = false, error = "Lỗi CSDL khi lập báo cáo: " + ex.Message },
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
app.MapPost("/api/devices", async (UpsertDeviceRequest req, DatabaseService db, DashboardBroadcaster broadcaster, HttpContext http) =>
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

    await Audit(http, req.IsEdit ? "device.update" : "device.create", "device", deviceId,
        $"{deviceName} · {zoneName} · {req.Status}");
    broadcaster.Request();
    return Results.Ok(new { success = true });
}).RequireAuthorization("Admin");

// 6e. Xoá trạm giám sát (chỉ Admin; xoá kèm nhật ký và cảnh báo của trạm)
app.MapDelete("/api/devices/{deviceId}", async (string deviceId, DatabaseService db, DashboardBroadcaster broadcaster, HttpContext http) =>
{
    var (success, error) = await db.DeleteDeviceAsync(deviceId);
    if (!success)
        return Results.Json(new { success, error }, statusCode: error?.StartsWith("Không tìm thấy") == true
            ? StatusCodes.Status404NotFound : StatusCodes.Status503ServiceUnavailable);

    await Audit(http, "device.delete", "device", deviceId);
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
        bool overdue = AlertPolicy.IsOverdue(incident.Severity, incident.AlertTime, incident.Acknowledged);
        bool canHandle = overdue || CanHandleZone(user, incident.ManagerUserId);
        return Results.Ok(new { success = true, incident, canHandle, overdue });
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
    DashboardBroadcaster broadcaster,
    HttpContext http) =>
{
    if (req.Resolution is not ("Handled" or "FalseAlarm" or "Test"))
        return Results.BadRequest(new { success = false, error = "Kết quả xử lý chỉ được là Đã xử lý, Báo nhầm hoặc Diễn tập." });
    string? note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
    if (note is { Length: > 500 })
        return Results.BadRequest(new { success = false, error = "Ghi chú tối đa 500 ký tự." });

    var denied = await CheckAlertPermissionAsync(alertId, user, db, http);
    if (denied != null) return denied;

    if (!await db.ResolveAlertAsync(alertId, CurrentUserId(user), req.Resolution, note))
        return Results.Json(new { success = false, error = $"Không ghi được phiếu sự cố #{alertId} (lỗi CSDL)." },
            statusCode: StatusCodes.Status503ServiceUnavailable);

    await Audit(http, "alert.resolve", "alert", alertId.ToString(),
        req.Resolution + (note != null ? " · " + note : "") + (http.Items["escalated"] is string esc ? " · " + esc : ""));
    broadcaster.Request();
    return Results.Ok(new { success = true });
}).RequireAuthorization();

// 6h. Thêm / sửa khu vực và cán bộ phụ trách (chỉ Admin)
app.MapPost("/api/zones", async (UpsertZoneRequest req, DatabaseService db, DashboardBroadcaster broadcaster, HttpContext http) =>
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

    await Audit(http, req.ZoneId is null ? "zone.create" : "zone.update", "zone", req.ZoneId?.ToString(),
        name + (req.ManagerUserId is int mgr ? $" · phụ trách #{mgr}" : " · chưa có người phụ trách"));
    broadcaster.Request();
    return Results.Ok(new { success = true });
}).RequireAuthorization("Admin");

// 6i. Xoá khu vực (chỉ Admin, chỉ khi khu vực không còn trạm)
app.MapDelete("/api/zones/{zoneId:int}", async (int zoneId, DatabaseService db, DashboardBroadcaster broadcaster, HttpContext http) =>
{
    var (success, error) = await db.DeleteZoneAsync(zoneId);
    if (!success)
        return Results.BadRequest(new { success, error });

    await Audit(http, "zone.delete", "zone", zoneId.ToString());
    broadcaster.Request();
    return Results.Ok(new { success = true });
}).RequireAuthorization("Admin");

// 7. Đăng ký tài khoản mới (chỉ Admin; chỉ Admin mới được chọn vai trò)
app.MapPost("/api/auth/register", async (
    RegisterRequest req,
    DatabaseService db,
    DashboardBroadcaster broadcaster,
    HttpContext http) =>
{
    if ((req.Password ?? "").Length < 6)
        return Results.BadRequest(new { success = false, user = (object?)null, error = "Mật khẩu phải có ít nhất 6 ký tự." });

    var (success, user, error) = await db.RegisterUserAsync(
        req.Username ?? "", req.Password!, req.FullName ?? "", req.Phone, req.Email, req.Role ?? "User");

    if (!success)
        return Results.BadRequest(new { success, user, error });

    await Audit(http, "user.create", "user", null, $"{(req.Username ?? "").Trim()} · {req.Role ?? "User"}");
    broadcaster.Request();
    return Results.Ok(new { success, user, error });
}).RequireAuthorization("Admin");

// 7b. Sửa thông tin cán bộ (chỉ Admin; không tự đổi vai trò của mình để không mất quyền quản trị)
app.MapPost("/api/users/{userId:int}", async (
    int userId,
    UpdateUserRequest req,
    ClaimsPrincipal user,
    DatabaseService db,
    DashboardBroadcaster broadcaster,
    HttpContext http) =>
{
    string fullName = (req.FullName ?? "").Trim();
    string? phone = string.IsNullOrWhiteSpace(req.Phone) ? null : req.Phone.Trim();
    string? email = string.IsNullOrWhiteSpace(req.Email) ? null : req.Email.Trim();
    string role = req.Role is "Admin" ? "Admin" : req.Role is "User" ? "User" : "";

    string? error = null;
    if (fullName.Length is 0 or > 100)
        error = "Họ tên bắt buộc, tối đa 100 ký tự.";
    else if (phone != null && (phone.Length > 15 || !phone.All(c => char.IsAsciiDigit(c) || c is '+' or ' ' or '.' or '-')))
        error = "Số điện thoại tối đa 15 ký tự, chỉ gồm số, dấu cách, '+', '.', '-'.";
    else if (email != null && (email.Length > 100 || !System.Net.Mail.MailAddress.TryCreate(email, out _)))
        error = "Email không hợp lệ.";
    else if (role.Length == 0)
        error = "Vai trò chỉ được là Admin hoặc User.";
    else if (userId == CurrentUserId(user) && role != "Admin")
        error = "Không thể tự bỏ quyền quản trị của chính mình.";
    if (error != null)
        return Results.BadRequest(new { success = false, error });

    var (success, dbError) = await db.UpdateUserAsync(userId, fullName, phone, email, role);
    if (!success)
        return Results.BadRequest(new { success, error = dbError });

    await Audit(http, "user.update", "user", userId.ToString(), $"{fullName} · {role}");
    UserSessionValidator.Forget(userId);   // đổi vai trò thì phiên cũ của người đó bị kiểm tra lại ngay
    broadcaster.Request();
    return Results.Ok(new { success = true });
}).RequireAuthorization("Admin");

// 7c. Đặt lại mật khẩu cho cán bộ khác (chỉ Admin); mọi phiên đang mở của người đó bị huỷ
app.MapPost("/api/users/{userId:int}/reset-password", async (
    int userId,
    ResetPasswordRequest req,
    ClaimsPrincipal user,
    DatabaseService db,
    HttpContext http) =>
{
    if (userId == CurrentUserId(user))
        return Results.BadRequest(new { success = false, error = "Tự đổi mật khẩu của mình ở mục Đổi mật khẩu." });
    if ((req.NewPassword ?? "").Length < 6)
        return Results.BadRequest(new { success = false, error = "Mật khẩu mới phải có ít nhất 6 ký tự." });

    var (success, error) = await db.ResetPasswordAsync(userId, req.NewPassword!);
    if (!success)
        return Results.BadRequest(new { success, error });

    UserSessionValidator.RevokeSessions(userId);
    await Audit(http, "user.reset_password", "user", userId.ToString());
    return Results.Ok(new { success = true });
}).RequireAuthorization("Admin");

// 8. Xóa tài khoản khỏi bảng Users (chỉ Admin, không tự xoá chính mình)
app.MapDelete("/api/users/{userId:int}", async (
    int userId,
    ClaimsPrincipal user,
    DatabaseService db,
    DashboardBroadcaster broadcaster,
    HttpContext http) =>
{
    if (userId == CurrentUserId(user))
        return Results.BadRequest(new { success = false, error = "Không thể xóa tài khoản đang đăng nhập." });

    bool ok = await db.DeleteUserAsync(userId);
    if (!ok)
        return Results.NotFound(new { success = false, error = $"Không xóa được tài khoản #{userId} (không tồn tại hoặc lỗi CSDL)." });

    await Audit(http, "user.delete", "user", userId.ToString());
    UserSessionValidator.Forget(userId);
    broadcaster.Request();
    return Results.Ok(new { success = true });
}).RequireAuthorization("Admin");

// 9. Nhật ký hoạt động (chỉ Admin), mặc định 7 ngày gần nhất, lọc theo cán bộ / nhóm thao tác
app.MapGet("/api/activity", async (DateOnly? fromDate, DateOnly? toDate, int? userId, string? action, DatabaseService db) =>
{
    var today = DateOnly.FromDateTime(DateTime.Now);
    var from = fromDate ?? today.AddDays(-6);
    var to = toDate ?? today;
    if (to < from) (from, to) = (to, from);
    if (to.DayNumber - from.DayNumber > 366)
        return Results.BadRequest(new { success = false, error = "Khoảng thời gian tối đa 1 năm." });
    try
    {
        var items = await db.GetActivityAsync(from.ToDateTime(TimeOnly.MinValue), to.ToDateTime(TimeOnly.MaxValue),
            userId, NullIfEmpty(action));
        return Results.Ok(new
        {
            success = true,
            fromDate = from.ToString("yyyy-MM-dd"),
            toDate = to.ToString("yyyy-MM-dd"),
            items,
            limited = items.Count >= 500,
        });
    }
    catch (Exception ex)
    {
        return Results.Json(new { success = false, error = "Lỗi CSDL khi đọc nhật ký: " + ex.Message },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).RequireAuthorization("Admin");

// 10. Sơ đồ mặt bằng: ảnh của khu vực (mọi cán bộ xem, Admin tải lên / xoá)
app.MapGet("/api/zones/{zoneId:int}/floorplan", async (int zoneId, DatabaseService db, HttpContext http) =>
{
    string? dataUrl;
    try
    {
        dataUrl = await db.GetFloorPlanAsync(zoneId);
    }
    catch (Exception ex)
    {
        return Results.Json(new { success = false, error = "Lỗi CSDL khi đọc sơ đồ: " + ex.Message },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    var image = dataUrl == null ? null : FloorPlanImage.Parse(dataUrl);
    if (image == null)
        return Results.Json(new { success = false, error = "Khu vực chưa có sơ đồ." }, statusCode: StatusCodes.Status404NotFound);
    http.Response.Headers.CacheControl = "private, no-cache";
    return Results.File(image.Value.Bytes, image.Value.ContentType);
}).RequireAuthorization();

app.MapPost("/api/zones/{zoneId:int}/floorplan", async (
    int zoneId, FloorPlanRequest req, DatabaseService db, DashboardBroadcaster broadcaster, HttpContext http) =>
{
    var image = FloorPlanImage.Parse(req.ImageBase64 ?? "");
    if (image == null)
        return Results.BadRequest(new { success = false, error = "Ảnh sơ đồ phải là PNG, JPEG hoặc WebP." });
    if (image.Value.Bytes.Length > FloorPlanImage.MaxBytes)
        return Results.BadRequest(new { success = false, error = $"Ảnh sơ đồ tối đa {FloorPlanImage.MaxBytes / 1024 / 1024} MB." });

    var (success, error) = await db.SetFloorPlanAsync(zoneId, image.Value.ToDataUrl());
    if (!success)
        return Results.BadRequest(new { success, error });

    await Audit(http, "zone.floorplan", "zone", zoneId.ToString(), $"Tải sơ đồ {Math.Max(1, image.Value.Bytes.Length / 1024)} KB, {image.Value.ContentType}");
    broadcaster.Request();
    return Results.Ok(new { success = true });
}).RequireAuthorization("Admin");

app.MapDelete("/api/zones/{zoneId:int}/floorplan", async (int zoneId, DatabaseService db, DashboardBroadcaster broadcaster, HttpContext http) =>
{
    var (success, error) = await db.SetFloorPlanAsync(zoneId, null);
    if (!success)
        return Results.BadRequest(new { success, error });

    await Audit(http, "zone.floorplan", "zone", zoneId.ToString(), "Xoá sơ đồ");
    broadcaster.Request();
    return Results.Ok(new { success = true });
}).RequireAuthorization("Admin");

// 10b. Vị trí trạm trên sơ đồ (% chiều rộng / cao ảnh); x, y null = gỡ trạm khỏi sơ đồ (chỉ Admin)
app.MapPost("/api/devices/{deviceId}/position", async (
    string deviceId, DevicePositionRequest req, DatabaseService db, DashboardBroadcaster broadcaster, HttpContext http) =>
{
    if ((req.X is null) != (req.Y is null)
        || req.X is < 0 or > 100 || req.Y is < 0 or > 100)
        return Results.BadRequest(new { success = false, error = "Vị trí phải nằm trong ảnh (0–100%)." });

    var (success, error) = await db.SetDevicePositionAsync(deviceId, req.X, req.Y);
    if (!success)
        return Results.BadRequest(new { success, error });

    await Audit(http, "device.position", "device", deviceId,
        req.X is double x ? $"Đặt tại {x:0.#}% · {req.Y:0.#}%" : "Gỡ khỏi sơ đồ");
    broadcaster.Request();
    return Results.Ok(new { success = true });
}).RequireAuthorization("Admin");

// 11. Bảo trì thiết bị: mọi cán bộ xem và ghi lần kiểm tra; Admin xoá dòng và đặt chu kỳ
app.MapGet("/api/devices/{deviceId}/maintenance", async (string deviceId, DatabaseService db) =>
{
    try
    {
        return Results.Ok(new { success = true, deviceId, items = await db.GetMaintenanceAsync(deviceId) });
    }
    catch (Exception ex)
    {
        return Results.Json(new { success = false, error = "Lỗi CSDL khi đọc lịch sử bảo trì: " + ex.Message },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).RequireAuthorization();

app.MapPost("/api/devices/{deviceId}/maintenance", async (
    string deviceId, AddMaintenanceRequest req, ClaimsPrincipal user, DatabaseService db,
    DashboardBroadcaster broadcaster, HttpContext http) =>
{
    var today = DateOnly.FromDateTime(DateTime.Now);
    var at = req.PerformedAt ?? today;
    string? note = NullIfEmpty(req.Note);
    if (req.Kind is not ("Inspection" or "Clean" or "Repair" or "Replace"))
        return Results.BadRequest(new { success = false, error = "Loại công việc chỉ được là Kiểm tra, Vệ sinh, Sửa chữa hoặc Thay thế." });
    if (at > today || at < today.AddYears(-5))
        return Results.BadRequest(new { success = false, error = "Ngày thực hiện không được sau hôm nay hoặc quá 5 năm trước." });
    if (note is { Length: > 500 })
        return Results.BadRequest(new { success = false, error = "Ghi chú tối đa 500 ký tự." });

    var (success, error) = await db.AddMaintenanceAsync(deviceId, CurrentUserId(user), at.ToDateTime(TimeOnly.MinValue), req.Kind, note);
    if (!success)
        return Results.BadRequest(new { success, error });

    await Audit(http, "maintenance.add", "device", deviceId, $"{req.Kind switch { "Inspection" => "Kiểm tra định kỳ", "Clean" => "Vệ sinh", "Repair" => "Sửa chữa", _ => "Thay thế" }} ngày {at:dd/MM/yyyy}" + (note != null ? " · " + note : ""));
    broadcaster.Request();
    return Results.Ok(new { success = true });
}).RequireAuthorization();

app.MapDelete("/api/maintenance/{maintenanceId:int}", async (
    int maintenanceId, DatabaseService db, DashboardBroadcaster broadcaster, HttpContext http) =>
{
    try
    {
        string? deviceId = await db.DeleteMaintenanceAsync(maintenanceId);
        if (deviceId == null)
            return Results.NotFound(new { success = false, error = $"Không tìm thấy lần bảo trì #{maintenanceId}." });
        await Audit(http, "maintenance.delete", "device", deviceId, $"Xoá lần bảo trì #{maintenanceId}");
        broadcaster.Request();
        return Results.Ok(new { success = true, deviceId });
    }
    catch (Exception ex)
    {
        return Results.Json(new { success = false, error = "Lỗi CSDL khi xoá: " + ex.Message },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).RequireAuthorization("Admin");

app.MapPost("/api/devices/{deviceId}/maintenance-settings", async (
    string deviceId, MaintenanceSettingsRequest req, DatabaseService db, DashboardBroadcaster broadcaster, HttpContext http) =>
{
    var today = DateOnly.FromDateTime(DateTime.Now);
    if (req.InstallDate is not DateOnly install || install > today || install < today.AddYears(-30))
        return Results.BadRequest(new { success = false, error = "Ngày lắp đặt không hợp lệ." });
    if (req.IntervalDays is < 7 or > 730)
        return Results.BadRequest(new { success = false, error = "Chu kỳ kiểm tra từ 7 đến 730 ngày." });

    var (success, error) = await db.UpdateMaintenanceSettingsAsync(deviceId, install.ToDateTime(TimeOnly.MinValue), req.IntervalDays);
    if (!success)
        return Results.BadRequest(new { success, error });

    await Audit(http, "device.maintenance_settings", "device", deviceId, $"Lắp đặt {install:dd/MM/yyyy} · chu kỳ {req.IntervalDays} ngày");
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
static async Task<IResult?> CheckAlertPermissionAsync(long alertId, ClaimsPrincipal user, DatabaseService db, HttpContext? http = null)
{
    try
    {
        var (exists, managerUserId, overdue) = await db.GetAlertManagerAsync(alertId);
        if (!exists)
            return Results.NotFound(new { success = false, error = $"Không tìm thấy cảnh báo #{alertId}." });
        if (CanHandleZone(user, managerUserId))
            return null;
        // Leo thang: cảnh báo quá hạn chưa ai xác nhận thì mọi cán bộ đều được xử lý
        if (overdue)
        {
            if (http is not null) http.Items["escalated"] = "Leo thang: xử lý thay khu vực khác vì quá hạn";
            return null;
        }
        return Results.Json(new { success = false, error = "Chỉ cán bộ phụ trách khu vực này hoặc quản trị viên mới được xử lý cảnh báo (trừ khi cảnh báo đã quá hạn)." },
            statusCode: StatusCodes.Status403Forbidden);
    }
    catch (Exception ex)
    {
        return Results.Json(new { success = false, error = "Lỗi CSDL khi kiểm tra quyền: " + ex.Message },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}

// Ghi nhật ký hoạt động cho người đang đăng nhập (tên lấy từ cookie, IP từ kết nối)
static Task Audit(HttpContext http, string action, string? targetType = null, string? targetId = null, string? detail = null)
{
    var db = http.RequestServices.GetRequiredService<DatabaseService>();
    int uid = CurrentUserId(http.User);
    string name = http.User.FindFirstValue(ClaimTypes.GivenName) ?? http.User.FindFirstValue(ClaimTypes.Name) ?? "";
    return db.LogActivityAsync(uid > 0 ? uid : null, name, action, targetType, targetId, detail,
        http.Connection.RemoteIpAddress?.ToString());
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
public sealed record UpdateUserRequest(string? FullName, string? Phone, string? Email, string? Role);
public sealed record ResetPasswordRequest(string? NewPassword);
public sealed record FloorPlanRequest(string? ImageBase64);
public sealed record DevicePositionRequest(double? X, double? Y);
public sealed record AddMaintenanceRequest(DateOnly? PerformedAt, string? Kind, string? Note);
public sealed record MaintenanceSettingsRequest(DateOnly? InstallDate, int IntervalDays);
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
public sealed record RegisterRequest(
    string? Username,
    string? Password,
    string? FullName,
    string? Phone,
    string? Email,
    string? Role = "User");
