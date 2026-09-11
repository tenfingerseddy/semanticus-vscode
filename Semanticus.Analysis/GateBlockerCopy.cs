using System;
using System.Collections.Generic;
using System.Linq;

namespace Semanticus.Analysis
{
    /// <summary>
    /// User-facing copy for the deploy gate's blockers. It exists because the gate used to tell a person a NUMBER
    /// and nothing else: the whole violation blocker was "41 blocking BPA error(s)", which names no rule, no object
    /// and no action, and the unknown blocker handed over a raw rule ID plus our internal doctrine wording
    /// ("coverage unknown, not clean"). Live-verified against two real models on 2026-07-30.
    ///
    /// THREE THINGS THIS FILE IS RESPONSIBLE FOR, and one it is not:
    ///   * Rule NAMES and object names, so the reader knows what to go and change. A count is a supplement here,
    ///     never the whole message.
    ///   * Severity words that match the severity NUMBERS. <see cref="BpaRule.Severity"/> documents 1 = info,
    ///     2 = warning, 3 = error, and the gate blocks on <c>Severity &gt;= 2</c>. The old copy called every one of
    ///     them an "error", so a user with 41 warnings was told they had 41 errors. Both levels still block; they
    ///     are now described with the right word. Changing the THRESHOLD would change what the gate blocks on and
    ///     is deliberately not done here.
    ///   * An action. Every message ends in something the reader can do.
    /// It does NOT decide anything. Which findings block is the gate's call (LocalEngine.DeployGateAsync); this
    /// class only renders the set it is handed, so a copy change can never move a verdict.
    ///
    /// Copy rules that bind here (docs/product-copy-style.md): no engine vocabulary in primary copy, so "BPA"
    /// never appears (rules 2 and 5); no em dashes (rule 3); honesty outranks smoothness (rule 9), which is why an
    /// unknown still says plainly that it is not clean and that a waiver cannot clear it.
    ///
    /// NOTHING SUPPLIED BY THE USER IS EDITED ON ITS WAY THROUGH. Rule names and object names are rendered
    /// verbatim, including the bundled corpus's "[Performance] " category prefix and the DAX decoration on an
    /// object name ("[Total Sales]", "'Date'"). Trimming either would mean pattern-matching a user-supplied string,
    /// and a rule legitimately named "[Total Sales] needs a format string" would then be silently mangled. The
    /// consequence to know about: a custom rule whose NAME contains an em dash puts an em dash in this copy. That
    /// is the rule author's text, not ours, and corrupting their name to satisfy our style sweep would be worse.
    /// </summary>
    public static class GateBlockerCopy
    {
        /// <summary>How many distinct rules get named before the message falls back to "and N more". Three keeps
        /// the line readable on a model with dozens of findings while still naming something concrete.</summary>
        public const int NamedRuleLimit = 3;

        /// <summary>The marker that opens the unknown blocker's trailing id clause. Everything before it is primary
        /// copy and may contain no raw rule id; the clause itself is the deliberate exception. Exposed so a test can
        /// assert the split at exactly the boundary the code uses, instead of hard-coding the same literal twice and
        /// letting the two drift apart.</summary>
        public const string IdClauseMarker = " Rule id";

        /// <summary>The blocker line for findings that block. <paramref name="blocking"/> must already be the
        /// gate's blocking set (severity >= 2, not auto-fixable, not waived); this method filters nothing, so it
        /// cannot disagree with the verdict. Returns null for an empty set.</summary>
        public static string ForBlockingViolations(IEnumerable<BpaViolation> blocking)
        {
            var list = blocking?.ToList();
            if (list == null || list.Count == 0) return null;

            var errors = list.Count(v => v.Severity >= 3);
            var warnings = list.Count(v => v.Severity == 2);

            // Group by rule so a rule that fired on twelve objects is one named item with a count, not twelve
            // lines. Errors first, then the biggest groups, so "start with" really is where to start.
            var groups = list
                .GroupBy(v => v.RuleId ?? v.RuleName ?? "")
                .Select(g => new
                {
                    Name = RuleLabel(g.First().RuleName, g.Key),
                    TopSeverity = g.Max(v => v.Severity),
                    Objects = g.Select(v => v.ObjectName).Where(n => !string.IsNullOrWhiteSpace(n)).ToList(),
                    Count = g.Count(),
                })
                .OrderByDescending(g => g.TopSeverity)
                .ThenByDescending(g => g.Count)
                .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var named = groups.Take(NamedRuleLimit)
                .Select(g =>
                {
                    if (g.Objects.Count == 0) return g.Name;
                    var extra = g.Objects.Count - 1;
                    return extra > 0
                        ? $"{g.Name} ({g.Objects[0]} and {extra} more)"
                        : $"{g.Name} ({g.Objects[0]})";
                });

            var text = SeverityPhrase(errors, warnings) + " to fix before this model can ship."
                     + " Start with: " + string.Join("; ", named) + ".";

            var restRules = groups.Count - Math.Min(groups.Count, NamedRuleLimit);
            if (restRules > 0)
                text += $" Then {restRules} more {(restRules == 1 ? "rule" : "rules")} after those.";

            return text + " Fix each one, or record a waiver saying why you accept it.";
        }

