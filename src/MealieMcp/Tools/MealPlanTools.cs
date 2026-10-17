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
    [Description("Gets meal plan entries between two dates (inclusive). Defaults to the next 7 days. " +
                 "Each entry's id is what update_meal_plan_entry and remove_meal_plan_entry take.")]
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
            e.Id,
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
        var type = ParseEntryType(entryType);

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

    [McpServerTool(Name = "update_meal_plan_entry", Title = "Change meal plan entry", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Changes a meal plan entry: move it to another date or meal, swap its recipe, or change its text. Fields left out are unchanged.")]
    public static Task<string> UpdateMealPlanEntry(
        MealieClient mealie,
        [Description("Entry id from get_meal_plan.")] int entryId,
        [Description("New date, yyyy-MM-dd.")] string? date = null,
        [Description("New meal: breakfast, lunch, dinner, side, snack, drink, or dessert.")] string? entryType = null,
        [Description("Recipe slug from search_recipes to put on the entry instead.")] string? recipeSlug = null,
        [Description("New text for the entry.")] string? title = null,
        [Description("Remove the recipe, leaving a free-text entry (needs a title if the entry has none).")] bool clearRecipe = false,
        CancellationToken ct = default) => Run(async () =>
    {
        var day = ParseDate(date, nameof(date));
        var type = entryType is null ? null : ParseEntryType(entryType);
        if (clearRecipe && !string.IsNullOrWhiteSpace(recipeSlug))
        {
            throw new MealieApiException("Pass either recipeSlug or clearRecipe, not both.");
        }

        string? recipeId = null;
        if (!string.IsNullOrWhiteSpace(recipeSlug))
        {
            recipeId = (await mealie.GetRecipeAsync(recipeSlug, ct)).Id;
        }

        var entry = await mealie.UpdateMealPlanEntryAsync(entryId, e =>
        {
            if (day is not null) e["date"] = day.Value.ToString("yyyy-MM-dd");
            if (type is not null) e["entryType"] = type;
            if (recipeId is not null) e["recipeId"] = recipeId;
            if (clearRecipe) e["recipeId"] = null;
            if (title is not null) e["title"] = title.Trim();

            if (e["recipeId"] is null && string.IsNullOrWhiteSpace(e["title"]?.GetValue<string>()))
            {
                throw new MealieApiException("Without a recipe the entry needs a title.");
            }
        }, ct);

        return ToJson(new
        {
            updated = true,
            entry.Id,
            date = entry.Date.ToString("yyyy-MM-dd"),
            entry.EntryType,
            recipe = entry.Recipe?.Name,
            title = string.IsNullOrWhiteSpace(entry.Title) ? null : entry.Title,
        });
    });

    [McpServerTool(Name = "remove_meal_plan_entry", Title = "Remove meal plan entry", Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Removes an entry from the meal plan. Only the plan entry is removed; the recipe itself is not affected.")]
    public static Task<string> RemoveMealPlanEntry(
        MealieClient mealie,
        [Description("Entry id from get_meal_plan.")] int entryId,
        CancellationToken ct = default) => Run(async () =>
    {
        var entry = await mealie.DeleteMealPlanEntryAsync(entryId, ct);
        return ToJson(new
        {
            removed = true,
            entry.Id,
            date = entry.Date.ToString("yyyy-MM-dd"),
            entry.EntryType,
            recipe = entry.Recipe?.Name,
            title = string.IsNullOrWhiteSpace(entry.Title) ? null : entry.Title,
        });
    });

    private static string ParseEntryType(string value)
    {
        var type = value.Trim().ToLowerInvariant();
        return EntryTypes.Contains(type)
            ? type
            : throw new MealieApiException($"entryType must be one of: {string.Join(", ", EntryTypes)}.");
    }

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
