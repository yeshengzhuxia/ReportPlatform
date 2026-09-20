using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using ReportPlatform.Infrastructure;
using ReportPlatform.Models;
using ReportPlatform.Services;

var builder = WebApplication.CreateBuilder(args);
// Do not require Windows Event Log registration/write privileges for normal application requests.
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables().AddCommandLine(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1024 * 1024);
builder.Services.Configure<IISServerOptions>(options => options.MaxRequestBodySize = 1024 * 1024);
builder.Services.AddSingleton(PlatformSettings.Load(builder));
builder.Services.AddSingleton<PlatformStore>();
builder.Services.AddSingleton<CredentialService>();
builder.Services.AddSingleton<AccessService>();
builder.Services.AddSingleton<ConfigurationService>();
builder.Services.AddSingleton<IReportExecutor, SqlServerReportExecutor>();
builder.Services.AddControllers().ConfigureApiBehaviorOptions(options =>
    options.InvalidModelStateResponseFactory = _ => new BadRequestObjectResult(new { error = "请求格式错误，请检查填写内容" }));
var app = builder.Build();
// Resolve the key before opening/initializing storage so a missing key cannot leave a partial installation.
_ = app.Services.GetRequiredService<CredentialService>();
var store = app.Services.GetRequiredService<PlatformStore>();
if (store.All<User>().Count == 0)
{
    var password = CredentialService.HashPassword(app.Services.GetRequiredService<PlatformSettings>().AdminPassword);
    store.Save(new Role { Id = "admin", Name = "系统管理员", Admin = true });
    store.Save(new Account { Id = "default", Name = "默认账套", OrgId = "1", Enabled = true });
    store.Save(new User { Id = "admin", Name = "系统管理员", Username = "admin", Password = password, RoleIds = ["admin"], AccountIds = ["default"], Enabled = true });
}
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; frame-ancestors 'none'";
    try
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && context.Request.Headers["X-Requested-With"] != "ReportPlatform")
                throw new ApiException("请求校验失败", 403);
        }
        await next();
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { context.Abort(); }
    catch (Exception ex)
    {
        if (context.Response.HasStarted) { context.Abort(); return; }
        var status = ex switch { ApiException api => api.Status, JsonException => 400, BadHttpRequestException bad => bad.StatusCode, _ => 500 };
        var message = ex switch { ApiException api => api.Message, JsonException => "请求格式错误", BadHttpRequestException => "请求无效或过大", _ => "操作失败，请联系管理员检查服务配置" };
        if (status == 500) app.Logger.LogError("Request failed: {Type}", ex.GetType().Name);
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { error = message });
    }
});
// A service restart invalidates old browser cookies. Return the normal 401 response before
// controller code is reached, so the UI can show the login page without a debugger break.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") &&
        !context.Request.Path.Equals("/api/login", StringComparison.OrdinalIgnoreCase) &&
        app.Services.GetRequiredService<AccessService>().GetLogin(context) is null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "请重新登录" });
        return;
    }
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();
app.MapFallback("/api/{**path}", () => Results.NotFound(new { error = "接口不存在" }));
app.Run();

public partial class Program { }
