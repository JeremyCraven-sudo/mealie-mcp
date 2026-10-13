using System.Text.Json;
using System.Text.Json.Serialization;
using MealieMcp.Mealie;
using ModelContextProtocol;

namespace MealieMcp.Tools;

internal static class ToolHelpers
{
    private static readonly JsonSerializerOptions Output = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serializes a trimmed result for the model.</summary>
    public static string ToJson(object value) => JsonSerializer.Serialize(value, Output);

    /// <summary>
    /// Runs a tool body, turning Mealie failures into MCP errors whose message the model can see
    /// (other exceptions are reported by the SDK without details).
    /// </summary>
    public static async Task<string> Run(Func<Task<string>> body)
    {
        try
        {
            return await body();
        }
        catch (MealieApiException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    public static string? Truncate(string? text, int max) =>
        text is null || text.Length <= max ? text : text[..max].TrimEnd() + "…";
}
