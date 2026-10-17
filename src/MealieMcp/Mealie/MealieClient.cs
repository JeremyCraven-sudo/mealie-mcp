using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MealieMcp.Mealie;

public sealed class MealieApiException(string message) : Exception(message);

/// <summary>Thin typed wrapper over the parts of the Mealie v3 REST API the tools need.</summary>
public sealed class MealieClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly MealieOptions _options;

    public MealieClient(HttpClient http, MealieOptions options)
    {
        _http = http;
        _options = options;
        _http.BaseAddress ??= options.BaseUrl;
        if (options.ApiToken is not null)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiToken);
        }
    }

    public Task<Page<RecipeSummary>> SearchRecipesAsync(string? search, int page, int perPage, CancellationToken ct)
    {
        var query = $"page={page}&perPage={perPage}&orderBy=name&orderDirection=asc";
        if (!string.IsNullOrWhiteSpace(search))
        {
            query += "&search=" + Uri.EscapeDataString(search);
        }

        return GetAsync<Page<RecipeSummary>>("api/recipes?" + query, ct);
    }

    public Task<Recipe> GetRecipeAsync(string slug, CancellationToken ct) =>
        GetAsync<Recipe>("api/recipes/" + Uri.EscapeDataString(slug), ct);

    /// <summary>Scrapes a recipe page into Mealie and returns the new recipe's slug.</summary>
    public async Task<string> ImportRecipeFromUrlAsync(string url, bool includeTags, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, "api/recipes/create/url",
            new { url, includeTags }, ct);
        return (await response.Content.ReadFromJsonAsync<string>(Json, ct))!;
    }

    public Task<JsonObject> GetRecipeJsonAsync(string slug, CancellationToken ct) =>
        GetAsync<JsonObject>("api/recipes/" + Uri.EscapeDataString(slug), ct);

    /// <summary>Runs Mealie's NLP ingredient parser. Each result has "input", "confidence" and "ingredient".</summary>
    public async Task<JsonArray> ParseIngredientsAsync(IReadOnlyList<string> lines, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, "api/parser/ingredients",
            new { parser = "nlp", ingredients = lines }, ct);
        return (await response.Content.ReadFromJsonAsync<JsonArray>(Json, ct))!;
    }

    public async Task<IReadOnlyList<JsonObject>> SearchFoodsAsync(string search, CancellationToken ct)
    {
        var page = await GetAsync<JsonObject>($"api/foods?search={Uri.EscapeDataString(search)}&page=1&perPage=20", ct);
        return page["items"]?.AsArray().OfType<JsonObject>().ToList() ?? [];
    }

    public async Task<IReadOnlyList<Label>> GetLabelsAsync(CancellationToken ct) =>
        (await GetAsync<Page<Label>>("api/groups/labels?page=1&perPage=200&orderBy=name", ct)).Items;

    /// <summary>Sets a food's store-section label, round-tripping the rest of the food unchanged.</summary>
    public async Task SetFoodLabelAsync(string foodId, string labelId, CancellationToken ct)
    {
        var path = "api/foods/" + Uri.EscapeDataString(foodId);
        var food = await GetAsync<JsonObject>(path, ct);
        food["labelId"] = labelId;
        food.Remove("label");
        using var _ = await SendAsync(HttpMethod.Put, path, food, ct);
    }

    public async Task<JsonObject> CreateFoodAsync(string name, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, "api/foods", new { name }, ct);
        return (await response.Content.ReadFromJsonAsync<JsonObject>(Json, ct))!;
    }

    /// <summary>Replaces only the recipe's ingredient list; every other field is left untouched.</summary>
    public async Task UpdateRecipeIngredientsAsync(string slug, JsonArray ingredients, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Patch, "api/recipes/" + Uri.EscapeDataString(slug),
            new JsonObject { ["recipeIngredient"] = ingredients }, ct);
    }

    public async Task<IReadOnlyList<ShoppingListSummary>> GetShoppingListsAsync(CancellationToken ct) =>
        (await GetAsync<Page<ShoppingListSummary>>("api/households/shopping/lists?page=1&perPage=100&orderBy=name", ct)).Items;

    public async Task<ShoppingListSummary> CreateShoppingListAsync(string name, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, "api/households/shopping/lists", new { name }, ct);
        return (await response.Content.ReadFromJsonAsync<ShoppingListSummary>(Json, ct))!;
    }

    public Task<ShoppingList> GetShoppingListAsync(string listId, CancellationToken ct) =>
        GetAsync<ShoppingList>("api/households/shopping/lists/" + Uri.EscapeDataString(listId), ct);

    /// <summary>Renames a list. The update replaces the whole list, so it is round-tripped with only the name changed.</summary>
    public async Task RenameShoppingListAsync(string listId, string name, CancellationToken ct)
    {
        var path = "api/households/shopping/lists/" + Uri.EscapeDataString(listId);
        var list = await GetAsync<JsonObject>(path, ct);
        list["name"] = name;
        using var _ = await SendAsync(HttpMethod.Put, path, list, ct);
    }

    /// <summary>Adds free-text items, each with an optional store-section label.</summary>
    public async Task AddShoppingListItemsAsync(
        string listId, IEnumerable<(string Note, string? LabelId)> items, CancellationToken ct)
    {
        // Free-text items: Mealie shows the note as the item when no food/unit is parsed.
        var body = items.Select(i => new { shoppingListId = listId, note = i.Note, quantity = 0, labelId = i.LabelId }).ToArray();
        using var _ = await SendAsync(HttpMethod.Post, "api/households/shopping/items/create-bulk", body, ct);
    }

    public async Task AddRecipeToShoppingListAsync(string listId, string recipeId, double scale, CancellationToken ct)
    {
        var body = new[] { new { recipeId, recipeIncrementQuantity = scale } };
        using var _ = await SendAsync(HttpMethod.Post,
            $"api/households/shopping/lists/{Uri.EscapeDataString(listId)}/recipe", body, ct);
    }

    /// <summary>Sets the checked state of items, changing nothing else.</summary>
    /// <returns>The IDs that were not found on the list.</returns>
    public Task<IReadOnlyList<string>> SetItemsCheckedAsync(
        string listId, IReadOnlyCollection<string> itemIds, bool isChecked, CancellationToken ct) =>
        UpdateItemsAsync(listId, itemIds, (_, item) => item["checked"] = isChecked, ct);

    /// <summary>
    /// Edits items on a list. Mealie's bulk update replaces whole items, so each item is round-tripped from the
    /// server as raw JSON and passed to <paramref name="edit"/> (with its ID) to change only what it needs to.
    /// </summary>
    /// <returns>The IDs that were not found on the list.</returns>
    public async Task<IReadOnlyList<string>> UpdateItemsAsync(
        string listId, IReadOnlyCollection<string> itemIds, Action<string, JsonObject> edit, CancellationToken ct)
    {
        var list = await GetAsync<JsonObject>("api/households/shopping/lists/" + Uri.EscapeDataString(listId), ct);
        var wanted = itemIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var updates = new JsonArray();

        foreach (var node in list["listItems"]?.AsArray() ?? [])
        {
            if (node is JsonObject item && item["id"]?.GetValue<string>() is { } id && wanted.Remove(id))
            {
                var copy = item.DeepClone().AsObject();
                edit(id, copy);
                updates.Add(copy);
            }
        }

        if (updates.Count > 0)
        {
            using var _ = await SendAsync(HttpMethod.Put, "api/households/shopping/items", updates, ct);
        }

        return wanted.ToList();
    }

    public async Task<IReadOnlyList<MealPlanEntry>> GetMealPlanAsync(DateOnly start, DateOnly end, CancellationToken ct)
    {
        var query = $"start_date={start:yyyy-MM-dd}&end_date={end:yyyy-MM-dd}&page=1&perPage=200&orderBy=date&orderDirection=asc";
        return (await GetAsync<Page<MealPlanEntry>>("api/households/mealplans?" + query, ct)).Items;
    }

    public async Task<MealPlanEntry> CreateMealPlanEntryAsync(
        DateOnly date, string entryType, string? recipeId, string? title, CancellationToken ct)
    {
        var body = new { date = date.ToString("yyyy-MM-dd"), entryType, recipeId, title = title ?? "", text = "" };
        using var response = await SendAsync(HttpMethod.Post, "api/households/mealplans", body, ct);
        return (await response.Content.ReadFromJsonAsync<MealPlanEntry>(Json, ct))!;
    }

    /// <summary>Edits a meal plan entry. The update replaces the whole entry, so it is round-tripped from the server.</summary>
    public async Task<MealPlanEntry> UpdateMealPlanEntryAsync(int entryId, Action<JsonObject> edit, CancellationToken ct)
    {
        var path = $"api/households/mealplans/{entryId}";
        var entry = await GetAsync<JsonObject>(path, ct);
        entry.Remove("recipe");
        edit(entry);
        using var response = await SendAsync(HttpMethod.Put, path, entry, ct);
        return (await response.Content.ReadFromJsonAsync<MealPlanEntry>(Json, ct))!;
    }

    public async Task<MealPlanEntry> DeleteMealPlanEntryAsync(int entryId, CancellationToken ct)
    {
        var path = $"api/households/mealplans/{entryId}";
        var entry = await GetAsync<MealPlanEntry>(path, ct);
        using var _ = await SendAsync(HttpMethod.Delete, path, body: null, ct);
        return entry;
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, path, body: null, ct);
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))
               ?? throw new MealieApiException($"Mealie returned an empty response for {path}.");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        if (_options.ApiToken is null)
        {
            throw new MealieApiException(
                $"No Mealie API token configured. Set the {MealieOptions.ApiTokenVariable} environment variable.");
        }

        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new MealieApiException($"Could not reach Mealie at {_http.BaseAddress}: {ex.Message}");
        }

        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            if (detail.Length > 300)
            {
                detail = detail[..300] + "…";
            }

            throw new MealieApiException($"Mealie {method} {path} failed with {(int)response.StatusCode}: {detail}");
        }

        return response;
    }
}
