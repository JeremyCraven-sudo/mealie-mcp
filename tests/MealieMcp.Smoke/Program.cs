// End-to-end smoke test: launches the published server exactly the way Claude Desktop does
// (a child process speaking MCP over stdio), lists its tools, and calls the read-only ones
// against the real Mealie instance. Usage: MealieMcp.Smoke <path-to-MealieMcp.exe> [--write]
// --write also exercises the additive tools on a "Food Lion" list: it adds two test items and
// checks them off (nothing is deleted; clear checked items in Mealie afterwards if you like).
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

// --call <tool> [json-args] calls one tool, prints its full result, and exits.
var write = args.Contains("--write");
string? callTool = null, callArgs = null;
var callAt = Array.IndexOf(args, "--call");
if (callAt >= 0 && callAt + 1 < args.Length)
{
    callTool = args[callAt + 1];
    callArgs = callAt + 2 < args.Length ? args[callAt + 2] : null;
    args = args[..callAt];
}

args = args.Where(a => a != "--write").ToArray();
if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: MealieMcp.Smoke <path-to-MealieMcp.exe> [--write] [--call <tool> [json-args]]");
    return 2;
}

var transport = new StdioClientTransport(new StdioClientTransportOptions
{
    Name = "mealie",
    Command = args[0],
    StandardErrorLines = line => Console.Error.WriteLine($"  [server] {line}"),
});

await using var client = await McpClient.CreateAsync(transport);
Console.WriteLine($"Connected: {client.ServerInfo.Name} {client.ServerInfo.Version}");

if (callTool is not null)
{
    // "@path" reads the JSON from a file, which sidesteps shell quoting of JSON on Windows.
    if (callArgs?.StartsWith('@') == true)
    {
        callArgs = File.ReadAllText(callArgs[1..]);
    }

    var parsedArgs = callArgs is null ? null : JsonSerializer.Deserialize<Dictionary<string, object?>>(callArgs);
    var single = await client.CallToolAsync(callTool, parsedArgs);
    var body = string.Join("", single.Content.OfType<TextContentBlock>().Select(c => c.Text));
    Console.WriteLine(single.IsError == true ? $"ERROR: {body}" : body);
    return single.IsError == true ? 1 : 0;
}

var tools = await client.ListToolsAsync();
foreach (var tool in tools.OrderBy(t => t.Name))
{
    var a = tool.ProtocolTool.Annotations;
    Console.WriteLine($"  tool {tool.Name,-34} readOnly={a?.ReadOnlyHint} destructive={a?.DestructiveHint}");
}

var failures = 0;

async Task<JsonElement?> Call(string name, Dictionary<string, object?>? arguments = null)
{
    var result = await client.CallToolAsync(name, arguments);
    var text = string.Join("", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
    var shown = text.Length > 400 ? text[..400] + "…" : text;
    Console.WriteLine($"\n{name}{(result.IsError == true ? " (ERROR)" : "")}: {shown}");
    if (result.IsError == true)
    {
        failures++;
        return null;
    }

    return JsonDocument.Parse(text).RootElement.Clone();
}

var lists = await Call("list_shopping_lists");
if (lists is { ValueKind: JsonValueKind.Array } arr && arr.GetArrayLength() > 0)
{
    await Call("get_shopping_list", new() { ["listId"] = arr[0].GetProperty("id").GetString() });
}

var recipes = await Call("search_recipes", new() { ["perPage"] = 3 });
if (recipes?.GetProperty("recipes") is { ValueKind: JsonValueKind.Array } r && r.GetArrayLength() > 0)
{
    await Call("get_recipe", new() { ["slug"] = r[0].GetProperty("slug").GetString() });
}

await Call("get_meal_plan");

if (write)
{
    var created = await Call("create_shopping_list", new() { ["name"] = "Food Lion" });
    var listId = created?.GetProperty("id").GetString();
    if (listId is not null)
    {
        string[] testItems = ["MCP smoke test item", "2 lb MCP smoke test chicken"];
        await Call("add_shopping_list_items", new() { ["listId"] = listId, ["items"] = testItems });

        var list = await Call("get_shopping_list", new() { ["listId"] = listId });
        var ids = list?.GetProperty("sections").EnumerateArray()
            .SelectMany(s => s.GetProperty("items").EnumerateArray())
            .Where(i => i.GetProperty("text").GetString() is { } t && t.Contains("MCP smoke test"))
            .Select(i => i.GetProperty("id").GetString())
            .ToArray() ?? [];
        Console.WriteLine($"\nfound {ids.Length} test items");
        if (ids.Length != testItems.Length)
        {
            failures++;
        }

        await Call("set_shopping_list_items_checked", new() { ["listId"] = listId, ["itemIds"] = ids });
        await Call("get_shopping_list", new() { ["listId"] = listId, ["includeChecked"] = true });
    }
}

Console.WriteLine(failures == 0 ? "\nSMOKE OK" : $"\nSMOKE FAILED ({failures} tool errors)");
return failures == 0 ? 0 : 1;
