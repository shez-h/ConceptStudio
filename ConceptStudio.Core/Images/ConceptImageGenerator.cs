using System.Text.Json;
using OpenAI.Images;

#pragma warning disable OPENAI001

public sealed class ConceptImageGenerator(ImageClient client)
{
    public const string DefaultModel = "gpt-image-2.5-flare";

    public static string BuildPrompt(Concept concept) => $"""
        Create a concept-design visualization of the following design brief.
        Show a single design in the following 3 views, all against a simple studio background: 
         - a clear three-quarter view
         - a clear front view
         - a clear rear view
        Preserve the stated colours, shapes, components, and quantities.
        No captions or labels. This is exploratory concept art, not an engineering drawing.

        Concept title: {concept.Title}
        Concept description: {concept.Description}
        Visual direction: {concept.ImagePrompt}

        Applied studio guidelines to preserve in this visualization:
        {(concept.AppliedGuidelines.Count == 0 ? "None supplied." : string.Join("\n", concept.AppliedGuidelines.Select(guideline => $"- {guideline}")))}
        """;

    public async Task<string> GenerateAsync(
        Concept concept, string outputDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(concept);
        ArgumentException.ThrowIfNullOrWhiteSpace(concept.ImagePrompt);
        string prompt = BuildPrompt(concept);
        Directory.CreateDirectory(outputDirectory);
        string runDirectory = Path.Combine(Path.GetFullPath(outputDirectory),
            $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runDirectory);

        // Save the exact input before the paid request, including for failed attempts.
        await File.WriteAllTextAsync(Path.Combine(runDirectory, "request.json"),
            JsonSerializer.Serialize(new
            {
                model = client.Model, quality = "low", size = "1024x1024",
                format = "png", concept, prompt
            }, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);

        ImageGenerationOptions options = new()
        {
            Size = GeneratedImageSize.W1024xH1024,
            Quality = new GeneratedImageQuality("low"),
            OutputFileFormat = GeneratedImageFileFormat.Png
            // GPT Image returns image bytes. Do not send the legacy response_format option.
        };

        // This is the image API call: POST /v1/images/generations.
        GeneratedImage image = await client.GenerateImageAsync(prompt, options, cancellationToken);
        if (image.ImageBytes is null || image.ImageBytes.ToMemory().IsEmpty)
            throw new InvalidDataException("The image API returned no image bytes.");

        string imagePath = Path.Combine(runDirectory, "preview.png");
        await File.WriteAllBytesAsync(imagePath, image.ImageBytes.ToArray(), cancellationToken);
        return imagePath;
    }
}
