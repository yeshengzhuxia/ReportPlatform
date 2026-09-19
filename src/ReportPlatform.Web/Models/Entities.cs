using System.Text.Json;

namespace ReportPlatform.Models;

public abstract class Entity
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}
public sealed class User : Entity
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public List<string> RoleIds { get; set; } = [];
    public List<string> AccountIds { get; set; } = [];
    public bool Enabled { get; set; }
}
public sealed class Role : Entity
{
    public bool Admin { get; set; }
    public Dictionary<string, List<string>> Permissions { get; set; } = [];
}
public sealed class Account : Entity
{
    public string OrgId { get; set; } = "";
    public bool Enabled { get; set; }
}
public sealed class DataSource : Entity
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 1433;
    public string Database { get; set; } = "";
    public string Username { get; set; } = "";
    public string Secret { get; set; } = "";
    public bool TrustCertificate { get; set; }
}
public sealed class Report : Entity
{
    public string ConnectionId { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public string Sql { get; set; } = "";
    public string OrgColumn { get; set; } = "";
    public List<ReportField> Fields { get; set; } = [];
    public bool Enabled { get; set; }
}
public sealed record ReportField(string Key, string Label);
public sealed record Filter(string Field, string Op, JsonElement Value);
public sealed record QueryRequest(List<Filter>? Filters);
public sealed record LoginRequest(string Username, string Password, string? AccountId);
public sealed record LoginContext(User User, Account Account, string Token);
public sealed record Session(string UserId, string AccountId);
public sealed class ApiException(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}
