# VRChat Agentic Tools

Avatar creation for VRChat in Unity using agentic coding tools.

My blog post about it: https://sentfromspace.xyz/blog/claude-vrchat-avatar/

## Setup

1. Download the `.zip` from this GitHub repository
2. Extract it into your **Unity project root** - the folder containing `Assets/`, `Packages/`, and `Library/` (in Creator Companion: three dots next to the project > Open Project Folder)
3. Files land in the right places automatically - the Unity package goes into `Packages/`, MCP configs are generated on first run
4. In Unity: **Tools > VRChat Agentic Tools > Generate Skill (Both)** to activate agent instructions

## Supported Agents
- [Claude Code](https://code.claude.com/docs/en/overview) (Anthropic)
- [Codex](https://developers.openai.com/codex/cli/) (OpenAI)
- [GitHub Copilot in VS Code](https://code.visualstudio.com/docs/copilot/overview) (Anthropic / OpenAI / Google)

Or any other capable coding agent with MCP support

## What's Included

**Unity package** (`Packages/xyz.sentfromspace.agentic-tools`):
- **UnityMcpBridge** - MCP server that lets AI agents execute C# inside the Unity Editor (read hierarchy, add components, create assets, configure settings)
- **SkillGenerator** - menu item to generate agent skills for your project (see [Agent Instructions Setup](#agent-instructions-setup))
- **AvatarTypeChecker** - post-build static analysis for avatar validation (broken animation paths, parameter mismatches, material issues)
- **GestureManagerVerifier** - runtime toggle verification using Gesture Manager

**Agent instructions** (`Packages/xyz.sentfromspace.agentic-tools/Docs/`):
- `INDEX.md` - core rules (MCP bridge, scene exploration, Unity conventions) + reference table pointing to topic docs
- `vrc-avatars.md` - playable layers, expressions, toggles, audit workflows, post-build verification
- `vrcfury.md` - VRCFury component scanning, build verification, public API
- `poiyomi.md` - shader property discovery, module configuration, locking lifecycle
- `physbones.md` - PhysBone setup, colliders, SDK samples
- `vrc-worlds.md` - world-specific guidance (stub, planned)

**Agent skill** (`Packages/xyz.sentfromspace.agentic-tools/Skills~/`):
- `unity-workflow/SKILL.md` - generated into your project root to teach agents how to use the docs above (see [Agent Instructions Setup](#agent-instructions-setup))

**MCP configs** (auto-generated on first run):
- `.mcp.json` - MCP server config for Claude Code, VS Code (Copilot), and other agents
- `.codex/config.toml` - MCP server config for Codex (only if `.codex/` exists; create the directory first if needed)

## Agent Instructions Setup

The generated [agent skill](https://agentskills.io/) is a small file that agents automatically detect and load when relevant - once generated, it kicks in whenever the agent works on Unity tasks without any manual invocation.

If you only use one agent, you can generate just the directory it reads from:

| Menu option | Creates | Works with |
|---|---|---|
| Generate Skill (.claude) | `.claude/skills/unity-workflow/SKILL.md` | Claude Code, VS Code (Copilot) |
| Generate Skill (.agents) | `.agents/skills/unity-workflow/SKILL.md` | Codex, VS Code (Copilot) |
| Generate Skill (Both) | Both of the above | All agents |

The skill points to `INDEX.md`, which routes to topic-specific docs based on what packages are installed. You can add project-specific instructions by creating additional skills in the same directory, or by editing `CLAUDE.md`/`AGENTS.md` alongside the skill (they coexist fine).

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

## Local Development

To test package changes against a Unity project without copying files, create a directory junction:

```powershell
# From the repo root:
$RepoPkg = "$(Get-Location)\Packages\xyz.sentfromspace.agentic-tools"

# Target project (adjust path):
$Project = "C:\path\to\my-project"

# Create junction:
cmd /c mklink /J "$Project\Packages\xyz.sentfromspace.agentic-tools" "$RepoPkg"
```

The Unity project will read the package directly from this repo. Changes here are reflected immediately on the next domain reload.
