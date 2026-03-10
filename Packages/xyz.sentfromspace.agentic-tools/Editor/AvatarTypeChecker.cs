using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.SDKBase;

namespace SentFromSpace.AgenticTools.Editor
{
    /// <summary>
    /// Post-build static analysis for VRChat avatars.
    /// Validates parameter consistency, animation paths, and material properties
    /// on the built avatar clone after VRCFury/NDMF preprocessing.
    /// </summary>
    public static class AvatarTypeChecker
    {
        // VRC built-in parameters that exist implicitly and should not be flagged
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
        };

        // Paths that VRCFury generates as dummy bindings — not real hierarchy targets
        static readonly HashSet<string> IgnoredAnimPaths = new HashSet<string>
        {
            "ThisHopefullyDoesntExist",
        };

        static readonly string[] PhysBoneSuffixes =
            { "_IsGrabbed", "_IsPosed", "_Angle", "_Stretch", "_Squish" };

        // Known VRCFT base parameter names (used with v2/ prefix).
        // Source: https://docs.vrcft.io/docs/tutorial-avatars/tutorial-avatars-extras/parameters
        static readonly HashSet<string> VrcftBaseParams = new HashSet<string>
        {
            // Eye Gaze
            "EyeLeftX", "EyeLeftY", "EyeRightX", "EyeRightY",
            // Eye Expression
            "EyeLidRight", "EyeLidLeft", "EyeLid",
            "EyeSquintRight", "EyeSquintLeft", "EyeSquint",
            "PupilDilation", "PupilDiameterRight", "PupilDiameterLeft", "PupilDiameter",
            // Brow
            "BrowPinchRight", "BrowPinchLeft", "BrowLowererRight", "BrowLowererLeft",
            "BrowInnerUpRight", "BrowInnerUpLeft", "BrowOuterUpRight", "BrowOuterUpLeft",
            // Nose
            "NoseSneerRight", "NoseSneerLeft",
            "NasalDilationRight", "NasalDilationLeft", "NasalConstrictRight", "NasalConstrictLeft",
            // Cheek
            "CheekSquintRight", "CheekSquintLeft", "CheekPuffSuckRight", "CheekPuffSuckLeft",
            "CheekPuffRight", "CheekPuffLeft",
            // Jaw
            "JawOpen", "MouthClosed", "JawX", "JawZ", "JawForward", "JawClench1", "JawMandibleRaise1",
            // Lip
            "LipSuckUpperRight", "LipSuckUpperLeft", "LipSuckLowerRight", "LipSuckLowerLeft",
            "LipSuckCornerRight", "LipSuckCornerLeft",
            "LipFunnelUpperRight", "LipFunnelUpperLeft", "LipFunnelLowerRight", "LipFunnelLowerLeft",
            "LipPuckerUpperRight", "LipPuckerUpperLeft", "LipPuckerLowerRight", "LipPuckerLowerLeft",
            // Mouth
            "MouthUpperUpRight", "MouthUpperUpLeft", "MouthLowerDownRight", "MouthLowerDownLeft",
            "MouthUpperDeepenRight", "MouthUpperDeepenLeft", "MouthUpperX", "MouthLowerX",
            "MouthCornerPullRight", "MouthCornerPullLeft", "MouthCornerSlantRight", "MouthCornerSlantLeft",
            "MouthDimpleRight", "MouthDimpleLeft", "MouthFrownRight", "MouthFrownLeft",
            "MouthStretchRight", "MouthStretchLeft", "MouthRaiserUpper", "MouthRaiserLower",
            "MouthPressRight", "MouthPressLeft", "MouthTightenerRight", "MouthTightenerLeft",
            // Tongue
            "TongueOut", "TongueX", "TongueY", "TongueRoll",
            "TongueArchY", "TongueShape", "TongueTwistRight", "TongueTwistLeft",
            // Neck
            "SoftPalateClose", "ThroatSwallow", "NeckFlexRight", "NeckFlexLeft",
            // Simplified
            "EyeX", "EyeY", "BrowDownRight", "BrowDownLeft",
            "BrowOuterUp", "BrowInnerUp", "BrowUp",
            "BrowExpressionRight", "BrowExpressionLeft", "BrowExpression",
            "MouthX", "MouthUpperUp", "MouthLowerDown", "MouthOpen",
            "MouthSmileRight", "MouthSmileLeft", "MouthSadRight", "MouthSadLeft",
            "SmileFrownRight", "SmileFrownLeft", "SmileFrown",
            "SmileSadRight", "SmileSadLeft", "SmileSad",
            "LipSuckUpper", "LipSuckLower", "LipSuck",
            "LipFunnelUpper", "LipFunnelLower", "LipFunnel",
            "LipPuckerUpper", "LipPuckerLower", "LipPucker",
            "MouthPress", "NoseSneer", "CheekSquint", "CheekPuffSuck",
        };

