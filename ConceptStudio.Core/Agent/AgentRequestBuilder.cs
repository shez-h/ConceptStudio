using System.Text.Json;
using OpenAI.Responses;

#pragma warning disable OPENAI001

public sealed class AgentRequestBuilder
{
    private string? model;
    private string? instructions;
    private readonly List<Func<ResponseItem>> inputs = [];
    private readonly Dictionary<string, AgentTool> tools = new(StringComparer.Ordinal);
    private string? outputName;
    private string? outputSchema;
    private int maxOutputTokens = 1000;

    public AgentRequestBuilder WithModel(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        model = value;
        return this;
    }

    public AgentRequestBuilder WithInstructions(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        instructions = value;
        return this;
    }

    // Repeated calls append user messages in order.
    public AgentRequestBuilder WithInput(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        inputs.Add(() => ResponseItem.CreateUserMessageItem(value));
        return this;
    }

    public AgentRequestBuilder WithInput(string text, BinaryData image)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentNullException.ThrowIfNull(image);
        inputs.Add(() => ResponseItem.CreateUserMessageItem([
            ResponseContentPart.CreateInputTextPart(text),
            ResponseContentPart.CreateInputImagePart(image)
        ]));
        return this;
    }

    public AgentRequestBuilder WithOutput(string schemaName, string jsonSchema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);
        ValidateSchema(jsonSchema);
        outputName = schemaName;
        outputSchema = jsonSchema;
        return this;
    }

    public AgentRequestBuilder WithTool(AgentTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentException.ThrowIfNullOrWhiteSpace(tool.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(tool.Description);
        ArgumentNullException.ThrowIfNull(tool.Handler);
        ValidateSchema(tool.ParametersSchema);
        if (!tools.TryAdd(tool.Name, tool))
            throw new ArgumentException($"Tool '{tool.Name}' is already registered.", nameof(tool));
        return this;
    }

    public AgentRequestBuilder WithMaxOutputTokens(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        maxOutputTokens = value;
        return this;
    }

    public AgentRequest Build()
    {
        if (model is null || inputs.Count == 0)
            throw new InvalidOperationException("A model and at least one input are required.");

        // Fresh options and collections: the runner can append history without mutating the builder.
        CreateResponseOptions options = new()
        {
            Model = model,
            Instructions = instructions,
            MaxOutputTokenCount = maxOutputTokens,
            StoredOutputEnabled = false
        };
        foreach (var input in inputs)
            options.InputItems.Add(input());

        if (outputSchema is not null)
            options.TextOptions = new ResponseTextOptions
            {
                TextFormat = ResponseTextFormat.CreateJsonSchemaFormat(
                    jsonSchemaFormatName: outputName!,
                    jsonSchema: BinaryData.FromString(outputSchema),
                    jsonSchemaIsStrict: true)
            };

        foreach (AgentTool tool in tools.Values)
            options.Tools.Add(tool.CreateDefinition());

        return new AgentRequest(options, new Dictionary<string, AgentTool>(tools));
    }

    private static void ValidateSchema(string schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        using JsonDocument document = JsonDocument.Parse(schema);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("A schema must be a JSON object.", nameof(schema));
        // Full validation of the provider's supported JSON Schema subset happens at the API.
    }
}

// One request represents one run; build another request to start an independent conversation.
public sealed class AgentRequest(
    CreateResponseOptions options,
    IReadOnlyDictionary<string, AgentTool> tools)
{
    public CreateResponseOptions Options { get; } = options;
    public IReadOnlyDictionary<string, AgentTool> Tools { get; } = tools;
    internal bool HasRun { get; set; }
}
