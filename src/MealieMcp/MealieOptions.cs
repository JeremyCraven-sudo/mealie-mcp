namespace MealieMcp;

/// <summary>
/// Connection settings for the Mealie instance. Values come from environment variables so the
/// API token never has to live in source control or in the Claude Desktop config file.
/// </summary>
public sealed record MealieOptions(Uri BaseUrl, string? ApiToken)
{
    public const string BaseUrlVariable = "MEALIE_BASE_URL";
    public const string ApiTokenVariable = "MEALIE_API_TOKEN";
    public const string DefaultBaseUrl = "http://pi4:9925";

    public static MealieOptions FromEnvironment()
    {
        var baseUrl = Read(BaseUrlVariable) ?? DefaultBaseUrl;
        return new MealieOptions(new Uri(baseUrl.TrimEnd('/') + "/"), Read(ApiTokenVariable));
    }

    // Prefer the process environment (what the MCP host passes in), then fall back to the
    // persisted user-level variable so a token set after Claude Desktop started still works.
    private static string? Read(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value) && OperatingSystem.IsWindows())
        {
            value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
        }

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
