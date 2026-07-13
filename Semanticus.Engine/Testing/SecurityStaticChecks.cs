using System;
using System.Collections.Generic;
using System.Linq;

namespace Semanticus.Engine
{
    /// <summary>The static facts about ONE role/table filter, provider-agnostic so the checks are offline-testable
    /// (no TOMWrapper here — the coordinator maps the model in). Mirrors <see cref="RelationshipCheckInput"/>.</summary>
    public sealed class RoleFilterInput
    {
        public string Role { get; set; }
        public string Table { get; set; }
        public string FilterExpression { get; set; }
        public string ErrorMessage { get; set; }     // TOM's recorded parse/eval error for this permission, if any
    }

    /// <summary>One role/table filter's outcome (reuses the E4 verdict + check-result vocabulary — Rule 3: one
    /// vocabulary everywhere).</summary>
    public sealed class RoleFilterResult
    {
        public string Role { get; set; }
        public string Table { get; set; }
        public CheckResult Check { get; set; }
        public string FilterPreview { get; set; }    // capped — evidence for the UI, never the whole expression
    }

    public sealed class SecurityStaticSummary
    {
        public int Filters { get; set; }
        public int Passed { get; set; }
        public int Failed { get; set; }
        public int NotVerifiable { get; set; }
        public double CoveragePct { get; set; }      // I2: carried beside the tallies, same as E4
    }

    /// <summary>One tile of a role's object-visibility grid. Illustration for the UI — the COUNTS on
    /// <see cref="RoleOls"/> are the truth; tiles may be capped.</summary>
    public sealed class RoleOlsTile
    {
        public string Table { get; set; }
        public bool Hidden { get; set; }
    }

    /// <summary>A role's object-level-security summary, read STATICALLY (metadata permissions, no impersonation).
    /// INFORMATIONAL ONLY: hiding nothing is a legitimate modelling choice, so OLS carries no verdict and can never
    /// move the grade (the Applicable=0 dormancy convention) — it exists so the security view can SHOW visibility
    /// honestly beside the filter checks.</summary>
    public sealed class RoleOls
    {
        public string Role { get; set; }
        public int TablesTotal { get; set; }
        public int TablesHidden { get; set; }
        public int ColumnsHidden { get; set; }
        public RoleOlsTile[] Tiles { get; set; } = Array.Empty<RoleOlsTile>();
    }

    public sealed class SecurityStaticReport
    {
        public IReadOnlyList<RoleFilterResult> Filters { get; set; }
        /// <summary>Per-role visibility summaries; null when the model cannot express OLS (compatibility level
        /// below 1400) — absent, never a fabricated all-visible.</summary>
        public IReadOnlyList<RoleOls> Ols { get; set; }
        public SecurityStaticSummary Summary { get; set; }
    }

    /// <summary>
    /// The STATIC security checks for the Tests tab (ratified decision 1: security ships static-first — reading each
    /// role's filter expression needs no impersonation and already catches the real bug class "this role's filter is
    /// 1=1, it restricts nothing"). PURE: filter texts in, verdicts out; no connection, no TOM.
    ///
    /// What static analysis can honestly decide: a TAUTOLOGY filter (admits every row → Fail), a filter with a
    /// recorded ERROR (cannot restrict anything → Fail), and a present, well-formed, non-tautological filter (→ Pass
    /// of THE STATIC CHECK ONLY — the message says so; whether the right users see the right rows needs impersonation,
    /// the separately-gated E8). Row-level assertions therefore never appear here at all — they arrive with E8, not
    /// as permanently-NotVerifiable noise.
    /// </summary>
    public static class SecurityStaticChecks
    {
        public const string CheckName = "StaticFilter";
        private const int PreviewCap = 300;

        public static SecurityStaticReport Evaluate(IEnumerable<RoleFilterInput> filters)
        {
            var results = (filters ?? Enumerable.Empty<RoleFilterInput>())
                .Where(f => f != null && !string.IsNullOrWhiteSpace(f.FilterExpression))   // no filter = metadata-only permission, nothing to test
                .Select(EvaluateOne)
                .ToList();
            var passed = results.Count(r => r.Check.Verdict == Verdict.Pass);
            var failed = results.Count(r => r.Check.Verdict == Verdict.Fail);
            var nv = results.Count(r => r.Check.Verdict == Verdict.NotVerifiable);
            var decisive = results.Count - nv;
            return new SecurityStaticReport
            {
                Filters = results,
                Summary = new SecurityStaticSummary
                {
                    Filters = results.Count,
                    Passed = passed,
                    Failed = failed,
                    NotVerifiable = nv,
                    CoveragePct = results.Count == 0 ? 0 : Math.Round(100.0 * decisive / results.Count, 1),
                },
            };
        }

        public static RoleFilterResult EvaluateOne(RoleFilterInput f)
        {
            var r = new RoleFilterResult
            {
                Role = f.Role,
                Table = f.Table,
                FilterPreview = Cap(f.FilterExpression),
            };
            if (IsTautology(f.FilterExpression))
                r.Check = new CheckResult
                {
                    Check = CheckName,
                    Verdict = Verdict.Fail,
                    Message = $"role '{f.Role}' filters '{f.Table}' with a tautology: the filter admits every row, so this role does not restrict the table at all",
                };
            else if (!string.IsNullOrEmpty(f.ErrorMessage))
                r.Check = new CheckResult
                {
                    Check = CheckName,
                    Verdict = Verdict.Fail,
                    Message = $"the filter expression has a recorded error and cannot restrict anything: {f.ErrorMessage}",
                };
            else
                r.Check = new CheckResult
                {
                    Check = CheckName,
                    Verdict = Verdict.Pass,
                    Message = "the filter is present, parses, and is not a tautology (static check only; whether the right users see the right rows needs impersonation)",
                };
            return r;
        }

        /// <summary>A filter that admits every row. Normalized (whitespace stripped, case-folded) then matched
        /// against the CLOSED tautology forms — deliberately NOT a general expression prover: a false negative is
        /// acceptable (a clever always-true filter reads as Pass on the static check), a false positive is not
        /// (a real filter must never be called a tautology).</summary>
        public static bool IsTautology(string expr)
        {
            var n = new string(expr.Where(ch => !char.IsWhiteSpace(ch)).ToArray()).ToUpperInvariant();
            return n == "1=1" || n == "TRUE" || n == "TRUE()" || n == "1==1" || n == "(1=1)" || n == "(TRUE())";
        }

        private static string Cap(string s) => s.Length <= PreviewCap ? s : s.Substring(0, PreviewCap) + " …";
    }
}
