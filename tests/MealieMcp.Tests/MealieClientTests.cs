using System.Net;
using System.Text.Json.Nodes;
using MealieMcp.Mealie;

namespace MealieMcp.Tests;

public class MealieClientTests
{
    private static readonly MealieOptions Options = new(new Uri("http://mealie.test/"), "test-token");

    private static (MealieClient Client, FakeHttpHandler Handler) Create(
        Func<HttpRequestMessage, (HttpStatusCode, string)> respond, MealieOptions? options = null)
    {
        var handler = new FakeHttpHandler(respond);
        return (new MealieClient(new HttpClient(handler), options ?? Options), handler);
    }

    [Fact]
    public async Task SearchRecipes_SendsBearerTokenAndQuery_AndReadsSnakeCasePaging()
    {
        var (client, handler) = Create(_ => (HttpStatusCode.OK, """
            {"page":2,"per_page":5,"total":12,"total_pages":3,"items":[
              {"id":"r1","slug":"chili","name":"Chili","tags":[{"id":"t1","name":"Dinner","slug":"dinner"}]}
            ],"next":null,"previous":null}
            """));

        var page = await client.SearchRecipesAsync("chili & beans", 2, 5, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer test-token", request.Auth);
        Assert.StartsWith("/api/recipes?page=2&perPage=5", request.PathAndQuery);
        Assert.Contains("search=chili%20%26%20beans", request.PathAndQuery);
        Assert.Equal(12, page.Total);
        Assert.Equal(3, page.TotalPages);
        Assert.Equal(5, page.PerPage);
        Assert.Equal("Dinner", Assert.Single(page.Items).Tags![0].Name);
    }

    [Fact]
    public async Task SetItemsChecked_RoundTripsWholeItems_ChangingOnlyChecked()
    {
        var list = """
            {"id":"L1","name":"Groceries","listItems":[
              {"id":"a","shoppingListId":"L1","note":"milk","quantity":2,"checked":false,"position":0,"extras":{"k":"v"}},
              {"id":"b","shoppingListId":"L1","note":"eggs","quantity":1,"checked":false,"position":1}
            ]}
            """;
        var (client, handler) = Create(req => req.Method == HttpMethod.Get
            ? (HttpStatusCode.OK, list)
            : (HttpStatusCode.OK, """{"createdItems":[],"updatedItems":[],"deletedItems":[]}"""));

        var notFound = await client.SetItemsCheckedAsync("L1", ["A", "missing"], true, CancellationToken.None);

        Assert.Equal(["missing"], notFound);
        var put = handler.Requests.Single(r => r.Method == HttpMethod.Put);
        Assert.Equal("/api/households/shopping/items", put.PathAndQuery);

        var sent = Assert.Single(JsonNode.Parse(put.Body!)!.AsArray())!.AsObject();
        var expected = JsonNode.Parse(list)!["listItems"]![0]!.DeepClone().AsObject();
        expected["checked"] = true;
        Assert.True(JsonNode.DeepEquals(expected, sent), $"Unexpected payload: {sent.ToJsonString()}");
    }

    [Fact]
    public async Task SetItemsChecked_DoesNotCallPut_WhenNothingMatches()
    {
        var (client, handler) = Create(_ => (HttpStatusCode.OK, """{"id":"L1","listItems":[]}"""));

        var notFound = await client.SetItemsCheckedAsync("L1", ["x"], true, CancellationToken.None);

        Assert.Equal(["x"], notFound);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task AddShoppingListItems_PostsFreeTextNotes()
    {
        var (client, handler) = Create(_ => (HttpStatusCode.OK, "{}"));

        await client.AddShoppingListItemsAsync("L1", [("2 lb chicken thighs", "meat"), ("rice", null)], CancellationToken.None);

        var post = Assert.Single(handler.Requests);
        Assert.Equal("/api/households/shopping/items/create-bulk", post.PathAndQuery);
        var items = JsonNode.Parse(post.Body!)!.AsArray();
        Assert.Equal(["2 lb chicken thighs", "rice"], items.Select(i => i!["note"]!.GetValue<string>()));
        Assert.All(items, i => Assert.Equal("L1", i!["shoppingListId"]!.GetValue<string>()));
        Assert.Equal("meat", items[0]!["labelId"]!.GetValue<string>());
        Assert.Null(items[1]!["labelId"]);
    }

    [Fact]
    public async Task GetMealPlan_SendsDateRange()
    {
        var (client, handler) = Create(_ => (HttpStatusCode.OK, """
            {"page":1,"per_page":200,"total":1,"total_pages":1,"items":[
              {"id":7,"date":"2026-10-14","entryType":"dinner","title":"","text":"","recipeId":"r1",
               "recipe":{"slug":"chili","name":"Chili"}}
            ]}
            """));

        var entries = await client.GetMealPlanAsync(new DateOnly(2026, 10, 14), new DateOnly(2026, 10, 20), CancellationToken.None);

        Assert.Contains("start_date=2026-10-14&end_date=2026-10-20", Assert.Single(handler.Requests).PathAndQuery);
        Assert.Equal("Chili", Assert.Single(entries).Recipe!.Name);
    }

    [Fact]
    public async Task RenameShoppingList_RoundTripsList_ChangingOnlyName()
    {
        var list = """{"id":"L1","name":"Groceries","groupId":"g","userId":"u","listItems":[{"id":"a","note":"milk"}]}""";
        var (client, handler) = Create(req => (HttpStatusCode.OK, req.Method == HttpMethod.Get ? list : "{}"));

        await client.RenameShoppingListAsync("L1", "Food Lion 10/17", CancellationToken.None);

        var put = handler.Requests.Single(r => r.Method == HttpMethod.Put);
        Assert.Equal("/api/households/shopping/lists/L1", put.PathAndQuery);
        var expected = JsonNode.Parse(list)!.AsObject();
        expected["name"] = "Food Lion 10/17";
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(put.Body!)), $"Unexpected payload: {put.Body}");
    }

