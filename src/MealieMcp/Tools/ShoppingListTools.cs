using System.ComponentModel;
using MealieMcp.Mealie;
using ModelContextProtocol.Server;
using static MealieMcp.Tools.ToolHelpers;

namespace MealieMcp.Tools;

[McpServerToolType]
public static class ShoppingListTools
{
    [McpServerTool(Name = "list_shopping_lists", Title = "List shopping lists", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the household's shopping lists with their IDs.")]
    public static Task<string> ListShoppingLists(MealieClient mealie, CancellationToken ct = default) => Run(async () =>
    {
        var lists = await mealie.GetShoppingListsAsync(ct);
        return ToJson(lists.Select(l => new { l.Id, l.Name }));
    });

    [McpServerTool(Name = "create_shopping_list", Title = "Create shopping list", Destructive = false, OpenWorld = false)]
    [Description("Creates a new shopping list, unless one with the same name already exists. Returns its ID.")]
    public static Task<string> CreateShoppingList(
        MealieClient mealie,
        [Description("Name for the list, e.g. \"Food Lion\".")] string name,
        CancellationToken ct = default) => Run(async () =>
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            throw new MealieApiException("List name is required.");
        }

        var existing = (await mealie.GetShoppingListsAsync(ct))
            .FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return ToJson(new { created = false, existing.Id, existing.Name });
        }

        var list = await mealie.CreateShoppingListAsync(name, ct);
        return ToJson(new { created = true, list.Id, list.Name });
    });

    [McpServerTool(Name = "get_shopping_list", Title = "Get shopping list", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Gets the items on a shopping list, grouped by store section (label). By default only unchecked items are returned.")]
    public static Task<string> GetShoppingList(
        MealieClient mealie,
        [Description("Shopping list ID from list_shopping_lists.")] string listId,
        [Description("Also include items already checked off.")] bool includeChecked = false,
        CancellationToken ct = default) => Run(async () =>
    {
        var list = await mealie.GetShoppingListAsync(listId, ct);
        var items = (list.ListItems ?? [])
            .Where(i => includeChecked || !i.Checked)
            .OrderBy(i => i.Position);

        return ToJson(new
        {
            list.Id,
            list.Name,
            sections = items
                .GroupBy(i => i.Label?.Name ?? "Other")
                .OrderBy(g => g.Key == "Other" ? 1 : 0).ThenBy(g => g.Key)
                .Select(g => new
                {
                    section = g.Key,
                    items = g.Select(i => new
                    {
                        i.Id,
                        text = string.IsNullOrWhiteSpace(i.Display) ? i.Note : i.Display,
                        @checked = includeChecked ? i.Checked : (bool?)null,
                    }),
                }),
        });
    });

    [McpServerTool(Name = "add_shopping_list_items", Title = "Add shopping list items", Destructive = false, OpenWorld = false)]
    [Description("Adds free-text items (e.g. \"2 lb chicken thighs\") to a shopping list.")]
    public static Task<string> AddShoppingListItems(
        MealieClient mealie,
        [Description("Shopping list ID from list_shopping_lists.")] string listId,
        [Description("Items to add, one entry per item.")] string[] items,
        CancellationToken ct = default) => Run(async () =>
    {
        var cleaned = items.Select(i => i.Trim()).Where(i => i.Length > 0).ToArray();
        if (cleaned.Length == 0)
        {
            throw new MealieApiException("No items to add.");
        }

        await mealie.AddShoppingListItemsAsync(listId, cleaned, ct);
        return ToJson(new { added = cleaned.Length, items = cleaned });
    });

    [McpServerTool(Name = "add_recipe_to_shopping_list", Title = "Add recipe ingredients to shopping list", Destructive = false, OpenWorld = false)]
    [Description("Adds all of a recipe's ingredients to a shopping list, merging with matching items already on it.")]
    public static Task<string> AddRecipeToShoppingList(
        MealieClient mealie,
        [Description("Shopping list ID from list_shopping_lists.")] string listId,
        [Description("Recipe slug from search_recipes.")] string recipeSlug,
        [Description("Multiplier for ingredient quantities, e.g. 2 for a double batch.")] double scale = 1,
        CancellationToken ct = default) => Run(async () =>
    {
        if (scale <= 0)
        {
            throw new MealieApiException("Scale must be greater than zero.");
        }

        var recipe = await mealie.GetRecipeAsync(recipeSlug, ct);
        if (recipe.Id is null)
        {
            throw new MealieApiException($"Recipe '{recipeSlug}' has no ID.");
        }

        await mealie.AddRecipeToShoppingListAsync(listId, recipe.Id, scale, ct);
        return ToJson(new { added = true, recipe = recipe.Name, scale, ingredientCount = recipe.RecipeIngredient?.Count ?? 0 });
    });

    [McpServerTool(Name = "set_shopping_list_items_checked", Title = "Check off shopping list items", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Checks off (or un-checks) items on a shopping list by item ID, e.g. after they've been added to a grocery cart.")]
    public static Task<string> SetShoppingListItemsChecked(
        MealieClient mealie,
        [Description("Shopping list ID from list_shopping_lists.")] string listId,
        [Description("Item IDs from get_shopping_list.")] string[] itemIds,
        [Description("true to check items off, false to un-check them.")] bool isChecked = true,
        CancellationToken ct = default) => Run(async () =>
    {
        var notFound = await mealie.SetItemsCheckedAsync(listId, itemIds, isChecked, ct);
        return ToJson(new { updated = itemIds.Length - notFound.Count, notFound });
    });
}
