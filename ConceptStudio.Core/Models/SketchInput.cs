// Server-owned path, never accepted directly from a browser request.
public sealed record SketchInput(string Path, string MediaType, string Mode)
{
    public string Guidance =>
        Mode == "preserve"
            ? "Use the reference as the design foundation. Preserve its main silhouette, proportions and key components unless the written brief explicitly changes them."
            : "Use the reference as loose visual inspiration. Explore new shapes and proportions that fit the written brief.";
}
