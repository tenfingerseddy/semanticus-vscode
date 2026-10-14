// Whether this engine may hand a sign-in to the system browser (28 Sep).
//
// WHY. A browser sign-in (authMode interactive) opens the machine's default browser on whatever desktop the engine can
// reach. On 27 Sep a test suite's human sign-in control did exactly that: xdg-open raised Chromium with a Microsoft
// sign-in page on the person's real screen, from a run nobody was watching. An agent could never do it (agent calls
// build credentials that may not prompt), but any in-process caller on the human side could, and every test, smoke and
// tool run is such a caller.
//
// HOW. The browser is OPT-IN per process: only an engine started with --interactive-signin may open it. The VS Code
// extension passes it when it starts the owner for a real window (never for its own test runs), and nothing else does:
// not the MCP door, not an owner the assistant starts, not a test, smoke or tool. Without it a browser credential is
// built so it cannot prompt, and the two places a prompt would begin (EntraToken.GetTokenAsync and the first-sign-in
// capture in EntraToken.BuildCredentialAsync) refuse with a plain sentence instead. A command-line flag, not an
// environment variable, so it is never inherited by a process that was not told.
//
// Device code is not gated: it prints a code and opens nothing.
//
// A test run also reaches no sign-in of the person's that needs no browser (28 Sep, targeted round): a push test asked
// the person's own Azure CLI for a Power BI token and connected to api.powerbi.com with it. CutCloudSignInForTestRun
// below is that half of the net.

using System;
using System.Linq;

namespace Semanticus.Engine
{
    /// <summary>Which of the person's sign-ins a test run, smoke or probe may still use (SemanticusHome.IsolateForTestRun).</summary>
    public enum CloudSignIn
    {
        /// <summary>None: the Azure CLI is a stub with an empty folder of the run's own, and a sign-in store or a service
        /// principal named from outside is dropped. Every test run and smoke.</summary>
        Cut,
        /// <summary>As <see cref="Cut"/>, but a service principal set in the environment stays, for a live gate that runs
        /// only when one is set (CicdSmoke).</summary>
        KeepServicePrincipal,
        /// <summary>All of them: a live probe a person runs by hand against an endpoint they name, which exists to sign in
        /// as that person (LiveProbe, AuthTest). The browser stays refused and the desktop stays cut.</summary>
        KeepPersons,
    }

    public static class InteractiveSignIn
    {
        /// <summary>The command-line option that lets this engine open the system browser for a sign-in.</summary>
        public const string Option = "--interactive-signin";

        private static volatile bool _allowed;

        /// <summary>True only in an engine started with <see cref="Option"/>. False in every test, smoke and tool run.</summary>
        public static bool Allowed => _allowed;

        /// <summary>Test seam: lets one test stand in for an engine started with <see cref="Option"/>, to prove a credential that may prompt still
        /// does not while nothing was pressed (SignInPress). The caller puts it back in a finally.</summary>
        internal static void AllowedForTests(bool allowed) => _allowed = allowed;

        /// <summary>Honour <see cref="Option"/> on this engine's command line. Absent, the browser stays off.</summary>
        public static void ApplyCommandLine(string[] args)
        {
            if (args != null && Array.IndexOf(args, Option) >= 0) _allowed = true;
        }

        /// <summary>What a person sees when a browser sign-in was needed and this engine may not open one.</summary>
        public const string RefusedText =
            "This sign-in needs a browser window, and this copy of Semanticus is not allowed to open one, so nothing was opened. "
            + "Sign in from Semanticus in VS Code, or choose Device code or Azure CLI sign-in.";

        /// <summary>Test seam: stands in for the system browser. When set, the point where a browser would open calls it
        /// with what the sign-in was for, and then fails the sign-in; the real browser is never reached. Null in
        /// production.</summary>
        internal static Action<string> BrowserForTests;

        /// <summary>Whether a browser credential built for a caller that is allowed to prompt must still be kept from
        /// prompting: always, unless this engine opted in and no test stands in for the browser.</summary>
        internal static bool MustGate => !_allowed || BrowserForTests != null;

