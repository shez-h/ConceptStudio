using ModelContextProtocol.Client;

public static class GuidelinesMcpClient
{
    public static async Task<McpClient> ConnectAsync(
        CancellationToken cancellationToken = default,
        string? serverAssembly = null)
    {
        // An explicit assembly keeps tests on stdio; otherwise opt into HTTP via configuration.
        string? endpoint = Environment.GetEnvironmentVariable("CONCEPT_STUDIO_MCP_URL");
        if (serverAssembly is null && !string.IsNullOrWhiteSpace(endpoint))
            return await ConnectHttpAsync(new Uri(endpoint, UriKind.Absolute), cancellationToken);

        serverAssembly ??= Path.Combine(AppContext.BaseDirectory,
            "mcp-server", "ConceptStudio.GuidelinesMcp.dll");
        if (!File.Exists(serverAssembly))
            throw new FileNotFoundException("Build the host application to build and copy the MCP server.", serverAssembly);

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "Concept Studio guidelines",
            Command = "dotnet",
            Arguments = [Path.GetFullPath(serverAssembly)],
            // The guidelines server needs no API keys from the parent application.
            InheritEnvironmentVariables = false,
            EnvironmentVariables = StdioClientTransportOptions.GetDefaultEnvironmentVariables()
        });

        // Starts a separate process and performs the MCP initialization handshake.
        return await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
    }

    public static async Task<McpClient> ConnectHttpAsync(Uri endpoint, CancellationToken cancellationToken = default)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https"))
            throw new ArgumentException("Use an absolute HTTP(S) MCP endpoint.", nameof(endpoint));
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = "Concept Studio guidelines over HTTP",
            Endpoint = endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            ConnectionTimeout = TimeSpan.FromSeconds(15)
        });
        // Connects to the independently running web service; it does not start a process.
        return await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
    }
}