    [Fact]
    public async Task UpdateMealPlanEntry_RoundTripsEntry_WithoutNestedRecipe()
    {
        var entry = """
            {"id":7,"date":"2026-10-14","entryType":"dinner","title":"","text":"","recipeId":"r1",
             "groupId":"g","userId":"u","recipe":{"slug":"chili","name":"Chili"}}
            """;
        var (client, handler) = Create(_ => (HttpStatusCode.OK, entry));

        await client.UpdateMealPlanEntryAsync(7, e => e["date"] = "2026-10-16", CancellationToken.None);

        var put = handler.Requests.Single(r => r.Method == HttpMethod.Put);
        Assert.Equal("/api/households/mealplans/7", put.PathAndQuery);
        var sent = JsonNode.Parse(put.Body!)!.AsObject();
        Assert.Equal("2026-10-16", sent["date"]!.GetValue<string>());
        Assert.Equal("r1", sent["recipeId"]!.GetValue<string>());
        Assert.False(sent.ContainsKey("recipe"));
    }

    [Fact]
    public async Task ErrorResponses_BecomeMealieApiException_WithStatus()
    {
        var (client, _) = Create(_ => (HttpStatusCode.NotFound, """{"detail":"Recipe not found"}"""));

        var ex = await Assert.ThrowsAsync<MealieApiException>(() => client.GetRecipeAsync("nope", CancellationToken.None));

        Assert.Contains("404", ex.Message);
        Assert.Contains("Recipe not found", ex.Message);
    }

    [Fact]
    public async Task MissingToken_FailsBeforeAnyRequest()
    {
        var (client, handler) = Create(_ => (HttpStatusCode.OK, "{}"), Options with { ApiToken = null });

        var ex = await Assert.ThrowsAsync<MealieApiException>(() => client.GetShoppingListsAsync(CancellationToken.None));

        Assert.Contains(MealieOptions.ApiTokenVariable, ex.Message);
        Assert.Empty(handler.Requests);
    }
}
