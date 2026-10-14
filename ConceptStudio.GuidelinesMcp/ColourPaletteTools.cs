using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

[McpServerToolType]
public static class ColourPaletteTools
{
    [McpServerTool(Name = "get_colour_palette")]
    [Description(
        "Looks up a named studio colour palette. Available names: ocean-rescue and sunset. Returns colour names and hex codes, not design guidelines."
    )]
    public static CallToolResult GetColourPalette(
        [Description("The palette name requested by the designer, such as ocean-rescue or sunset.")]
            string paletteName
    )
    {
        // Fictional local data for this exercise. An API or database lookup could live here.
        object? palette = paletteName?.Trim().ToLowerInvariant() switch
        {
            "ocean-rescue" => new
            {
                name = "ocean-rescue",
                colours = new[]
                {
                    new { name = "Rescue yellow", hex = "#FFD400" },
                    new { name = "Ocean teal", hex = "#008C95" },
                },
            },
            "sunset" => new
            {
                name = "sunset",
                colours = new[]
                {
                    new { name = "Warm orange", hex = "#F97316" },
                    new { name = "Deep purple", hex = "#6D28D9" },
                },
            },
            _ => null,
        };
        return new CallToolResult
        {
            IsError = palette is null,
            Content =
            [
                new TextContentBlock
                {
                    Text = JsonSerializer.Serialize(
                        palette
                            ?? new
                            {
                                error = "Unknown palette. Available palettes: ocean-rescue, sunset.",
                            }
                    ),
                },
            ],
        };
    }
}