        /// <summary>Called where a browser sign-in would begin, and returns what the caller throws: after calling the
        /// test stand-in, a failed sign-in; otherwise the plain refusal.</summary>
        internal static Exception Refuse(string what, Exception cause = null)
        {
            var fake = BrowserForTests;
            if (fake != null)
            {
                fake(what);
                return new InvalidOperationException("A test stood in for the browser (" + what + "), so no sign-in happened.", cause);
            }
            return new InvalidOperationException(RefusedText, cause);
        }

        /// <summary>The environment variable that names the stub log of a test run (<see cref="StubBrowsersForTestRun"/>).
        /// Unset in the product.</summary>
        public const string StubLogVariable = "SEMANTICUS_BROWSER_STUB_LOG";

        /// <summary>What a test run or smoke exits with when anything it ran called one of the stubs, whatever it
        /// would have exited with otherwise (<see cref="StubBrowsersForTestRun"/>).</summary>
        public const int LaunchAttemptExitCode = 86;

        /// <summary>Every name a sign-in library, a tool or a test could run to open a web page or reach the person's
        /// desktop: the openers, the browsers, and the desktop launchers and session tools a launcher goes through. On
        /// Omarchy BROWSER is omarchy-launch-browser, which opens the browser through systemd-run --user and needs
        /// neither DISPLAY nor WAYLAND_DISPLAY (28 Sep, round 4). Every omarchy-launch-* found on PATH is stubbed too.</summary>
        public static readonly string[] BrowserLaunchers =
        {
            // openers
            "xdg-open", "xdg-email", "gnome-open", "kde-open", "kde-open5", "kfmclient", "kioclient", "kioclient5", "exo-open",
            "gvfs-open", "gio", "wslview", "sensible-browser", "x-www-browser", "www-browser", "open",
            // browsers
            "microsoft-edge", "microsoft-edge-stable", "firefox", "firefox-esr", "librewolf", "chromium", "chromium-browser",
            "google-chrome", "google-chrome-stable", "brave", "brave-browser", "vivaldi", "vivaldi-stable", "opera", "epiphany",
            "qutebrowser", "code", "code-insiders", "codium",
            // desktop launchers and the session tools they go through
            "omarchy-launch-browser", "omarchy-launch-webapp", "omarchy-launch-or-focus", "omarchy-launch-or-focus-webapp",
            "gtk-launch", "uwsm", "uwsm-app", "systemd-run", "hyprctl", "gdbus", "busctl", "dbus-send", "notify-send", "flatpak",
        };

