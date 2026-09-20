using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReportPlatform.Infrastructure;
using ReportPlatform.Models;
using ReportPlatform.Services;
using Xunit;

namespace ReportPlatform.Tests;

public sealed class TestExecutor : IReportExecutor
{
    public PreparedQuery? LastQuery { get; private set; }
    public bool Fail { get; set; }
    public int Count { get; set; } = 1;
    public Task TestAsync(DataSource source, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<PagedQueryResult> QueryPageAsync(DataSource source, PreparedQuery query, int page, int pageSize, CancellationToken cancellationToken)
    {
        LastQuery = query;
        if (Fail) throw new InvalidOperationException("Simulated database failure");
        var rows = Enumerable.Range(0, Count).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(i => new Dictionary<string, object?> { ["Amount"] = i + 100 }).ToArray();
        return Task.FromResult(new PagedQueryResult(rows, Count));
    }
    public async IAsyncEnumerable<Dictionary<string, object?>> StreamAsync(DataSource source, PreparedQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        LastQuery = query;
        if (Fail) throw new InvalidOperationException("Simulated database failure");
        foreach (var i in Enumerable.Range(0, Count))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new Dictionary<string, object?> { ["Amount"] = i + 100 };
            await Task.Yield();
        }
    }
    public Task<IReadOnlyList<ReportField>> PreviewFieldsAsync(DataSource source, string sql, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ReportField>>([new("OrgId", ""), new("Amount", "金额")]);
}
public sealed class PlatformFactory : WebApplicationFactory<Program>
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ReportPlatform-" + Guid.NewGuid().ToString("N"));
    public TestExecutor Executor { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<PlatformSettings>();
            services.AddSingleton(new PlatformSettings(directory, new string('a', 64), "Integration123!", false));
            services.RemoveAll<IReportExecutor>(); services.AddSingleton<IReportExecutor>(Executor);
        });
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
public sealed class ApiTests
{
    [Fact]
    public async Task ReportMenuSettingsPersistAndRespectViewAndButtonPermissions()
    {
        using var factory = new PlatformFactory(); using var admin = Client(factory); await Login(admin);
        var store = factory.Services.GetRequiredService<PlatformStore>();
        store.Save(new DataSource { Id = "menu-source", Name = "Test" });
        const string icon = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
        async Task<JsonObject> CreateReport(string name, bool showInMenu, bool enabled = true, string reportIcon = "") =>
            await Json(await admin.PostAsJsonAsync("/api/admin/reports", new
            {
                name, showInMenu, enabled, icon = reportIcon, category = "生产管理", connectionId = "menu-source",
                sql = "SELECT OrgId, Amount FROM Orders", orgColumn = "OrgId", fields = new[] { new { key = "Amount", label = "金额" } }
            }));
        var featured = await CreateReport("可查看的菜单报表", true, reportIcon: icon);
        var hidden = await CreateReport("无查看权限的菜单报表", true);
        var disabled = await CreateReport("停用的菜单报表", true, false);
        var center = await CreateReport("仅报表中心显示", false);
        var featuredId = featured["id"]!.GetValue<string>();
        var hiddenId = hidden["id"]!.GetValue<string>();
        var centerId = center["id"]!.GetValue<string>();
        Assert.True((await Json(await admin.GetAsync("/api/admin/reports/" + featuredId)))["showInMenu"]!.GetValue<bool>());
        var role = await Json(await admin.PostAsJsonAsync("/api/admin/roles", new
        {
            name = "菜单查看员", permissions = new Dictionary<string, string[]>
            {
                [featuredId] = ["view"], [centerId] = ["view"], [hiddenId] = ["query"],
                [disabled["id"]!.GetValue<string>()] = ["view"]
            }
        }));
        await Json(await admin.PostAsJsonAsync("/api/admin/users", new
        {
            name = "菜单查看员", username = "menu_reader", password = "MenuReader123!",
            roleIds = new[] { role["id"]!.GetValue<string>() }, accountIds = new[] { "default" }, enabled = true
        }));
        using var reader = Client(factory); await Login(reader, "menu_reader", "MenuReader123!");
        var reports = (await Json(await reader.GetAsync("/api/bootstrap")))["reports"]!.AsArray();
        Assert.Equal(2, reports.Count);
        var menu = Assert.Single(reports.Where(r => r!["showInMenu"]!.GetValue<bool>()))!;
        Assert.Equal(featuredId, menu["id"]!.GetValue<string>());
        Assert.Equal("生产管理", menu["category"]!.GetValue<string>());
        Assert.Equal(icon, menu["icon"]!.GetValue<string>());
        Assert.Equal("view", Assert.Single(menu["actions"]!.AsArray())!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"/api/reports/{featuredId}/query", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"/api/reports/{featuredId}/export", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"/api/reports/{hiddenId}/query", new { })).StatusCode);
        featured["category"] = "财务管理";
        featured["showInMenu"] = false;
        await Json(await admin.PutAsJsonAsync("/api/admin/reports/" + featuredId, featured));
        reports = (await Json(await reader.GetAsync("/api/bootstrap")))["reports"]!.AsArray();
        Assert.Empty(reports.Where(r => r!["showInMenu"]!.GetValue<bool>()));
        Assert.Equal("财务管理", reports.Single(r => r!["id"]!.GetValue<string>() == featuredId)!["category"]!.GetValue<string>());
        featured["enabled"] = false;
        await Json(await admin.PutAsJsonAsync("/api/admin/reports/" + featuredId, featured));
        Assert.Single((await Json(await reader.GetAsync("/api/bootstrap")))["reports"]!.AsArray());
        var invalidIcon = await admin.PostAsJsonAsync("/api/admin/reports", new
        {
            name = "非法图标", connectionId = "menu-source", sql = "SELECT OrgId, Amount FROM Orders", orgColumn = "OrgId",
            icon = "data:image/svg+xml;base64,PHN2Zz48L3N2Zz4=", fields = new[] { new { key = "Amount", label = "金额" } }, enabled = true
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalidIcon.StatusCode);
    }

    [Fact]
    public async Task ReportConfigurationSupportsChineseColumnsAndReturnsFieldValidationErrors()
    {
        using var factory = new PlatformFactory(); using var client = Client(factory); await Login(client);
        var store = factory.Services.GetRequiredService<PlatformStore>();
        store.Save(new DataSource { Id = "source", Name = "Test" });
        var draft = new Report { Name = "中文报表", ConnectionId = "source", Sql = "SELECT OrgId AS [组织ID], Amount AS [销售金额] FROM Orders", OrgColumn = "组织ID", Fields = [new("销售金额", "销售金额")], Enabled = true };
        var saved = await Json(await client.PostAsJsonAsync("/api/admin/reports", draft));
        Assert.Equal("组织ID", saved["orgColumn"]!.GetValue<string>());
        draft.Fields = [new("", "金额")];
        var invalid = await client.PostAsJsonAsync("/api/admin/reports", draft);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("第 1 行字段名为空", JsonNode.Parse(await invalid.Content.ReadAsStringAsync())!["error"]!.GetValue<string>());
        draft.Fields = [new("销售金额", "销售金额")];
        draft.OrgColumn = "";
        var withoutOrganizationFilter = await Json(await client.PutAsJsonAsync("/api/admin/reports/" + saved["id"]!.GetValue<string>(), draft));
        Assert.Equal("", withoutOrganizationFilter["orgColumn"]!.GetValue<string>());
        Assert.Single(store.All<Report>());
    }

    [Fact]
    public async Task InvalidUsernamesReturnHelpfulErrorsWithoutThrowingOrSaving()
    {
        using var factory = new PlatformFactory(); using var client = Client(factory); await Login(client);
        foreach (var username in new[] { "", "a", "张三", "user name", "user\n", new string('a', 81) })
        {
            var response = await client.PostAsJsonAsync("/api/admin/users", new
            {
                name = "张三", username, password = "ReaderPassword123!",
                roleIds = Array.Empty<string>(), accountIds = new[] { "default" }, enabled = true
            });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var error = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            Assert.Equal(UsernamePolicy.ErrorMessage, error["error"]!.GetValue<string>());
        }
        var store = factory.Services.GetRequiredService<PlatformStore>();
        Assert.Single(store.All<User>());
        var created = await Json(await client.PostAsJsonAsync("/api/admin/users", new
        {
            name = "张三", username = "zhangsan_01", password = "ReaderPassword123!",
            roleIds = Array.Empty<string>(), accountIds = new[] { "default" }, enabled = true
        }));
        Assert.Equal("张三", created["name"]!.GetValue<string>());
        created["username"] = "张三";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/admin/users/" + created["id"]!.GetValue<string>(), created)).StatusCode);
        Assert.Equal("zhangsan_01", store.Find<User>(created["id"]!.GetValue<string>())!.Username);
    }

    private static HttpClient Client(PlatformFactory factory)
    {
        var client = factory.CreateClient(); client.DefaultRequestHeaders.Add("X-Requested-With", "ReportPlatform"); return client;
    }
    private static async Task<JsonObject> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        return JsonNode.Parse(text)!.AsObject();
    }
    private static async Task Login(HttpClient client, string user = "admin", string password = "Integration123!", string account = "default") =>
        await Json(await client.PostAsJsonAsync("/api/login", new { username = user, password, accountId = account }));

    [Fact]
    public async Task LoginAccountSelectionAndRequestForgeryProtection()
    {
        using var factory = new PlatformFactory(); using var client = Client(factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/bootstrap")).StatusCode);
        var wrongLogin = await client.PostAsJsonAsync("/api/login", new { username = "admin", password = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongLogin.StatusCode);
        Assert.Equal("账号或密码错误", JsonNode.Parse(await wrongLogin.Content.ReadAsStringAsync())!["error"]!.GetValue<string>());
        var step = await Json(await client.PostAsJsonAsync("/api/login", new { username = "admin", password = "Integration123!" }));
        Assert.Single(step["accounts"]!.AsArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/login", new { username = "admin", password = "Integration123!", accountId = "foreign" })).StatusCode);
        await Login(client);
        Assert.Equal("1", (await Json(await client.GetAsync("/api/bootstrap")))["account"]!["orgId"]!.GetValue<string>());
        await client.PostAsJsonAsync("/api/logout", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/bootstrap")).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Requested-With");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/login", new { username = "admin", password = "Integration123!" })).StatusCode);
    }
    [Fact]
    public async Task PasswordCanBeChangedAndRevokesExistingSession()
    {
        using var factory = new PlatformFactory(); using var client = Client(factory); await Login(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/password", new { currentPassword = "wrong", newPassword = "NewPassword123!" })).StatusCode);
        await Json(await client.PostAsJsonAsync("/api/password", new { currentPassword = "Integration123!", newPassword = "abc12" }));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/bootstrap")).StatusCode);
        await Login(client, password: "abc12");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/bootstrap")).StatusCode);
    }
    [Fact]
    public async Task ButtonPermissionsOrganizationScopeExportAndAuditWorkTogether()
    {
        using var factory = new PlatformFactory(); using var admin = Client(factory); await Login(admin);
        var connection = await Json(await admin.PostAsJsonAsync("/api/admin/connections", new { name = "ERP", host = "localhost", database = "ERP", username = "readonly", password = "Database123!", port = "1433" }));
        Assert.Null(connection["secret"]); Assert.Null(connection["password"]);
        var report = await Json(await admin.PostAsJsonAsync("/api/admin/reports", new { name = "订单", connectionId = connection["id"]!.GetValue<string>(), sql = "SELECT OrgId, Amount FROM Orders", orgColumn = "OrgId", fields = new[] { new { key = "Amount", label = "金额" } }, enabled = true }));
        var reportId = report["id"]!.GetValue<string>();
        var role = await Json(await admin.PostAsJsonAsync("/api/admin/roles", new { name = "查询员", permissions = new Dictionary<string, string[]> { [reportId] = ["view", "query"] } }));
        var account = await Json(await admin.PostAsJsonAsync("/api/admin/accounts", new { name = "第二账套", orgId = "200", enabled = true }));
        var user = await Json(await admin.PostAsJsonAsync("/api/admin/users", new { name = "查询员", username = "reader", password = "ReaderPassword123!", roleIds = new[] { role["id"]!.GetValue<string>() }, accountIds = new[] { account["id"]!.GetValue<string>() }, enabled = true }));
        Assert.Null(user["password"]);
        using var reader = Client(factory); await Login(reader, "reader", "ReaderPassword123!", account["id"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"/api/reports/{reportId}/export", new { })).StatusCode);
        var result = await Json(await reader.PostAsJsonAsync($"/api/reports/{reportId}/query", new { orgId = "evil", filters = Array.Empty<object>() }));
        Assert.Single(result["rows"]!.AsArray()); Assert.Equal("200", factory.Executor.LastQuery!.Parameters["org"]);
        var export = await admin.PostAsJsonAsync($"/api/reports/{reportId}/export", new { });
        Assert.Equal("text/csv", export.Content.Headers.ContentType!.MediaType);
        Assert.Contains("金额", await export.Content.ReadAsStringAsync());
        factory.Executor.Fail = true;
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.PostAsJsonAsync($"/api/reports/{reportId}/query", new { })).StatusCode);
        var audit = await Json(await admin.GetAsync("/api/audit"));
        var readerSummary = audit["summary"]!.AsArray().Single(x => x!["username"]!.GetValue<string>() == "reader")!;
        Assert.Equal(2, readerSummary["queries"]!.GetValue<int>()); Assert.Equal(1, readerSummary["failures"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.DeleteAsync("/api/admin/connections/" + connection["id"]!.GetValue<string>())).StatusCode);
        user["enabled"] = false;
        await Json(await admin.PutAsJsonAsync("/api/admin/users/" + user["id"]!.GetValue<string>(), user));
        Assert.Equal(HttpStatusCode.Unauthorized, (await reader.GetAsync("/api/bootstrap")).StatusCode);
    }
    [Fact]
    public async Task PagingAndUnlimitedExportWork()
    {
        using var factory = new PlatformFactory(); using var client = Client(factory); await Login(client);
        var store = factory.Services.GetRequiredService<PlatformStore>();
        store.Save(new DataSource { Id = "source", Name = "Test" });
        store.Save(new Report { Id = "report", Name = "测试报表", ConnectionId = "source", Enabled = true, Sql = "SELECT OrgId, Amount FROM Orders", OrgColumn = "OrgId", Fields = [new("Amount", "金额")] });
        factory.Executor.Count = 1501;
        var result = await Json(await client.PostAsJsonAsync("/api/reports/report/query", new { page = 2, pageSize = 500 }));
        Assert.Equal(500, result["rows"]!.AsArray().Count); Assert.Equal(1501, result["total"]!.GetValue<int>());
        Assert.Equal(2, result["page"]!.GetValue<int>()); Assert.Equal(500, result["pageSize"]!.GetValue<int>());
        var export = await client.PostAsJsonAsync("/api/reports/report/export", new { });
        Assert.True(export.IsSuccessStatusCode);
        var csv = await export.Content.ReadAsStringAsync();
        Assert.Contains("金额", csv); Assert.Contains("1600", csv);
    }
}
