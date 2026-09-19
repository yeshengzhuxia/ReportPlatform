using System.Text.Json;
using System.Text.RegularExpressions;
using ReportPlatform.Models;

namespace ReportPlatform.Services;

public sealed record PreparedQuery(string Text, IReadOnlyDictionary<string, string> Parameters,
    string? SchemaSql = null, string? OrgColumn = null, IReadOnlyList<string>? Fields = null);

public static partial class SqlQueryBuilder
{
    [GeneratedRegex(@"^\s*select\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SelectPattern();
    [GeneratedRegex(@"[;]|--|/\*|\*/|\b(insert|update|delete|drop|alter|create|exec|execute|merge|truncate|into|openrowset|opendatasource|openquery|bulk|waitfor|use|grant|revoke|deny|dbcc)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForbiddenPattern();
    public static string Identifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl))
            throw new ApiException("字段名不能为空，最多 128 个字符，不能包含换行等控制字符；请填写 SQL 返回的列名或别名。");
        // SQL Server delimited identifiers: escape the closing delimiter, never concatenate raw names.
        // This supports Chinese names, aliases, spaces and literal brackets without executable SQL.
        return "[" + value.Replace("]", "]]") + "]";
    }
    public static void Validate(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql) || sql.Length > 100_000 || !SelectPattern().IsMatch(sql) || ForbiddenPattern().IsMatch(sql))
            throw new ApiException("仅允许单条 SELECT，不允许注释、分号或写入操作");
    }
    public static PreparedQuery Build(Report report, string orgId, IReadOnlyList<Filter>? filters, int limit)
    {
        Validate(report.Sql);
        if (report.Fields.Count == 0) throw new ApiException("请配置报表字段");
        filters ??= [];
        if (filters.Count > 50) throw new ApiException("筛选条件最多 50 个");
        var parameters = new Dictionary<string, string> { ["org"] = orgId };
        var where = new List<string> { $"q.{Identifier(report.OrgColumn)} = @org" };
        var operators = new Dictionary<string, string> { ["eq"] = "=", ["ne"] = "<>", ["gt"] = ">", ["gte"] = ">=", ["lt"] = "<", ["lte"] = "<=" };
        for (var i = 0; i < filters.Count; i++)
        {
            var filter = filters[i];
            if (!report.Fields.Any(f => f.Key == filter.Field)) throw new ApiException("未知筛选字段");
            var column = $"q.{Identifier(filter.Field)}"; var parameter = $"p{i}";
            if (filter.Op == "empty") { where.Add($"{column} IS NULL"); continue; }
            if (filter.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object) throw new ApiException("筛选值必须是单个值");
            var value = filter.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "" : filter.Value.ToString();
            if (value.Length > 1000) throw new ApiException("筛选值过长");
            if (filter.Op == "contains")
            {
                where.Add($"{column} LIKE @{parameter} ESCAPE '~'");
                parameters[parameter] = "%" + value.Replace("~", "~~").Replace("%", "~%").Replace("_", "~_").Replace("[", "~[") + "%";
            }
            else
            {
                if (!operators.TryGetValue(filter.Op, out var op)) throw new ApiException("不支持的筛选方式");
                where.Add($"{column} {op} @{parameter}"); parameters[parameter] = value;
            }
        }
        var fields = string.Join(",", report.Fields.Select(f => $"q.{Identifier(f.Key)}"));
        return new($"SELECT TOP ({Math.Clamp(limit, 1, 10001)}) {fields} FROM ({report.Sql}) AS q WHERE {string.Join(" AND ", where)}", parameters,
            $"SELECT TOP (0) * FROM ({report.Sql}) AS q", report.OrgColumn, report.Fields.Select(f => f.Key).ToArray());
    }
}
