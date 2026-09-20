using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using ReportPlatform.Infrastructure;
using ReportPlatform.Models;
using ReportPlatform.Services;

namespace ReportPlatform.Controllers;

[ApiController]
[Route("api/reports/{reportId}/filter-templates")]
public sealed class FilterTemplatesController(PlatformStore store, AccessService access) : ControllerBase
{
    private Report RequireReport(LoginContext login, string reportId, bool writing = false)
    {
        var report = store.Find<Report>(reportId);
        if (report?.Enabled != true || !access.Allowed(login.User, reportId, "view") ||
            writing && !access.Allowed(login.User, reportId, "query"))
            throw new ApiException("没有该报表的操作权限", 403);
        return report;
    }

    [HttpGet]
    public IActionResult List(string reportId)
    {
        try
        {
            var login = access.RequireLogin(HttpContext);
            var report = RequireReport(login, reportId);
            var templates = store.Query("""
                SELECT id,name,filters FROM report_filter_templates
                WHERE reportId=@report AND userId=@user AND accountId=@account ORDER BY name
                """, ("@report", reportId), ("@user", login.User.Id), ("@account", login.Account.Id)).Select(row =>
            {
                var filters = JsonSerializer.Deserialize<List<Filter>>((string)row["filters"]!, PlatformStore.Json)!;
                string? invalidReason = null;
                try { FilterTemplateRules.ValidateFilters(report, filters); }
                catch (ApiException ex) { invalidReason = ex.Message; }
                return new { id = (string)row["id"]!, name = (string)row["name"]!, filters, invalidReason };
            }).ToArray();
            return Ok(new { templates });
        }
        catch (ApiException ex) { return StatusCode(ex.Status, new { error = ex.Message }); }
    }

    [HttpPost]
    public IActionResult Save(string reportId, SaveFilterTemplateRequest request)
    {
        try
        {
            var login = access.RequireLogin(HttpContext);
            lock (store.WriteLock)
            {
                var report = RequireReport(login, reportId, writing: true);
                var name = FilterTemplateRules.Name(request.Name);
                FilterTemplateRules.ValidateFilters(report, request.Filters);
                var existing = store.Query("""
                    SELECT id,name FROM report_filter_templates WHERE reportId=@report AND userId=@user AND accountId=@account
                    """, ("@report", reportId), ("@user", login.User.Id), ("@account", login.Account.Id));
                if (existing.Count >= 30) throw new ApiException("当前报表最多保存 30 个个人模板，请先删除不需要的模板");
                if (existing.Any(row => string.Equals((string)row["name"]!, name, StringComparison.OrdinalIgnoreCase)))
                    throw new ApiException("同名个人模板已存在，请使用其他名称");
                var template = new ReportFilterTemplate(Guid.NewGuid().ToString("N"), name, request.Filters);
                store.Execute("""
                    INSERT INTO report_filter_templates(id,reportId,userId,accountId,name,filters)
                    VALUES(@id,@report,@user,@account,@name,@filters)
                    """, ("@id", template.Id), ("@report", reportId), ("@user", login.User.Id),
                    ("@account", login.Account.Id), ("@name", name), ("@filters", JsonSerializer.Serialize(template.Filters, PlatformStore.Json)));
                store.Audit(login.User, login.Account, report, "save-filter-template", true, detail: template.Id);
                return Ok(template);
            }
        }
        catch (ApiException ex) { return StatusCode(ex.Status, new { error = ex.Message }); }
    }

    [HttpDelete("{id}")]
    public IActionResult Delete(string reportId, string id)
    {
        try
        {
            var login = access.RequireLogin(HttpContext);
            lock (store.WriteLock)
            {
                var report = RequireReport(login, reportId);
                var exists = store.Query("""
                    SELECT id FROM report_filter_templates WHERE id=@id AND reportId=@report AND userId=@user AND accountId=@account
                    """, ("@id", id), ("@report", reportId), ("@user", login.User.Id), ("@account", login.Account.Id));
                if (exists.Count == 0) return NotFound(new { error = "模板不存在或不属于当前用户和账套" });
                store.Execute("DELETE FROM report_filter_templates WHERE id=@id AND userId=@user AND accountId=@account",
                    ("@id", id), ("@user", login.User.Id), ("@account", login.Account.Id));
                store.Audit(login.User, login.Account, report, "delete-filter-template", true, detail: id);
                return Ok(new { ok = true });
            }
        }
        catch (ApiException ex) { return StatusCode(ex.Status, new { error = ex.Message }); }
    }
}
