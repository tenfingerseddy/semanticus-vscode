// On Unix a .NET named pipe IS a domain socket, and its address is "<temp>/CoreFxPipe_<name>". That path has a
// hard 108-byte limit (107 usable: the terminating NUL takes one), which the framework reports from deep inside a socket constructor as a raw
// ArgumentOutOfRangeException naming neither the test nor the pipe. On 2026-09-28 five engine tests failed that
// way at once, under a scratch TMPDIR 47 characters long:
//
//   WorkflowV2SilentDropTests x2, WorkflowUpgradeTests x2, PersonQueueTests x1
//   System.ArgumentOutOfRangeException : The path '.../CoreFxPipe_semanticus-public-call-<32 hex>' is of an
//   invalid length for use with domain sockets on this platform. The length must be between 1 and 108 ...
//
// On the same subject, and written to test a hypothesis that turned out to be WRONG: this host's /tmp held 603
// orphaned CoreFxPipe_semanticus-* sockets on 2026-09-28, the oldest three days old, in a 16 GB tmpfs the lanes
// share. The guess was that RpcServer.Dispose leaks its socket file. It does not: the last test below passed
// the first time it ran, so a clean dispose does take the file with it. The orphans are the engines that never
// got a clean dispose: a lane that killed one by pid, and also the engines the suite itself starts and names
// (EngineLifetimeTests, McpOwnerLossTests, McpAttachTests leave CoreFxPipe_semanticus-<12 hex> behind on a run
// with no kill at all). The test stays as a pin, not as
// proof of a fix, because nothing here was broken.
//
// The root cause is not the temp directory. It is that the suite minted pipe names with a whole 32-character
// guid, spending 24 bytes of a 108-byte budget on uniqueness nothing needed: one test run never comes close to
// colliding 8 hex characters, and several tests in this suite already used 8. These tests hold the two things
// that keep it fixed: a minted name fits under a realistic temp directory, and when a directory is genuinely
// too long the failure names the limit instead of the socket constructor.

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;
using Xunit.Sdk;

namespace Semanticus.Tests
{
    public sealed class TestPipeNameTests
    {
        // The directory that actually broke the run, kept verbatim so the number in the comment stays honest.
        private const string TheDirectoryThatBrokeIt = "/home/tenfingers/.cache/base-fixes-scratch/tmp3";

        [Fact]
        public void A_minted_pipe_name_fits_a_unix_socket_path_under_the_temp_directory_that_broke_the_run()
        {
            foreach (var prefix in new[] { "semanticus-public-call-", "semanticus-upgrade-", "semanticus-queue-" })
            {
                var name = TestPipe.Mint(prefix);
                var path = TestPipe.SocketPathFor(TheDirectoryThatBrokeIt, name);
                var bytes = Encoding.UTF8.GetByteCount(path);
                Assert.True(bytes <= TestPipe.UnixSocketPathLimit,
                    $"'{path}' is {bytes} bytes; a domain socket path may be at most {TestPipe.UnixSocketPathLimit}");
            }
        }

