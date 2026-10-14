using System.Net;
using ModelContextProtocol.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://127.0.0.1:5090");

// Same tool implementations as the stdio host; only the transport changes.
builder
    .Services.AddMcpServer()
    .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
    .WithToolsFromAssembly(typeof(DesignGuidelinesTools).Assembly);

var app = builder.Build();

// This demo has no authentication. Keep it local, including Host/Origin validation.
app.Use(
    async (context, next) =>
    {
        if (
            context.Connection.RemoteIpAddress is { } remote && !IPAddress.IsLoopback(remote)
            || context.Request.Host.Host is not ("localhost" or "127.0.0.1" or "::1")
            || context.Request.Headers.Origin.FirstOrDefault() is { } origin
                && origin != $"{context.Request.Scheme}://{context.Request.Host}"
        )
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        await next();
    }
);

// One MCP endpoint handles protocol operations such as tools/list and tools/call.
app.MapMcp("/mcp");
app.MapGet("/", () => "Concept Studio MCP is running. Connect an MCP client to /mcp.");
await app.RunAsync();
