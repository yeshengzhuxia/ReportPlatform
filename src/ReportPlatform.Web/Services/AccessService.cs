using System.Security.Cryptography;
using ReportPlatform.Infrastructure;
using ReportPlatform.Models;

namespace ReportPlatform.Services;

public sealed class AccessService(PlatformStore store, PlatformSettings settings)
{
    private readonly object loginLock = new();
    private readonly Dictionary<string, (int Count, DateTimeOffset Until)> attempts = [];
    public bool IsAdmin(User user) => user.RoleIds.Any(id => store.Find<Role>(id)?.Admin == true);
    public bool Allowed(User user, string reportId, string action) => IsAdmin(user) || user.RoleIds
        .Select(store.Find<Role>).Any(role => role?.Permissions.TryGetValue(reportId, out var actions) == true && actions.Contains(action));
    public void RequireAdmin(User user) { if (!IsAdmin(user)) throw new ApiException("没有管理权限", 403); }
    public LoginContext RequireLogin(HttpContext context)
    {
        var token = context.Request.Cookies["session"] ?? "";
        var session = store.FindSession(token);
        var user = session is null ? null : store.Find<User>(session.UserId);
        var account = session is null ? null : store.Find<Account>(session.AccountId);
        if (user?.Enabled != true || account?.Enabled != true || !user.AccountIds.Contains(account.Id))
            throw new ApiException("请重新登录", 401);
        return new(user, account, token);
    }
    public object Login(HttpContext context, LoginRequest request)
    {
        // Serializing this short section ensures concurrent requests cannot bypass the failure counter.
        lock (loginLock)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var expired in attempts.Where(p => p.Value.Until < now).Select(p => p.Key).ToList()) attempts.Remove(expired);
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "local";
            var bucket = attempts.GetValueOrDefault(ip, (Count: 0, Until: now.AddMinutes(15)));
            if (bucket.Count >= 10) throw new ApiException("登录尝试过多，请 15 分钟后重试", 429);
            var user = store.All<User>().FirstOrDefault(u => u.Username == request.Username);
            if (user?.Enabled != true || !CredentialService.VerifyPassword(request.Password, user.Password))
            {
                attempts[ip] = (bucket.Count + 1, bucket.Until);
                throw new ApiException("账号或密码错误", 401);
            }
            var accounts = user.AccountIds.Select(store.Find<Account>).Where(a => a?.Enabled == true).Cast<Account>().ToList();
            if (string.IsNullOrEmpty(request.AccountId)) return new { accounts = accounts.Select(a => new { a.Id, a.Name }) };
            var account = accounts.FirstOrDefault(a => a.Id == request.AccountId) ?? throw new ApiException("无权登录该账套", 403);
            attempts.Remove(ip);
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            store.AddSession(token, user.Id, account.Id);
            context.Response.Cookies.Append("session", token, new CookieOptions
            {
                HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = settings.CookieSecure,
                Path = "/", MaxAge = TimeSpan.FromHours(8), IsEssential = true
            });
            store.Audit(user, account, null, "login", true);
            return new { ok = true };
        }
    }
}
