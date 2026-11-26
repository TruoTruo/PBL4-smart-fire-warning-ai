using Microsoft.AspNetCore.SignalR;
using SmartFireWeb.Hubs;
using SmartFireWeb.Services;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://0.0.0.0:5000");

builder.Services.AddSingleton<DatabaseService>();
builder.Services.AddSingleton<MqttAlertSubscriber>();
builder.Services.AddHostedService<MqttHostedService>();
builder.Services.AddSignalR();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHub<FireAlertHub>("/hubs/firealert");

// 1. Lấy toàn bộ dữ liệu thực từ MySQL
app.MapGet("/api/dashboard", async (DatabaseService db) =>
{
    var data = await db.FetchRealDataAsync();
    return Results.Ok(data);
});

// 2. Xác nhận đã xử lý sự cố cháy
app.MapPost("/api/alerts/acknowledge", async (
    AcknowledgeRequest req,
    DatabaseService db,
    IHubContext<FireAlertHub> hub) =>
{
    bool ok = await db.AcknowledgeAlertAsync(req.AlertId, req.UserId > 0 ? req.UserId : 1);
    if (ok)
    {
        var data = await db.FetchRealDataAsync();
        await hub.Clients.All.SendAsync("DashboardUpdated", data);
    }
    return Results.Ok(new { success = ok });
});

// 3. Lọc lịch sử & thống kê cảnh báo theo ngày
app.MapGet("/api/alerts/history", async (
    DateTime? fromDate,
    DateTime? toDate,
    string? alertType,
    DatabaseService db) =>
{
    var start = fromDate?.Date ?? DateTime.Today.AddDays(-7);
    var end = toDate?.Date.AddDays(1).AddSeconds(-1) ?? DateTime.Today.AddDays(1).AddSeconds(-1);
    var alerts = await db.GetAlertsByDateRangeAsync(start, end, alertType);
    var stats = await db.GetAlertStatsAsync(start, end);
    return Results.Ok(new
    {
        alerts,
        stats,
        fromDate = start.ToString("yyyy-MM-dd"),
        toDate = end.ToString("yyyy-MM-dd")
    });
});

// 4. Thêm mới hoặc cập nhật trạm giám sát (Devices)
app.MapPost("/api/devices", async (
    UpsertDeviceRequest req,
    DatabaseService db,
    IHubContext<FireAlertHub> hub) =>
{
    var (success, error) = await db.UpsertDeviceAsync(
        req.DeviceId, req.DeviceName, req.ZoneName, req.DeviceType, req.Status, req.IpAddress, req.IsEdit);
    if (success)
    {
        var data = await db.FetchRealDataAsync();
        await hub.Clients.All.SendAsync("DashboardUpdated", data);
    }
    return Results.Ok(new { success, error });
});

// 5. Xóa trạm giám sát
app.MapDelete("/api/devices/{deviceId}", async (
    string deviceId,
    DatabaseService db,
    IHubContext<FireAlertHub> hub) =>
{
    var (success, error) = await db.DeleteDeviceAsync(deviceId);
    if (success)
    {
        var data = await db.FetchRealDataAsync();
        await hub.Clients.All.SendAsync("DashboardUpdated", data);
    }
    return Results.Ok(new { success, error });
});

// 6. Đăng nhập tài khoản (bảng Users)
app.MapPost("/api/auth/login", async (LoginRequest req, DatabaseService db) =>
{
    var user = await db.LoginUserAsync(req.Username, req.Password);
    return Results.Ok(new
    {
        success = user != null,
        user,
        error = user == null ? "Tên đăng nhập hoặc mật khẩu không đúng." : null
    });
});

// 7. Đăng ký tài khoản mới vào bảng Users
app.MapPost("/api/auth/register", async (
    RegisterRequest req,
    DatabaseService db,
    IHubContext<FireAlertHub> hub) =>
{
    var (success, user, error) = await db.RegisterUserAsync(
        req.Username, req.Password, req.FullName, req.Phone, req.Email, req.Role ?? "User");

    if (success)
    {
        var data = await db.FetchRealDataAsync();
        await hub.Clients.All.SendAsync("DashboardUpdated", data);
    }

    return Results.Ok(new { success, user, error });
});

// 8. Xóa tài khoản khỏi bảng Users
app.MapDelete("/api/users/{userId:int}", async (
    int userId,
    DatabaseService db,
    IHubContext<FireAlertHub> hub) =>
{
    bool ok = await db.DeleteUserAsync(userId);
    if (ok)
    {
        var data = await db.FetchRealDataAsync();
        await hub.Clients.All.SendAsync("DashboardUpdated", data);
    }
    return Results.Ok(new { success = ok });
});

app.Run();

public sealed record AcknowledgeRequest(long AlertId, int UserId = 1);
public sealed record UpsertDeviceRequest(
    string DeviceId,
    string DeviceName,
    string ZoneName,
    string DeviceType,
    string Status,
    string? IpAddress,
    bool IsEdit);
public sealed record LoginRequest(string Username, string Password);
public sealed record RegisterRequest(
    string Username,
    string Password,
    string FullName,
    string? Phone,
    string? Email,
    string? Role = "User");
