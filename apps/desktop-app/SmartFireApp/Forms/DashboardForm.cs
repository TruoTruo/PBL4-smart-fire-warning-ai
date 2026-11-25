using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SmartFireApp.Models;
using SmartFireApp.Services;

namespace SmartFireApp.Forms;

/// <summary>
/// DashboardForm tích hợp WebView2 (Chromium Engine) - 100% dữ liệu thực từ MySQL (SmartFireDB)
/// và nhận trực tiếp telemetry từ Raspberry Pi 5. Hoàn toàn không dùng bất kỳ dữ liệu mẫu nào.
/// </summary>
public sealed class DashboardForm : Form
{
    private readonly WebView2 _webView = new();
    private readonly MqttAlertSubscriber _mqtt = new();
    private readonly DatabaseService _db = new();
    private readonly System.Windows.Forms.Timer _syncTimer = new() { Interval = 3000 };

    public DashboardForm()
    {
        Text = "FireGuard IoT — Giám Sát Cảnh Báo Cháy Thông Minh";
        BackColor = Color.FromArgb(0x0F, 0x13, 0x1C);
        ForeColor = Color.FromArgb(0xDF, 0xE2, 0xEF);
        MinimumSize = new Size(1280, 800);
        WindowState = FormWindowState.Maximized;
        Icon = SystemIcons.Shield;

        _webView.Dock = DockStyle.Fill;
        Controls.Add(_webView);
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        try
        {
            await _webView.EnsureCoreWebView2Async();
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;

            // Tự động cấp quyền truy cập Camera cho WebView2 khi bật webcam
            _webView.CoreWebView2.PermissionRequested += (sender, args) =>
            {
                if (args.PermissionKind == Microsoft.Web.WebView2.Core.CoreWebView2PermissionKind.Camera)
                {
                    args.State = Microsoft.Web.WebView2.Core.CoreWebView2PermissionState.Allow;
                }
            };

            // Lắng nghe tương tác từ nút bấm trên trang Web (ví dụ: bấm nút Xác nhận xử lý sự cố)
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            string appDir = AppContext.BaseDirectory;
            string wwwrootDir = Path.Combine(appDir, "wwwroot", "dashboard.html");
            string sourceDir = Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", "dashboard.html"));

            string targetHtmlPath = File.Exists(wwwrootDir) ? wwwrootDir : sourceDir;

            if (File.Exists(targetHtmlPath))
            {
                _webView.CoreWebView2.Navigate(new Uri(targetHtmlPath).AbsoluteUri);
            }
            else
            {
                _webView.CoreWebView2.NavigateToString(
                    "<h2 style='color:white;font-family:sans-serif;padding:2rem;'>Đang tìm file dashboard.html...</h2>");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Lỗi khởi tạo WebView2: {ex.Message}", "FireGuard IoT", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        // Khi WebView2 nạp xong HTML thì đẩy ngay dữ liệu thật từ CSDL vào
        _webView.NavigationCompleted += async (_, _) => await SyncRealDataAsync();

        // Quét CSDL mỗi 3 giây: đây là nguồn dữ liệu chính của giao diện
        _syncTimer.Tick += async (_, _) => await SyncRealDataAsync();
        _syncTimer.Start();

        // MQTT chỉ đóng vai "chuông báo": có tin mới từ Pi/subscriber thì đọc lại CSDL ngay.
        // Chạy nền để không chặn giao diện (nếu broker không kết nối được sẽ mất vài giây chờ).
        _mqtt.SensorReadingReceived += (_, _) => RequestSync();
        _mqtt.AlertReceived += (_, _) => RequestSync();
        _ = StartMqttAsync();
    }

    private async Task StartMqttAsync()
    {
        try
        {
            await _mqtt.StartAsync("192.168.137.191", 1883);   // cùng broker với Pi và mqtt-subscriber
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MQTT Init Error] {ex.Message}");
        }
    }

    /// <summary>Sự kiện MQTT chạy trên thread nền, còn WebView2 chỉ dùng được trên thread giao diện.</summary>
    private void RequestSync()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(new Func<Task>(SyncRealDataAsync)); }
        catch (InvalidOperationException) { /* form đang đóng */ }
    }

    private bool _syncing;   // chặn nhiều lần đồng bộ chồng lên nhau

    private async Task SyncRealDataAsync()
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            if (_webView.CoreWebView2 == null) return;

            var data = await _db.FetchRealDataAsync();

            string json = JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            string script = $"if (window.renderRealDatabaseData) window.renderRealDatabaseData({json});";
            await _webView.ExecuteScriptAsync(script);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SyncRealData Error] {ex.Message}");
        }
        finally
        {
            _syncing = false;
        }
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            string messageJson = e.WebMessageAsJson;
            using var doc = JsonDocument.Parse(messageJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("action", out var actProp))
            {
                string action = actProp.GetString() ?? "";

                // Người dùng click "Xác nhận xử lý" trên Web View
                if (action == "acknowledgeAlert" && root.TryGetProperty("alertId", out var idProp))
                {
                    long alertId = idProp.GetInt64();
                    int userId = root.TryGetProperty("userId", out var uIdProp) ? uIdProp.GetInt32() : 1;
                    await _db.AcknowledgeAlertAsync(alertId, userId);
                    await SyncRealDataAsync();
                }
                else if (action == "refreshDatabase")
                {
                    await SyncRealDataAsync();
                }
                else if (action == "queryHistoryAndStats")
                {
                    string? fromStr = root.TryGetProperty("fromDate", out var fProp) ? fProp.GetString() : null;
                    string? toStr = root.TryGetProperty("toDate", out var tProp) ? tProp.GetString() : null;
                    string? alertType = root.TryGetProperty("alertType", out var typeProp) ? typeProp.GetString() : null;

                    DateTime fromDate = DateTime.TryParse(fromStr, out var fParsed)
                        ? fParsed.Date
                        : DateTime.Today.AddDays(-30);
                    DateTime toDate = DateTime.TryParse(toStr, out var tParsed)
                        ? tParsed.Date.AddDays(1).AddSeconds(-1)
                        : DateTime.Today.AddDays(1).AddSeconds(-1);

                    var filteredAlerts = await _db.GetAlertsByDateRangeAsync(fromDate, toDate, alertType);
                    var stats = await _db.GetAlertStatsAsync(fromDate, toDate);

                    var resultObj = new
                    {
                        alerts = filteredAlerts,
                        stats = stats,
                        fromDate = fromDate.ToString("yyyy-MM-dd"),
                        toDate = toDate.ToString("yyyy-MM-dd")
                    };

                    string resJson = JsonSerializer.Serialize(resultObj, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    });

                    await _webView.ExecuteScriptAsync($"if (window.renderHistoryAndStats) window.renderHistoryAndStats({resJson});");
                }
                else if (action == "login")
                {
                    string username = root.TryGetProperty("username", out var uProp) ? uProp.GetString() ?? "" : "";
                    string password = root.TryGetProperty("password", out var pProp) ? pProp.GetString() ?? "" : "";

                    var user = await _db.LoginUserAsync(username, password);
                    var response = new
                    {
                        success = user != null,
                        user = user,
                        error = user == null ? "Tên đăng nhập hoặc mật khẩu không đúng." : null
                    };

                    string resJson = JsonSerializer.Serialize(response, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    });
                    await _webView.ExecuteScriptAsync($"if (window.handleAuthResponse) window.handleAuthResponse('login', {resJson});");
                    if (user != null) await SyncRealDataAsync();
                }
                else if (action == "register")
                {
                    string username = root.TryGetProperty("username", out var uProp) ? uProp.GetString() ?? "" : "";
                    string password = root.TryGetProperty("password", out var pProp) ? pProp.GetString() ?? "" : "";
                    string fullName = root.TryGetProperty("fullName", out var fnProp) ? fnProp.GetString() ?? "" : "";
                    string? phone = root.TryGetProperty("phone", out var phProp) ? phProp.GetString() : null;
                    string? email = root.TryGetProperty("email", out var emProp) ? emProp.GetString() : null;
                    string role = root.TryGetProperty("role", out var roProp) ? roProp.GetString() ?? "User" : "User";

                    var (success, user, error) = await _db.RegisterUserAsync(username, password, fullName, phone, email, role);
                    var response = new { success, user, error };

                    string resJson = JsonSerializer.Serialize(response, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    });
                    await _webView.ExecuteScriptAsync($"if (window.handleAuthResponse) window.handleAuthResponse('register', {resJson});");
                    if (success) await SyncRealDataAsync();
                }
                else if (action == "deleteUser" && root.TryGetProperty("userId", out var delIdProp))
                {
                    int delUserId = delIdProp.GetInt32();
                    await _db.DeleteUserAsync(delUserId);
                    await SyncRealDataAsync();
                }
                else if (action == "upsertDevice")
                {
                    string deviceId = root.TryGetProperty("deviceId", out var dId) ? dId.GetString() ?? "" : "";
                    string deviceName = root.TryGetProperty("deviceName", out var dName) ? dName.GetString() ?? "" : "";
                    string zoneName = root.TryGetProperty("zoneName", out var zName) ? zName.GetString() ?? "" : "";
                    string deviceType = root.TryGetProperty("deviceType", out var dType) ? dType.GetString() ?? "RaspberryPi-Node" : "RaspberryPi-Node";
                    string status = root.TryGetProperty("status", out var st) ? st.GetString() ?? "Active" : "Active";
                    string? ipAddress = root.TryGetProperty("ipAddress", out var ip) ? ip.GetString() : null;
                    bool isEdit = root.TryGetProperty("isEdit", out var ed) && ed.GetBoolean();

                    var (success, error) = await _db.UpsertDeviceAsync(deviceId, deviceName, zoneName, deviceType, status, ipAddress, isEdit);
                    string resJson = JsonSerializer.Serialize(new { success, error }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                    await _webView.ExecuteScriptAsync($"if (window.handleDeviceActionResult) window.handleDeviceActionResult({resJson});");
                    if (success) await SyncRealDataAsync();
                }
                else if (action == "deleteDevice" && root.TryGetProperty("deviceId", out var delDevProp))
                {
                    string delDevId = delDevProp.GetString() ?? "";
                    var (success, error) = await _db.DeleteDeviceAsync(delDevId);
                    string resJson = JsonSerializer.Serialize(new { success, error }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                    await _webView.ExecuteScriptAsync($"if (window.handleDeviceActionResult) window.handleDeviceActionResult({resJson});");
                    if (success) await SyncRealDataAsync();
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebMessage Error] {ex.Message}");
        }
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        _syncTimer.Stop();
        _syncTimer.Dispose();
        await _mqtt.DisposeAsync();
    }
}