        /// <summary><see cref="BrowserLaunchers"/> plus every omarchy-launch-* on <paramref name="path"/> (this process's
        /// PATH when null).</summary>
        public static string[] LaunchersToStub(string path = null)
        {
            var names = new System.Collections.Generic.List<string>(BrowserLaunchers);
            foreach (var dir in (path ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    foreach (var f in System.IO.Directory.EnumerateFiles(dir, "omarchy-launch-*"))
                    {
                        var name = System.IO.Path.GetFileName(f);
                        if (!names.Contains(name) && name.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')) names.Add(name);
                    }
                }
                catch { /* an unreadable PATH entry holds nothing to stub */ }
            }
            return names.ToArray();
        }

        /// <summary>The programs a credential runs to sign in as the person with no browser: the Azure CLI (the engine's
        /// azcli mode through AzureCliCredential, and the sign-in probe's az account show) and the Azure Developer CLI,
        /// which Azure.Identity can run as well. A test run stubs them like the launchers (<see cref="CloudSignIn.Cut"/>).</summary>
        public static readonly string[] CloudSignInHelpers = { "az", "azd" };

        /// <summary>The variables that lead a credential to a sign-in of the person's with no browser and no program: a
        /// sign-in store named from outside (its saved accounts sign in silently) and a service principal. A test run
        /// drops them (<see cref="CutCloudSignInForTestRun"/>).</summary>
        public static string[] PersonsSignInVariables =>
            new[] { SemanticusHome.SignInVariable }.Concat(EntraToken.ServicePrincipalVariables).ToArray();

        /// <summary>The environment variables through which a process reaches the person's desktop session without a
        /// display variable: the session bus (the keyring, the desktop portal's OpenURI, systemd --user, which
        /// omarchy-launch-browser uses) and the compositor sockets. <see cref="StubBrowsersForTestRun"/> removes them and
        /// points XDG_RUNTIME_DIR, where the bus, systemd and Wayland sockets live, at an empty folder of the run's own.</summary>
        public static readonly string[] DesktopSessionVariables =
        {
            "DISPLAY", "WAYLAND_DISPLAY", "DBUS_SESSION_BUS_ADDRESS", "HYPRLAND_INSTANCE_SIGNATURE", "SWAYSOCK", "I3SOCK", "XAUTHORITY",
        };

        private static int _exitCheckArmed;

        /// <summary>For test runs and smokes only (SemanticusHome.IsolateForTestRun calls it; nothing in the product
        /// does). A second net behind the opt-in, for this process and every process it starts:
        /// <list type="bullet">
        /// <item>every name on <see cref="LaunchersToStub"/> becomes a stub in <paramref name="dir"/>, first on PATH, that
        /// writes its name and arguments to <c>calls.log</c> there and fails, and BROWSER names the xdg-open stub;</item>
        /// <item>the desktop session is cut off: <see cref="DesktopSessionVariables"/> are removed and XDG_RUNTIME_DIR is an
        /// empty folder of the run's own, so no session bus, systemd --user, Wayland or Hyprland socket can be found (and
        /// MSAL keeps a test's token cache in its file fallback, never in the person's keyring). Every change is made in
        /// the native environment as well as the managed one, because native code in this process reads the native one;</item>
        /// <item>unless <paramref name="cloud"/> keeps the person's sign-in, <see cref="CloudSignInHelpers"/> are stubs too,
        /// so an Azure CLI sign-in is caught and named like a launch;</item>
        /// <item>the process fails at exit (<see cref="LaunchAttemptExitCode"/>) when the stub log is not empty, whatever
        /// called a stub. A test run has the same check after its last test, because the test host's exit code does not
        /// reach dotnet test. So a stub called after that check, by code that runs as the process exits or by a thread
        /// still running then, opens nothing but fails nothing either; no test may register code that runs at exit
        /// (NoBrowserSignInGuardTests), and a thread a test leaves running is the gap that remains.</item>
        /// <item>CHROME_CONFIG_HOME names an empty folder, so a Chromium-family browser cannot find the person's running
        /// one through its profile's singleton socket.</item>
        /// </list>
        /// What it cannot catch: a browser started by its full path (/usr/bin/chromium) never meets a PATH stub and is not
        /// in the log. It still has no display, bus or runtime folder, and Chromium no profile of the person's, so it
        /// has nothing to open on (measured in a sandbox on 28 Sep: "Missing X server or $DISPLAY"), and the source scan
        /// in NoBrowserSignInGuardTests refuses the shape. A run that inherited an armed stub log keeps it; an inherited
        /// log whose stubs are not armed in this process (a shell that carries the variable) is replaced (Sol, round 4).
        /// On Windows, where a browser opens through the shell rather than PATH, it does nothing and the opt-in is the
        /// net. Returns the log path, or null on Windows.</summary>
        public static string StubBrowsersForTestRun(string dir, CloudSignIn cloud = CloudSignIn.Cut)
        {
            if (OperatingSystem.IsWindows()) return null;
            var cut = cloud != CloudSignIn.KeepPersons;
            var log = Environment.GetEnvironmentVariable(StubLogVariable);
            if (string.IsNullOrWhiteSpace(log) || NetGaps(log, cut).Count > 0)
            {
                log = WriteStubs(dir, cut);
                var stubs = System.IO.Path.GetDirectoryName(log);
                SetForRun("PATH", stubs + System.IO.Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
                SetForRun("BROWSER", System.IO.Path.Combine(stubs, "xdg-open"));
                SetForRun(StubLogVariable, log);
            }
            foreach (var v in DesktopSessionVariables) SetForRun(v, null);
            var runtime = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(log), "runtime");
            System.IO.Directory.CreateDirectory(runtime);
            System.IO.File.SetUnixFileMode(runtime, System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite | System.IO.UnixFileMode.UserExecute);
            SetForRun("XDG_RUNTIME_DIR", runtime);
            // A Chromium-family browser finds the person's running browser through the singleton socket in its profile
            // folder and hands the page to it, display or not. Here it gets an empty profile folder of its own.
            SetForRun("CHROME_CONFIG_HOME", System.IO.Path.Combine(System.IO.Path.GetDirectoryName(log), "chrome-config"));
            if (System.Threading.Interlocked.Exchange(ref _exitCheckArmed, 1) == 0)
                AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                {
                    var report = LaunchAttemptReport();
                    if (report == null) return;
                    try { Console.Error.WriteLine(report); } catch { }
                    Environment.ExitCode = LaunchAttemptExitCode;
                };
            return log;
        }

        /// <summary>For test runs and smokes only (SemanticusHome.IsolateForTestRun calls it). Unless
        /// <paramref name="cloud"/> keeps the person's sign-in, the Azure CLI and the Azure Developer CLI get empty
        /// config folders of the run's own in <paramref name="dir"/> (AZURE_CONFIG_DIR, AZD_CONFIG_DIR), so one run by
        /// its full path past the stubs still finds nobody signed in; a sign-in store named from outside
        /// (SEMANTICUS_SIGNIN_DIR) is dropped, so the store is the run's own and no saved account of the person's can sign
        /// a device-code or browser credential in silently; and the service principal variables are dropped unless it
        /// keeps those. In both environments, on every system (Windows has no PATH stubs, so there the folders are the
        /// net for the Azure CLI).</summary>
        public static void CutCloudSignInForTestRun(string dir, CloudSignIn cloud = CloudSignIn.Cut)
        {
            if (cloud == CloudSignIn.KeepPersons) return;
            SetForRun("AZURE_CONFIG_DIR", OwnFolder(dir, "azure-config"));
            SetForRun("AZD_CONFIG_DIR", OwnFolder(dir, "azd-config"));
            SetForRun(SemanticusHome.SignInVariable, null);
            if (cloud == CloudSignIn.Cut)
                foreach (var v in EntraToken.ServicePrincipalVariables) SetForRun(v, null);
        }

        private static string OwnFolder(string dir, string name)
        {
            var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, name));
            System.IO.Directory.CreateDirectory(path);
            if (!OperatingSystem.IsWindows())
                System.IO.File.SetUnixFileMode(path, System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite | System.IO.UnixFileMode.UserExecute);
            return path;
        }

