using System.Text.Json;
using ModelContextProtocol.Protocol;

public static class HttpMcpDemo
{
    public static async Task RunAsync(Uri endpoint)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await GuidelinesMcpClient.ConnectHttpAsync(
            endpoint,
            timeout.Token
        );

        // tools/list: the server returns names, descriptions and argument schemas.
        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        foreach (var tool in tools)
            Console.WriteLine($"Discovered: {tool.Name}");
        if (
            !tools.Any(t => t.Name == "get_design_guidelines")
            || !tools.Any(t => t.Name == "get_colour_palette")
        )
            throw new Exception("Expected studio tools were not discovered over HTTP.");

        // tools/call: a direct invocation. An LLM would normally choose these arguments.
        var guidelines = await client.CallToolAsync(
            "get_design_guidelines",
            new Dictionary<string, object?> { ["theme"] = "underwater" },
            cancellationToken: timeout.Token
        );
        string json = string.Join(
            "\n",
            guidelines.Content.OfType<TextContentBlock>().Select(c => c.Text)
        );
        Console.WriteLine(json);
        using var result = JsonDocument.Parse(json);
        if (
            guidelines.IsError == true
            || result.RootElement.GetProperty("guidelines").GetArrayLength() != 4
        )
            throw new Exception("HTTP guidelines invocation failed.");

        var palette = await client.CallToolAsync(
            "get_colour_palette",
            new Dictionary<string, object?> { ["paletteName"] = "sunset" },
            cancellationToken: timeout.Token
        );
        string paletteJson = string.Join(
            "\n",
            palette.Content.OfType<TextContentBlock>().Select(c => c.Text)
        );
        Console.WriteLine(paletteJson);
        using var paletteResult = JsonDocument.Parse(paletteJson);
        if (
            palette.IsError == true
            || paletteResult.RootElement.GetProperty("name").GetString() != "sunset"
        )
            throw new Exception("HTTP palette invocation failed.");
        Console.WriteLine(
            "PASS: HTTP MCP discovery and both tool calls. No LLM or API key required."
        );
    }
}
