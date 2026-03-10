using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using Microsoft.CSharp;
using UnityEditor;
using UnityEngine;

namespace SentFromSpace.AgenticTools.Editor
{
    [InitializeOnLoad]
    public static class UnityMcpBridge
    {
        const int Port = 14523;
        const string Prefix = "http://127.0.0.1:14523/mcp/";
        const int ExecutionTimeoutMs = 30000;
        const string ProtocolVersion = "2025-03-26";

        static HttpListener s_Listener;
        static Thread s_ListenerThread;
        static volatile bool s_Running;
        static string s_SessionId;
        static readonly object s_Lock = new object();
        static readonly Queue<WorkItem> s_WorkQueue = new Queue<WorkItem>();
        static string[] s_CachedAssemblyPaths;
        static int s_CachedAssemblyCount;

        // Template lines before user code (for adjusting error line numbers)
        // Template: 1 blank + 8 usings + 1 blank + class + { + method + { = 14 lines before user code
        const int TemplateLineOffset = 14;
        const string SnippetTemplate = @"
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class SnippetRunner
{
    public static string Execute()
    {
{0}
    }
}";

        class WorkItem
        {
            public string Code;
            public string Result;
            public bool IsError;
            public ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        static UnityMcpBridge()
        {
            AssemblyReloadEvents.beforeAssemblyReload += StopServer;
            EditorApplication.quitting += StopServer;
            EditorApplication.update += ProcessQueue;
            StartServer();
        }

        static void StartServer()
        {
            if (s_Running) return;

            try
            {
                s_Listener = new HttpListener();
                s_Listener.Prefixes.Add(Prefix);
                s_Listener.Start();
                s_Running = true;
                s_SessionId = null;
                s_CachedAssemblyPaths = null;

                s_ListenerThread = new Thread(ListenerLoop)
                {
                    IsBackground = true,
                    Name = "MCP-Listener"
                };
                s_ListenerThread.Start();
                Debug.Log($"[MCP] Listening on {Prefix}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[MCP] Failed to start: {e.Message}");
            }
        }

        static void StopServer()
        {
            s_Running = false;
            try { s_Listener?.Stop(); } catch { }
            s_Listener = null;
            s_SessionId = null;
            s_CachedAssemblyPaths = null;
            s_CachedAssemblyCount = 0;
        }

        static void ListenerLoop()
        {
            while (s_Running)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = s_Listener.GetContext();
                }
                catch
                {
                    break;
                }

                try
                {
                    HandleRequest(ctx);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[MCP] Request error: {e}");
                    try
                    {
                        SendJsonResponse(ctx.Response, 500,
                            MakeErrorResponse(null, -32603, "Internal error"));
                    }
                    catch { }
                }
            }
        }

        static void HandleRequest(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var resp = ctx.Response;

            if (req.HttpMethod != "POST")
            {
                resp.StatusCode = 405;
                resp.Close();
                return;
            }

            string body;
            using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
                body = reader.ReadToEnd();

            string method = ExtractJsonString(body, "method");
            string rawId = ExtractRawJsonValue(body, "id");

            // Handle initialize — no session required
            if (method == "initialize")
            {
                s_SessionId = Guid.NewGuid().ToString("N");
                resp.Headers.Set("Mcp-Session-Id", s_SessionId);
                SendJsonResponse(resp, 200, MakeInitializeResponse(rawId));
                return;
            }

            // Handle notifications (no id)
            if (method != null && method.StartsWith("notifications/"))
            {
                resp.StatusCode = 202;
                resp.Close();
                return;
            }

            // All other methods require valid session
            string clientSession = req.Headers["Mcp-Session-Id"];
            if (string.IsNullOrEmpty(clientSession))
            {
                SendJsonResponse(resp, 400,
                    MakeErrorResponse(rawId, -32600, "Missing Mcp-Session-Id header"));
                return;
            }
            if (clientSession != s_SessionId)
            {
                // Accept stale session — reassign so client can continue after recompile
                s_SessionId = clientSession;
            }

            resp.Headers.Set("Mcp-Session-Id", s_SessionId);

            switch (method)
            {
                case "ping":
                    SendJsonResponse(resp, 200,
                        "{\"jsonrpc\":\"2.0\",\"id\":" + Id(rawId) + ",\"result\":{}}");
                    break;

                case "tools/list":
                    SendJsonResponse(resp, 200, MakeToolsListResponse(rawId));
                    break;

                case "tools/call":
                    HandleToolCall(resp, body, rawId);
                    break;

                default:
                    SendJsonResponse(resp, 200,
                        MakeErrorResponse(rawId, -32601, $"Unknown method: {method}"));
                    break;
            }
        }

