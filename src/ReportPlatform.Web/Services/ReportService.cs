using System.Data;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using ReportPlatform.Models;

namespace ReportPlatform.Services;

public sealed record PagedQueryResult(IReadOnlyList<Dictionary<string, object?>> Rows, long Total);

public interface IReportExecutor
{
    Task TestAsync(DataSource source, CancellationToken cancellationToken);
    Task<PagedQueryResult> QueryPageAsync(DataSource source, PreparedQuery query, int page, int pageSize, CancellationToken cancellationToken);
    IAsyncEnumerable<Dictionary<string, object?>> StreamAsync(DataSource source, PreparedQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<ReportField>> PreviewFieldsAsync(DataSource source, string sql, CancellationToken cancellationToken);
}
public sealed class SqlServerReportExecutor(CredentialService credentials) : IReportExecutor
{
    private SqlConnection Connection(DataSource source)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = $"{source.Host},{source.Port}", InitialCatalog = source.Database,
            UserID = source.Username, Password = credentials.Decrypt(source.Secret),
            Encrypt = SqlConnectionEncryptOption.Mandatory, TrustServerCertificate = source.TrustCertificate,
            ConnectTimeout = 10, ApplicationIntent = ApplicationIntent.ReadOnly,
            ApplicationName = "ReportPlatform", MaxPoolSize = 20
        };
        return new SqlConnection(builder.ConnectionString);
    }
    public async Task TestAsync(DataSource source, CancellationToken cancellationToken)
    {
        await using var connection = Connection(source); await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("SELECT 1", connection) { CommandTimeout = 10 };
        await command.ExecuteScalarAsync(cancellationToken);
    }
    public async Task<PagedQueryResult> QueryPageAsync(DataSource source, PreparedQuery query, int page, int pageSize, CancellationToken cancellationToken)
    {
        await using var connection = Connection(source); await connection.OpenAsync(cancellationToken);
        // Dispose the schema reader before reusing this connection for count and page queries.
        await using (var schemaCommand = new SqlCommand(query.SchemaSql, connection) { CommandTimeout = 30 })
        await using (var schemaReader = await schemaCommand.ExecuteReaderAsync(cancellationToken))
        {
            ReportColumns.Validate(query, Enumerable.Range(0, schemaReader.FieldCount).Select(schemaReader.GetName));
        }
        await using var count = new SqlCommand(query.CountSql, connection) { CommandTimeout = 30 };
        foreach (var (name, value) in query.Parameters) count.Parameters.Add("@" + name, SqlDbType.NVarChar, 4000).Value = value;
        var total = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        await using var command = new SqlCommand($"{query.DataSql} ORDER BY {query.OrderBySql} OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY", connection) { CommandTimeout = 30 };
        foreach (var (name, value) in query.Parameters) command.Parameters.Add("@" + name, SqlDbType.NVarChar, 4000).Value = value;
        command.Parameters.Add("@offset", SqlDbType.BigInt).Value = (long)(page - 1) * pageSize;
        command.Parameters.Add("@pageSize", SqlDbType.Int).Value = pageSize;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                // Keep the configured spelling: SQL Server can return MoCode for a configured Mocode.
                var key = query.Fields.Count == reader.FieldCount ? query.Fields[i] : reader.GetName(i);
                row[key] = await reader.IsDBNullAsync(i, cancellationToken) ? null : reader.GetValue(i);
            }
            rows.Add(row);
        }
        return new(rows, total);
    }

    public async IAsyncEnumerable<Dictionary<string, object?>> StreamAsync(DataSource source, PreparedQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = Connection(source); await connection.OpenAsync(cancellationToken);
        await using (var schemaCommand = new SqlCommand(query.SchemaSql, connection) { CommandTimeout = 30 })
        await using (var schemaReader = await schemaCommand.ExecuteReaderAsync(cancellationToken))
            ReportColumns.Validate(query, Enumerable.Range(0, schemaReader.FieldCount).Select(schemaReader.GetName));
        await using var command = new SqlCommand($"{query.DataSql} ORDER BY {query.OrderBySql}", connection) { CommandTimeout = 0 };
        foreach (var (name, value) in query.Parameters) command.Parameters.Add("@" + name, SqlDbType.NVarChar, 4000).Value = value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++) row[query.Fields.Count == reader.FieldCount ? query.Fields[i] : reader.GetName(i)] = await reader.IsDBNullAsync(i, cancellationToken) ? null : reader.GetValue(i);
            yield return row;
        }
    }

    public async Task<IReadOnlyList<ReportField>> PreviewFieldsAsync(DataSource source, string sql, CancellationToken cancellationToken)
    {
        await using var connection = Connection(source); await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand($"SELECT TOP (0) * FROM ({sql}) AS q", connection) { CommandTimeout = 30 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return SqlAliases.ToFields(sql, Enumerable.Range(0, reader.FieldCount).Select(reader.GetName));
    }
}
public static class ReportColumns
{
    public static void Validate(PreparedQuery query, IEnumerable<string> columnNames)
    {
        var columns = columnNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(query.OrgColumn) && !columns.Contains(query.OrgColumn))
            throw new ApiException($"组织 ID 字段“{query.OrgColumn}”不在 SQL 返回列中。这里应填写数据库列名（例如 OrgId 或 TenantId），不是账套组织 ID 值；请在报表设置中更正。不能确定时请联系管理员确认组织对应字段。");
        var missing = query.Fields?.Where(f => !columns.Contains(f)).ToArray() ?? [];
        if (missing.Length > 0)
            throw new ApiException($"以下字段不在 SQL 返回列中：{string.Join("、", missing)}。请填写 SQL 返回的列名或别名，中文显示名称在右侧单独设置。");
    }
}

