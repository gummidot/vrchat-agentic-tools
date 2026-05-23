# VRCFury

**VRCFury docs:** https://vrcfury.com/ -- non-destructive avatar tools (toggles, gestures, controller merging, prefab-based workflows).

Package path: `Packages/com.vrcfury.vrcfury`
Ships as **pure C# source** (no DLLs).

## Component Storage

VRCFury uses v3 storage: single feature in `content` field (`[SerializeReference] FeatureModel`). Legacy v2 (`config.features` list) is auto-migrated. When reading via MCP, `config.features` will be empty -- always use `SerializedObject.FindProperty("content").managedReferenceValue` to access the feature.

## Component Order = Menu Order

VRCFury processes components in **GameObject component order**. Menu items, toggles, and submenu entries appear in-game in the order their VRCFury components are arranged on the GameObject. If a FullController (e.g., for a submenu's radial) is the last component, its menu entries appear last in that submenu.

To reorder via MCP, use `UnityEditorInternal.ComponentUtility.MoveComponentUp/Down` inside a `PrefabUtility.LoadPrefabContents`/`SaveAsPrefabAsset` block:

```csharp
var root = PrefabUtility.LoadPrefabContents(prefabPath);
var components = root.GetComponents(vrcfuryType);
var target = components[indexToMove];
for (int i = 0; i < moveCount; i++)
    UnityEditorInternal.ComponentUtility.MoveComponentUp(target);
PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
PrefabUtility.UnloadPrefabContents(root);
```

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

**Component safety:** Never bulk-remove VRCFury components to "clean up" before adding new ones. When adding a component to a GameObject that already has VRCFury components, only add. If you need to remove something you previously created, identify it by a unique field (e.g., the `content.name` menu path) and only destroy that specific instance. Using `GetComponents()` + destroy loop wipes user-created components that cannot be recovered.

**Source-first rule:** Before creating or configuring any VRCFury component:
1. **Read the public API first** — check `PublicApi/` for a supported method. The public API is safer and handles internal details automatically.
2. **If the public API doesn't cover the need**, fall back to `SerializedObject` — but first **read the model source** at `Runtime/VF/Model/Feature/<FeatureName>.cs` to understand all required fields, their types, and default values. Never guess at serialized field names or types.
3. **For advanced features** (transitions, separate local, security, exclusive tags), also **read the builder source** at `Editor/VF/Feature/<FeatureName>Builder.cs`. The model defines fields; the builder defines how they're used — transition timing, state machine structure, resting state registration. Never guess at runtime semantics.

## Features & StateAction Discovery

Discover available VRCFury features by scanning `Runtime/VF/Model/Feature/` for `FeatureModel` subclasses. Discover available state actions by scanning `Runtime/VF/Model/StateAction/` for action types and their fields.

Prefer specific actions (ObjectToggleAction, BlendShapeAction, MaterialAction, etc.) over AnimationClipAction when possible.

## Toggle Transition Pitfall

When using `hasTransition = true`, **the ON state (`state.actions`) must not be empty** — VRCFury uses it for resting state registration, WD ON defaults, and `expandIntoTransition`. Read `Editor/VF/Feature/ToggleBuilder.cs` for the full state machine structure.

## Clothing Visibility Toggles

Pattern for toggles that hide a clothing mesh and reveal the body underneath:

1. **Set `defaultOn: false`** -- clothing is visible in the scene's default state (mesh active, body blendshape at rest value of 100).
2. **ObjectToggleAction with `mode = TurnOff`** -- when toggled ON, the mesh is hidden.
3. **BlendShapeAction** targeting the body mesh with the matching blendshape set to `0` -- when toggled ON, the body is revealed.

**BlendShapeAction rest-value gotcha:** VRCFury's `BlendShapeAction` only animates between the scene rest value (toggle inactive) and the action's `blendShapeValue` (toggle active). If both are the same (e.g., rest=100 and action=100), nothing happens. The scene rest value must differ from the action value.

**Reverse pattern (mesh OFF in scene):** If the mesh is disabled in the scene by default, use `ObjectToggleAction` with `mode = TurnOn` and `BlendShapeAction` with value `100` (hide body when mesh appears). The scene blendshape rest value must be `0` (body visible when mesh is inactive).

**Blendshape scoping:** Each toggle should only drive blendshapes that correspond to its own mesh's body coverage area. Don't bundle unrelated blendshapes (e.g., a boots toggle should only drive the boots blendshape, not also a pants blendshape, even if both are from the same outfit package).

## FlipBook Material Swap Sliders

Pattern for radial sliders that swap between material color variants:

1. **Toggle with `slider: true`** and a `FlipBookBuilderAction` inside `state.actions`.
2. **Each `FlipBookPage`** has a `state` containing `MaterialAction`s.
3. **MaterialAction** references a `renderer` (the SkinnedMeshRenderer on the same mesh), a `materialIndex` (slot number), and a `GuidMaterial`.
4. **GuidMaterial.id format:** `"{assetGUID}|{assetPath}"` (e.g., `"f5c9c34d...|Assets/MyPackage/Materials/MyMat.mat"`). Also set `objRef` to the loaded Material asset for reliability (some code paths use `objRef` directly).
5. **Place the VRCFury component on the mesh GameObject itself**, not on a separate child or the avatar root.
6. Page 0 should match the scene's default materials (slider value 0 = no change).
7. **Type paths for reflection:** `FlipBookBuilderAction` is at `VF.Model.StateAction.FlipBookBuilderAction`. Pages are the nested type `VF.Model.StateAction.FlipBookBuilderAction+FlipBookPage` (field: `state` of type `VF.Model.State`). `GuidMaterial` is `VF.Model.GuidMaterial` (fields: `id`, `objRef`, `typeDetector`).

## Parameter Type Matching

A VRCFury Toggle (non-slider, `slider: 0`) creates a **Bool** parameter. If a FullController on the same prefab references a `VRCExpressionParameters` asset that declares the same parameter name as **Float**, the build will fail with: *"parameter already exists with type Float"*. Ensure the expression parameter type matches the VRCFury component that owns it: Bool for non-slider Toggles, Float for Radial Puppets and slider Toggles.

## Unsynced (Local) Parameters via FullController

For features that don't need network sync (local-only toggles, personal effects), use unsynced parameters to avoid consuming the 256-bit synced budget.

**Pattern:**

1. **LocalParams asset:** Create a `VRCExpressionParameters` ScriptableObject with each parameter set to `networkSynced: 0` and `saved: 1`.

   `defaultValue` is the saved parameter's resting position. The valid range/meaning depends on the Toggle's clip shape -- see **Slider Toggle Clip Semantics** below. For non-slider Toggles (Bool params), use 0 (off) or 1 (on).

2. **FullController component:** Add a VRCFury FullController to the prefab with:
   - `prms` array referencing the LocalParams asset
   - `globalParams: ['*']` -- makes all params in the asset global (bypasses VRCFury prefix scoping)
   - No controllers or menus needed if only declaring params

3. **Global param names:** Use `useGlobalParam = true` on each Toggle/Radial with a namespaced name (e.g., `MyPrefab/FeatureToggle`). This is separate from the expression menu path.

See **Parameter Type Matching** above for type alignment between LocalParams and VRCFury components.

## Slider Toggle Clip Semantics

Slider Toggles (`slider: true` on `VF.Model.Feature.Toggle`) wire the clip via `MotionTime(weight)` in `Editor/VF/Feature/ToggleBuilder.cs` -- the slider's Float param value is the **clip time** (in seconds), sampled directly from the AnimationClip curve. Behavior depends on the clip shape:

**Single-keyframe clips (the common pattern):** `Editor/VF/Service/ActionClipService.cs` calls `MergeSingleFrameClips((0, empty), (1, clip))`, producing a 0..1s ramp from "no animation / shader default" to "clip value". Slider 0 = shader default, slider 1 = clip's frame-0 value, linear blend in between. In this case `defaultValue` should be `0` (slider parked at shader-default end).

**Multi-keyframe clips (signed ranges, midpoint defaults, non-linear curves):** The merge is bypassed and the curve is sampled at `time = slider`. So a 2-keyframe clip with `t=0:value=A`, `t=1:value=B`, `m_StopTime=1.0` gives slider 0..1 -> shader A..B with linear interpolation. This unlocks signed-range sliders (e.g., `t=0:-0.5`, `t=1:+0.5` for a slider that maps midpoint to zero) and non-linear remaps that single-keyframe clips can't express. Set `defaultValue` to the desired resting clip-time (e.g., `0.5` to park at midpoint), and `defaultSliderValue` to the matching slider position.

**Pitfalls:**
- When using FullController + LocalParams alongside slider Toggles, the toggle's `defaultSliderValue` takes precedence over the LocalParams `defaultValue` at load time. Both must be set to the same value. If the toggle has `defaultSliderValue=0` but LocalParams declares `defaultValue=0.8`, the parameter starts at 0.
- Always set `m_StopTime` to match the last keyframe time. The animator samples in absolute seconds; if `m_StopTime` is left at 0 (or matches a tiny frame-time like `1/60`), `PingPong` post-infinity wrapping at slider=0.5 lands at an unrelated curve value and looks broken.
- Verify keyframe times via `AnimationUtility.GetEditorCurve` before shipping -- MCP-built clips can leave the wrong `m_StopTime` if you only use `SetEditorCurve` without explicitly setting it.
- Verify the current `ToggleBuilder` source if behavior seems off; VRCFury internals can change between versions.

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

**Toggle creation via reflection (when public API is insufficient):**
- Component type: `VF.Model.VRCFury` (not `VRCFuryComponent` which is abstract)
- Content assigned via: `so.FindProperty("content").managedReferenceValue = content;`
- Must set version: `so.FindProperty("version").intValue = 3;`
- Toggle type: `VF.Model.Feature.Toggle`
  - Fields: `name` (string, menu path), `saved` (bool), `slider` (bool), `state` (State), `simpleOutTransition` (bool), `expandIntoTransition` (bool), `defaultOn` (bool)
- ObjectToggleAction mode enum: TurnOn=0, TurnOff=1, Toggle=2
- BlendShapeAction fields: `blendShape` (string), `allRenderers` (bool), `blendShapeValue` (float), `renderer` (Renderer, optional)

**Entry point:** `com.vrcfury.api.FuryComponents` — read source files in `Packages/com.vrcfury.vrcfury/PublicApi/` to discover available factory methods (e.g., `CreateToggle`, `CreateArmatureLink`) and their returned types' APIs.

**Common FuryToggle methods:**
- `SetMenuPath(string)` — VRC menu path
- `SetGlobalParameter(string)` — expression parameter name
- `SetSlider(true)` — makes a radial puppet (Float param). Takes a `bool` argument.
- `SetDefaultOn()` — starts enabled on avatar load
- `SetSaved()` — persist value across avatar reload/world change
- `SetExclusiveOffState()` — this toggle re-enables when all others with the same exclusive tag turn off
- `AddExclusiveTag(string)` — radio group tag
- `GetActions().AddAnimationClip(clip)` — attach an animation clip to the ON state

**Not in public API (must edit prefab YAML):** `defaultSliderValue` -- the initial slider position. After creating the prefab, read its YAML, find `defaultSliderValue: 0` lines, and set them directly. Then `AssetDatabase.ImportAsset()` to reload.

**Common FuryFullController methods:**
- `AddParams(VRCExpressionParameters)` — declare expression parameters
- `AddGlobalParam(string)` — make a param global (bypass VRCFury prefix scoping). Use `"*"` for all params in the asset.
- `AddController(RuntimeAnimatorController, AnimLayerType)` — merge a controller
- `AddMenu(VRCExpressionsMenu, string prefix)` — merge a menu
