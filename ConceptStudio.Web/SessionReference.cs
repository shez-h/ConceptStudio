public sealed record SessionReference(Guid Id, string Name, string MediaType)
{
    public const int MaxBytes = 5 * 1024 * 1024;
    public object View(Guid sessionId) => new { Id, Name, MediaType,
        imageUrl = $"/api/sessions/{sessionId}/references/{Id}" };

    public static string DetectMediaType(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxBytes)
            throw new ArgumentException("Choose a PNG or JPEG up to 5 MB.");
        if (bytes.Length >= 24 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
            bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8)) return "image/png";
        if (bytes.Length >= 4 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255 &&
            bytes[^2] == 255 && bytes[^1] == 217) return "image/jpeg";
        throw new ArgumentException("This file is not a supported PNG or JPEG image.");
    }
}
