using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    // While the assistant waits on the person about a red check, the Studio says so on Publish (hero19 D7). One activity event
    // carries the line when the assistant is refused, and a second one clears it when a publish gets past the check.
    public sealed partial class LocalEngine
    {
        internal const string PublishAskKind = "publish_asking";
        private volatile bool _redAsking;

        /// <summary>The one calm line: how many red checks wait, and the first named. The gate's own sentence carries the names after "Start with: ".</summary>
        internal static string PublishAskWords(IReadOnlyList<string> blockers, int count)
        {
            var named = (blockers ?? Array.Empty<string>()).Select(NameAfterStartWith).Select(PlainCheckName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
            var n = count > 0 ? count : Math.Max(1, blockers?.Count ?? 1);
            var lead = $"Your assistant is asking you about {n} red {(n == 1 ? "check" : "checks")}";
            return string.IsNullOrWhiteSpace(named) ? lead : lead + ": " + named;
        }

        /// <summary>The gate names a check the way the rule file does: "[DAX Expressions] Avoid using the IFERROR function ([Return Rate])".
        /// The person reads "Avoid using the IFERROR function on Return Rate": no category tag, no brackets, the object after "on".
        /// Only this line is reworded; the gate's own sentence for the assistant keeps the names verbatim (hero 20 D14).</summary>
        internal static string PlainCheckName(string named)
        {
            if (string.IsNullOrWhiteSpace(named)) return named;
            var first = named.Split(new[] { "; " }, StringSplitOptions.None)[0];   // the gate lists several, the line names the first
            var text = System.Text.RegularExpressions.Regex.Replace(first.Trim(), @"^\[[^\]]*\]\s*", "");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s*\((.+)\)$", m => " on " + m.Groups[1].Value);
            return text.Replace("[", "").Replace("]", "").Replace("'", "").Trim();
        }

        private static string NameAfterStartWith(string blocker)
        {
            if (string.IsNullOrWhiteSpace(blocker)) return null;
            const string marker = "Start with: ";
            var at = blocker.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) return null;
            var rest = blocker.Substring(at + marker.Length);
            var end = rest.IndexOf(". ", StringComparison.Ordinal);
            if (end < 0) end = rest.EndsWith(".", StringComparison.Ordinal) ? rest.Length - 1 : rest.Length;
            return rest.Substring(0, end).Trim();
        }

        private Task NoteRedAskAsync(DeployGate gate)
        {
            _redAsking = true;
            // The person answers in chat, which the engine cannot hear. What it can see is the next edit: the assistant working
            // again means the question is answered (a no), and the person fixing the check makes it green. Both end the line.
            try { _sessions.Bus.Changed -= OnRedAskCommit; _sessions.Bus.Changed += OnRedAskCommit; } catch { }
            return PublishActivityAsync(new ActivityEvent { Kind = PublishAskKind, Origin = "agent", Ok = true, Label = PublishAskWords(gate?.Blockers, (gate?.BpaBlocking ?? 0) + (gate?.BpaUnknownBlocking ?? 0)) });
        }

        private void OnRedAskCommit(ChangeNotification n)
        {
            if (!_redAsking || n == null) return;
            var walk = n.Label == "undo" || n.Label == "redo";
            if (!n.IsCommit && !walk) return;
            var agent = IsAssistantOrigin(n.Origin);
            _ = Task.Run(async () =>
            {
                try
                {
                    if (!_redAsking) return;
                    // The assistant edited again: it is not waiting on the person any more. The person edited: clear when the check is green now.
                    if (!agent)
                    {
                        var gate = await DeployGateAsync(null, "system").ConfigureAwait(false);
                        if (gate == null || !gate.Pass) return;
                    }
                    await ClearRedAskAsync().ConfigureAwait(false);
                }
                catch { /* the line stays until the next publish, which is what it did before */ }
            });
        }

        private Task ClearRedAskAsync()
        {
            if (!_redAsking) return Task.CompletedTask;
            _redAsking = false;
            try { _sessions.Bus.Changed -= OnRedAskCommit; } catch { }
            return PublishActivityAsync(new ActivityEvent { Kind = PublishAskKind, Origin = "agent", Ok = false });
        }
    }
}
