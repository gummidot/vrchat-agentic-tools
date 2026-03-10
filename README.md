# VRChat Agentic Tools

Avatar creation for VRChat in Unity using agentic coding tools.

## Setup

1. Download the `.zip` from this GitHub repository
2. Extract it into your **Unity project root** — the folder containing `Assets/`, `Packages/`, and `Library/` (in Creator Companion: three dots next to the project → Open Project Folder)
3. Files land in the right places automatically — the Unity package goes into `Packages/`, configs go to the project root

## Supported Agents
- [Claude Code](https://code.claude.com/docs/en/overview) (Anthropic)
- [Codex](https://developers.openai.com/codex/cli/) (OpenAI)

Or any other capable coding agent with MCP support

## What's Included

**Unity package** (`Packages/xyz.sentfromspace.agentic-tools`):
- **UnityMcpBridge** — MCP server that lets AI agents execute C# inside the Unity Editor (read hierarchy, add components, create assets, configure settings)
- **AvatarTypeChecker** — post-build static analysis for avatar validation (broken animation paths, parameter mismatches, material issues)
- **GestureManagerVerifier** — runtime toggle verification using Gesture Manager

**Agent configs**:
- `AGENTS.md` / `CLAUDE.md` — detailed instructions for AI agents covering VRChat SDK, VRCFury, Poiyomi, avatar workflows, and Unity conventions
- `.mcp.json` — MCP server config for Claude Code
- `.codex/` — MCP server config for Codex

## MCP Server

The Unity package includes a built-in MCP server (`UnityMcpBridge`) that starts automatically when the Unity Editor loads — no external process or dependency needed. It listens on `http://127.0.0.1:14523/mcp/` using Streamable HTTP transport and exposes a single tool: `execute_csharp`, which compiles and runs C# snippets on the Unity main thread with access to all loaded assemblies (Unity, VRC SDK, VRCFury, project scripts). The `.mcp.json` and `.codex/config.toml` files point Claude Code and Codex to this local server.

## Third-Party Package Support

The agent instructions cover deep integration with:
- **VRCFury** — automated toggle creation, component scanning, build verification
- **Poiyomi Toon Shader** — material property discovery, module configuration, animated properties
