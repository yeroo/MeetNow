# MeetNow Development Guidelines

## MCP Server Rule

When adding new features that expose data or actions, also add corresponding MCP tools in `MeetNow/McpServer.cs`:
1. Add a `ToolXxx` method implementing the tool logic
2. Add a case to `DispatchTool()` switch
3. Add a tool definition to `GetToolDefinitions()`
4. Action tools must go through `TeamsOperationQueue.Enqueue()`, read-only tools call APIs directly

## Build

```
dotnet build MeetNow/MeetNow.csproj
```

## Constraints

- No Microsoft Graph API (requires IT approval)
- No registry changes (IT monitors registry)
- No external API dependencies (everything must work locally/offline)
- No certificates/code signing (IT noise)
- No speculative API calls that return 401/403 (IT monitors auth failures)
- User-level privileges only (no admin)
