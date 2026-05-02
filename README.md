# VRChat Agentic Tools

Avatar creation for VRChat in Unity using agentic coding tools.

My blog post about it: https://sentfromspace.xyz/blog/claude-vrchat-avatar/

## Setup

1. Download the `.zip` from this GitHub repository
2. Extract it into your **Unity project root** — the folder containing `Assets/`, `Packages/`, and `Library/` (in Creator Companion: three dots next to the project → Open Project Folder)
3. Files land in the right places automatically — the Unity package goes into `Packages/`, configs go to the project root

## Supported Agents
- [Claude Code](https://code.claude.com/docs/en/overview) (Anthropic)
- [Codex](https://developers.openai.com/codex/cli/) (OpenAI)
- [GitHub Copilot in VS Code](https://code.visualstudio.com/docs/copilot/overview) (Anthropic / OpenAI / Google)

Or any other capable coding agent with MCP support

## What's Included

**Unity package** (`Packages/xyz.sentfromspace.agentic-tools`):
- **UnityMcpBridge** — MCP server that lets AI agents execute C# inside the Unity Editor (read hierarchy, add components, create assets, configure settings)
- **AvatarTypeChecker** — post-build static analysis for avatar validation (broken animation paths, parameter mismatches, material issues)
- **GestureManagerVerifier** — runtime toggle verification using Gesture Manager

**Agent configs**:
- `AGENTS.md` / `CLAUDE.md` — detailed instructions for AI agents covering VRChat SDK, VRCFury, Poiyomi, avatar workflows, and Unity conventions
- `.mcp.json` — MCP server config for Claude Code, VS Code (Copilot), and other agents (auto-generated on first run)
- `.codex/config.toml` — MCP server config for Codex (auto-generated if `.codex/` exists; create the directory first if needed)

## MCP Server

The Unity package includes a built-in MCP server (`UnityMcpBridge`) that starts automatically when the Unity Editor loads. No external process, dependency, or manual configuration needed.

On startup the server:
1. Binds to a dynamic port (tries the previous port, falls back to 14523, then OS-assigned)
2. Compiles a lightweight stdio proxy (`Library/mcp-proxy.exe`) that bridges MCP clients to the HTTP server
3. Auto-generates MCP config files:
   - `.mcp.json` for Claude Code, VS Code (Copilot), and other agents
   - `.codex/config.toml` for Codex (only if `.codex/` already exists)

The proxy reads the port from `Library/MCP_PORT` at runtime, so config files stay the same even if the port changes. It also retries during Unity domain reloads (Play Mode, script recompilation), keeping the connection alive.

Multiple Unity projects can run simultaneously since each gets its own port. The server identifies itself as `unity-<ProjectFolder>` so clients can tell them apart.

Generated `.mcp.json`:
```json
{
  "mcpServers": {
    "unity": {
      "type": "stdio",
      "command": "Library/mcp-proxy.exe"
    }
  }
}
```

The server exposes a single tool, `execute_csharp`, which compiles and runs C# snippets on the Unity main thread with access to all loaded assemblies (Unity, VRC SDK, VRCFury, project scripts).

## Third-Party Package Support

The agent instructions cover deep integration with:
- **VRCFury** — automated toggle creation, component scanning, build verification
- **Poiyomi Toon Shader** — material property discovery, module configuration, animated properties
