using System.Text.Json.Serialization;

namespace MealieMcp.Mealie;

// Minimal views of Mealie v3 API payloads (see docs/mealie-openapi-v3.28.0.json).
// Only the fields the tools actually use are modeled; everything else is ignored.

public sealed record Page<T>(
    [property: JsonPropertyName("page")] int PageNumber,
    [property: JsonPropertyName("per_page")] int PerPage,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("total_pages")] int TotalPages,
    [property: JsonPropertyName("items")] IReadOnlyList<T> Items);

public sealed record NamedRef(string? Id, string? Name, string? Slug);

public sealed record RecipeSummary(
    string? Id,
    string Slug,
    string? Name,
    string? Description,
    string? TotalTime,
    string? RecipeYield,
    double? Rating,
    IReadOnlyList<NamedRef>? Tags,
    IReadOnlyList<NamedRef>? RecipeCategory);

public sealed record RecipeIngredient(string? Display, string? Note, string? Title, string? OriginalText);

public sealed record RecipeStep(string? Title, string Text);

public sealed record Recipe(
    string? Id,
    string Slug,
    string? Name,
    string? Description,
    double? RecipeServings,
    string? RecipeYield,
    string? TotalTime,
    string? PrepTime,
    string? CookTime,
    [property: JsonPropertyName("orgURL")] string? OrgUrl,
    IReadOnlyList<NamedRef>? Tags,
    IReadOnlyList<RecipeIngredient>? RecipeIngredient,
    IReadOnlyList<RecipeStep>? RecipeInstructions);

public sealed record ShoppingListSummary(string Id, string? Name);

public sealed record Label(string Id, string Name);

public sealed record ShoppingListItem(
    string Id,
    string ShoppingListId,
    string? Display,
    string? Note,
    double Quantity,
    bool Checked,
    int Position,
    Label? Label);

public sealed record ShoppingList(string Id, string? Name, IReadOnlyList<ShoppingListItem>? ListItems);

public sealed record MealPlanEntry(
    int Id,
    DateOnly Date,
    string? EntryType,
    string? Title,
    string? Text,
    string? RecipeId,
    RecipeSummary? Recipe);
