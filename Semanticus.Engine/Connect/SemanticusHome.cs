// Where the per-user Semanticus home is (F-232).
//
// ONE PLACE. The connection list, the connection timeline, the agent policy, the approval ledger, the restore
// points, the decision consents, the global knowledge / interview / experience stores and the licence file all live
// under one per-user directory. The cross-process locks do not (LockRoot): they must be shared by every engine of the
// user, whatever home each was given. Each of those stores still has its own RootOverride for a test
// that wants a private directory; when none is set they all resolve here.
//
// WHY AN ENVIRONMENT VARIABLE. Test runs used to write into the real ~/.semanticus/connections.json because
// every fixture that opens a model records it, and only the fixtures that remembered to set a RootOverride were
// kept out. SEMANTICUS_HOME is set ONCE for a whole test run (the test assembly's module initializer and each
// smoke's Main), and it is inherited by every engine process a smoke starts, so a fixture that forgets nothing
// and a child process that was never told anything both land in the scratch home. Unset, which is every real
// install, the home is ~/.semanticus exactly as before.
//
// --state-dir <dir> (or --state-dir=<dir>) on the engine's command line does the same for one run
// (ApplyCommandLine), for a person or an agent who starts an engine on a throwaway copy and does not want it recorded
// in their real home. A moved home also moves the sign-in store (EntraToken.PersistDir), an engine given one never
// joins an owner that keeps its state somewhere else, and an engine given none (the person's own) never joins an owner
// that another run started with a folder of its own (EngineBroker.StateFolderMismatch).

using System;
using System.IO;
using System.Linq;

namespace Semanticus.Engine
{
    public static class SemanticusHome
    {
        /// <summary>The environment variable that moves the per-user home. Unset in the product.</summary>
        public const string Variable = "SEMANTICUS_HOME";

        /// <summary>SEMANTICUS_HOME when it is set to something, else ~/.semanticus, else (no profile folder, but a
        /// per-user data folder exists) &lt;data&gt;/Semanticus/state. Read on every call, so a test run that sets it before
        /// its first store read is honoured everywhere. With no per-user folder at all it throws
        /// <see cref="InvalidOperationException"/> with <see cref="NoUserFolderMessage"/>: .NET answers "" for a
        /// folder that does not exist, and a store built on "" writes under whatever folder the engine started in
        /// (measured 27 Sep: ./.semanticus/connections.json with HOME naming a missing folder).</summary>
        public static string Root()
        {
            var set = Environment.GetEnvironmentVariable(Variable);
            if (!string.IsNullOrWhiteSpace(set)) return set;
            var profile = ProfileFolder();
            if (profile != null) return Path.Combine(profile, ".semanticus");
            var data = DataFolder();
            if (data != null) return Path.Combine(data, "Semanticus", "state");
            throw new InvalidOperationException(NoUserFolderMessage());
        }

        /// <summary>The person's profile folder (HOME, USERPROFILE), only when it is an absolute path that exists.
        /// Null otherwise, never "" and never a relative path.</summary>
        private static string ProfileFolder()
        {
            var p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
            return Path.IsPathRooted(p) && Directory.Exists(p) ? p : null;
        }

