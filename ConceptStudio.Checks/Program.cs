using System.Text.Json;
using OpenAI.Responses;

#pragma warning disable OPENAI001

if (args.FirstOrDefault() == "--mcp-http")
{
    await HttpMcpDemo.RunAsync(new Uri(args.ElementAtOrDefault(1) ?? "http://127.0.0.1:5090/mcp"));
    return;
}

await ImageChecks.RunAsync();
await ReviewChecks.RunAsync();
await SessionChecks.RunAsync();
await ConceptDeletionChecks.RunAsync();

// Local checks: no API key, model calls, or paid requests. Starts a real MCP server subprocess.
int checks = 0;
void Check(bool condition, string description)
{
    if (!condition)
        throw new Exception(description);
    checks++;
    Console.WriteLine($"PASS: {description}");
}

void Throws<T>(Action action, string description)
    where T : Exception
{
    try
    {
        action();
    }
    catch (T)
    {
        Check(true, description);
        return;
    }
    throw new Exception($"Expected {typeof(T).Name}: {description}");
}

var builder = new AgentRequestBuilder().WithModel("test-model").WithInput("underwater");
AgentRequest first = builder.Build();
first.Options.InputItems.Add(ResponseItem.CreateUserMessageItem("history from first run"));
AgentRequest second = builder.Build();
Check(second.Options.InputItems.Count == 1, "Run history does not leak into a second build");
builder.WithInput("another input");
Check(
    second.Options.InputItems.Count == 1 && builder.Build().Options.InputItems.Count == 2,
    "Builder changes do not mutate earlier requests"
);
Throws<InvalidOperationException>(
    () => new AgentRequestBuilder().Build(),
    "Missing configuration fails early"
);
Throws<ArgumentOutOfRangeException>(
    () => builder.WithMaxOutputTokens(0),
    "Invalid token limit rejected"
);
Throws<JsonException>(() => builder.WithOutput("bad", "{"), "Malformed schema rejected locally");
Throws<ArgumentException>(() => builder.WithOutput("bad", "[]"), "Non-object schema rejected");

using var mcpTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
string serverAssembly =
    args.FirstOrDefault()
    ?? Path.GetFullPath(
        "ConceptStudio.Console/bin/Debug/net10.0/mcp-server/ConceptStudio.GuidelinesMcp.dll"
    );
await using var mcp = await GuidelinesMcpClient.ConnectAsync(mcpTimeout.Token, serverAssembly);
IReadOnlyList<AgentTool> discovered = await McpToolAdapter.DiscoverAsync(mcp, mcpTimeout.Token);
AgentTool guidelines = discovered.Single(tool => tool.Name == "get_design_guidelines");
AgentTool palette = discovered.Single(tool => tool.Name == "get_colour_palette");
Check(discovered.Count == 2, "Both server tools are discovered");
using (JsonDocument schema = JsonDocument.Parse(guidelines.ParametersSchema))
    Check(
        schema.RootElement.GetProperty("properties").TryGetProperty("theme", out _),
        "MCP discovery supplies the theme parameter schema"
    );
builder.WithTool(guidelines);
Throws<ArgumentException>(() => builder.WithTool(guidelines), "Duplicate tool name rejected");

AgentRequest request = ConceptGenerationRequest.Build(
    "test-model",
    "underwater vehicle",
    discovered.ToArray()
);
Check(
    request.Options.Tools.Count == 2
        && request.Tools.ContainsKey(guidelines.Name)
        && request.Tools.ContainsKey(palette.Name),
    "Definition and executable handler are registered together"
);
Check(
    request.Options.TextOptions is not null && request.Options.StoredOutputEnabled == false,
    "Structured output and explicit local conversation state are configured"
);

FunctionCallResponseItem Call(string name, string arguments) =>
    ResponseItem.CreateFunctionCallItem("call_test", name, BinaryData.FromString(arguments));

string output = await AgentRunner.ExecuteToolAsync(
    request,
    Call(guidelines.Name, """{"theme":"underwater"}"""),
    mcpTimeout.Token
);
using (JsonDocument doc = JsonDocument.Parse(output))
    Check(
        doc.RootElement.GetProperty("guidelines").GetArrayLength() == 4,
        "Model-supplied theme reaches the separate MCP server"
    );

foreach (
    string arguments in new[]
    {
        "{",
        "{}",
        "[]",
        """{"theme":42}""",
        """{"theme":"space"}""",
        """{"theme":" "}""",
        """{"theme":null}""",
    }
)
{
    output = await AgentRunner.ExecuteToolAsync(
        request,
        Call(guidelines.Name, arguments),
        mcpTimeout.Token
    );
    using JsonDocument doc = JsonDocument.Parse(output);
    Check(
        doc.RootElement.TryGetProperty("error", out _),
        $"Recoverable tool error for {arguments}"
    );
}