        static void HandleToolCall(HttpListenerResponse resp, string body, string rawId)
        {
            string toolName = ExtractNestedJsonString(body, "params", "name");
            if (toolName != "execute_csharp")
            {
                SendJsonResponse(resp, 200, MakeToolResult(rawId,
                    $"Unknown tool: {toolName}", true));
                return;
            }

            // Extract code from params.arguments.code
            string arguments = ExtractRawJsonValue(
                ExtractRawJsonObject(body, "params"), "arguments");
            string code = (arguments != null) ? ExtractJsonString(arguments, "code") : null;

            if (string.IsNullOrEmpty(code))
            {
                SendJsonResponse(resp, 200, MakeToolResult(rawId,
                    "Missing 'code' argument", true));
                return;
            }

            // Dispatch to main thread
            var item = new WorkItem { Code = code };
            lock (s_Lock)
                s_WorkQueue.Enqueue(item);

            if (item.Done.Wait(ExecutionTimeoutMs))
            {
                SendJsonResponse(resp, 200,
                    MakeToolResult(rawId, item.Result, item.IsError));
            }
            else
            {
                SendJsonResponse(resp, 504,
                    MakeToolResult(rawId, "Execution timed out (30s)", true));
            }
        }

        static void ProcessQueue()
        {
            WorkItem item;
            lock (s_Lock)
            {
                if (s_WorkQueue.Count == 0) return;
                item = s_WorkQueue.Dequeue();
            }

            try
            {
                ExecuteSnippet(item);
            }
            catch (Exception e)
            {
                item.Result = $"Internal error: {e.Message}";
                item.IsError = true;
            }
            finally
            {
                item.Done.Set();
            }
        }

