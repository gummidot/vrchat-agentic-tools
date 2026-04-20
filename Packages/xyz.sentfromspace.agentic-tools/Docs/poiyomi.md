# Poiyomi Toon Shader

Discover Poiyomi shaders from the material's `shader.name`, or search via `AssetDatabase.FindAssets("t:Shader").Select(g => AssetDatabase.GUIDToAssetPath(g))` filtering for names containing "poiyomi".

**Discovery-first:** Never hardcode Poiyomi property names, values, or keywords. Always dump the material to discover what's active, then dump specific module properties to find exact names.

## Dump-First Workflow

**Never guess Poiyomi property names.** Poiyomi has many properties with inconsistent slot numbering. Use a **two-phase approach** — discover active modules first, then dump only those.

**Locked shader name resolution:** If `shader.name` starts with `"Hidden/Locked/"`, strip that prefix and remove the trailing segment after the last `/` to get the unlocked shader name. Use `Shader.Find()` with the unlocked name to access the full property list.

**Phase 1 — Module Discovery:** Iterate shader properties. Properties named `m_start_*` are section markers. Parse `reference_property:<propName>` from `shader.GetPropertyDescription(i)` (substring after `"reference_property:"` up to the next `,`, `}`, or space). If that property exists on the material and is non-zero (`mat.GetFloat(prop) != 0`), the module is enabled. The section name is the `m_start_` suffix. Also check `mat.shaderKeywords` for active keywords.

**Phase 2 — Targeted Property Dump:** `m_start_<name>` / `m_end_<name>` pairs define section ranges. Use a stack to handle nesting — push on `m_start_`, pop on `m_end_`. For properties within a section range, compare values to shader defaults (`shader.GetPropertyDefaultFloatValue(i)`, `shader.GetPropertyDefaultVectorValue(i)`) to find non-defaults. Handle all property types: Float/Range, Color, Texture, Vector.

**Prefix Dump:** For features without section markers (e.g., outlines with `_EnableOutlines`), filter by property name prefix (e.g., `_Outline`). Iterate all shader properties and dump those matching the prefix.

## Source Reading Strategy

The shader is a large monolithic file. Never read it whole — use targeted searches.

**Locate shader source:** Resolve the locked name (see above), then `FindAssets("t:Shader")` and match by `.name`.

**Property semantics:** Search the shader's Properties block for the property name. Thry Editor metadata reveals ranges (`Range(min, max)`), enum options (`ThryWideEnum`), dependencies (`reference_property:`), and visibility conditions (`condition_showS:`).

**Module implementation:** Search for `//ifex _Enable<Feature>==0` in the `.shader` file to find feature code blocks. For older versions, search for `CGI_Poi<Feature>.cginc` include files.

**Discover paths dynamically** — Poiyomi's install location and version vary per project. Use `AssetDatabase.FindAssets` or `find` to locate shader sources, include files, and `PoiLabels.txt`.

## Animated Properties (Locking)

Mark properties animated at runtime with `material.SetOverrideTag("{PropertyName}Animated", "1")`.
- `"1"` = Animated (stays as shader parameter when locked)
- `"2"` = Renamed (becomes `{PropertyName}_{MaterialName}`)

Leave materials unlocked (`_ShaderOptimizerEnabled` = 0) — Poiyomi re-locks on upload.

## Locking Lifecycle

- **Play mode / VRCFury builds trigger Poiyomi re-locking.** The locked shader compiles out code paths for disabled modules. Always verify the material is unlocked (`_ShaderOptimizerEnabled = 0`) before editing, and re-check after play mode.
- **Programmatic unlock:** Use `ShaderOptimizer.UnlockMaterials(new[]{mat})` (class may be `Thry.ThryEditor.ShaderOptimizer` or `Thry.ShaderOptimizer` depending on version — resolve via reflection if needed). Check first with `ShaderOptimizer.IsMaterialLocked(mat)`.
- **After edits, leave unlocked** — Poiyomi re-locks automatically on upload and play mode. Do not manually re-lock.
- **VRCFury only locks, never unlocks** — `MaterialLocker.Lock()` pre-locks materials during VRCFury build. If VRCFury is present and the material needs editing, unlock it before VRCFury build runs.
- **After enabling a new module:** run Phase 1 Module Discovery to verify the enable property is set and the keyword is active. Don't assume — dump and confirm.
