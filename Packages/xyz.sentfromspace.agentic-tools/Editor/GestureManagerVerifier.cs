using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace SentFromSpace.AgenticTools.Editor
{
    /// <summary>
    /// Runtime toggle verification using Gesture Manager's emulated playable layer stack.
    /// Programmatically sets each toggle parameter and diffs the avatar's visual state
    /// to confirm toggles actually produce visible changes.
    /// </summary>
    public static class GestureManagerVerifier
    {
        const string GmPrefabPath = "Packages/vrchat.blackstartx.gesture-manager/GestureManager.prefab";

        // VRC built-in parameters to exclude from ListParameters output
        static readonly HashSet<string> VrcBuiltInParams = new HashSet<string>
        {
            "IsLocal", "Viseme", "Voice", "GestureLeft", "GestureRight",
            "GestureLeftWeight", "GestureRightWeight",
            "AngularY", "VelocityX", "VelocityY", "VelocityZ", "VelocityMagnitude",
            "Upright", "Grounded", "Seated", "AFK", "TrackingType",
            "VRMode", "MuteSelf", "InStation", "Eyelids", "EyeMovement",
            "ScaleModified", "ScaleFactor", "ScaleFactorInverse",
            "EyeHeightAsMeters", "EyeHeightAsPercent",
            "IsOnFriendsList", "AvatarVersion", "Supine",
            "IsAnimatorEnabled", "PreviewMode", "VRCEmote", "Earmuffs",
        };

        #region Avatar Snapshot

        struct AvatarSnapshot
        {
            public Dictionary<string, bool> ObjectActive;           // path -> activeSelf
            public Dictionary<string, Dictionary<string, float>> BlendShapes; // renderer path -> (shape -> weight)
            public Dictionary<string, int[]> MaterialIds;           // renderer path -> material instanceIDs
            public Dictionary<string, Dictionary<string, float>> MaterialFloats; // renderer path -> (propName -> value)
            public Dictionary<string, Dictionary<string, Color>> MaterialColors; // renderer path -> (propName -> color)
        }

        static AvatarSnapshot CaptureSnapshot(Transform avatarRoot)
        {
            var snap = new AvatarSnapshot
            {
                ObjectActive = new Dictionary<string, bool>(),
                BlendShapes = new Dictionary<string, Dictionary<string, float>>(),
                MaterialIds = new Dictionary<string, int[]>(),
                MaterialFloats = new Dictionary<string, Dictionary<string, float>>(),
                MaterialColors = new Dictionary<string, Dictionary<string, Color>>(),
            };

            CaptureRecursive(avatarRoot, avatarRoot, snap);
            return snap;
        }

        static void CaptureRecursive(Transform root, Transform current, AvatarSnapshot snap)
        {
            string path = GetRelativePath(root, current);

            snap.ObjectActive[path] = current.gameObject.activeSelf;

            var smr = current.GetComponent<SkinnedMeshRenderer>();
            if (smr != null && smr.sharedMesh != null)
            {
                var shapes = new Dictionary<string, float>();
                for (int i = 0; i < smr.sharedMesh.blendShapeCount; i++)
                {
                    string name = smr.sharedMesh.GetBlendShapeName(i);
                    shapes[name] = smr.GetBlendShapeWeight(i);
                }
                if (shapes.Count > 0)
                    snap.BlendShapes[path] = shapes;

                CaptureRendererMaterials(path, smr, snap);
            }

            var mr = current.GetComponent<MeshRenderer>();
            if (mr != null)
                CaptureRendererMaterials(path, mr, snap);

            for (int i = 0; i < current.childCount; i++)
                CaptureRecursive(root, current.GetChild(i), snap);
        }

        static void CaptureRendererMaterials(string path, Renderer renderer, AvatarSnapshot snap)
        {
            var mats = renderer.sharedMaterials;
            if (mats == null || mats.Length == 0) return;

            snap.MaterialIds[path] = mats.Select(m => m != null ? m.GetInstanceID() : 0).ToArray();

            var floats = new Dictionary<string, float>();
            var colors = new Dictionary<string, Color>();

            for (int mi = 0; mi < mats.Length; mi++)
            {
                var mat = mats[mi];
                if (mat == null || mat.shader == null) continue;

                string prefix = mats.Length > 1 ? $"[mat {mi}] " : "";
                var shader = mat.shader;
                int propCount = shader.GetPropertyCount();

                for (int pi = 0; pi < propCount; pi++)
                {
                    var propType = shader.GetPropertyType(pi);
                    string propName = shader.GetPropertyName(pi);
                    string key = prefix + propName;

                    if (propType == UnityEngine.Rendering.ShaderPropertyType.Float ||
                        propType == UnityEngine.Rendering.ShaderPropertyType.Range)
                    {
                        floats[key] = mat.GetFloat(propName);
                    }
                    else if (propType == UnityEngine.Rendering.ShaderPropertyType.Color)
                    {
                        colors[key] = mat.GetColor(propName);
                    }
                }
            }

            if (floats.Count > 0) snap.MaterialFloats[path] = floats;
            if (colors.Count > 0) snap.MaterialColors[path] = colors;
        }

        static string GetRelativePath(Transform root, Transform target)
        {
            if (target == root) return "";
            var parts = new List<string>();
            var t = target;
            while (t != null && t != root)
            {
                parts.Add(t.name);
                t = t.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        #endregion

        #region Snapshot Diff

        struct SnapshotDiff
        {
            public List<string> Changes;
        }

        static SnapshotDiff DiffSnapshots(AvatarSnapshot before, AvatarSnapshot after)
        {
            var diff = new SnapshotDiff { Changes = new List<string>() };

            // Object active changes
            foreach (var kvp in before.ObjectActive)
            {
                bool afterVal;
                if (after.ObjectActive.TryGetValue(kvp.Key, out afterVal) && kvp.Value != afterVal)
                {
                    string displayPath = string.IsNullOrEmpty(kvp.Key) ? "(root)" : kvp.Key;
                    diff.Changes.Add($"  - {displayPath}: activeSelf {kvp.Value.ToString().ToLower()} -> {afterVal.ToString().ToLower()}");
                }
            }

            // Blendshape changes
            foreach (var kvp in before.BlendShapes)
            {
                Dictionary<string, float> afterShapes;
                if (!after.BlendShapes.TryGetValue(kvp.Key, out afterShapes)) continue;

                foreach (var shape in kvp.Value)
                {
                    float afterWeight;
                    if (afterShapes.TryGetValue(shape.Key, out afterWeight) &&
                        Math.Abs(shape.Value - afterWeight) > 0.01f)
                    {
                        diff.Changes.Add($"  - {kvp.Key}: blendShape.{shape.Key} {shape.Value:F1} -> {afterWeight:F1}");
                    }
                }
            }

            // Material swap changes — track which renderer+slot pairs were swapped
            // so we can skip noisy property diffs for those slots
            var swappedSlots = new HashSet<string>(); // "rendererPath|slotIndex"

            foreach (var kvp in before.MaterialIds)
            {
                int[] afterIds;
                if (!after.MaterialIds.TryGetValue(kvp.Key, out afterIds)) continue;

                int len = Math.Min(kvp.Value.Length, afterIds.Length);
                for (int i = 0; i < len; i++)
                {
                    if (kvp.Value[i] != afterIds[i] && kvp.Value[i] != 0 && afterIds[i] != 0)
                    {
                        diff.Changes.Add($"  - {kvp.Key} [mat {i}]: material swapped (ID {kvp.Value[i]} -> {afterIds[i]})");
                        swappedSlots.Add($"{kvp.Key}|{i}");
                    }
                }
            }

            // Material float property changes — skip properties on swapped material slots
            foreach (var kvp in before.MaterialFloats)
            {
                Dictionary<string, float> afterFloats;
                if (!after.MaterialFloats.TryGetValue(kvp.Key, out afterFloats)) continue;

                foreach (var prop in kvp.Value)
                {
                    float afterVal;
                    if (afterFloats.TryGetValue(prop.Key, out afterVal) &&
                        Math.Abs(prop.Value - afterVal) > 0.01f)
                    {
                        // Check if this property belongs to a swapped material slot
                        if (IsPropertyOnSwappedSlot(kvp.Key, prop.Key, swappedSlots))
                            continue;

                        diff.Changes.Add($"  - {kvp.Key}: {prop.Key} {prop.Value:G4} -> {afterVal:G4}");
                    }
                }
            }

            // Material color property changes — skip properties on swapped material slots
            foreach (var kvp in before.MaterialColors)
            {
                Dictionary<string, Color> afterColors;
                if (!after.MaterialColors.TryGetValue(kvp.Key, out afterColors)) continue;

                foreach (var prop in kvp.Value)
                {
                    Color afterVal;
                    if (afterColors.TryGetValue(prop.Key, out afterVal) && prop.Value != afterVal)
                    {
                        if (IsPropertyOnSwappedSlot(kvp.Key, prop.Key, swappedSlots))
                            continue;

                        diff.Changes.Add($"  - {kvp.Key}: {prop.Key} ({prop.Value.r:F2},{prop.Value.g:F2},{prop.Value.b:F2},{prop.Value.a:F2}) -> ({afterVal.r:F2},{afterVal.g:F2},{afterVal.b:F2},{afterVal.a:F2})");
                    }
                }
            }

            return diff;
        }

        /// <summary>
        /// Checks if a material property key belongs to a swapped material slot.
        /// Property keys are either "propName" (single-mat renderer) or "[mat N] propName" (multi-mat).
        /// </summary>
        static bool IsPropertyOnSwappedSlot(string rendererPath, string propertyKey, HashSet<string> swappedSlots)
        {
            // Multi-material: key starts with "[mat N] "
            if (propertyKey.StartsWith("[mat "))
            {
                int closeBracket = propertyKey.IndexOf(']');
                if (closeBracket > 5)
                {
                    string slotStr = propertyKey.Substring(5, closeBracket - 5);
                    return swappedSlots.Contains($"{rendererPath}|{slotStr}");
                }
            }

            // Single-material renderer: slot is always 0
            return swappedSlots.Contains($"{rendererPath}|0");
        }

        #endregion

        #region Gesture Manager Reflection

        static class GMReflection
        {
            // Cached types and members
            static bool _initialized;
            static bool _available;
            static string _initError;

            static Type _gestureManagerType;    // BlackStartX.GestureManager.GestureManager (Runtime)
            static Type _moduleBaseType;        // BlackStartX.GestureManager.Data.ModuleBase (Runtime)
            static Type _moduleVrc3Type;        // BlackStartX.GestureManager.Editor.Modules.Vrc3.ModuleVrc3 (Editor)
            static Type _vrc3ParamType;         // BlackStartX.GestureManager.Editor.Modules.Vrc3.Params.Vrc3Param (Editor)
            static Type _moduleHelperType;      // BlackStartX.GestureManager.Editor.Modules.ModuleHelper (Editor)

            // GestureManager members
            static FieldInfo _gmModuleField;    // Module (public ModuleBase)
            static FieldInfo _gmSettingsField;  // settings (public ModuleSettings)
            static MethodInfo _gmSetModuleMethod; // SetModule(ModuleBase)

            // ModuleBase members
            static FieldInfo _mbAvatarField;    // Avatar (public readonly GameObject)
            static FieldInfo _mbAnimatorField;  // AvatarAnimator (public readonly Animator)

            // ModuleVrc3 members
            static FieldInfo _mv3ParamsField;   // Params (public Dictionary<string, Vrc3Param>)
            static MethodInfo _mv3GetParamMethod; // GetParam(string)
            static FieldInfo _mv3DescriptorField; // AvatarDescriptor
            static FieldInfo _mv3PlayableGraphField; // _playableGraph (private PlayableGraph)

            // Vrc3Param members
            static MethodInfo _paramSetMethod;   // Set(ModuleVrc3 module, float value, object source)
            static MethodInfo _paramFloatValue;  // FloatValue()
            static FieldInfo _paramNameField;    // Name (public readonly string)
            static FieldInfo _paramTypeField;    // Type (public readonly AnimatorControllerParameterType)

            // ModuleHelper members
            static MethodInfo _helperGetModuleFor; // GetModuleFor(VRC_AvatarDescriptor)

            public static bool TryInit(out string error)
            {
                if (_initialized)
                {
                    error = _initError;
                    return _available;
                }
                _initialized = true;

                try
                {
                    var assemblies = AppDomain.CurrentDomain.GetAssemblies();

                    // Find GM runtime assembly
                    var runtimeAsm = assemblies.FirstOrDefault(a =>
                        a.GetName().Name == "vrchat.blackstartx.gesture-manager" ||
                        a.GetName().Name == "BlackStartX.GestureManager");

                    if (runtimeAsm == null)
                    {
                        // Try broader search
                        runtimeAsm = assemblies.FirstOrDefault(a =>
                            a.GetName().Name.Contains("gesture-manager") &&
                            !a.GetName().Name.Contains("editor") &&
                            !a.GetName().Name.Contains("Editor"));
                    }

                    if (runtimeAsm == null)
                    {
                        _initError = "Gesture Manager runtime assembly not found. Is the package installed?";
                        error = _initError;
                        return false;
                    }

                    // Find GM editor assembly
                    var editorAsm = assemblies.FirstOrDefault(a =>
                        a.GetName().Name == "vrchat.blackstartx.gesture-manager.editor" ||
                        a.GetName().Name == "BlackStartX.GestureManager.Editor");

                    if (editorAsm == null)
                    {
                        editorAsm = assemblies.FirstOrDefault(a =>
                            a.GetName().Name.Contains("gesture-manager") &&
                            (a.GetName().Name.Contains("editor") || a.GetName().Name.Contains("Editor")));
                    }

                    if (editorAsm == null)
                    {
                        _initError = "Gesture Manager editor assembly not found.";
                        error = _initError;
                        return false;
                    }

                    // Resolve types
                    _gestureManagerType = runtimeAsm.GetType("BlackStartX.GestureManager.GestureManager");
                    _moduleBaseType = runtimeAsm.GetType("BlackStartX.GestureManager.Data.ModuleBase");
                    _moduleVrc3Type = editorAsm.GetType("BlackStartX.GestureManager.Editor.Modules.Vrc3.ModuleVrc3");
                    _vrc3ParamType = editorAsm.GetType("BlackStartX.GestureManager.Editor.Modules.Vrc3.Params.Vrc3Param");
                    _moduleHelperType = editorAsm.GetType("BlackStartX.GestureManager.Editor.Modules.ModuleHelper");

                    if (_gestureManagerType == null || _moduleBaseType == null ||
                        _moduleVrc3Type == null || _vrc3ParamType == null || _moduleHelperType == null)
                    {
                        var missing = new List<string>();
                        if (_gestureManagerType == null) missing.Add("GestureManager");
                        if (_moduleBaseType == null) missing.Add("ModuleBase");
                        if (_moduleVrc3Type == null) missing.Add("ModuleVrc3");
                        if (_vrc3ParamType == null) missing.Add("Vrc3Param");
                        if (_moduleHelperType == null) missing.Add("ModuleHelper");
                        _initError = $"Missing GM types: {string.Join(", ", missing)}";
                        error = _initError;
                        return false;
                    }

                    // Resolve members
                    var bf = BindingFlags.Public | BindingFlags.Instance;

                    _gmModuleField = _gestureManagerType.GetField("Module", bf);
                    _gmSettingsField = _gestureManagerType.GetField("settings", bf);
                    _gmSetModuleMethod = _gestureManagerType.GetMethod("SetModule", bf, null, new[] { _moduleBaseType }, null);

                    _mbAvatarField = _moduleBaseType.GetField("Avatar", bf);
                    _mbAnimatorField = _moduleBaseType.GetField("AvatarAnimator", bf);

                    _mv3ParamsField = _moduleVrc3Type.GetField("Params", bf);
                    _mv3GetParamMethod = _moduleVrc3Type.GetMethod("GetParam", bf, null, new[] { typeof(string) }, null);
                    _mv3DescriptorField = _moduleVrc3Type.GetField("AvatarDescriptor", bf);
                    _mv3PlayableGraphField = _moduleVrc3Type.GetField("_playableGraph",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                    _paramSetMethod = _vrc3ParamType.GetMethod("Set", bf, null,
                        new[] { _moduleVrc3Type, typeof(float), typeof(object) }, null);
                    _paramFloatValue = _vrc3ParamType.GetMethod("FloatValue", bf, null, Type.EmptyTypes, null);
                    _paramNameField = _vrc3ParamType.GetField("Name", bf);
                    _paramTypeField = _vrc3ParamType.GetField("Type", bf);

                    _helperGetModuleFor = _moduleHelperType.GetMethod("GetModuleFor",
                        BindingFlags.Public | BindingFlags.Static, null,
                        new[] { typeof(VRC.SDKBase.VRC_AvatarDescriptor) }, null);

                    // Validate critical members
                    var missingMembers = new List<string>();
                    if (_gmModuleField == null) missingMembers.Add("GestureManager.Module");
                    if (_gmSetModuleMethod == null) missingMembers.Add("GestureManager.SetModule");
                    if (_mv3ParamsField == null) missingMembers.Add("ModuleVrc3.Params");
                    if (_paramSetMethod == null) missingMembers.Add("Vrc3Param.Set");
                    if (_paramFloatValue == null) missingMembers.Add("Vrc3Param.FloatValue");
                    if (_helperGetModuleFor == null) missingMembers.Add("ModuleHelper.GetModuleFor");

                    if (missingMembers.Count > 0)
                    {
                        _initError = $"Missing GM members: {string.Join(", ", missingMembers)}";
                        error = _initError;
                        return false;
                    }

                    _available = true;
                    error = null;
                    return true;
                }
                catch (Exception ex)
                {
                    _initError = $"GM reflection init failed: {ex.Message}";
                    error = _initError;
                    return false;
                }
            }

            public static UnityEngine.Object FindGestureManager()
            {
                return UnityEngine.Object.FindObjectOfType(_gestureManagerType) as UnityEngine.Object;
            }

            public static Component[] FindAllGestureManagers()
            {
                return UnityEngine.Object.FindObjectsOfType(_gestureManagerType)
                    .Cast<Component>().ToArray();
            }

            public static object GetModule(Component gm)
            {
                return _gmModuleField.GetValue(gm);
            }

            public static void SetModule(Component gm, object module)
            {
                _gmSetModuleMethod.Invoke(gm, new[] { module });
            }

            public static object GetSettings(Component gm)
            {
                return _gmSettingsField.GetValue(gm);
            }

            public static GameObject GetAvatar(object module)
            {
                return _mbAvatarField.GetValue(module) as GameObject;
            }

            public static Animator GetAvatarAnimator(object module)
            {
                return _mbAnimatorField.GetValue(module) as Animator;
            }

            public static VRCAvatarDescriptor GetAvatarDescriptor(object moduleVrc3)
            {
                return _mv3DescriptorField.GetValue(moduleVrc3) as VRCAvatarDescriptor;
            }

            public static object CreateModuleForDescriptor(VRCAvatarDescriptor descriptor)
            {
                return _helperGetModuleFor.Invoke(null, new object[] { descriptor });
            }

            public static Dictionary<string, object> GetParams(object moduleVrc3)
            {
                var raw = _mv3ParamsField.GetValue(moduleVrc3);
                if (raw == null) return new Dictionary<string, object>();

                // Params is Dictionary<string, Vrc3Param> — convert to Dictionary<string, object>
                var result = new Dictionary<string, object>();
                var dict = raw as System.Collections.IDictionary;
                if (dict != null)
                {
                    foreach (System.Collections.DictionaryEntry entry in dict)
                        result[(string)entry.Key] = entry.Value;
                }
                return result;
            }

            public static object GetParam(object moduleVrc3, string name)
            {
                return _mv3GetParamMethod.Invoke(moduleVrc3, new object[] { name });
            }

            public static float GetParamFloatValue(object param)
            {
                return (float)_paramFloatValue.Invoke(param, null);
            }

            public static void SetParamValue(object moduleVrc3, object param, float value)
            {
                _paramSetMethod.Invoke(param, new[] { moduleVrc3, (object)value, null });
            }

            public static string GetParamName(object param)
            {
                return (string)_paramNameField.GetValue(param);
            }

            public static AnimatorControllerParameterType GetParamType(object param)
            {
                return (AnimatorControllerParameterType)_paramTypeField.GetValue(param);
            }

            public static bool IsModuleVrc3(object module)
            {
                return module != null && _moduleVrc3Type.IsInstanceOfType(module);
            }

            /// <summary>
            /// Evaluates GM's PlayableGraph to apply parameter changes to the avatar.
            /// GM uses its own PlayableGraph (not the Animator's), so animator.Update() won't work.
            /// </summary>
            public static void EvaluateGraph(object moduleVrc3, float deltaTime = 0.016f)
            {
                if (_mv3PlayableGraphField == null) return;
                var graph = (UnityEngine.Playables.PlayableGraph)_mv3PlayableGraphField.GetValue(moduleVrc3);
                if (graph.IsValid() && graph.IsPlaying())
                    graph.Evaluate(deltaTime);
            }

            /// <summary>
            /// Instantiates the GM prefab, finds the target descriptor, creates a ModuleVrc3, and connects it.
            /// </summary>
            public static Component AutoInstantiateGM(VRCAvatarDescriptor descriptor)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(GmPrefabPath);
                if (prefab == null)
                    throw new InvalidOperationException($"GM prefab not found at {GmPrefabPath}");

                var gmObj = UnityEngine.Object.Instantiate(prefab);
                gmObj.name = "GestureManager (Auto)";
                var gm = gmObj.GetComponent(_gestureManagerType) as Component;
                if (gm == null)
                    throw new InvalidOperationException("Instantiated GM prefab has no GestureManager component");

                var module = CreateModuleForDescriptor(descriptor);
                if (module == null)
                    throw new InvalidOperationException("ModuleHelper.GetModuleFor returned null");

                SetModule(gm, module);
                return gm;
            }
        }

        #endregion

        #region Menu Walking

        struct MenuControl
        {
            public string Name;
            public string MenuPath;      // "Root/Outfits/Shirt"
            public string ParameterName;
            public float Value;
            public VRCExpressionsMenu.Control.ControlType Type;
        }

        static List<MenuControl> CollectMenuControls(VRCExpressionsMenu rootMenu)
        {
            var controls = new List<MenuControl>();
            if (rootMenu == null) return controls;

            var visited = new HashSet<VRCExpressionsMenu>();
            WalkMenu(rootMenu, "Root", controls, visited);
            return controls;
        }

        static void WalkMenu(VRCExpressionsMenu menu, string path, List<MenuControl> controls,
            HashSet<VRCExpressionsMenu> visited)
        {
            if (menu == null || visited.Contains(menu)) return;
            visited.Add(menu);

            if (menu.controls == null) return;

            foreach (var ctrl in menu.controls)
            {
                string controlPath = $"{path}/{ctrl.name}";

                controls.Add(new MenuControl
                {
                    Name = ctrl.name,
                    MenuPath = controlPath,
                    ParameterName = ctrl.parameter != null ? ctrl.parameter.name : null,
                    Value = ctrl.value,
                    Type = ctrl.type,
                });

                if (ctrl.type == VRCExpressionsMenu.Control.ControlType.SubMenu && ctrl.subMenu != null)
                    WalkMenu(ctrl.subMenu, controlPath, controls, visited);
            }
        }

        #endregion

        #region Public API

        /// <summary>
        /// Verifies specific toggles in the avatar's expression menu by flipping each parameter
        /// and diffing the avatar's visual state. Accepts authoring-time parameter names —
        /// VRCFury VF###_ prefixed renames are matched automatically.
        /// </summary>
        public static string VerifyToggles(string avatarHierarchyPath, params string[] parameterNames)
        {
            if (string.IsNullOrEmpty(avatarHierarchyPath))
                return "[ERROR] avatarHierarchyPath is required. Pass the full hierarchy path to the avatar (e.g., \"Avatar\" or \"Root/Avatar\").";

            if (parameterNames == null || parameterNames.Length == 0)
                return "[ERROR] Specify which toggles to test by parameter name. Example: VerifyToggles(\"Avatar\", \"Clothing/Top/Bikini\", \"Clothing/Acc/Glasses\")";

            if (!Application.isPlaying)
                return "[ERROR] Must be in Play Mode. Enter Play Mode first to trigger the VRCFury/NDMF build pipeline.";

            string initError;
            if (!GMReflection.TryInit(out initError))
                return $"[ERROR] {initError}";

            // Find the avatar descriptor by path
            VRCAvatarDescriptor descriptor;
            Transform avatarRoot;
            string findError = FindDescriptorByPath(avatarHierarchyPath, out descriptor, out avatarRoot);
            if (findError != null)
                return findError;

            // Find or create Gesture Manager
            Component gm;
            object module;
            string gmError = FindOrCreateGM(descriptor, out gm, out module);
            if (gmError != null)
                return gmError;

            // Read expression menu from descriptor
            var expressionMenu = descriptor.expressionsMenu;
            if (expressionMenu == null)
                return "[ERROR] No expression menu found on the avatar descriptor.";

            var allControls = CollectMenuControls(expressionMenu);
            if (allControls.Count == 0)
                return "[WARN] Expression menu has no controls.";

            // Filter to Toggle-type controls only
            var toggleControls = allControls.Where(c => c.Type == VRCExpressionsMenu.Control.ControlType.Toggle).ToList();

            // Match requested parameter names to toggle controls (with VRCFury fuzzy matching)
            var controlsToTest = new List<MenuControl>();
            var unmatchedParams = new List<string>();

            foreach (var requestedName in parameterNames)
            {
                var matches = toggleControls.Where(c =>
                    !string.IsNullOrEmpty(c.ParameterName) &&
                    MatchesParameterName(c.ParameterName, requestedName)).ToList();

                if (matches.Count > 0)
                    controlsToTest.AddRange(matches);
                else
                    unmatchedParams.Add(requestedName);
            }

            // Settle the graph before testing — after GM initialization, all layers need
            // time to reach steady state (transitions fire, parameter drivers chain, etc.)
            SettleGraph(module);

            var sb = new StringBuilder();
            sb.AppendLine("=== Toggle Verification (Gesture Manager) ===");
            sb.AppendLine();

            int passCount = 0, failCount = 0, warnCount = 0;

            // Report unmatched parameters
            foreach (var name in unmatchedParams)
            {
                sb.AppendLine($"[WARN] \"{name}\" not found in expression menu (checked {toggleControls.Count} toggle controls)");
                warnCount++;
            }

            foreach (var ctrl in controlsToTest)
            {
                if (string.IsNullOrEmpty(ctrl.ParameterName))
                {
                    sb.AppendLine($"[WARN] \"{ctrl.Name}\" [{ctrl.MenuPath}] -- no parameter name");
                    warnCount++;
                    continue;
                }

                var param = GMReflection.GetParam(module, ctrl.ParameterName);
                if (param == null)
                {
                    sb.AppendLine($"[WARN] \"{ctrl.Name}\" [{ctrl.MenuPath}] (param: {ctrl.ParameterName}) -- parameter not found in Gesture Manager");
                    warnCount++;
                    continue;
                }

                float currentValue = GMReflection.GetParamFloatValue(param);
                float activeValue = ctrl.Value != 0 ? ctrl.Value : 1f;

                // Determine test direction: if currently at active value, flip to 0; otherwise flip to active
                float targetValue, restoreValue;
                bool isCurrentlyActive = Math.Abs(currentValue - activeValue) < 0.001f;
                if (isCurrentlyActive)
                {
                    targetValue = 0f;
                    restoreValue = activeValue;
                }
                else
                {
                    targetValue = activeValue;
                    restoreValue = currentValue;
                }

                // Take BEFORE snapshot (graph already settled)
                var before = CaptureSnapshot(avatarRoot);

                // Set parameter and settle — parameter drivers can chain,
                // so we need multiple evaluations for the full effect to propagate
                GMReflection.SetParamValue(module, param, targetValue);
                SettleGraph(module);

                // Take AFTER snapshot
                var after = CaptureSnapshot(avatarRoot);

                // Restore original value and settle for next toggle
                GMReflection.SetParamValue(module, param, restoreValue);
                SettleGraph(module);

                // Diff
                var diff = DiffSnapshots(before, after);

                string paramDisplay = $"param: {ctrl.ParameterName}={targetValue:G}";
                if (diff.Changes.Count > 0)
                {
                    string changeType = SummarizeChangeTypes(diff);
                    sb.AppendLine($"[PASS] \"{ctrl.Name}\" [{ctrl.MenuPath}] ({paramDisplay}) -- {diff.Changes.Count} {changeType}");
                    foreach (var change in diff.Changes)
                        sb.AppendLine(change);
                    passCount++;
                }
                else
                {
                    sb.AppendLine($"[FAIL] \"{ctrl.Name}\" [{ctrl.MenuPath}] ({paramDisplay}) -- no changes detected");
                    failCount++;
                }
                sb.AppendLine();
            }

            sb.AppendLine($"Summary: {parameterNames.Length} requested, {controlsToTest.Count} matched, {passCount} passed, {failCount} failed, {warnCount} warnings");
            return sb.ToString();
        }

        /// <summary>
        /// Lists all user parameters (excluding VRC built-ins) with name, type, and current value.
        /// </summary>
        public static string ListParameters(string avatarHierarchyPath = null)
        {
            if (!Application.isPlaying)
                return "[ERROR] Must be in Play Mode.";

            string initError;
            if (!GMReflection.TryInit(out initError))
                return $"[ERROR] {initError}";

            Component gm;
            object module;

            if (!string.IsNullOrEmpty(avatarHierarchyPath))
            {
                VRCAvatarDescriptor descriptor;
                Transform avatarRoot;
                string findError = FindDescriptorByPath(avatarHierarchyPath, out descriptor, out avatarRoot);
                if (findError != null)
                    return findError;

                string gmError = FindOrCreateGM(descriptor, out gm, out module);
                if (gmError != null)
                    return gmError;
            }
            else
            {
                string gmError = FindExistingGM(out gm, out module);
                if (gmError != null)
                    return gmError;
            }

            var allParams = GMReflection.GetParams(module);
            if (allParams.Count == 0)
                return "[WARN] No parameters found in Gesture Manager module.";

            var sb = new StringBuilder();
            sb.AppendLine("=== Gesture Manager Parameters ===");
            sb.AppendLine();

            var userParams = allParams
                .Where(kvp => !VrcBuiltInParams.Contains(kvp.Key))
                .OrderBy(kvp => kvp.Key)
                .ToList();

            sb.AppendLine($"Total parameters: {allParams.Count} ({userParams.Count} user, {allParams.Count - userParams.Count} VRC built-in)");
            sb.AppendLine();

            foreach (var kvp in userParams)
            {
                var paramType = GMReflection.GetParamType(kvp.Value);
                float value = GMReflection.GetParamFloatValue(kvp.Value);

                string typeStr;
                string valueStr;
                switch (paramType)
                {
                    case AnimatorControllerParameterType.Bool:
                        typeStr = "Bool";
                        valueStr = (value != 0f).ToString().ToLower();
                        break;
                    case AnimatorControllerParameterType.Int:
                        typeStr = "Int";
                        valueStr = ((int)value).ToString();
                        break;
                    default:
                        typeStr = "Float";
                        valueStr = value.ToString("F3");
                        break;
                }

                sb.AppendLine($"  {kvp.Key} ({typeStr}) = {valueStr}");
            }

            return sb.ToString();
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Evaluates the PlayableGraph repeatedly until the avatar's visual state stabilizes.
        /// Needed because parameter drivers can chain (A sets B which triggers C) and transitions
        /// may have non-zero duration. Compares object active states and blendshape weights
        /// between consecutive evaluations to detect convergence.
        /// </summary>
        static void SettleGraph(object module, int maxIterations = 15)
        {
            // Phase 1: coarse — cover transition durations (Unity default 0.25s, some creators use up to 0.5s)
            for (int i = 0; i < 5; i++)
                GMReflection.EvaluateGraph(module, 0.1f);

            // Phase 2: fine convergence — catch parameter driver cascades
            const float dt = 1f / 60f;
            var prev = QuickStateHash(module);
            for (int i = 0; i < maxIterations - 5; i++)
            {
                GMReflection.EvaluateGraph(module, dt);
                long curr = QuickStateHash(module);
                if (curr == prev)
                    return;
                prev = curr;
            }
        }

        /// <summary>
        /// Fast hash of avatar state for convergence detection.
        /// Only checks object active states and a sample of blendshape weights
        /// to avoid the cost of a full snapshot per iteration.
        /// </summary>
        static long QuickStateHash(object module)
        {
            var avatar = GMReflection.GetAvatar(module);
            if (avatar == null) return 0;

            long hash = 17;
            var transforms = avatar.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
                hash = hash * 31 + (transforms[i].gameObject.activeSelf ? i : ~i);

            var renderers = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (var smr in renderers)
            {
                if (smr.sharedMesh == null) continue;
                int count = smr.sharedMesh.blendShapeCount;
                for (int i = 0; i < count; i++)
                    hash = hash * 31 + (long)(smr.GetBlendShapeWeight(i) * 100);
            }

            return hash;
        }

        static string FindDescriptorByPath(string avatarHierarchyPath, out VRCAvatarDescriptor descriptor,
            out Transform avatarRoot)
        {
            descriptor = null;
            avatarRoot = null;

            var descriptors = UnityEngine.Object.FindObjectsOfType<VRCAvatarDescriptor>(true);
            if (descriptors.Length == 0)
                return "[ERROR] No VRCAvatarDescriptor found in scene.";

            foreach (var d in descriptors)
            {
                string path = BuildHierarchyPath(d.transform);
                if (path == avatarHierarchyPath)
                {
                    descriptor = d;
                    avatarRoot = d.transform;
                    return null;
                }
            }

            // List available paths
            var sb = new StringBuilder();
            sb.AppendLine($"[ERROR] No avatar found at path \"{avatarHierarchyPath}\". Available descriptors:");
            foreach (var d in descriptors)
                sb.AppendLine($"  - {BuildHierarchyPath(d.transform)}");
            return sb.ToString();
        }

        static string BuildHierarchyPath(Transform t)
        {
            var parts = new List<string>();
            while (t != null)
            {
                parts.Add(t.name);
                t = t.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        static string FindOrCreateGM(VRCAvatarDescriptor descriptor, out Component gm, out object module)
        {
            gm = null;
            module = null;

            // Try to find an existing GM with a valid ModuleVrc3
            string existingError = FindExistingGM(out gm, out module);
            if (existingError == null && module != null)
            {
                // Check if this module is for our descriptor
                var moduleDescriptor = GMReflection.GetAvatarDescriptor(module);
                if (moduleDescriptor == descriptor)
                    return null;
            }

            // Auto-instantiate
            try
            {
                gm = GMReflection.AutoInstantiateGM(descriptor);
                module = GMReflection.GetModule(gm);
                if (module == null)
                    return "[ERROR] GM auto-instantiation succeeded but Module is null after SetModule.";
                if (!GMReflection.IsModuleVrc3(module))
                    return "[ERROR] GM module is not ModuleVrc3 — wrong SDK version?";
                return null;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Failed to auto-instantiate Gesture Manager: {ex.Message}";
            }
        }

        static string FindExistingGM(out Component gm, out object module)
        {
            gm = null;
            module = null;

            var managers = GMReflection.FindAllGestureManagers();
            foreach (var mgr in managers)
            {
                var mod = GMReflection.GetModule(mgr);
                if (mod != null && GMReflection.IsModuleVrc3(mod))
                {
                    gm = mgr;
                    module = mod;
                    return null;
                }
            }

            return "[INFO] No active Gesture Manager with ModuleVrc3 found.";
        }

        /// <summary>
        /// Matches a menu parameter name against a requested name, accounting for VRCFury's
        /// VF###_ prefix renaming. Works in both directions: caller can pass either the
        /// authoring-time name or the VF-prefixed built name.
        /// </summary>
        static bool MatchesParameterName(string menuParamName, string requestedName)
        {
            if (menuParamName == requestedName) return true;
            // Menu has VF prefix, caller used authoring name
            if (menuParamName.EndsWith(requestedName) && menuParamName.Length > requestedName.Length)
            {
                string prefix = menuParamName.Substring(0, menuParamName.Length - requestedName.Length);
                if (System.Text.RegularExpressions.Regex.IsMatch(prefix, @"^VF\d+_$"))
                    return true;
            }
            // Caller used VF-prefixed name, menu has authoring name
            if (requestedName.EndsWith(menuParamName) && requestedName.Length > menuParamName.Length)
            {
                string prefix = requestedName.Substring(0, requestedName.Length - menuParamName.Length);
                if (System.Text.RegularExpressions.Regex.IsMatch(prefix, @"^VF\d+_$"))
                    return true;
            }
            return false;
        }

        static string SummarizeChangeTypes(SnapshotDiff diff)
        {
            int objects = 0, blendshapes = 0, matSwaps = 0, matProps = 0;
            foreach (var c in diff.Changes)
            {
                if (c.Contains("activeSelf")) objects++;
                else if (c.Contains("blendShape.")) blendshapes++;
                else if (c.Contains("material swapped")) matSwaps++;
                else matProps++;
            }

            var parts = new List<string>();
            if (objects > 0) parts.Add($"{objects} object{(objects > 1 ? "s" : "")} toggled");
            if (blendshapes > 0) parts.Add($"{blendshapes} blendshape{(blendshapes > 1 ? "s" : "")} changed");
            if (matSwaps > 0) parts.Add($"{matSwaps} material{(matSwaps > 1 ? "s" : "")} swapped");
            if (matProps > 0) parts.Add($"{matProps} material prop{(matProps > 1 ? "s" : "")} changed");
            return string.Join(", ", parts);
        }

        #endregion
    }
}
