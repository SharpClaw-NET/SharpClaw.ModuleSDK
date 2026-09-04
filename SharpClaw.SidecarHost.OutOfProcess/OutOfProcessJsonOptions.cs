using System.Text.Json;

internal static class OutOfProcessJsonOptions
{
    public static readonly JsonSerializerOptions Manifest = new()
    {
        MaxDepth = 8,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true,
    };
}
