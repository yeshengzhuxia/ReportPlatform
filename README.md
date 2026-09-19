# 澄数报表平台 · ASP.NET Core / VS2022

后端已迁移到 **C# + ASP.NET Core 8**。使用 **VS2022 17.11 或更新版本**，安装“ASP.NET 和 Web 开发”工作负载及 .NET 8 SDK。界面为应用自带的 HTML/CSS/JavaScript，由 ASP.NET Core 同源提供，**编译、发布和服务器运行均不需要 Node.js/npm**。

## 在 VS2022 中运行

1. 打开根目录 `ReportPlatform.sln`。
2. 将 `ReportPlatform.Web` 设为启动项目，等待 NuGet 自动还原。
3. 选择 `ReportPlatform` 启动配置，按 F5 或 Ctrl+F5。
4. 浏览器进入 `http://localhost:5080`。
5. 当前目录已有旧版数据时，继续使用 `首次登录.txt` 中的 admin 密码和原账套。新安装先运行根目录 `setup.ps1` 设置初始密码及密钥。

本仓库未固定 SDK 补丁版本，可使用 VS2022 安装的兼容 .NET 8 SDK；使用 C# 12 语法。默认端口为 5080。

## 发布到 IIS / Windows

1. 右键 `ReportPlatform.Web` → **发布**。
2. 选择提供的 **FolderProfile** 文件夹发布配置，点击发布。
3. 输出为根目录 `artifacts/publish`，包含 DLL、静态页面、依赖和 IIS `web.config`。
4. 在目标服务器安装 **.NET 8 Hosting Bundle** 并重启 IIS。
5. 将发布目录复制到 IIS 站点目录，例如 `D:\Sites\ReportPlatform`。
6. 新站点在发布目录执行 `powershell -ExecutionPolicy Bypass -File .\setup.ps1`。已有站点必须保留原配置、密钥和数据库，不要重新初始化。
7. IIS 应用程序池设置“无托管代码”，独立应用程序池，**最大工作进程数为 1**，站点物理路径指向发布目录，绑定 HTTPS。
8. 应用程序池身份对站点文件有读取权限，对 `App_Data` 有修改权限；`appsettings.Local.json` 仅允许管理员和服务身份读取。

发布采用框架依赖模式，需要 .NET 8 ASP.NET Core 运行时，IIS 使用 Hosting Bundle。发布配置不清空目标目录，也不打包密钥、用户数据库或登录信息。不要直接把源码目录作为静态网站发布。

命令行等效流程：

```powershell
dotnet restore ReportPlatform.sln
dotnet build ReportPlatform.sln -c Release --no-restore
dotnet test ReportPlatform.sln -c Release --no-build
dotnet publish src/ReportPlatform.Web/ReportPlatform.Web.csproj -c Release /p:PublishProfile=FolderProfile
```

发布文件也可用 `dotnet ReportPlatform.Web.dll --urls http://127.0.0.1:5080` 启动。临时 HTTP 测试需设置环境变量 `Platform__CookieSecure=false`，正式 HTTPS 保持 true。F5 启动配置已为本机调试设为 false。

## 配置与旧版迁移

### 本目录自动兼容

源码运行检测到根目录 `ReportPlatform.sln` 和 `data/platform.sqlite` 时默认复用原数据库，密钥及初始密码可从根目录 `.env` 读取。显式设置 `Platform:DataDirectory` 为其他路径时使用指定位置。

- 用户、角色、账套、SQL、字段、数据源和查询日志保留，SQLite 表结构兼容旧版。
- 原 scrypt 密码继续验证；新建和重置密码使用 PBKDF2-SHA512。
- 数据源密码保持 AES-256-GCM 格式，必须保留原 ENCRYPTION_KEY。
- 迁移时停止旧 Node 服务，避免两个版本同时修改同一数据库。
- 原 Node 源码归档到 `legacy/node`，不参与 .NET 编译或发布。

### 迁移到另一台服务器

1. 停止原服务，备份完整 `data` 目录及 `.env`。
2. 将停机后的 data 目录内容复制到新发布目录 `App_Data`。如存在 SQLite WAL 文件，必须完整复制，不能只复制主数据库。
3. 在发布目录创建 `appsettings.Local.json`，填入 **原 .env 的 ENCRYPTION_KEY**，不要生成新密钥。已有数据库无需 AdminPassword。

```json
{
  "Platform": {
    "DataDirectory": "App_Data",
    "EncryptionKey": "填入原来的64位十六进制密钥",
    "AdminPassword": "",
    "CookieSecure": true
  }
}
```

