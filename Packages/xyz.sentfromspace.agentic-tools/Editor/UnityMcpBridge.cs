using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
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
        const int DefaultPort = 14523;
        static int s_Port;
        static string s_Prefix;
        static volatile bool s_Quitting;
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

            // 0 = Pending, 1 = Running, 2 = Cancelled
            const int Pending = 0, Running = 1, Cancelled = 2;
            int _state;

            /// <summary>Atomically transition Pending→Running. Returns true if this side won.</summary>
            public bool TryClaimForExecution()
                => Interlocked.CompareExchange(ref _state, Running, Pending) == Pending;

            /// <summary>Atomically transition Pending→Cancelled. Returns true if this side won.</summary>
            public bool TryCancel()
                => Interlocked.CompareExchange(ref _state, Cancelled, Pending) == Pending;
        }

        static string PortFilePath => Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library", "MCP_PORT");
        static string ProxyExePath => Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library", "mcp-proxy.exe");

        // Version stamp embedded in compiled proxy -- bump to force recompile
        const string ProxyVersion = "2";

        const string ProxySource = @"
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

class McpProxy
{
    const string Version = """ + ProxyVersion + @""";
    const int MaxRetries = 40;       // 40 x 1.5s = 60s max wait for domain reload
    const int RetryDelayMs = 1500;

    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == ""--version"")
        {
            Console.WriteLine(Version);
            return 0;
        }

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string portFile = Path.Combine(baseDir, ""MCP_PORT"");

        var stderr = Console.Error;
        var stdout = Console.OpenStandardOutput();
        var stdin = Console.OpenStandardInput();

        string sessionId = null;

        while (true)
        {
            // Read one JSON-RPC message from stdin (newline-delimited)
            string line;
            try
            {
                line = ReadLine(stdin);
            }
            catch
            {
                break;
            }
            if (line == null) break;
            if (line.Trim().Length == 0) continue;

            // Retry loop: Unity may be unreachable during domain reloads
            // (Play Mode transitions, script recompilation)
            bool handled = false;
            for (int attempt = 0; attempt <= MaxRetries; attempt++)
            {
                // Resolve port on each attempt (Unity may restart with a new port)
                string port;
                try
                {
                    port = File.ReadAllText(portFile).Trim();
                }
                catch
                {
                    if (attempt < MaxRetries)
                    {
                        Thread.Sleep(RetryDelayMs);
                        continue;
                    }
                    WriteStdout(stdout, MakeError(line, -32000, ""Unity is not running (no Library/MCP_PORT file)""));
                    handled = true;
                    break;
                }

                string url = ""http://127.0.0.1:"" + port + ""/mcp/"";

                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(url);
                    req.Method = ""POST"";
                    req.ContentType = ""application/json"";
                    req.Timeout = 60000;
                    if (sessionId != null)
                        req.Headers[""Mcp-Session-Id""] = sessionId;

                    byte[] body = Encoding.UTF8.GetBytes(line);
                    req.ContentLength = body.Length;
                    using (var s = req.GetRequestStream())
                        s.Write(body, 0, body.Length);

                    using (var resp = (HttpWebResponse)req.GetResponse())
                    {
                        string sid = resp.Headers[""Mcp-Session-Id""];
                        if (sid != null) sessionId = sid;

                        if ((int)resp.StatusCode == 202)
                        {
                            handled = true;
                            break; // notification acknowledged, no response
                        }

                        using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                        {
                            WriteStdout(stdout, reader.ReadToEnd());
                            handled = true;
                            break;
                        }
                    }
                }
                catch (WebException we)
                {
                    var httpResp = we.Response as HttpWebResponse;
                    if (httpResp != null)
                    {
                        // Got an HTTP response (real error, not a connection failure)
                        using (var reader = new StreamReader(httpResp.GetResponseStream(), Encoding.UTF8))
                        {
                            WriteStdout(stdout, reader.ReadToEnd());
                            handled = true;
                            break;
                        }
                    }

                    // Connection refused / timed out -- Unity is likely reloading
                    if (attempt < MaxRetries)
                    {
                        if (attempt == 0)
                            stderr.WriteLine(""[mcp-proxy] Unity unreachable, waiting for domain reload..."");
                        Thread.Sleep(RetryDelayMs);
                        continue;
                    }

                    WriteStdout(stdout, MakeError(line, -32000, ""Cannot reach Unity at "" + url + "" after "" + (MaxRetries + 1) + "" attempts: "" + we.Message));
                    handled = true;
                    break;
                }
                catch (Exception ex)
                {
                    WriteStdout(stdout, MakeError(line, -32603, ex.Message));
                    handled = true;
                    break;
                }
            }

            if (!handled)
            {
                WriteStdout(stdout, MakeError(line, -32000, ""Unity is not running""));
            }
        }
        return 0;
    }

    static string ReadLine(Stream stdin)
    {
        var sb = new StringBuilder();
        int b;
        while ((b = stdin.ReadByte()) >= 0)
        {
            if (b == '\n') return sb.ToString();
            if (b != '\r') sb.Append((char)b);
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    static void WriteStdout(Stream stdout, string msg)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(msg + ""\n"");
        stdout.Write(bytes, 0, bytes.Length);
        stdout.Flush();
    }

    static string MakeError(string request, int code, string message)
    {
        // Extract id from request
        string id = ""null"";
        int idx = request.IndexOf(""\""id\"""");
        if (idx >= 0)
        {
            int colon = request.IndexOf(':', idx + 4);
            if (colon >= 0)
            {
                int start = colon + 1;
                while (start < request.Length && request[start] == ' ') start++;
                int end = start;
                while (end < request.Length && request[end] != ',' && request[end] != '}') end++;
                id = request.Substring(start, end - start).Trim();
            }
        }
        message = message.Replace(""\\"", ""\\\\"").Replace(""\"""", ""\\\"""");
        return ""{\""jsonrpc\"":\""2.0\"",\""id\"":"" + id + "",\""error\"":{\""code\"":"" + code + "",\""message\"":"" + ""\"""" + message + ""\"""" + ""}}"";
    }
}
";

        static UnityMcpBridge()
        {
            // Asset Import Workers are separate processes that also run
            // [InitializeOnLoad] — don't start MCP in them or they'll
            // overwrite Library/MCP_PORT with their own ephemeral port.
            if (AssetDatabase.IsAssetImportWorkerProcess())
                return;

            AssemblyReloadEvents.beforeAssemblyReload += StopServer;
            EditorApplication.quitting += OnQuitting;
            EditorApplication.update += ProcessQueue;
            StartServer();
        }

        static void StartServer()
        {
            if (s_Running) return;

            try
            {
                // Try ports in order: sticky port from last session, default, then OS-assigned
                if (!TryStartWithStickyPort() && !TryStartListener(DefaultPort))
                {
                    // All preferred ports taken -- let OS assign one
                    var tcp = new TcpListener(IPAddress.Loopback, 0);
                    tcp.Start();
                    s_Port = ((IPEndPoint)tcp.LocalEndpoint).Port;
                    tcp.Stop();
                    if (!TryStartListener(s_Port))
                    {
                        Debug.LogError("[MCP] Failed to start: could not bind to any port");
                        return;
                    }
                }

                s_Prefix = $"http://127.0.0.1:{s_Port}/mcp/";
                s_Running = true;
                s_SessionId = null;
                s_CachedAssemblyPaths = null;

                try { File.WriteAllText(PortFilePath, s_Port.ToString()); }
                catch (Exception e) { Debug.LogWarning($"[MCP] Could not write port file: {e.Message}"); }

                s_ListenerThread = new Thread(ListenerLoop)
                {
                    IsBackground = true,
                    Name = "MCP-Listener"
                };
                s_ListenerThread.Start();
                Debug.Log($"[MCP] Listening on {s_Prefix}");

                try { CompileProxy(); }
                catch (Exception e) { Debug.LogWarning($"[MCP] Could not compile stdio proxy: {e.Message}"); }

                try { UpdateMcpConfigs(s_Port); }
                catch (Exception e) { Debug.LogWarning($"[MCP] Could not update config files: {e.Message}\n{e.StackTrace}"); }
            }
            catch (Exception e)
            {
                Debug.LogError($"[MCP] Failed to start: {e.Message}");
            }
        }

        static bool TryStartListener(int port)
        {
            try
            {
                s_Listener = new HttpListener();
                s_Listener.Prefixes.Add($"http://127.0.0.1:{port}/mcp/");
                s_Listener.Start();
                s_Port = port;
                return true;
            }
            catch
            {
                try { s_Listener?.Close(); } catch { }
                s_Listener = null;
                return false;
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

            if (s_Quitting)
            {
                try { File.Delete(PortFilePath); } catch { }
            }
        }

        static void OnQuitting()
        {
            s_Quitting = true;
            StopServer();
        }

        // ── Stdio proxy compilation ─────────────────────────────────────

        static void CompileProxy()
        {
            // Check if existing proxy is current version
            if (File.Exists(ProxyExePath))
            {
                try
                {
                    var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(ProxyExePath);
                    // Re-check by running --version (lightweight)
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = ProxyExePath,
                        Arguments = "--version",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    };
                    using (var proc = System.Diagnostics.Process.Start(psi))
                    {
                        string output = proc.StandardOutput.ReadToEnd().Trim();
                        proc.WaitForExit(3000);
                        if (output == ProxyVersion)
                            return; // Already up to date
                    }
                }
                catch { } // Fall through to recompile
            }

            var provider = new CSharpCodeProvider();
            var parameters = new CompilerParameters
            {
                GenerateExecutable = true,
                OutputAssembly = ProxyExePath,
                GenerateInMemory = false,
                TreatWarningsAsErrors = false,
                CompilerOptions = "/optimize /platform:anycpu"
            };
            parameters.ReferencedAssemblies.Add("System.dll");

            CompilerResults results = provider.CompileAssemblyFromSource(parameters, ProxySource);
            if (results.Errors.HasErrors)
            {
                var sb = new StringBuilder("[MCP] Failed to compile stdio proxy:\n");
                foreach (CompilerError err in results.Errors)
                    if (!err.IsWarning) sb.AppendLine($"  Line {err.Line}: {err.ErrorText}");
                Debug.LogError(sb.ToString());
            }
            else
            {
                Debug.Log($"[MCP] Compiled stdio proxy to {ProxyExePath}");
            }
        }

        // ── Port discovery ──────────────────────────────────────────────

        static bool TryStartWithStickyPort()
        {
            try
            {
                if (File.Exists(PortFilePath))
                {
                    string content = File.ReadAllText(PortFilePath).Trim();
                    if (int.TryParse(content, out int stickyPort) && stickyPort > 0 && stickyPort <= 65535)
                        return TryStartListener(stickyPort);
                }
            }
            catch { }
            return false;
        }

        // ── MCP config file management ──────────────────────────────────

        internal static void RegenerateMcpConfigs()
        {
            if (s_Port == 0)
            {
                Debug.LogWarning("[MCP] Server is not running, cannot regenerate configs.");
                return;
            }
            UpdateMcpConfigs(s_Port);
            Debug.Log("[MCP] Regenerated MCP config files.");
        }

        static void UpdateMcpConfigs(int port)
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath);

            // .mcp.json (VS Code, Claude Code, other agents) -- uses "mcpServers" key
            string mcpJsonPath = Path.Combine(projectRoot, ".mcp.json");
            if (File.Exists(ProxyExePath))
                UpdateStdioConfigFile(mcpJsonPath, "mcpServers");
            else
                UpdateConfigFile(mcpJsonPath, "mcpServers", port);

            // .codex/config.toml (Codex) -- only if .codex/ directory exists
            string codexDir = Path.Combine(projectRoot, ".codex");
            if (Directory.Exists(codexDir))
            {
                string codexPath = Path.Combine(codexDir, "config.toml");
                if (File.Exists(ProxyExePath))
                    UpdateCodexConfig(codexPath, "Library/mcp-proxy.exe");
                else
                    UpdateCodexConfig(codexPath, null, port);
            }
        }

        static void UpdateStdioConfigFile(string filePath, string defaultServersKey)
        {
            // Use forward slashes and relative path for cross-platform compat in JSON
            string proxyRelative = "Library/mcp-proxy.exe";
            string unityBlock = "{\n      \"type\": \"stdio\",\n      \"command\": \"" + proxyRelative + "\"\n    }";

            if (!File.Exists(filePath))
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(filePath,
                    "{\n  \"" + defaultServersKey + "\": {\n    \"unity\": " + unityBlock + "\n  }\n}\n");
                Debug.Log($"[MCP] Created {filePath} (stdio)");
                return;
            }

            string text = File.ReadAllText(filePath);

            // Already up to date?
            if (text.Contains(proxyRelative)) return;

            // Determine which servers key the file uses
            string serversKey = null;
            if (FindJsonKey(text, "mcpServers") >= 0) serversKey = "mcpServers";
            else if (FindJsonKey(text, "servers") >= 0) serversKey = "servers";

            // Reuse the same merge logic as HTTP config
            int unityKeyIdx = FindJsonKey(text, "unity");
            if (unityKeyIdx >= 0)
            {
                int colonIdx = text.IndexOf(':', unityKeyIdx + "\"unity\"".Length);
                if (colonIdx >= 0)
                {
                    int openBrace = text.IndexOf('{', colonIdx);
                    if (openBrace >= 0)
                    {
                        int closeBrace = FindMatchingBrace(text, openBrace);
                        if (closeBrace >= 0)
                        {
                            text = text.Substring(0, openBrace) + unityBlock + text.Substring(closeBrace + 1);
                            File.WriteAllText(filePath, text);
                            return;
                        }
                    }
                }
            }

            if (serversKey != null)
            {
                int sKeyIdx = FindJsonKey(text, serversKey);
                int sColon = text.IndexOf(':', sKeyIdx + serversKey.Length + 2);
                if (sColon >= 0)
                {
                    int sOpen = text.IndexOf('{', sColon);
                    if (sOpen >= 0)
                    {
                        int sClose = FindMatchingBrace(text, sOpen);
                        if (sClose >= 0)
                        {
                            int insertPos = sClose;
                            while (insertPos > sOpen + 1 && char.IsWhiteSpace(text[insertPos - 1]))
                                insertPos--;
                            bool hasEntries = text.Substring(sOpen + 1, insertPos - sOpen - 1).Trim().Length > 0;
                            string comma = hasEntries ? "," : "";
                            text = text.Substring(0, insertPos) + comma
                                + "\n    \"unity\": " + unityBlock + "\n  " + text.Substring(sClose);
                            File.WriteAllText(filePath, text);
                            return;
                        }
                    }
                }
            }

            Debug.LogWarning($"[MCP] Could not update {filePath} - unrecognized format");
        }

        static void UpdateConfigFile(string filePath, string defaultServersKey, int port)
        {
            string url = $"http://127.0.0.1:{port}/mcp/";
            string unityBlock = "{\n      \"type\": \"http\",\n      \"url\": \"" + url + "\"\n    }";

            if (!File.Exists(filePath))
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(filePath,
                    "{\n  \"" + defaultServersKey + "\": {\n    \"unity\": " + unityBlock + "\n  }\n}\n");
                Debug.Log($"[MCP] Created {filePath}");
                return;
            }

            string text = File.ReadAllText(filePath);

            // Already up to date?
            if (text.Contains(url))
            {
                Debug.Log($"[MCP] {filePath} already up to date");
                return;
            }

            // Determine which servers key the file uses
            string serversKey = null;
            if (FindJsonKey(text, "mcpServers") >= 0) serversKey = "mcpServers";
            else if (FindJsonKey(text, "servers") >= 0) serversKey = "servers";

            // Has "unity" key (as a JSON key, not value)? Replace its value block
            int unityKeyIdx = FindJsonKey(text, "unity");
            if (unityKeyIdx >= 0)
            {
                int colonIdx = text.IndexOf(':', unityKeyIdx + "\"unity\"".Length);
                if (colonIdx >= 0)
                {
                    int openBrace = text.IndexOf('{', colonIdx);
                    if (openBrace >= 0)
                    {
                        int closeBrace = FindMatchingBrace(text, openBrace);
                        if (closeBrace >= 0)
                        {
                            text = text.Substring(0, openBrace) + unityBlock + text.Substring(closeBrace + 1);
                            File.WriteAllText(filePath, text);
                            Debug.Log($"[MCP] Updated {filePath}");
                            return;
                        }
                    }
                }
            }

            // Has servers key but no "unity"? Insert
            if (serversKey != null)
            {
                int sKeyIdx = FindJsonKey(text, serversKey);
                int sColon = text.IndexOf(':', sKeyIdx + serversKey.Length + 2);
                if (sColon >= 0)
                {
                    int sOpen = text.IndexOf('{', sColon);
                    if (sOpen >= 0)
                    {
                        int sClose = FindMatchingBrace(text, sOpen);
                        if (sClose >= 0)
                        {
                            // Insert before closing brace, after last entry
                            int insertPos = sClose;
                            while (insertPos > sOpen + 1 && char.IsWhiteSpace(text[insertPos - 1]))
                                insertPos--;
                            bool hasEntries = text.Substring(sOpen + 1, insertPos - sOpen - 1).Trim().Length > 0;
                            string comma = hasEntries ? "," : "";
                            text = text.Substring(0, insertPos) + comma
                                + "\n    \"unity\": " + unityBlock + "\n  " + text.Substring(sClose);
                            File.WriteAllText(filePath, text);
                            Debug.Log($"[MCP] Updated {filePath} (inserted unity entry)");
                            return;
                        }
                    }
                }
            }

            Debug.LogWarning($"[MCP] Could not update {filePath} - unrecognized format. Set unity server URL to {url}");
        }

        static void UpdateCodexConfig(string filePath, string proxyCommand, int port = 0)
        {
            string sectionHeader = "[mcp_servers.unity]";
            string newSection;
            if (proxyCommand != null)
                newSection = sectionHeader + "\ncommand = \"" + proxyCommand + "\"";
            else
                newSection = sectionHeader + "\nurl = \"http://127.0.0.1:" + port + "/mcp/\"";

            if (!File.Exists(filePath))
            {
                File.WriteAllText(filePath, newSection + "\n");
                Debug.Log($"[MCP] Created {filePath}");
                return;
            }

            string text = File.ReadAllText(filePath);

            // Already up to date?
            if (proxyCommand != null && text.Contains(proxyCommand)) return;
            if (proxyCommand == null && text.Contains($"http://127.0.0.1:{port}/mcp/")) return;

            // Find and replace existing [mcp_servers.unity] section
            int sectionIdx = text.IndexOf(sectionHeader);
            if (sectionIdx >= 0)
            {
                // Find end of section: next [section] header or end of file
                int nextSection = text.IndexOf("\n[", sectionIdx + sectionHeader.Length);
                int sectionEnd = nextSection >= 0 ? nextSection : text.Length;
                // Trim trailing whitespace before next section
                while (sectionEnd > sectionIdx && sectionEnd - 1 >= 0 && text[sectionEnd - 1] == '\n')
                    sectionEnd--;
                text = text.Substring(0, sectionIdx) + newSection + text.Substring(sectionEnd);
                File.WriteAllText(filePath, text);
                Debug.Log($"[MCP] Updated {filePath}");
                return;
            }

            // No existing unity section -- append
            if (!text.EndsWith("\n")) text += "\n";
            text += "\n" + newSection + "\n";
            File.WriteAllText(filePath, text);
            Debug.Log($"[MCP] Updated {filePath} (appended unity section)");
        }

        /// <summary>Find a JSON key (followed by ':') in text. Returns index or -1.</summary>
        static int FindJsonKey(string text, string key, int searchFrom = 0)
        {
            string pattern = "\"" + key + "\"";
            int idx = searchFrom;
            while (idx < text.Length)
            {
                idx = text.IndexOf(pattern, idx);
                if (idx < 0) return -1;
                int afterKey = idx + pattern.Length;
                while (afterKey < text.Length && char.IsWhiteSpace(text[afterKey])) afterKey++;
                if (afterKey < text.Length && text[afterKey] == ':')
                    return idx;
                idx += pattern.Length;
            }
            return -1;
        }

        /// <summary>Find the closing '}' that matches the opening '{' at the given index.</summary>
        static int FindMatchingBrace(string text, int openBraceIdx)
        {
            int depth = 1;
            bool inString = false;
            for (int i = openBraceIdx + 1; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"') inString = false;
                }
                else
                {
                    if (c == '"') inString = true;
                    else if (c == '{') depth++;
                    else if (c == '}') { depth--; if (depth == 0) return i; }
                }
            }
            return -1;
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
                item.TryCancel();
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

            if (!item.TryClaimForExecution())
            {
                // HTTP thread already cancelled this item — skip execution
                item.Done.Set();
                return;
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
            string projectName = JsonEscape(Path.GetFileName(Path.GetDirectoryName(Application.dataPath)));
            return "{\"jsonrpc\":\"2.0\",\"id\":" + Id(rawId)
                + ",\"result\":{\"protocolVersion\":\"" + ProtocolVersion
                + "\",\"capabilities\":{\"tools\":{}}"
                + ",\"serverInfo\":{\"name\":\"unity-" + projectName + "\",\"version\":\"1.0.0\"}}}";
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
