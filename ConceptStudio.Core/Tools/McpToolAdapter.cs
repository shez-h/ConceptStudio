using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

// Bridges a discovered MCP tool into our existing agent loop.
public static class McpToolAdapter
{
    public static async Task<IReadOnlyList<AgentTool>> DiscoverAsync(
        McpClient client, CancellationToken cancellationToken = default)
    {
        // MCP tools/list: the name, description and argument schema come from the SERVER.
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
        return tools.Select(tool => new AgentTool(
            Name: tool.Name,
            Description: tool.Description ?? tool.Name,
            ParametersSchema: tool.JsonSchema.GetRawText(),
            Handler: async (argumentsJson, ct) =>
            {
                using JsonDocument arguments = JsonDocument.Parse(argumentsJson);
                if (arguments.RootElement.ValueKind != JsonValueKind.Object)
                    return """{"error":"Tool arguments must be a JSON object"}""";

                var values = arguments.RootElement.EnumerateObject()
                    .ToDictionary(property => property.Name, property => (object?)property.Value.Clone());

                // MCP tools/call: this crosses the process boundary to the server.
                var result = await client.CallToolAsync(tool.Name, values, cancellationToken: ct);
                string text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(part => part.Text));
                if (result.IsError == true)
                    return JsonSerializer.Serialize(new { error = text });
                if (string.IsNullOrWhiteSpace(text))
                    return """{"error":"The MCP tool returned no text"}""";
                return text;
            },
            // MCP JSON schemas are not necessarily OpenAI strict-mode schemas.
            // Preserve the discovered schema; the server validates the arguments.
            StrictModeEnabled: false)).ToList();
    }
}
