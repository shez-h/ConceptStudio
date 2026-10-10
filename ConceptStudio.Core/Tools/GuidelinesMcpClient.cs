using ModelContextProtocol.Client;

public static class GuidelinesMcpClient
{
    public static async Task<McpClient> ConnectAsync(
        CancellationToken cancellationToken = default,
        string? serverAssembly = null)
    {
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
}
