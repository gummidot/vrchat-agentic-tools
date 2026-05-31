# VRChat Avatars

## Avatar Scene Exploration

- **Find avatar root** by enumerating all `VRCAvatarDescriptor` objects and recording each descriptor's full hierarchy path (`Root/Child/Avatar`), then target the avatar by exact path

The VRChat Avatar SDK is located at:
- Packages/com.vrchat.base
- Packages/com.vrchat.avatars

**VRChat SDK docs:** Detect the installed SDK version from `Packages/com.vrchat.base/package.json` (`version` field). If the version contains "beta" (e.g., `3.8.2-beta.1`), use the beta docs. Otherwise use the release docs.

- **Beta SDK:** https://vrc-beta-docs.netlify.app/avatars/
- **Release SDK:** https://creators.vrchat.com/avatars/
- **SDK release notes:** https://creators.vrchat.com/releases/

Key pages for avatar work (substitute the base URL accordingly):
- Avatars overview (`/avatars`) -- AV3 concepts, playable layers, Write Defaults, visemes, eye simulation
- Avatar components (`/avatars/avatar-components`) -- PhysBones, Contacts, Constraints, Raycast, Head Chop
- Expression Menu & Controls (`/avatars/expression-menu-and-controls`) -- menus, parameters, control types
- Animator parameters (`/avatars/animator-parameters`) -- built-in parameters and sync types
- Performance ranking (`/avatars/avatar-performance-ranking-system`) -- performance limits and rankings
- PhysBones (`/common-components/physbones`), Contacts (`/common-components/contacts`), Constraints (`/common-components/constraints`) -- detailed component docs

## VRC SDK Notes

