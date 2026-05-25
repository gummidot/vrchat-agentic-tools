# VRC Constraints Reference

VRChat avatar constraint components (`VRCParentConstraint`, `VRCPositionConstraint`, `VRCRotationConstraint`, `VRCAimConstraint`, `VRCLookAtConstraint`, `VRCScaleConstraint`). Quest-compatible replacements for Unity's built-in constraints. Lessons here apply when scripting prefab edits via reflection / `AddComponent` / `SerializedObject`.

**Discover SDK version first.** Constraint internals have changed across SDK versions. Check `Packages/com.vrchat.base/package.json` and inspect types under `VRC.Dynamics.*` and `VRC.SDK3.Dynamics.Constraint.Components.*` via reflection rather than assuming a field layout.

## Class Hierarchy

All VRC constraints derive from `VRCConstraintBase` (in `VRC.Dynamics`), which holds the shared `Sources` list, `IsActive`, `Locked`, `FreezeToWorld`, `Weight`, and source-respect flags. Subclasses add type-specific fields:

```
VRCConstraintBase                    // Sources, IsActive, Locked, FreezeToWorld, Weight
  -> VRCParentConstraintBase         // (transform-affecting flags)
    -> VRCParentConstraint
  -> VRCPositionConstraintBase
    -> VRCPositionConstraint
  -> VRCRotationConstraintBase
    -> VRCRotationConstraint
  -> VRCAimConstraintBase            // AimAxis, UpAxis, WorldUp (enum), WorldUpVector
    -> VRCWorldUpConstraintBase      // WorldUpTransform
      -> VRCAimConstraint
      -> VRCLookAtConstraint
  -> VRCScaleConstraintBase
    -> VRCScaleConstraint
```

When reflecting fields, walk up the hierarchy: e.g. `AimAxis` is declared on `VRCAimConstraintBase`, `WorldUpTransform` on `VRCWorldUpConstraintBase`, `Sources` and `IsActive` on `VRCConstraintBase`.

`WorldUpType` enum (used by `VRCAimConstraintBase.WorldUp`): `SceneUp=0, ObjectUp=1, ObjectRotationUp=2, Vector=3, None=4`.

## `Sources` Is a STRUCT, Not a List

`VRC.Dynamics.VRCConstraintSourceKeyableList` is a **value type** (struct), not a reference type. Its private layout is:

- `source0` ... `source15` — sixteen inline `VRCConstraintSource` slots (each has `Weight` + `SourceTransform`)
- `totalLength` (Int32) — active source count
- `overflowList` (List<VRCConstraintSource>) — for >16 sources

It implements `IList<VRCConstraintSource>` for convenience, but...

**`IList.Add()` via reflection on `GetValue()` operates on a boxed COPY.** Mutations do not persist into the host component. This bug is silent: `lst.Add(src)` succeeds, the in-memory count looks right, but after `PrefabUtility.SaveAsPrefabAsset` the saved Sources is empty.

**Correct pattern** (editor scripting):

```csharp
var so = new SerializedObject(constraint);
so.FindProperty("Sources.source0.Weight").floatValue = 0.9f;
so.FindProperty("Sources.source0.SourceTransform").objectReferenceValue = originTransform;
so.FindProperty("Sources.source1.Weight").floatValue = 0.1f;
so.FindProperty("Sources.source1.SourceTransform").objectReferenceValue = containerTransform;
so.FindProperty("Sources.totalLength").intValue = 2;
so.ApplyModifiedPropertiesWithoutUndo();
```

Then `PrefabUtility.SaveAsPrefabAsset` to persist. Always verify after save by reloading the prefab and inspecting `Sources` count via reflection.

## `IsActive` Defaults to FALSE on `AddComponent`

When adding any VRC constraint via `gameObject.AddComponent(constraintType)` (typed reflection or via the typed `AddComponent<T>()`), the inherited `IsActive` field defaults to `false`. A constraint with `IsActive=false` evaluates silently to no-op — no error, no warning, the constrained transform simply ignores its sources.

Always explicitly set after `AddComponent`:

```csharp
var so = new SerializedObject(constraint);
so.FindProperty("IsActive").boolValue = true;
so.ApplyModifiedPropertiesWithoutUndo();
```

This differs from constraints created through the Unity Inspector, which start active.

## `Locked` Defaults to FALSE on `AddComponent`

`Locked` is the "Activate And Preserve Offset" baked state. It is `false` by default after `AddComponent` via reflection. Set via SerializedProperty `Locked=true` to match constraints created through the Inspector "Activate" button.

`Locked=false` constraints may evaluate inconsistently (different behavior between Editor preview and runtime, or between fresh-spawn and after-toggle states). Treat it as required.

## `ObjectRotationUp` Semantics

When `VRCAimConstraint.WorldUp = ObjectRotationUp`, the constraint uses the `WorldUpTransform`'s **local Y axis** (a rotation property) as the up reference, NOT the direction from the constraint pivot to that transform's position.

This catches people who try to use a position-driven helper transform as an up reference. A position-driven `WorldUpTransform` at identity rotation gives world-up regardless of its position — equivalent to `WorldUp = Vector(0,1,0)` and reconstructs no roll information.

To convert a target **position** into a usable up **axis**, nest an intermediate `VRCAimConstraint`:

```
RecvCube/
  UpAxisHelper        // VRCAimConstraint: source=TargetY position, AimAxis=+Y, WorldUp=Vector(0,1,0)
  RecvArrow           // VRCAimConstraint: source=TargetZ position, AimAxis=+Z,
                      //   WorldUp=ObjectRotationUp, WorldUpTransform=UpAxisHelper
```

After UpAxisHelper's aim solves, its local `+Y` axis (in world space) points from RecvCube toward TargetY's world position. RecvArrow then consumes that as its up reference via `ObjectRotationUp`. The chain reconstructs the full 3-axis rotation defined by the two direction targets.

## FreezeToWorld and Late Joiners

`VRCParentConstraint.FreezeToWorld` captures the current world position of the source(s) at the moment it flips from `false` to `true`. It **cannot reconstruct the original freeze position** for a remote client who joins after the flip — their constraint captures at the avatar position on their first frame after spawn.

This is the fundamental limitation that motivates contact-based position-encoding for synced world-drop systems: the synced floats are the truth source for late-joiners, not the FreezeToWorld constraint. See [vrc-contacts.md](./vrc-contacts.md) for the encoding/decoding pattern and the critical `localOnly` gotcha.

## Verification Pattern

After scripted constraint edits, always verify by reloading the prefab and inspecting via reflection. A defensive dump:

```csharp
var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
foreach (var t in prefab.GetComponentsInChildren<Transform>(true)) {
  foreach (var c in t.GetComponents<Component>()) {
    if (c == null || !c.GetType().Name.Contains("Constraint")) continue;
    var so = new SerializedObject(c);
    Debug.Log($"{t.name} {c.GetType().Name} " +
              $"IsActive={so.FindProperty("IsActive").boolValue} " +
              $"Locked={so.FindProperty("Locked").boolValue} " +
              $"SourcesCount={so.FindProperty("Sources.totalLength").intValue}");
  }
}
```

Catch silent struct-copy / default-false bugs before upload, not in-VRChat.
