// Connect another assistant (connect-any, 1.5): Semanticus writes the MCP connection into another assistant's config
// file, so the person's own Claude Desktop, Codex, Cursor or Claude Code in a terminal can drive the same model.
//
// ONE PATH for both doors. The Studio button and the connect_assistant tool both land in
// LocalEngine.ConnectAssistantAsync, which calls this class. Nothing here touches a model.
//
// What it writes is only the semanticus entry: edits are splices (AssistantConfigEdit), the first change to a file
// leaves a copy beside it (<name>.semanticus-bak), and a file that cannot be read safely is refused with a plain
// sentence and left as it was.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace Semanticus.Engine
{
    /// <summary>One assistant Semanticus can connect, as the picker shows it.</summary>
    public sealed class ConnectableAssistant
    {
        /// <summary>claude-desktop | codex | cursor | claude-code</summary>
        public string Id { get; set; }
        public string Name { get; set; }
        /// <summary>The assistant looks installed on this machine.</summary>
        public bool Found { get; set; }
        /// <summary>notFound | notConnected | connected | outdated (an entry is there but points somewhere else)</summary>
        public string State { get; set; }
        /// <summary>The file Semanticus would write, or for Claude Code in a terminal the command it would run.</summary>
        public string ConfigPath { get; set; }
        public string NextStep { get; set; }
    }

    public sealed class AssistantConnectResult
    {
        public string Assistant { get; set; }
        public string Name { get; set; }
        public bool Ok { get; set; }
        /// <summary>False when the entry was already right and nothing was written.</summary>
        public bool Changed { get; set; }
        /// <summary>One plain sentence: what happened.</summary>
        public string Message { get; set; }
        /// <summary>One plain sentence: what to do next ("Restart Claude Desktop."). Empty when the connect failed.</summary>
        public string NextStep { get; set; }
        public string ConfigPath { get; set; }
        /// <summary>The copy of the file made the first time Semanticus changed it. Null when there was none to copy.</summary>
        public string BackupPath { get; set; }
        /// <summary>One plain sentence when the Pro licence was saved for the other assistant too. Null otherwise.</summary>
        public string LicenceNote { get; set; }
    }

    /// <summary>Everything the connector reads from the machine, so a test can be any OS and any home.</summary>
    internal sealed class ConnectHost
    {
        /// <summary>linux | macos | windows</summary>
        public string Os;
        public string Home;
        public string AppData;          // Windows %APPDATA%
        public string LocalAppData;     // Windows %LOCALAPPDATA%
        public string CodexHome;        // $CODEX_HOME, or null
        public string Workspace;
        public Func<string, string> FindOnPath = _ => null;
        /// <summary>Run a program; returns the exit code and its output. Never throws for a program that ran and failed.</summary>
        public Func<string, IReadOnlyList<string>, (int code, string output)> Run = (_, __) => (-1, "not available");
        /// <summary>The program Semanticus puts in the entry. On POSIX the stable launcher when it exists, else this engine.</summary>
        public Func<string> Command;
        public Func<string[]> CommandPrefix = () => Array.Empty<string>();

        public static ConnectHost Real(string workspace)
        {
            var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return new ConnectHost
            {
                Os = os,
                Home = home,
                AppData = Environment.GetEnvironmentVariable("APPDATA"),
                LocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA"),
                CodexHome = Environment.GetEnvironmentVariable("CODEX_HOME"),
                Workspace = workspace,
                FindOnPath = name => WhichOnPath(name, os, home),
                Run = RunProgram,
                Command = () => SelfCommand(os, home).command,
                CommandPrefix = () => SelfCommand(os, home).prefix,
            };
        }

        /// <summary>The stable launcher (POSIX, written by the extension) when it is there, else this engine's own program.</summary>
        private static (string command, string[] prefix) SelfCommand(string os, string home)
        {
            if (os != "windows")
            {
                var launcher = Path.Combine(home, ".semanticus", "bin", "semanticus-mcp");
                if (File.Exists(launcher)) return (launcher, Array.Empty<string>());
            }
            var exe = EngineBroker.CurrentExecutablePath();
            if (string.IsNullOrEmpty(exe)) return (null, Array.Empty<string>());
            if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                var dll = typeof(ConnectHost).Assembly.Location;
                return (exe, string.IsNullOrEmpty(dll) ? Array.Empty<string>() : new[] { dll });
            }
            return (exe, Array.Empty<string>());
        }

        private static string WhichOnPath(string name, string os, string home)
        {
            var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
            // An assistant started by another program often has a short PATH: also look where the installers put the command.
            if (os != "windows")
                dirs.AddRange(new[] { Path.Combine(home, ".local", "bin"), Path.Combine(home, ".claude", "local"), Path.Combine(home, ".npm-global", "bin"),
                    Path.Combine(home, ".bun", "bin"), "/usr/local/bin", "/opt/homebrew/bin", "/usr/bin" });
            var exts = os == "windows" ? new[] { ".exe", ".cmd", ".bat", "" } : new[] { "" };
            foreach (var d in dirs)
                foreach (var e in exts)
                {
                    try { var p = Path.Combine(d, name + e); if (File.Exists(p)) return p; } catch { /* a bad PATH entry */ }
                }
            return null;
        }

        private static (int code, string output) RunProgram(string file, IReadOnlyList<string> args)
        {
            try
            {
                var psi = new ProcessStartInfo { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true };
                if (file.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
                {
                    psi.FileName = "cmd.exe"; psi.ArgumentList.Add("/c"); psi.ArgumentList.Add(file);
                }
                else psi.FileName = file;
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi);
                if (p == null) return (-1, "could not start");
                p.StandardInput.Close();
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(12000)) { try { p.Kill(true); } catch { } return (-1, "timed out"); }
                return (p.ExitCode, (outTask.Result + errTask.Result).Trim());
            }
            catch (Exception ex) { return (-1, ex.Message); }
        }
    }

    internal static class AssistantConnector
    {
        public static readonly string[] Ids = { "claude-desktop", "codex", "cursor", "claude-code" };
        /// <summary>Codex stops waiting for a server after ten seconds by default. Starting the engine for a folder can take longer.</summary>
        private const int CodexStartupSeconds = 30;

        public static string NameOf(string id) => id switch
        {
            "claude-desktop" => "Claude Desktop",
            "codex" => "Codex",
            "cursor" => "Cursor",
            "claude-code" => "Claude Code (terminal)",
            _ => id,
        };

        private static string NextStepOf(string id) => id switch
        {
            "claude-desktop" => "Quit Claude Desktop fully and open it again.",
            "codex" => "Start a new Codex session.",
            "cursor" => "Restart Cursor, then check that Semanticus is on in its MCP settings.",
            "claude-code" => "Start a new Claude Code session in your terminal.",
            _ => "Restart the assistant.",
        };

        /// <summary>Normalise what a person or a tool types: "Claude Desktop", "claude_desktop", "Codex CLI", "claude code".</summary>
        public static string Normalise(string assistant)
        {
            var a = (assistant ?? "").Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
            if (a.StartsWith("claude-desktop") || a == "desktop") return "claude-desktop";
            if (a.StartsWith("codex")) return "codex";
            if (a.StartsWith("cursor")) return "cursor";
            if (a.StartsWith("claude-code") || a == "claude" || a == "claudecode" || a == "claude-cli") return "claude-code";
            return a;
        }

        // ------------------------------------------------------------------------------------------- paths

        /// <summary>Every place the assistant may keep its config on this OS, best first. The first is where a new file goes.</summary>
        public static List<string> ConfigPaths(string id, ConnectHost h)
        {
            var list = new List<string>();
            switch (id)
            {
                case "claude-desktop":
                    if (h.Os == "windows")
                    {
                        if (!string.IsNullOrEmpty(h.AppData)) list.Add(Path.Combine(h.AppData, "Claude", "claude_desktop_config.json"));
                        // The Microsoft Store build keeps its app data in a package folder.
                        if (!string.IsNullOrEmpty(h.LocalAppData))
                        {
                            try
                            {
                                var pk = Path.Combine(h.LocalAppData, "Packages");
                                if (Directory.Exists(pk))
                                    foreach (var d in Directory.GetDirectories(pk, "Claude_*").OrderBy(x => x, StringComparer.Ordinal))
                                        list.Add(Path.Combine(d, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json"));
                            }
                            catch { /* unreadable: skip */ }
                        }
                    }
                    else if (h.Os == "macos") list.Add(Path.Combine(h.Home, "Library", "Application Support", "Claude", "claude_desktop_config.json"));
                    else list.Add(Path.Combine(h.Home, ".config", "Claude", "claude_desktop_config.json"));
                    break;
                case "cursor":
                    list.Add(Path.Combine(h.Home, ".cursor", "mcp.json"));
                    break;
                case "codex":
                    list.Add(Path.Combine(string.IsNullOrWhiteSpace(h.CodexHome) ? Path.Combine(h.Home, ".codex") : h.CodexHome, "config.toml"));
                    break;
                case "claude-code":
                    list.Add(Path.Combine(h.Home, ".claude.json"));
                    break;
            }
            return list;
        }

        /// <summary>The config file to write: the first one that exists, else the first one whose folder exists, else the first.</summary>
        private static string PickPath(string id, ConnectHost h)
        {
            var paths = ConfigPaths(id, h);
            return paths.FirstOrDefault(File.Exists)
                ?? paths.FirstOrDefault(p => Directory.Exists(Path.GetDirectoryName(p)))
                ?? paths.FirstOrDefault();
        }

        public static bool IsFound(string id, ConnectHost h)
        {
            var paths = ConfigPaths(id, h);
            switch (id)
            {
                case "claude-code": return h.FindOnPath("claude") != null || paths.Any(File.Exists);
                case "codex": return h.FindOnPath("codex") != null || paths.Any(p => File.Exists(p) || Directory.Exists(Path.GetDirectoryName(p)));
                default: return paths.Any(p => File.Exists(p) || Directory.Exists(Path.GetDirectoryName(p)));
            }
        }

        // ------------------------------------------------------------------------------------------- entry

        /// <summary>Command and args as the assistant should launch them, or null when this engine cannot name its own program.</summary>
        private static (string command, string[] args)? Entry(ConnectHost h)
        {
            var command = h.Command();
            if (string.IsNullOrEmpty(command)) return null;
            var args = h.CommandPrefix().Concat(new[] { "mcp", "--workspace", Path.GetFullPath(h.Workspace) }).ToArray();
            return (command, args);
        }

        private static JsonObject JsonEntry(string id, (string command, string[] args) e)
        {
            var o = new JsonObject();
            if (id == "claude-code") o["type"] = "stdio";
            o["command"] = e.command;
            o["args"] = new JsonArray(e.args.Select(a => (JsonNode)JsonValue.Create(a)).ToArray());
            if (id == "claude-code") o["env"] = new JsonObject();
            return o;
        }

        // ------------------------------------------------------------------------------------------- list

        public static ConnectableAssistant[] List(ConnectHost h)
        {
            var entry = Entry(h);
            return Ids.Select(id =>
            {
                var found = IsFound(id, h);
                var path = id == "claude-code" && h.FindOnPath("claude") != null ? "claude mcp add --scope user" : PickPath(id, h);
                return new ConnectableAssistant
                {
                    Id = id, Name = NameOf(id), Found = found, ConfigPath = path, NextStep = NextStepOf(id),
                    State = !found ? "notFound" : StateOf(id, h, entry),
                };
            }).ToArray();
        }

        private static string StateOf(string id, ConnectHost h, (string command, string[] args)? entry)
        {
            try
            {
                var path = PickPath(id, h);
                if (path == null || !File.Exists(path)) return "notConnected";
                if (id == "codex")
                {
                    var text = File.ReadAllText(path);
                    if (!AssistantConfigEdit.CodexHasServer(text)) return "notConnected";
                    var (c, a) = AssistantConfigEdit.ReadCodexServer(text);
                    return entry != null && c == entry.Value.command && a != null && a.SequenceEqual(entry.Value.args) ? "connected" : "outdated";
                }
                var cur = AssistantConfigEdit.ReadJsonServer(File.ReadAllBytes(path));
                if (cur == null) return "notConnected";
                if (entry == null) return "connected";
                var want = JsonEntry(id, entry.Value);
                // Compare what launches, not the extras another tool may have added (type, env, a timeout).
                var sameCommand = cur["command"]?.GetValue<string>() == entry.Value.command;
                var sameArgs = cur["args"] is JsonArray arr && arr.Select(x => x?.GetValue<string>()).SequenceEqual(entry.Value.args);
                _ = want;
                return sameCommand && sameArgs ? "connected" : "outdated";
            }
            catch (Exception) { return "notConnected"; }
        }

        // ------------------------------------------------------------------------------------------- connect

        public static AssistantConnectResult Connect(string assistant, ConnectHost h)
        {
            var id = Normalise(assistant);
            if (!Ids.Contains(id))
                return Fail(id, assistant, "Semanticus can connect " + string.Join(", ", Ids.Select(NameOf)) + ". Pick one of them.");
            var res = new AssistantConnectResult { Assistant = id, Name = NameOf(id) };
            if (string.IsNullOrWhiteSpace(h.Workspace))
                return Fail(id, null, "Semanticus has no folder open, so there is nothing to connect your assistant to. Open a folder first.");
            var entry = Entry(h);
            if (entry == null)
                return Fail(id, null, "Semanticus could not find its own program to put in the connection.");

            try
            {
                switch (id)
                {
                    case "codex": return ConnectCodex(res, h, entry.Value);
                    case "claude-code": return ConnectClaudeCode(res, h, entry.Value);
                    default: return ConnectJson(res, id, h, entry.Value);
                }
            }
            catch (UnauthorizedAccessException) { return Fail(id, null, "Semanticus is not allowed to write the " + NameOf(id) + " config file. Check the file's permissions."); }
            catch (IOException ex) { return Fail(id, null, "Semanticus could not write the " + NameOf(id) + " config file: " + ex.Message); }
        }

        private static AssistantConnectResult Fail(string id, string given, string message) =>
            new AssistantConnectResult { Assistant = id, Name = Ids.Contains(id) ? NameOf(id) : given, Ok = false, Message = message };

        private static AssistantConnectResult Done(AssistantConnectResult r, string path, string backup, bool changed)
        {
            r.Ok = true; r.Changed = changed; r.ConfigPath = path; r.BackupPath = backup;
            r.Message = changed ? "Connected " + r.Name + "." : r.Name + " is already connected.";
            r.NextStep = NextStepOf(r.Assistant);
            return r;
        }

        private static AssistantConnectResult ConnectJson(AssistantConnectResult r, string id, ConnectHost h, (string command, string[] args) entry)
        {
            var path = PickPath(id, h);
            if (path == null) return Fail(id, null, "Semanticus could not work out where " + r.Name + " keeps its config.");
            var target = ResolveLink(path);
            var existing = File.Exists(target) ? File.ReadAllBytes(target) : null;
            var edit = AssistantConfigEdit.SetJsonServer(existing, "config file of " + r.Name + " (" + path + ")", JsonEntry(id, entry));
            if (edit.Refusal != null) return Fail(id, null, edit.Refusal);
            if (!edit.Changed) return Done(r, path, null, false);
            var backup = BackupOnce(target);
            WriteAtomic(target, edit.Bytes);
            return Done(r, path, backup, true);
        }

        private static AssistantConnectResult ConnectCodex(AssistantConnectResult r, ConnectHost h, (string command, string[] args) entry)
        {
            var path = PickPath("codex", h);
            var target = ResolveLink(path);
            var existing = File.Exists(target) ? File.ReadAllText(target) : null;
            var edit = AssistantConfigEdit.SetCodexServer(existing, entry.command, entry.args, CodexStartupSeconds);
            if (edit.Refusal != null) return Fail("codex", null, edit.Refusal);
            if (!edit.Changed) return Done(r, path, null, false);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            var backup = BackupOnce(target);
            WriteAtomic(target, edit.Bytes);
            return Done(r, path, backup, true);
        }

        private static AssistantConnectResult ConnectClaudeCode(AssistantConnectResult r, ConnectHost h, (string command, string[] args) entry)
        {
            var file = ConfigPaths("claude-code", h).First();
            var target = ResolveLink(file);
            var existing = File.Exists(target) ? File.ReadAllBytes(target) : null;
            // Already right? Then nothing runs and nothing is written.
            var cur = AssistantConfigEdit.ReadJsonServer(existing);
            if (cur != null && cur["command"]?.GetValue<string>() == entry.command
                && cur["args"] is JsonArray arr && arr.Select(x => x?.GetValue<string>()).SequenceEqual(entry.args))
                return Done(r, file, null, false);

            var claude = h.FindOnPath("claude");
            if (claude != null)
            {
                // The command owns its own file, so it does the write. A copy goes beside the file first.
                var backup = existing != null ? BackupOnce(target) : null;
                var add = new List<string> { "mcp", "add", "--scope", "user", "semanticus", "--", entry.command };
                add.AddRange(entry.args);
                if (cur != null) h.Run(claude, new[] { "mcp", "remove", "--scope", "user", "semanticus" });
                var (code, output) = h.Run(claude, add);
                if (code != 0)
                    return Fail("claude-code", null, "Claude Code did not accept the connection" + (string.IsNullOrWhiteSpace(output) ? "." : ": " + FirstLine(output)));
                return Done(r, "claude mcp add --scope user", backup, true);
            }

            // No command on this machine's path, but the settings file is there: edit its semanticus entry directly.
            if (existing == null) return Fail("claude-code", null, "Semanticus could not find Claude Code on this machine.");
            var edit = AssistantConfigEdit.SetJsonServer(existing, "Claude Code settings file (" + file + ")", JsonEntry("claude-code", entry));
            if (edit.Refusal != null) return Fail("claude-code", null, edit.Refusal);
            if (!edit.Changed) return Done(r, file, null, false);
            var bak = BackupOnce(target);
            WriteAtomic(target, edit.Bytes);
            return Done(r, file, bak, true);
        }

        private static string FirstLine(string s) => s.Split('\n')[0].Trim();

        // ------------------------------------------------------------------------------------------- files

        /// <summary>A symlinked config (a dotfiles manager) is edited where it really lives, so the link stays a link.</summary>
        private static string ResolveLink(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                var final = fi.ResolveLinkTarget(true);
                return final?.FullName ?? path;
            }
            catch { return path; }
        }

        /// <summary>The first change to a file leaves its old content beside it. Later changes leave that copy alone.</summary>
        internal static string BackupOnce(string target)
        {
            if (!File.Exists(target)) return null;
            var backup = target + ".semanticus-bak";
            if (File.Exists(backup)) return backup;
            File.Copy(target, backup);
            return backup;
        }

        /// <summary>Write the new bytes to a sibling file, then move it over the old one, keeping the old file's permissions.</summary>
        internal static void WriteAtomic(string target, byte[] bytes)
        {
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = target + ".semanticus-tmp-" + Environment.ProcessId;
            File.WriteAllBytes(tmp, bytes);
            try
            {
                if (!OperatingSystem.IsWindows() && File.Exists(target))
                    File.SetUnixFileMode(tmp, File.GetUnixFileMode(target));
                File.Move(tmp, target, true);
            }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* leftover temp: harmless */ } }
        }
    }
}
