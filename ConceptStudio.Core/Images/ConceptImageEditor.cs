using System.Text.Json;
using OpenAI.Images;

#pragma warning disable OPENAI001

public sealed class ConceptImageEditor(ImageClient client)
{
    public static string BuildPrompt(Concept concept, string feedback) =>
        $"""
            Edit the supplied concept visualization according to the designer's feedback.
            Preserve the existing design, composition, colours, and all unrelated details.
            Do not add captions or labels.

            Studio guidelines (unless explicitly changed by the designer's feedback):
            {(
                concept.AppliedGuidelines.Count == 0
                    ? "None supplied."
                    : string.Join("\n", concept.AppliedGuidelines.Select(g => $"- {g}"))
            )}

            Designer feedback:
            {feedback}
            """;

    public async Task<string> EditAsync(
        Concept concept,
        string sourceImagePath,
        string feedback,
        string outputDirectory,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(concept);
        ArgumentException.ThrowIfNullOrWhiteSpace(feedback);
        // Open the existing PNG before making a request. A missing file fails locally.
        await using var source = File.OpenRead(sourceImagePath);
        string prompt = BuildPrompt(concept, feedback);
        string runDirectory = Path.Combine(
            Path.GetFullPath(outputDirectory),
            $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(runDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(runDirectory, "request.json"),
            JsonSerializer.Serialize(
                new
                {
                    operation = "edit",
                    sourceImagePath = Path.GetFullPath(sourceImagePath),
                    feedback,
                    model = client.Model,
                    quality = "low",
                    size = "1024x1024",
                    format = "png",
                    concept,
                    prompt,
                },
                new JsonSerializerOptions { WriteIndented = true }
            ),
            cancellationToken
        );

        ImageEditOptions options = new()
        {
            Size = GeneratedImageSize.W1024xH1024,
            Quality = new GeneratedImageQuality("low"),
            OutputFileFormat = GeneratedImageFileFormat.Png,
        };

        // POST /v1/images/edits: upload the actual image bytes alongside the new prompt.
        GeneratedImage image = await client.GenerateImageEditAsync(
            source,
            Path.GetFileName(sourceImagePath),
            prompt,
            options,
            cancellationToken
        );
        if (image.ImageBytes is null || image.ImageBytes.ToMemory().IsEmpty)
            throw new InvalidDataException("The image API returned no image bytes.");

        string imagePath = Path.Combine(runDirectory, "preview.png");
        await File.WriteAllBytesAsync(imagePath, image.ImageBytes.ToArray(), cancellationToken);
        return imagePath;
    }
}
