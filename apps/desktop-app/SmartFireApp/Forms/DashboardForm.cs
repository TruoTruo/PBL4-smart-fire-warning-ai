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
        Text = "FireGuard IoT — Giám Sát Cảnh Báo Cháy Thông Minh (CSDL: SmartFireDB)";
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

        // Lắng nghe MQTT từ Raspberry Pi 5
        _mqtt.SensorReadingReceived += async (_, _) => await SyncRealDataAsync();
        _mqtt.AlertReceived += async (_, _) => await SyncRealDataAsync();

        try
        {
            await _mqtt.StartAsync("test.mosquitto.org", 1883);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MQTT Init Error] {ex.Message}");
        }

        // Khi WebView2 nạp xong HTML thì đẩy ngay dữ liệu thực từ CSDL vào
        _webView.NavigationCompleted += async (_, _) =>
        {
            await SyncRealDataAsync();
        };

        // Quét lại CSDL mỗi 3 giây để cập nhật tức thì nếu có dữ liệu mới từ XAMPP
        _syncTimer.Tick += async (_, _) => await SyncRealDataAsync();
        _syncTimer.Start();
    }

    private async Task SyncRealDataAsync()
    {
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
