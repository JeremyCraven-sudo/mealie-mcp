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
    public async Task<string> ImportRecipeFromUrlAsync(string url, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, "api/recipes/create/url",
            new { url, includeTags = true }, ct);
        return (await response.Content.ReadFromJsonAsync<string>(Json, ct))!;
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

    public async Task AddShoppingListItemsAsync(string listId, IEnumerable<string> items, CancellationToken ct)
    {
        // Free-text items: Mealie shows the note as the item when no food/unit is parsed.
        var body = items.Select(note => new { shoppingListId = listId, note, quantity = 0 }).ToArray();
        using var _ = await SendAsync(HttpMethod.Post, "api/households/shopping/items/create-bulk", body, ct);
    }

    public async Task AddRecipeToShoppingListAsync(string listId, string recipeId, double scale, CancellationToken ct)
    {
        var body = new[] { new { recipeId, recipeIncrementQuantity = scale } };
        using var _ = await SendAsync(HttpMethod.Post,
            $"api/households/shopping/lists/{Uri.EscapeDataString(listId)}/recipe", body, ct);
    }

    /// <summary>
    /// Sets the checked state of items. Mealie's bulk update replaces whole items, so each item is
    /// round-tripped from the server as raw JSON and only the "checked" field is changed.
    /// </summary>
    /// <returns>The IDs that were not found on the list.</returns>
    public async Task<IReadOnlyList<string>> SetItemsCheckedAsync(
        string listId, IReadOnlyCollection<string> itemIds, bool isChecked, CancellationToken ct)
    {
        var list = await GetAsync<JsonObject>("api/households/shopping/lists/" + Uri.EscapeDataString(listId), ct);
        var wanted = itemIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var updates = new JsonArray();

        foreach (var node in list["listItems"]?.AsArray() ?? [])
        {
            if (node is JsonObject item && item["id"]?.GetValue<string>() is { } id && wanted.Remove(id))
            {
                var copy = item.DeepClone().AsObject();
                copy["checked"] = isChecked;
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
