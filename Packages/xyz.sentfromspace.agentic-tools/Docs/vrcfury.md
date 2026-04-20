# VRCFury

**VRCFury docs:** https://vrcfury.com/ -- non-destructive avatar tools (toggles, gestures, controller merging, prefab-based workflows).

Package path: `Packages/com.vrcfury.vrcfury`
Ships as **pure C# source** (no DLLs).

## Component Storage

VRCFury uses v3 storage: single feature in `content` field (`[SerializeReference] FeatureModel`). Legacy v2 (`config.features` list) is auto-migrated.

## Pre-Creation Conflict Check

**Before creating any new toggle or animation layer for an object**, search for existing bindings:

1. **Search all FullController-merged controllers** — find AnimatorControllers referenced by FullController components, then search their layers for bindings on the target object path (e.g., `shirt|m_IsActive`, `shirt|material.*`).
2. **Search existing VRCFury Toggles** — check if another Toggle already controls the same object.
3. **Search the avatar descriptor's playable layer controllers** — the FX controller (and others) may already have layers affecting the object.

If conflicts exist, decide whether to **modify** the existing system or **replace** it — never silently add a second controller for the same object.

## VRCFury Build Verification & Discovery (Required for Avatar Work)

If the target avatar has one or more VRCFury components, this applies to **all work on that avatar**. Use this discovery-first workflow to infer behavior from source + build output, not from assumptions.

1. **Inventory features** — enumerate all VRCFury components on the avatar. Record feature types, referenced controllers/menus/params, base object overrides, and binding rewrites.
2. **Trace builders** — for each feature in use, read the corresponding builder(s) in `Editor/VF/Feature/` and any called service logic that can rewrite parameters, bindings, layers, or object paths.
3. **Predict transformations** — create a quick authoring-to-built map for parameter names, animation binding paths/types, layer/state names, and object existence/paths after preprocess.
4. **Build and verify** — enter Play Mode to trigger the build pipeline. All tools implementing `IVRCSDKPreprocessAvatarCallback` (VRCFury, NDMF, etc.) run automatically on Play Mode entry. Read resulting built assets from `VRCAvatarDescriptor` (expression parameters, expression menu, all playable layer controllers). Treat this merged output as authoritative.
5. **Diff and resolve** — compare predicted vs built results, then adjust assets/config until they match intended behavior. If build fails or built descriptor assets are unavailable, treat verification as incomplete and report the blocker.

**Definition of done:** Do not mark avatar changes complete until both source-level checks and built-output verification pass.

## Scanning Existing VRCFury Components

**Reflection access:** Assembly `"VRCFury"`, type `"VF.Model.VRCFury"`, then `FindObjectsOfType(type)`. Cast each to `Component` to get `.gameObject`.