        /// <summary>The blocker line for rules the scan could not finish. <paramref name="blocking"/> must already
        /// be the unknowns the gate blocks over (<see cref="BpaRuleUnknown.BlocksGate"/>). Returns null for an
        /// empty set.</summary>
        public static string ForBlockingUnknowns(IEnumerable<BpaRuleUnknown> blocking)
        {
            var list = blocking?.ToList();
            if (list == null || list.Count == 0) return null;

            // KEYED ON THE RULE/SCOPE PAIR, which is exactly what one BpaRuleUnknown IS and exactly what the gate
            // counts into BpaUnknownBlocking. The first version of this method collapsed the list by DISPLAY NAME
            // instead, and that was the count-instead-of-a-set defect reappearing inside the fix for it: two
            // different rule ids that happen to share a name, or the SAME rule unevaluable at two scopes, became
            // one "check", so the lead count could read "1 check" while the gate's own BpaUnknownBlocking field
            // said 2, and the "N more" remainder was wrong rather than merely brief. Caught by Sol on review.
            // The scope travels with each name so the reader can see why two entries are two, not one.
            var pairs = list
                .Select(u => new
                {
                    Label = RuleLabel(u.RuleName, u.RuleId)
                            + (string.IsNullOrWhiteSpace(u.Scope) ? "" : $" (on {u.Scope})"),
                    Id = u.RuleId,
                })
                .ToList();
            var named = string.Join("; ", pairs.Take(NamedRuleLimit).Select(p => p.Label));
            var rest = pairs.Count - Math.Min(pairs.Count, NamedRuleLimit);

            var text = pairs.Count == 1
                ? $"1 check could not run on this model, so its result is unknown: {named}."
                : $"{pairs.Count} checks could not run on this model, so their results are unknown: {named}"
                  + (rest > 0 ? $", and {rest} more." : ".");

            // Honesty outranks smoothness (copy rule 9). An unknown is not a pass and a waiver genuinely cannot
            // clear one, so both facts survive the rewrite in plain words.
            text += " Unknown is not the same as clean, and a waiver cannot clear it, because nothing was"
                  + " actually checked. Fix the rule or take it out of the rule set, then run the gate again.";

            // THE IDS, in a trailing detail clause, because an engineer needs the exact identifier and the plain
            // name alone does not give it. This is the one place a raw id is allowed in this copy: the copy guide
            // permits a glossed engine term in a secondary detail line and forbids it only in the primary line
            // (rule 2). EVERY distinct blocking id is listed rather than the first few, because this clause is the
            // set-faithful record the truncated name list above deliberately is not. Unknowns are rare (a rule has
            // to fail mid-scope to produce one), so the length is bounded in practice.
            var ids = pairs.Select(p => p.Id).Where(id => !string.IsNullOrWhiteSpace(id))
                           .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList();
            if (ids.Count > 0)
                text += IdClauseMarker + (ids.Count == 1 ? "" : "s") + $": {string.Join(", ", ids)}.";
            return text;
        }

        /// <summary>One-line result of the checks on THIS change. Whole-model leftovers are a count with a
        /// pointer to AI Readiness, never a block of their own.</summary>
        public static string CheckLine(IEnumerable<string> blockers, int olderWarnings)
        {
            var list = blockers?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList()
                       ?? new List<string>();
            if (list.Count == 0)
            {
                if (olderWarnings <= 0) return "Checks on this change: nothing to fix.";
                return "Checks on this change: nothing to fix. The model has "
                     + OlderPhrase(olderWarnings)
                     + " this change did not cause. See them on AI Readiness.";
            }

            var text = "Checks on this change: " + string.Join(" ", list);
            if (olderWarnings > 0)
                text += " The model also has " + OlderPhrase(olderWarnings)
                      + " this change did not cause. See them on AI Readiness.";
            return text;
        }

        public static string OlderPhrase(int n)
        {
            if (n == 1) return "1 older warning";
            return n + " older warnings";
        }

        /// <summary>The note's trailing sentence about waived findings. Kept here so the waived line uses the same
        /// severity words as the blocker line above.</summary>
        public static string WaivedNote(int waived)
        {
            if (waived <= 0) return "";
            return $" ({waived} blocking {(waived == 1 ? "finding" : "findings")} waived: accepted decisions, not"
                 + " blockers. list_waivers shows the reasons.)";
        }

        /// <summary>"2 errors and 41 warnings", with the right word for each level and the right plural. Both levels
        /// block; only the wording differs.</summary>
        internal static string SeverityPhrase(int errors, int warnings)
        {
            string e() => $"{errors} {(errors == 1 ? "error" : "errors")}";
            string w() => $"{warnings} {(warnings == 1 ? "warning" : "warnings")}";
            if (errors > 0 && warnings > 0) return e() + " and " + w();
            if (errors > 0) return e();
            if (warnings > 0) return w();
            return "0 findings";   // unreachable from the callers above (both return null on an empty set)
        }

        /// <summary>The rule's name, which is what a person can act on. Falls back to the id ONLY when a rule
        /// carries no name at all: an unnamed rule has nothing else to identify it, and showing the reader
        /// "a rule" with no handle would be less useful than the id. Every one of the 74 bundled rules is named
        /// (measured 2026-07-30), so this fallback is reachable only through a hand-written custom rule.</summary>
        internal static string RuleLabel(string ruleName, string ruleId) =>
            string.IsNullOrWhiteSpace(ruleName) ? (ruleId ?? "an unnamed rule") : ruleName;
    }
}
