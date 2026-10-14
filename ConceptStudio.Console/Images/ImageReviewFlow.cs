using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using OpenAI;
using OpenAI.Responses;

#pragma warning disable OPENAI001

public static class ImageReviewFlow
{
    public static async Task RunAsync(
        string imagePath,
        IReadOnlyList<string> guidelines,
        string apiKey
    )
    {
        if (guidelines.Count == 0)
        {
            Console.WriteLine("No applied guidelines to review.");
            return;
        }
        string model =
            Environment.GetEnvironmentVariable("OPENAI_REVIEW_MODEL")
            ?? ConceptImageReviewer.DefaultModel;
        Console.WriteLine(
            $"Reviewing against the original applied guidelines with {model} (paid API call)..."
        );
        try
        {
            ResponsesClient client = new(
                new ApiKeyCredential(apiKey),
                new ResponsesClientOptions
                {
                    RetryPolicy = new ClientRetryPolicy(0),
                    NetworkTimeout = TimeSpan.FromSeconds(60),
                }
            );
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            ImageReview review = await new ConceptImageReviewer(client, model).ReviewAsync(
                imagePath,
                guidelines,
                timeout.Token
            );
            Console.WriteLine("Advisory review — check these findings yourself:");
            foreach (var finding in review.Findings.OrderBy(f => f.Id))
                Console.WriteLine(
                    $"  [{finding.Verdict}] {guidelines[finding.Id - 1]}\n    {finding.Explanation}"
                );
            Console.WriteLine(
                $"Review saved: {Path.Combine(Path.GetDirectoryName(imagePath)!, "review.json")}"
            );
        }
        catch (Exception ex)
            when (ex
                    is ClientResultException
                        or HttpRequestException
                        or OperationCanceledException
                        or JsonException
                        or AgentResponseException
                        or IOException
                        or UnauthorizedAccessException
            )
        {
            string reason = ex switch
            {
                ClientResultException api => $"API HTTP {api.Status}",
                OperationCanceledException => "timed out after 60 seconds",
                _ => ex.GetType().Name,
            };
            Console.WriteLine(
                $"Review unavailable ({reason}). Your image is saved; you can still edit it."
            );
        }
    }
}