        // .NET keeps its own copy of the environment. Environment.SetEnvironmentVariable changes what managed code and
        // every process started from here see, but not what NATIVE code in this process reads with getenv. MSAL's keyring
        // check (libsecret, through GLib) is native and runs inside the test process, so after a managed-only unset it
        // still found the person's session bus and asked the keyring for a session (round 5 gate, recorded on a private
        // bus). So every change the net makes goes into both. It runs once, at the start of the run, before the tests.
        private static void SetForRun(string name, string value)
        {
            Environment.SetEnvironmentVariable(name, value);
            if (OperatingSystem.IsWindows()) return;   // there the managed environment is the process's own
            var rc = value == null ? NativeEnvironment.unsetenv(name) : NativeEnvironment.setenv(name, value, 1);
            if (rc != 0) throw new InvalidOperationException($"The test-run net could not change {name} in this process's native environment (errno {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}), so native code could still reach the desktop.");
        }

        private static class NativeEnvironment
        {
            [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
            internal static extern int setenv(string name, string value, int overwrite);

            [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
            internal static extern int unsetenv(string name);
        }

        /// <summary>Write a stub for every name on <see cref="LaunchersToStub"/>, and on <see cref="CloudSignInHelpers"/>
        /// when <paramref name="cloud"/>, into <paramref name="dir"/> and return the path of their shared log, creating it
        /// empty when it does not exist. An existing log is never emptied: what another process of the run wrote there is
        /// the evidence. Changes no environment variable.</summary>
        internal static string WriteStubs(string dir, bool cloud = true)
        {
            System.IO.Directory.CreateDirectory(dir);
            var log = System.IO.Path.Combine(dir, "calls.log");
            System.IO.File.AppendAllText(log, "");
            var quoted = "'" + log.Replace("'", "'\\''") + "'";
            foreach (var name in Stubbed(cloud))
            {
                var stub = System.IO.Path.Combine(dir, name);
                System.IO.File.WriteAllText(stub, "#!/bin/sh\n" + StubMarker + "\n"
                    + "printf '%s %s\\n' '" + name + "' \"$*\" >> " + quoted + "\nexit 1\n");
                System.IO.File.SetUnixFileMode(stub, System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite | System.IO.UnixFileMode.UserExecute);
            }
            return log;
        }

        /// <summary>The line every stub carries, so a check can tell a stub from the real program.</summary>
        public const string StubMarker = "# A test run may never open a browser or reach the desktop (Semanticus InteractiveSignIn).";

        private static System.Collections.Generic.IEnumerable<string> Stubbed(bool cloud) =>
            cloud ? LaunchersToStub().Concat(CloudSignInHelpers) : LaunchersToStub();

        /// <summary>Why the stubs beside <paramref name="log"/> do not guard this process: a name on
        /// <see cref="LaunchersToStub"/> (and on <see cref="CloudSignInHelpers"/> when <paramref name="cloud"/>) with no
        /// stub there, or one that PATH resolves to something else first, or a BROWSER that is not the xdg-open stub.
        /// Empty when the net is armed.</summary>
        public static System.Collections.Generic.List<string> NetGaps(string log, bool cloud = true)
        {
            var gaps = new System.Collections.Generic.List<string>();
            var stubs = System.IO.Path.GetDirectoryName(log);
            if (string.IsNullOrEmpty(stubs) || !System.IO.File.Exists(log)) { gaps.Add("no stub log at " + log); return gaps; }
            var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            foreach (var name in Stubbed(cloud))
            {
                var stub = System.IO.Path.Combine(stubs, name);
                var first = path.Select(d => System.IO.Path.Combine(d, name)).FirstOrDefault(System.IO.File.Exists);
                if (!System.IO.File.Exists(stub) || !System.IO.File.ReadAllText(stub).Contains(StubMarker)) gaps.Add("no stub for " + name);
                else if (first != stub) gaps.Add(name + " resolves to " + (first ?? "nothing") + ", not to its stub");
            }
            if (Environment.GetEnvironmentVariable("BROWSER") != System.IO.Path.Combine(stubs, "xdg-open")) gaps.Add("BROWSER is not the xdg-open stub");
            return gaps;
        }

        /// <summary>The sentence a test run or smoke fails with when its stub log is not empty; null when nothing called a
        /// stub (or no net is armed).</summary>
        public static string LaunchAttemptReport()
        {
            var log = Environment.GetEnvironmentVariable(StubLogVariable);
            string[] calls;
            try { calls = !string.IsNullOrWhiteSpace(log) && System.IO.File.Exists(log) ? System.IO.File.ReadAllLines(log).Where(l => l.Trim().Length > 0).ToArray() : Array.Empty<string>(); }
            catch (Exception ex) { return "SEMANTICUS NO-BROWSER NET: the stub log " + log + " could not be read (" + ex.Message + "), so this run cannot show it opened no browser."; }
            if (calls.Length == 0) return null;
            return "SEMANTICUS NO-BROWSER NET: this run tried to open a browser, reach the desktop or sign in with the person's Azure CLI " + calls.Length
                + " time(s), so it fails. A stub caught each call and nothing was opened. Find what ran these (log: " + log + "):\n  "
                + string.Join("\n  ", calls.Take(20)) + (calls.Length > 20 ? "\n  ..." : "");
        }
    }
}
