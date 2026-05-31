# NDMF (Non-Destructive Modular Framework)

Build-time avatar processing framework. Both **Modular Avatar** and **VRCFury** run on NDMF. Use it to create custom build passes that modify the avatar at upload/play-mode without altering the scene.

Package path: `Packages/nadena.dev.ndmf`

## IEditorOnly Requirement

Any MonoBehaviour placed on a VRChat avatar that exists only for build-time processing **must** implement `IEditorOnly`. Without it, the VRC SDK warns "the following component types are found on the avatar and will be removed by the client" and strips the component at upload.

**Use `INDMFEditorOnly`** (from `nadena.dev.ndmf` namespace, in `nadena.dev.ndmf.runtime` assembly). This conditionally inherits from `VRC.SDKBase.IEditorOnly` when the VRC SDK is present, keeping your component portable.

```csharp
using nadena.dev.ndmf;
using UnityEngine;

public class MyBuildTimeComponent : MonoBehaviour, INDMFEditorOnly
{
    // This component won't trigger the VRC SDK stripping warning
}
```

The runtime asmdef must reference the NDMF runtime assembly: `GUID:fe747755f7b44e048820525b07f9b956`.

## Creating a Custom NDMF Plugin

A plugin registers one or more build passes. Each pass runs at a specific phase and can be ordered relative to other plugins.

### Assembly Setup

- **Runtime asmdef** (for your MonoBehaviour): reference `nadena.dev.ndmf.runtime`
- **Editor asmdef** (for Plugin + Pass): reference `nadena.dev.ndmf` (GUID: `62ced99b048af7f4d8dfe4bed8373d76`), your runtime asmdef, and any SDK assemblies you need (e.g., `VRC.SDK3A` GUID: `5718fb738711cd34ea54e9553040911d`)
- **package.json**: add `"nadena.dev.ndmf": ">=1.0.0"` to dependencies

### Plugin + Pass Pattern

```csharp
using nadena.dev.ndmf;
using UnityEngine;

[assembly: ExportsPlugin(typeof(MyNamespace.MyPlugin))]

namespace MyNamespace
{
    [RunsOnAllPlatforms]
    internal class MyPlugin : Plugin<MyPlugin>
    {
        public override string QualifiedName => "com.example.my-plugin";
        public override string DisplayName => "My Plugin";

        protected override void Configure()
        {
            InPhase(BuildPhase.Transforming)
                .AfterPlugin("nadena.dev.modular-avatar")
                .Run(MyPass.Instance);
        }
    }

    internal class MyPass : Pass<MyPass>
    {
        public override string DisplayName => "My Pass";

        protected override void Execute(BuildContext context)
        {
            var root = context.AvatarRootObject;
            // Find your components, apply modifications, then destroy them
            foreach (var comp in root.GetComponentsInChildren<MyBuildTimeComponent>(true))
            {
                // ... process ...
                Object.DestroyImmediate(comp);
            }
        }
    }
}
```

### Build Phases (Execution Order)

| Phase | Purpose |
|-------|---------|
| `FirstChance` | Runs before platform init |
| `PlatformInit` | Early platform backend init |
| `Resolving` | Early component processing |
| `Generating` | Component generation (before transforms) |
| `Transforming` | General avatar transformations (MA and most plugins use this) |
| `Optimizing` | Pure optimizations (late) |
| `PlatformFinish` | Final platform cleanup/validation |

### Ordering Constraints

```csharp
seq.Run(MyPass.Instance)
    .BeforePlugin("com.vrcfury.vrcfury")      // Run before entire VRCFury
    .AfterPlugin("nadena.dev.modular-avatar"); // Run after entire MA
```

## Key Facts

- **ViewPosition is baked at upload** and cannot be animated at runtime. Adjust it in an NDMF pass or pre-upload hook if your plugin changes head/eye positioning.
- **MA Scale Adjuster** (`ModularAvatarScaleAdjuster`) is for non-uniform bone fitting (X!=Y!=Z). For uniform bone scaling, directly set `localScale` at build time.
- **Play Mode triggers the build pipeline.** All NDMF passes, VRCFury, and MA run on Play Mode entry -- use this for testing without uploading.
- Passes access the avatar clone (not the scene original). Modifications are nondestructive by design.
- Always `Object.DestroyImmediate(component)` your processed components at the end of your pass.
