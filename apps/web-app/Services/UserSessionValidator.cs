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

    public static async Task ValidateAsync(CookieValidatePrincipalContext ctx)
    {
        var principal = ctx.Principal;
        if (principal == null || !int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out int userId))
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

    private static async Task RejectAsync(CookieValidatePrincipalContext ctx)
    {
        ctx.RejectPrincipal();
        await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