发布环境不搜索旧源码/.env，只读取自身配置和环境变量。配置优先级为命令行、环境变量、`appsettings.Local.json`、普通应用配置。环境变量可使用 `Platform__EncryptionKey`、`Platform__DataDirectory`。新安装首次启动完成后可移除 AdminPassword，但不能移除 EncryptionKey。

## 功能与使用顺序

1. **数据源**：配置多个 SQL Server 服务器、端口、数据库、只读账号，并测试连接。密码加密保存，接口不回显密码及密文。
2. **账套**：维护账套及组织 ID。同一报表访问同一数据库，不同账套由服务端限定不同组织 ID。
3. **报表**：维护 SELECT、组织字段、显示字段、中文名称、分类和启用状态。
4. **角色权限**：逐报表控制查看、查询、导出、SQL/字段配置，服务端逐次验证；多角色取并集。报表编辑者只有配置权限时不可更换数据源。
5. **用户**：创建/停用、重置密码、分配角色和账套。用户修改后撤销现有会话。
6. **查询导出**：每个配置字段均可组合筛选，支持包含、等于、不等于、大小比较、为空；多个条件为 AND，范围可用两个比较条件。导出 UTF-8 BOM CSV。
7. **审计**：按北京时间、用户、报表、账套统计每日查询及导出次数（含失败尝试），保留成功/失败明细。

SQL 示例：

```sql
SELECT OrgId, OrderNo, CustomerName, OrderDate, Amount
FROM dbo.SalesOrders
```

组织字段填写 `OrgId`，显示字段例如 `OrderNo → 单据编号`、`Amount → 金额`。组织字段必须在 SQL 返回列中，可不显示。SQL 包装为派生表，外层强制 `OrgId = @org`；组织 ID 来自服务端登录账套，浏览器不能覆盖，所有筛选值也使用参数。

## 代码结构

```text
ReportPlatform.sln
src/ReportPlatform.Web/
  Controllers/       HTTP 接口：登录、管理、报表、审计
  Models/            用户、角色、账套、数据源、报表模型
  Services/          权限、密码、配置、SQL 构造及数据库执行
  Infrastructure/    SQLite 仓储、审计、兼容配置读取
  wwwroot/           报表管理界面
  Properties/        VS2022 调试及发布配置
tests/ReportPlatform.Tests/   .NET 安全和 HTTP 集成测试
legacy/node/                 旧版源码参考
```

配置及审计保存在 SQLite，业务报表由 Microsoft.Data.SqlClient 连接 SQL Server。当前为单机单工作进程架构。

## 当前边界

- 仅支持可作派生表的单条 SELECT，不支持存储过程、CTE、临时表、多语句、注释和分号；请移除 ORDER BY。SQL 字段名支持中文、空格及别名，最多 128 个字符，由后端加方括号并安全转义。配置中填写返回列名本身，不加表名前缀或额外的方括号。
- 数据源必须是只读账号，仅授予所需表/视图的 SELECT 权限。ApplicationIntent 和语句检查不替代数据库授权。
- SQL 编辑者是可信角色，其能改写组织字段表达式；严格隔离还应结合 SQL Server 行级安全或受控视图。
- 查询显示最多 500 行，导出最多 10000 行，超限提示缩小范围；查询超时 30 秒。CSV 防护公式注入。
- 会话有效 8 小时，HttpOnly、SameSite=Strict，生产默认 Secure。登录失败按来源 IP 限制 15 分钟 10 次；反向代理可能共享来源限流，不信任任意转发头。
- 暂不含 SSO/MFA、邮件找回密码、异步大文件导出、分页排序和审计归档。
- 尚无真实 SQL Server 连接信息，仍需实际表结构、证书、组织归属和只读账号联调。HTTP 集成测试使用替身查询执行器，不表示真实数据库已验证。

官方兼容性参考：[Microsoft .NET 与 Visual Studio 版本要求](https://learn.microsoft.com/en-us/dotnet/core/compatibility/sdk/8.0/version-requirements)。

## 本次验证

- 使用本机 VS2022 17.11 的 MSBuild 完成 Release 编译及 FolderProfile 文件夹发布。
- 13 项 .NET 测试全部通过：旧 scrypt/AES 格式兼容、SQL 参数化、CSV 公式防护、HTTP 登录、账套授权、按钮权限、查询及导出限额、审计与会话撤销。
- `scripts/Test-Publish.ps1` 验证发布目录独立启动、静态资源、登录、账套和管理员权限，并检查发布目录不含私有配置。该脚本使用独立测试数据库并自动停止测试程序。
- 依赖漏洞检查未报告已知漏洞；SQLite 原生依赖显式升级到 2.1.13，并锁定在 packages.lock.json。
- 真实 SQL Server 和目标 IIS 服务器尚未联调。
