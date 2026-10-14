// No test, smoke or tool run may open a browser or a sign-in on the desktop of whoever runs it (28 Sep).
//
// On 27 Sep a suite's human sign-in control ran for real: xdg-open raised Chromium with a Microsoft sign-in page on
// the person's screen, from a run nobody was watching. Three things now stand in the way, and this file guards all
// three so that losing any one of them fails here rather than on someone's desktop:
//
//   1. The opt-in. An engine opens the system browser only when started with --interactive-signin
//      (InteractiveSignIn), and only the VS Code window's owner spawn passes it. No test, smoke or tool does.
//   2. The chokepoints. Without the opt-in, every human-door browser sign-in refuses in plain words before anything
//      could launch (EntraToken.GetTokenAsync and the first-sign-in capture in BuildCredentialAsync).
//   3. The net. A test run's PATH puts a recording stub in front of every browser, opener and desktop launcher, names
//      it as BROWSER, and cuts the run off from the desktop session: no display, no session bus, and a runtime folder
//      of its own (InteractiveSignIn.StubBrowsersForTestRun, from SemanticusHome.IsolateForTestRun). A path that got
//      past 1 and 2 reaches a stub that writes its name down and fails, and then the whole run fails: after its last
//      test (NoBrowserTestFramework) and, for a smoke, at exit. A source scan refuses every launch shape we know of.
//
// A test run also reaches no sign-in of the person's that needs no browser (targeted round): the Azure CLI and the
// Azure Developer CLI are stubs whose config folders are empty ones of the run's own, and a sign-in store or a service
// principal named from outside is dropped (InteractiveSignIn.CutCloudSignInForTestRun). Only the two hand-run live
// probes keep the person's sign-in.
//
// The one test that must reach the browser step (the T163 positive control in AgentSigninObservedTests) uses
// InteractiveSignIn.BrowserForTests, a stand-in, and never a browser.

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    [Collection("restore-root")]   // shares EntraToken's static test seams and InteractiveSignIn.BrowserForTests
    public sealed class NoBrowserSignInGuardTests : IDisposable
    {
        private readonly string _root;
        private readonly string _prevPersistDir;
        private readonly string _prevCacheName;
        private readonly string _prevClientId;

        public NoBrowserSignInGuardTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "sem-nobrowser-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_root);
            _prevPersistDir = EntraToken.PersistDirOverride;
            _prevCacheName = EntraToken.TokenCacheNameOverride;
            _prevClientId = Environment.GetEnvironmentVariable("SEMANTICUS_ENTRA_CLIENT_ID");
            EntraToken.PersistDirOverride = Path.Combine(_root, "auth");      // an empty sign-in store: a first sign-in
            EntraToken.TokenCacheNameOverride = "semanticus-nobrowser-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            EntraToken.FabricTokenForTests = null;
            // Not a registered application, so even a browser that opened could not complete a sign-in.
            Environment.SetEnvironmentVariable("SEMANTICUS_ENTRA_CLIENT_ID", "00000000-dead-4bee-8000-000000000928");
        }

        public void Dispose()
        {
            EntraToken.PersistDirOverride = _prevPersistDir;
            EntraToken.TokenCacheNameOverride = _prevCacheName;
            Environment.SetEnvironmentVariable("SEMANTICUS_ENTRA_CLIENT_ID", _prevClientId);
            try { Directory.Delete(_root, true); } catch { }
        }

        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "pro" };
        }

        private static string StubLog() => Environment.GetEnvironmentVariable(InteractiveSignIn.StubLogVariable);

        private static string[] StubCalls()
        {
            var log = StubLog();
            return log != null && File.Exists(log) ? File.ReadAllLines(log).Where(l => l.Length > 0).ToArray() : Array.Empty<string>();
        }

        // ---- 1 and 3: the run holds no opt-in, and the net is armed ------------------------------------------------

        [Fact]
        public void A_test_run_holds_no_browser_opt_in_and_every_browser_launcher_on_its_path_is_a_stub()
        {
            Assert.False(InteractiveSignIn.Allowed, "this test run may open a browser: something passed " + InteractiveSignIn.Option);
            Assert.Null(InteractiveSignIn.BrowserForTests);
            if (OperatingSystem.IsWindows()) return;   // a browser opens through the shell there, not PATH: 1 and 2 are the net

            var log = StubLog();
            Assert.False(string.IsNullOrWhiteSpace(log), "the test run did not arm the browser stubs (" + InteractiveSignIn.StubLogVariable + " unset)");
            Assert.True(File.Exists(log));
            var stubDir = Path.GetDirectoryName(log);
            foreach (var v in InteractiveSignIn.DesktopSessionVariables)
                Assert.True(Environment.GetEnvironmentVariable(v) == null, v + " is still set in this test run: it leads to the person's desktop session");
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            Assert.Equal(Path.Combine(stubDir, "runtime"), runtime);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(runtime));
            Assert.Equal(Path.Combine(stubDir, "xdg-open"), Environment.GetEnvironmentVariable("BROWSER"));
            Assert.Equal(Path.Combine(stubDir, "chrome-config"), Environment.GetEnvironmentVariable("CHROME_CONFIG_HOME"));
            var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            var names = InteractiveSignIn.LaunchersToStub();
            foreach (var name in new[] { "xdg-open", "omarchy-launch-browser", "systemd-run", "gtk-launch", "uwsm-app", "hyprctl", "gdbus", "busctl", "dbus-send" })
                Assert.Contains(name, names);
            foreach (var name in names)
            {
                var first = path.Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
                Assert.True(first == Path.Combine(stubDir, name), $"'{name}' resolves to {first ?? "nothing"} on this run's PATH, not to the stub");
                Assert.True(File.GetUnixFileMode(first).HasFlag(UnixFileMode.UserExecute));
                Assert.Contains(InteractiveSignIn.StubMarker, File.ReadAllText(first));
                Assert.Contains("exit 1", File.ReadAllText(first));
            }
            Assert.Empty(InteractiveSignIn.NetGaps(log));
        }

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "getenv")]
        private static extern IntPtr NativeGetenv(string name);

        private static string Native(string name) => System.Runtime.InteropServices.Marshal.PtrToStringUTF8(NativeGetenv(name));

        /// <summary>The net holds for native code in the test process too, not only for managed code and child processes.
        /// .NET keeps its own copy of the environment, so a managed unset left DBUS_SESSION_BUS_ADDRESS in the native one,
        /// where MSAL's keyring check (libsecret, native, in this process) found the person's session bus and asked for
        /// the keyring (round 5 gate: OpenSession to org.freedesktop.secrets, recorded on a private bus).</summary>
        [Fact]
        public void The_desktop_session_is_cut_for_native_code_in_the_test_process_too()
        {
            if (OperatingSystem.IsWindows()) return;   // no session bus there
            foreach (var v in InteractiveSignIn.DesktopSessionVariables)
                Assert.True(Native(v) == null, v + " is still in this process's native environment, where native code such as MSAL's keyring check reads it");
            foreach (var v in new[] { "XDG_RUNTIME_DIR", "PATH", "BROWSER", "CHROME_CONFIG_HOME", InteractiveSignIn.StubLogVariable })
                Assert.True(Native(v) == Environment.GetEnvironmentVariable(v), $"{v} differs between the managed and the native environment: '{Native(v)}'");
        }

        /// <summary>A launcher called after every check in this class, or in another collection running beside it, still
        /// fails the run (round 4: the verifier's probe, named to sort after the stub check, left the class green). Proved
        /// with a second test run of this assembly that runs only <see cref="Nested_run_probe_calls_a_launcher_stub"/>:
        /// that test passes, and the run must still fail, naming the launcher.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public void A_launcher_called_after_every_check_still_fails_the_whole_run()
        {
            if (OperatingSystem.IsWindows()) return;   // no PATH net there
            var (code, output) = NestedRun(nameof(Nested_run_probe_calls_a_launcher_stub), ProbeVariable);
            Assert.Contains("Passed:     1", output);   // the probe itself passed ...
            Assert.True(code != 0, "a test run in which a launcher stub was called finished green:\n" + output);
            Assert.Contains("SEMANTICUS NO-BROWSER NET", output);   // ... and the run failed on what it called
            Assert.Contains("omarchy-launch-browser https://example.invalid/nested-run-probe", output);
        }

        /// <summary>A second test run of this assembly that runs only <paramref name="test"/>, with
        /// <paramref name="probeVariable"/> set so that probe does its work there and nowhere else. The nested run arms a
        /// net of its own: nothing it does may land in this run's log or home.</summary>
        private (int Code, string Output) NestedRun(string test, string probeVariable, System.Collections.Generic.IDictionary<string, string> extra = null)
        {
            var dotnet = Environment.ProcessPath;
            Assert.True(dotnet != null && Path.GetFileNameWithoutExtension(dotnet) == "dotnet", "the test host is not run by dotnet: " + dotnet);
            var psi = new System.Diagnostics.ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "test", typeof(NoBrowserSignInGuardTests).Assembly.Location, "--filter",
                "FullyQualifiedName=Semanticus.Tests.NoBrowserSignInGuardTests." + test })
                psi.ArgumentList.Add(a);
            foreach (var k in new[] { InteractiveSignIn.StubLogVariable, SemanticusHome.LockVariable })
                psi.Environment.Remove(k);
            var nestedHome = Path.Combine(_root, "nested-home-" + test);
            Directory.CreateDirectory(nestedHome);
            psi.Environment[SemanticusHome.Variable] = nestedHome;
            psi.Environment[probeVariable] = "1";
            foreach (var kv in extra ?? new System.Collections.Generic.Dictionary<string, string>()) psi.Environment[kv.Key] = kv.Value;
            using var run = System.Diagnostics.Process.Start(psi)!;
            var stdout = run.StandardOutput.ReadToEndAsync();
            var stderr = run.StandardError.ReadToEndAsync();
            Assert.True(run.WaitForExit(180_000), "the nested test run did not finish in 3 minutes");
            return (run.ExitCode, stdout.Result + stderr.Result);
        }

        private const string ProbeVariable = "SEMANTICUS_NO_BROWSER_NESTED_PROBE";

        // ---- 3, the person's cloud sign-in: the Azure CLI and every saved sign-in that needs no browser ---------------

        /// <summary>A test run reaches no sign-in of the person's that needs no browser (verifier, targeted round, F1: a
        /// push test asked the real az for a Power BI token on the person's own ~/.azure and connected to api.powerbi.com
        /// with it, and the sign-in probe ran az account show there). The Azure CLI and the Azure Developer CLI are stubs
        /// like the launchers, their config folders are empty folders of the run's own (so even one run by its full path
        /// finds nobody signed in), a sign-in store named from outside is dropped (so no saved account of the person's
        /// can be used silently), and so are the service principal variables. In both environments.</summary>
        [Fact]
        public void A_test_run_can_reach_no_sign_in_of_the_persons_that_needs_no_browser()
        {
            var config = Environment.GetEnvironmentVariable("AZURE_CONFIG_DIR");
            Assert.False(string.IsNullOrWhiteSpace(config), "AZURE_CONFIG_DIR is not set in this test run, so the Azure CLI reads the person's own ~/.azure");
            Assert.True(Directory.Exists(config), "AZURE_CONFIG_DIR names no folder: " + config);
            var azd = Environment.GetEnvironmentVariable("AZD_CONFIG_DIR");
            Assert.False(string.IsNullOrWhiteSpace(azd), "AZD_CONFIG_DIR is not set in this test run, so the Azure Developer CLI reads the person's own ~/.azd");
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var own in new[] { config, azd })
                Assert.False(SemanticusHome.SameFolder(own, Path.Combine(home, ".azure")) || SemanticusHome.SameFolder(own, Path.Combine(home, ".azd")),
                    "a test run's Azure CLI folder is the person's own: " + own);
            Assert.True(Environment.GetEnvironmentVariable(SemanticusHome.SignInVariable) == null,
                SemanticusHome.SignInVariable + " is set in this test run, so a sign-in store named from outside (the person's) is in use");
            if (OperatingSystem.IsWindows()) return;   // no PATH net there; the folders above are the net

            var stubDir = Path.GetDirectoryName(StubLog());
            Assert.Equal(Path.Combine(stubDir, "azure-config"), config);
            Assert.Equal(Path.Combine(stubDir, "azd-config"), azd);
            var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            foreach (var name in new[] { "az", "azd" })
            {
                Assert.Contains(name, InteractiveSignIn.CloudSignInHelpers);
                var first = path.Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
                Assert.True(first == Path.Combine(stubDir, name), $"'{name}' resolves to {first ?? "nothing"} on this run's PATH, not to the stub");
                Assert.Contains(InteractiveSignIn.StubMarker, File.ReadAllText(first));
            }
            Assert.Empty(InteractiveSignIn.NetGaps(StubLog()));
            // The native environment, which no test changes: other collections set the service principal names in the
            // managed one for a moment, so that one is checked by the nested run below.
            foreach (var v in new[] { "AZURE_CONFIG_DIR", "AZD_CONFIG_DIR" })
                Assert.True(Native(v) == Environment.GetEnvironmentVariable(v), $"{v} differs between the managed and the native environment: '{Native(v)}'");
            foreach (var v in InteractiveSignIn.PersonsSignInVariables)
                Assert.True(Native(v) == null, v + " is still in this process's native environment");
        }

        /// <summary>An Azure CLI sign-in anywhere in a test run fails the whole run and names it, like a launcher. Proved
        /// with a second test run of this assembly whose one test does what the push and the sign-in probe did: the probe
        /// test passes, and the run must still fail, naming both az calls.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public void An_Azure_CLI_sign_in_anywhere_in_a_test_run_fails_the_whole_run_naming_it()
        {
            if (OperatingSystem.IsWindows()) return;   // no PATH net there
            // Planted as a shell that carries them would: none may reach the nested run's tests.
            var planted = InteractiveSignIn.PersonsSignInVariables.ToDictionary(v => v, v => v == SemanticusHome.SignInVariable ? Path.Combine(_root, "persons-sign-ins") : "planted-" + v);
            var (code, output) = NestedRun(nameof(Nested_run_probe_asks_the_Azure_CLI_for_a_token), AzProbeVariable, planted);
            Assert.True(output.Contains("Passed:     1"), "the probe did not pass in the nested run:\n" + output);
            Assert.True(code != 0, "a test run in which the Azure CLI was asked for a token finished green:\n" + output);
            Assert.Contains("SEMANTICUS NO-BROWSER NET", output);
            Assert.Contains("az account get-access-token --output json --resource https://analysis.windows.net/powerbi/api", output);
            Assert.Contains("az account show -o json", output);
        }

        private const string AzProbeVariable = "SEMANTICUS_NO_AZ_NESTED_PROBE";

        /// <summary>Only ever does anything inside the nested run above. It stops before any call unless az on PATH is the
        /// net's stub, so it can never reach the real Azure CLI even if the net were broken. Then it runs what reached the
        /// person's az before: the sign-in probe, and a push preview to a published model as the person.</summary>
        [Fact]
        public async Task Nested_run_probe_asks_the_Azure_CLI_for_a_token()
        {
            if (Environment.GetEnvironmentVariable(AzProbeVariable) != "1" || OperatingSystem.IsWindows()) return;
            var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            var az = path.Select(d => Path.Combine(d, "az")).FirstOrDefault(File.Exists);
            Assert.True(az != null && File.ReadAllText(az).Contains(InteractiveSignIn.StubMarker), "the Azure CLI on this run's PATH is not the net's stub (" + (az ?? "none") + "), so nothing was called");
            foreach (var v in InteractiveSignIn.PersonsSignInVariables)
                Assert.True(Environment.GetEnvironmentVariable(v) == null, v + ", planted by the outer run, reached this test run");
            using var engine = new LocalEngine(new SessionManager());
            await engine.OpenAsync(TestModels.FindBim());
            var probe = await engine.ProbeAuthPrerequisitesAsync("azcli", null);
            Assert.False(probe.Ready);
            var workspace = new ModelRef { Kind = "workspace", Endpoint = "powerbi://api.powerbi.com/v1.0/myorg/Contoso", Database = "Contoso" };
            try { await engine.ApplyDiffAsync(new ModelRef { Kind = "session" }, workspace, new[] { "measure:Sales/A" }, false, "human"); }
            catch { /* the stub refuses: nobody is signed in */ }
            var calls = StubCalls();
            Assert.Contains("az account show -o json", calls);
            Assert.Contains(calls, l => l.StartsWith("az account get-access-token ", StringComparison.Ordinal));
        }

        /// <summary>Every omarchy-launch-* script on PATH is stubbed, not only the ones the list names: one Omarchy adds
        /// later opens a browser just the same (round 4). A name a shell would read as more than a name is left alone.
        /// This part of the net had no test (verifier, targeted round, L3).</summary>
        [Fact]
        public void Every_omarchy_launcher_on_the_path_is_stubbed_even_one_the_list_does_not_name()
        {
            const string prefix = "omarchy-launch-";
            var dir = Path.Combine(_root, "bin");
            Directory.CreateDirectory(dir);
            foreach (var n in new[] { prefix + "zz-new", prefix + "zz bad", prefix + "zz$x" })
                File.WriteAllText(Path.Combine(dir, n), "#!/bin/sh\nexit 0\n");
            var names = InteractiveSignIn.LaunchersToStub(string.Join(Path.PathSeparator, dir, Path.Combine(_root, "missing")));
            Assert.Contains(prefix + "zz-new", names);
            Assert.DoesNotContain(prefix + "zz bad", names);
            Assert.DoesNotContain(prefix + "zz$x", names);
            Assert.Equal(names.Length, names.Distinct().Count());
            foreach (var listed in InteractiveSignIn.BrowserLaunchers) Assert.Contains(listed, names);
        }

        /// <summary>The engine builds only the credentials whose sign-in this net covers: the browser (the opt-in and the
        /// chokepoints), device code (it prints a code and opens nothing; it signs in only when a person enters the code,
        /// and a test run's sign-in store is its own, so no saved account can sign it in silently), the Azure CLI (stubbed,
        /// its folder the run's own) and a service principal (its variables dropped). Any other Azure.Identity credential,
        /// managed identity, the Azure PowerShell or Visual Studio sign-in, the default chain, reaches a sign-in the net
        /// does not cover, so building one fails here. Read from the compiled engine, so no spelling hides one.</summary>
        [Fact]
        public void The_engine_builds_only_the_credentials_whose_sign_in_the_test_run_net_covers()
        {
            var credential = typeof(Azure.Core.TokenCredential);
            var built = new[] { typeof(LocalEngine).Assembly }
                .SelectMany(CompiledCallGraph.Callees)
                .Where(m => m is System.Reflection.ConstructorInfo && credential.IsAssignableFrom(m.DeclaringType)
                    && m.DeclaringType!.Assembly.GetName().Name!.StartsWith("Azure.", StringComparison.Ordinal))
                .Select(m => m.DeclaringType!.FullName)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(new[]
            {
                "Azure.Identity.AzureCliCredential", "Azure.Identity.ClientSecretCredential", "Azure.Identity.DeviceCodeCredential",
                "Azure.Identity.InteractiveBrowserCredential",
            }, built);
        }

        /// <summary>No test registers code that runs as the test process exits. That code runs after the run's last check,
        /// where a launcher it calls is caught by its stub but fails nothing, because dotnet test does not read the test
        /// host's exit code (verifier, targeted round, L2). Read from the compiled tests, so a lambda cannot hide one.</summary>
        [Fact]
        public void No_test_runs_code_as_the_test_process_exits()
        {
            var exitHooks = new System.Reflection.MethodBase[]
            {
                typeof(AppDomain).GetEvent(nameof(AppDomain.ProcessExit))!.GetAddMethod()!,
                typeof(System.Runtime.Loader.AssemblyLoadContext).GetEvent(nameof(System.Runtime.Loader.AssemblyLoadContext.Unloading))!.GetAddMethod()!,
                typeof(System.Runtime.InteropServices.PosixSignalRegistration).GetMethod(nameof(System.Runtime.InteropServices.PosixSignalRegistration.Create))!,
            };
            var callers = CompiledCallGraph.CallSites(typeof(NoBrowserSignInGuardTests).Assembly)
                .Where(c => exitHooks.Contains(c.Callee))
                .Select(c => c.Caller.DeclaringType!.FullName + "." + c.Caller.Name + " -> " + c.Callee.Name)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            Assert.True(callers.Count == 0, "test code runs as the test process exits, after the run's last check:\n" + string.Join("\n", callers));
        }

        /// <summary>Only ever does anything inside the nested run above, which sets <see cref="ProbeVariable"/>. It calls the
        /// omarchy-launch-browser STUB by its full path, after checking it is the stub, so it can never reach the real
        /// launcher even if the net were broken. Anywhere else it returns at once.</summary>
        [Fact]
        public void Nested_run_probe_calls_a_launcher_stub()
        {
            if (Environment.GetEnvironmentVariable(ProbeVariable) != "1" || OperatingSystem.IsWindows()) return;
            var stub = Path.Combine(Path.GetDirectoryName(StubLog())!, "omarchy-launch-browser");
            Assert.Contains(InteractiveSignIn.StubMarker, File.ReadAllText(stub));
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(stub, "https://example.invalid/nested-run-probe") { UseShellExecute = false })!;
            p.WaitForExit(10_000);
            Assert.NotEmpty(StubCalls());
        }

        /// <summary>A smoke fails at exit when its stub log is not empty (the process-exit check), and a stub log it
        /// inherited without its stubs armed is not trusted (Sol, round 4: a shell carrying the variable switched the net
        /// off). Proved on the real PerfSmoke, which isolates first and then refuses a missing fixture (exit 2).</summary>
        [PerfSmokeFact]
        public void A_smoke_fails_at_exit_after_a_launcher_call_and_never_trusts_an_unarmed_stub_log()
        {
            var smoke = PerfSmokeFactAttribute.Find();
            var scratch = Path.Combine(_root, "smoke");

            // An armed net with one real stub call in its log: the smoke keeps it, and exits with the launch code.
            var armed = InteractiveSignIn.WriteStubs(Path.Combine(scratch, "armed"));
            var stub = Path.Combine(Path.GetDirectoryName(armed)!, "omarchy-launch-browser");
            Assert.Contains(InteractiveSignIn.StubMarker, File.ReadAllText(stub));
            using (var call = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(stub, "https://example.invalid/smoke-probe") { UseShellExecute = false })!)
                call.WaitForExit(10_000);
            var withCall = RunSmoke(smoke, Path.Combine(scratch, "home-a"), armed, Path.GetDirectoryName(armed));
            Assert.True(withCall.Code == InteractiveSignIn.LaunchAttemptExitCode,
                $"a smoke whose stub log holds a launcher call exited {withCall.Code}, not {InteractiveSignIn.LaunchAttemptExitCode}:\n{withCall.Output}");
            Assert.Contains("omarchy-launch-browser https://example.invalid/smoke-probe", withCall.Output);

            // The same smoke with a clean armed net exits as it would anyway.
            var clean = InteractiveSignIn.WriteStubs(Path.Combine(scratch, "clean"));
            var noCall = RunSmoke(smoke, Path.Combine(scratch, "home-b"), clean, Path.GetDirectoryName(clean));
            Assert.True(noCall.Code == 2, $"a smoke with a clean net exited {noCall.Code}:\n{noCall.Output}");

            // An inherited log with no stubs on PATH: the smoke arms its own under its home and never uses that log.
            var bogus = Path.Combine(scratch, "bogus", "calls.log");
            Directory.CreateDirectory(Path.GetDirectoryName(bogus)!);
            File.WriteAllText(bogus, "");
            var homeC = Path.Combine(scratch, "home-c");
            var unarmed = RunSmoke(smoke, homeC, bogus, null);
            Assert.True(unarmed.Code == 2, $"exit {unarmed.Code}:\n{unarmed.Output}");
            Assert.True(File.Exists(Path.Combine(homeC, "no-browser", "omarchy-launch-browser")),
                "a smoke that inherited a stub log with no stubs armed ran without a net of its own");
        }

        private static (int Code, string Output) RunSmoke(string smoke, string home, string stubLog, string stubDir)
        {
            Directory.CreateDirectory(home);
            var psi = new System.Diagnostics.ProcessStartInfo(smoke) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "--fixture", Path.Combine(home, "missing.bim") }) psi.ArgumentList.Add(a);
            var runtimeDir = new DirectoryInfo(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
            psi.Environment["DOTNET_ROOT"] = runtimeDir.Parent?.Parent?.Parent?.FullName ?? runtimeDir.FullName;
            psi.Environment.Remove(SemanticusHome.LockVariable);
            psi.Environment[SemanticusHome.Variable] = home;
            psi.Environment[InteractiveSignIn.StubLogVariable] = stubLog;
            // This run's own stub folder leaves PATH, so only the net under test can guard the smoke.
            var mine = Path.GetDirectoryName(StubLog());
            var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(d => d != mine);
            psi.Environment["PATH"] = string.Join(Path.PathSeparator, stubDir == null ? path : new[] { stubDir }.Concat(path));
            if (stubDir != null) psi.Environment["BROWSER"] = Path.Combine(stubDir, "xdg-open");
            else psi.Environment.Remove("BROWSER");
            using var p = System.Diagnostics.Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(120_000)) { try { p.Kill(true); } catch { } return (-1, "timed out\n" + o.Result + e.Result); }
            return (p.ExitCode, o.Result + e.Result);
        }

        internal sealed class PerfSmokeFactAttribute : FactAttribute
        {
            public PerfSmokeFactAttribute()
            {
                if (OperatingSystem.IsWindows()) Skip = "NOT RUN on Windows: the PATH net does not exist there. This is not a pass.";
                else if (Find() == null) Skip = "NOT RUN: no built Semanticus.PerfSmoke beside the tests (build the solution first). This is not a pass.";
            }

            public static string Find()
            {
                var here = new DirectoryInfo(AppContext.BaseDirectory);      // .../Semanticus.Tests/bin/<config>/net8.0/
                var config = here.Parent?.Name;
                var root = here.Parent?.Parent?.Parent?.Parent?.FullName;
                if (config == null || root == null) return null;
                var path = Path.Combine(root, "Semanticus.PerfSmoke", "bin", config, "net8.0", "Semanticus.PerfSmoke");
                return File.Exists(path) ? path : null;
            }
        }

        // ---- 2: every human-door browser sign-in refuses without the opt-in, and reaches nothing -------------------

        [Fact]
        public async Task Every_human_browser_sign_in_refuses_without_the_opt_in_and_reaches_no_launcher()
        {
            using var press = SignInPress.Press();   // a person's press: the sign-in is the one that would open a window, so the opt-in is what refuses it
            var before = StubCalls().Length;

            // The first-sign-in capture (an empty store): where a browser used to open for the account picker.
            var capture = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                EntraToken.BuildCredentialAsync("interactive", null, CancellationToken.None));
            Assert.Equal(InteractiveSignIn.RefusedText, capture.Message);
            var forced = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                EntraToken.BuildCredentialAsync("entra", null, CancellationToken.None, forceReauth: true));
            Assert.Equal(InteractiveSignIn.RefusedText, forced.Message);

            // A token acquisition: where a browser credential used to open the browser when it had no cached token.
            var xmla = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                EntraToken.AcquireFullAsync("interactive", null, CancellationToken.None));
            Assert.Equal(InteractiveSignIn.RefusedText, xmla.Message);
            var sql = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                EntraToken.AcquireSqlAsync("mfa", null, CancellationToken.None));
            Assert.Equal(InteractiveSignIn.RefusedText, sql.Message);

            // Through a real engine call on the human door.
            using (var engine = new LocalEngine(new SessionManager(), new Pro()))
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                var r = await engine.FabricGitStatusAsync("ws-nobrowser", "interactive", null, "human", cts.Token);
                Assert.Contains(InteractiveSignIn.RefusedText, r.Error ?? "");
            }

            Assert.Equal(before, StubCalls().Length);
            Assert.False(Directory.Exists(EntraToken.PersistDirOverride)
                && Directory.GetFiles(EntraToken.PersistDirOverride, "record-*.json").Length > 0, "a sign-in record was written");
        }

        /// <summary>The agent door never builds a credential that may prompt, so it never even reaches the stand-in.</summary>
        [Fact]
        public async Task The_agent_door_never_reaches_the_browser_step_at_all()
        {
            var reached = 0;
            InteractiveSignIn.BrowserForTests = _ => Interlocked.Increment(ref reached);
            try
            {
                await Assert.ThrowsAnyAsync<Exception>(() =>
                    EntraToken.AcquireFullAsync("interactive", null, CancellationToken.None, disableInteractive: true));
                using var engine = new LocalEngine(new SessionManager(), new Pro());
                var r = await engine.FabricGitStatusAsync("ws-nobrowser", "interactive", null, "agent", CancellationToken.None);
                Assert.False(string.IsNullOrWhiteSpace(r.Error));
            }
            finally { InteractiveSignIn.BrowserForTests = null; }
            Assert.Equal(0, reached);
        }

        // ---- the sources: no test, smoke or tool can open a browser, and only a person's window passes the opt-in ---

        private static readonly Regex OptIn = new Regex(@"--interactive-signin|InteractiveSignIn\.(ApplyCommandLine|Option)", RegexOptions.Compiled);

        /// <summary>Every shape in fixtures/no-browser/launch-shapes.txt (the verifier's 18 from round 4, and more of the
        /// same families) is caught by the source scan. The scan saw 3 of the 18 before.</summary>
        [Fact]
        public void The_source_scan_catches_every_known_launch_shape()
        {
            var shapes = File.ReadAllLines(Path.Combine(RepoRoot(), "Semanticus.Tests", "fixtures", "no-browser", "launch-shapes.txt"))
                .Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith("#", StringComparison.Ordinal)).ToList();
            Assert.True(shapes.Count >= 18, $"only {shapes.Count} launch shapes listed");
            var missed = shapes.Where(l => LaunchScan.Match(l) == null).ToList();
            Assert.True(missed.Count == 0, "the source scan misses these launch shapes:\n" + string.Join("\n", missed));
            // And it does not cry wolf on ordinary code.
            foreach (var quiet in new[]
            {
                "var code = err.TryGetProperty(\"code\", out var c) ? c.GetString() : null;",
                "if (['key', 'id', 'number', 'code', 'ordinal'].some((s) => n.endsWith(s))) return 'none';",
                "using var fs = File.Open(path, FileMode.Open);",
                "const opened = await openFile(path, { flag: 'r' });",
                "for (const k of ['DISPLAY', 'WAYLAND_DISPLAY', 'BROWSER', 'ANTHROPIC_API_KEY']) delete env[k];",
                "delete env.BROWSER;",
                "code = err.code; if (code === 'ENOENT') return open ? 'a' : 'b';",
                "const shell = 'bash'; const opened = open(path);",
                "const status = signInProblem(\"AzureCliCredential authentication failed: ERROR: Please run 'az login' to setup account.\");",
            })
                Assert.True(LaunchScan.Match(quiet) == null, "the scan flags ordinary code: " + quiet + " (" + LaunchScan.Match(quiet) + ")");
        }

        [Fact]
        public void No_test_smoke_or_tool_source_opens_a_browser_or_passes_the_opt_in()
        {
            var repo = RepoRoot();
            var roots = new[]
            {
                "Semanticus.Tests", "Semanticus.Smoke", "Semanticus.RpcSmoke", "Semanticus.McpSmoke", "Semanticus.AirSmoke",
                "Semanticus.CicdSmoke", "Semanticus.LearnSmoke", "Semanticus.LearnBench", "Semanticus.PerfSmoke",
                "Semanticus.LiveProbe", "Semanticus.ReviewProbe", "Semanticus.AuthTest", "tools", Path.Combine("Semanticus.VSCode", "test"),
                Path.Combine("Semanticus.VSCode", "src", "test"), Path.Combine("Semanticus.VSCode", "scripts"),
            };
            var files = LaunchScan.SourceFiles(repo, roots);
            Assert.True(files.Count > 100, $"only {files.Count} source files found under the test, smoke and tool folders");
            var hits = files
                .SelectMany(f => LaunchScan.CodeLines(f).Select(x => (f, x.Number, x.Text)))
                .Where(x => !AllowedLines.Value.Contains((Path.GetRelativePath(repo, x.f).Replace('\\', '/'), x.Text.Trim())))
                .Select(x => (x.f, x.Number, x.Text, Why: LaunchScan.Match(x.Text)
                    ?? (OptIn.IsMatch(x.Text) && Path.GetFileName(x.f) != "engine-lifetime.test.mjs" ? "passes the opt-in" : null)))
                .Where(x => x.Why != null)
                .Select(x => $"{Path.GetRelativePath(repo, x.f)}:{x.Number}: [{x.Why}] {x.Text.Trim()}")
                .ToList();
            Assert.True(hits.Count == 0, "a test, smoke or tool source can open a browser, reach the desktop or passes the sign-in opt-in:\n" + string.Join("\n", hits));
        }

        /// <summary>Every smoke and probe that uses the engine arms the net before its first sign-in or engine call
        /// (round 4: LiveProbe signed in with the person's own store, unguarded, and only then isolated; AuthTest never
        /// isolated at all). LiveProbe, ReviewProbe and AuthTest are not in the solution, so no test can run them.</summary>
        [Fact]
        public void Every_smoke_and_probe_arms_the_net_before_it_signs_in_or_starts_the_engine()
        {
            var repo = RepoRoot();
            var programs = new[]
            {
                "Semanticus.RpcSmoke", "Semanticus.McpSmoke", "Semanticus.AirSmoke", "Semanticus.CicdSmoke", "Semanticus.LearnSmoke",
                "Semanticus.LearnBench", "Semanticus.PerfSmoke", "Semanticus.LiveProbe", "Semanticus.ReviewProbe", "Semanticus.AuthTest",
            };
            var late = new System.Collections.Generic.List<string>();
            foreach (var name in programs)
            {
                var code = string.Join("\n", LaunchScan.CodeLines(Path.Combine(repo, name, "Program.cs")).Select(x => x.Text));
                var isolate = code.IndexOf("SemanticusHome.IsolateForTestRun(", StringComparison.Ordinal);
                var first = new[] { "EntraToken.", "new LocalEngine(", "new SessionManager(", "Process.Start(", "new ProcessStartInfo" }
                    .Select(t => code.IndexOf(t, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(int.MaxValue).Min();
                if (isolate < 0 || isolate > first) late.Add(name + (isolate < 0 ? ": never isolates" : ": signs in or starts the engine before it isolates"));
            }
            Assert.True(late.Count == 0, string.Join("\n", late));
        }

        /// <summary>Only the two live probes a person runs by hand against an endpoint they name (LiveProbe, AuthTest) keep
        /// the person's own sign-in, the Azure CLI included, because signing in as the person is what they test. Only
        /// CicdSmoke keeps a service principal, for its live gate that runs only when one is set. The test run and every
        /// other smoke cut both (verifier, targeted round, F1).</summary>
        [Fact]
        public void Only_the_live_probes_keep_the_persons_sign_in()
        {
            var repo = RepoRoot();
            var keeps = new System.Collections.Generic.Dictionary<string, string>
            {
                ["Semanticus.LiveProbe"] = "CloudSignIn.KeepPersons", ["Semanticus.AuthTest"] = "CloudSignIn.KeepPersons",
                ["Semanticus.CicdSmoke"] = "CloudSignIn.KeepServicePrincipal",
            };
            var wrong = new System.Collections.Generic.List<string>();
            foreach (var name in new[] { "Semanticus.RpcSmoke", "Semanticus.McpSmoke", "Semanticus.AirSmoke", "Semanticus.CicdSmoke", "Semanticus.LearnSmoke",
                "Semanticus.LearnBench", "Semanticus.PerfSmoke", "Semanticus.LiveProbe", "Semanticus.ReviewProbe", "Semanticus.AuthTest" })
            {
                var calls = LaunchScan.CodeLines(Path.Combine(repo, name, "Program.cs")).Select(x => x.Text).Where(t => t.Contains("IsolateForTestRun(")).ToList();
                keeps.TryGetValue(name, out var expected);
                if (calls.Count != 1) wrong.Add($"{name}: {calls.Count} isolate calls");
                else if (expected == null ? calls[0].Contains("CloudSignIn.") : !calls[0].Contains(expected)) wrong.Add($"{name}: {calls[0].Trim()} (expected {expected ?? "the default, which cuts every sign-in"})");
            }
            var tests = File.ReadAllText(Path.Combine(repo, "Semanticus.Tests", "TestEnvironment.cs"));
            Assert.Contains("SemanticusHome.IsolateForTestRun(\"tests\");", tests);
            Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        }

        // The lines the scan may pass, by file and exact text, from fixtures/no-browser/scan-allowed.txt. Each names a
        // launcher or the variable without starting anything. It is a data file so that this file, which is scanned like
        // every other, does not name them twice; adding a line there is a reviewed change, never a comment in the file.
        private static readonly Lazy<System.Collections.Generic.HashSet<(string File, string Line)>> AllowedLines = new(() =>
            File.ReadAllLines(Path.Combine(RepoRoot(), "Semanticus.Tests", "fixtures", "no-browser", "scan-allowed.txt"))
                .Where(l => l.Contains('\t') && !l.StartsWith("#", StringComparison.Ordinal))
                .Select(l => (l.Substring(0, l.IndexOf('\t')), l.Substring(l.IndexOf('\t') + 1).Trim()))
                .ToHashSet());

        /// <summary>The source scan: what opens a web page or reaches the desktop from a process, one line at a time.
        /// Built from the product's own stub list, so a launcher added to the net is scanned for too.</summary>
        internal static class LaunchScan
        {
            // Names that are ordinary words in code: a launcher only as the program a process call starts, or as the
            // first word of a shell line.
            private static readonly string[] Common = { "open", "gio", "code", "codium", "firefox", "chromium", "brave", "vivaldi", "opera", "epiphany", "librewolf", "uwsm", "flatpak" };
            private static readonly string[] Shell = Common.Except(new[] { "open", "code" }).ToArray();
            // Compiled once: the scans run these patterns over every line of the test, smoke, tool and product sources (lean suite).
            private static Regex R(string pattern, RegexOptions options = RegexOptions.None) => new Regex(pattern, options | RegexOptions.Compiled);
            private const string Q = "[\"'`]";

            // A browser's own name, as a whole string ("firefox", 'chromium.exe'): the program a process call, an env
            // wrapper or a path built from parts starts.
            private static readonly string Browser =
                @"(chromium|chromium-browser|google-chrome|google-chrome-stable|chrome|msedge|microsoft-edge|microsoft-edge-stable|firefox|firefox-esr|"
                + @"brave|brave-browser|vivaldi|vivaldi-stable|opera|librewolf|epiphany|qutebrowser|safari)";

            // What may stand in front of a command on a shell line and still run it: env with its options and
            // assignments, timeout with its duration, and the other wrappers.
            private const string Wrappers =
                @"((exec|nohup|setsid|command|builtin|doas|xvfb-run|dbus-launch|stdbuf(\s+-\S+)*|nice(\s+-n)?(\s+-?\d+)?|ionice(\s+-\S+)*|sudo(\s+-\S+)*"
                + @"|env(\s+-\S+)*(\s+[A-Za-z_]\w*=\S*)*|timeout(\s+-\S+)*\s+\S+)\s+)*";

            // Where a command begins: a shell line or a command after a separator, and a string that is a shell
            // command, handed to a call, assigned, or run with -c.
            private const string CommandStart = @"(^|[;&|(]|\bthen\b|\bdo\b|[(,=]\s*" + Q + @"|-c\s+\\?" + Q + @"?|-c\s*" + Q + @"\s*,\s*" + Q + ")";

            // code and open are ordinary words in code, so as a command they need an argument that looks like one.
            private const string ArgLike = @"(?=[""'`$\-/~%@]|\.(\s|/|$))";

            private static readonly (string Why, Regex Rx)[] Rules =
            {
                ("a launcher by name", R(@"(?<![\w.-])(" + string.Join("|", InteractiveSignIn.LaunchersToStub().Except(Common).Select(Regex.Escape))
                    + @"|omarchy-launch-[\w-]+)(?![\w-])", RegexOptions.IgnoreCase)),
                ("a process started on a launcher", R(
                    @"(\bspawn(Sync)?|\bexecFile(Sync)?|\bexec(Sync)?|\bexeca|Process\]?(\.|::)Start|ProcessStartInfo|FileName\s*=|Start-Process|\bsubprocess\.\w+|\bPopen|\bos\.(system|exec\w*))"
                    + @"\s*\(?\s*\[?\s*" + Q + @"\s*([^""'`\s]*/)?(" + string.Join("|", Common) + ")(?=[\"'`\\s])", RegexOptions.IgnoreCase)),
                ("a shell line that starts a browser", R(CommandStart + @"\s*" + Wrappers + @"([^\s""'`]*/)?((" + string.Join("|", Shell) + @")\s|(code|open)\s+" + ArgLike + ")", RegexOptions.IgnoreCase)),
                ("a browser named as a program", R(Q + Browser + @"(\.exe|\.desktop)?" + Q, RegexOptions.IgnoreCase)),
                ("a browser by its full path", R(@"/(usr|opt|snap|Applications)/[^""'`\s]*(chrom|firefox|brave|edge|vivaldi|opera|librewolf|epiphany|safari)", RegexOptions.IgnoreCase)),
                ("the BROWSER variable", R(@"\$\{?BROWSER\b|\$env:BROWSER\b|process\.env\.BROWSER\b|process\.env\[\s*" + Q + @"BROWSER|GetEnvironmentVariables?\(\s*\)?\s*\[?\s*""BROWSER""|os\.environ(\.get)?\s*[\[(]\s*" + Q + "BROWSER"
                    + @"|\{[^}]*\bBROWSER\b[^}]*\}\s*=\s*(process\.)?env\b|(?<!delete\s+)\benv\w*\s*(\.BROWSER\b|\[\s*" + Q + @"BROWSER)|(^|\s)BROWSER=\S")),
                ("the npm open package", R(@"(\bimport\s*\(|\brequire\s*\()\s*" + Q + "open" + Q + @"|\bfrom\s+" + Q + "open" + Q)),
                ("a browser credential", R(@"\bInteractiveBrowserCredential\b|\bAcquireTokenInteractive\b|\bWithSystemWebViewOptions\b|\bWithUseEmbeddedWebView\b")),
                ("the opt-in set by reflection", R(@"[""']_allowed[""']|typeof\(\s*([\w.]+\.)?InteractiveSignIn\s*\)\s*\.\s*Get(Field|Property|Member|Method)")),
                ("a shell-executed start", R(@"UseShellExecute\s*=\s*true|\bwebbrowser\b|Start-Process\s+(-FilePath\s+)?(\$|['""]?https?:|['""]?(\w+/)*" + Browser + @"\b|['""]?code\b)"
                    + @"|\bstart\s+['""]?https?:|/c\s+start\b|FileProtocolHandler|\bInvoke-Item\b|(^|[;|{]\s*)ii\s+\$|\bexplorer(\.exe)?\s+['""]?(\$|https?:)|Process\]::Start\(\s*\$", RegexOptions.IgnoreCase)),
                ("a headed browser", R(@"\bheadless\s*[:=]\s*(false|False|0)\b|--headed\b")),
                ("a GitHub CLI web page", R(@"\bgh\s+(browse|auth\s+login)\b|\bgh\s[^\n]*\s--web\b|" + Q + @"gh" + Q + @"\s*,\s*\[?\s*" + Q + "browse")),
                ("the person's Azure sign-in", R(CommandStart + @"\s*" + Wrappers + @"([^\s""'`]*/)?(az|azd)\s+[a-z]|" + Q + @"(az|azd)" + Q + @"\s*,\s*\[?\s*" + Q + @"(login|logout|account|ad|auth|rest|group|resource|role|keyvault|storage|config|extension|devops|init|up|provision|env)\b"
                    + @"|\bConnect-AzAccount\b|\bGet-AzAccessToken\b|\bnew\s+(Azure\.Identity\.)?(DefaultAzure|ManagedIdentity|AzureCli|AzurePowerShell|AzureDeveloperCli|VisualStudio|VisualStudioCode|SharedTokenCache|Environment|WorkloadIdentity|ChainedToken)Credential\b"
                    + @"|=\s*DefaultAzureCredential\(")),
            };

            /// <summary>Why this line opens a browser, reaches the desktop or signs in as the person, or null. Product code
            /// (<paramref name="credentials"/> false) builds credentials and runs the Azure CLI on purpose, behind the
            /// opt-in and the chokepoints, so there only the launches count.</summary>
            public static string Match(string line, bool credentials = true)
            {
                foreach (var (why, rx) in Rules)
                    if ((credentials || (why != "a browser credential" && why != "the person's Azure sign-in")) && rx.IsMatch(line)) return why;
                return null;
            }

            public static System.Collections.Generic.List<string> SourceFiles(string repo, System.Collections.Generic.IEnumerable<string> roots)
            {
                var exts = new[] { ".cs", ".mjs", ".js", ".cjs", ".ts", ".py", ".ps1", ".psm1", ".sh", ".bash" };
                return roots.Select(r => Path.Combine(repo, r)).Where(Directory.Exists)
                    .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
                    .Where(f => exts.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj" or "node_modules" or "out" or "dist" or "external"))
                    .ToList();
            }

            /// <summary>The lines of a source file that are code: comment lines (//, a # line in a script, a block comment
            /// that starts a line) are left out, because a comment starts nothing. A block comment in the middle of a line
            /// is kept, so a launch can never hide after one.</summary>
            public static System.Collections.Generic.IEnumerable<(int Number, string Text)> CodeLines(string file)
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                var hashComments = ext is ".py" or ".ps1" or ".psm1" or ".sh" or ".bash";
                var inBlock = false;
                var n = 0;
                foreach (var raw in File.ReadLines(file))
                {
                    n++;
                    var line = raw.TrimStart();
                    if (inBlock)
                    {
                        var close = ext is ".ps1" or ".psm1" ? line.IndexOf("#>", StringComparison.Ordinal) : line.IndexOf("*/", StringComparison.Ordinal);
                        if (close < 0) continue;
                        inBlock = false;
                        line = line.Substring(close + 2).TrimStart();
                    }
                    while (line.StartsWith("/*", StringComparison.Ordinal) || ((ext is ".ps1" or ".psm1") && line.StartsWith("<#", StringComparison.Ordinal)))
                    {
                        var close = line.StartsWith("<#", StringComparison.Ordinal) ? line.IndexOf("#>", 2, StringComparison.Ordinal) : line.IndexOf("*/", 2, StringComparison.Ordinal);
                        if (close < 0) { inBlock = true; line = ""; break; }
                        line = line.Substring(close + 2).TrimStart();
                    }
                    if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal) || line == "*"
                        || (hashComments && line.StartsWith("#", StringComparison.Ordinal))
                        || ((ext is ".mjs" or ".js" or ".cjs") && line.StartsWith("#!", StringComparison.Ordinal)))
                        continue;
                    yield return (n, line);
                }
            }
        }

        [Fact]
        public void Only_the_persons_window_can_let_an_engine_open_a_browser()
        {
            var repo = RepoRoot();
            string Read(params string[] p) => File.ReadAllText(Path.Combine(new[] { repo }.Concat(p).ToArray()));

            // The engine takes the option on the owner (serve) only; the MCP door and an assistant-started owner never.
            var program = Read("Semanticus.Engine", "Program.cs");
            var serve = program.Substring(program.IndexOf("private static async Task<int> Serve(", StringComparison.Ordinal));
            serve = serve.Substring(0, serve.IndexOf("private static async Task<int> Mcp(", StringComparison.Ordinal));
            Assert.Contains("InteractiveSignIn.ApplyCommandLine(args);", serve);
            Assert.Equal(1, Regex.Matches(program, @"InteractiveSignIn\.ApplyCommandLine").Count);
            Assert.DoesNotContain("InteractiveSignIn", Read("Semanticus.Engine", "OwnerSpawner.cs"));
            Assert.DoesNotContain("--interactive-signin", Read("Semanticus.Engine", "OwnerSpawner.cs"));

            // Product code builds a browser credential in one place, and opens nothing by itself.
            var product = Directory.EnumerateFiles(repo, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj" or "external" or "node_modules"))
                .Where(f => !Path.GetRelativePath(repo, f).StartsWith("Semanticus.Tests", StringComparison.Ordinal))
                .ToList();
            var builders = product.SelectMany(f => Regex.Matches(File.ReadAllText(f), @"new\s+InteractiveBrowserCredential\s*\(").Select(_ => Path.GetFileName(f))).ToList();
            Assert.Equal(new[] { "EntraToken.cs" }, builders);
            var entra = Read("Semanticus.Engine", "Connect", "EntraToken.cs");
            Assert.Contains("if (IsBrowserGated(ibc)) throw InteractiveSignIn.Refuse(", entra);
            Assert.Contains("catch (AuthenticationRequiredException ex) when (IsBrowserGated(cred))", entra);
            var launches = product
                .Where(f => !Path.GetFileName(f).Equals("InteractiveSignIn.cs", StringComparison.Ordinal))   // the stub names
                .SelectMany(f => LaunchScan.CodeLines(f).Select(x => (f, x.Number, x.Text, Why: LaunchScan.Match(x.Text, credentials: false))))
                .Where(x => x.Why != null)
                .Select(x => $"{Path.GetRelativePath(repo, x.f)}:{x.Number}: [{x.Why}] {x.Text.Trim()}")
                .ToList();
            Assert.True(launches.Count == 0, "product code launches a browser itself:\n" + string.Join("\n", launches));

            // The window passes the option except in the extension's own test runs.
            var extension = Read("Semanticus.VSCode", "src", "extension.ts");
            Assert.Contains("...ownerSignInArgs(context.extensionMode === vscode.ExtensionMode.Test)", extension);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Semanticus.sln"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("could not find the repository root from " + AppContext.BaseDirectory);
        }
    }
}
