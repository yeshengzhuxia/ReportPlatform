using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using ReportPlatform.Models;

namespace ReportPlatform.Services;

public interface IReportExecutor
{
    Task TestAsync(DataSource source, CancellationToken cancellationToken);
    Task<List<Dictionary<string, object?>>> QueryAsync(DataSource source, PreparedQuery query, CancellationToken cancellationToken);
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
    public async Task<List<Dictionary<string, object?>>> QueryAsync(DataSource source, PreparedQuery query, CancellationToken cancellationToken)
    {
        await using var connection = Connection(source); await connection.OpenAsync(cancellationToken);
        if (query.SchemaSql is not null)
        {
            await using var schemaCommand = new SqlCommand(query.SchemaSql, connection) { CommandTimeout = 30 };
            await using var schemaReader = await schemaCommand.ExecuteReaderAsync(cancellationToken);
            ReportColumns.Validate(query, Enumerable.Range(0, schemaReader.FieldCount).Select(schemaReader.GetName));
        }
        await using var command = new SqlCommand(query.Text, connection) { CommandTimeout = 30 };
        foreach (var (name, value) in query.Parameters) command.Parameters.Add("@" + name, SqlDbType.NVarChar, 4000).Value = value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                // Keep the configured spelling: SQL Server can return MoCode for a configured Mocode.
                var key = query.Fields is { } fields && fields.Count == reader.FieldCount ? fields[i] : reader.GetName(i);
                row[key] = await reader.IsDBNullAsync(i, cancellationToken) ? null : reader.GetValue(i);
            }
            rows.Add(row);
        }
        return rows;
    }
}
public static class ReportColumns
{
    public static void Validate(PreparedQuery query, IEnumerable<string> columnNames)
    {
        var columns = columnNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (query.OrgColumn is not null && !columns.Contains(query.OrgColumn))
            throw new ApiException($"组织 ID 字段“{query.OrgColumn}”不在 SQL 返回列中。这里应填写数据库列名（例如 OrgId 或 TenantId），不是账套组织 ID 值；请在报表设置中更正。不能确定时请联系管理员确认组织对应字段。");
        var missing = query.Fields?.Where(f => !columns.Contains(f)).ToArray() ?? [];
        if (missing.Length > 0)
            throw new ApiException($"以下字段不在 SQL 返回列中：{string.Join("、", missing)}。请填写 SQL 返回的列名或别名，中文显示名称在右侧单独设置。");
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
    public static byte[] Create(Report report, IEnumerable<Dictionary<string, object?>> rows)
    {
        var builder = new StringBuilder("\ufeff");
        builder.AppendLine(string.Join(',', report.Fields.Select(f => Cell(f.Label))));
        foreach (var row in rows) builder.AppendLine(string.Join(',', report.Fields.Select(f => Cell(row.GetValueOrDefault(f.Key)))));
        return Encoding.UTF8.GetBytes(builder.ToString());
    }
}
