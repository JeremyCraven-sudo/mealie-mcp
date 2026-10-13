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
    [Description("Imports a recipe into Mealie by scraping a recipe web page. Returns the new recipe's slug, " +
                 "and a warning if the page had no recipe data (the empty recipe must then be deleted in Mealie).")]
    public static Task<string> ImportRecipeFromUrl(
        MealieClient mealie,
        [Description("Full URL of the recipe page.")] string url,
        [Description("Also import the page's keywords as tags. Off by default: many sites put the same site-wide keywords on every page.")]
        bool includeTags = false,
        CancellationToken ct = default) => Run(async () =>
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new MealieApiException($"'{url}' is not a valid http(s) URL.");
        }

        var slug = await mealie.ImportRecipeFromUrlAsync(uri.ToString(), includeTags, ct);
        var recipe = await mealie.GetRecipeAsync(slug, ct);
        var ingredientCount = recipe.RecipeIngredient?.Count(i => !IsPlaceholder(i.Display ?? i.Note)) ?? 0;
        var stepCount = recipe.RecipeInstructions?.Count(s => !IsPlaceholder(s.Text)) ?? 0;

        if (ingredientCount == 0 && stepCount == 0)
        {
            return ToJson(new
            {
                imported = false,
                slug,
                recipe.Name,
                warning = "Mealie found no recipe on this page (it may not be a recipe page, or the site blocks scraping). " +
                          "An empty recipe was created; delete it in Mealie.",
            });
        }

        return ToJson(new { imported = true, slug, recipe.Name, ingredientCount, stepCount });
    });

    // Mealie fills scrapes that found nothing with these placeholder lines.
    internal static bool IsPlaceholder(string? text) =>
        string.IsNullOrWhiteSpace(text) || text.StartsWith("Could not detect", StringComparison.OrdinalIgnoreCase);
}
