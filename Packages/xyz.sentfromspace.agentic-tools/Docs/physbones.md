# PhysBone Reference

**Inspection first:** When understanding an avatar, list all existing PhysBones before creating new ones — root bones, colliders, parameter names, grab/pose settings. Use `FindObjectsOfType<VRCPhysBone>()` and `FindObjectsOfType<VRCPhysBoneCollider>()` to enumerate them.

When configuring PhysBones, inspect the avatar's existing PhysBones first. For new PhysBones, reference the VRC SDK sample scene (discover via `AssetDatabase.FindAssets("t:Scene", new[]{"Packages/com.vrchat.avatars/Samples/"})`) and tune values to the specific avatar's scale and body part. Hair chains typically need a chest VRCPhysBoneCollider (capsule) to prevent clipping — size it to the avatar.
