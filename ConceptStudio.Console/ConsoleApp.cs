using System.ClientModel;
using System.Diagnostics;
using System.Text.Json;
using OpenAI.Responses;
using ModelContextProtocol;

// The SDK currently marks the Responses surface as experimental.
#pragma warning disable OPENAI001

public static class ConsoleApp
{
    public static async Task<int> RunAsync()
    {
        string? apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.Error.WriteLine("Set OPENAI_API_KEY in your terminal first. See README.md.");
            return 1;
        }

        string model = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4.1-mini";
        Console.WriteLine("Describe your concept, or press Enter to use the sample brief:");
        string? enteredBrief = Console.ReadLine();
        string brief = string.IsNullOrWhiteSpace(enteredBrief)
            ? "An underwater exploration vehicle. Friendly, rounded shapes. Follow our studio guidelines."
            : enteredBrief;

        ResponsesClient client = new(apiKey);
        AgentRunner runner = new(client);

        Console.WriteLine($"\nGenerating with {model}...");
        Stopwatch timer = Stopwatch.StartNew();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));

        try
        {
            Console.WriteLine("Connecting to the local guidelines MCP server...");
            await using var mcp = await GuidelinesMcpClient.ConnectAsync(timeout.Token);
            IReadOnlyList<AgentTool> tools = await McpToolAdapter.DiscoverAsync(mcp, timeout.Token);
            foreach (AgentTool tool in tools)
                Console.WriteLine($"Discovered MCP tool: {tool.Name}");
            AgentRequest request = ConceptGenerationRequest.Build(model, brief, tools.ToArray());
            AgentRunResult result = await runner.RunAsync(request, Console.WriteLine, timeout.Token);
            IReadOnlyList<Concept> concepts = ConceptGenerationRequest.ReadConcepts(result.Text, brief: brief);
            Console.WriteLine($"\nCompleted {result.Rounds} round(s) in {timer.Elapsed.TotalSeconds:F1}s");
            Console.WriteLine($"Total tokens: {result.InputTokens?.ToString() ?? "unknown"} input, {result.OutputTokens?.ToString() ?? "unknown"} output");

            for (int index = 0; index < concepts.Count; index++)
            {
                Concept concept = concepts[index];
                Console.WriteLine($"\n=== {index + 1}. {concept.Title} ===");
                Console.WriteLine(concept.Description);
                Console.WriteLine($"Image prompt: {concept.ImagePrompt}");
                Console.WriteLine("Applied guidelines (reported by the model):");
                if (concept.AppliedGuidelines.Count == 0) Console.WriteLine("  None reported.");
                foreach (string guideline in concept.AppliedGuidelines)
                    Console.WriteLine($"  - {guideline}");
            }

            return await ImagePreviewFlow.RunAsync(concepts, apiKey);
        }
        catch (ClientResultException ex)
        {
            // Avoid printing the raw error body, which can contain request details.
            Console.Error.WriteLine($"API request failed (HTTP {ex.Status}).");
            Console.Error.WriteLine(ex.Status switch
            {
                401 => "Check your API key.",
                403 or 404 => "Check project permissions and access to the configured model.",
                429 => "Check API billing, quota, and rate limits before trying again.",
                _ => "Check the API configuration and service status before trying again."
            });
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Stopped waiting after 60 seconds. The provider may still have processed the request.");
            return 1;
        }
        catch (HttpRequestException)
        {
            Console.Error.WriteLine("Could not reach the API. Check your network connection.");
            return 1;
        }
        catch (JsonException)
        {
            Console.Error.WriteLine(
                "The response could not be read as the expected concept data.");
            return 2;
        }
        catch (McpException ex)
        {
            Console.Error.WriteLine($"MCP request failed: {ex.Message}");
            return 2;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"MCP server connection failed: {ex.Message}");
            return 2;
        }
        catch (AgentResponseException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }
}
