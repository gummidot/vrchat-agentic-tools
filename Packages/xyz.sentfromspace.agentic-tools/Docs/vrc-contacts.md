# VRC Contact Reference

VRChat avatar contact senders and receivers (`VRCContactSender`, `VRCContactReceiver`). Covers shape limits, proximity semantics, multi-sender behavior, and the local-by-default sync model.

**Discover SDK version first.** Contact features and shape options change across SDK versions. Check `Packages/com.vrchat.base/package.json` for the installed version and `VRC.Dynamics.ContactBase` (and `ContactReceiver` / `ContactSender`) via reflection for the currently available shape types and fields rather than assuming.

## Shape Types and Limits

- **Sphere** (default): radius cap depends on SDK version (sphere contacts historically capped at 3m radius).
- **Capsule**: height + radius.
- **Box** (SDK 3.10.4+): width / height / depth, capped at 6m per axis. Caps apply *after* GameObject scale, so scaling the host transform cannot widen the effective volume.

Inspect `VRCContactReceiver.shapeType` (enum) at runtime to see which shapes the installed SDK exposes.

## Proximity Semantics

`ReceiverType = Proximity` returns a float `0.0 - 1.0` written to the animator parameter named in `parameter`.

- **Sphere / capsule / default box:** `1.0 = sender at center`, `0.0 = sender at edge of volume`.
- **Box with `Use Face Proximity` checked:** `1.0 = sender touching the +Z face` of the box (the face whose normal is the box's local +Z), `0.0 = sender at the opposite face or outside the volume`. Value is linear in distance to the +Z face along the box's local Z axis.
- Out-of-volume senders do not contribute (no value written). The parameter retains its last value, or 0 if nothing has ever been in range -- verify empirically for your case.

`AllowSelf` enables the receiver to detect senders on the same avatar. `AllowOthers` enables remote-avatar senders.

## Multi-Sender Behavior

If multiple in-range senders match the receiver's tag list, the receiver reports **only the closest** sender's proximity. There is no aggregation. To track multiple distinct senders with one receiver design, give each sender a unique tag and use one receiver per tag.

Tag limit: 16 tags per contact component.

## Local-by-Default Sync Model

**Receivers do not auto-sync.** Each client computes proximity locally from its own view of the scene. A receiver writing to animator parameter `X` produces `X` on every client independently; the values can differ if the geometry differs across clients (e.g. late joiner missing transient state, network position lag).

To make the **owner's** locally-computed value authoritative across remote clients:

1. Add the parameter to the avatar's Expression Parameters list with **Synced** checked.
2. The owner's client writes the value via contact computation. VRChat syncs it to remotes (including snapshot on join) like any other synced expression parameter.
3. **Set `localOnly = true` on every receiver that writes a synced parameter.** Otherwise the remote's local contact computation also runs and overwrites the synced value, frame by frame.

### `localOnly` Is Required for Owner-Authoritative Sync

By default `localOnly = false`, which means the receiver runs on every client (owner + remotes). For a synced parameter, the remote will then locally re-compute from its own view of the sender and write the result every frame. This fights with the owner's synced value rather than yielding to it. Two common failure modes:

- **Stale geometry on remotes**: a remote's view of a sender position can lag behind reality (e.g. armature interpolation), so the remote-computed value differs from the owner-computed value and the param visibly jitters or drifts.
- **Late-joiner with `VRCParentConstraint.FreezeToWorld`-based encoding**: when the source transform's true world position cannot be reconstructed on the remote (because `FreezeToWorld` captured at the wrong moment on the remote's spawn), the remote re-computes a value reflecting the wrong source position. The owner's correct synced value is silently overwritten each frame by the wrong local value. Symptom: the decoded position snaps onto the late-joiner's view of the avatar body instead of the encoded world position.

With `localOnly = true`, the receiver only runs on the avatar owner's client. The owner writes the synced parameter; remotes receive it via sync and never overwrite it. This is the correct setting for any contact loop that produces an owner-authoritative value.

If you do NOT add the parameter to synced Expression Parameters, the receiver still works but each client sees only their own local computation. This is correct for purely-local feedback (e.g. petting visual on the toucher's screen) and wrong for owner-authoritative state (e.g. a synced world-anchored position).

The beta docs note "this parameter DOES NOT need to be defined on the synced Avatar Parameter list" -- this is generic guidance for the local-feedback case, not a statement that contact values are auto-synced or that local writes yield to synced values.

## Performance

- Box and other non-sphere shapes count the same as sphere contacts in the avatar performance rank table.
- `Local Only` enabled on a contact prevents it from counting toward perf rank.
- Check `VRCContactReceiver.collisionTags.Count` and the global count via `FindObjectsOfType<ContactBase>()` when budgeting.

## Beta-Docs Routing

While features are in open beta, the canonical reference is the VRChat beta docs site for the current beta cycle (search for "VRChat beta docs contacts" rather than hardcoding a URL -- the host changes each beta cycle). For released features, the stable VRChat creator docs are authoritative.
