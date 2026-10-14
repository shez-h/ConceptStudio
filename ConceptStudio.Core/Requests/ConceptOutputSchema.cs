public static class ConceptOutputSchema
{
    public static string Json => ForCount(3);

    public static string ForCount(int count)
    {
        if (count is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(count), "Choose 1 to 3 concepts.");
        return $$"""
            {
              "type": "object",
              "properties": {
                "concepts": {
                  "type": "array",
                  "minItems": {{count}},
                  "maxItems": {{count}},
                  "items": {
                    "type": "object",
                    "properties": {
                      "title": {
                        "type": "string"
                      },
                      "description": {
                        "type": "string"
                      },
                      "imagePrompt": {
                        "type": "string"
                      },
                      "appliedGuidelines": {
                        "type": "array",
                        "items": { "type": "string" }
                      }
                    },
                    "required": [
                      "title",
                      "description",
                      "imagePrompt",
                      "appliedGuidelines"
                    ],
                    "additionalProperties": false
                  }
                }
              },
              "required": [
                "concepts"
              ],
              "additionalProperties": false
            }
            """;
    }
}
