using System.Security.Cryptography;
using System.Text.Json;
using ReportPlatform.Infrastructure;
using ReportPlatform.Models;

namespace ReportPlatform.Services;

public sealed class ConfigurationService(PlatformStore store, AccessService access, CredentialService credentials)
{
    public Entity Save(string kind, string? id, JsonElement body, User currentUser)
    {
        lock (store.WriteLock)
        {
            if (!PlatformStore.EntityTypes.TryGetValue(kind, out var type)) throw new ApiException("配置类型不存在", 404);
            var old = id is null ? null : store.Find(kind, id) ?? throw new ApiException("记录不存在", 404);
            var entity = (Entity?)body.Deserialize(type, PlatformStore.Json) ?? throw new ApiException("配置无效");
            entity.Id = old?.Id ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(entity.Name) || entity.Name.Length > 150) throw new ApiException("名称不能为空且不能超过 150 字");
            var password = body.TryGetProperty("password", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            switch (entity)
            {
                case User user:
                    if (!UsernamePolicy.IsValid(user.Username)) throw new ApiException(UsernamePolicy.ErrorMessage);
                    if (store.All<User>().Any(u => u.Id != user.Id && u.Username == user.Username)) throw new ApiException("登录名已存在");
                    user.Password = !string.IsNullOrEmpty(password) ? CredentialService.HashPassword(password) : (old as User)?.Password ?? "";
                    if (user.Password == "") throw new ApiException("请设置初始密码");
                    if (user.RoleIds is null || user.AccountIds is null || user.RoleIds.Any(r => store.Find<Role>(r) is null) || user.AccountIds.Any(a => store.Find<Account>(a) is null))
                        throw new ApiException("角色或账套无效");
                    if (old is User previous && access.IsAdmin(previous) && (!user.Enabled || !access.IsAdmin(user)) &&
                        !store.All<User>().Any(u => u.Id != user.Id && u.Enabled && access.IsAdmin(u))) throw new ApiException("必须保留一个启用的管理员");
                    break;
                case Role role:
                    if (old is Role { Admin: true } && !role.Admin) throw new ApiException("内置管理员角色不可降级");
                    role.Admin = (old as Role)?.Admin ?? false;
                    if (role.Permissions is null || role.Permissions.Any(p => store.Find<Report>(p.Key) is null || p.Value is null || p.Value.Any(a => !new[] { "view", "query", "export", "edit" }.Contains(a))))
                        throw new ApiException("报表权限配置无效");
                    break;
                case Account account:
                    if (string.IsNullOrWhiteSpace(account.OrgId) || account.OrgId.Length > 1000) throw new ApiException("组织 ID 不能为空或过长");
                    break;
                case DataSource source:
                    if (string.IsNullOrWhiteSpace(source.Host) || string.IsNullOrWhiteSpace(source.Database) || string.IsNullOrWhiteSpace(source.Username) || source.Port is < 1 or > 65535)
                        throw new ApiException("请填写服务器、有效端口、数据库和账号");
                    source.Secret = !string.IsNullOrEmpty(password) ? credentials.Encrypt(password) : (old as DataSource)?.Secret ?? "";
                    if (source.Secret == "") throw new ApiException("请填写数据库密码");
                    break;
                case Report report:
                    SqlQueryBuilder.Validate(report.Sql);
                    if (string.IsNullOrWhiteSpace(report.OrgColumn)) throw new ApiException("请填写组织 ID 字段，它必须是 SQL 返回的列名或别名，例如 OrgId 或 组织ID。");
                    SqlQueryBuilder.Identifier(report.OrgColumn);
                    if (store.Find<DataSource>(report.ConnectionId) is null) throw new ApiException("请选择数据源");
                    if (report.Fields is null || report.Fields.Count is < 1 or > 100) throw new ApiException("请配置 1–100 个字段");
                    var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (var index = 0; index < report.Fields.Count; index++)
                    {
                        var field = report.Fields[index];
                        if (field is null || string.IsNullOrWhiteSpace(field.Key)) throw new ApiException($"第 {index + 1} 行字段名为空，请填写 SQL 返回的列名或删除该行。");
                        SqlQueryBuilder.Identifier(field.Key);
                        if (!keys.Add(field.Key)) throw new ApiException($"第 {index + 1} 行字段“{field.Key}”重复。");
                        if (string.IsNullOrWhiteSpace(field.Label)) throw new ApiException($"请填写第 {index + 1} 行字段“{field.Key}”的显示名称。");
                    }
                    // An editor can maintain an assigned report but cannot redirect it into a different database.
                    if (!access.IsAdmin(currentUser) && old is Report prior && report.ConnectionId != prior.ConnectionId)
                        throw new ApiException("只有管理员可以更换数据源", 403);
                    break;
            }
            store.Save(entity);
            if (entity is User) store.RevokeUser(entity.Id);
            return entity;
        }
    }
    public void Delete(string kind, string id, User currentUser)
    {
        lock (store.WriteLock)
        {
            var entity = store.Find(kind, id) ?? throw new ApiException("记录不存在", 404);
            switch (entity)
            {
                case User user when user.Id == currentUser.Id || access.IsAdmin(user): throw new ApiException("不能删除当前用户或管理员");
                case Role role when role.Admin || store.All<User>().Any(u => u.RoleIds.Contains(id)): throw new ApiException("角色正在使用，无法删除");
                case Account when store.All<User>().Any(u => u.AccountIds.Contains(id)): throw new ApiException("账套正在使用，无法删除");
                case DataSource when store.All<Report>().Any(r => r.ConnectionId == id): throw new ApiException("数据源正在使用，无法删除");
            }
            store.Delete(kind, id);
            if (entity is User) store.RevokeUser(id);
            if (entity is Report)
                foreach (var role in store.All<Role>().Where(r => r.Permissions.Remove(id))) store.Save(role);
        }
    }
}
