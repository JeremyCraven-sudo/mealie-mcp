using System.ComponentModel;
using System.Globalization;
using MealieMcp.Mealie;
using ModelContextProtocol.Server;
using static MealieMcp.Tools.ToolHelpers;

namespace MealieMcp.Tools;

[McpServerToolType]
public static class MealPlanTools
{
    public static readonly string[] EntryTypes = ["breakfast", "lunch", "dinner", "side", "snack", "drink", "dessert"];

    [McpServerTool(Name = "get_meal_plan", Title = "Get meal plan", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Gets meal plan entries between two dates (inclusive). Defaults to the next 7 days.")]
    public static Task<string> GetMealPlan(
        MealieClient mealie,
        [Description("Start date, yyyy-MM-dd. Defaults to today.")] string? startDate = null,
        [Description("End date, yyyy-MM-dd. Defaults to 6 days after the start date.")] string? endDate = null,
        CancellationToken ct = default) => Run(async () =>
    {
        var start = ParseDate(startDate, nameof(startDate)) ?? DateOnly.FromDateTime(DateTime.Now);
        var end = ParseDate(endDate, nameof(endDate)) ?? start.AddDays(6);
        if (end < start)
        {
            throw new MealieApiException("endDate must be on or after startDate.");
        }

        var entries = await mealie.GetMealPlanAsync(start, end, ct);
        return ToJson(entries.Select(e => new
        {
            date = e.Date.ToString("yyyy-MM-dd"),
            day = e.Date.DayOfWeek.ToString(),
            e.EntryType,
            recipe = e.Recipe?.Name,
            recipeSlug = e.Recipe?.Slug,
            title = string.IsNullOrWhiteSpace(e.Title) ? null : e.Title,
            note = string.IsNullOrWhiteSpace(e.Text) ? null : e.Text,
        }));
    });

    [McpServerTool(Name = "add_meal_plan_entry", Title = "Add meal plan entry", Destructive = false, OpenWorld = false)]
    [Description("Adds a recipe (or a free-text meal like \"Leftovers\") to the meal plan on a given date.")]
    public static Task<string> AddMealPlanEntry(
        MealieClient mealie,
        [Description("Date, yyyy-MM-dd.")] string date,
        [Description("One of: breakfast, lunch, dinner, side, snack, drink, dessert.")] string entryType = "dinner",
        [Description("Recipe slug from search_recipes. Omit to add a free-text entry.")] string? recipeSlug = null,
        [Description("Text for the entry when no recipe is given.")] string? title = null,
        CancellationToken ct = default) => Run(async () =>
    {
        var day = ParseDate(date, nameof(date)) ?? throw new MealieApiException("date is required.");
        var type = entryType.Trim().ToLowerInvariant();
        if (!EntryTypes.Contains(type))
        {
            throw new MealieApiException($"entryType must be one of: {string.Join(", ", EntryTypes)}.");
        }

        if (string.IsNullOrWhiteSpace(recipeSlug) && string.IsNullOrWhiteSpace(title))
        {
            throw new MealieApiException("Provide either recipeSlug or title.");
        }

        string? recipeId = null;
        string? recipeName = null;
        if (!string.IsNullOrWhiteSpace(recipeSlug))
        {
            var recipe = await mealie.GetRecipeAsync(recipeSlug, ct);
            (recipeId, recipeName) = (recipe.Id, recipe.Name);
        }

        var entry = await mealie.CreateMealPlanEntryAsync(day, type, recipeId, title, ct);
        return ToJson(new { added = true, entry.Id, date = entry.Date.ToString("yyyy-MM-dd"), entryType = type, recipe = recipeName, title });
    });

    private static DateOnly? ParseDate(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : throw new MealieApiException($"{name} must be in yyyy-MM-dd format, got '{value}'.");
    }
}