The VRC SDK ships as compiled DLLs — no readable C# source for components.
- Whitelist source (readable C#): `AvatarValidation.cs` in both base and avatars packages
- PhysBone reference scene: `Packages/com.vrchat.avatars/Samples/Dynamics/Robot Avatar/Avatar Dynamics Robot Avatar PC.unity`

**Key C# namespaces:**
- `VRC.SDK3.Avatars.Components` -- VRCAvatarDescriptor, VRCStation
- `VRC.SDK3.Dynamics.PhysBone.Components` -- VRCPhysBone, VRCPhysBoneCollider
- `VRC.SDK3.Dynamics.Contact.Components` -- VRCContactSender, VRCContactReceiver
- `VRC.SDK3.Dynamics.Constraint.Components` -- VRCPositionConstraint, VRCRotationConstraint, etc.
- `VRC.SDK3.Avatars.ScriptableObjects` -- VRCExpressionParameters, VRCExpressionsMenu

**ViewPosition** (`VRCAvatarDescriptor.ViewPosition`): baked at upload time. Cannot be animated at runtime. If a build-time tool changes head/eye positioning (bone scale, bone repositioning), the ViewPosition must be recalculated in the same NDMF pass or pre-upload hook.

## Avatar Logic

### Write Defaults Convention

**Use Write Defaults ON** on all states.
- WD OFF in unmasked controllers causes FX to claim ownership of transforms/muscles from higher layers (Gesture)
- WD OFF with empty/None motions causes "sticky" properties that never reset
- Direct BlendTrees and Additive layers MUST always be WD ON (values fly off with WD OFF)
- Mixed WD in a controller causes random properties to stick
- **Rule:** All states WD ON. Never mix within a controller.

### Playable Layers

| Layer | Purpose |
|---|---|
| **Base** | Locomotion (humanoid muscles). Default supplied by VRChat. |
| **Additive** | Additive on top of Base (breathing, idle animations). Always WD ON. |
| **Gesture** | Hand gestures, face expressions. Masked to upper body / hands. |
| **Action** | Full-body overrides (emotes, AFK). Starts at weight 0; use VRCPlayableLayerControl to blend in. |
| **FX** | Non-transform only: toggles, blendshapes, materials, shader properties. |

### Toggle & Feature Inspection Workflow

When inspecting avatar toggles/features — regardless of whether the user mentions VRCFury, FX, or just "toggles" — **always check all sources**:

If the avatar has one or more VRCFury components, this workflow applies to **any work on that avatar** (including non-VRCFury edits like direct FX/controller/menu/parameter/animation/material changes). Verification must use the built output from `VRCAvatarDescriptor` after VRCFury build succeeds and Unity enters Play Mode.

1. **VRCFury components** — scan all VRCFury components in the scene (Toggle, FullController, GestureDriver, Puppet). These generate layers/menus/parameters during VRCFury build and may not appear in descriptor-linked playable layer controllers before build completes.
2. **Expression Parameters** — read the VRCExpressionParameters asset from the descriptor. Lists all synced parameters with types, defaults, saved/synced flags. Cross-reference with what controllers and VRCFury use.
3. **Expression Menus** — walk the full menu tree (root menu + all submenus). Shows what the user sees in the radial menu: toggles, submenus, puppets, buttons.
4. **All playable layer controllers** — not just FX. Inspect layers, states, transitions, blend trees, and state behaviors in:
   - **FX** (toggles, blendshapes, material swaps, shader properties)
   - **Gesture** (hand gesture → face expression mappings, can also have toggles)
   - **Action** (emotes, AFK states)
   - **Additive** (breathing, idle overlays)
   - **Base** (usually default, but may be customized)

   For each controller, also check:
   - **State behaviors** — especially VRCAvatarParameterDriver (side-effect parameter drives), VRCAnimatorTrackingControl (tracking overrides), VRCPlayableLayerControl (layer blending). These are invisible if you only look at transitions and motions.
   - **Layer weight and AvatarMask** — a layer at default weight 0 does nothing until blended in by a state behavior. Masks change what a layer can affect. Report both for every layer.
   - **WD consistency** — flag any states with Write Defaults OFF or mixed WD within a controller.
5. **Parameter budget** — calculate used bits from the VRCExpressionParameters asset (Bool=1, Int=8, Float=8 for synced params). Report used/256 bits remaining. Unsynced parameters don't count toward the budget but still exist in controllers. **Note:** When VRCFury is in the project, it automatically adds Parameter Compressor to the avatar at upload time, which reduces synced parameter bandwidth — so the raw bit count is a worst-case estimate.

Present a unified summary combining all sources. Flag any inconsistencies (e.g., a parameter in the menu but missing from the parameters asset, an FX layer with no corresponding menu entry, mixed Write Defaults, or a layer at weight 0 with no behavior to activate it).

### Avatar Audit Workflow

When the user asks to understand the full avatar setup — not just toggles — check **all** of the following:

1. **Dynamics:**
   - **PhysBones** — list all PhysBone roots, their key settings (pull, spring, stiffness, gravity), colliders, and parameter names (which feed `_IsGrabbed`, `_Angle`, etc. into animators).
   - **Contacts** — all VRCContactSenders and VRCContactReceivers. Report collision tags, receiver types (Constant/OnEnter/Proximity), parameter names, and what they're used for (headpats, boops, proximity toggles).
   - **Constraints** — VRC constraints (VRCPositionConstraint, VRCRotationConstraint, VRCScaleConstraint, VRCParentConstraint, VRCAimConstraint, VRCLookAtConstraint). Often used for world-fixed objects, orbit systems, and prop mechanics.
2. **Toggles & features** — run the Toggle & Feature Inspection Workflow above.
3. **Renderers & materials** — list all SkinnedMeshRenderers and MeshRenderers, their material counts, shader names, and blendshape counts. This gives a quick picture of visual complexity.
4. **Other components** — VRCStations, AudioSources, Lights, ParticleSystems, TrailRenderers, LineRenderers. These affect performance and behavior.

Present findings grouped by category. Flag potential issues: high PhysBone counts, contacts without matching parameters, constraints with missing sources, unoptimized renderers.

### Expression Parameters

Types: Bool=1bit, Int=8bits (0-255), Float=8bits (-1.0 to 1.0). **Synced budget: 256 bits.**

**Serialized `valueType` enum:** Int=0, Float=1, Bool=2. Note the non-obvious ordering: Float is 1, not 2. When reading `.asset` YAML files, `valueType: 1` means Float, not Int.

Create via `ScriptableObject.CreateInstance<VRCExpressionParameters>()`. The `.parameters` field is an array of `Parameter` with fields: `name` (string), `valueType` (Bool/Int/Float), `defaultValue`, `saved`, `networkSynced`.

### Expression Menus

Max 8 controls per menu. Control types: Button, Toggle, SubMenu, TwoAxisPuppet, FourAxisPuppet, RadialPuppet. Discover enum values and subParameter counts via `System.Enum.GetValues(typeof(VRCExpressionsMenu.Control.ControlType))`.

### Contacts, Constraints & PhysBone Parameters

**Dynamics inspection:** When understanding an avatar, list all contacts and constraints before creating new ones. For contacts, note collision tags, receiver types, and parameter names -- these often drive interaction systems (headpats, boops, proximity toggles). For constraints, note source objects and what they achieve (world-fixed props, orbit systems, etc.).

**Contact shapes (SDK 3.10.4+):**
- Sphere (original), Capsule (original), **Box** (added SDK 3.10.4-beta.2, May 2026)
- Box: width/height/depth individually adjustable, **max 6m per axis** (applied after scaling)
- Box receivers support `Use Face Proximity`: measures distance from sender to the **positive Z face** (linear per-axis readout)
- Multi-sender: if multiple contacts detected, receiver reports the **closest**
- `Allow Self`: works on all shapes (receiver detects senders on same avatar)
- `Local Only`: available on both senders and receivers; local-only contacts don't count toward perf rank (up to 256 total)
- Receiver types: Constant (bool, always 1 when touching), OnEnter (bool, triggers once), Proximity (float 0.0-1.0)

**Contact performance rank thresholds (PC):**
| Rating | Senders | Receivers |
|--------|---------|-----------|
| Excellent | 4 | 4 |
| Good | 8 | 8 |
| Medium | 16 | 16 |
| Poor | 24 | 24 |

### VRChat Upload-Ready Avatar Checklist

1. **VRCAvatarDescriptor** on the root GameObject (ViewPosition, lip sync, eye look, playable layers)
2. **PipelineManager** on the root (holds `blueprintId` — SDK auto-adds it)
3. **Animator** with a humanoid Avatar asset (from FBX import)
4. **ViewPosition** between the eyes, slightly forward: typically `(0, eyeHeight, 0.05-0.1)`
5. Root at scene root (not nested under another object)

## Post-Build Verification (AvatarTypeChecker)

`Packages/xyz.sentfromspace.agentic-tools/Editor/AvatarTypeChecker.cs` performs static analysis on the **built** avatar clone after VRCFury/NDMF preprocessing. It catches parameter mismatches (like VRCFury renames), broken animation paths, missing blendshapes, and invalid material properties.

**When to run:** After any avatar edit that changes parameters, animations, toggles, materials, or PhysBones — run this as the final verification step:

1. Enter Play Mode (triggers VRCFury/NDMF build pipeline)
2. Discover descriptor paths (if needed) via MCP, then choose the exact avatar path:
   `var ds = UnityEngine.Object.FindObjectsOfType<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>(true); var sb = new System.Text.StringBuilder(); foreach (var d in ds) { var t = d.transform; var p = new System.Collections.Generic.List<string>(); while (t != null) { p.Add(t.name); t = t.parent; } p.Reverse(); sb.AppendLine(string.Join("/", p)); } return sb.ToString();`
3. Call `return SentFromSpace.AgenticTools.Editor.AvatarTypeChecker.ValidateBuiltAvatar("Root/Child/Avatar");` via MCP
4. Review the output — fix any `[ERROR]` items before considering the task complete
5. Exit Play Mode

`ValidateBuiltAvatar()` without a path intentionally returns an error to avoid selecting the wrong avatar.

**What it checks:**
- **Parameters:** PhysBone/Contact params vs animator controllers (with VRCFury rename fuzzy matching), type mismatches, unused expression params, menu param validity, synced bit budget
- **Animation Paths:** hierarchy path resolution, component existence, blendshape names, material slot indices
- **Material Properties:** shader property existence on locked materials, Poiyomi AnimatedTag detection

**Interpreting results:**
- `[ERROR]` = broken at runtime, must fix
- `[WARN]` = wasteful but functional
- `[INFO]` = informational (bit budget, VRCFT/VRCFury type difference counts)
- VRCFT (OSC-driven face tracking) Bool→Float type differences and undefined controller params are auto-detected and skipped
- VRCFury toggle (`VF###_*` prefix) Bool→Float type differences are auto-detected and skipped
- VRCFury dummy paths (`ThisHopefullyDoesntExist`) and internal params (`VF DN`, etc.) are filtered out

## Toggle Verification (GestureManagerVerifier)

`Packages/xyz.sentfromspace.agentic-tools/Editor/GestureManagerVerifier.cs` uses Gesture Manager (if installed) to verify toggles actually work at runtime. It tests **specific toggles by parameter name** — pass the authoring-time names of the toggles you just changed. VRCFury `VF###_` prefix renames are matched automatically.

**When to run:** After AvatarTypeChecker, when toggle behavior needs runtime verification (new/modified toggles, debugging broken toggles). Requires play mode. If Gesture Manager isn't in the scene, it's instantiated automatically (cleaned up on play mode exit).

**MCP invocation:**
`return SentFromSpace.AgenticTools.Editor.GestureManagerVerifier.VerifyToggles("Root/Avatar", "Clothing/Top/Bikini", "Clothing/Acc/Glasses");`

Authoring-time parameter names work even when VRCFury renames them with `VF###_` prefixes at build time. Calling without parameter names returns an error — always specify which toggles to test.

**What it checks:** For each matched Toggle in the expression menu, flips the parameter and reports:
- Object active state changes (activeSelf)
- Blendshape weight changes
- Material swaps (different material instances)
- Material property changes (shader float/color values — dissolve, emission, etc.)

**Interpreting results:**
- `[PASS]` = toggle caused visible changes
- `[FAIL]` = toggle changed nothing (broken animation path, wrong parameter, WD issue)
- `[WARN]` = parameter not found in expression menu or Gesture Manager