public static partial class SqlAliases
{
    [GeneratedRegex("\\bAS\\s+(?:\\[(?<bracket>(?:[^\\]]|\\]\\])*)\\]|\"(?<quote>[^\"]+)\"|(?<plain>[\\p{L}_][\\p{L}\\p{N}_]*))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AliasPattern();

    public static IReadOnlyList<ReportField> ToFields(string sql, IEnumerable<string> columns)
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in AliasPattern().Matches(sql))
        {
            var alias = match.Groups["bracket"].Success ? match.Groups["bracket"].Value.Replace("]]", "]")
                : match.Groups["quote"].Success ? match.Groups["quote"].Value : match.Groups["plain"].Value;
            if (alias.Length > 0) aliases[alias] = alias;
        }
        return columns.Select(column => new ReportField(column, aliases.GetValueOrDefault(column, ""))).ToArray();
    }
}

public static class SqlFailureMessages
{
    public static string ForNumber(int number) => number switch
    {
        207 => "SQL 引用了不存在的列，请核对 SQL 本身、组织字段和显示字段的列名或别名（SQL Server 207）。",
        208 => "SQL 中的表或视图不存在，请核对所选数据库和表名（SQL Server 208）。",
        229 => "数据源账号没有读取相关表或视图的权限，请授予所需 SELECT 权限（SQL Server 229）。",
        1033 => "报表 SQL 包装为子查询后不支持当前 ORDER BY，请移除末尾排序后重试（SQL Server 1033）。",
        102 or 156 => $"报表 SQL 语法有误，或不能作为单条 SELECT 子查询使用（SQL Server {number}）。",
        245 or 8114 => $"组织 ID 或筛选值与数据库字段类型不匹配，请检查字段和值（SQL Server {number}）。",
        -2 => "数据库查询超时，请缩小查询范围或优化 SQL。",
        _ => $"数据库查询失败（SQL Server {number}），请管理员检查表权限、SQL 和连接状态。"
    };
}
public static class CsvExport
{
    public static string Cell(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        // Spreadsheet applications may ignore initial spaces before a formula.
        var trimmed = text.TrimStart();
        if ((trimmed.Length > 0 && "=+-@".Contains(trimmed[0])) || text.StartsWith('\t') || text.StartsWith('\r') || text.StartsWith('\n')) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
    public static string Header(Report report) => string.Join(',', report.Fields.Select(f => Cell(f.Label)));
    public static string Row(Report report, IReadOnlyDictionary<string, object?> row) => string.Join(',', report.Fields.Select(f => Cell(row.GetValueOrDefault(f.Key))));
}
