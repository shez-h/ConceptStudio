using System.Text.Json.Serialization;

public sealed record Concept(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("imagePrompt")] string ImagePrompt,
    [property: JsonPropertyName("appliedGuidelines")] List<string> AppliedGuidelines,
    [property: JsonPropertyName("brief")] string? Brief = null,
    [property: JsonPropertyName("referenceImageId")] Guid? ReferenceImageId = null,
    [property: JsonPropertyName("referenceMode")] string? ReferenceMode = null);

public sealed record ConceptResponse(
    [property: JsonPropertyName("concepts")] List<Concept> Concepts);