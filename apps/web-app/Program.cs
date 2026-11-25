using Microsoft.AspNetCore.SignalR;
using SmartFireWeb.Hubs;
using SmartFireApp.Services;
using SmartFireWeb.Services;

var builder = WebApplication.CreateBuilder(args);

// Cho phép truy cập từ localhost và các thiết bị trong mạng LAN (điện thoại, laptop khác)
builder.WebHost.UseUrls("http://0.0.0.0:5000");

// Cùng file cấu hình với desktop-app và mqtt-subscriber: appsettings.json (+ appsettings.Local.json, biến môi trường)
var settings = AppSettings.Load(builder.Environment.ContentRootPath);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(new DatabaseService(settings.ConnectionString));
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
app.UseDefaultFiles(); // Tự động phục vụ wwwroot/index.html tại "/"
app.UseStaticFiles();

// SignalR Hub cho real-time
app.MapHub<FireAlertHub>("/hubs/firealert");

// ============================================================
// REST API ENDPOINTS (Thay thế cho WebView2 postMessage)
// ============================================================

// 1. Lấy toàn bộ dữ liệu thực từ 5 bảng MySQL
app.MapGet("/api/dashboard", async (DatabaseService db) =>
{
    var data = await db.FetchRealDataAsync();
    return Results.Ok(data);
});

// 2. Xác nhận đã xử lý sự cố cháy (cập nhật AlertHistory)
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

// 3. Đăng nhập tài khoản (bảng Users)
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

// 4. Đăng ký tài khoản mới vào bảng Users
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

// 5. Xóa tài khoản khỏi bảng Users
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

// Request DTOs
public sealed record AcknowledgeRequest(long AlertId, int UserId = 1);
public sealed record LoginRequest(string Username, string Password);
public sealed record RegisterRequest(
    string Username,
    string Password,
    string FullName,
    string? Phone,
    string? Email,
    string? Role = "User");
