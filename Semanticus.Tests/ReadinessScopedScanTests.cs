using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests
{
    /// <summary>
    /// S1 (health-delta review): the scoped incremental readiness rescan must be EQUAL to a full rescan —
    /// grade, overall, gates, per-category tallies and the finding-key multiset — after a series of real
    /// dual-drive edits on the AdventureWorks fixture, including the hard cases: a measure rename (key moves via
    /// FormulaFixup), a TABLE rename (every child ref re-keys without its own delta), a delete, and an undo.
    /// The scoped path may only skip re-evaluating an object when skipping cannot change the answer.
    /// Also prints the per-edit scoped-vs-full timings (the PR's S1 evidence).
    /// </summary>
    public sealed class ReadinessScopedScanTests
    {
        private readonly ITestOutputHelper _out;
        public ReadinessScopedScanTests(ITestOutputHelper output) => _out = output;

        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "pro" };
        }

        [Fact]
        public async Task Scoped_incremental_rescan_equals_a_full_rescan_after_a_series_of_edits()
        {
            var sm = new SessionManager();
            using var engine = new LocalEngine(sm, new Pro());
            await engine.OpenAsync(TestModels.FindBim());

            // Collect each commit's touched refs off the live bus — exactly what the health probe receives.
            var commits = new List<ChangeNotification>();
            Action<ChangeNotification> tap = n => commits.Add(n);
            sm.Bus.Changed += tap;
            try
            {
                var analyzer = new ReadinessAnalyzer();          // S3 posture: one analyzer, one rule set
                var state = new ReadinessScanState();
                await sm.Current!.ReadAsync(m => analyzer.Baseline(m, state));   // the seeding full scan

                var mRef = await engine.CreateMeasureAsync("table:Date", "Scoped Probe Measure", "1", "agent");
                await Compare(sm, analyzer, state, commits, "create measure");

                await engine.SetDescriptionAsync(mRef, "A probe measure used by the scoped-scan equivalence test.", "agent");
                await Compare(sm, analyzer, state, commits, "set description");

                await engine.SetDaxAsync(mRef, "IFERROR(1/0, 0)", "agent");
                await Compare(sm, analyzer, state, commits, "set dax (IFERROR)");

                await engine.RenameObjectAsync(mRef, "Scoped Probe Renamed", "agent");
                await Compare(sm, analyzer, state, commits, "rename measure");

                // The re-key cascade: renaming a TABLE moves EVERY child ref (measure:Date/x -> measure:Dates2/x)
                // while only the table itself (plus fixup-rewritten dependents) carries a delta. The scoped path
                // must treat the re-keyed children as unseen and re-evaluate them.
                await engine.RenameObjectAsync("table:Date", "Dates Renamed", "agent");
                await Compare(sm, analyzer, state, commits, "rename TABLE (re-key cascade)");

                await engine.DeleteObjectAsync("measure:Dates Renamed/Scoped Probe Renamed", "agent");
                await Compare(sm, analyzer, state, commits, "delete measure");

                await engine.UndoAsync("human");
                await Compare(sm, analyzer, state, commits, "undo");
            }
            finally { sm.Bus.Changed -= tap; }
        }

        private async Task Compare(SessionManager sm, ReadinessAnalyzer analyzer, ReadinessScanState state,
            List<ChangeNotification> commits, string label)
        {
            Assert.NotEmpty(commits);
            // Union every commit since the last comparison (an op may commit more than once).
            var touched = new HashSet<string>(
                commits.SelectMany(c => c.Deltas ?? Array.Empty<ChangeDelta>())
                       .Select(d => d.Ref).Where(r => !string.IsNullOrEmpty(r)),
                StringComparer.Ordinal);
            commits.Clear();

            var (full, scoped, fullMs, scopedMs) = await sm.Current!.ReadAsync(m =>
            {
                var sw = Stopwatch.StartNew();
                var s = analyzer.Reanalyze(m, state, touched);
                sw.Stop();
                var sMs = sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                var f = new ReadinessAnalyzer().Analyze(m);      // an independent, memo-less full rescan
                sw.Stop();
                return (f, s, sw.Elapsed.TotalMilliseconds, sMs);
            });
            _out.WriteLine($"{label}: touched={touched.Count} scoped={scopedMs:F1}ms full={fullMs:F1}ms");

            Assert.Equal(full.Grade, scoped.Grade);
            Assert.Equal(full.Overall, scoped.Overall);
            Assert.Equal(full.RawOverall, scoped.RawOverall);
            Assert.Equal(full.GatedBy, scoped.GatedBy);
            Assert.Equal(full.SafeFixCount, scoped.SafeFixCount);
            Assert.Equal(full.WaivedCount, scoped.WaivedCount);
            Assert.Equal(Keys(full), Keys(scoped));              // the finding-key multiset, exactly
            foreach (var fc in full.Categories)
            {
                var sc = Assert.Single(scoped.Categories, c => c.Category == fc.Category);
                Assert.Equal(fc.Applicable, sc.Applicable);
                Assert.Equal(fc.Violations, sc.Violations);
                Assert.Equal(fc.Score, sc.Score);
                Assert.Equal(fc.HasRules, sc.HasRules);
            }
        }

        private static string[] Keys(Scorecard card) =>
            card.Findings.Select(f => f.RuleId + "␟" + f.ObjectRef).OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }
}
