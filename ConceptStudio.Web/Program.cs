using System.ClientModel;
using System.Net;
using System.Text.Json;
using ModelContextProtocol;

var builder = WebApplication.CreateBuilder(args);
// Local learning app: it has no user accounts and must not be exposed publicly.
builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://127.0.0.1:5080");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 32_768);
builder.Services.AddSingleton(new SessionStore(
    builder.Configuration["CONCEPT_STUDIO_SESSIONS_DIR"] ?? Path.Combine(builder.Environment.ContentRootPath, "artifacts", "sessions")));
builder.Services.AddSingleton<StudioService>();
var app = builder.Build();
app.Use(async (context, next) =>
{
    if (context.Connection.RemoteIpAddress is { } remote && !IPAddress.IsLoopback(remote) ||
        context.Request.Host.Host is not ("127.0.0.1" or "localhost" or "::1"))
    { context.Response.StatusCode = 403; return; }
    if (context.Request.Headers.Origin.FirstOrDefault() is { } origin &&
        origin != $"{context.Request.Scheme}://{context.Request.Host}")
    { context.Response.StatusCode = 403; return; }
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self'; style-src 'self'; script-src 'self'; frame-ancestors 'none'";
    if (context.Request.Path.StartsWithSegments("/api")) context.Response.Headers.CacheControl = "no-store";
    try { await next(); }
    catch (Exception ex) when (!context.Response.HasStarted)
    {
        // Do not expose provider bodies, filesystem paths or secrets in browser errors.
        var (status, message) = ex switch
        {
            ClientResultException api => (502, $"The model API returned HTTP {api.Status}. Check model access, billing and quota."),
            OperationCanceledException => (504, "The request timed out or was cancelled. It may still have been processed; check your usage before retrying."),
            InvalidOperationException => (503, "Set OPENAI_API_KEY in the server environment, then restart the app."),
            AgentResponseException or JsonException => (502, "The model response could not be read. Your existing images remain available."),
            McpException => (502, "The guidelines server could not complete the request."),
            _ => (500, "The request could not be completed. Your existing images remain available.")
        };
        app.Logger.LogWarning("Request failed: {ErrorType}", ex.GetType().Name);
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { error = message });
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/api/config", (StudioService studio) => new
{
    configured = studio.IsConfigured, studio.TextModel, studio.ImageModel, studio.ReviewModel
});
app.MapGet("/api/sessions", async (SessionStore store, CancellationToken ct) => Results.Ok(await store.ListAsync(ct)));
app.MapPost("/api/sessions", (BriefRequest input, StudioService studio) =>
{
    if (input.Brief is null || input.Brief.Length > 4000)
        return Results.BadRequest(new { error = "A brief can contain up to 4,000 characters." });
    return Results.Ok(studio.CreateSession(input.Brief).View());
});
app.MapGet("/api/sessions/{id:guid}", async (Guid id, StudioService studio, CancellationToken ct) =>
    await studio.FindSessionAsync(id, ct) is { } session ? Results.Ok(session.View()) : Results.NotFound(new { error = "Session not found. Unsaved sessions are lost when the server restarts." }));
app.MapPost("/api/sessions/{id:guid}/open", async (Guid id, StudioService studio, SessionStore store, CancellationToken ct) =>
{
    var saved = await store.LoadAsync(id, ct);
    if (saved is null) return Results.NotFound(new { error = "No saved session found." });
    if (studio.Sessions.TryGetValue(id, out var current))
    {
        if (!await current.Gate.WaitAsync(0, ct)) return Results.Conflict(new { error = "This session is busy." });
        try
        {
            current.Name = saved.Name; current.Brief = saved.Brief; current.SavedAt = saved.SavedAt;
            current.SelectedRevisionId = saved.SelectedRevisionId; current.HasUnsavedChanges = false;
            current.Concepts.Clear(); current.Concepts.AddRange(saved.Concepts);
            current.Trace.Clear(); current.Trace.AddRange(saved.Trace);
            current.Revisions.Clear();
            foreach (var item in saved.Revisions) current.Revisions[item.Key] = item.Value;
            return Results.Ok(current.View());
        }
        finally { current.Gate.Release(); }
    }
    studio.Sessions[id] = saved;
    return Results.Ok(saved.View());
});
app.MapPost("/api/sessions/{id:guid}/save", async (Guid id, SaveSessionRequest input, StudioService studio, SessionStore store, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 100 || input.Brief is null || input.Brief.Length > 4000)
        return Results.BadRequest(new { error = "Enter a session name (up to 100 characters) and a brief (up to 4,000 characters)." });
    var session = await studio.FindSessionAsync(id, ct);
    if (session is null) return Results.NotFound();
    await session.Gate.WaitAsync(ct);
    try
    {
        if (input.SelectedRevisionId is { } selected && !session.Revisions.ContainsKey(selected))
            return Results.BadRequest(new { error = "Unknown selected revision." });
        session.Name = input.Name.Trim(); session.Brief = input.Brief; session.HasUnsavedChanges = true;
        session.SelectedRevisionId = input.SelectedRevisionId;
        await store.SaveAsync(session, ct);
        return Results.Ok(session.View());
    }
    finally { session.Gate.Release(); }
});
app.MapPost("/api/sessions/{id:guid}/concepts", async (Guid id, GenerateConceptsRequest input, StudioService studio, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(input.Brief) || input.Brief.Length > 4000)
        return Results.BadRequest(new { error = "Enter a brief between 1 and 4,000 characters." });
    if (input.Count is < 1 or > 3)
        return Results.BadRequest(new { error = "Choose between 1 and 3 concepts." });
    var session = await studio.FindSessionAsync(id, ct);
    if (session is null) return Results.NotFound();
    if (!await session.Gate.WaitAsync(0, ct)) return Results.Conflict(new { error = "An operation is already running for this session." });
    try { return Results.Ok((await studio.GenerateConceptsAsync(session, input.Brief.Trim(), input.Count, ct)).View()); }
    finally { session.Gate.Release(); }
});
app.MapPost("/api/sessions/{id:guid}/concepts/{index:int}/delete", async (Guid id, int index, StudioService studio, CancellationToken ct) =>
{
    var session = await studio.FindSessionAsync(id, ct);
    if (session is null) return Results.NotFound();
    if (!await session.Gate.WaitAsync(0, ct)) return Results.Conflict(new { error = "An operation is already running for this session." });
    try
    {
        return session.DeleteConcept(index) ? Results.Ok(session.View())
            : Results.NotFound(new { error = "Concept not found. Refresh the session and try again." });
    }
    finally { session.Gate.Release(); }
});
app.MapPost("/api/sessions/{id:guid}/images", async (Guid id, ImageRequest input, StudioService studio, CancellationToken ct) =>
{
    var session = await studio.FindSessionAsync(id, ct);
    if (session is null) return Results.NotFound();
    if (input.ConceptIndex < 0 || input.ConceptIndex >= session.Concepts.Count)
        return Results.BadRequest(new { error = "Choose a valid concept." });
    if (input.SourceRevisionId is { } source &&
        (!session.Revisions.TryGetValue(source, out var previous) || previous.ConceptIndex != input.ConceptIndex ||
         string.IsNullOrWhiteSpace(input.Feedback) || input.Feedback.Length > 2000))
        return Results.BadRequest(new { error = "Choose a revision of this concept and enter feedback (up to 2,000 characters)." });
    if (!await session.Gate.WaitAsync(0, ct)) return Results.Conflict(new { error = "An operation is already running for this brief." });
    try { return Results.Ok((await studio.GenerateImageAsync(session, input, ct)).View(id)); }
    finally { session.Gate.Release(); }
});
app.MapGet("/api/sessions/{id:guid}/images/{revisionId:guid}", async (Guid id, Guid revisionId, StudioService studio, CancellationToken ct) =>
{
    var session = await studio.FindSessionAsync(id, ct);
    return session is not null && session.Revisions.TryGetValue(revisionId, out var revision) && File.Exists(revision.Path)
        ? Results.File(revision.Path, "image/png") : Results.NotFound();
});
app.MapPost("/api/sessions/{id:guid}/images/{revisionId:guid}/review", async (Guid id, Guid revisionId, StudioService studio, CancellationToken ct) =>
{
    var session = await studio.FindSessionAsync(id, ct);
    if (session is null || !session.Revisions.TryGetValue(revisionId, out var revision)) return Results.NotFound();
    if (revision.Review is not null) return Results.Ok(revision.Review);
    if (!await session.Gate.WaitAsync(0, ct)) return Results.Conflict(new { error = "An operation is already running for this brief." });
    try { return Results.Ok(await studio.ReviewAsync(session, revision, ct)); }
    finally { session.Gate.Release(); }
});
app.Run();
