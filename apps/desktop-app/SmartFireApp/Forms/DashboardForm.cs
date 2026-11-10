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
            await _mqtt.StartAsync("broker.emqx.io", 1883);   // cùng broker với Pi và mqtt-subscriber
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
                    await _db.AcknowledgeAlertAsync(alertId);
                    await SyncRealDataAsync();
                }
                else if (action == "refreshDatabase")
                {
                    await SyncRealDataAsync();
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