**SerializedProperty paths** (all relative to `SerializedObject` → `FindProperty`):
- **Feature type:** `"content"` → `.managedReferenceFullTypename` (e.g., contains `"Toggle"`, `"FullController"`)
- **Toggle fields:** `"content"` → `name` (menu label), `saved`, `defaultOn`, `state.actions` (array — each element's `.managedReferenceFullTypename` = action type)
- **FullController fields:** `"content"` → `controllers` array (each has `controller.objRef`, `type`), `menus` array (`menu.objRef`), `prms` array (`parameters.objRef`)

## Modifying Existing VRCFury Components

Use `SerializedObject` to modify existing VRCFury components with undo support.

- **Action types** live at `"VF.Model.StateAction.<ActionName>"` (e.g., `ObjectToggleAction`)
- **Two-pass pattern (critical):** After setting `managedReferenceValue = Activator.CreateInstance(actionType)` on a new array element, you must `ApplyModifiedProperties()` → `so.Update()` → re-fetch the element via `GetArrayElementAtIndex()` before setting child fields (e.g., `FindPropertyRelative("obj")`). Skipping this causes null references.

**GuidAnimationClip fields:** When setting animation clips via SerializedObject, use `FindPropertyRelative("clip.objRef")` — this is the `UnityEngine.Object` reference inside the GuidWrapper.

**Usage policy:** Only use VRCFury components when the user explicitly requests it. If VRCFury would simplify a task but wasn't requested, ask the user before using it. Default to manual FX controllers + expression parameters + expression menus.

**Source-first rule:** Before creating or configuring any VRCFury component:
1. **Read the public API first** — check `PublicApi/` for a supported method. The public API is safer and handles internal details automatically.
2. **If the public API doesn't cover the need**, fall back to `SerializedObject` — but first **read the model source** at `Runtime/VF/Model/Feature/<FeatureName>.cs` to understand all required fields, their types, and default values. Never guess at serialized field names or types.
3. **For advanced features** (transitions, separate local, security, exclusive tags), also **read the builder source** at `Editor/VF/Feature/<FeatureName>Builder.cs`. The model defines fields; the builder defines how they're used — transition timing, state machine structure, resting state registration. Never guess at runtime semantics.

## Features & StateAction Discovery

Discover available VRCFury features by scanning `Runtime/VF/Model/Feature/` for `FeatureModel` subclasses. Discover available state actions by scanning `Runtime/VF/Model/StateAction/` for action types and their fields.

Prefer specific actions (ObjectToggleAction, BlendShapeAction, MaterialAction, etc.) over AnimationClipAction when possible.

## Toggle Transition Pitfall

When using `hasTransition = true`, **the ON state (`state.actions`) must not be empty** — VRCFury uses it for resting state registration, WD ON defaults, and `expandIntoTransition`. Read `Editor/VF/Feature/ToggleBuilder.cs` for the full state machine structure.

## Animation Binding Paths in VRCFury Prefabs

Animation clips in VRCFury prefabs use paths **relative to the prefab root**, not the avatar root. VRCFury automatically rewrites these paths during build based on where the prefab is placed in the avatar hierarchy. Do not use the full avatar hierarchy path in authoring-time clips.

## Exclusive Tags (Radio Groups)

Use exclusive tags to make a set of toggles act as a radio group -- only one active at a time. Ref: https://vrcfury.com/components/toggle

- **Exclusive Tag:** Give all toggles in the group the same tag string (e.g., `GhostFXBlendMode`). When one is enabled, all others with that tag are disabled. Multiple tags can be comma-separated.
- **Exclusive Off State:** Set on exactly **one** toggle -- the "default" option. It auto-activates when all other toggles with the same tag are disabled. Without this, deselecting all toggles leaves the group in an undriven state (falls back to shader default).
- **Default On:** Set on the same toggle as Exclusive Off State so it starts enabled on avatar load.
- **Do NOT set Exclusive Off State on multiple toggles** -- they will fight for enablement when all others are off.
- **Public API:** `SetExclusiveOffState()` sets `exclusiveOffState = true`. `AddExclusiveTag(tag)` adds the tag. `SetDefaultOn()` sets default on.

## Key Source Paths

| Content | Path (relative to package root) |
|---|---|
| MonoBehaviour components | `Runtime/VF/Component/` |
| FeatureModel subclasses | `Runtime/VF/Model/Feature/` |
| StateAction subclasses | `Runtime/VF/Model/StateAction/` |

## Public API (`com.vrcfury.api`)

**Creating new components:** Use the public API — it's simpler and handles setup automatically.
**Reading/modifying existing components:** The public API is create-only. Internal types (`VF.Model.*`) can't be referenced directly in MCP snippets (they're `internal`). Use reflection to get the type (`vfAsm.GetType("VF.Model.VRCFury")`) and `SerializedObject` to read/write properties. See "Scanning Existing VRCFury Components" above.

**Entry point:** `com.vrcfury.api.FuryComponents` — read source files in `Packages/com.vrcfury.vrcfury/PublicApi/` to discover available factory methods (e.g., `CreateToggle`, `CreateArmatureLink`) and their returned types' APIs.
