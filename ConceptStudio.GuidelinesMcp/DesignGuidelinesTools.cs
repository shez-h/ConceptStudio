using System.ComponentModel;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

[McpServerToolType]
public static class DesignGuidelinesTools
{
    [McpServerTool(Name = "get_design_guidelines")]
    [Description("Retrieves the studio's internal design guidelines for a theme.")]
    public static CallToolResult GetDesignGuidelines(
        [Description("The design theme, such as underwater or space.")] string theme
    )
    {
        if (string.IsNullOrWhiteSpace(theme))
            return Result(new { error = "A non-empty theme is required" }, isError: true);

        return GetGuidelinesForTheme(theme);
    }

    private static CallToolResult GetGuidelinesForTheme(string theme)
    {
        return theme.ToLowerInvariant() switch
        {
            "underwater" => Result(
                new
                {
                    theme,
                    guidelines = new[]
                    {
                        "Use yellow and teal as the primary colours.",
                        "Include exactly two circular observation windows.",
                        "Include a visible rescue beacon.",
                        "Avoid external robotic arms.",
                    },
                }
            ),
            "adventure" => Result(
                new
                {
                    theme,
                    guidelines = new[]
                    {
                        "Use earthy tones, associated with outdoor adventure.",
                        "Include items associated with a fantasy adventure, such as a sword and shield.",
                        "Include a backpack with multiple compartments.",
                        "Avoid fragile materials.",
                    },
                }
            ),
            _ when String.IsNullOrWhiteSpace(theme) => Result(
                new { error = "A non-empty theme is required" },
                isError: true
            ),
            _ => Result(new { error = "No guidelines found for this theme" }, isError: true),
        };
    }

    private static CallToolResult Result(object value, bool isError = false) =>
        new()
        {
            IsError = isError,
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(value) }],
        };
}
