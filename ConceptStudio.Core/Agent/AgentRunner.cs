using System.Text.Json;
using OpenAI.Responses;

#pragma warning disable OPENAI001

public sealed class AgentRunner(ResponsesClient client)
{
    public async Task<AgentRunResult> RunAsync(
        AgentRequest request,
        Action<string>? trace = null,
        CancellationToken cancellationToken = default)
    {
        if (request.HasRun)
            throw new InvalidOperationException("Build a fresh request for each run.");
        request.HasRun = true;
        long inputTokens = 0, outputTokens = 0;
        bool completeUsage = true;
        const int maxRounds = 4;

        for (int round = 1; round <= maxRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResponseResult response = await client.CreateResponseAsync(
                request.Options, cancellationToken);

            trace?.Invoke($"Round {round}: {response.Status}");
            if (response.Usage is { } usage)
            {
                inputTokens += usage.InputTokenCount;
                outputTokens += usage.OutputTokenCount;
                trace?.Invoke($"Tokens: {usage.InputTokenCount} input, {usage.OutputTokenCount} output");
            }
            else completeUsage = false;

            if (response.Status != ResponseStatus.Completed)
                throw new AgentResponseException($"Generation did not complete ({response.Status}).");

            if (response.OutputItems.OfType<MessageResponseItem>()
                .SelectMany(message => message.Content)
                .Any(part => part.Kind == ResponseContentPartKind.Refusal))
                throw new AgentResponseException("The model declined this request. Try revising the brief.");

            var calls = response.OutputItems.OfType<FunctionCallResponseItem>().ToList();
            if (calls.Count == 0)
            {
                string text = response.GetOutputText();
                if (string.IsNullOrWhiteSpace(text))
                    throw new AgentResponseException("The response contained no output text.");
                return new AgentRunResult(text, round,
                    completeUsage ? inputTokens : null, completeUsage ? outputTokens : null);
            }

            // Do not execute more tools if we cannot make another model request.
            if (round == maxRounds)
                throw new AgentResponseException("Stopped: tool-call round limit reached.");

            foreach (ResponseItem item in response.OutputItems)
                request.Options.InputItems.Add(item);

            foreach (FunctionCallResponseItem call in calls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                trace?.Invoke($"Tool requested: {call.FunctionName} ({call.CallId})");
                trace?.Invoke($"Arguments: {call.FunctionArguments}");

                string output = await ExecuteToolAsync(request, call, cancellationToken);
                trace?.Invoke($"Tool result: {output}");

                // CallId links our result to the model's original request.
                request.Options.InputItems.Add(ResponseItem.CreateFunctionCallOutputItem(
                    call.CallId, output));
            }
        }

        throw new AgentResponseException("Stopped: tool-call round limit reached.");
    }

    public static async Task<string> ExecuteToolAsync(
        AgentRequest request,
        FunctionCallResponseItem call,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.Tools.TryGetValue(call.FunctionName, out AgentTool? tool))
            return """{"error":"Unknown tool"}""";

        try
        {
            // This is where the model's requested function ACTUALLY runs in C#.
            return await tool.Handler(call.FunctionArguments.ToString(), cancellationToken);
        }
        catch (JsonException)
        {
            return """{"error":"Tool arguments were not valid JSON"}""";
        }
    }
}

public sealed record AgentRunResult(string Text, int Rounds, long? InputTokens, long? OutputTokens);
public sealed class AgentResponseException(string message) : Exception(message);
