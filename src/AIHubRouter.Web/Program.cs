using System.Threading.RateLimiting;
using System.Text.Json.Serialization;
using AIHubRouter.Web;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var urls = Environment.GetEnvironmentVariable("AIHUB_WEB_URLS") ?? "http://127.0.0.1:5080";
var webPassword = Environment.GetEnvironmentVariable("AIHUB_WEB_PASSWORD");
if (string.IsNullOrWhiteSpace(webPassword))
{
    throw new InvalidOperationException("必须设置 AIHUB_WEB_PASSWORD 才能启动 Web 端。");
}

if (webPassword.Length < 12)
{
    throw new InvalidOperationException("AIHUB_WEB_PASSWORD 至少需要 12 个字符。");
}

if (urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Any(url => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
            !url.Contains("localhost", StringComparison.OrdinalIgnoreCase)) &&
    Environment.GetEnvironmentVariable("AIHUB_WEB_ALLOW_HTTP") != "1")
{
    throw new InvalidOperationException(
        "外网监听必须使用 HTTPS；仅在可信内网临时使用时才设置 AIHUB_WEB_ALLOW_HTTP=1。");
}

builder.WebHost.UseUrls(urls);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(
        new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
});
builder.Services.AddSingleton(new WebSessionManager(webPassword));
builder.Services.AddSingleton<WebRouterCoordinator>();
builder.Services.AddSingleton<IHostedService>(services => services.GetRequiredService<WebRouterCoordinator>());
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 8,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});

var app = builder.Build();
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        context.Context.Response.Headers.CacheControl =
            context.File.Name is "index.html" ? "no-store" : "public,max-age=86400";
        context.Context.Response.Headers.XContentTypeOptions = "nosniff";
    }
});

app.Use(async (context, next) =>
{
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; " +
        "connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";

    if (context.Request.Path == "/" || context.Request.Path == "/index.html")
    {
        context.Response.Headers.CacheControl = "no-store";
    }

    if (!context.Request.Path.StartsWithSegments("/api"))
    {
        await next(context);
        return;
    }

    var requestPath = context.Request.Path.Value;
    var isPublicAuth = requestPath is "/api/auth/status" or "/api/auth/login";
    var sessions = context.RequestServices.GetRequiredService<WebSessionManager>();
    if (!isPublicAuth && !sessions.IsAuthenticated(context.Request))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "登录已失效。" });
        return;
    }

    if (!HttpMethods.IsGet(context.Request.Method) &&
        !HttpMethods.IsHead(context.Request.Method) &&
        context.Request.Headers["X-AIHub-Web"] != "1")
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = "请求来源校验失败。" });
        return;
    }

    await next(context);
});

app.MapGet("/api/auth/status", (HttpRequest request, WebSessionManager sessions) =>
    Results.Ok(new { authenticated = sessions.IsAuthenticated(request) }));

app.MapPost("/api/auth/login", (LoginRequest login, HttpContext context, WebSessionManager sessions) =>
{
    if (!sessions.TryLogin(login.Password, context))
    {
        return Results.Json(new { error = "访问口令错误。" }, statusCode: StatusCodes.Status401Unauthorized);
    }

    return Results.Ok(new { authenticated = true });
}).RequireRateLimiting("login");

app.MapPost("/api/auth/logout", (HttpContext context, WebSessionManager sessions) =>
{
    sessions.Logout(context);
    return Results.Ok(new { authenticated = false });
});

app.MapGet("/api/dashboard", (WebRouterCoordinator coordinator) =>
    Results.Ok(coordinator.GetDashboard()));
app.MapPut("/api/settings", async (SettingsUpdateRequest request, WebRouterCoordinator coordinator, CancellationToken token) =>
    Results.Ok(await coordinator.SaveSettingsAsync(request, token)));
app.MapPost("/api/actions/refresh", async (WebRouterCoordinator coordinator, CancellationToken token) =>
    Results.Ok(await coordinator.RunCycleAsync(dryRun: true, forceRefresh: true, token)));
app.MapPost("/api/actions/dry-run", async (WebRouterCoordinator coordinator, CancellationToken token) =>
    Results.Ok(await coordinator.RunCycleAsync(dryRun: true, forceRefresh: false, token)));
app.MapPost("/api/actions/route", async (WebRouterCoordinator coordinator, CancellationToken token) =>
    Results.Ok(await coordinator.RunCycleAsync(dryRun: false, forceRefresh: false, token)));
app.MapPost("/api/actions/manual-route", async (ManualRouteRequest request, WebRouterCoordinator coordinator, CancellationToken token) =>
    Results.Ok(await coordinator.RouteManuallyAsync(request.GroupId, token)));
app.MapPut("/api/auto-routing", async (AutoRoutingRequest request, WebRouterCoordinator coordinator, CancellationToken token) =>
    Results.Ok(await coordinator.SetAutoRoutingAsync(request.Enabled, token)));
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapFallbackToFile("index.html");

app.Run();