        /// <summary>The platform per-user data folder (LocalApplicationData: ~/.local/share on Linux,
        /// ~/Library/Application Support on macOS, %LOCALAPPDATA% on Windows). A fresh machine may not have it yet,
        /// which is fine when it sits inside a profile folder that exists: the store creates it there. Null when
        /// it neither exists nor has an existing profile to live in, so a missing HOME is never created.</summary>
        private static string DataFolder()
        {
            var d = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
            if (!Path.IsPathRooted(d)) return null;
            if (Directory.Exists(d)) return d;
            var profile = ProfileFolder();
            if (profile == null) return null;
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return Path.GetFullPath(d).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile)) + Path.DirectorySeparatorChar, cmp) ? d : null;
        }

        /// <summary>What the engine says, and why it stops, when there is no per-user folder to keep state in.</summary>
        public static string NoUserFolderMessage()
        {
            var name = OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME";
            var value = Environment.GetEnvironmentVariable(name);
            var what = string.IsNullOrWhiteSpace(value) ? $"{name} is not set" : $"{name} is \"{value}\", which does not exist";
            return $"Semanticus cannot find your user folder ({what}), so it has nowhere safe to keep saved connections and sign-ins. "
                + $"Nothing was started. Set {name} to your user folder, or give this run a folder of its own with {Option} <dir>.";
        }

        /// <summary>Throws with <see cref="NoUserFolderMessage"/> when this run has nowhere to keep its state or its
        /// sign-ins. The engine calls it once at start, so it refuses before any store can be touched.</summary>
        public static void EnsureResolvable()
        {
            _ = Root();
            _ = SignInDir();
        }

        /// <summary>True when this run chose its home (SEMANTICUS_HOME set, directly or by --state-dir), so what it
        /// writes must stay there. False for every ordinary install.</summary>
        public static bool IsMoved => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable));

        /// <summary>The environment variable that names the sign-in store (saved accounts and sign-in records) for a
        /// run whose home is moved but which must still use the person's own sign-in: a read-only tool that reaches
        /// the person's live models. Unset in the product and in every isolated run.</summary>
        public const string SignInVariable = "SEMANTICUS_SIGNIN_DIR";

        /// <summary>Where an ordinary install keeps saved accounts and sign-in records (LocalApplicationData, which is
        /// ~/.local/share on Linux). Throws like <see cref="Root"/> when there is no per-user data folder.</summary>
        public static string DefaultSignInDir() =>
            TryDefaultSignInDir() ?? throw new InvalidOperationException(NoUserFolderMessage());

        /// <summary><see cref="DefaultSignInDir"/>, or null when there is no per-user data folder.</summary>
        public static string TryDefaultSignInDir()
        {
            var data = DataFolder();
            return data == null ? null : Path.Combine(data, "Semanticus", "auth");
        }

        /// <summary>The sign-in store for this run. It moves with the home: a run given its own state folder keeps its
        /// sign-ins in <c>&lt;home&gt;/auth</c> and never reads or writes the person's (27 Sep). Only
        /// SEMANTICUS_SIGNIN_DIR, named on purpose, points it anywhere else.</summary>
        public static string SignInDir()
        {
            var set = Environment.GetEnvironmentVariable(SignInVariable);
            if (!string.IsNullOrWhiteSpace(set)) return Path.GetFullPath(set);
            return IsMoved ? Path.Combine(Root(), "auth") : DefaultSignInDir();
        }

        /// <summary>The environment variable that names the lock folder (<see cref="LockRoot"/>). Test runs set it
        /// (<see cref="IsolateForTestRun"/>); a real install and every isolated run leave it unset.</summary>
        public const string LockVariable = "SEMANTICUS_LOCK_DIR";

        /// <summary>Where the locks live that keep two engines on one model from overwriting each other's History,
        /// drafts and Plan claims (SidecarFileLock, EngineRunLiveness). NOT under the home: a run given its own state
        /// folder may open the person's real model, and with the locks in its own folder it took the History lock while
        /// the person's engine held it, and read the person's live Plan claim as gone (27 Sep, round 3). So the home
        /// moving never moves them: Semanticus/locks in the per-user data folder, the one folder every engine of this user
        /// shares whatever state folder it was given. On Linux that is ~/.local/share/Semanticus/locks spelled from the
        /// profile folder, NOT from XDG_DATA_HOME, which a desktop session and a shell can see differently (Sol, 28 Sep).
        /// Empty lock files are all an isolated run ever puts there, and never the person's ~/.semanticus. With no
        /// per-user folder there is no person's engine to share with, so they stay in this run's home.</summary>
        public static string LockRoot()
        {
            var set = Environment.GetEnvironmentVariable(LockVariable);
            if (!string.IsNullOrWhiteSpace(set)) return Path.GetFullPath(set);
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            {
                var profile = ProfileFolder();
                if (profile != null) return Path.Combine(profile, ".local", "share", "Semanticus", "locks");
            }
            var data = DataFolder();
            return data != null ? Path.Combine(data, "Semanticus", "locks") : Path.Combine(Root(), "locks");
        }

        /// <summary>Where engines older than <see cref="LockRoot"/> took the same locks: &lt;home&gt;/locks, which is
        /// ~/.semanticus/locks for the person and the state folder for a run given one. Every engine takes them there as
        /// well, so it still excludes an older engine on the same home during an update (Sol, 28 Sep: two engines on one
        /// custom SEMANTICUS_HOME, one older). That is always this run's own home, so a run given a state folder never
        /// writes in the person's. Null only when a lock folder is named (a test run), which no older engine shares.</summary>
        public static string LegacyLockRoot()
        {
            if (LegacyLockRootForTests != null) return LegacyLockRootForTests();
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(LockVariable))) return null;
            return Path.Combine(Root(), "locks");
        }

        /// <summary>Test seam for <see cref="LegacyLockRoot"/>: a test run names its lock folder, so it cannot reach the
        /// product's case otherwise. Null in production.</summary>
        internal static Func<string> LegacyLockRootForTests;

        /// <summary>The lock folder released engines use under the person's usual home (~/.semanticus/locks), for a run
        /// given a state folder of its own, which may open the person's real model while one of them runs. That run
        /// uses it READ-ONLY: it only opens lock files that already exist, and never creates, writes or deletes anything
        /// there (SidecarFileLock, EngineRunLiveness). Null for the person's own engine (its <see cref="LegacyLockRoot"/>
        /// is that folder already), when a lock folder is named (a test run), or with no profile folder.</summary>
        public static string PersonLegacyLockRoot()
        {
            if (PersonLegacyLockRootForTests != null) return PersonLegacyLockRootForTests();
            if (!IsMoved || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(LockVariable))) return null;
            var profile = ProfileFolder();
            if (profile == null) return null;
            var person = Path.Combine(profile, ".semanticus", "locks");
            return SamePlace(person, LegacyLockRoot()) || SamePlace(person, LockRoot()) ? null : person;
        }

        /// <summary>Test seam for <see cref="PersonLegacyLockRoot"/>. Null in production.</summary>
        internal static Func<string> PersonLegacyLockRootForTests;

        /// <summary>The same place on disk: <see cref="SameFolder"/>, or the same once every symbolic link along both
        /// paths is followed, case-blind on Windows and macOS. For the lock folders, where taking one file twice under
        /// two names would make an engine wait on itself (Sol, 28 Sep).</summary>
        public static bool SamePlace(string a, string b)
        {
            if (SameFolder(a, b)) return true;
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            try
            {
                var cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                return string.Equals(RealPath(a), RealPath(b), cmp);
            }
            catch { return false; }
        }

        /// <summary>Where <paramref name="path"/> really is on disk, every symbolic link along it followed, for telling
        /// two names of one lock file apart. The full path as written when it cannot be resolved.</summary>
        internal static string ResolvedPath(string path)
        {
            try { return RealPath(path); }
            catch { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        }

        // The full path with every symbolic link along it followed (as far as the path exists). A link's target takes the
        // place of the path read so far and everything after it is resolved again, so a link inside a link's target is
        // followed too (Sol, 28 Sep: "alias" -> "bridge/locks" with "bridge" itself a link). At most 40 links in all, as
        // the kernel allows; past that the rest of the path is taken as written.
        private static string RealPath(string p)
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
            var current = Path.GetPathRoot(full) ?? "";
            var rest = new System.Collections.Generic.List<string>(Parts(full));
            var links = 0;
            while (rest.Count > 0)
            {
                var next = Path.Combine(current, rest[0]);
                rest.RemoveAt(0);
                FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
                var target = info.LinkTarget;
                if (target == null || ++links > 40) { current = next; continue; }
                var resolved = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(current, target));
                rest.InsertRange(0, Parts(resolved));
                current = Path.GetPathRoot(resolved) ?? "";
            }
            return Path.TrimEndingDirectorySeparator(current);
        }

        private static string[] Parts(string fullPath) =>
            fullPath.Substring((Path.GetPathRoot(fullPath) ?? "").Length).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        /// <summary>The same folder, spelled either way: full paths, no trailing separator, case-blind on Windows.
        /// A path that cannot be resolved matches nothing.</summary>
        public static bool SameFolder(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            try
            {
                var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), cmp);
            }
            catch { return false; }
        }

        /// <summary>The command-line option that moves the per-user home for one engine run.</summary>
        public const string Option = "--state-dir";

        /// <summary>Honour <c>--state-dir &lt;dir&gt;</c> or <c>--state-dir=&lt;dir&gt;</c> for this engine run: it wins over
        /// an inherited SEMANTICUS_HOME, and it is applied by setting that variable, so every engine process this one
        /// starts (an owner the MCP door spawns inherits the environment) uses the same home. Absent, nothing changes.
        /// Given twice, the first one wins. Anything that looks like the option but is not one of those two shapes
        /// (no folder, <c>--STATE-DIR</c>, <c>-state-dir</c>, <c>--statedir</c>, <c>--state-dir:x</c>) is refused rather
        /// than ignored, because an ignored one runs on the real home: exactly what the person asked it not to do.</summary>
        public static void ApplyCommandLine(string[] args)
        {
            if (args == null) return;
            string chosen = null;
            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i];
                string value;
                if (string.Equals(a, Option, StringComparison.Ordinal))
                {
                    value = i + 1 < args.Length ? args[i + 1] : null;
                    if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException($"{Option} needs a folder after it, for example {Option} /path/to/state.");
                    i++;   // the folder is this option's value, not another argument
                }
                else if (a != null && a.StartsWith(Option + "=", StringComparison.Ordinal))
                {
                    value = a.Substring(Option.Length + 1);
                    if (string.IsNullOrWhiteSpace(value))
                        throw new ArgumentException($"{Option}= needs a folder after it, for example {Option}=/path/to/state.");
                }
                else if (LooksLikeTheOption(a))
                    throw new ArgumentException($"'{a}' is not a form this engine understands. Use {Option} <folder> or {Option}=<folder>; nothing was started, so nothing was written to the real home.");
                else continue;
                chosen ??= value;
            }
            if (chosen != null) Environment.SetEnvironmentVariable(Variable, Path.GetFullPath(chosen));
        }

        // Any dash-led argument whose letters, ignoring case, dashes and underscores, start with "statedir". A dash is any
        // dash character, not only the ASCII hyphen: a command copied from a document, an email or a chat often arrives
        // with "--" turned into an en or em dash, and an ignored one runs on the real home. Invisible format characters (a
        // zero-width space, a byte-order mark, a word joiner) are dropped first, so one pasted in front of "--state-dir"
        // cannot hide it either (round 4).
        private static bool LooksLikeTheOption(string a)
        {
            if (string.IsNullOrEmpty(a)) return false;
            var visible = new string(a.Where(c => IsDash(c) || char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray());
            if (visible.Length == 0 || !IsDash(visible[0])) return false;
            var letters = new string(visible.Where(c => !IsDash(c) && c != '_').Select(char.ToLowerInvariant).ToArray());
            return letters.StartsWith("statedir", StringComparison.Ordinal);
        }

        // The ASCII hyphen, every Unicode dash (en, em, figure, the non-breaking hyphen and the rest), and the characters
        // that look like one: the minus signs (U+2212, the modifier, superscript and subscript minus, the heavy minus) and
        // the soft hyphen, which is invisible but still a character (round 4).
        private static bool IsDash(char c) =>
            c == '-' || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.DashPunctuation
            || c is '\u2212' or '\u02D7' or '\u207B' or '\u208B' or '\u2796' or '\u00AD';

        /// <summary>Point this process, and every process it starts, at a fresh scratch home, unless the run
        /// already chose one, with its own lock folder, no way to open a browser, no way to reach the desktop session
        /// (InteractiveSignIn.StubBrowsersForTestRun) and, unless <paramref name="cloud"/> keeps them, no way to reach
        /// a sign-in of the person's that needs no browser, such as their Azure CLI (InteractiveSignIn.CutCloudSignInForTestRun).
        /// For test runs, smokes and the probes only; nothing in the product calls it. Returns the home in use.</summary>
        public static string IsolateForTestRun(string label, CloudSignIn cloud = CloudSignIn.Cut)
        {
            var home = Environment.GetEnvironmentVariable(Variable);
            if (string.IsNullOrWhiteSpace(home))
            {
                // NOT "semanticus-<guid>": that prefix marks an ephemeral model anchor (ExperienceLog.IsEphemeralAnchor).
                home = Path.Combine(Path.GetTempPath(), "sem-home-" + (label ?? "test") + "-" + Guid.NewGuid().ToString("N").Substring(0, 12));
                Directory.CreateDirectory(home);
                Environment.SetEnvironmentVariable(Variable, home);
            }
            // The locks do not move with the home (LockRoot), so a test run names its own, inside its scratch home: the
            // default is the person's shared lock folder. Every engine the run starts inherits it.
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(LockVariable)))
                Environment.SetEnvironmentVariable(LockVariable, Path.Combine(home, "locks"));
            // No test run may open a browser or reach the desktop session of whoever runs it (28 Sep). The engine already
            // refuses without --interactive-signin, which no test passes; this is the second net for anything else.
            // Nor sign in as that person (28 Sep, targeted round): a push test asked their own Azure CLI for a Power BI token.
            var net = Path.Combine(home, "no-browser");
            var log = InteractiveSignIn.StubBrowsersForTestRun(net, cloud);
            InteractiveSignIn.CutCloudSignInForTestRun(log == null ? net : Path.GetDirectoryName(log), cloud);
            return home;
        }
    }
}
