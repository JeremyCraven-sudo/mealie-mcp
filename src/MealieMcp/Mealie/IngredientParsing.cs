using System.Text.Json.Nodes;

namespace MealieMcp.Mealie;

public enum ParseOutcome
{
    /// <summary>Food matched one already in Mealie (and so usually has a store-section label).</summary>
    Matched,
    /// <summary>Parser is confident but the food is new; it will be created (without a label).</summary>
    NewFood,
    /// <summary>Line is left as plain text: already structured, a section title, or the parse looked unreliable.</summary>
    KeptAsText,
}

public sealed record ParsedLine(
    int Index,
    string Original,
    ParseOutcome Outcome,
    double? Quantity,
    string? Unit,
    string? Food,
    string? Section,
    string? Note,
    double Confidence)
{
    /// <summary>The parser's raw ingredient object, used when applying the plan.</summary>
    internal JsonObject? Parsed { get; init; }
}

/// <summary>
/// Turns a recipe's free-text ingredient lines into structured Mealie ingredients (quantity, unit, food).
/// Structured ingredients are what let shopping lists group items by store section and merge duplicates.
/// </summary>
public static class IngredientParsing
{
    /// <summary>Minimum parser confidence before a brand-new food is created.</summary>
    public const double NewFoodConfidence = 0.9;

    /// <summary>Minimum parser confidence for using a parse at all.</summary>
    public const double MinimumConfidence = 0.5;

    public static async Task<IReadOnlyList<ParsedLine>> PlanAsync(MealieClient mealie, JsonObject recipe, CancellationToken ct)
    {
        var ingredients = recipe["recipeIngredient"]?.AsArray() ?? [];
        var toParse = new List<(int Index, string Text)>();
        var plan = new ParsedLine?[ingredients.Count];

        for (var i = 0; i < ingredients.Count; i++)
        {
            var ing = ingredients[i]!.AsObject();
            var text = OriginalText(ing);
            if (ing["food"] is JsonObject || !string.IsNullOrWhiteSpace(Str(ing, "title")) || string.IsNullOrWhiteSpace(text))
            {
                // Already structured, a section heading, or empty: leave alone.
                plan[i] = new ParsedLine(i, text, ParseOutcome.KeptAsText, null, null, null, null, null, 1);
            }
            else
            {
                toParse.Add((i, text));
            }
        }

        if (toParse.Count > 0)
        {
            var results = await mealie.ParseIngredientsAsync(toParse.Select(p => p.Text).ToList(), ct);
            for (var k = 0; k < toParse.Count; k++)
            {
                plan[toParse[k].Index] = Classify(toParse[k].Index, toParse[k].Text, results[k]!.AsObject());
            }
        }

        return plan.Select(p => p!).ToList();
    }

    internal static ParsedLine Classify(int index, string original, JsonObject result)
    {
        var ing = result["ingredient"]?.AsObject();
        var confidence = result["confidence"]?["average"]?.GetValue<double>() ?? 0;
        var food = ing?["food"] as JsonObject;
        var foodName = food is null ? null : Str(food, "name");
        var foodId = food is null ? null : Str(food, "id");
        var unit = ing?["unit"] as JsonObject;

        var outcome = foodName is null || confidence < MinimumConfidence ? ParseOutcome.KeptAsText
            : foodId is not null ? ParseOutcome.Matched
            : confidence >= NewFoodConfidence ? ParseOutcome.NewFood
            : ParseOutcome.KeptAsText;

        return new ParsedLine(
            index,
            original,
            outcome,
            ing?["quantity"]?.GetValue<double>(),
            unit is null ? null : Str(unit, "name"),
            foodName,
            food?["label"] is JsonObject label ? Str(label, "name") : null,
            ing is null ? null : Str(ing, "note"),
            Math.Round(confidence, 2))
        {
            Parsed = ing,
        };
    }

    /// <summary>
    /// Writes the plan back to the recipe. Matched and new foods become structured ingredients; everything else is
    /// kept exactly as it was. The original line is always preserved in originalText.
    /// </summary>
    public static async Task<JsonArray> ApplyAsync(
        MealieClient mealie, string slug, JsonObject recipe, IReadOnlyList<ParsedLine> plan, CancellationToken ct)
    {
        var current = recipe["recipeIngredient"]?.AsArray() ?? [];
        var createdFoods = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        var updated = new JsonArray();

        foreach (var line in plan)
        {
            var existing = current[line.Index]!.DeepClone().AsObject();
            if (line.Outcome == ParseOutcome.KeptAsText)
            {
                updated.Add(existing);
                continue;
            }

            var ing = line.Parsed!;
            var food = ing["food"]!.AsObject();
            if (Str(food, "id") is null)
            {
                var name = Str(food, "name")!;
                if (!createdFoods.TryGetValue(name, out var created))
                {
                    // The parser can miss an existing food over casing or plurals; reuse an exact match if there is one.
                    created = (await mealie.SearchFoodsAsync(name, ct)).FirstOrDefault(f => SameFood(Str(f, "name"), name))
                              ?? await mealie.CreateFoodAsync(name, ct);
                    createdFoods[name] = created;
                }

                food = created;
            }

            // Units the parser couldn't match to an existing unit go into the note rather than being created.
            var unit = ing["unit"] as JsonObject;
            var note = Str(ing, "note");
            if (unit is not null && Str(unit, "id") is null)
            {
                note = string.Join(", ", new[] { Str(unit, "name"), note }.Where(s => !string.IsNullOrWhiteSpace(s)));
                unit = null;
            }

            existing["quantity"] = ing["quantity"]?.GetValue<double>() ?? 0;
            existing["unit"] = unit is null ? null : new JsonObject { ["id"] = Str(unit, "id"), ["name"] = Str(unit, "name") };
            existing["food"] = new JsonObject { ["id"] = Str(food, "id"), ["name"] = Str(food, "name") };
            existing["note"] = note ?? "";
            existing["originalText"] = line.Original;
            existing.Remove("display");
            updated.Add(existing);
        }

        await mealie.UpdateRecipeIngredientsAsync(slug, updated, ct);
        return updated;
    }

    internal static bool SameFood(string? a, string b)
    {
        // Possible singular forms: "oranges" -> orange/orang, "tomatoes" -> tomatoe/tomato.
        static HashSet<string> Forms(string s)
        {
            s = s.Trim().ToLowerInvariant();
            var forms = new HashSet<string> { s };
            if (s.EndsWith('s') && s.Length > 2) forms.Add(s[..^1]);
            if (s.EndsWith("es") && s.Length > 3) forms.Add(s[..^2]);
            return forms;
        }

        return a is not null && Forms(a).Overlaps(Forms(b));
    }

    private static string OriginalText(JsonObject ing) =>
        Str(ing, "originalText") ?? Str(ing, "note") ?? Str(ing, "display") ?? "";

    private static string? Str(JsonObject obj, string name) =>
        obj[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
}
