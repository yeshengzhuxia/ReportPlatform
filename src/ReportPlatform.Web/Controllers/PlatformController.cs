using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using ReportPlatform.Infrastructure;
using ReportPlatform.Models;
using ReportPlatform.Services;

namespace ReportPlatform.Controllers;

[ApiController]
[Route("api")]
public sealed class PlatformController(PlatformStore store, AccessService access, ConfigurationService configuration,
    IReportExecutor executor, ILogger<PlatformController> logger) : ControllerBase
{
    [HttpPost("login")]
    public object Login(LoginRequest request) => access.Login(HttpContext, request);

    [HttpPost("logout")]
    public object Logout()
    {
        var login = access.RequireLogin(HttpContext); store.RevokeSession(login.Token);
        Response.Cookies.Delete("session", new CookieOptions { Path = "/", HttpOnly = true, SameSite = SameSiteMode.Strict });
        return new { ok = true };
    }
    [HttpGet("bootstrap")]
    public object Bootstrap()
    {
        var login = access.RequireLogin(HttpContext);
        return new
        {
            user = PlatformStore.Public(login.User), account = login.Account, admin = access.IsAdmin(login.User),
            reports = store.All<Report>().Where(r => r.Enabled && access.Allowed(login.User, r.Id, "view")).Select(r => new
            {
                r.Id, r.Name, r.Description, r.Category, r.Fields,
                actions = new[] { "view", "query", "export", "edit" }.Where(a => access.Allowed(login.User, r.Id, a))
            })
        };
    }
    [HttpGet("admin/{kind}")]
    [HttpGet("admin/{kind}/{id}")]
    public object ReadConfiguration(string kind, string? id = null)
    {
        CheckKind(kind); var login = access.RequireLogin(HttpContext); RequireConfigurationAccess(login.User, kind, id);
        return id is null ? store.All(kind).Select(PlatformStore.Public).ToArray()
            : PlatformStore.Public(store.Find(kind, id) ?? throw new ApiException("记录不存在", 404));
    }
    [HttpPost("admin/{kind}")]
    public object CreateConfiguration(string kind, JsonElement body) => Save(kind, null, body);
    [HttpPut("admin/{kind}/{id}")]
    public object UpdateConfiguration(string kind, string id, JsonElement body) => Save(kind, id, body);
    private object Save(string kind, string? id, JsonElement body)
    {
        // Handle expected business errors before returning through MVC's external async pipeline.
        try
        {
            CheckKind(kind); var login = access.RequireLogin(HttpContext); RequireConfigurationAccess(login.User, kind, id);
            if (kind == "users")
            {
                var username = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("username", out var value)
                    && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                if (!UsernamePolicy.IsValid(username))
                    return BadRequest(new { error = UsernamePolicy.ErrorMessage });
            }
            var entity = configuration.Save(kind, id, body, login.User);
            store.Audit(login.User, login.Account, entity as Report, "save:" + kind, true, detail: entity.Id);
            return PlatformStore.Public(entity);
        }
        catch (ApiException ex)
        {
            return StatusCode(ex.Status, new { error = ex.Message });
        }
    }
    [HttpDelete("admin/{kind}/{id}")]
    public object DeleteConfiguration(string kind, string id)
    {
        CheckKind(kind); var login = access.RequireLogin(HttpContext); access.RequireAdmin(login.User);
        configuration.Delete(kind, id, login.User); store.Audit(login.User, login.Account, null, "delete:" + kind, true, detail: id);
        return new { ok = true };
    }
    [HttpPost("admin/connections/{id}/test")]
    public async Task<object> TestConnection(string id, CancellationToken cancellationToken)
    {
        var login = access.RequireLogin(HttpContext); access.RequireAdmin(login.User);
        var source = store.Find<DataSource>(id) ?? throw new ApiException("数据源不存在", 404);
        try { await executor.TestAsync(source, cancellationToken); return new { ok = true, message = "连接成功" }; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Connection test failed for {Id}: {Type}", id, ex.GetType().Name);
            throw new ApiException("连接失败，请检查地址、账号和证书配置");
        }
    }
    [HttpPost("reports/{id}/query")]
    public Task<IActionResult> QueryReport(string id, QueryRequest request, CancellationToken cancellationToken) => Run(id, "query", request, cancellationToken);
    [HttpPost("reports/{id}/export")]
    public Task<IActionResult> ExportReport(string id, QueryRequest request, CancellationToken cancellationToken) => Run(id, "export", request, cancellationToken);
    private async Task<IActionResult> Run(string id, string action, QueryRequest request, CancellationToken cancellationToken)
    {
        var login = access.RequireLogin(HttpContext); var report = store.Find<Report>(id);
        if (report?.Enabled != true || !access.Allowed(login.User, id, "view") || !access.Allowed(login.User, id, action))
            throw new ApiException("没有该报表的操作权限", 403);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var limit = action == "export" ? 10000 : 500;
            var query = SqlQueryBuilder.Build(report, login.Account.OrgId, request.Filters, limit + 1);
            var source = store.Find<DataSource>(report.ConnectionId) ?? throw new ApiException("数据源不存在");
            var records = await executor.QueryAsync(source, query, cancellationToken);
            var truncated = records.Count > limit;
            if (action == "export" && truncated) throw new ApiException("导出超过 10000 行，请缩小筛选范围");
            var rows = records.Take(limit).ToList();
            store.Audit(login.User, login.Account, report, action, true, rows.Count, stopwatch.ElapsedMilliseconds);
            if (action == "query") return Ok(new { rows, truncated, limit, duration = stopwatch.ElapsedMilliseconds });
            var filename = string.Concat(report.Name.Select(c => char.IsControl(c) || "/\\:*?\"<>|".Contains(c) ? '_' : c));
            return File(CsvExport.Create(report, rows), "text/csv; charset=utf-8", filename + ".csv");
        }
        catch (Exception ex)
        {
            var message = ex switch
            {
                ApiException => ex.Message,
                SqlException sql => SqlFailureMessages.ForNumber(sql.Number),
                OperationCanceledException => "查询已取消。",
                _ => "数据库查询失败，请管理员检查连接及报表配置。"
            };
            store.Audit(login.User, login.Account, report, action, false, duration: stopwatch.ElapsedMilliseconds,
                detail: message);
            if (ex is OperationCanceledException) throw;
            logger.LogWarning("Report {Id} failed: {Type}; {Message}", id, ex.GetType().Name, message);
            return StatusCode(ex is ApiException api ? api.Status : 400, new { error = message });
        }
    }
    [HttpGet("audit")]
    public object Audit([FromQuery] string? date)
    {
        var login = access.RequireLogin(HttpContext); access.RequireAdmin(login.User);
        date ??= DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd");
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw new ApiException("日期无效");
        return new
        {
            records = store.Query("SELECT * FROM audit WHERE date(at,'+8 hours')=@day ORDER BY id DESC LIMIT 1000", ("@day", date)),
            summary = store.Query("""
                SELECT username,reportName,accountId,SUM(action='query') queries,SUM(action='export') exports,SUM(success=0) failures
                FROM audit WHERE date(at,'+8 hours')=@day AND action IN ('query','export')
                GROUP BY userId,username,reportId,reportName,accountId
                """, ("@day", date))
        };
    }
    private static void CheckKind(string kind)
    {
        if (!PlatformStore.EntityTypes.ContainsKey(kind)) throw new ApiException("配置类型不存在", 404);
    }
    private void RequireConfigurationAccess(User user, string kind, string? id)
    {
        if (!access.IsAdmin(user) && !(kind == "reports" && id is not null && access.Allowed(user, id, "edit")))
            throw new ApiException("没有管理权限", 403);
    }
}