foreach (string name in new[] { "ocean-rescue", "sunset" })
{
    string paletteOutput = await AgentRunner.ExecuteToolAsync(
        request,
        Call(palette.Name, JsonSerializer.Serialize(new { paletteName = name })),
        mcpTimeout.Token
    );
    using JsonDocument doc = JsonDocument.Parse(paletteOutput);
    Check(
        doc.RootElement.GetProperty("name").GetString() == name
            && doc.RootElement.GetProperty("colours").GetArrayLength() == 2,
        $"Discovered palette handler dispatches {name} to the correct MCP method"
    );
}
foreach (
    string arguments in new[]
    {
        "{}",
        "[]",
        "{",
        """{"paletteName":"unknown"}""",
        """{"paletteName":null}""",
        """{"paletteName":42}""",
    }
)
{
    string paletteOutput = await AgentRunner.ExecuteToolAsync(
        request,
        Call(palette.Name, arguments),
        mcpTimeout.Token
    );
    using JsonDocument doc = JsonDocument.Parse(paletteOutput);
    Check(
        doc.RootElement.TryGetProperty("error", out _),
        $"Palette error returned for {arguments}"
    );
}

output = await AgentRunner.ExecuteToolAsync(
    request,
    Call("unregistered_tool", "{}"),
    mcpTimeout.Token
);
Check(output.Contains("Unknown tool"), "Unknown tool cannot execute");

using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel();
    try
    {
        await AgentRunner.ExecuteToolAsync(
            request,
            Call(guidelines.Name, "{}"),
            cancellation.Token
        );
        throw new Exception("Expected cancellation");
    }
    catch (OperationCanceledException)
    {
        Check(true, "Cancellation is propagated, not returned as tool data");
    }
}

Concept[] validConcepts =
[
    new("A", "Description", "Prompt", []),
    new("B", "Description", "Prompt", []),
    new("C", "Description", "Prompt", []),
];
string validJson = JsonSerializer.Serialize(new ConceptResponse(validConcepts.ToList()));
Check(ConceptGenerationRequest.ReadConcepts(validJson).Count == 3, "Valid concepts deserialize");
Throws<AgentResponseException>(
    () => ConceptGenerationRequest.ReadConcepts(validJson.Replace(",\"appliedGuidelines\":[]", "")),
    "Missing applied guidelines rejected"
);
validConcepts[0] = validConcepts[0] with
{
    AppliedGuidelines = ["Include exactly two circular observation windows."],
};
Check(
    ConceptGenerationRequest
        .ReadConcepts(JsonSerializer.Serialize(new ConceptResponse(validConcepts.ToList())))[0]
        .AppliedGuidelines.SequenceEqual(validConcepts[0].AppliedGuidelines),
    "Guideline wording survives deserialization"
);
Throws<AgentResponseException>(
    () => ConceptGenerationRequest.ReadConcepts("{}"),
    "Missing concepts rejected"
);
foreach (int count in new[] { 1, 2, 3 })
{
    using var schema = JsonDocument.Parse(ConceptOutputSchema.ForCount(count));
    var array = schema.RootElement.GetProperty("properties").GetProperty("concepts");
    Check(
        array.GetProperty("minItems").GetInt32() == count
            && array.GetProperty("maxItems").GetInt32() == count,
        $"Schema requires exactly {count} concept(s)"
    );
    string response = JsonSerializer.Serialize(
        new ConceptResponse(
            validConcepts.Take(count).Select(c => c with { Brief = "invented" }).ToList()
        )
    );
    var parsed = ConceptGenerationRequest.ReadConcepts(response, count, "The actual new idea");
    Check(
        parsed.Count == count && parsed.All(c => c.Brief == "The actual new idea"),
        $"{count} concept(s) retain the actual request brief, not model text"
    );
}
Throws<AgentResponseException>(
    () => ConceptGenerationRequest.ReadConcepts(validJson, 1),
    "Wrong concept count rejected"
);
Throws<ArgumentOutOfRangeException>(
    () => ConceptGenerationRequest.Build("test-model", "brief", 0),
    "Invalid requested count rejected before API call"
);
Throws<ArgumentOutOfRangeException>(
    () => ConceptOutputSchema.ForCount(4),
    "Counts above three rejected"
);
validConcepts[1] = new("", "Description", "Prompt", []);
Throws<AgentResponseException>(
    () =>
        ConceptGenerationRequest.ReadConcepts(
            JsonSerializer.Serialize(new ConceptResponse(validConcepts.ToList()))
        ),
    "Empty required content rejected"
);

Console.WriteLine(
    $"\n{checks} checks passed (including real MCP discovery and calls; no model requests)."
);
