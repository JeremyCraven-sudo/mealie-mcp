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

    [Fact]
    public async Task AddShoppingListItems_RejectsEmptyInput_AsMcpError()
    {
        await Assert.ThrowsAsync<McpException>(() => ShoppingListTools.AddShoppingListItems(Client("{}"), "L1", ["  ", ""]));
    }
}
