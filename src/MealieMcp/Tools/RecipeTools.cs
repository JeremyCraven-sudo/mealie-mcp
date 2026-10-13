using System.ComponentModel;
using MealieMcp.Mealie;
using ModelContextProtocol.Server;
using static MealieMcp.Tools.ToolHelpers;

namespace MealieMcp.Tools;

[McpServerToolType]
public static class RecipeTools
{
    [McpServerTool(Name = "search_recipes", Title = "Search recipes", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Searches recipes in Mealie by name, description, or ingredient text. Returns slugs to use with get_recipe.")]
    public static Task<string> SearchRecipes(
        MealieClient mealie,
        [Description("Text to search for. Leave empty to list all recipes.")] string? query = null,
        [Description("1-based page number.")] int page = 1,
        [Description("Results per page (max 50).")] int perPage = 20,
        CancellationToken ct = default) => Run(async () =>
    {
        var result = await mealie.SearchRecipesAsync(query, Math.Max(1, page), Math.Clamp(perPage, 1, 50), ct);
        return ToJson(new
        {
            result.Total,
            page = result.PageNumber,
            result.TotalPages,
            recipes = result.Items.Select(r => new
            {
                r.Slug,
                r.Name,
                description = Truncate(r.Description, 160),
                r.TotalTime,
                r.RecipeYield,
                tags = r.Tags?.Select(t => t.Name),
            }),
        });
    });

    [McpServerTool(Name = "get_recipe", Title = "Get recipe", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Gets a recipe's ingredients and instructions by its slug.")]
    public static Task<string> GetRecipe(
        MealieClient mealie,
        [Description("Recipe slug, as returned by search_recipes.")] string slug,
        CancellationToken ct = default) => Run(async () =>
    {
        var r = await mealie.GetRecipeAsync(slug, ct);
        return ToJson(new
        {
            r.Id,
            r.Slug,
            r.Name,
            r.Description,
            servings = r.RecipeServings,
            r.RecipeYield,
            r.TotalTime,
            r.PrepTime,
            r.CookTime,
            sourceUrl = r.OrgUrl,
            tags = r.Tags?.Select(t => t.Name),
            ingredients = r.RecipeIngredient?.Select(i =>
                string.IsNullOrWhiteSpace(i.Display) ? i.Note ?? i.OriginalText : i.Display),
            instructions = r.RecipeInstructions?.Select(s =>
                string.IsNullOrWhiteSpace(s.Title) ? s.Text : $"{s.Title}: {s.Text}"),
        });
    });

    [McpServerTool(Name = "import_recipe_from_url", Title = "Import recipe from URL", Destructive = false, OpenWorld = true)]
    [Description("Imports a recipe into Mealie by scraping a recipe web page. Returns the new recipe's slug.")]
    public static Task<string> ImportRecipeFromUrl(
        MealieClient mealie,
        [Description("Full URL of the recipe page.")] string url,
        CancellationToken ct = default) => Run(async () =>
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new MealieApiException($"'{url}' is not a valid http(s) URL.");
        }

        var slug = await mealie.ImportRecipeFromUrlAsync(uri.ToString(), ct);
        return ToJson(new { imported = true, slug });
    });
}
