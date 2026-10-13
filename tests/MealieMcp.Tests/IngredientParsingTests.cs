using System.Net;
using System.Text.Json.Nodes;
using MealieMcp.Mealie;

namespace MealieMcp.Tests;

public class IngredientParsingTests
{
    private static JsonObject Result(string? foodId, string foodName, double confidence, string? unitId = "u1", string? label = null)
    {
        var food = new JsonObject { ["id"] = foodId, ["name"] = foodName };
        if (label is not null)
        {
            food["label"] = new JsonObject { ["id"] = "l", ["name"] = label };
        }

        return new JsonObject
        {
            ["input"] = "x",
            ["confidence"] = new JsonObject { ["average"] = confidence },
            ["ingredient"] = new JsonObject
            {
                ["quantity"] = 2.0,
                ["note"] = "chopped",
                ["unit"] = new JsonObject { ["id"] = unitId, ["name"] = "cup" },
                ["food"] = food,
            },
        };
    }

    [Theory]
    [InlineData("f1", 0.6, ParseOutcome.Matched)]      // known food, decent confidence
    [InlineData(null, 0.95, ParseOutcome.NewFood)]     // unknown food, very confident
    [InlineData(null, 0.8, ParseOutcome.KeptAsText)]   // unknown food, not confident enough to create
    [InlineData("f1", 0.3, ParseOutcome.KeptAsText)]   // known food but unreliable parse
    public void Classify_AppliesConfidenceRules(string? foodId, double confidence, ParseOutcome expected)
    {
        var line = IngredientParsing.Classify(0, "2 cups onion, chopped", Result(foodId, "onion", confidence));

        Assert.Equal(expected, line.Outcome);
    }

    [Theory]
    [InlineData("Orange", "oranges", true)]
    [InlineData("tomatoes", "tomato", true)]
    [InlineData("ginger root", "ginger", false)]
    [InlineData(null, "ginger", false)]
    public void SameFood_IgnoresCaseAndPlurals_ButNotDifferentFoods(string? a, string b, bool expected)
    {
        Assert.Equal(expected, IngredientParsing.SameFood(a, b));
    }

    [Fact]
    public void Classify_ReportsSectionFromFoodLabel()
    {
        var line = IngredientParsing.Classify(3, "2 oranges", Result("f1", "orange", 0.9, label: "Produce"));

        Assert.Equal("Produce", line.Section);
        Assert.Equal(3, line.Index);
        Assert.Equal(2, line.Quantity);
    }

    [Fact]
    public async Task Apply_CreatesNewFood_MovesUnknownUnitToNote_KeepsOtherLinesAndOriginalText()
    {
        var recipe = JsonNode.Parse("""
            {"slug":"r","recipeIngredient":[
              {"note":"2 cups mystery grain","display":"2 cups mystery grain","referenceId":"ref-1"},
              {"title":"For the sauce","note":"","referenceId":"ref-2"}
            ]}
            """)!.AsObject();
        var plan = new[]
        {
            IngredientParsing.Classify(0, "2 cups mystery grain", Result(null, "mystery grain", 0.95, unitId: null)),
            new ParsedLine(1, "", ParseOutcome.KeptAsText, null, null, null, null, null, 1),
        };

        var handler = new FakeHttpHandler(req => req.RequestUri!.AbsolutePath == "/api/foods"
            ? (HttpStatusCode.Created, """{"id":"new-food","name":"mystery grain"}""")
            : (HttpStatusCode.OK, "{}"));
        var client = new MealieClient(new HttpClient(handler), new MealieOptions(new Uri("http://mealie.test/"), "t"));

        await IngredientParsing.ApplyAsync(client, "r", recipe, plan, CancellationToken.None);

        var patch = handler.Requests.Single(r => r.Method == HttpMethod.Patch);
        var sent = JsonNode.Parse(patch.Body!)!["recipeIngredient"]!.AsArray();
        var first = sent[0]!.AsObject();
        Assert.Equal("new-food", first["food"]!["id"]!.GetValue<string>());
        Assert.Null(first["unit"]);
        Assert.Equal("cup, chopped", first["note"]!.GetValue<string>());
        Assert.Equal("2 cups mystery grain", first["originalText"]!.GetValue<string>());
        Assert.Equal("ref-1", first["referenceId"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(recipe["recipeIngredient"]![1], sent[1]), "untouched line changed");
    }
}
