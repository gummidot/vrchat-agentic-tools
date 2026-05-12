# PhysBone Reference

**Inspection first:** When understanding an avatar, list all existing PhysBones before creating new ones — root bones, colliders, parameter names, grab/pose settings. Use `FindObjectsOfType<VRCPhysBone>()` and `FindObjectsOfType<VRCPhysBoneCollider>()` to enumerate them.

When configuring PhysBones, inspect the avatar's existing PhysBones first. For new PhysBones, reference the VRC SDK sample scene (discover via `AssetDatabase.FindAssets("t:Scene", new[]{"Packages/com.vrchat.avatars/Samples/"})`) and tune values to the specific avatar's scale and body part. Hair chains typically need a chest VRCPhysBoneCollider (capsule) to prevent clipping -- size it to the avatar.

## Scoping Rule

Only add `VRCPhysBone` components for bones belonging to the specific outfit being set up (identified by naming convention, e.g., `Silhouette_*`). Do not add PhysBones to all non-standard bone roots -- finger tracking bones, toe bones, twist/support bones, collider bones, and other outfits' physics bones already have their own components or are not meant to be physics-driven.

## Post-Copy Validation

PhysBones (and GlobalColliders) copied via Pumkins Avatar Copier can have their `rootTransform` pointing at bones on a *different* avatar in the scene (the source avatar they were copied from). This silently breaks physics at runtime.

**Validation pattern:** After copying, check all PhysBones on the target avatar:

```csharp
foreach (var pb in avatar.GetComponentsInChildren<VRCPhysBone>(true))
{
    var so = new SerializedObject(pb);
    var rootT = so.FindProperty("rootTransform").objectReferenceValue as Transform;
    if (rootT != null && !rootT.IsChildOf(avatar.transform))
    {
        // Find same-named bone under this avatar's armature
        var correctBone = armatureBones[rootT.name];
        if (correctBone == pb.gameObject.transform)
            so.FindProperty("rootTransform").objectReferenceValue = null; // self = null
        else
            so.FindProperty("rootTransform").objectReferenceValue = correctBone;
        so.ApplyModifiedProperties();
    }
}
```

**Self-ref clearing:** VRCPhysBone uses `self` when `rootTransform` is null. If the corrected bone is the same GameObject the component lives on, clear `rootTransform` to null instead of assigning it.
