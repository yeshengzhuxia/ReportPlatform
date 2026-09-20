using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using ReportPlatform.Infrastructure;
using ReportPlatform.Models;
using ReportPlatform.Services;
using Xunit;
using Xunit.Abstractions;

namespace ReportPlatform.Tests;

public sealed class LiveSqlFactAttribute : FactAttribute
{
    public LiveSqlFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("REPORT_PLATFORM_SQL_CHECK_ROOT")))
            Skip = "Set REPORT_PLATFORM_SQL_CHECK_ROOT to explicitly enable read-only checks against a configured SQL Server.";
    }
}

public sealed class SqlServerPagingTests(ITestOutputHelper output)
{
    [LiveSqlFact]
    public async Task SchemaReaderIsClosedBeforeCountAndPageCommands()
    {
        var (executor, source, _, _) = Load();
        // Constant rows make this a repeatable check without modifying any business data.
        var report = new Report
        {
            Sql = "SELECT v.TenantId,v.Id FROM (VALUES ('A',1),('A',2),('A',3),('B',4)) v(TenantId,Id)",
            OrgColumn = "TenantId", Fields = [new("Id", "编号")]
        };
        var query = SqlQueryBuilder.Build(report, "A", []);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var first = await executor.QueryPageAsync(source, query, 1, 2, timeout.Token);
        var second = await executor.QueryPageAsync(source, query, 2, 2, timeout.Token);
        Assert.Equal(3L, first.Total);
        Assert.Equal(3L, second.Total);
        Assert.Equal(new[] { 1, 2 }, first.Rows.Select(r => Convert.ToInt32(r["Id"])));
        Assert.Equal(3, Convert.ToInt32(Assert.Single(second.Rows)["Id"]));
        var exported = new List<int>();
        await foreach (var row in executor.StreamAsync(source, query, timeout.Token))
            exported.Add(Convert.ToInt32(row["Id"]));
        Assert.Equal(new[] { 1, 2, 3 }, exported);
        output.WriteLine("Real SQL Server: schema, count, two pages, organization filter and export passed.");
    }

    [LiveSqlFact]
    public async Task ConfiguredReportCanReadFirstAndSecondPages()
    {
        var (executor, source, report, account) = Load();
        var query = SqlQueryBuilder.Build(report, account.OrgId, []);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var first = await executor.QueryPageAsync(source, query, 1, 20, timeout.Token);
        Assert.Equal(Math.Min(20L, first.Total), (long)first.Rows.Count);
        output.WriteLine($"Configured report: first page {first.Rows.Count} rows, total {first.Total}.");
        if (first.Total > 20)
        {
            var second = await executor.QueryPageAsync(source, query, 2, 20, timeout.Token);
            Assert.Equal(Math.Min(20L, second.Total - 20), (long)second.Rows.Count);
            output.WriteLine($"Configured report: second page {second.Rows.Count} rows.");
        }
    }

    private static (SqlServerReportExecutor, DataSource, Report, Account) Load()
    {
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("REPORT_PLATFORM_SQL_CHECK_ROOT")!);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = Path.Combine(root, "src", "ReportPlatform.Web"), EnvironmentName = "Testing"
        });
        var settings = PlatformSettings.Load(builder);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(settings.DataDirectory, "platform.sqlite"),
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        connection.Open();
        List<T> Read<T>(string kind)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT body FROM items WHERE type=@kind";
            command.Parameters.AddWithValue("@kind", kind);
            using var reader = command.ExecuteReader();
            var result = new List<T>();
            while (reader.Read()) result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), PlatformStore.Json)!);
            return result;
        }
        var report = Read<Report>("reports").First(r => r.Enabled);
        var source = Read<DataSource>("connections").Single(s => s.Id == report.ConnectionId);
        var accounts = Read<Account>("accounts").Where(a => a.Enabled).ToList();
        var account = accounts.FirstOrDefault(a => a.Name == "kmt") ?? accounts.First();
        return (new(new CredentialService(settings)), source, report, account);
    }
}
