using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    /// <summary>
    /// Local source control via the `git` CLI (a child process). Kane's decision: shell out so the user's existing
    /// git config, credential helper (GCM / SSH) and remotes drive auth — push/pull "just works" with nothing to
    /// wire. The engine never handles git credentials. Git can still echo a credential-bearing URL on failure, so
    /// every captured error/result is scrubbed before it crosses a door. Read verbs (status/diff/log) are safe; the remote/outward writes
    /// are gated at the engine op layer (commit defaults to a dry-run preview, push needs confirm=true). Local
    /// working-tree verbs (checkout / pull --ff-only / branch) run directly and rely on git's own abort-on-conflict.
    /// </summary>
    internal static class GitCli
    {
        internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);
        internal static readonly TimeSpan TransferTimeout = TimeSpan.FromMinutes(15);
        private static readonly HashSet<string> TransferVerbs = new(StringComparer.OrdinalIgnoreCase)
            { "clone", "push", "pull", "fetch" };
        internal sealed class GitRun { public int ExitCode; public string Stdout; public string Stderr; public bool TimedOut; public bool Ok => ExitCode == 0; }

        // A caller can paste an HTTPS URL containing basic-auth credentials. Git echoes that URL in transport
        // failures, so scrub all URI userinfo before ANY stdout/stderr crosses a door. Deliberately consume every
        // non-whitespace character through the last '@', including '/' or '@' in malformed pasted credentials: over-redacting a URL
        // with '@' in its path is safer than returning a secret Git echoed after rejecting that malformed URL.
        // scp-style SSH identities have no URI scheme and are intentionally unaffected. The generic scrub then
        // catches query-string tokens, JWTs and key=value forms.
        private static readonly Regex UrlCredentials = new(
            @"(?i)\b([a-z][a-z0-9+.-]*://)([^\s]+)@",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Run `git <args>` in workingDir, capturing stdout/stderr. Throws a clear error if git isn't installed.
        internal static Task<GitRun> RunAsync(string workingDir, params string[] args)
            => RunWithTimeoutAsync(workingDir, TimeoutFor(args), args);

        internal static async Task<GitRun> RunWithTimeoutAsync(string workingDir, TimeSpan timeout, params string[] args)
        {
            if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
            using var cts = new CancellationTokenSource(timeout);
            try { return await RunCoreAsync(workingDir, cts.Token, args).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            { return TimeoutFailure(timeout); }
        }

        // Cancellable overload. When <paramref name="ct"/> fires (a caller timeout or cancellation) the child git
        // process is KILLED — awaiting Task.WaitAsync alone would abandon a git stalled on IO, leaking a zombie that
        // accumulates over many appends — and an OperationCanceledException propagates for the caller to fail soft.
        internal static async Task<GitRun> RunAsync(string workingDir, CancellationToken ct, params string[] args)
        {
            var budget = TimeoutFor(args);
            using var timeout = new CancellationTokenSource(budget);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try { return await RunCoreAsync(workingDir, linked.Token, args).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { return TimeoutFailure(budget); }
        }

        internal static TimeSpan TimeoutFor(string[] args)
            => args != null && args.Any(TransferVerbs.Contains) ? TransferTimeout : DefaultTimeout;

        private static async Task<GitRun> RunCoreAsync(string workingDir, CancellationToken ct, string[] args)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            psi.Environment["GCM_INTERACTIVE"] = "Never";
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = new Process { StartInfo = psi };
            var sb = new StringBuilder();
            var eb = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) eb.AppendLine(e.Data); };
            try
            {
                p.Start();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                throw new InvalidOperationException("git was not found on PATH. Install Git (and ensure 'git' is runnable) to use source control.");
            }
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            try
            {
                await p.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* already exited / race — nothing to kill */ }
                throw;
            }
            return new GitRun { ExitCode = p.ExitCode, Stdout = sb.ToString(), Stderr = eb.ToString() };
        }

        private static GitRun TimeoutFailure(TimeSpan timeout) => new()
        {
            ExitCode = -1,
            TimedOut = true,
            Stderr = $"git did not finish within {FormatTimeout(timeout)} and was stopped. Check the remote and network, then retry.",
        };

        private static string FormatTimeout(TimeSpan timeout)
            => timeout.TotalMinutes >= 1 && timeout.TotalMinutes == Math.Truncate(timeout.TotalMinutes)
                ? $"{timeout.TotalMinutes:0} minute" + (timeout.TotalMinutes == 1 ? "" : "s")
                : $"{Math.Max(1, Math.Ceiling(timeout.TotalSeconds)):0} second" + (timeout.TotalSeconds <= 1 ? "" : "s");

        internal static async Task<bool> IsRepoAsync(string dir)
        {
            var r = await RunAsync(dir, "rev-parse", "--is-inside-work-tree");
            return r.Ok && r.Stdout.Trim() == "true";
        }

        internal static async Task<string> RepoRootAsync(string dir)
        {
            var r = await RunAsync(dir, "rev-parse", "--show-toplevel");
            return r.Ok ? r.Stdout.Trim() : null;
        }

        // `git status --porcelain=v2 --branch` — machine-parseable status + branch/upstream/ahead/behind.
        internal static async Task<GitStatus> StatusAsync(string dir)
        {
            var st = new GitStatus { WorkingDir = dir };
            if (!await IsRepoAsync(dir)) { st.IsRepo = false; st.Note = "Not a git repository."; return st; }
            st.IsRepo = true;
            st.RepoRoot = await RepoRootAsync(dir);

            var r = await RunAsync(dir, "status", "--porcelain=v2", "--branch", "--untracked-files=all");
            if (!r.Ok) { st.Note = Error(r); return st; }

            var files = new List<GitFileChange>();
            foreach (var line in r.Stdout.Replace("\r\n", "\n").Split('\n'))
            {
                if (line.Length == 0) continue;
                if (line.StartsWith("# branch.head ")) { var h = line.Substring("# branch.head ".Length).Trim(); if (h == "(detached)") st.Detached = true; else st.Branch = h; }
                else if (line.StartsWith("# branch.upstream ")) st.Upstream = line.Substring("# branch.upstream ".Length).Trim();
                else if (line.StartsWith("# branch.ab "))
                {
                    // "# branch.ab +A -B"
                    var parts = line.Substring("# branch.ab ".Length).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var part in parts)
                    {
                        if (part.StartsWith("+") && int.TryParse(part.Substring(1), out var a)) st.Ahead = a;
                        else if (part.StartsWith("-") && int.TryParse(part.Substring(1), out var b)) st.Behind = b;
                    }
                }
                else if (line[0] == '1' || line[0] == '2')
                {
                    // Changed/renamed tracked entry: "1 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <path>"
                    var cols = line.Split(' ', 9, StringSplitOptions.None);
                    if (cols.Length < 9) continue;
                    var xy = cols[1];
                    var path = cols[8];
                    if (line[0] == '2')   // rename/copy: "...<path><TAB><origPath>" in the last field
                        path = path.Split('\t')[0];
                    files.Add(new GitFileChange { Path = path, Status = StatusLetter(xy), Staged = xy[0] != '.', Worktree = xy[1] != '.' });
                }
                else if (line[0] == '?')
                {
                    files.Add(new GitFileChange { Path = line.Substring(2), Status = "??", Staged = false, Worktree = true });
                }
                // '#' header lines other than the branch ones, and 'u' (unmerged) — fold unmerged into a U file:
                else if (line[0] == 'u')
                {
                    var cols = line.Split(' ', 11, StringSplitOptions.None);
                    if (cols.Length >= 11) files.Add(new GitFileChange { Path = cols[10], Status = "U", Staged = false, Worktree = true });
                }
            }
            st.Files = files.ToArray();
            return st;
        }

        private static string StatusLetter(string xy)
        {
            // Prefer the worktree status, fall back to the index status. A/M/D/R/C/U/.
            char c = xy[1] != '.' ? xy[1] : xy[0];
            return c == '.' ? "M" : c.ToString();
        }

        internal static async Task<GitDiffResult> DiffAsync(string dir, string path, bool staged)
        {
            var args = new List<string> { "diff" };
            if (staged) args.Add("--staged");
            if (!string.IsNullOrWhiteSpace(path)) { args.Add("--"); args.Add(path); }
            var r = await RunAsync(dir, args.ToArray());
            if (!r.Ok) return new GitDiffResult { Path = path, Error = Error(r) };
            var text = r.Stdout;
            return new GitDiffResult { Path = path, Text = text, Empty = string.IsNullOrWhiteSpace(text) };
        }

        internal static async Task<GitLogEntry[]> LogAsync(string dir, int max)
        {
            var n = max <= 0 ? 20 : max;
            // \x1f field separator, \x1e record separator — survive multi-line subjects.
            var r = await RunAsync(dir, "log", "-n", n.ToString(), "--date=short", "--pretty=format:%H\x1f%h\x1f%an\x1f%ad\x1f%s\x1e");
            if (!r.Ok) return Array.Empty<GitLogEntry>();
            var entries = new List<GitLogEntry>();
            foreach (var rec in r.Stdout.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = rec.Trim('\n', '\r').Split('\x1f');
                if (f.Length >= 5) entries.Add(new GitLogEntry { Hash = f[0], ShortHash = f[1], Author = f[2], Date = f[3], Subject = f[4] });
            }
            return entries.ToArray();
        }

        // The hash of the new HEAD after a commit (for the result).
        internal static async Task<string> HeadHashAsync(string dir)
        {
            var r = await RunAsync(dir, "rev-parse", "--short", "HEAD");
            return r.Ok ? r.Stdout.Trim() : null;
        }

        // The full 40-char HEAD commit sha, or null when it can't be resolved — <paramref name="dir"/> is not in a
        // repo, HEAD is unborn (a fresh repo with no commits yet), or any other odd state where `rev-parse HEAD`
        // fails. Used to anchor an audit record to the commit the model sat on; the caller treats null as "no
        // anchor" (fail-soft). A detached HEAD still resolves to its commit, which is exactly what we want to record.
        internal static async Task<string> HeadCommitAsync(string dir, CancellationToken ct = default)
        {
            var r = await RunAsync(dir, ct, "rev-parse", "HEAD");
            if (!r.Ok) return null;
            var sha = r.Stdout.Trim();
            return sha.Length == 40 && sha.All(Uri.IsHexDigit) ? sha : null;
        }

        internal static async Task<string[]> BranchesAsync(string dir)
        {
            var r = await RunAsync(dir, "branch", "--format=%(refname:short)");
            return r.Ok ? r.Stdout.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToArray() : Array.Empty<string>();
        }

        internal static string Scrub(string value)
            => XmlaAuthHint.Scrub(UrlCredentials.Replace(value ?? "", "$1***@"));

        internal static string Error(GitRun r) => Scrub(r?.Stderr?.Trim());

        // Trim+merge stdout/stderr into one user-facing line (git writes progress/results to stderr).
        internal static string Combine(GitRun r) => Scrub(string.Join("\n", new[] { r.Stdout?.Trim(), r.Stderr?.Trim() }.Where(s => !string.IsNullOrEmpty(s))));
    }
}
