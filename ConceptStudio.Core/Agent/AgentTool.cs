using OpenAI.Responses;

#pragma warning disable OPENAI001

// The schema tells the model how to request a call. The handler is our executable code.
public sealed record AgentTool(
    string Name,
    string Description,
    string ParametersSchema,
    Func<string, CancellationToken, Task<string>> Handler,
    bool StrictModeEnabled = true)
{
    public ResponseTool CreateDefinition() => ResponseTool.CreateFunctionTool(
        functionName: Name,
        functionParameters: BinaryData.FromString(ParametersSchema),
        strictModeEnabled: StrictModeEnabled,
        functionDescription: Description);
}
