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
    .WithMethods("GET","POST").WithHeaders("Content-Type")));
builder.Services.AddSingleton<IBibleRepository>(new SqliteBibleRepository(Path.Combine(AppContext.BaseDirectory,"data","bible.db")));
builder.Services.AddSingleton<IScriptureMatcher, ScriptureMatchingEngine>();
builder.Services.AddHttpClient<MetaClient>(c => {
    c.BaseAddress = new Uri("https://api.meta.ai/v1/");
    c.Timeout = TimeSpan.FromSeconds(90);
    c.MaxResponseContentBufferSize = 24 * 1024 * 1024;
});
var app = builder.Build();
app.UseCors();
app.Use(async (ctx,next) => {
    try { await next(); }
    catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
    catch (Exception e) when (!ctx.Response.HasStarted) {
        var (code,message) = e switch {
            MetaFailure m => (m.Status,m.Message),
            BadHttpRequestException b => (b.StatusCode,"Invalid or oversized request."),
            OperationCanceledException => (504,"Meta timed out. Please try again."),
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
    api.MapPost("/transcribe", MetaClient.Transcribe);
    api.MapPost("/backgrounds/generate", MetaClient.Generate);
}
var repo = app.Services.GetRequiredService<IBibleRepository>();
if ((await repo.GetVersesAsync("Genesis",1,1)).Count != 1)
    throw new InvalidOperationException("Bundled KJV data failed the startup check.");
app.Run();

static async Task<IResult> Search(string? q, string? preceding, bool? spoken, IBibleRepository repo, IScriptureMatcher matcher, CancellationToken ct) {
    if (string.IsNullOrWhiteSpace(q) || q.Length > 2000)
        return Results.BadRequest(new {error="Enter up to 2,000 characters."});
    var passages = new List<object>();
    if (preceding?.Length > 300) return Results.BadRequest(new {error="Context exceeds 300 characters."});
    var citations = SpokenCitationLexer.Parse(q);
    if (spoken == true && !string.IsNullOrWhiteSpace(preceding)) {
        var earlier = SpokenCitationLexer.Parse(preceding);
        var combined = SpokenCitationLexer.Parse(preceding + " " + q);
        int consumed = 0;
        while (consumed < earlier.Count && consumed < combined.Count &&
               earlier[consumed].FormattedReference == combined[consumed].FormattedReference) consumed++;
        citations = combined.Skip(consumed).ToList();
    }
    foreach (var reference in citations.DistinctBy(r => r.FormattedReference)) {
        var verses = await repo.GetVersesAsync(reference.Book,reference.Chapter,reference.VerseStart,reference.VerseEnd,cancellationToken:ct);
        if (verses.Count > 0 && (reference.VerseEnd == null || verses[^1].Verse == reference.VerseEnd))
            passages.Add(new { reference=reference.FormattedReference, verses });
    }
    if (spoken != true && citations.Count == 0) {
        var result = await matcher.MatchScriptureAsync(q,cancellationToken:ct);
        if (result != null) passages.Add(new {reference=result.MatchedReference, verses=result.Verses});
    }
    return Results.Ok(new {passages});
}
