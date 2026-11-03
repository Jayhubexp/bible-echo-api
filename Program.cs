using BibleEcho.Workspace;
using BibleEcho.AI;
using BibleEcho.Core.Interfaces;
using BibleEcho.Data;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{builder.Configuration["PORT"] ?? "5080"}");
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 512_000);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(
    (builder.Configuration["FRONTEND_ORIGIN"] ?? "http://localhost:5173").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
    .WithExposedHeaders("X-Echo-Time").WithMethods("GET","POST","PUT","DELETE").WithHeaders("Content-Type","X-Operator-Key","X-Controller")));
builder.Services.AddSingleton<IBibleRepository>(new SqliteBibleRepository(Path.Combine(AppContext.BaseDirectory,"data","bible.db")));
builder.Services.AddSingleton<IScriptureMatcher, ScriptureMatchingEngine>();
builder.Services.AddHttpClient<MetaClient>(c => {
    c.BaseAddress = new Uri("https://api.meta.ai/v1/");
    c.Timeout = TimeSpan.FromSeconds(90);
    c.MaxResponseContentBufferSize = 24 * 1024 * 1024;
});
builder.Services.AddHttpClient("generated-image", c => {
    c.MaxResponseContentBufferSize=24*1024*1024;
    c.Timeout=TimeSpan.FromSeconds(25);
}).ConfigurePrimaryHttpMessageHandler(()=>new HttpClientHandler {AllowAutoRedirect=false});
Endpoints.Configure(builder);
var app = builder.Build();
app.UseCors();
app.UseRateLimiter();
Endpoints.Map(app);
app.Use(async (ctx,next) => {
    try { await next(); }
    catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
    catch (Exception e) when (!ctx.Response.HasStarted) {
        var (code,message) = e switch {
            WorkspaceError w => (w.Status,w.Message),
            MetaFailure m => (m.Status,m.Message),
            BadHttpRequestException b => (b.StatusCode,"Invalid or oversized request."),
            OperationCanceledException => (504,"Meta timed out. Please try again."),
            HttpRequestException when ctx.Request.Path.StartsWithSegments("/api/v1/workspace") => (502,"Workspace provider unreachable. Check connectivity and refresh before retrying."),
            HttpRequestException => (502,"Could not reach Meta. Please try again."),
            System.Text.Json.JsonException => (502,"Meta returned an unreadable response."),
            _ => (500,"The API could not complete this request.")
        };
        app.Logger.LogWarning("Request failed {Method} {Path} {Status} {ErrorType}",ctx.Request.Method,ctx.Request.Path,code,e.GetType().Name);
        ctx.Response.StatusCode = code;
        await ctx.Response.WriteAsJsonAsync(new {error=message});
    }
});
foreach (var prefix in new[]{"/api","/api/v1"}) {
    var api = app.MapGroup(prefix);
    api.MapGet("/health", () => new { status="ok",version=1 });
    api.MapGet("/scripture/search", Search);
    api.MapPost("/transcribe", MetaClient.Transcribe).RequireRateLimiting("model");
    api.MapPost("/backgrounds/generate", MetaClient.Generate).RequireRateLimiting("model");
}
var repo = app.Services.GetRequiredService<IBibleRepository>();
if ((await repo.GetVersesAsync("Genesis",1,1)).Count != 1)
    throw new InvalidOperationException("Bundled KJV data failed the startup check.");
app.Run();

static async Task<IResult> Search(string? q, string? preceding, bool? spoken, IBibleRepository repo, IScriptureMatcher matcher, CancellationToken ct) {
    if (string.IsNullOrWhiteSpace(q) || q.Length > 2000)
        return Results.BadRequest(new {error="Enter up to 2,000 characters."});
    if (preceding?.Length > 300) return Results.BadRequest(new {error="Context exceeds 300 characters."});
    var resultSet = await CitationLookup.Resolve(q,preceding,spoken==true,repo,ct);
    var passages = resultSet.Passages;
    if (spoken != true && passages.Count == 0 && resultSet.Unresolved.Count == 0) {
        var result = await matcher.MatchScriptureAsync(q,cancellationToken:ct);
        if (result != null) passages.Add(new {reference=result.MatchedReference, verses=result.Verses});
    }
    return Results.Ok(resultSet);
}
