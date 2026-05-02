using System.IO;
using UnityEditor;
using UnityEngine;

namespace SentFromSpace.AgenticTools.Editor
{
    static class SkillGenerator
    {
        const string SkillName = "unity-workflow";
        const string MenuBase = "Tools/VRChat Agentic Tools/";

        [MenuItem(MenuBase + "Generate Skill (.claude)", false, 100)]
        static void GenerateClaude() => Generate(".claude/skills");

        [MenuItem(MenuBase + "Generate Skill (.agents)", false, 101)]
        static void GenerateAgents() => Generate(".agents/skills");

        [MenuItem(MenuBase + "Generate Skill (Both)", false, 102)]
        static void GenerateBoth()
        {
            Generate(".claude/skills");
            Generate(".agents/skills");
        }

        [MenuItem(MenuBase + "Regenerate MCP Config", false, 200)]
        static void RegenerateMcpConfig() => UnityMcpBridge.RegenerateMcpConfigs();

        static void Generate(string skillsRelDir)
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string templateDir = Path.GetFullPath(
                Path.Combine("Packages", "xyz.sentfromspace.agentic-tools", "Skills~", SkillName));
            string templateFile = Path.Combine(templateDir, "SKILL.md");

            if (!File.Exists(templateFile))
            {
                Debug.LogError($"[AgenticTools] Skill template not found: {templateFile}");
                return;
            }

            string content = File.ReadAllText(templateFile);
            string targetDir = Path.Combine(projectRoot, skillsRelDir, SkillName);
            string targetFile = Path.Combine(targetDir, "SKILL.md");

            if (File.Exists(targetFile) && File.ReadAllText(targetFile) == content)
            {
                Debug.Log($"[AgenticTools] Skill already up to date: {targetFile}");
                return;
            }

            Directory.CreateDirectory(targetDir);
            File.WriteAllText(targetFile, content);
            Debug.Log($"[AgenticTools] Generated skill: {targetFile}");
        }
    }
}
