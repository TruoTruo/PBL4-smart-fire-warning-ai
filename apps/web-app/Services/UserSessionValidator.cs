using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using SmartFireWeb.Services;

namespace SmartFireWeb.Services;

/// <summary>
/// Kiểm tra cookie đăng nhập còn khớp CSDL: tài khoản bị xoá hoặc đổi quyền thì phiên bị huỷ.
/// Mỗi tài khoản chỉ hỏi CSDL tối đa 15 s/lần. CSDL lỗi thì giữ phiên (API sẽ báo lỗi CSDL riêng).
/// </summary>
public static class UserSessionValidator
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);
    private static readonly ConcurrentDictionary<int, (DateTime CheckedAt, string Role)> LastChecked = new();
    // Phiên cấp trước mốc này bị huỷ (admin đặt lại mật khẩu). Lưu trong bộ nhớ: khởi động lại máy chủ thì mất,
    // nhưng cookie cũng chỉ sống tối đa 8 giờ không dùng.
    private static readonly ConcurrentDictionary<int, long> RevokedBefore = new();   // UTC ticks

    public const string LoginTicksClaim = "smartfire:login_ticks";

    public static async Task ValidateAsync(CookieValidatePrincipalContext ctx)
    {
        var principal = ctx.Principal;
        if (principal == null || !int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out int userId))
        {
            await RejectAsync(ctx);
            return;
        }

        if (RevokedBefore.TryGetValue(userId, out long revokedTicks)
            && (!long.TryParse(principal.FindFirstValue(LoginTicksClaim), out long loginTicks) || loginTicks <= revokedTicks))
        {
            await RejectAsync(ctx);
            return;
        }

        string role = principal.FindFirstValue(ClaimTypes.Role) ?? "";
        if (LastChecked.TryGetValue(userId, out var last)
            && DateTime.UtcNow - last.CheckedAt < CheckInterval
            && last.Role == role)
            return;

        var db = ctx.HttpContext.RequestServices.GetRequiredService<DatabaseService>();
        try
        {
            var user = await db.GetUserByIdAsync(userId);
            if (user == null || !string.Equals(user.Role, role, StringComparison.Ordinal))
            {
                LastChecked.TryRemove(userId, out _);
                await RejectAsync(ctx);
                return;
            }
            LastChecked[userId] = (DateTime.UtcNow, user.Role);
        }
        catch (Exception)
        {
            // CSDL tạm lỗi: không đăng xuất người đang trực
        }
    }

    /// <summary>Gọi khi xoá tài khoản để phiên của người đó hết hiệu lực ngay ở lần gọi API kế tiếp.</summary>
    public static void Forget(int userId) => LastChecked.TryRemove(userId, out _);

    /// <summary>Huỷ mọi phiên đang mở của tài khoản (dùng khi admin đặt lại mật khẩu).</summary>
    public static void RevokeSessions(int userId)
    {
        RevokedBefore[userId] = DateTime.UtcNow.Ticks;
        LastChecked.TryRemove(userId, out _);
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext ctx)
    {
        ctx.RejectPrincipal();
        await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
