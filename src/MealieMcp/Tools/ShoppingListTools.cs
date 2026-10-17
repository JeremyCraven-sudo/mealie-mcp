using System.ComponentModel;
using System.Text.RegularExpressions;
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

    [McpServerTool(Name = "rename_shopping_list", Title = "Rename shopping list", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Renames a shopping list, e.g. to label it for a store or a specific order (\"Food Lion pickup 10/17\").")]
    public static Task<string> RenameShoppingList(
        MealieClient mealie,
        [Description("Shopping list ID from list_shopping_lists.")] string listId,
        [Description("New name for the list.")] string name,
        CancellationToken ct = default) => Run(async () =>
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            throw new MealieApiException("List name is required.");
        }

        var lists = await mealie.GetShoppingListsAsync(ct);
        var current = lists.FirstOrDefault(l => string.Equals(l.Id, listId, StringComparison.OrdinalIgnoreCase))
                      ?? throw new MealieApiException($"No shopping list with ID '{listId}'.");
        if (lists.Any(l => l != current && string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new MealieApiException($"Another list is already named '{name}'.");
        }

        await mealie.RenameShoppingListAsync(current.Id, name, ct);
        return ToJson(new { renamed = true, current.Id, oldName = current.Name, name });
    });

    [McpServerTool(Name = "get_shopping_list", Title = "Get shopping list", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Gets the items on a shopping list, grouped by store section (label). By default only unchecked items are returned. " +
                 "Items marked with set_shopping_list_items_skipped come back with skipped=true.")]
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
                        skipped = SkipMarker.IsMatch(i.Note ?? "") ? true : (bool?)null,
                    }),
                }),
        });
    });

    [McpServerTool(Name = "add_shopping_list_items", Title = "Add shopping list items", Destructive = false, OpenWorld = false)]
    [Description("Adds free-text items (e.g. \"2 lb chicken thighs\") to a shopping list. Each item is filed under a store " +
                 "section: the one given, or else one guessed from a matching food in Mealie (items with no match go under Other). " +
                 "The item text is kept exactly as written.")]
    public static Task<string> AddShoppingListItems(
        MealieClient mealie,
        [Description("Shopping list ID from list_shopping_lists.")] string listId,
        [Description("Items to add, one entry per item.")] string[] items,
        [Description("Store section for all the items, e.g. \"Produce\". Must be an existing section name.")] string? section = null,
        [Description("When no section is given, guess each item's section from the food it mentions.")] bool guessSections = true,
        CancellationToken ct = default) => Run(async () =>
    {
        var cleaned = items.Select(i => i.Trim()).Where(i => i.Length > 0).ToArray();
        if (cleaned.Length == 0)
        {
            throw new MealieApiException("No items to add.");
        }

        var labels = await mealie.GetLabelsAsync(ct);
        var sections = new Label?[cleaned.Length];
        if (!string.IsNullOrWhiteSpace(section))
        {
            Array.Fill(sections, FindLabel(labels, section));
        }
        else if (guessSections)
        {
            var parsed = await mealie.ParseIngredientsAsync(cleaned, ct);
            for (var i = 0; i < cleaned.Length; i++)
            {
                var line = IngredientParsing.Classify(i, cleaned[i], parsed[i]!.AsObject());
                if (line is { Outcome: ParseOutcome.Matched, Section: { } name })
                {
                    sections[i] = labels.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
                }
            }
        }

        await mealie.AddShoppingListItemsAsync(listId, cleaned.Select((t, i) => (t, sections[i]?.Id)), ct);
        return ToJson(new
        {
            added = cleaned.Length,
            items = cleaned.Select((t, i) => new { text = t, section = sections[i]?.Name ?? "Other" }),
        });
    });

    [McpServerTool(Name = "update_shopping_list_items", Title = "Edit shopping list items", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Edits items on a shopping list: change the text (e.g. to record the exact product bought or a substitution), " +
                 "the quantity, or the store section. Fields left out are unchanged.")]
    public static Task<string> UpdateShoppingListItems(
        MealieClient mealie,
        [Description("Shopping list ID from list_shopping_lists.")] string listId,
        [Description("One edit per item.")] ShoppingItemEdit[] edits,
        CancellationToken ct = default) => Run(async () =>
    {
        if (edits.Length == 0)
        {
            throw new MealieApiException("No edits given.");
        }

        var labels = edits.Any(e => !string.IsNullOrWhiteSpace(e.Section)) ? await mealie.GetLabelsAsync(ct) : [];
        var byId = new Dictionary<string, (string? Text, double? Quantity, Label? Label)>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in edits)
        {
            var text = e.Text?.Trim();
            if (text is { Length: 0 })
            {
                throw new MealieApiException($"Text for item {e.ItemId} can't be empty.");
            }

            if (e.Quantity < 0)
            {
                throw new MealieApiException($"Quantity for item {e.ItemId} can't be negative.");
            }

            byId[e.ItemId] = (text, e.Quantity, string.IsNullOrWhiteSpace(e.Section) ? null : FindLabel(labels, e.Section));
        }

        var notFound = await mealie.UpdateItemsAsync(listId, byId.Keys, (id, item) =>
        {
            var (text, quantity, label) = byId[id];
            if (text is not null)
            {
                // New wording replaces the parsed food/unit, making it a plain-text item; its section is kept.
                item["note"] = text;
                foreach (var key in new[] { "food", "foodId", "unit", "unitId" })
                {
                    item[key] = null;
                }
            }

            if (quantity is not null)
            {
                item["quantity"] = quantity;
            }

            if (label is not null)
            {
                item["labelId"] = label.Id;
                item.Remove("label");
            }

            item.Remove("display");
        }, ct);

        return ToJson(new { updated = byId.Count - notFound.Count, notFound });
    });

    [McpServerTool(Name = "set_shopping_list_items_skipped", Title = "Mark items skipped", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Marks items as skipped / not bought (e.g. out of stock, or replaced by a substitute) without deleting them: " +
                 "the item is checked off and tagged \"[skipped]\" with an optional reason. Pass skipped=false to undo.")]
    public static Task<string> SetShoppingListItemsSkipped(
        MealieClient mealie,
        [Description("Shopping list ID from list_shopping_lists.")] string listId,
        [Description("Item IDs from get_shopping_list.")] string[] itemIds,
        [Description("Optional reason, e.g. \"out of stock\" or \"subbed with store brand\".")] string? reason = null,
        [Description("true to mark items skipped, false to clear the mark and un-check them.")] bool skipped = true,
        CancellationToken ct = default) => Run(async () =>
    {
        var marker = string.IsNullOrWhiteSpace(reason) ? "[skipped]" : $"[skipped: {reason.Trim().Replace("]", ")")}]";
        var notFound = await mealie.UpdateItemsAsync(listId, itemIds, (_, item) =>
        {
            var note = SkipMarker.Replace(item["note"]?.GetValue<string>() ?? "", "");
            item["note"] = skipped ? (note.Length == 0 ? marker : $"{note} {marker}") : note;
            item["checked"] = skipped;
            item.Remove("display");
        }, ct);

        return ToJson(new { updated = itemIds.Length - notFound.Count, skipped, notFound });
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

    /// <summary>The tag set_shopping_list_items_skipped appends to an item's note.</summary>
    internal static readonly Regex SkipMarker = new(@"\s*\[skipped(?::[^\]]*)?\]\s*$", RegexOptions.IgnoreCase);

    private static Label FindLabel(IReadOnlyList<Label> labels, string name) =>
        labels.FirstOrDefault(l => string.Equals(l.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new MealieApiException(
            $"No store section named '{name.Trim()}'. Sections: {string.Join(", ", labels.Select(l => l.Name))}.");
}

public sealed record ShoppingItemEdit(
    [property: Description("Item ID from get_shopping_list.")] string ItemId,
    [property: Description("New text for the item, e.g. \"Kroger 2% milk, 1 gal (subbed for Horizon)\".")] string? Text = null,
    [property: Description("New quantity.")] double? Quantity = null,
    [property: Description("Store section to move the item to, e.g. \"Dairy\".")] string? Section = null);
