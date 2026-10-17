using System.Net;
using System.Text.Json.Nodes;
using MealieMcp.Mealie;
using MealieMcp.Tools;
using ModelContextProtocol;

namespace MealieMcp.Tests;

public class ShoppingListToolsTests
{
    private static MealieClient Client(string listJson) =>
        new(new HttpClient(new FakeHttpHandler(_ => (HttpStatusCode.OK, listJson))),
            new MealieOptions(new Uri("http://mealie.test/"), "t"));

    private const string List = """
        {"id":"L1","name":"Groceries","listItems":[
          {"id":"1","shoppingListId":"L1","display":"Milk","quantity":1,"checked":false,"position":2,
           "label":{"id":"d","name":"Dairy"}},
          {"id":"2","shoppingListId":"L1","note":"Batteries","display":"","quantity":0,"checked":false,"position":0},
          {"id":"3","shoppingListId":"L1","display":"Eggs","quantity":1,"checked":true,"position":1,
           "label":{"id":"d","name":"Dairy"}}
        ]}
        """;

    [Fact]
    public async Task GetShoppingList_GroupsBySection_HidesCheckedByDefault_PutsOtherLast()
    {
        var json = JsonNode.Parse(await ShoppingListTools.GetShoppingList(Client(List), "L1"))!;

        var sections = json["sections"]!.AsArray();
        Assert.Equal(["Dairy", "Other"], sections.Select(s => s!["section"]!.GetValue<string>()));
        Assert.Equal("Milk", Assert.Single(sections[0]!["items"]!.AsArray())!["text"]!.GetValue<string>());
        Assert.Equal("Batteries", sections[1]!["items"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetShoppingList_IncludeChecked_ReturnsCheckedFlag()
    {
        var json = JsonNode.Parse(await ShoppingListTools.GetShoppingList(Client(List), "L1", includeChecked: true))!;

        var dairy = json["sections"]![0]!["items"]!.AsArray();
        Assert.Equal(2, dairy.Count);
        Assert.Contains(dairy, i => i!["checked"]!.GetValue<bool>());
    }

    private const string Labels = """
        {"page":1,"per_page":200,"total":2,"total_pages":1,"items":[{"id":"d","name":"Dairy"},{"id":"p","name":"Produce"}]}
        """;

    private static (MealieClient Client, FakeHttpHandler Handler) Routed(string parseResult = "[]")
    {
        var handler = new FakeHttpHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/api/groups/labels" => (HttpStatusCode.OK, Labels),
            "/api/parser/ingredients" => (HttpStatusCode.OK, parseResult),
            "/api/households/shopping/lists/L1" => (HttpStatusCode.OK, List),
            _ => (HttpStatusCode.OK, "{}"),
        });
        return (new MealieClient(new HttpClient(handler), new MealieOptions(new Uri("http://mealie.test/"), "t")), handler);
    }

    private static JsonArray SentItems(FakeHttpHandler handler, HttpMethod method) =>
        JsonNode.Parse(handler.Requests.Single(r => r.Method == method && r.PathAndQuery.Contains("/shopping/items")).Body!)!.AsArray();

    [Fact]
    public async Task AddShoppingListItems_WithSection_LabelsEveryItem_CaseInsensitively()
    {
        var (client, handler) = Routed();

        await ShoppingListTools.AddShoppingListItems(client, "L1", ["bananas", "limes"], section: "produce");

        Assert.All(SentItems(handler, HttpMethod.Post), i => Assert.Equal("p", i!["labelId"]!.GetValue<string>()));
        Assert.DoesNotContain(handler.Requests, r => r.PathAndQuery.Contains("parser"));
    }

    [Fact]
    public async Task AddShoppingListItems_UnknownSection_ListsTheValidOnes()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() =>
            ShoppingListTools.AddShoppingListItems(Routed().Client, "L1", ["x"], section: "Bakery"));

        Assert.Contains("Dairy, Produce", ex.Message);
    }

    [Fact]
    public async Task AddShoppingListItems_GuessesSectionFromMatchedFood_KeepingOriginalText()
    {
        var parse = """
            [{"input":"Horizon whole milk","confidence":{"average":0.95},
              "ingredient":{"quantity":0,"note":"Horizon","food":{"id":"f1","name":"milk","label":{"id":"d","name":"Dairy"}}}},
             {"input":"batteries","confidence":{"average":0.2},"ingredient":{"note":"batteries"}}]
            """;
        var (client, handler) = Routed(parse);

        var json = JsonNode.Parse(await ShoppingListTools.AddShoppingListItems(client, "L1", ["Horizon whole milk", "batteries"]))!;

        var sent = SentItems(handler, HttpMethod.Post);
        Assert.Equal("Horizon whole milk", sent[0]!["note"]!.GetValue<string>());
        Assert.Equal("d", sent[0]!["labelId"]!.GetValue<string>());
        Assert.Null(sent[1]!["labelId"]);
        Assert.Equal(["Dairy", "Other"], json["items"]!.AsArray().Select(i => i!["section"]!.GetValue<string>()));
    }

    [Fact]
    public async Task UpdateShoppingListItems_NewText_MakesPlainTextItem_AndMovesSection()
    {
        var (client, handler) = Routed();

        await ShoppingListTools.UpdateShoppingListItems(client, "L1",
            [new ShoppingItemEdit("1", Text: "Kroger 2% milk", Quantity: 2, Section: "Produce")]);

        var item = Assert.Single(SentItems(handler, HttpMethod.Put))!.AsObject();
        Assert.Equal("Kroger 2% milk", item["note"]!.GetValue<string>());
        Assert.Equal(2, item["quantity"]!.GetValue<double>());
        Assert.Equal("p", item["labelId"]!.GetValue<string>());
        Assert.Null(item["foodId"]);
        Assert.False(item.ContainsKey("label"));
        Assert.False(item.ContainsKey("display"));
    }

    [Fact]
    public async Task SetSkipped_ChecksOffAndTags_ThenUndoRestoresText()
    {
        var (client, handler) = Routed();

        await ShoppingListTools.SetShoppingListItemsSkipped(client, "L1", ["2"], reason: "out of stock");

        var item = Assert.Single(SentItems(handler, HttpMethod.Put))!.AsObject();
        Assert.Equal("Batteries [skipped: out of stock]", item["note"]!.GetValue<string>());
        Assert.True(item["checked"]!.GetValue<bool>());

        Assert.Equal("Batteries", ShoppingListTools.SkipMarker.Replace(item["note"]!.GetValue<string>(), ""));
    }

    [Fact]
    public async Task GetShoppingList_FlagsSkippedItems()
    {
        var list = List.Replace("\"note\":\"Batteries\"", "\"note\":\"Batteries [skipped]\"");
        var json = JsonNode.Parse(await ShoppingListTools.GetShoppingList(Client(list), "L1"))!;

        Assert.True(json["sections"]![1]!["items"]![0]!["skipped"]!.GetValue<bool>());
        Assert.Null(json["sections"]![0]!["items"]![0]!["skipped"]);
    }

    [Fact]
    public async Task AddShoppingListItems_RejectsEmptyInput_AsMcpError()
    {
        await Assert.ThrowsAsync<McpException>(() => ShoppingListTools.AddShoppingListItems(Client("{}"), "L1", ["  ", ""]));
    }
}
