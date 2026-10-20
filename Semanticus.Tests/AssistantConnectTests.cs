using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>Connect another assistant (1.5, lane connect-any). Kane said yes on 11 Oct: Semanticus writes the MCP
    /// connection into the person's other assistants, and their Pro licence works in engines those assistants start.
    ///
    /// The promises tested here: only the semanticus entry changes (every other byte of the file stays), pressing twice
    /// changes nothing the second time, a file that is not valid JSON is left alone with a plain sentence, the first change
    /// leaves a copy, each OS looks in its own place, and a Pro licence reaches the user licence file and follows the
    /// licence when it is replaced or removed. Both doors call the same engine method.</summary>
    public sealed class AssistantConnectTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "asc" + Guid.NewGuid().ToString("N").Substring(0, 6));
        private readonly string _home;
        private readonly string _ws;

        public AssistantConnectTests()
        {
            _home = Path.Combine(_root, "home"); _ws = Path.Combine(_root, "ws");
            Directory.CreateDirectory(_home); Directory.CreateDirectory(_ws);
        }

        public void Dispose() { try { Directory.Delete(_root, true); } catch { /* a scratch folder */ } }

        private static JsonObject Entry(string command = "/home/u/.semanticus/bin/semanticus-mcp", params string[] args)
        {
            if (args.Length == 0) args = new[] { "mcp", "--workspace", "/work/Contoso" };
            return new JsonObject { ["command"] = command, ["args"] = new JsonArray(args.Select(a => (JsonNode)JsonValue.Create(a)).ToArray()) };
        }

        private static string Text(ConfigEditResult r) => Encoding.UTF8.GetString(r.Bytes);
        private static ConfigEditResult Json(string existing, JsonObject entry = null) =>
            AssistantConfigEdit.SetJsonServer(existing == null ? null : Encoding.UTF8.GetBytes(existing), "config file", entry ?? Entry());

        private ConnectHost Host(string os = "linux", Func<string, string> onPath = null, Func<string, IReadOnlyList<string>, (int, string)> run = null) => new ConnectHost
        {
            Os = os, Home = _home, Workspace = _ws,
            AppData = Path.Combine(_home, "AppData", "Roaming"), LocalAppData = Path.Combine(_home, "AppData", "Local"),
            FindOnPath = onPath ?? (_ => null), Run = run ?? ((_, __) => (0, "")),
            Command = () => "/opt/semanticus/Semanticus.Engine", CommandPrefix = () => Array.Empty<string>(),
        };

        // ------------------------------------------------------------------------------------------- JSON merge

        [Fact]
        public void Json_insert_keeps_every_other_byte_of_the_file()
        {
            const string original = "{\n  \"globalShortcut\": \"Ctrl+Alt+Space\",\n  \"mcpServers\": {\n    \"filesystem\": {\n      \"command\": \"npx\",\n      \"args\": [ \"-y\", \"server-filesystem\" ]\n    }\n  },\n  \"theme\":   \"dark\"\n}\n";
            var r = Json(original);
            Assert.True(r.Changed); Assert.Null(r.Refusal);
            var after = Text(r);
            var added = ",\n    \"semanticus\": {\n      \"command\": \"/home/u/.semanticus/bin/semanticus-mcp\",\n      \"args\": [\n        \"mcp\",\n        \"--workspace\",\n        \"/work/Contoso\"\n      ]\n    }";
            Assert.Equal(original, after.Replace(added, ""));   // take our insert out and the file is the file it was, odd spacing and all
            Assert.NotNull(JsonNode.Parse(after)["mcpServers"]["semanticus"]);
        }

        [Fact]
        public void Json_pressing_twice_changes_nothing_the_second_time()
        {
            var first = Json("{\n  \"mcpServers\": {}\n}\n");
            Assert.True(first.Changed);
            var second = Json(Text(first));
            Assert.False(second.Changed);
            Assert.Null(second.Bytes); Assert.Null(second.Refusal);
        }

        [Fact]
        public void Json_replaces_a_different_semanticus_entry_and_leaves_the_neighbours_alone()
        {
            const string original = "{\n\t\"mcpServers\": {\n\t\t\"a\": { \"command\": \"x\" },\n\t\t\"semanticus\": { \"command\": \"/old/engine\", \"args\": [\"mcp\"] },\n\t\t\"b\": { \"command\": \"y\" }\n\t}\n}\n";
            var r = Json(original);
            Assert.True(r.Changed);
            var after = Text(r);
            Assert.StartsWith("{\n\t\"mcpServers\": {\n\t\t\"a\": { \"command\": \"x\" },\n\t\t\"semanticus\": {", after);
            Assert.EndsWith("},\n\t\t\"b\": { \"command\": \"y\" }\n\t}\n}\n", after);
            Assert.Contains("\n\t\t\t\"command\"", after);   // the new entry uses the file's own tab indent
            Assert.Equal("/home/u/.semanticus/bin/semanticus-mcp", (string)JsonNode.Parse(after)["mcpServers"]["semanticus"]["command"]);
        }

        [Fact]
        public void Json_with_no_mcpServers_adds_it_after_the_last_setting()
        {
            const string original = "{\r\n  \"theme\": \"dark\",\r\n  \"zoom\": 1.25\r\n}\r\n";
            var r = Json(original);
            var after = Text(r);
            Assert.StartsWith("{\r\n  \"theme\": \"dark\",\r\n  \"zoom\": 1.25,\r\n  \"mcpServers\": {", after);
            Assert.DoesNotContain("\r\n".Replace("\r\n", "\n") + "  \"mcpServers\"", after.Replace("\r\n", "|"));   // every break stayed CRLF
            Assert.All(after.Split('\n').Where(l => l.Length > 0), l => Assert.EndsWith("\r", l));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  \n")]
        [InlineData("{}")]
        public void Json_a_missing_or_empty_file_gets_a_whole_small_file(string existing)
        {
            var r = Json(existing);
            Assert.True(r.Changed);
            var obj = JsonNode.Parse(Text(r));
            Assert.Equal("mcp", (string)obj["mcpServers"]["semanticus"]["args"][0]);
        }

        [Fact]
        public void Json_keeps_a_byte_order_mark_and_non_ascii_text()
        {
            var original = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("{\n  \"note\": \"café ☕\"\n}\n")).ToArray();
            var r = AssistantConfigEdit.SetJsonServer(original, "config file", Entry());
            Assert.True(r.Changed);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, r.Bytes.Take(3).ToArray());
            Assert.Contains("\"note\": \"café ☕\"", Text(r));
        }

        [Theory]
        [InlineData("{ \"mcpServers\": { ")]                          // cut off
        [InlineData("{ \"mcpServers\": { }, }")]                     // trailing comma
        [InlineData("not json at all")]
        [InlineData("[1, 2]")]                                       // not an object
        [InlineData("{ \"mcpServers\": [] }")]                       // not a list of servers
        public void Json_that_cannot_be_edited_safely_is_refused_with_a_plain_sentence_and_no_bytes(string existing)
        {
            var r = Json(existing);
            Assert.False(r.Changed); Assert.Null(r.Bytes);
            Assert.False(string.IsNullOrWhiteSpace(r.Refusal));
            Assert.DoesNotContain("Exception", r.Refusal);
            Assert.DoesNotContain("—", r.Refusal);
        }

        // ------------------------------------------------------------------------------------------- Codex TOML

        private static ConfigEditResult Toml(string existing) =>
            AssistantConfigEdit.SetCodexServer(existing, "/home/u/.semanticus/bin/semanticus-mcp", new[] { "mcp", "--workspace", "/work/Contoso" }, 30);

        [Fact]
        public void Toml_appends_a_table_and_keeps_the_rest_of_the_file()
        {
            const string original = "model = \"gpt-5\"\n\n[mcp_servers.docs]\ncommand = \"npx\"\nargs = [\"-y\", \"docs\"]\n";
            var r = Toml(original);
            Assert.True(r.Changed);
            var after = Text(r);
            Assert.StartsWith(original + "\n[mcp_servers.semanticus]\ncommand = \"/home/u/.semanticus/bin/semanticus-mcp\"\nargs = [\"mcp\", \"--workspace\", \"/work/Contoso\"]\nstartup_timeout_sec = 30\n", after);
        }

        [Fact]
        public void Toml_updates_our_table_in_place_and_keeps_keys_the_person_added()
        {
            const string original = "[mcp_servers.semanticus]\ncommand = \"/old\"\nargs = [\n  \"mcp\",\n  \"--workspace\",\n  \"/old/ws\",\n]\nenabled = true # keep\n\n[projects.\"/work\"]\ntrust_level = \"trusted\"\n";
            var r = Toml(original);
            var after = Text(r);
            Assert.Contains("command = \"/home/u/.semanticus/bin/semanticus-mcp\"", after);
            Assert.Contains("enabled = true # keep", after);
            Assert.EndsWith("\n\n[projects.\"/work\"]\ntrust_level = \"trusted\"\n", after);
            Assert.DoesNotContain("/old", after);
            Assert.Single(after.Split('\n'), l => l.StartsWith("args"));
        }

        [Fact]
        public void Toml_pressing_twice_changes_nothing_the_second_time()
        {
            var first = Toml("model = \"gpt-5\"\n");
            var second = Toml(Text(first));
            Assert.False(second.Changed);
        }

        [Fact]
        public void Toml_a_bracket_inside_a_multi_line_string_is_not_a_table()
        {
            const string original = "notes = \"\"\"\n[mcp_servers.semanticus]\nnot a table\n\"\"\"\n";
            var r = Toml(original);
            Assert.True(r.Changed);
            Assert.StartsWith(original, Text(r));
        }

        [Theory]
        [InlineData("mcp_servers = { semanticus = { command = \"x\" } }\n")]
        [InlineData("mcp_servers.semanticus.command = \"x\"\n")]
        [InlineData("[mcp_servers]\nsemanticus = { command = \"x\" }\n")]
        public void Toml_an_entry_written_another_way_is_left_alone(string existing)
        {
            var r = Toml(existing);
            Assert.False(r.Changed); Assert.Null(r.Bytes);
            Assert.Contains("by hand", r.Refusal);
        }

        [Fact]
        public void Verifier_toml_with_comments_and_sub_tables_keeps_every_other_line_and_is_idempotent()
        {
            const string original = "# Codex settings, kept by hand\nmodel = \"gpt-5\"   # the default\n\n[mcp_servers.docs]  # docs server\ncommand = \"npx\"\nargs = [\"-y\", \"docs\"] # pinned\n\n[mcp_servers.docs.env]\nTOKEN_FILE = \"/x\"\n\n[mcp_servers.semanticus] # ours\ncommand = \"/old\"\nargs = [\"mcp\"]\nenabled = true\n\n[mcp_servers.semanticus.env]\nFOO = \"bar\" # keep me\n\n# trailing note\n[profiles.fast]\nmodel = \"o4\"\n";
            var r = Toml(original);
            Assert.True(r.Changed); Assert.Null(r.Refusal);
            var after = Text(r);
            var before = original.Split('\n'); var now = after.Split('\n');
            foreach (var line in before.Where(l => !l.StartsWith("command = \"/old\"") && !l.StartsWith("args = [\"mcp\"]")))
                Assert.Contains(line, now);
            Assert.Contains("[mcp_servers.semanticus.env]\nFOO = \"bar\" # keep me\n", after);
            Assert.EndsWith("# trailing note\n[profiles.fast]\nmodel = \"o4\"\n", after);
            Assert.Single(now, l => l.TrimStart().StartsWith("[mcp_servers.semanticus]"));
            Assert.False(Toml(after).Changed);
        }

        [Fact]
        public void Verifier_nested_json_keeps_every_other_byte_and_is_idempotent()
        {
            const string original = "{\n\t\"mcpServers\": {\n\t\t\"github\": {\"command\": \"docker\", \"args\": [\"run\", \"-i\", {\"weird\": [1, 2, {\"deep\": null}]}], \"env\": {\"A\": \"b\\\\c\", \"U\": \"\\u00e9\"}}\n\t},\n\t\"preferences\": {\"nested\": {\"x\": [true, false, 1.5e3]}},\n\t\"z\": []\n}";
            var r = Json(original);
            Assert.True(r.Changed); Assert.Null(r.Refusal);
            var after = Text(r);
            var o = JsonNode.Parse(after);
            Assert.NotNull(o["mcpServers"]["semanticus"]);
            var start = after.IndexOf(",", after.IndexOf("\"U\": \"\\u00e9\"}}"));
            var end = after.IndexOf("\n\t},\n\t\"preferences\"");
            Assert.True(start > 0 && end > start);
            Assert.Equal(original, after.Remove(start, end - start));
            Assert.False(Json(after).Changed);
        }

        // ------------------------------------------------------------------------------------------- paths per OS

        [Fact]
        public void Each_os_looks_in_its_own_place()
        {
            var h = Host();
            Assert.Equal(Path.Combine(_home, ".config", "Claude", "claude_desktop_config.json"), AssistantConnector.ConfigPaths("claude-desktop", Host("linux")).Single());
            Assert.Equal(Path.Combine(_home, "Library", "Application Support", "Claude", "claude_desktop_config.json"), AssistantConnector.ConfigPaths("claude-desktop", Host("macos")).Single());
            Assert.Equal(Path.Combine(_home, "AppData", "Roaming", "Claude", "claude_desktop_config.json"), AssistantConnector.ConfigPaths("claude-desktop", Host("windows")).First());
            foreach (var os in new[] { "linux", "macos", "windows" })
            {
                Assert.Equal(Path.Combine(_home, ".cursor", "mcp.json"), AssistantConnector.ConfigPaths("cursor", Host(os)).Single());
                Assert.Equal(Path.Combine(_home, ".codex", "config.toml"), AssistantConnector.ConfigPaths("codex", Host(os)).Single());
                Assert.Equal(Path.Combine(_home, ".claude.json"), AssistantConnector.ConfigPaths("claude-code", Host(os)).Single());
            }
            var codexHome = Host(); codexHome.CodexHome = Path.Combine(_root, "elsewhere");
            Assert.Equal(Path.Combine(_root, "elsewhere", "config.toml"), AssistantConnector.ConfigPaths("codex", codexHome).Single());
            _ = h;
        }

        [Fact]
        public void Windows_store_install_of_claude_desktop_is_found_by_its_package_folder()
        {
            var pkg = Path.Combine(_home, "AppData", "Local", "Packages", "Claude_pzs8sxrjxfjjc", "LocalCache", "Roaming", "Claude");
            Directory.CreateDirectory(pkg);
            File.WriteAllText(Path.Combine(pkg, "claude_desktop_config.json"), "{}");
            var h = Host("windows");
            var info = AssistantConnector.List(h).Single(a => a.Id == "claude-desktop");
            Assert.True(info.Found);
            Assert.Equal(Path.Combine(pkg, "claude_desktop_config.json"), info.ConfigPath);
        }

        // ------------------------------------------------------------------------------------------- connect, end to end on disk

        [Fact]
        public void Nothing_found_says_not_found_and_connect_creates_nothing()
        {
            var h = Host();
            var list = AssistantConnector.List(h);
            Assert.Equal(4, list.Length);
            Assert.All(list, a => Assert.Equal("notFound", a.State));
            Assert.False(Directory.Exists(Path.Combine(_home, ".config")));
        }

        [Fact]
        public void Claude_desktop_connect_writes_the_entry_leaves_a_copy_once_and_names_the_next_step()
        {
            var cfg = Path.Combine(_home, ".config", "Claude", "claude_desktop_config.json");
            Directory.CreateDirectory(Path.GetDirectoryName(cfg));
            const string original = "{\n  \"mcpServers\": {\n    \"other\": { \"command\": \"x\" }\n  }\n}\n";
            File.WriteAllText(cfg, original);
            var h = Host();
            Assert.Equal("notConnected", AssistantConnector.List(h).Single(a => a.Id == "claude-desktop").State);

            var r = AssistantConnector.Connect("Claude Desktop", h);
            Assert.True(r.Ok); Assert.True(r.Changed);
            Assert.Equal("Connected Claude Desktop.", r.Message);
            Assert.Equal("Quit Claude Desktop fully and open it again.", r.NextStep);
            Assert.Equal(cfg + ".semanticus-bak", r.BackupPath);
            Assert.Equal(original, File.ReadAllText(r.BackupPath));
            var json = JsonNode.Parse(File.ReadAllText(cfg));
            Assert.Equal("/opt/semanticus/Semanticus.Engine", (string)json["mcpServers"]["semanticus"]["command"]);
            Assert.Equal(_ws, (string)json["mcpServers"]["semanticus"]["args"][2]);
            Assert.Equal("x", (string)json["mcpServers"]["other"]["command"]);
            Assert.Equal("connected", AssistantConnector.List(h).Single(a => a.Id == "claude-desktop").State);

            // Pressing again: nothing written, and the copy keeps the FIRST content.
            var stamp = File.GetLastWriteTimeUtc(cfg);
            var again = AssistantConnector.Connect("claude-desktop", h);
            Assert.True(again.Ok); Assert.False(again.Changed);
            Assert.Equal("Claude Desktop is already connected.", again.Message);
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(cfg));
            Assert.Equal(original, File.ReadAllText(cfg + ".semanticus-bak"));
        }

        [Fact]
        public void An_entry_that_points_somewhere_else_reads_as_outdated_and_connect_fixes_it()
        {
            var cfg = Path.Combine(_home, ".cursor", "mcp.json");
            Directory.CreateDirectory(Path.GetDirectoryName(cfg));
            File.WriteAllText(cfg, "{\"mcpServers\":{\"semanticus\":{\"command\":\"/gone/engine\",\"args\":[\"mcp\"]}}}");
            var h = Host();
            Assert.Equal("outdated", AssistantConnector.List(h).Single(a => a.Id == "cursor").State);
            Assert.True(AssistantConnector.Connect("cursor", h).Changed);
            Assert.Equal("connected", AssistantConnector.List(h).Single(a => a.Id == "cursor").State);
        }

        [Fact]
        public void A_file_that_is_not_valid_json_is_left_exactly_as_it_was_with_no_copy()
        {
            var cfg = Path.Combine(_home, ".cursor", "mcp.json");
            Directory.CreateDirectory(Path.GetDirectoryName(cfg));
            const string broken = "{ \"mcpServers\": { \"a\": ";
            File.WriteAllText(cfg, broken);
            var r = AssistantConnector.Connect("cursor", Host());
            Assert.False(r.Ok);
            Assert.Contains("not valid JSON", r.Message);
            Assert.Equal(broken, File.ReadAllText(cfg));
            Assert.False(File.Exists(cfg + ".semanticus-bak"));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(cfg)).Where(f => f != cfg));
        }

        [Fact]
        public void Codex_connect_writes_the_table_into_config_toml()
        {
            var cfg = Path.Combine(_home, ".codex", "config.toml");
            Directory.CreateDirectory(Path.GetDirectoryName(cfg));
            File.WriteAllText(cfg, "model = \"gpt-5\"\n");
            var r = AssistantConnector.Connect("Codex CLI", Host());
            Assert.True(r.Ok);
            Assert.Equal("Start a new Codex session.", r.NextStep);
            var text = File.ReadAllText(cfg);
            Assert.StartsWith("model = \"gpt-5\"\n", text);
            Assert.Contains("[mcp_servers.semanticus]", text);
            Assert.Equal("connected", AssistantConnector.List(Host()).Single(a => a.Id == "codex").State);
            Assert.False(AssistantConnector.Connect("codex", Host()).Changed);
        }

        [Fact]
        public void Claude_code_uses_the_claude_command_at_user_scope_when_it_is_installed()
        {
            var calls = new List<string>();
            var h = Host(onPath: n => n == "claude" ? "/usr/bin/claude" : null, run: (file, args) => { calls.Add(file + " " + string.Join(" ", args)); return (0, ""); });
            var r = AssistantConnector.Connect("claude-code", h);
            Assert.True(r.Ok);
            Assert.Equal("Start a new Claude Code session in your terminal.", r.NextStep);
            Assert.Equal($"/usr/bin/claude mcp add --scope user semanticus -- /opt/semanticus/Semanticus.Engine mcp --workspace {_ws}", Assert.Single(calls));
        }

        [Fact]
        public void Claude_code_command_that_fails_gives_a_plain_message()
        {
            var h = Host(onPath: n => n == "claude" ? "/usr/bin/claude" : null, run: (_, __) => (1, "error: something broke\nmore"));
            var r = AssistantConnector.Connect("claude-code", h);
            Assert.False(r.Ok);
            Assert.Equal("Claude Code did not accept the connection: error: something broke", r.Message);
        }

        [Fact]
        public void Claude_code_update_that_fails_after_the_remove_puts_the_old_entry_back()
        {
            // F-291: remove worked, add failed. The person must not be left with no Semanticus entry.
            File.WriteAllText(Path.Combine(_home, ".claude.json"),
                "{\n  \"mcpServers\": { \"semanticus\": { \"command\": \"/old/engine\", \"args\": [\"mcp\", \"--workspace\", \"/old/ws\"] } }\n}\n");
            var calls = new List<string>();
            var h = Host(onPath: n => n == "claude" ? "/usr/bin/claude" : null, run: (file, args) =>
            {
                var line = string.Join(" ", args);
                calls.Add(line);
                // the new entry (this workspace) is refused; the remove and the restore of the old one go through
                return (line.StartsWith("mcp add") && line.Contains(_ws) ? 1 : 0, line.StartsWith("mcp add") && line.Contains(_ws) ? "error: boom" : "");
            });
            var r = AssistantConnector.Connect("claude-code", h);
            Assert.False(r.Ok);
            Assert.Equal("mcp add --scope user semanticus -- /old/engine mcp --workspace /old/ws", calls.Last());
            Assert.Contains("mcp remove --scope user semanticus", calls);
        }

        [Fact]
        public void Verifier_F291_the_restore_of_an_old_entry_with_env_is_a_line_claude_accepts()
        {
            // v-trial-fixes: claude's -e is variadic, so "-e A=x semanticus -- cmd" eats the name and is refused
            // (measured with claude 2.1.300: "Invalid environment variable format: semanticus"). The name must come before -e.
            File.WriteAllText(Path.Combine(_home, ".claude.json"),
                "{\n  \"mcpServers\": { \"semanticus\": { \"command\": \"/old/engine\", \"args\": [\"mcp\"], \"env\": { \"SEMANTICUS_HOME\": \"/old/home\" } } }\n}\n");
            var calls = new List<string>();
            var h = Host(onPath: n => n == "claude" ? "/usr/bin/claude" : null, run: (file, args) =>
            {
                var line = string.Join(" ", args);
                calls.Add(line);
                return (line.StartsWith("mcp add") && line.Contains(_ws) ? 1 : 0, "");
            });
            AssistantConnector.Connect("claude-code", h);
            var restore = calls.Last();
            Assert.Contains("SEMANTICUS_HOME=/old/home", restore);
            Assert.True(restore.IndexOf(" semanticus ", StringComparison.Ordinal) < restore.IndexOf("-e ", StringComparison.Ordinal), restore);
        }

        [Fact]
        public void Claude_code_without_the_command_but_with_its_settings_file_edits_only_our_entry()
        {
            var file = Path.Combine(_home, ".claude.json");
            const string original = "{\n  \"numStartups\": 7,\n  \"mcpServers\": {}\n}\n";
            File.WriteAllText(file, original);
            var r = AssistantConnector.Connect("claude-code", Host());
            Assert.True(r.Ok);
            var json = JsonNode.Parse(File.ReadAllText(file));
            Assert.Equal(7, (int)json["numStartups"]);
            Assert.Equal("stdio", (string)json["mcpServers"]["semanticus"]["type"]);
            Assert.Equal(original, File.ReadAllText(file + ".semanticus-bak"));
        }

        [Fact]
        public void An_unknown_name_gets_a_plain_message_listing_the_choices()
        {
            var r = AssistantConnector.Connect("notepad", Host());
            Assert.False(r.Ok);
            Assert.Contains("Claude Desktop", r.Message);
        }

        [Fact]
        public void A_symlinked_config_is_edited_where_it_lives_and_stays_a_link()
        {
            if (OperatingSystem.IsWindows()) return;
            var real = Path.Combine(_root, "dotfiles", "mcp.json");
            Directory.CreateDirectory(Path.GetDirectoryName(real));
            File.WriteAllText(real, "{}");
            var link = Path.Combine(_home, ".cursor", "mcp.json");
            Directory.CreateDirectory(Path.GetDirectoryName(link));
            File.CreateSymbolicLink(link, real);
            Assert.True(AssistantConnector.Connect("cursor", Host()).Ok);
            Assert.NotNull(new FileInfo(link).LinkTarget);
            Assert.Contains("semanticus", File.ReadAllText(real));
        }

        // ------------------------------------------------------------------------------------------- the licence

        private (LocalEngine engine, string licenseFile, string token, string pub, string priv) ProEngine()
        {
            var (pub, priv) = LicenseVerifier.GenerateKeyPair();
            var token = LicenseVerifier.Mint(priv, new LicenseClaims { Sub = "buyer@contoso.com", Tier = "pro", Iat = 1, Exp = 0 });
            var now = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
            var engine = new LocalEngine(new SessionManager(), LicenseEntitlement.Evaluate(token, pub, devPro: false, now));
            engine.UseLicenseEvaluatorForTest(t => LicenseEntitlement.Evaluate(t, pub, devPro: false, now));
            var file = Path.Combine(_home, ".semanticus", "license");
            engine.LicenseFileForTest = () => file;
            engine.AssistantHostForTest = () => Host();
            Directory.CreateDirectory(Path.Combine(_home, ".cursor"));
            return (engine, file, token, pub, priv);
        }

        [Fact]
        public async Task A_pro_licence_is_saved_for_the_other_assistant_and_the_answer_says_so()
        {
            var (engine, file, token, pub, _) = ProEngine();
            var r = await engine.ConnectAssistantAsync("cursor");
            Assert.True(r.Ok);
            Assert.Equal(token, File.ReadAllText(file));
            Assert.Contains("Your Pro licence is saved in", r.LicenceNote);
            Assert.Contains("runs as Pro", r.LicenceNote);
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
            // An engine started by the other assistant reads that file and is Pro.
            var outside = LicenseEntitlement.Evaluate(File.ReadAllText(file).Trim(), pub, devPro: false, new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero));
            Assert.True(outside.IsPro);
            // Pressing again says nothing about the licence: it is already there.
            Assert.Null((await engine.ConnectAssistantAsync("cursor")).LicenceNote);
        }

        [Fact]
        public async Task A_free_session_writes_no_licence_file()
        {
            var (engine, file, _, _, _) = ProEngine();
            var free = new LocalEngine(new SessionManager(), LicenseEntitlement.Evaluate(null, "x", devPro: false, DateTimeOffset.UtcNow));
            free.LicenseFileForTest = () => file; free.AssistantHostForTest = engine.AssistantHostForTest;
            var r = await free.ConnectAssistantAsync("cursor");
            Assert.True(r.Ok); Assert.Null(r.LicenceNote);
            Assert.False(File.Exists(file));
        }

        [Fact]
        public async Task The_licence_file_follows_a_removed_or_replaced_licence_but_is_never_made_by_a_change()
        {
            var (engine, file, token, pub, priv) = ProEngine();
            // No file yet: swapping the licence does not make one.
            engine.ApplyLicense(token, verifyOnly: false);
            Assert.False(File.Exists(file));
            await engine.ConnectAssistantAsync("cursor");
            Assert.True(File.Exists(file));
            // A replaced licence is written through.
            var renewed = LicenseVerifier.Mint(priv, new LicenseClaims { Sub = "buyer@contoso.com", Tier = "pro", Iat = 1, Exp = 0 });
            engine.ApplyLicense(renewed, verifyOnly: false);
            Assert.Equal(renewed, File.ReadAllText(file));
            // Remove licence deletes it, so an outside engine does not stay Pro on a licence the person removed.
            engine.ApplyLicense("", verifyOnly: false);
            Assert.False(File.Exists(file));
        }

        [Fact]
        public async Task Pro_that_starts_after_an_assistant_was_connected_on_Free_reaches_its_licence_file()
        {
            // hero17 P1: connect on Free, then the trial turns Pro on. The other assistant's engine must read Pro too.
            var (_, file, token, pub, _) = ProEngine();
            var now = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
            var free = new LocalEngine(new SessionManager(), LicenseEntitlement.Evaluate(null, pub, devPro: false, now));
            free.UseLicenseEvaluatorForTest(t => LicenseEntitlement.Evaluate(t, pub, devPro: false, now));
            free.LicenseFileForTest = () => file; free.AssistantHostForTest = () => Host();
            Assert.True((await free.ConnectAssistantAsync("cursor")).Ok);
            Assert.False(File.Exists(file));
            free.ApplyLicense(token, verifyOnly: false);
            Assert.Equal(token, File.ReadAllText(file));
        }

        [Fact]
        public async Task Verifier_remove_licence_after_connect_lands_free_in_this_engine_like_production()
        {
            // The production evaluator (FromEnvironmentOrToken) falls back to the user licence file for an empty token.
            // This one does the same, so the test sees what a real engine sees.
            var (engine, file, token, pub, _) = ProEngine();
            var now = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
            engine.UseLicenseEvaluatorForTest(t => LicenseEntitlement.Evaluate(t ?? (File.Exists(file) ? File.ReadAllText(file).Trim() : null), pub, devPro: false, now));
            await engine.ConnectAssistantAsync("cursor");
            Assert.True(File.Exists(file));
            var removed = engine.ApplyLicense("", verifyOnly: false);
            Assert.False(File.Exists(file));
            Assert.Equal("free", removed.Entitlement.Tier);
        }

        // ------------------------------------------------------------------------------------------- both doors, one path

        [Fact]
        public async Task The_tool_lists_with_no_name_and_connects_with_one_through_the_same_engine_method()
        {
            var (engine, file, token, _, _) = ProEngine();
            var listed = Assert.IsType<ConnectableAssistant[]>(await McpTools.ConnectAssistant(engine, null));
            Assert.Contains(listed, a => a.Id == "cursor" && a.Found && a.State == "notConnected");
            var connected = Assert.IsType<AssistantConnectResult>(await McpTools.ConnectAssistant(engine, "cursor"));
            Assert.True(connected.Ok);
            Assert.Equal("connected", (await engine.ListAssistantsAsync()).Single(a => a.Id == "cursor").State);
            Assert.Equal(token, File.ReadAllText(file));
        }

        [Fact]
        public async Task The_real_mcp_door_lists_the_assistants_and_connects_one_with_the_licence()
        {
            var (engine, file, token, _, _) = ProEngine();
            var (door, client) = await ToolMergeTests.StartDoorAsync(engine, gated: true);
            using (door)
            await using (client)
            {
                Assert.Contains(await client.ListToolsAsync(), t => t.Name == "connect_assistant");
                var listed = await client.CallToolAsync("connect_assistant", new Dictionary<string, object>());
                Assert.False(listed.IsError == true);
                Assert.Contains("claude-desktop", string.Join("", listed.Content.Select(c => c.ToString())));
                var done = await client.CallToolAsync("connect_assistant", new Dictionary<string, object> { ["assistant"] = "cursor" });
                Assert.False(done.IsError == true);
                var text = string.Join("", done.Content.Select(c => c.ToString()));
                Assert.Contains("Connected Cursor.", text);
                Assert.Contains("Restart Cursor", text);
                Assert.Contains("runs as Pro too", text);
                Assert.Equal(token, File.ReadAllText(file));
                var cursor = JsonNode.Parse(File.ReadAllText(Path.Combine(_home, ".cursor", "mcp.json")));
                Assert.Equal(_ws, (string)cursor["mcpServers"]["semanticus"]["args"][2]);
                await door.StopAsync();
            }
        }

        [Fact]
        public void The_gate_and_the_rpc_door_know_both_new_methods()
        {
            Assert.NotNull(GateRegistry.Find("ConnectAssistantAsync"));
            Assert.NotNull(GateRegistry.Find("ListAssistantsAsync"));
            var rpc = typeof(EngineRpcTarget);
            Assert.NotNull(rpc.GetMethod("connectAssistant"));
            Assert.NotNull(rpc.GetMethod("listAssistants"));
        }
    }
}