        static void ExecuteSnippet(WorkItem item)
        {
            // Indent user code to fit inside the method body
            string indented = "        " + item.Code.Replace("\n", "\n        ");
            string source = SnippetTemplate.Replace("{0}", indented);

            var provider = new CSharpCodeProvider();
            var parameters = new CompilerParameters
            {
                GenerateInMemory = true,
                GenerateExecutable = false,
                TreatWarningsAsErrors = false,
                // Prevent mcs from auto-referencing mscorlib — we provide all assemblies explicitly
                CompilerOptions = "/nostdlib"
            };

            // Add assembly references (invalidate cache if assembly count changed)
            var currentAssemblyCount = AppDomain.CurrentDomain.GetAssemblies().Length;
            if (s_CachedAssemblyPaths == null || s_CachedAssemblyCount != currentAssemblyCount)
            {
                s_CachedAssemblyPaths = CollectAssemblyPaths();
                s_CachedAssemblyCount = currentAssemblyCount;
            }
            foreach (var path in s_CachedAssemblyPaths)
                parameters.ReferencedAssemblies.Add(path);

            CompilerResults results = provider.CompileAssemblyFromSource(parameters, source);

            if (results.Errors.HasErrors)
            {
                var sb = new StringBuilder("Compilation failed:\n");
                bool hasTypeConflict = false;
                foreach (CompilerError err in results.Errors)
                {
                    if (err.IsWarning) continue;
                    int adjustedLine = err.Line - TemplateLineOffset;
                    sb.AppendLine($"  Line {adjustedLine}: {err.ErrorText}");
                    if (err.ErrorText.Contains("defined multiple times") ||
                        err.ErrorText.Contains("is not referenced"))
                        hasTypeConflict = true;
                }
                if (hasTypeConflict)
                {
                    Debug.LogWarning("[MCP] Assembly reference conflict detected. Referenced assemblies:\n" +
                        string.Join("\n", s_CachedAssemblyPaths));
                    // Invalidate cache so next attempt rebuilds with fresh filtering
                    s_CachedAssemblyPaths = null;
                }
                item.Result = sb.ToString();
                item.IsError = true;
                return;
            }

            try
            {
                var assembly = results.CompiledAssembly;
                var type = assembly.GetType("SnippetRunner");
                var method = type.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static);
                var result = method.Invoke(null, null);
                item.Result = result as string ?? "(null)";
                item.IsError = false;
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException ?? tie;
                item.Result = $"Runtime error: {inner.GetType().Name}: {inner.Message}\n{inner.StackTrace}";
                item.IsError = true;
            }
            catch (Exception e)
            {
                item.Result = $"Runtime error: {e.GetType().Name}: {e.Message}";
                item.IsError = true;
            }
        }

        static string[] CollectAssemblyPaths()
        {
            var paths = new List<string>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic) continue;
                try
                {
                    var loc = asm.Location;
                    if (string.IsNullOrEmpty(loc) || !File.Exists(loc)) continue;

                    var name = asm.GetName().Name;

                    // Skip facade assemblies that re-export types (cause duplicate type defs)
                    // Keep netstandard — Unity assemblies target it, needed with /nostdlib
                    var normalizedLoc = loc.Replace('\\', '/');
                    if (normalizedLoc.Contains("/Facades/") && name != "netstandard") continue;
                    if (normalizedLoc.Contains("/NetCoreRuntime/")) continue;
                    if (normalizedLoc.Contains("/NetStandard/")) continue;
                    if (normalizedLoc.Contains("/il2cpp/")) continue;

                    // Skip duplicate assembly names (keep first encountered)
                    if (!seenNames.Add(name)) continue;

                    paths.Add(loc);
                }
                catch { }
            }
            return paths.ToArray();
        }

        // ── JSON helpers (minimal, no external dependencies) ────────────

        static void SendJsonResponse(HttpListenerResponse resp, int status, string json)
        {
            resp.StatusCode = status;
            resp.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(json);
            resp.ContentLength64 = bytes.Length;
            resp.OutputStream.Write(bytes, 0, bytes.Length);
            resp.Close();
        }

        static string JsonEscape(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.AppendFormat("\\u{0:x4}", (int)c);
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        const string ToolDescription =
            "Execute a C# snippet inside the Unity Editor and return the result string.\\n\\n"
            + "The snippet runs inside a static method body with these usings available:\\n"
            + "System, System.Collections.Generic, System.IO, System.Linq, System.Text,\\n"
            + "UnityEditor, UnityEngine, UnityEngine.SceneManagement\\n\\n"
            + "All loaded assemblies are referenced (Unity, VRC SDK, VRCFury, project scripts).\\n\\n"
            + "The snippet MUST end with: return \\\"some string\\\";\\n\\n"
            + "Examples:\\n"
            + "  return Application.unityVersion;\\n"
            + "  return AssetDatabase.FindAssets(\\\"t:Model\\\").Length.ToString();";

        static string Id(string rawId) => rawId ?? "null";

        static string MakeInitializeResponse(string rawId)
        {
            return "{\"jsonrpc\":\"2.0\",\"id\":" + Id(rawId)
                + ",\"result\":{\"protocolVersion\":\"" + ProtocolVersion
                + "\",\"capabilities\":{\"tools\":{}}"
                + ",\"serverInfo\":{\"name\":\"unity\",\"version\":\"1.0.0\"}}}";
        }

        static string MakeToolsListResponse(string rawId)
        {
            return "{\"jsonrpc\":\"2.0\",\"id\":" + Id(rawId)
                + ",\"result\":{\"tools\":[{\"name\":\"execute_csharp\""
                + ",\"description\":\"" + ToolDescription + "\""
                + ",\"inputSchema\":{\"type\":\"object\""
                + ",\"properties\":{\"code\":{\"type\":\"string\""
                + ",\"description\":\"C# code to execute. Must return a string.\"}}"
                + ",\"required\":[\"code\"]}}]}}";
        }

        static string MakeToolResult(string rawId, string text, bool isError)
        {
            return "{\"jsonrpc\":\"2.0\",\"id\":" + Id(rawId)
                + ",\"result\":{\"content\":[{\"type\":\"text\",\"text\":\""
                + JsonEscape(text) + "\"}],\"isError\":"
                + (isError ? "true" : "false") + "}}";
        }

        static string MakeErrorResponse(string rawId, int code, string message)
        {
            return "{\"jsonrpc\":\"2.0\",\"id\":" + Id(rawId)
                + ",\"error\":{\"code\":" + code
                + ",\"message\":\"" + JsonEscape(message) + "\"}}";
        }

        // ── Minimal JSON field extraction ───────────────────────────────

        /// <summary>Extract a string value for a given key from a JSON object.</summary>
        static string ExtractJsonString(string json, string key)
        {
            string pattern = $"\"{key}\"";
            int keyIdx = json.IndexOf(pattern);
            if (keyIdx < 0) return null;

            int colonIdx = json.IndexOf(':', keyIdx + pattern.Length);
            if (colonIdx < 0) return null;

            // Find opening quote
            int start = json.IndexOf('"', colonIdx + 1);
            if (start < 0) return null;
            start++;

            // Find closing quote (handle escapes)
            var sb = new StringBuilder();
            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '\\' && i + 1 < json.Length)
                {
                    char next = json[i + 1];
                    switch (next)
                    {
                        case '"': sb.Append('"'); i++; break;
                        case '\\': sb.Append('\\'); i++; break;
                        case 'n': sb.Append('\n'); i++; break;
                        case 'r': sb.Append('\r'); i++; break;
                        case 't': sb.Append('\t'); i++; break;
                        case '/': sb.Append('/'); i++; break;
                        case 'u':
                            if (i + 5 < json.Length)
                            {
                                string hex = json.Substring(i + 2, 4);
                                sb.Append((char)Convert.ToInt32(hex, 16));
                                i += 5;
                            }
                            break;
                        default: sb.Append(c); break;
                    }
                }
                else if (c == '"')
                {
                    break;
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        /// <summary>Extract a raw JSON value (number, string with quotes, object, etc.) for a key.</summary>
        static string ExtractRawJsonValue(string json, string key)
        {
            if (json == null) return null;
            string pattern = $"\"{key}\"";
            int keyIdx = json.IndexOf(pattern);
            if (keyIdx < 0) return null;

            int colonIdx = json.IndexOf(':', keyIdx + pattern.Length);
            if (colonIdx < 0) return null;

            // Skip whitespace
            int start = colonIdx + 1;
            while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
            if (start >= json.Length) return null;

            char first = json[start];

            if (first == '"')
            {
                // String — find matching close quote
                int end = start + 1;
                while (end < json.Length)
                {
                    if (json[end] == '\\') { end += 2; continue; }
                    if (json[end] == '"') { end++; break; }
                    end++;
                }
                return json.Substring(start, end - start);
            }

            if (first == '{' || first == '[')
            {
                // Object or array — bracket matching
                char open = first;
                char close = first == '{' ? '}' : ']';
                int depth = 1;
                int end = start + 1;
                bool inStr = false;
                while (end < json.Length && depth > 0)
                {
                    char c = json[end];
                    if (inStr)
                    {
                        if (c == '\\') { end++; }
                        else if (c == '"') { inStr = false; }
                    }
                    else
                    {
                        if (c == '"') inStr = true;
                        else if (c == open) depth++;
                        else if (c == close) depth--;
                    }
                    end++;
                }
                return json.Substring(start, end - start);
            }

            // Number, bool, null
            int valEnd = start;
            while (valEnd < json.Length && json[valEnd] != ',' && json[valEnd] != '}'
                   && json[valEnd] != ']' && !char.IsWhiteSpace(json[valEnd]))
                valEnd++;
            return json.Substring(start, valEnd - start);
        }

        /// <summary>Extract a raw JSON object value for a key, then extract a string from it.</summary>
        static string ExtractNestedJsonString(string json, string outerKey, string innerKey)
        {
            string inner = ExtractRawJsonValue(json, outerKey);
            if (inner == null) return null;
            return ExtractJsonString(inner, innerKey);
        }

        /// <summary>Extract a raw JSON object (braces) for a key.</summary>
        static string ExtractRawJsonObject(string json, string key)
        {
            return ExtractRawJsonValue(json, key);
        }
    }
}
