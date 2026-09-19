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
    public Task<List<Dictionary<string, object?>>> QueryAsync(DataSource source, PreparedQuery query, CancellationToken cancellationToken)
    {
        LastQuery = query;
        if (Fail) throw new InvalidOperationException("Simulated database failure");
        return Task.FromResult(Enumerable.Range(0, Count).Select(i => new Dictionary<string, object?> { ["Amount"] = i + 100 }).ToList());
    }
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
        draft.OrgColumn = "";
        invalid = await client.PostAsJsonAsync("/api/admin/reports", draft);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("请填写组织 ID 字段", JsonNode.Parse(await invalid.Content.ReadAsStringAsync())!["error"]!.GetValue<string>());
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
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/login", new { username = "admin", password = "wrong" })).StatusCode);
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
    public async Task QueryAndExportLimitsAreEnforced()
    {
        using var factory = new PlatformFactory(); using var client = Client(factory); await Login(client);
        var store = factory.Services.GetRequiredService<PlatformStore>();
        store.Save(new DataSource { Id = "source", Name = "Test" });
        store.Save(new Report { Id = "report", Name = "测试报表", ConnectionId = "source", Enabled = true, Sql = "SELECT OrgId, Amount FROM Orders", OrgColumn = "OrgId", Fields = [new("Amount", "金额")] });
        factory.Executor.Count = 501;
        var result = await Json(await client.PostAsJsonAsync("/api/reports/report/query", new { }));
        Assert.Equal(500, result["rows"]!.AsArray().Count); Assert.True(result["truncated"]!.GetValue<bool>());
        factory.Executor.Count = 10001;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/reports/report/export", new { })).StatusCode);
    }
}
