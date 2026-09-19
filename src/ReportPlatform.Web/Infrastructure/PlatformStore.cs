using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using ReportPlatform.Models;

namespace ReportPlatform.Infrastructure;

public sealed class PlatformStore
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };
    public static readonly IReadOnlyDictionary<string, Type> EntityTypes = new Dictionary<string, Type>
    {
        ["users"] = typeof(User), ["roles"] = typeof(Role), ["accounts"] = typeof(Account),
        ["connections"] = typeof(DataSource), ["reports"] = typeof(Report)
    };
    private readonly string connectionString;
    // Serialize configuration writes and last-administrator checks within this single-process app.
    public object WriteLock { get; } = new();

    public PlatformStore(PlatformSettings settings)
    {
        Directory.CreateDirectory(settings.DataDirectory);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(settings.DataDirectory, "platform.sqlite"), DefaultTimeout = 30
        }.ToString();
        Execute("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS items(type TEXT,id TEXT,body TEXT,PRIMARY KEY(type,id));
            CREATE TABLE IF NOT EXISTS sessions(token TEXT PRIMARY KEY,userId TEXT,accountId TEXT,expires INTEGER);
            CREATE TABLE IF NOT EXISTS audit(id INTEGER PRIMARY KEY,at TEXT,userId TEXT,username TEXT,accountId TEXT,reportId TEXT,reportName TEXT,action TEXT,success INTEGER,rows INTEGER,duration INTEGER,detail TEXT);
            CREATE INDEX IF NOT EXISTS ix_audit_at ON audit(at);
            """);
    }
    private SqliteConnection Open() { var connection = new SqliteConnection(connectionString); connection.Open(); return connection; }
    public void Execute(string sql, params (string Name, object? Value)[] values)
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
    public List<Dictionary<string, object?>> Query(string sql, params (string Name, object? Value)[] values)
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using var reader = command.ExecuteReader(); var rows = new List<Dictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }
    private static string Kind<T>() => EntityTypes.Single(p => p.Value == typeof(T)).Key;
    public List<T> All<T>() where T : Entity => All(Kind<T>()).Cast<T>().ToList();
    public List<Entity> All(string kind) => Query("SELECT body FROM items WHERE type=@type", ("@type", kind))
        .Select(row => (Entity)JsonSerializer.Deserialize((string)row["body"]!, EntityTypes[kind], Json)!).ToList();
    public T? Find<T>(string id) where T : Entity => Find(Kind<T>(), id) as T;
    public Entity? Find(string kind, string id)
    {
        var row = Query("SELECT body FROM items WHERE type=@type AND id=@id", ("@type", kind), ("@id", id)).FirstOrDefault();
        return row is null ? null : (Entity?)JsonSerializer.Deserialize((string)row["body"]!, EntityTypes[kind], Json);
    }
    public void Save(Entity entity)
    {
        var kind = EntityTypes.Single(p => p.Value == entity.GetType()).Key;
        Execute("INSERT OR REPLACE INTO items VALUES(@type,@id,@body)", ("@type", kind), ("@id", entity.Id),
            ("@body", JsonSerializer.Serialize(entity, entity.GetType(), Json)));
    }
    public void Delete(string kind, string id) => Execute("DELETE FROM items WHERE type=@type AND id=@id", ("@type", kind), ("@id", id));
    public static JsonObject Public(Entity entity)
    {
        var json = (JsonObject)JsonSerializer.SerializeToNode(entity, entity.GetType(), Json)!;
        json.Remove("password"); json.Remove("secret"); return json;
    }
    public void AddSession(string token, string userId, string accountId)
    {
        Execute("DELETE FROM sessions WHERE expires < @now", ("@now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        Execute("INSERT INTO sessions VALUES(@token,@user,@account,@expires)", ("@token", token), ("@user", userId),
            ("@account", accountId), ("@expires", DateTimeOffset.UtcNow.AddHours(8).ToUnixTimeMilliseconds()));
    }
    public Session? FindSession(string token)
    {
        var row = Query("SELECT userId,accountId FROM sessions WHERE token=@token AND expires>@now", ("@token", token),
            ("@now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())).FirstOrDefault();
        return row is null ? null : new((string)row["userId"]!, (string)row["accountId"]!);
    }
    public void RevokeUser(string userId) => Execute("DELETE FROM sessions WHERE userId=@user", ("@user", userId));
    public void RevokeSession(string token) => Execute("DELETE FROM sessions WHERE token=@token", ("@token", token));
    public void Audit(User user, Account? account, Report? report, string action, bool success, int rows = 0, long duration = 0, string detail = "") =>
        Execute("""
            INSERT INTO audit(at,userId,username,accountId,reportId,reportName,action,success,rows,duration,detail)
            VALUES(@at,@user,@username,@account,@report,@reportName,@action,@success,@rows,@duration,@detail)
            """, ("@at", DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)),
            ("@user", user.Id), ("@username", user.Username), ("@account", account?.Id ?? ""), ("@report", report?.Id ?? ""),
            ("@reportName", report?.Name ?? ""), ("@action", action), ("@success", success ? 1 : 0),
            ("@rows", rows), ("@duration", duration), ("@detail", detail));
}