        // Tracking status parameters (no v2/ prefix)
        static readonly HashSet<string> VrcftTrackingStatusParams = new HashSet<string>
        {
            "EyeTrackingActive", "ExpressionTrackingActive", "LipTrackingActive",
        };

        // Binary encoding suffixes used by VRCFT
        static readonly string[] VrcftBinarySuffixes = { "Negative", "1", "2", "4" };

        /// <summary>
        /// Check if a parameter name is a known VRCFT (VRCFaceTracking) parameter.
        /// Matches tracking status params, v2/ base params (with any prefix path like FT/v2/),
        /// binary-encoded variants, and OSCm/ helper parameters.
        /// </summary>
        static bool IsVrcftParameter(string paramName)
        {
            if (string.IsNullOrEmpty(paramName)) return false;

            // Tracking status parameters (no prefix)
            if (VrcftTrackingStatusParams.Contains(paramName)) return true;

            // VRCFT params use v2/ prefix, optionally preceded by a path (e.g. FT/v2/)
            int v2Idx = paramName.IndexOf("v2/");
            if (v2Idx < 0) return false;
            // Only match if v2/ is at start or preceded by /
            if (v2Idx > 0 && paramName[v2Idx - 1] != '/') return false;
            string stripped = paramName.Substring(v2Idx + 3);

            // Exact base parameter match
            if (VrcftBaseParams.Contains(stripped)) return true;

            // Binary encoding: {baseName}{suffix} where suffix is Negative, 1, 2, 4
            foreach (var suffix in VrcftBinarySuffixes)
            {
                if (stripped.EndsWith(suffix))
                {
                    string baseName = stripped.Substring(0, stripped.Length - suffix.Length);
                    if (VrcftBaseParams.Contains(baseName)) return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Validate the built avatar by exact full hierarchy path (e.g. "Root/Avatar").
        /// </summary>
        public static string ValidateBuiltAvatar(string avatarHierarchyPath)
        {
            string normalizedPath = NormalizeHierarchyPath(avatarHierarchyPath);
            if (string.IsNullOrEmpty(normalizedPath))
                return BuildMissingPathError();

            if (!TryFindByHierarchyPath(normalizedPath, out var avatar, out var findError))
            {
                var sb = new StringBuilder();
                sb.AppendLine(findError);
                sb.AppendLine();
                sb.AppendLine("Available avatar descriptor paths:");
                sb.AppendLine(GetDescriptorPathList());
                return sb.ToString().TrimEnd();
            }

            if (avatar.GetComponent<VRCAvatarDescriptor>() == null)
            {
                return $"[ERROR] Object at path \"{normalizedPath}\" does not have a VRCAvatarDescriptor component.";
            }

            return $"[Found via explicit hierarchy path: {GetHierarchyPath(avatar.transform)}]\n\n{Validate(avatar)}";
        }

        /// <summary>
        /// No-arg overload intentionally disabled to prevent wrong-avatar auto-selection.
        /// </summary>
        public static string ValidateBuiltAvatar()
        {
            return BuildMissingPathError();
        }

        static string BuildMissingPathError()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[ERROR] ValidateBuiltAvatar() requires an explicit avatar hierarchy path.");
            sb.AppendLine("Call ValidateBuiltAvatar(\"Root/Child/Avatar\") with the descriptor object's full path.");
            sb.AppendLine();
            sb.AppendLine("Available avatar descriptor paths:");
            sb.AppendLine(GetDescriptorPathList());
            return sb.ToString().TrimEnd();
        }

        static string GetDescriptorPathList()
        {
            var descriptors = UnityEngine.Object.FindObjectsOfType<VRCAvatarDescriptor>(true)
                .Where(d => d != null)
                .Select(d => d.transform)
                .ToArray();

            if (descriptors.Length == 0)
                return "  (none found)";

            var lines = descriptors
                .Select(GetHierarchyPath)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToArray();

            var sb = new StringBuilder();
            foreach (var line in lines)
                sb.AppendLine($"  - {line}");
            return sb.ToString().TrimEnd();
        }

        static string NormalizeHierarchyPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            var segments = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            return string.Join("/", segments);
        }

        static bool TryFindByHierarchyPath(string fullPath, out GameObject result, out string error)
        {
            result = null;
            error = null;

            var segments = fullPath.Split('/');
            if (segments.Length == 0)
            {
                error = $"[ERROR] Avatar path \"{fullPath}\" is invalid.";
                return false;
            }

            var roots = SceneManager.GetActiveScene().GetRootGameObjects();
            var matches = roots
                .Where(r => r.name == segments[0])
                .Select(r => r.transform)
                .ToList();

            if (matches.Count == 0)
            {
                error = $"[ERROR] Avatar path \"{fullPath}\" not found. Missing root \"{segments[0]}\".";
                return false;
            }

            if (matches.Count > 1)
            {
                error = BuildAmbiguousPathError(fullPath, segments[0], matches);
                return false;
            }

            var current = matches[0];
            for (int i = 1; i < segments.Length; i++)
            {
                string segment = segments[i];
                var childMatches = new List<Transform>();
                for (int c = 0; c < current.childCount; c++)
                {
                    var child = current.GetChild(c);
                    if (child.name == segment)
                        childMatches.Add(child);
                }

                if (childMatches.Count == 0)
                {
                    error = $"[ERROR] Avatar path \"{fullPath}\" not found. Missing segment \"{segment}\" under \"{GetHierarchyPath(current)}\".";
                    return false;
                }

                if (childMatches.Count > 1)
                {
                    error = BuildAmbiguousPathError(fullPath, segment, childMatches);
                    return false;
                }

                current = childMatches[0];
            }

            result = current.gameObject;
            return true;
        }

        static string BuildAmbiguousPathError(string requestedPath, string segment, IEnumerable<Transform> matches)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[ERROR] Avatar path \"{requestedPath}\" is ambiguous at segment \"{segment}\".");
            sb.AppendLine("Matching candidates:");
            foreach (var match in matches)
                sb.AppendLine($"  - {GetHierarchyPathWithSiblingIndices(match)}");
            sb.AppendLine("Use unique hierarchy names so the slash-name full path resolves to exactly one object.");
            return sb.ToString().TrimEnd();
        }

        static string GetHierarchyPath(Transform t)
        {
            if (t == null) return string.Empty;

            var parts = new List<string>();
            while (t != null)
            {
                parts.Add(t.name);
                t = t.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        static string GetHierarchyPathWithSiblingIndices(Transform t)
        {
            if (t == null) return string.Empty;

            var parts = new List<string>();
            while (t != null)
            {
                parts.Add($"{t.name}[{t.GetSiblingIndex()}]");
                t = t.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        /// <summary>
        /// Validate a specific avatar root after build.
        /// </summary>
        public static string Validate(GameObject avatarRoot)
        {
            var desc = avatarRoot.GetComponent<VRCAvatarDescriptor>();
            if (desc == null)
                return "[ERROR] No VRCAvatarDescriptor on provided GameObject.";

            var sb = new StringBuilder();
            sb.AppendLine($"=== Avatar Type Check: {avatarRoot.name} ===");

            int totalErrors = 0, totalWarnings = 0;

            // Collect all controllers from playable layers
            var controllers = CollectControllers(desc);

            // A. Parameter Consistency
            sb.AppendLine();
            sb.AppendLine("[Parameters]");
            var (paramErrors, paramWarnings) = CheckParameters(desc, controllers, avatarRoot, sb);
            totalErrors += paramErrors;
            totalWarnings += paramWarnings;

            // B. Animation Path Validation
            sb.AppendLine();
            sb.AppendLine("[Animation Paths]");
            var (pathErrors, pathWarnings) = CheckAnimationPaths(controllers, avatarRoot, sb);
            totalErrors += pathErrors;
            totalWarnings += pathWarnings;

            // C. Material Property Validation
            sb.AppendLine();
            sb.AppendLine("[Material Properties]");
            var (matErrors, matWarnings) = CheckMaterialProperties(controllers, avatarRoot, sb);
            totalErrors += matErrors;
            totalWarnings += matWarnings;

            sb.AppendLine();
            sb.AppendLine($"=== Summary: {totalErrors} error{(totalErrors != 1 ? "s" : "")}, " +
                          $"{totalWarnings} warning{(totalWarnings != 1 ? "s" : "")} ===");

            return sb.ToString();
        }

        #region Controller Collection

        struct ControllerInfo
        {
            public AnimatorController Controller;
            public string LayerTypeName; // "Base", "FX", etc.
        }

        static List<ControllerInfo> CollectControllers(VRCAvatarDescriptor desc)
        {
            var result = new List<ControllerInfo>();

            void TryAdd(VRCAvatarDescriptor.CustomAnimLayer layer)
            {
                if (layer.isDefault || layer.animatorController == null) return;
                var ac = layer.animatorController as AnimatorController;
                if (ac == null) return;
                result.Add(new ControllerInfo { Controller = ac, LayerTypeName = layer.type.ToString() });
            }

            if (desc.baseAnimationLayers != null)
                foreach (var l in desc.baseAnimationLayers) TryAdd(l);
            if (desc.specialAnimationLayers != null)
                foreach (var l in desc.specialAnimationLayers) TryAdd(l);

            return result;
        }

        #endregion

        #region A. Parameter Consistency

        static (int errors, int warnings) CheckParameters(
            VRCAvatarDescriptor desc, List<ControllerInfo> controllers,
            GameObject avatarRoot, StringBuilder sb)
        {
            int errors = 0, warnings = 0;

            // 1. Collect animator controller parameters (name → type)
            var animParams = new Dictionary<string, AnimatorControllerParameterType>();
            foreach (var ci in controllers)
                foreach (var p in ci.Controller.parameters)
                    animParams[p.name] = p.type;

            // 2. Collect expression parameters
            var exprParams = new Dictionary<string, VRCExpressionParameters.ValueType>();
            int syncedBits = 0;
            if (desc.expressionParameters != null && desc.expressionParameters.parameters != null)
            {
                foreach (var p in desc.expressionParameters.parameters)
                {
                    if (string.IsNullOrEmpty(p.name)) continue;
                    exprParams[p.name] = p.valueType;
                    if (p.networkSynced)
                    {
                        switch (p.valueType)
                        {
                            case VRCExpressionParameters.ValueType.Bool: syncedBits += 1; break;
                            case VRCExpressionParameters.ValueType.Int: syncedBits += 8; break;
                            case VRCExpressionParameters.ValueType.Float: syncedBits += 8; break;
                        }
                    }
                }
            }

            // 3. Collect PhysBone parameters
            var physBones = avatarRoot.GetComponentsInChildren<VRCPhysBone>(true);
            var physBoneParams = new HashSet<string>();
            foreach (var pb in physBones)
            {
                if (string.IsNullOrEmpty(pb.parameter)) continue;
                foreach (var suffix in PhysBoneSuffixes)
                    physBoneParams.Add(pb.parameter + suffix);
            }

            // 4. Collect Contact Receiver parameters
            var contacts = avatarRoot.GetComponentsInChildren<VRCContactReceiver>(true);
            var contactParams = new HashSet<string>();
            foreach (var c in contacts)
            {
                if (!string.IsNullOrEmpty(c.parameter))
                    contactParams.Add(c.parameter);
            }

            // 5. Collect menu parameters
            var menuParams = new HashSet<string>();
            if (desc.expressionsMenu != null)
                CollectMenuParams(desc.expressionsMenu, menuParams, new HashSet<VRCExpressionsMenu>());

            // 6. Collect parameters used in controllers (conditions, drivers, blend trees)
            //    Also collect driver write targets separately for value-source analysis
            var usedInControllers = new HashSet<string>();
            var driverWriteTargets = new HashSet<string>();
            foreach (var ci in controllers)
                CollectUsedParams(ci.Controller, usedInControllers, driverWriteTargets);

            // 7. Collect parameters animated by clips (Animator type bindings — VRCFury blend tree math)
            var clipAnimatedParams = CollectClipAnimatedParams(controllers);

            // --- Checks ---

            // PhysBone params not in any animator controller
            foreach (var p in physBoneParams)
            {
                if (animParams.ContainsKey(p)) continue;
                var fuzzy = FindFuzzyMatch(p, animParams.Keys);
                if (fuzzy != null)
                    sb.AppendLine($"  [WARN] PhysBone parameter \"{p}\" not found in any animator controller (possible match: \"{fuzzy}\")");
                else
                    sb.AppendLine($"  [WARN] PhysBone parameter \"{p}\" not found in any animator controller");
                errors++;
            }

            // Contact params not in any animator controller
            foreach (var p in contactParams)
            {
                if (animParams.ContainsKey(p)) continue;
                if (VrcBuiltInParams.Contains(p)) continue;
                var fuzzy = FindFuzzyMatch(p, animParams.Keys);
                if (fuzzy != null)
                    sb.AppendLine($"  [WARN] Contact parameter \"{p}\" not found in any animator controller (possible match: \"{fuzzy}\")");
                else
                    sb.AppendLine($"  [WARN] Contact parameter \"{p}\" not found in any animator controller");
                errors++;
            }

            // Condition/driver/blendtree params not in animator controller
            int vrcftUndefinedSkips = 0;
            foreach (var p in usedInControllers)
            {
                if (animParams.ContainsKey(p)) continue;
                if (exprParams.ContainsKey(p)) continue;
                if (VrcBuiltInParams.Contains(p)) continue;
                if (string.IsNullOrEmpty(p)) continue;
                // VRCFT params are OSC-driven and don't need animator controller definitions
                if (IsVrcftParameter(p)) { vrcftUndefinedSkips++; continue; }
                sb.AppendLine($"  [ERROR] Controller references parameter \"{p}\" but it is not defined in any animator controller");
                errors++;
            }
            if (vrcftUndefinedSkips > 0)
                sb.AppendLine($"  [INFO] {vrcftUndefinedSkips} VRCFT (OSC-driven) undefined controller parameters skipped");

            // Type mismatch between expression params and animator controller params
            int vrcftTypeSkips = 0;
            int vrcfuryToggleSkips = 0;
            foreach (var kvp in exprParams)
            {
                if (!animParams.ContainsKey(kvp.Key)) continue;
                if (!IsTypeCompatible(kvp.Value, animParams[kvp.Key]))
                {
                    bool isBoolToFloat = kvp.Value == VRCExpressionParameters.ValueType.Bool &&
                                         animParams[kvp.Key] == AnimatorControllerParameterType.Float;

                    if (isBoolToFloat)
                    {
                        // VRCFT binary encoding: Bool expression params with Float animator params
                        if (IsVrcftParameter(kvp.Key))
                        {
                            vrcftTypeSkips++;
                            continue;
                        }
                        // VRCFury toggle internals: VF###_* prefix Bool→Float
                        if (Regex.IsMatch(kvp.Key, @"^VF\d+_"))
                        {
                            vrcfuryToggleSkips++;
                            continue;
                        }
                    }

                    sb.AppendLine($"  [WARN] Type mismatch for \"{kvp.Key}\": expression={kvp.Value}, animator={animParams[kvp.Key]} (VRChat auto-converts, but may be unintended)");
                    warnings++;
                }
            }
            if (vrcftTypeSkips > 0)
                sb.AppendLine($"  [INFO] {vrcftTypeSkips} VRCFT (OSC-driven) Bool\u2192Float type differences skipped");
            if (vrcfuryToggleSkips > 0)
                sb.AppendLine($"  [INFO] {vrcfuryToggleSkips} VRCFury toggle Bool\u2192Float type differences skipped");

            // Menu params not in expression parameters
            foreach (var p in menuParams)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (exprParams.ContainsKey(p)) continue;
                if (VrcBuiltInParams.Contains(p)) continue;
                sb.AppendLine($"  [ERROR] Menu parameter \"{p}\" not found in expression parameters (toggle would be non-functional)");
                errors++;
            }

            // Expression params not used anywhere
            foreach (var kvp in exprParams)
            {
                if (animParams.ContainsKey(kvp.Key)) continue;
                if (usedInControllers.Contains(kvp.Key)) continue;
                if (VrcBuiltInParams.Contains(kvp.Key)) continue;
                if (IsVrcFuryInternal(kvp.Key)) continue;
                sb.AppendLine($"  [WARN] Expression parameter \"{kvp.Key}\" not used in any animator controller");
                warnings++;
            }

            // Animator params with no known value source
            foreach (var kvp in animParams)
            {
                if (string.IsNullOrEmpty(kvp.Key)) continue;
                if (exprParams.ContainsKey(kvp.Key)) continue;
                if (physBoneParams.Contains(kvp.Key)) continue;
                if (contactParams.Contains(kvp.Key)) continue;
                if (VrcBuiltInParams.Contains(kvp.Key)) continue;
                if (IsVrcftParameter(kvp.Key)) continue;
                if (IsVrcFuryInternal(kvp.Key)) continue;
                if (driverWriteTargets.Contains(kvp.Key)) continue;
                if (clipAnimatedParams.Contains(kvp.Key)) continue;
                sb.AppendLine($"  [WARN] Animator parameter \"{kvp.Key}\" has no known value source");
                warnings++;
            }

            // Synced bit usage
            sb.AppendLine($"  [INFO] {syncedBits}/256 synced parameter bits used");

            return (errors, warnings);
        }

        static void CollectMenuParams(VRCExpressionsMenu menu, HashSet<string> outParams,
            HashSet<VRCExpressionsMenu> visited)
        {
            if (menu == null || visited.Contains(menu)) return;
            visited.Add(menu);

            if (menu.controls == null) return;
            foreach (var ctrl in menu.controls)
            {
                if (ctrl.parameter != null && !string.IsNullOrEmpty(ctrl.parameter.name))
                    outParams.Add(ctrl.parameter.name);

                if (ctrl.subParameters != null)
                    foreach (var sp in ctrl.subParameters)
                        if (sp != null && !string.IsNullOrEmpty(sp.name))
                            outParams.Add(sp.name);

                if (ctrl.type == VRCExpressionsMenu.Control.ControlType.SubMenu && ctrl.subMenu != null)
                    CollectMenuParams(ctrl.subMenu, outParams, visited);
            }
        }

        static void CollectUsedParams(AnimatorController controller, HashSet<string> outParams,
            HashSet<string> driverWriteTargets = null)
        {
            foreach (var layer in controller.layers)
            {
                if (layer.stateMachine == null) continue;
                CollectStateMachineParams(layer.stateMachine, outParams, driverWriteTargets);
            }
        }

        static void CollectStateMachineParams(AnimatorStateMachine sm, HashSet<string> outParams,
            HashSet<string> driverWriteTargets = null)
        {
            foreach (var state in sm.states)
            {
                var s = state.state;

                // Transition conditions
                foreach (var t in s.transitions)
                    foreach (var c in t.conditions)
                        outParams.Add(c.parameter);

                // State behaviors (parameter drivers)
                foreach (var b in s.behaviours)
                {
                    if (b is VRCAvatarParameterDriver driver && driver.parameters != null)
                    {
                        foreach (var p in driver.parameters)
                        {
                            if (!string.IsNullOrEmpty(p.name))
                            {
                                outParams.Add(p.name);
                                driverWriteTargets?.Add(p.name);
                            }
                            if (!string.IsNullOrEmpty(p.source)) outParams.Add(p.source);
                        }
                    }
                }

                // BlendTree parameters
                if (s.motion is BlendTree bt)
                    CollectBlendTreeParams(bt, outParams);
            }

            // Any-state transitions
            foreach (var t in sm.anyStateTransitions)
                foreach (var c in t.conditions)
                    outParams.Add(c.parameter);

            // Entry/exit transitions
            foreach (var t in sm.entryTransitions)
                foreach (var c in t.conditions)
                    outParams.Add(c.parameter);

            // Sub state machines
            foreach (var sub in sm.stateMachines)
                CollectStateMachineParams(sub.stateMachine, outParams, driverWriteTargets);
        }

        static void CollectBlendTreeParams(BlendTree bt, HashSet<string> outParams)
        {
            if (!string.IsNullOrEmpty(bt.blendParameter))
                outParams.Add(bt.blendParameter);
            if (!string.IsNullOrEmpty(bt.blendParameterY))
                outParams.Add(bt.blendParameterY);

            foreach (var child in bt.children)
            {
                // Direct blend tree children have individual parameters
                if (bt.blendType == BlendTreeType.Direct &&
                    !string.IsNullOrEmpty(child.directBlendParameter))
                    outParams.Add(child.directBlendParameter);

                if (child.motion is BlendTree childBt)
                    CollectBlendTreeParams(childBt, outParams);
            }
        }

        /// <summary>
        /// Collect parameters that are directly animated by animation clips (Animator type bindings).
        /// VRCFury uses these for blend tree math — clips keyframe parameters to constants.
        /// </summary>
        static HashSet<string> CollectClipAnimatedParams(List<ControllerInfo> controllers)
        {
            var result = new HashSet<string>();
            foreach (var ci in controllers)
                foreach (var clip in CollectClips(ci.Controller))
                {
                    if (clip == null) continue;
                    foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                        if (binding.type == typeof(Animator) && !string.IsNullOrEmpty(binding.propertyName))
                            result.Add(binding.propertyName);
                }
            return result;
        }

        static bool IsTypeCompatible(VRCExpressionParameters.ValueType exprType,
            AnimatorControllerParameterType animType)
        {
            switch (exprType)
            {
                case VRCExpressionParameters.ValueType.Bool:
                    return animType == AnimatorControllerParameterType.Bool;
                case VRCExpressionParameters.ValueType.Int:
                    return animType == AnimatorControllerParameterType.Int;
                case VRCExpressionParameters.ValueType.Float:
                    return animType == AnimatorControllerParameterType.Float;
                default:
                    return true;
            }
        }

        /// <summary>
        /// VRCFury internal/metadata parameters (version stamps, debug network, etc.)
        /// </summary>
        static bool IsVrcFuryInternal(string paramName)
        {
            if (paramName.StartsWith("VF ")) return true;  // "VF DN", "VF 2022.3.22f1", "VF 3.10.1", etc.
            return false;
        }

        /// <summary>
        /// Detect VRCFury-style renames like "VF103_SidePull_Stretch" matching "SidePull_Stretch"
        /// </summary>
        static string FindFuzzyMatch(string paramName, IEnumerable<string> candidates)
        {
            foreach (var candidate in candidates)
            {
                // Check if candidate ends with the param name after a VF prefix (VF###_)
                if (candidate.EndsWith(paramName) && candidate.Length > paramName.Length)
                {
                    string prefix = candidate.Substring(0, candidate.Length - paramName.Length);
                    if (System.Text.RegularExpressions.Regex.IsMatch(prefix, @"^VF\d+_$"))
                        return candidate;
                }
                // Also check reverse: param has VF prefix, candidate is the base
                if (paramName.EndsWith(candidate) && paramName.Length > candidate.Length)
                {
                    string prefix = paramName.Substring(0, paramName.Length - candidate.Length);
                    if (System.Text.RegularExpressions.Regex.IsMatch(prefix, @"^VF\d+_$"))
                        return candidate;
                }
            }
            return null;
        }

        #endregion

        #region B. Animation Path Validation

        static (int errors, int warnings) CheckAnimationPaths(
            List<ControllerInfo> controllers, GameObject avatarRoot, StringBuilder sb)
        {
            int errors = 0, warnings = 0;

            // Caches
            var pathCache = new Dictionary<string, Transform>();    // path → resolved transform (null = not found)
            var pathChecked = new HashSet<string>();                // track which paths we already resolved
            var blendShapeCache = new Dictionary<SkinnedMeshRenderer, HashSet<string>>();
            var checkedBindings = new HashSet<string>();            // deduplicate: "clipName|path|property"
            var materialSlotCache = new Dictionary<Renderer, int>();
            var reportedBlendShapes = new HashSet<string>();    // deduplicate: "path|shapeName"
            var reportedMissingPaths = new HashSet<string>();   // deduplicate: "path"

            // Collect all clips from all controllers
            foreach (var ci in controllers)
            {
                foreach (var clip in CollectClips(ci.Controller))
                {
                    if (clip == null) continue;
                    var bindings = AnimationUtility.GetCurveBindings(clip);
                    var objBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);

                    foreach (var binding in bindings.Concat(objBindings))
                    {
                        string key = $"{clip.name}|{binding.path}|{binding.propertyName}|{binding.type?.Name}";
                        if (!checkedBindings.Add(key)) continue;

                        // Skip empty paths (root-level bindings) and known dummy paths
                        if (string.IsNullOrEmpty(binding.path)) continue;
                        if (IgnoredAnimPaths.Contains(binding.path)) continue;

                        // Resolve path
                        Transform resolved = ResolvePath(avatarRoot.transform, binding.path, pathCache, pathChecked);

                        if (resolved == null)
                        {
                            if (reportedMissingPaths.Add(binding.path))
                            {
                                sb.AppendLine($"  [ERROR] Path \"{binding.path}\" not found in hierarchy (first seen in clip: \"{clip.name}\")");
                                errors++;
                            }
                            continue;
                        }

                        // Check component exists on resolved object
                        if (binding.type != null && binding.type != typeof(GameObject))
                        {
                            var component = resolved.GetComponent(binding.type);
                            // VRCFury may swap constraint types — check for any constraint
                            if (component == null && IsConstraintType(binding.type))
                                component = GetAnyConstraint(resolved, binding.type);

                            if (component == null)
                            {
                                sb.AppendLine($"  [ERROR] Component {binding.type.Name} not found on \"{binding.path}\" (clip: \"{clip.name}\")");
                                errors++;
                                continue;
                            }
                        }

                        // Check blendshape bindings
                        if (binding.propertyName != null && binding.propertyName.StartsWith("blendShape."))
                        {
                            var smr = resolved.GetComponent<SkinnedMeshRenderer>();
                            if (smr != null && smr.sharedMesh != null)
                            {
                                var shapes = GetBlendShapeNames(smr, blendShapeCache);
                                string shapeName = binding.propertyName.Substring("blendShape.".Length);
                                if (!shapes.Contains(shapeName) &&
                                    reportedBlendShapes.Add($"{binding.path}|{shapeName}"))
                                {
                                    sb.AppendLine($"  [ERROR] BlendShape \"{shapeName}\" not found on \"{binding.path}\" (first seen in clip: \"{clip.name}\")");
                                    errors++;
                                }
                            }
                        }

                        // Check material slot index
                        if (binding.propertyName != null && binding.propertyName.StartsWith("m_Materials.Array.data["))
                        {
                            var renderer = resolved.GetComponent<Renderer>();
                            if (renderer != null)
                            {
                                int slotCount = GetMaterialSlotCount(renderer, materialSlotCache);
                                // Parse slot index
                                int startIdx = "m_Materials.Array.data[".Length;
                                int endIdx = binding.propertyName.IndexOf(']', startIdx);
                                if (endIdx > startIdx && int.TryParse(
                                    binding.propertyName.Substring(startIdx, endIdx - startIdx), out int slotIdx))
                                {
                                    if (slotIdx >= slotCount)
                                    {
                                        sb.AppendLine($"  [ERROR] Material slot [{slotIdx}] out of range (count={slotCount}) on \"{binding.path}\" (clip: \"{clip.name}\")");
                                        errors++;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if (errors == 0 && warnings == 0)
                sb.AppendLine("  (all paths valid)");

            return (errors, warnings);
        }

        static Transform ResolvePath(Transform root, string path,
            Dictionary<string, Transform> cache, HashSet<string> checked_)
        {
            if (cache.TryGetValue(path, out var cached)) return cached;
            if (!checked_.Add(path)) return null;

            var result = root.Find(path);
            cache[path] = result;
            return result;
        }

        static HashSet<string> GetBlendShapeNames(SkinnedMeshRenderer smr,
            Dictionary<SkinnedMeshRenderer, HashSet<string>> cache)
        {
            if (cache.TryGetValue(smr, out var names)) return names;
            names = new HashSet<string>();
            var mesh = smr.sharedMesh;
            if (mesh != null)
                for (int i = 0; i < mesh.blendShapeCount; i++)
                    names.Add(mesh.GetBlendShapeName(i));
            cache[smr] = names;
            return names;
        }

        static int GetMaterialSlotCount(Renderer r, Dictionary<Renderer, int> cache)
        {
            if (cache.TryGetValue(r, out int count)) return count;
            count = r.sharedMaterials?.Length ?? 0;
            cache[r] = count;
            return count;
        }

        static bool IsConstraintType(Type t)
        {
            if (t == null) return false;
            string name = t.Name;
            return name.Contains("Constraint") &&
                   (name.StartsWith("VRC") || t.Namespace?.Contains("Dynamics.Constraint") == true ||
                    t.Namespace?.Contains("UnityEngine.Animations") == true);
        }

        static Component GetAnyConstraint(Transform t, Type originalType)
        {
            // Try VRC constraint types
            var components = t.GetComponents<Component>();
            foreach (var c in components)
            {
                if (c == null) continue;
                var cType = c.GetType();
                if (IsConstraintType(cType)) return c;
            }
            return null;
        }

        static HashSet<AnimationClip> CollectClips(AnimatorController controller)
        {
            var clips = new HashSet<AnimationClip>();
            foreach (var layer in controller.layers)
            {
                if (layer.stateMachine == null) continue;
                CollectClipsFromSM(layer.stateMachine, clips);
            }
            return clips;
        }

        static void CollectClipsFromSM(AnimatorStateMachine sm, HashSet<AnimationClip> clips)
        {
            foreach (var state in sm.states)
            {
                if (state.state.motion is AnimationClip clip)
                    clips.Add(clip);
                else if (state.state.motion is BlendTree bt)
                    CollectClipsFromBT(bt, clips);
            }
            foreach (var sub in sm.stateMachines)
                CollectClipsFromSM(sub.stateMachine, clips);
        }

        static void CollectClipsFromBT(BlendTree bt, HashSet<AnimationClip> clips)
        {
            foreach (var child in bt.children)
            {
                if (child.motion is AnimationClip clip)
                    clips.Add(clip);
                else if (child.motion is BlendTree childBt)
                    CollectClipsFromBT(childBt, clips);
            }
        }

        #endregion

        #region C. Material Property Validation

        static (int errors, int warnings) CheckMaterialProperties(
            List<ControllerInfo> controllers, GameObject avatarRoot, StringBuilder sb)
        {
            int errors = 0, warnings = 0;
            var checkedProps = new HashSet<string>(); // deduplicate: "path|propertyName"

            foreach (var ci in controllers)
            {
                foreach (var clip in CollectClips(ci.Controller))
                {
                    if (clip == null) continue;

                    var bindings = AnimationUtility.GetCurveBindings(clip);
                    var objBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);

                    foreach (var binding in bindings.Concat(objBindings))
                    {
                        // Material property bindings look like "material._PropertyName"
                        // or "material._PropertyName.r" (color channels)
                        if (binding.propertyName == null) continue;
                        if (!binding.propertyName.StartsWith("material.")) continue;
                        if (binding.type == null) continue;
                        if (!typeof(Renderer).IsAssignableFrom(binding.type)) continue;

                        // Extract base property name (strip "material." prefix and any .r/.g/.b/.a/.x/.y/.z/.w suffix)
                        string matProp = binding.propertyName.Substring("material.".Length);
                        // Strip channel suffix for color/vector properties
                        int dotIdx = matProp.LastIndexOf('.');
                        if (dotIdx > 0)
                        {
                            string suffix = matProp.Substring(dotIdx + 1);
                            if (suffix.Length == 1 && "rgbaxyzw".Contains(suffix))
                                matProp = matProp.Substring(0, dotIdx);
                        }

                        string dedupeKey = $"{binding.path}|{matProp}";
                        if (!checkedProps.Add(dedupeKey)) continue;

                        if (string.IsNullOrEmpty(binding.path)) continue;

                        var resolved = avatarRoot.transform.Find(binding.path);
                        if (resolved == null) continue; // Path errors already reported in section B

                        var renderer = resolved.GetComponent<Renderer>();
                        if (renderer == null) continue;

                        var materials = renderer.sharedMaterials;
                        if (materials == null || materials.Length == 0) continue;

                        bool foundOnAny = false;
                        bool anyPoiyomi = false;

                        foreach (var mat in materials)
                        {
                            if (mat == null) continue;
                            if (mat.HasProperty(matProp))
                            {
                                foundOnAny = true;
                                break;
                            }
                            if (mat.shader != null && mat.shader.name.IndexOf("poiyomi",
                                StringComparison.OrdinalIgnoreCase) >= 0)
                                anyPoiyomi = true;
                            // Also check locked Poiyomi shaders
                            if (mat.shader != null && mat.shader.name.StartsWith("Hidden/Locked/") &&
                                mat.shader.name.IndexOf("poiyomi", StringComparison.OrdinalIgnoreCase) >= 0)
                                anyPoiyomi = true;
                        }

                        if (!foundOnAny)
                        {
                            string hint = anyPoiyomi
                                ? " (Poiyomi shader: property was likely compiled out during lock — ensure AnimatedTag is set before build)"
                                : "";
                            sb.AppendLine($"  [ERROR] Material property \"{matProp}\" not found on any material on \"{binding.path}\" (clip: \"{clip.name}\"){hint}");
                            errors++;
                        }
                    }
                }
            }

            if (errors == 0 && warnings == 0)
                sb.AppendLine("  (all material properties valid)");

            return (errors, warnings);
        }

        #endregion

        #region Menu Item

        [MenuItem("Tools/Avatar Type Check")]
        static void RunFromMenu()
        {
            // Try selected object first
            var selected = Selection.activeGameObject;
            if (selected != null)
            {
                var desc = selected.GetComponentInParent<VRCAvatarDescriptor>()
                        ?? selected.GetComponentInChildren<VRCAvatarDescriptor>();
                if (desc != null)
                {
                    Debug.Log(ValidateBuiltAvatar(GetHierarchyPath(desc.transform)));
                    return;
                }
            }

            // No fallback auto-find: require explicit path
            Debug.Log(ValidateBuiltAvatar());
        }

        #endregion
    }
}
