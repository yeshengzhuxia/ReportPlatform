using ReportPlatform.Models;

namespace ReportPlatform.Services;

public static class FilterTemplateRules
{
    public static string Name(string? value)
    {
        var name = value?.Trim() ?? "";
        if (name.Length is < 1 or > 80) throw new ApiException("模板名称应为 1–80 个字符");
        return name;
    }

    public static void ValidateFilters(Report report, IReadOnlyList<Filter>? filters)
    {
        if (filters is null || filters.Count > 50 || filters.Any(f => f is null))
            throw new ApiException("模板条件无效，最多支持 50 个条件");
        // Reuse exactly the same field/operator/value validation as an actual query.
        // This only builds parameterized SQL; no database query is executed here.
        SqlQueryBuilder.Build(report, "", filters);
    }

    public static void ValidatePresets(Report report)
    {
        if (report.FilterTemplates is null || report.FilterTemplates.Count > 30)
            throw new ApiException("每个报表最多配置 30 个预设模板");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < report.FilterTemplates.Count; i++)
        {
            var template = report.FilterTemplates[i];
            if (template is null || string.IsNullOrEmpty(template.Id) || template.Id.Length > 64 ||
                template.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_') || !ids.Add(template.Id))
                throw new ApiException("预设模板标识无效或重复");
            var name = Name(template.Name);
            if (!names.Add(name)) throw new ApiException("预设模板名称不能重复");
            try { ValidateFilters(report, template.Filters); }
            catch (ApiException ex) { throw new ApiException($"模板“{name}”：{ex.Message}。请同步调整模板条件。"); }
            report.FilterTemplates[i] = template with { Name = name };
        }
        report.DefaultFilterTemplateId ??= "";
        if (report.DefaultFilterTemplateId != "" && !ids.Contains(report.DefaultFilterTemplateId))
            throw new ApiException("默认模板不存在，请重新选择默认模板");
    }
}
