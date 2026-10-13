# mealie-mcp

An [MCP](https://modelcontextprotocol.io) server, written in C# on .NET 10, that lets Claude work with a self-hosted
[Mealie](https://mealie.io) instance: search recipes, plan meals, and build shopping lists.

It runs locally next to Claude Desktop and talks to Mealie over the LAN or Tailscale, so nothing is exposed to the internet.

## Tools

| Tool | Kind | What it does |
|---|---|---|
| `search_recipes` | read | Search recipes by text; returns slugs |
| `get_recipe` | read | Ingredients and instructions for a recipe |
| `import_recipe_from_url` | write | Scrape a recipe page into Mealie |
| `list_shopping_lists` | read | Shopping lists and their IDs |
| `create_shopping_list` | write | Create a list (no-op if the name exists) |
| `get_shopping_list` | read | Items grouped by store section; unchecked only by default |
| `add_shopping_list_items` | write | Add free-text items |
| `add_recipe_to_shopping_list` | write | Add a recipe's ingredients, with optional scaling |
| `set_shopping_list_items_checked` | write | Check off / un-check items |
| `get_meal_plan` | read | Meal plan for a date range (default: next 7 days) |
| `add_meal_plan_entry` | write | Put a recipe or free-text meal on a day |

There are deliberately no delete tools. Write tools are marked non-destructive and read tools read-only, using MCP tool annotations.

## Configuration

| Variable | Default | Notes |
|---|---|---|
| `MEALIE_BASE_URL` | `http://pi4:9925` | Mealie's address |
| `MEALIE_API_TOKEN` | *(required)* | Create one in Mealie → profile → API Tokens |

Keep the token out of config files. Store it as a user environment variable instead; the server checks the process
environment first, then the user-level variable:

```powershell
[Environment]::SetEnvironmentVariable('MEALIE_API_TOKEN', '<token>', 'User')
```

## Build and run

```powershell
dotnet test
dotnet publish src/MealieMcp -c Release -o publish
```

Claude Desktop (`%APPDATA%\Claude\claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "mealie": {
      "command": "C:\\path\\to\\mealie-mcp\\publish\\MealieMcp.exe",
      "env": { "MEALIE_BASE_URL": "http://pi4:9925" }
    }
  }
}
```

Point Desktop at the published `.exe`, not `dotnet run`, because build output written to stdout would corrupt the MCP stream. Quit
Claude Desktop before republishing; it holds a lock on the exe while it runs.

## Testing

- `tests/MealieMcp.Tests`: unit tests that run against a fake HTTP handler (no Mealie needed).
- `tests/MealieMcp.Smoke`: end-to-end check. It launches the published exe over stdio the way Claude Desktop does, lists the
  tools, and calls the read-only ones against a real Mealie. Add `--write` to also exercise the write tools on a "Food Lion"
  list. That mode adds two test items and checks them off.

```powershell
dotnet run --project tests/MealieMcp.Smoke -- "$PWD\publish\MealieMcp.exe" [--write]
```

## Repo layout

- `src/MealieMcp`: the server (`Mealie/` is the API client, `Tools/` holds the MCP tools)
- `docs/mealie-openapi-v3.28.0.json`: the Mealie API spec the client was written against
- The Pi that hosts Mealie is set up from a separate homelab repo.
