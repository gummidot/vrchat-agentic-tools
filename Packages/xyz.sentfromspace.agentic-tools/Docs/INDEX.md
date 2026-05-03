When adding guidance to these docs, prefer discovery-first approaches (runtime queries, dynamic lookups, search patterns) over hardcoded values, paths, or version-specific details that can go stale.

This is a Unity Project. We work inside the Assets folder (working directory).

**Primary approach**: Use the MCP bridge (`execute_csharp`) for all scene interaction — reading hierarchy, adding components, configuring settings, creating assets.
**File reads**: Freely read any file to explore assets, code, configs, YAML structure.
**File writes**: Fallback for creating/editing `.anim`, `.controller`, `.asset`, `.mat` files when the C# API is awkward or for bulk operations. When editing YAML directly, read an existing file of the same type first to understand the structure.
Do not create `.meta` files — Unity generates these automatically.
**Editor scripts** must be wrapped in `#if UNITY_EDITOR` / `#endif`. Even scripts inside an `Editor/` folder with an asmdef need the guard -- the preprocessor directive is the standard VRChat SDK pattern and is more reliable than `includePlatforms` in the asmdef.

**Subagent limitation**: Subagents (Explore, Plan, etc.) do NOT have access to MCP tools like `execute_csharp`. Never delegate scene exploration or Unity Editor interaction to subagents — they will try to parse `.unity` files directly, which is unreliable. Always run MCP calls in the main conversation.

**Plan mode**: MCP tools (`execute_csharp`) are available during plan mode since the main agent retains all tools. Use MCP for scene exploration during planning — don't rely on parsing `.unity` YAML files.

## MCP Bridge (`execute_csharp`)

C# snippets run inside the Unity Editor via the MCP `execute_csharp` tool. Key details:

- Runs on Unity main thread with a **30-second timeout**
- Must end with `return "some string";`
- Available usings: `System`, `System.Collections.Generic`, `System.IO`, `System.Linq`, `System.Text`, `UnityEditor`, `UnityEngine`, `UnityEngine.SceneManagement`
- All loaded assemblies are referenced (Unity, VRC SDK, VRCFury, project scripts)
- Each snippet is independent — chain multiple MCP calls for multi-step operations
- Always call `EditorUtility.SetDirty(obj)` on modified objects
- Save with `AssetDatabase.SaveAssets()` or `EditorSceneManager.SaveOpenScenes()`
- **Never call methods that use `EditorUtility.DisplayDialog()` from MCP.** The modal dialog blocks Unity's main thread waiting for a click, but MCP can't interact with the GUI -- it hangs until the client times out and cancels. Instead, replicate the logic inline without dialogs.
- **Don't edit loaded asset files on disk** -- Unity keeps assets in memory and overwrites disk changes on save. Use the C# API (`AssetDatabase.LoadAssetAtPath` + modify + `SetDirty` + `SaveAssets`) instead of editing `.asset` YAML directly for assets Unity has loaded.

## Workflow — Scene Exploration First

**Default**: work on the currently open scene unless the user specifies otherwise.

Start by exploring the scene hierarchy — find root GameObjects, understand the structure before making changes.

- **Walk hierarchy** recursively from root transforms
- **List components, blendshapes, materials** via standard Unity APIs (`GetComponents`, `sharedMesh.GetBlendShapeName`, `sharedMaterials`)
- **Navigate bones** via `transform.Find("Armature/Hips/Spine/Chest/...")`

## Animation Clip Binding Reference

- **Blendshapes:** `EditorCurveBinding.FloatCurve("MeshPath", typeof(SkinnedMeshRenderer), "blendShape.ShapeName")`
- **GameObject active:** `EditorCurveBinding.FloatCurve("ObjectPath", typeof(GameObject), "m_IsActive")`
- Always `EditorUtility.SetDirty()` on modified objects; `Undo.RecordObject()` before changes

### SetCurve vs EditorCurveBinding Gotcha

- `AnimationClip.SetCurve(path, typeof(Transform), "m_LocalPosition.x", curve)` **AUTO-FILLS** the other 2 axes (y, z) at value 0. Creates 3 bindings instead of 1.
- `AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(...), curve)` creates ONLY the specified binding. Use this for per-property isolation in blend trees.

### Direct Blend Tree Normalization

- `m_NormalizedBlendValues` defaults to FALSE on new BlendTree objects.
- When false: result = sum(child_output x directBlendParameter_value). Pure weighted sum, no normalization.
- When true: weights are normalized to sum to 1. Per-property (only children with curves for that property contribute).
- VRChat standard pattern: all children use `directBlendParameter = "Always 1"` (Float, default=1.0). With normalization OFF, each child contributes its full output.

## FBX Metadata Cache (`userData`)

`Assets/Claude/Editor/ModelMetadataCache.cs` caches FBX hierarchy, fileIDs, materials, and blendshapes into each model's `.meta` file under `userData`. **Check `userData` first** when you need bone hierarchy, material slots, or blendshape names — but it may not be present. Always fall back to C# API queries if absent.

Prefer querying live data via `SkinnedMeshRenderer.sharedMesh` and `transform.Find()` rather than parsing the cache format.

## Project Type & Reference Docs

Detect the project type by checking installed VRC SDKs:
- **Avatar project**: `Packages/com.vrchat.avatars` present
- **World project**: `Packages/com.vrchat.worlds` present
- Both may be present (hybrid project)

Read these reference docs as needed based on project type and current task:

| Doc | Covers | Read when |
|---|---|---|
| `./vrc-avatars.md` | Playable layers, expressions, toggles, audit, upload checklist, post-build verification, toggle verification | Avatar project (`com.vrchat.avatars` present) |
| `./vrc-worlds.md` | World components, Udon | World project (`com.vrchat.worlds` present) |
| `./vrcfury.md` | VRCFury components, build pipeline, scanning, public API | VRCFury installed (`com.vrcfury.vrcfury` present) |
| `./poiyomi.md` | Poiyomi shader workflow, locking, property discovery | Working with Poiyomi materials |
| `./physbones.md` | PhysBone setup, colliders, SDK samples | Avatar project, working with PhysBones or dynamics |
