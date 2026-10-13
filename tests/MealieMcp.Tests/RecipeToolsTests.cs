using System.Net;
using System.Text.Json.Nodes;
using MealieMcp.Mealie;
using MealieMcp.Tools;

namespace MealieMcp.Tests;

public class RecipeToolsTests
{
    private static (MealieClient Client, FakeHttpHandler Handler) Create(string recipeJson)
    {
        var handler = new FakeHttpHandler(req => req.Method == HttpMethod.Post
            ? (HttpStatusCode.Created, "\"imported-slug\"")
            : (HttpStatusCode.OK, recipeJson));
        return (new MealieClient(new HttpClient(handler), new MealieOptions(new Uri("http://mealie.test/"), "t")), handler);
    }

    [Fact]
    public async Task Import_DoesNotRequestTags_ByDefault()
    {
        var (client, handler) = Create("""
            {"id":"r","slug":"imported-slug","name":"Chili",
             "recipeIngredient":[{"display":"1 lb beef"}],"recipeInstructions":[{"text":"Brown the beef."}]}
            """);

        var json = JsonNode.Parse(await RecipeTools.ImportRecipeFromUrl(client, "https://example.com/chili"))!;

        var post = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.False(JsonNode.Parse(post.Body!)!["includeTags"]!.GetValue<bool>());
        Assert.True(json["imported"]!.GetValue<bool>());
        Assert.Equal(1, json["ingredientCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task Import_WarnsWhenPageHadNoRecipe()
    {
        var (client, _) = Create("""
            {"id":"r","slug":"imported-slug","name":"Some Store",
             "recipeIngredient":[{"display":"","note":"Could not detect ingredients"}],
             "recipeInstructions":[{"text":"Could not detect instructions"}]}
            """);

        var json = JsonNode.Parse(await RecipeTools.ImportRecipeFromUrl(client, "https://example.com/store"))!;

        Assert.False(json["imported"]!.GetValue<bool>());
        Assert.Contains("no recipe", json["warning"]!.GetValue<string>());
    }
}
