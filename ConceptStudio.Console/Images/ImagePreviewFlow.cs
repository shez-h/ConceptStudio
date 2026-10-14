using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using OpenAI;
using OpenAI.Images;

public static class ImagePreviewFlow
{
    public static async Task<int> RunAsync(IReadOnlyList<Concept> concepts, string apiKey)
    {
        string model =
            Environment.GetEnvironmentVariable("OPENAI_IMAGE_MODEL")
            ?? ConceptImageGenerator.DefaultModel;
        Console.WriteLine(
            $"\nGenerate one preview with {model} (1024×1024, low quality; paid API call)."
        );
        Console.WriteLine($"Choose 1–{concepts.Count}, or press Enter to skip:");

        int selection;
        while (true)
        {
            string? input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input))
                return 0;
            if (int.TryParse(input, out selection) && selection >= 1 && selection <= concepts.Count)
                break;
            Console.WriteLine($"Enter a number from 1 to {concepts.Count}, or Enter to skip.");
        }

        // Separate client, model, and timeout from text generation and MCP.
        ImageClient client = new(
            model,
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions
            {
                RetryPolicy = new ClientRetryPolicy(0),
                NetworkTimeout = TimeSpan.FromMinutes(3),
            }
        );
        ConceptImageGenerator generator = new(client);
        string outputDirectory =
            Environment.GetEnvironmentVariable("CONCEPT_STUDIO_OUTPUT_DIR")
            ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "images");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        Stopwatch timer = Stopwatch.StartNew();
        Console.WriteLine($"Generating preview for '{concepts[selection - 1].Title}'...");

        try
        {
            string imagePath = await generator.GenerateAsync(
                concepts[selection - 1],
                outputDirectory,
                timeout.Token
            );
            Console.WriteLine($"Image saved: {imagePath}");
            Console.WriteLine(
                $"Prompt and settings: {Path.Combine(Path.GetDirectoryName(imagePath)!, "request.json")}"
            );
            Console.WriteLine($"Image generation took {timer.Elapsed.TotalSeconds:F1}s.");
            await ImageReviewFlow.RunAsync(
                imagePath,
                concepts[selection - 1].AppliedGuidelines,
                apiKey
            );
            ConceptImageEditor editor = new(client);
            while (true)
            {
                Console.WriteLine(
                    "\nReview the saved image, then type an edit (paid API call), or press Enter to finish:"
                );
                string? feedback = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(feedback))
                    break;

                // Each edit starts with the latest successful image and its own timeout.
                using var editTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                timer.Restart();
                Console.WriteLine("Editing preview...");
                imagePath = await editor.EditAsync(
                    concepts[selection - 1],
                    imagePath,
                    feedback,
                    outputDirectory,
                    editTimeout.Token
                );
                Console.WriteLine($"Edited image saved: {imagePath}");
                Console.WriteLine(
                    $"Prompt and settings: {Path.Combine(Path.GetDirectoryName(imagePath)!, "request.json")}"
                );
                Console.WriteLine($"Image edit took {timer.Elapsed.TotalSeconds:F1}s.");
                await ImageReviewFlow.RunAsync(
                    imagePath,
                    concepts[selection - 1].AppliedGuidelines,
                    apiKey
                );
            }
            return 0;
        }
        catch (ClientResultException ex)
        {
            Console.Error.WriteLine(
                $"Image API failed (HTTP {ex.Status}). Your text concepts remain above."
            );
            Console.Error.WriteLine(
                ex.Status switch
                {
                    401 => "Check your API key.",
                    403 =>
                        "Check image-model access and whether your API organisation requires verification.",
                    404 => "Check OPENAI_IMAGE_MODEL and project access to that model.",
                    429 => "Check billing, quota, or rate limits before retrying.",
                    400 =>
                        "The image request was rejected. Review the prompt, model, and image settings.",
                    _ =>
                        "Check service availability before retrying. The request has not been automatically retried.",
                }
            );
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine(
                "Stopped waiting for the image after three minutes. The provider may still have processed it."
            );
        }
        catch (HttpRequestException)
        {
            Console.Error.WriteLine(
                "The image API could not be reached. The request has not been automatically retried."
            );
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Image output could not be saved: {ex.Message}");
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                "Cannot write image files. Choose a writable CONCEPT_STUDIO_OUTPUT_DIR."
            );
        }
        return 2;
    }
}
