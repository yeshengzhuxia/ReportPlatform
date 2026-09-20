using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ReportPlatform.Infrastructure;
using ReportPlatform.Models;
using Xunit;

namespace ReportPlatform.Tests;

public sealed class FilterTemplateTests
{
    private static HttpClient Client(PlatformFactory factory)
    {
        var client = factory.CreateClient(); client.DefaultRequestHeaders.Add("X-Requested-With", "ReportPlatform"); return client;
    }
    private static async Task<JsonObject> Json(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode, body);
        return JsonNode.Parse(body)!.AsObject();
    }
    private static Task<JsonObject> Login(HttpClient client, string username = "admin", string password = "Integration123!", string accountId = "default") =>
        LoginCore(client, username, password, accountId);
    private static async Task<JsonObject> LoginCore(HttpClient client, string username, string password, string accountId) =>
        await Json(await client.PostAsJsonAsync("/api/login", new { username, password, accountId }));
    private static Report Report(string id = "report") => new()
    {
        Id = id, Name = "订单报表", ConnectionId = "source", Enabled = true,
        Sql = "SELECT OrgId, Amount FROM Orders", OrgColumn = "OrgId", Fields = [new("Amount", "金额")]
    };
    private static Filter Condition(string value = "100") => new("Amount", "gte", JsonSerializer.SerializeToElement(value));

    [Fact]
    public async Task PresetDefaultsPersistValidateAndRequireReportEditPermission()
    {
        using var factory = new PlatformFactory(); using var admin = Client(factory); await Login(admin);
        var store = factory.Services.GetRequiredService<PlatformStore>();
        store.Save(new DataSource { Id = "source", Name = "Test" });
        var draft = Report(); draft.FilterTemplates = [new("large", "大额订单", [Condition()])]; draft.DefaultFilterTemplateId = "large";
        var saved = await Json(await admin.PostAsJsonAsync("/api/admin/reports", draft));
        var id = saved["id"]!.GetValue<string>();
        var boot = await Json(await admin.GetAsync("/api/bootstrap"));
        Assert.Equal("large", boot["reports"]![0]!["defaultFilterTemplateId"]!.GetValue<string>());
        Assert.Equal("100", boot["reports"]![0]!["filterTemplates"]![0]!["filters"]![0]!["value"]!.GetValue<string>());
        var legacy = saved.DeepClone().AsObject(); legacy.Remove("filterTemplates"); legacy.Remove("defaultFilterTemplateId");
        var retained = await Json(await admin.PutAsJsonAsync("/api/admin/reports/" + id, legacy));
        Assert.Single(retained["filterTemplates"]!.AsArray());
        saved["defaultFilterTemplateId"] = "missing";
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/reports/" + id, saved)).StatusCode);
        saved["defaultFilterTemplateId"] = "large";
        saved["filterTemplates"]![0]!["filters"]![0]!["field"] = "Unknown";
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/reports/" + id, saved)).StatusCode);
        store.Save(new Role { Id = "query", Name = "查询员", Permissions = new() { [id] = ["view", "query"] } });
        await Json(await admin.PostAsJsonAsync("/api/admin/users", new { name = "查询员", username = "query_user", password = "QueryReader123!", roleIds = new[] { "query" }, accountIds = new[] { "default" }, enabled = true }));
        using var reader = Client(factory); await Login(reader, "query_user", "QueryReader123!");
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync("/api/admin/reports/" + id, retained)).StatusCode);
        Assert.Equal("large", (await Json(await reader.GetAsync("/api/bootstrap")))["reports"]![0]!["defaultFilterTemplateId"]!.GetValue<string>());
        var queryRole = store.Find<Role>("query")!; queryRole.Permissions.Clear(); store.Save(queryRole);
        Assert.Empty((await Json(await reader.GetAsync("/api/bootstrap")))["reports"]!.AsArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"/api/reports/{id}/filter-templates")).StatusCode);
    }

    [Fact]
    public async Task PersonalTemplatesAreDurableScopedAndCannotBypassQueryValidation()
    {
        using var factory = new PlatformFactory(); using var admin = Client(factory); await Login(admin);
        var store = factory.Services.GetRequiredService<PlatformStore>();
        store.Save(new DataSource { Id = "source", Name = "Test" }); store.Save(Report()); store.Save(Report("other-report"));
        var saved = await Json(await admin.PostAsJsonAsync("/api/reports/report/filter-templates", new { name = "常用查询", filters = new[] { Condition("0' OR 1=1") } }));
        var id = saved["id"]!.GetValue<string>();
        using var sameUser = Client(factory); await Login(sameUser);
        Assert.Single((await Json(await sameUser.GetAsync("/api/reports/report/filter-templates")))["templates"]!.AsArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/reports/report/filter-templates", new { name = "常用查询", filters = Array.Empty<Filter>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/reports/report/filter-templates", new { name = "无效字段", filters = new[] { new { field = "Other", op = "eq", value = "1" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/reports/report/filter-templates", new { name = "无效方式", filters = new[] { new { field = "Amount", op = "execute", value = "1" } } })).StatusCode);
        await Json(await admin.PostAsJsonAsync("/api/reports/report/query", new { filters = saved["filters"] }));
        Assert.Equal("1", factory.Executor.LastQuery!.Parameters["org"]);
        Assert.DoesNotContain("OR 1=1", factory.Executor.LastQuery.DataSql);
        Assert.Empty((await Json(await admin.GetAsync("/api/reports/other-report/filter-templates")))["templates"]!.AsArray());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/reports/other-report/filter-templates/{id}")).StatusCode);
        store.Save(new Role { Id = "query", Name = "查询员", Permissions = new() { ["report"] = ["view", "query"] } });
        await Json(await admin.PostAsJsonAsync("/api/admin/users", new { name = "查询员", username = "other_user", password = "OtherReader123!", roleIds = new[] { "query" }, accountIds = new[] { "default" }, enabled = true }));
        using var otherUser = Client(factory); await Login(otherUser, "other_user", "OtherReader123!");
        Assert.Empty((await Json(await otherUser.GetAsync("/api/reports/report/filter-templates")))["templates"]!.AsArray());
        Assert.Equal(HttpStatusCode.NotFound, (await otherUser.DeleteAsync($"/api/reports/report/filter-templates/{id}")).StatusCode);
        store.Save(new Account { Id = "second", Name = "第二账套", OrgId = "200", Enabled = true });
        var user = store.Find<User>("admin")!; user.AccountIds.Add("second"); store.Save(user);
        using var second = Client(factory); await Login(second, accountId: "second");
        Assert.Empty((await Json(await second.GetAsync("/api/reports/report/filter-templates")))["templates"]!.AsArray());
        Assert.Equal(HttpStatusCode.NotFound, (await second.DeleteAsync($"/api/reports/report/filter-templates/{id}")).StatusCode);
        var report = Report(); report.Fields = [new("NewAmount", "新金额")]; store.Save(report);
        Assert.NotNull((await Json(await admin.GetAsync("/api/reports/report/filter-templates")))["templates"]![0]!["invalidReason"]);
        await Json(await admin.DeleteAsync($"/api/reports/report/filter-templates/{id}"));
        Assert.Empty((await Json(await admin.GetAsync("/api/reports/report/filter-templates")))["templates"]!.AsArray());
        var role = store.Find<Role>("query")!; role.Permissions["report"] = ["view"]; store.Save(role);
        Assert.Equal(HttpStatusCode.Forbidden, (await otherUser.PostAsJsonAsync("/api/reports/report/filter-templates", new { name = "无查询权限", filters = Array.Empty<Filter>() })).StatusCode);
    }
}