        [Fact]
        public void A_temp_directory_no_name_can_fit_fails_with_the_limit_named_not_a_socket_constructor()
        {
            if (OperatingSystem.IsWindows()) return;   // \\.\pipe\ has no such limit, so Mint never refuses a directory there

            var hopeless = "/" + new string('x', 120);
            var thrown = Assert.Throws<XunitException>(() => TestPipe.MintIn(hopeless, "semanticus-queue-"));

            Assert.Contains(TestPipe.UnixSocketPathLimit.ToString(), thrown.Message, StringComparison.Ordinal);
            Assert.Contains("TMPDIR", thrown.Message, StringComparison.Ordinal);
            // It must say how much the directory alone costs, or the reader cannot tell what to shorten.
            Assert.Contains(hopeless.Length.ToString(), thrown.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void The_platform_refuses_a_socket_path_of_the_full_108_bytes_so_Mint_does_too()
        {
            // Found in round 3 of the review: under a 70-byte TMPDIR, Mint handed McpAttachTests a name whose socket
            // path was exactly 108 bytes, and the server never bound. The 108 bytes of sun_path include the path's
            // terminating NUL, so 107 is the most a path may use. Both halves are pinned here: what the framework
            // refuses, and that Mint refuses the same.
            if (OperatingSystem.IsWindows()) return;   // \\.\pipe\ has no such limit

            const string prefix = "semanticus-queue-";               // 17 bytes, plus 8 hex
            var overhead = "/CoreFxPipe_".Length + prefix.Length + 8;  // 37
            var dirFor = new Func<int, string>(pathBytes => "/" + new string('x', pathBytes - overhead - 1));

            Assert.Throws<ArgumentOutOfRangeException>(
                () => new System.Net.Sockets.UnixDomainSocketEndPoint(dirFor(108) + "/CoreFxPipe_" + prefix + "0123abcd"));
            _ = new System.Net.Sockets.UnixDomainSocketEndPoint(dirFor(107) + "/CoreFxPipe_" + prefix + "0123abcd");

            Assert.Throws<XunitException>(() => TestPipe.MintIn(dirFor(108), prefix));
            var fits = TestPipe.MintIn(dirFor(107), prefix);
            Assert.Equal(107, Encoding.UTF8.GetByteCount(TestPipe.SocketPathFor(dirFor(107), fits)));
        }

        [Fact]
        public void No_test_builds_a_pipe_name_from_a_guid_by_hand()
        {
            // The guard that keeps every call site on TestPipe.Mint. The first version of it matched one spelling
            // (`var <something>pipe<something> = "..." + Guid.NewGuid().ToString("N");`) and missed a renamed
            // variable, an interpolated string, an inline argument, a field, and every 8-hex site, so ten sites
            // that open a pipe never went through the length check CLAUDE.md said they did. It now reads statements,
            // not one line shape. Hand-built pipe names are refused whatever their length: an 8-hex name fits
            // today, but only Mint says so out loud when a directory is too long.
            var root = Path.Combine(RepoRoot(), "Semanticus.Tests");
            var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f => !string.Equals(Path.GetFileName(f), "TestPipeNameTests.cs", StringComparison.Ordinal))
                .SelectMany(f => PipeNamesBuiltByHand(File.ReadAllText(f)).Select(s => Path.GetFileName(f) + ": " + s))
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();

            Assert.True(offenders.Length == 0,
                "these build a pipe name from a guid by hand, so nothing checks it against the 108-byte domain-socket "
                + "path. Use TestPipe.Mint(prefix) instead:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
        }

        // The ways of breaking the rule a verifier listed on 2026-09-28, plus the shapes this suite actually used.
        [Theory]
        [InlineData("var pipeName = \"semanticus-x-\" + Guid.NewGuid().ToString(\"N\");")]
        [InlineData("var name = \"semanticus-x-\" + Guid.NewGuid().ToString(\"N\");\nusing var server = new RpcServer(sessions, engine, name);")]
        [InlineData("var pipeName = $\"semanticus-x-{Guid.NewGuid():N}\";")]
        [InlineData("using var server = new RpcServer(s, e, \"semanticus-x-\" + Guid.NewGuid().ToString(\"N\"));")]
        [InlineData("private readonly string Pipe = \"semanticus-x-\" + Guid.NewGuid().ToString(\"N\");")]
        [InlineData("var pipe = \"semanticus-x-\" + Guid.NewGuid().ToString(\"N\").Substring(0, 8);")]
        [InlineData("EngineBroker.WriteInfo(ws, new EngineInfo\n{\n    PipeName = \"semanticus-x-\" + Guid.NewGuid().ToString(\"N\")[..8],\n    Pid = 1,\n});")]
        [InlineData("var n = $\"semanticus-{Guid.NewGuid().ToString(\"N\")}\";\nvar c = new NamedPipeClientStream(\".\", n, PipeDirection.InOut);")]
        [InlineData("var missing = \"semanticus-missing-\" + Guid.NewGuid().ToString(\"N\");\nAssert.False(File.Exists(EngineBroker.PipePathFor(missing)));")]
        public void The_guard_catches_a_hand_built_pipe_name_however_it_is_spelled(string source)
        {
            Assert.NotEmpty(PipeNamesBuiltByHand(source));
        }

        [Theory]
        [InlineData("var pipe = TestPipe.Mint(\"semanticus-x-\");\nusing var server = new RpcServer(sessions, engine, pipe);")]
        [InlineData("var dir = Path.Combine(Path.GetTempPath(), \"sem-\" + Guid.NewGuid().ToString(\"N\"));")]
        [InlineData("// var pipe = \"semanticus-x-\" + Guid.NewGuid().ToString(\"N\");\nvar x = 1;")]
        [InlineData("/* new RpcServer(s, e, \"x\" + Guid.NewGuid()) */ var x = 1;")]
        [InlineData("var pipelines = \"p-\" + Guid.NewGuid().ToString(\"N\");")]
        [InlineData("var c = '{'; var s = \"pipe {\"; var dir = \"d-\" + Guid.NewGuid().ToString(\"N\");")]
        [InlineData("var name = Path.Combine(tmp, Guid.NewGuid().ToString(\"N\"));\nFile.WriteAllText(name, \"\");")]
        public void The_guard_leaves_what_is_not_a_hand_built_pipe_name_alone(string source)
        {
            Assert.Empty(PipeNamesBuiltByHand(source));
        }

        [Fact]
        public async Task A_disposed_rpc_server_takes_its_socket_file_with_it()
        {
            if (OperatingSystem.IsWindows()) return;   // \\.\pipe\ names are kernel objects, not files

            var name = TestPipe.Mint("semanticus-sockleak-");
            var path = EngineBroker.PipePathFor(name);
            // Every resource is disposed on every path, so a failed assert cannot leave the listener up and its
            // socket file behind, which is the very thing this test pins against.
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            using var stop = new CancellationTokenSource();
            var server = new RpcServer(sessions, engine, name);
            try
            {
                var serving = server.RunAsync(stop.Token);

                // The listener has to be up before the file means anything.
                for (var i = 0; i < 200 && !File.Exists(path); i++) await Task.Delay(10);
                Assert.True(File.Exists(path), $"the server never bound '{path}', so this proves nothing");

                stop.Cancel();
                try { await serving; } catch (OperationCanceledException) { }
            }
            finally
            {
                stop.Cancel();
                server.Dispose();
            }

            Assert.False(File.Exists(path),
                $"'{path}' outlived the server that made it. Nothing else ever deletes it, so one would accumulate "
                + "per engine run, forever, in a temp directory other lanes share.");
        }

        private static readonly Regex PipeWord = new Regex(@"(?i)pipe(?!line)|RpcServer", RegexOptions.CultureInvariant);

        // The places a name is handed to something that opens, or looks for, a pipe.
        private static readonly Regex[] PipeSinks =
        {
            new Regex(@"new\s+RpcServer\s*\(\s*[^,]+,\s*[^,]+,\s*([A-Za-z_]\w*)\s*[,)]"),
            new Regex(@"NamedPipeServerStream\s*\(\s*([A-Za-z_]\w*)\s*[,)]"),
            new Regex(@"NamedPipeClientStream\s*\(\s*""[^""]*""\s*,\s*([A-Za-z_]\w*)\s*[,)]"),
            new Regex(@"\bPipeName\s*=\s*([A-Za-z_]\w*)\s*(?:,|$)"),
            new Regex(@"PipePathFor\s*\(\s*([A-Za-z_]\w*)\s*\)"),
            new Regex(@"RemoteEngine\.ConnectAsync\s*\(\s*([A-Za-z_]\w*)\s*[,)]"),
        };

        /// <summary>Every statement in this C# source that builds a pipe name from a guid without TestPipe: one
        /// that mentions a pipe (or an RpcServer) and calls Guid.NewGuid, or one that assigns a guid-built value to
        /// a name the same source later hands to a pipe. Comments are ignored; strings are kept whole.</summary>
        internal static string[] PipeNamesBuiltByHand(string source)
        {
            var statements = Statements(source);
            var sinkNames = statements
                .SelectMany(s => PipeSinks.SelectMany(r => r.Matches(s).Select(m => m.Groups[1].Value)))
                .ToHashSet(StringComparer.Ordinal);

            return statements
                .Where(s => s.Contains("Guid.NewGuid", StringComparison.Ordinal))
                .Where(s =>
                {
                    if (PipeWord.IsMatch(s)) return true;
                    var assigned = Regex.Match(s, @"\b([A-Za-z_]\w*)\s*=(?![=>])");
                    return assigned.Success && sinkNames.Contains(assigned.Groups[1].Value);
                })
                .Select(s => Regex.Replace(s.Trim(), @"\s+", " "))
                .ToArray();
        }

        /// <summary>Splits C# source into statements at ';', '{' and '}', but never inside a string, a char literal
        /// or an interpolation hole, and drops comments. Enough C# for a guard, not a parser.</summary>
        internal static string[] Statements(string src)
        {
            var result = new System.Collections.Generic.List<string>();
            var cur = new StringBuilder();
            var i = 0;
            while (i < src.Length)
            {
                var c = src[i];
                if (c == '/' && i + 1 < src.Length && src[i + 1] == '/')
                {
                    while (i < src.Length && src[i] != '\n') i++;
                    continue;
                }
                if (c == '/' && i + 1 < src.Length && src[i + 1] == '*')
                {
                    var end = src.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? src.Length : end + 2;
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    var start = i;
                    i = SkipLiteral(src, i);
                    cur.Append(src, start, i - start);
                    continue;
                }
                if (c == ';' || c == '{' || c == '}')
                {
                    if (cur.ToString().Trim().Length > 0) result.Add(cur.ToString());
                    cur.Clear();
                    i++;
                    continue;
                }
                cur.Append(c);
                i++;
            }
            if (cur.ToString().Trim().Length > 0) result.Add(cur.ToString());
            return result.ToArray();
        }

        // Returns the index just past the literal that starts at `i`. The '@' and '$' prefixes, already consumed by
        // the caller, are read back from the characters just before it.
        private static int SkipLiteral(string src, int i)
        {
            if (src[i] == '\'')
            {
                i++;
                while (i < src.Length && src[i] != '\'' && src[i] != '\n') i += src[i] == '\\' ? 2 : 1;
                return Math.Min(i + 1, src.Length);
            }

            var verbatim = false;
            var interpolated = false;
            for (var j = i - 1; j >= 0 && (src[j] == '@' || src[j] == '$'); j--)
            {
                if (src[j] == '@') verbatim = true; else interpolated = true;
            }

            var quotes = 0;
            while (i + quotes < src.Length && src[i + quotes] == '"') quotes++;
            if (quotes >= 3)
            {
                var close = src.IndexOf(new string('"', quotes), i + quotes, StringComparison.Ordinal);
                return close < 0 ? src.Length : close + quotes;
            }
            if (quotes == 2) return i + 2;   // ""

            i++;
            while (i < src.Length)
            {
                var c = src[i];
                if (verbatim && c == '"')
                {
                    if (i + 1 < src.Length && src[i + 1] == '"') { i += 2; continue; }
                    return i + 1;
                }
                if (!verbatim && c == '\\') { i += 2; continue; }
                if (!verbatim && c == '"') return i + 1;
                if (!verbatim && c == '\n') return i;
                if (interpolated && c == '{')
                {
                    if (i + 1 < src.Length && src[i + 1] == '{') { i += 2; continue; }
                    i = SkipHole(src, i + 1);
                    continue;
                }
                i++;
            }
            return i;
        }

        private static int SkipHole(string src, int i)
        {
            var depth = 0;
            while (i < src.Length)
            {
                var c = src[i];
                if (c == '"' || c == '\'') { i = SkipLiteral(src, i); continue; }
                if (c == '{') depth++;
                if (c == '}')
                {
                    if (depth == 0) return i + 1;
                    depth--;
                }
                i++;
            }
            return i;
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Semanticus.sln"))) dir = dir.Parent;
            Assert.True(dir != null, "could not find Semanticus.sln above " + AppContext.BaseDirectory);
            return dir!.FullName;
        }
    }
}
