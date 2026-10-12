// The commutation gate (Plan-heart contract 5, lane E2), carried from spike P0b as a permanent test.
//
// THE QUESTION. When the footprint rule calls two rows independent, can they really be done in either order with
// the same result, and does each one's change still mean the same thing after the other lands? For every pair of
// rows on each fixture model, the pair is done both ways in two throwaway copies:
//   check 1  the two models are the same, byte for byte once lineage-tag values and the verified-edits audit log
//            are masked (a new object gets a random tag in each copy; the log carries a timestamp).
//   check 2  each row's worked-out lines are the same before and after the other row lands (target, property,
//            after value; the revision stamp and the stale flag are ignored).
//   check 3  the revision guard still guards: the second row's OLD set is stale and its old digest is refused,
//            so the test cannot pass by switching the guard off.
// Every INDEPENDENT pair must pass 1 and 2; every pair must pass 3.
//
// TWO TESTS, BECAUSE THE FULL RUN TAKES ABOUT SIXTEEN MINUTES (p0b-result.md). The sample runs on every build:
// the Wide fixture, one independent pair per pair of families. The full run over all eight fixtures, with the
// coverage bar asserted (at least 30 independent pairs from at least 3 models across at least 5 families, and at
// least 10 dependent pairs), carries [Trait("Cost", "sweep")]: CI's default run filters it out (Cost!=sweep) and the
// nightly sweep runs it (Cost=sweep). It used to be switched by SEMANTICUS_PLAN_COMMUTATION=full and reported Skipped
// without it; the trait replaces that switch, so an unfiltered local run now runs it (sixteen minutes). Filter it out.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Semanticus.Engine;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests
{
    public sealed partial class PlanCommutationTests
    {
        private readonly ITestOutputHelper _out;
        public PlanCommutationTests(ITestOutputHelper output) { _out = output; }

        internal static string OutDir =>
            Environment.GetEnvironmentVariable("SEMANTICUS_PLAN_COMMUTATION_OUT") ?? Path.Combine(Path.GetTempPath(), "plan-commutation");

    }

    public sealed partial class PlanCommutationTests
    {
        private sealed class OrderedRun
        {
            public CommutationLine[] FirstBefore = Array.Empty<CommutationLine>(), SecondBefore = Array.Empty<CommutationLine>(), SecondAfter = Array.Empty<CommutationLine>();
            public bool SecondOnPlanAfter, SecondStale;
            public string SecondStaleReason, Error, Bim, SecondStaleFlag, OldDigestSentence;
            public bool OldDigestRefused;
        }

        private static async Task<OrderedRun> RunOrderedAsync(CommutationFixture fx, string first, string second, string tag)
        {
            var run = new OrderedRun();
            using var f = await fx.Copy(tag);
            var x0 = await CommutationRows.WorkOutAsync(f, first);
            var y0 = await CommutationRows.WorkOutAsync(f, second);
            run.FirstBefore = x0.Lines;
            run.SecondBefore = y0.Lines;
            if (!x0.OnPlan || !y0.OnPlan) { run.Error = "row not on the plan at the start"; return run; }
            // Keep every line of B now (so the digest captured is the one a person would approve), then note it.
            var y0Digest = await CommutationRows.KeepAllAsync(f, second);
            var errX = await CommutationRows.ApproveAsync(f, first);
            if (errX != null) { run.Error = "approve " + first + " refused: " + errX; return run; }
            var ySet = await CommutationRows.SetOfAsync(f, second);
            run.SecondStaleFlag = ySet == null ? "set gone" : ySet.Stale.ToString();
            run.SecondStaleReason = ySet == null ? "set gone" : ySet.StaleReason;
            // The guard itself: approving B's OLD digest after A must write nothing.
            var old = await f.Engine.ModelPlanActionAsync(CommutationRows.Holder(second), "approve", "human", y0Digest);
            run.OldDigestRefused = !old.Performed;
            run.OldDigestSentence = old.Sentence;
            // A Jev row is worked out again by its family at every revision, so by now its set may already be the new
            // one rather than a stale copy of the old. The guard is the same: the old digest is refused, and what is on
            // the plan is not the set that was read.
            run.SecondStale = run.OldDigestRefused && (ySet == null || ySet.Stale
                || (CommutationRows.IsJev(second) && !string.Equals(ySet.Digest, y0Digest, StringComparison.OrdinalIgnoreCase)));
            await CommutationRows.CancelAsync(f, second);
            var y1 = await CommutationRows.WorkOutAsync(f, second);
            run.SecondOnPlanAfter = y1.OnPlan;
            run.SecondAfter = y1.Lines;
            if (y1.OnPlan && y1.Lines.Length > 0)
            {
                var errY = await CommutationRows.ApproveAsync(f, second);
                if (errY != null) run.Error = "approve " + second + " refused: " + errY;
            }
            var path = Path.Combine(f.Workspace, "result-" + tag + ".bim");
            await f.Engine.SaveAsync(path, "bim", false, "human");
            run.Bim = File.ReadAllText(path);
            var keep = Path.Combine(OutDir, "results"); Directory.CreateDirectory(keep);
            File.Copy(path, Path.Combine(keep, tag.Replace('/', '_') + ".bim"), true);
            return run;
        }

        private static readonly Regex LineageTag = new Regex("\"lineageTag\": \"[^\"]*\"", RegexOptions.Compiled);
        /// <summary>The model with two things masked and nothing else touched: lineage-tag VALUES (a new object
        /// gets a random one in each copy) and the verified-edits audit annotations (a log of when each batch ran,
        /// with a timestamp and a session id, so it differs even between two runs in the same order). Array order,
        /// property order and every other value are kept, so an order difference in a collection still fails.</summary>
        private static string Normal(string bim)
        {
            if (bim == null) return null;
            var node = System.Text.Json.Nodes.JsonNode.Parse(bim);
            void Walk(System.Text.Json.Nodes.JsonNode n)
            {
                if (n is System.Text.Json.Nodes.JsonObject o)
                {
                    if (o["lineageTag"] != null) o["lineageTag"] = "*";
                    if (o["annotations"] is System.Text.Json.Nodes.JsonArray anns)
                        for (var k = anns.Count - 1; k >= 0; k--)
                            if ((anns[k]?["name"]?.GetValue<string>() ?? "").StartsWith("Semanticus_VerifiedEdits", StringComparison.Ordinal))
                                anns.RemoveAt(k);
                    foreach (var kv in o.ToList()) Walk(kv.Value);
                }
                else if (n is System.Text.Json.Nodes.JsonArray a) foreach (var x in a.ToList()) Walk(x);
            }
            Walk(node);
            return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }

        private static string FirstDiff(string a, string b)
        {
            var la = (a ?? "").Split('\n'); var lb = (b ?? "").Split('\n');
            for (var i = 0; i < Math.Max(la.Length, lb.Length); i++)
            {
                var x = i < la.Length ? la[i] : "<end>"; var y = i < lb.Length ? lb[i] : "<end>";
                if (x != y) return "line " + (i + 1) + ": A-then-B `" + x.Trim() + "` vs B-then-A `" + y.Trim() + "`";
            }
            return "same";
        }

        private static string SetDiff(CommutationLine[] before, CommutationLine[] after)
        {
            var b = new HashSet<string>(before.Select(l => l.ToString()), StringComparer.Ordinal);
            var a = new HashSet<string>(after.Select(l => l.ToString()), StringComparer.Ordinal);
            if (b.SetEquals(a)) return null;
            var gone = b.Except(a).ToArray(); var added = a.Except(b).ToArray();
            return (gone.Length > 0 ? "lost " + gone.Length + " [" + string.Join(" | ", gone.Take(3)) + "]" : "")
                 + (added.Length > 0 ? " gained " + added.Length + " [" + string.Join(" | ", added.Take(3)) + "]" : "");
        }

        /// <summary>Every row on one fixture that the harness knows how to do, with the ENGINE's footprint for
        /// it, worked out in one copy at one revision.</summary>
        private static async Task<(string[] Rows, Dictionary<string, Footprint> Fps)> FootprintsAsync(CommutationFixture fx, StringBuilder log)
        {
            var fps = new Dictionary<string, Footprint>(StringComparer.Ordinal);
            using var f = await fx.Copy("Fp-" + fx.Name);
            var plan = await CommutationRows.SettledPlanAsync(f);
            var open = plan.Items.Where(i => i.Status is ModelPlanStatuses.Ready or ModelPlanStatuses.Proposed).ToArray();
            var candidates = open.Where(i => CommutationRows.HasExecutor(i.Id)).Select(i => i.Id).ToArray();
            // A row that works out to no lines has nothing to approve, so there is no change to order. The words row is
            // on the plan of a fully described model (it also waits on business names, synonyms and instructions, which
            // only the assistant writes), and the person has nothing to approve there until the assistant adds lines.
            var worked = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var id in candidates) worked[id] = (await CommutationRows.WorkOutAsync(f, id)).Lines.Length;
            var rows = candidates.Where(id => worked[id] > 0).ToArray();
            foreach (var id in rows)
            {
                fps[id] = await CommutationRows.EngineFootprintAsync(f, id);
                log.AppendLine(fx.Name + " " + id + " known=" + fps[id].Known
                    + " W={" + string.Join(", ", fps[id].Writes.OrderBy(x => x)) + "}"
                    + " R={" + string.Join(", ", fps[id].Reads.OrderBy(x => x)) + "}");
            }
            log.AppendLine(fx.Name + " rows without an executor: "
                + string.Join(", ", open.Where(i => !CommutationRows.HasExecutor(i.Id)).Select(i => i.Id)));
            log.AppendLine(fx.Name + " rows with no lines to work out: " + string.Join(", ", candidates.Where(id => worked[id] == 0)));
            return (rows, fps);
        }

        private static async Task<PairOutcome> RunPairAsync(CommutationFixture fx, string a, string b, Dictionary<string, Footprint> fps, string tag)
        {
            var o = new PairOutcome { Fixture = fx.Name, A = a, B = b };
            o.Independent = PlanFootprint.Independent(fps[a], fps[b]);
            o.Why = CommutationRows.Why(fps[a], fps[b]);
            try
            {
                var ab = await RunOrderedAsync(fx, a, b, tag + "-ab");
                var ba = await RunOrderedAsync(fx, b, a, tag + "-ba");
                o.RunError = string.Join(" / ", new[] { ab.Error, ba.Error }.Where(e => e != null));
                o.Check1Raw = ab.Bim != null && ab.Bim == ba.Bim;
                o.Check1 = ab.Bim != null && Normal(ab.Bim) == Normal(ba.Bim);
                o.Check1Detail = o.Check1 ? (o.Check1Raw ? "bytes equal" : "equal once lineage-tag values and the audit log are masked") : FirstDiff(Normal(ab.Bim), Normal(ba.Bim));
                // check 2: B's lines before and after A (run ab), A's before and after B (run ba)
                var dB = !ab.SecondOnPlanAfter ? "B's row gone after A" : SetDiff(ab.SecondBefore, ab.SecondAfter);
                var dA = !ba.SecondOnPlanAfter ? "A's row gone after B" : SetDiff(ba.SecondBefore, ba.SecondAfter);
                o.Check2 = dB == null && dA == null;
                o.Check2Detail = o.Check2 ? "same lines both ways" : ((dB == null ? "" : "B after A: " + dB) + (dA == null ? "" : " ; A after B: " + dA)).Trim();
                // check 3: the guard fires on the old set
                o.Check3 = ab.SecondStale && ba.SecondStale;
                o.Check3Detail = "B after A: stale=" + ab.SecondStaleFlag + " old-digest refused=" + ab.OldDigestRefused
                    + "; A after B: stale=" + ba.SecondStaleFlag + " old-digest refused=" + ba.OldDigestRefused;
                if (!string.IsNullOrEmpty(o.RunError)) { o.Check1 = false; o.Check2 = false; }
            }
            catch (Exception ex)
            {
                o.RunError = ex.GetType().Name + ": " + ex.Message;
                o.Check1 = o.Check2 = o.Check3 = false;
            }
            return o;
        }

        private static string Line(PairOutcome o) =>
            $"{o.Fixture} | {o.A} x {o.B} | {(o.Independent ? "IND" : "dep")} | c1={o.Check1} c2={o.Check2} c3={o.Check3} | {o.RunError} | {o.Why} | {o.Check1Detail} | {o.Check2Detail} | {o.Check3Detail}";

        private void Write(string name, List<PairOutcome> outcomes, StringBuilder log)
        {
            Directory.CreateDirectory(OutDir);
            File.WriteAllText(Path.Combine(OutDir, "pairs-" + name + ".json"),
                JsonSerializer.Serialize(outcomes, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
            File.WriteAllText(Path.Combine(OutDir, "pairs-" + name + ".log"), log.ToString());
            _out.WriteLine("evidence: " + Path.Combine(OutDir, "pairs-" + name + ".log"));
        }

        /// <summary>The failures every run asserts: an independent pair that does not commute (check 1 or 2), a
        /// pair where the guard did not fire (check 3), and a pair that could not be run at all.</summary>
        private static string[] Failures(IEnumerable<PairOutcome> outcomes) =>
            outcomes.Where(o => !string.IsNullOrEmpty(o.RunError)
                             || !o.Check3
                             || (o.Independent && (!o.Check1 || !o.Check2)))
                    .Select(Line).ToArray();

        /// <summary>On every build: the Wide fixture, one independent pair for each pair of families it has.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Independent_rows_commute_on_the_wide_fixture()
        {
            var fx = CommutationFixtures.All.Single(x => x.Name == "Wide");
            var log = new StringBuilder();
            var (rows, fps) = await FootprintsAsync(fx, log);
            var outcomes = new List<PairOutcome>();
            var families = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < rows.Length; i++)
            for (var j = i + 1; j < rows.Length; j++)
            {
                if (!PlanFootprint.Independent(fps[rows[i]], fps[rows[j]])) continue;
                var key = string.Join("|", new[] { CommutationRows.Family(rows[i]), CommutationRows.Family(rows[j]) }.OrderBy(x => x));
                if (!families.Add(key)) continue;
                var o = await RunPairAsync(fx, rows[i], rows[j], fps, "sample-" + i + "-" + j);
                outcomes.Add(o);
                log.AppendLine(Line(o));
            }
            Write("sample", outcomes, log);
            Assert.True(outcomes.Count >= 5, "the sample ran only " + outcomes.Count + " independent pairs:\n" + log);
            var failed = Failures(outcomes);
            Assert.True(failed.Length == 0, "pairs that fail the gate:\n" + string.Join("\n", failed));
        }

        /// <summary>UAT 26 Sep: the date mark read every measure, so a row that files Margin % (no time intelligence,
        /// nothing on the Date table) was called dependent on "Mark the date table" and waited for it. The pair commutes:
        /// the same model both ways and the same lines, so the wait was spurious. The footprint now calls it
        /// independent, and the gate proves that is safe.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_date_mark_and_a_measure_without_time_intelligence_commute()
        {
            var fx = CommutationFixtures.All.Single(x => x.Name == "DateMark");
            var log = new StringBuilder();
            var (rows, fps) = await FootprintsAsync(fx, log);
            const string mark = "datetable/model", folder = "asst/set_display_folder";
            Assert.True(rows.Contains(mark) && rows.Contains(folder), "rows: " + string.Join(", ", rows) + "\n" + log);
            Assert.Contains("measure:Sales/Margin %", fps[folder].Writes);
            Assert.Contains("column:Date/Month", fps[folder].Writes);
            Assert.True(PlanFootprint.Independent(fps[mark], fps[folder]), "still dependent: " + CommutationRows.Why(fps[mark], fps[folder]) + "\n" + log);

            var o = await RunPairAsync(fx, mark, folder, fps, "datemark-pair");
            log.AppendLine(Line(o));
            Write("datemark", new List<PairOutcome> { o }, log);
            Assert.True(Failures(new[] { o }).Length == 0, "the pair fails the gate: " + Line(o));
            Assert.True(o.Check1 && o.Check2, Line(o));
        }

        /// <summary>The same pair BEFORE the rule's row has a line (UAT 26 Sep, second pass: before consent "Mark Date as
        /// the date table" still waited for the Margin % tasks). The gate above works every row's lines out first, so it
        /// never sees this footprint. Here the rule's row is done the way Run the plan does it: the engine works its line
        /// out (PlanDateMark) at that moment, then it is approved. Filing Margin % first must leave that line, and the
        /// model, exactly as marking first does.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Before_its_line_is_worked_out_the_rule_date_mark_and_a_measure_without_dates_commute()
        {
            var fx = CommutationFixtures.All.Single(x => x.Name == "DateMark");
            const string mark = "datetable/model", folder = "asst/set_display_folder";
            Footprint markFp, folderFp;
            using (var f = await fx.Copy("datemark-declared-fp"))
            {
                var row = (await f.Engine.GetModelPlanAsync()).Items.Single(i => i.Id == mark);
                Assert.Equal("Mark Date as the date table", row.Title);
                Assert.Null(row.ChangeSet);
                var (reads, writes, known) = await f.Engine.FootprintForTestAsync(mark, Array.Empty<ModelPlanChangeLine>());
                markFp = new Footprint().ReadAll(reads).WriteAll(writes);
                markFp.Known = known;
                folderFp = await CommutationRows.EngineFootprintAsync(f, folder);
            }
            Assert.Contains("measure:Sales/Margin %", folderFp.Writes);
            Assert.Contains("column:Date/Month", folderFp.Writes);
            Assert.Contains("column:Date/Date", markFp.Writes);
            Assert.True(PlanFootprint.Independent(markFp, folderFp), "still dependent: " + CommutationRows.Why(markFp, folderFp));

            async Task<CommutationLine[]> MarkAsync(DecisionFixture f)
            {
                var r = await f.Engine.ModelPlanActionAsync(mark, "propose", "human");
                var set = r.Plan?.Items.FirstOrDefault(i => i.Id == mark)?.ChangeSet;
                Assert.True(set != null && set.Lines.Length > 0, "the engine did not work the mark out: " + r.Sentence);
                var done = await f.Engine.ModelPlanActionAsync(mark, "approve", "human", set.Digest);
                Assert.True(done.Performed, "approve refused: " + done.Sentence);
                return set.Lines.Select(CommutationLine.Of).ToArray();
            }
            // Worked out again at the moment it is done, as a person or the assistant would after the model moved.
            async Task<CommutationLine[]> FolderAsync(DecisionFixture f)
            {
                await CommutationRows.CancelAsync(f, folder);
                var (onPlan, lines) = await CommutationRows.WorkOutAsync(f, folder);
                Assert.True(onPlan && lines.Length > 0, "the folder row is gone");
                Assert.Null(await CommutationRows.ApproveAsync(f, folder));
                return lines;
            }
            async Task<string> BimAsync(DecisionFixture f, string tag)
            {
                var path = Path.Combine(f.Workspace, "result-" + tag + ".bim");
                await f.Engine.SaveAsync(path, "bim", false, "human");
                return File.ReadAllText(path);
            }

            CommutationLine[] markFirst, markSecond, folderSecond, folderFirst;
            string ab, ba;
            using (var f = await fx.Copy("datemark-declared-ab"))
            {
                markFirst = await MarkAsync(f);
                folderSecond = await FolderAsync(f);
                ab = await BimAsync(f, "ab");
            }
            using (var f = await fx.Copy("datemark-declared-ba"))
            {
                folderFirst = await FolderAsync(f);
                markSecond = await MarkAsync(f);
                ba = await BimAsync(f, "ba");
            }
            Assert.Equal(folderFirst, folderSecond);
            Assert.Equal(new[] { "mark_date_table table:Date = Date" }, markFirst.Select(l => l.Op + " " + l.Target + " = " + l.After).ToArray());
            Assert.Equal(markFirst, markSecond);
            Assert.True(Normal(ab) == Normal(ba), FirstDiff(Normal(ab), Normal(ba)));
        }

        // ---- the creates (lane m11/plan-waits, coordinator 26 Sep) --------------------------------------------------

        private static (string[] Rows, Dictionary<string, Footprint> Fps)? _creates;
        private static readonly System.Threading.SemaphoreSlim CreatesGate = new System.Threading.SemaphoreSlim(1, 1);

        private static async Task<(string[] Rows, Dictionary<string, Footprint> Fps)> CreatesFootprintsAsync(StringBuilder log)
        {
            await CreatesGate.WaitAsync();
            try { return _creates ??= await FootprintsAsync(CommutationFixtures.Creates, log); }
            finally { CreatesGate.Release(); }
        }

        /// <summary>Each plan kind that creates (or rewires a relationship) fell through FromLines' default and wrote only
        /// its target, so it was called independent of the row it feeds. For each: the engine's footprints now call the
        /// pair dependent, and doing the pair both ways shows the order matters (a different model or different lines,
        /// or one order refused), so the old "independent" was a real error, not a cautious one. The guard (check 3)
        /// still fires both ways.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("asst/create_column", "words/model")]
        [InlineData("asst/create_import_table", "words/model")]
        [InlineData("asst/create_calculation_group", "proposal/collisions")]
        // RESTORED ROW (join, part 2). Part 1 retired it because a calculation group's own columns are never a cleanup candidate
        // (m11/feedback-rules). m11/plan-harm's fixture (round 4) has the item read Sales[Note], a plain unused text column the
        // cleanup still lists, so the order matters again for a different reason: the cleanup can delete the column the item reads.
        [InlineData("asst/create_calculation_item", "cleanup/model")]
        [InlineData("asst/create_role", "asst/set_table_permission")]
        [InlineData("asst/create_relationship", "safefix/Visibility")]
        [InlineData("asst/set_relationship_active", "asst/create_relationship")]
        [InlineData("asst/generate_time_intelligence", "words/model")]
        public async Task A_create_and_the_row_it_feeds_are_ordered(string create, string partner)
        {
            var log = new StringBuilder();
            var (rows, fps) = await CreatesFootprintsAsync(log);
            Assert.True(rows.Contains(create) && rows.Contains(partner), "rows: " + string.Join(", ", rows) + "\n" + log);
            Assert.False(PlanFootprint.Independent(fps[create], fps[partner]), "called independent\n" + log);

            var o = await RunPairAsync(CommutationFixtures.Creates, create, partner, fps, "creates-" + create.Replace('/', '_'));
            log.AppendLine(Line(o));
            Write("creates-" + create.Replace('/', '_'), new List<PairOutcome> { o }, log);
            // One order refused outright is the plainest proof the order matters; the guard check needs both orders to run.
            if (string.IsNullOrEmpty(o.RunError)) Assert.True(o.Check3, "the guard did not fire: " + Line(o));
            else Assert.Contains("refused", o.RunError, StringComparison.Ordinal);
            Assert.False(o.Check1 && o.Check2, "the pair commutes, so it does not show the order matters: " + Line(o));
        }

        /// <summary>A cardinality change now writes the relationships at both its tables, so it is ordered against every
        /// row that reads them (the safe fixes, cleanup, the starter measures, the date mark). The Visibility safe fixes
        /// do not read cardinality, and this line (One and One) leaves the many side where it was, so on this fixture the
        /// pair gives the same model both ways: here the dependency is the careful rule's wide side. The starter rule DOES
        /// read cardinality (StarterMeasureTemplates.FactTables takes the many side as a fact), and a line that turns a
        /// relationship round is a proven conflict with a starter row:
        /// PlanNamesAndKeysTests.cs, A_cardinality_change_that_moves_the_many_side_is_ordered_against_the_starter_row.
        /// The guard still fires both ways.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_cardinality_change_is_ordered_against_what_reads_relationships()
        {
            var log = new StringBuilder();
            var (rows, fps) = await CreatesFootprintsAsync(log);
            const string cardinality = "asst/set_relationship_cardinality", visibility = "safefix/Visibility";
            Assert.True(rows.Contains(cardinality) && rows.Contains(visibility), "rows: " + string.Join(", ", rows) + "\n" + log);
            Assert.Contains("model:relationships:Sales", fps[cardinality].Writes);
            Assert.Contains("model:relationships:Product", fps[cardinality].Writes);
            Assert.False(PlanFootprint.Independent(fps[cardinality], fps[visibility]), "called independent\n" + log);

            var o = await RunPairAsync(CommutationFixtures.Creates, cardinality, visibility, fps, "creates-cardinality");
            log.AppendLine(Line(o));
            Write("creates-cardinality", new List<PairOutcome> { o }, log);
            Assert.True(string.IsNullOrEmpty(o.RunError), Line(o));
            Assert.True(o.Check3, "the guard did not fire: " + Line(o));
        }

        /// <summary>Two test processes on one machine shared /tmp/plan-commutation/bases/&lt;Name&gt;.bim: each built its own
        /// base (relationship names and lineage tags are random per build) and wrote it over the other's, so a pair's
        /// two orders could start from different builds and fail check 1 on relationship names (seen 26 Sep, lane
        /// m11/plan-waits). Each process now keeps its bases in a folder of its own, and one fixture builds its base
        /// once even when two tests ask for it at the same time.</summary>
        [Fact]
        public async Task Each_process_builds_its_fixture_bases_once_in_a_folder_of_its_own()
        {
            var builds = 0;
            var fx = new CommutationFixture
            {
                Name = "Isolation",
                Build = file => { System.Threading.Interlocked.Increment(ref builds); return CommutationFixtures.GappyAsync(file); },
            };
            var bases = await Task.WhenAll(fx.EnsureBaseAsync(), fx.EnsureBaseAsync());
            using (var copy = await fx.Copy("iso")) Assert.NotNull(copy.Sessions.Current);

            Assert.Equal(1, builds);
            Assert.Equal(bases[0], bases[1]);
            Assert.NotEqual(Path.GetFullPath(Path.Combine(OutDir, "bases")), Path.GetFullPath(CommutationFixture.BasesDir));
            Assert.Contains(Environment.ProcessId.ToString(), CommutationFixture.BasesDir, StringComparison.Ordinal);
            Assert.StartsWith(CommutationFixture.BasesDir, fx.BasePath, StringComparison.Ordinal);
        }

        /// <summary>The full gate over every fixture and every pair, with the coverage bar (about sixteen minutes). A sweep test:
        /// the default run filters it out and the nightly sweep runs it. SEMANTICUS_PLAN_COMMUTATION_FIXTURE narrows it to one fixture.</summary>
        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task Full_commutation_gate_with_the_coverage_bar()
        {
            var only = Environment.GetEnvironmentVariable("SEMANTICUS_PLAN_COMMUTATION_FIXTURE");
            var outcomes = new List<PairOutcome>();
            var log = new StringBuilder();
            foreach (var fx in CommutationFixtures.All.Where(x => only == null || x.Name == only))
            {
                var (rows, fps) = await FootprintsAsync(fx, log);
                for (var i = 0; i < rows.Length; i++)
                for (var j = i + 1; j < rows.Length; j++)
                {
                    var o = await RunPairAsync(fx, rows[i], rows[j], fps, fx.Name + "-" + i + "-" + j);
                    outcomes.Add(o);
                    log.AppendLine(Line(o));
                }
            }
            var independent = outcomes.Where(o => o.Independent).ToArray();
            var families = independent.SelectMany(o => new[] { o.FamA, o.FamB }).Distinct(StringComparer.Ordinal).OrderBy(x => x).ToArray();
            var models = independent.Select(o => o.Fixture).Distinct(StringComparer.Ordinal).ToArray();
            var dependent = outcomes.Count(o => !o.Independent);
            var cautious = outcomes.Count(o => !o.Independent && o.Check1 && o.Check2);
            var summary = $"pairs={outcomes.Count} independent={independent.Length} dependent={dependent} models={models.Length} "
                        + $"families={families.Length} ({string.Join(", ", families)}) dependent-that-commute-anyway={cautious}";
            log.AppendLine(summary);
            Write(only ?? "all", outcomes, log);
            _out.WriteLine(summary);

            // A fixture filter that names no fixture ran no pair, and zero failures over zero pairs is not a pass (N14).
            Assert.True(outcomes.Count > 0, "the gate ran no pair" + (only == null ? "" : " for SEMANTICUS_PLAN_COMMUTATION_FIXTURE=" + only
                + "; the fixtures are " + string.Join(", ", CommutationFixtures.All.Select(x => x.Name))) + ". " + summary);
            var failed = Failures(outcomes);
            Assert.True(failed.Length == 0, "pairs that fail the gate:\n" + string.Join("\n", failed));
            if (only == null)
            {
                Assert.True(independent.Length >= 30, "coverage: " + summary);
                Assert.True(models.Length >= 3, "coverage: " + summary);
                Assert.True(families.Length >= 5, "coverage: " + summary);
                Assert.True(dependent >= 10, "coverage: " + summary);
            }
        }
    }

    internal sealed class CommutationFixture
    {
        public string Name;
        public Func<string, Task<DecisionFixture>> Build;
        public Func<DecisionFixture, Task> PostOpen;
        public bool ReportsChosen;
        private string _base;
        private readonly System.Threading.SemaphoreSlim _building = new System.Threading.SemaphoreSlim(1, 1);
        internal string BasePath => _base;

        /// <summary>This process's own bases folder (lane m11/plan-waits). The bases were one shared folder, so a second
        /// test process on the machine rebuilt a base over the first's mid-run. The folder carries the process id and a
        /// random part (a process id can be reused). The next process removes the folders of processes that are gone.</summary>
        internal static string BasesDir => BasesDirLazy.Value;

        private static readonly Lazy<string> BasesDirLazy = new Lazy<string>(() =>
        {
            var dir = Path.Combine(PlanCommutationTests.OutDir, "bases",
                Environment.ProcessId + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            // NO CODE RUNS AS THE TEST PROCESS EXITS (NoBrowserSignInGuardTests, state-home lane): a folder is removed by the
            // NEXT process instead, which sweeps the folders whose process is gone. The process id leads the folder's name.
            SweepDeadProcessBases(Path.GetDirectoryName(dir));
            return dir;
        });

        private static void SweepDeadProcessBases(string parent)
        {
            try
            {
                foreach (var old in Directory.GetDirectories(parent))
                {
                    var head = Path.GetFileName(old).Split('-')[0];
                    if (!int.TryParse(head, out var pid) || pid == Environment.ProcessId) continue;
                    bool alive;
                    try { System.Diagnostics.Process.GetProcessById(pid).Dispose(); alive = true; } catch (ArgumentException) { alive = false; }
                    if (alive) continue;
                    try { Directory.Delete(old, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>Build the base once. Two tests asking at once wait for one build: two builds in one process at the
        /// same moment also collide inside TOM.</summary>
        internal async Task<string> EnsureBaseAsync()
        {
            await _building.WaitAsync();
            try
            {
                if (_base == null)
                {
                    using (var b = await Build("Base-" + Name + ".bim"))
                    {
                        var target = Path.Combine(BasesDir, Name + ".bim");
                        File.Copy(Path.Combine(b.Workspace, "Base-" + Name + ".bim"), target, true);
                        _base = target;
                    }
                }
                return _base;
            }
            finally { _building.Release(); }
        }

        /// <summary>A throwaway copy of ONE saved build of the fixture, so every copy starts from the same bytes
        /// (relationship names and lineage tags are random per build).</summary>
        public async Task<DecisionFixture> Copy(string tag)
        {
            await EnsureBaseAsync();
            var f = new DecisionFixture(TestEntitlements.Pro);
            var path = Path.Combine(f.Workspace, tag.Replace('/', '_') + ".bim");
            File.Copy(_base, path, true);
            await f.Engine.OpenAsync(path);
            f.UseNull();
            if (PostOpen != null) await PostOpen(f);
            return f;
        }
    }

    internal static class CommutationFixtures
    {
        public static readonly CommutationFixture[] All =
        {
            new CommutationFixture { Name = "Gappy", Build = GappyAsync },
            new CommutationFixture { Name = "Star", Build = StarAsync },
            new CommutationFixture { Name = "Colliding", Build = f => CollidingAsync(f, 4) },
            new CommutationFixture { Name = "Measures", Build = MeasuresAsync },
            new CommutationFixture { Name = "GappyReports", Build = GappyAsync, PostOpen = AddReportsAsync },
            new CommutationFixture { Name = "StarDescribed", Build = StarDescribedAsync },
            new CommutationFixture { Name = "Wide", Build = WideAsync, PostOpen = SeedAssistantAsync },
            // A fixture with a live Jev family, so the gate sees Jev rows (the verifier's D4, 26 Sep): what Copilot sees,
            // with two put-backs across two tables that lean on each other.
            new CommutationFixture { Name = "CopilotSees", Build = CopilotSeesAsync, PostOpen = JevOnAsync },
            new CommutationFixture { Name = "DateMark", Build = DateMarkAsync, PostOpen = SeedAssistantAsync },
        };

        /// <summary>Sales, Customer and Product, a measure on Sales that uses Product[List Price], and a person who left
        /// both out of what Copilot sees. Permission is given in the base build, so every copy carries the same marker
        /// and a copy's own grant writes nothing into its model. Everything is described except the Customer table.</summary>
        public static async Task<DecisionFixture> CopilotSeesAsync(string file)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Customer", "City", "String", null, "human");
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.SetObjectPropertyAsync("column:Sales/Amount", "FormatString", "#,0.00", "human");
            await e.CreateTableAsync("Product", "human");
            await e.CreateColumnAsync("table:Product", "List Price", "Double", null, "human");
            await e.SetObjectPropertyAsync("column:Product/List Price", "FormatString", "#,0.00", "human");
            await e.CreateColumnAsync("table:Product", "Colour", "String", null, "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await e.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await e.CreateMeasureAsync("table:Sales", "Average List Price", "AVERAGE ( Product[List Price] )", "human");
            await e.SetMeasureFormatAsync("measure:Sales/Total Sales", "#,0.00", "human");
            await e.SetMeasureFormatAsync("measure:Sales/Average List Price", "#,0.00", "human");
            await e.SetAiDataSchemaAsync("measure:Sales/Average List Price", false, null, "human");
            await e.SetAiDataSchemaAsync("column:Product/List Price", false, null, "human");
            var refs = await f.Sessions.Current.ReadAsync(m =>
                m.AllColumns.Select(c => ObjectRefs.For(c))
                 .Concat(m.AllMeasures.Select(x => ObjectRefs.For(x)))
                 .Concat(m.Tables.Where(t => t.Name != "Customer").Select(t => ObjectRefs.For(t)))
                 .ToArray());
            foreach (var r in refs) await e.SetDescriptionAsync(r, "Described by the fixture.", "human");
            await f.SaveAsAsync(file);
            await f.ConsentAsync();
            await f.SaveAsAsync(file);
            f.UseNull();
            return f;
        }

        /// <summary>Jev on for what Copilot sees and nothing else: sure to leave out Customer[CustomerKey], sure that
        /// people ask about everything else (so the person's two exclusions come back as put-backs, dropped, which the
        /// harness keeps as a person would).</summary>
        public static async Task JevOnAsync(DecisionFixture f)
        {
            await f.ConsentAsync();
            f.Engine.AiSchemaJevDecidesForTest = true;
            await f.Engine.SetDecisionPointEnabledAsync(Semanticus.Decisions.DecisionRegistry.MeasureFolderAndFormat, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(Semanticus.Decisions.DecisionRegistry.ColumnFormat, false, "human");
            f.Engine.UseDecisionsForTest(new Semanticus.Engine.Decisions.DecisionService(
                new CommutationJevOracle((table, name) => table == "Customer" && name == "CustomerKey" ? ("exclude", 0.95) : ("include", 0.95)),
                new Semanticus.Decisions.DecisionBudget()));
        }

        // ModelPlanChangesTests.GappyAsync, verbatim.
        public static async Task<DecisionFixture> GappyAsync(string file)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("Contoso", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await f.Engine.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await f.Engine.CreateTableAsync("Customer", "human");
            await f.Engine.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await f.Engine.CreateColumnAsync("table:Customer", "Name", "String", null, "human");
            await f.Engine.CreateColumnAsync("table:Customer", "City", "String", null, "human");
            await f.Engine.CreateColumnAsync("table:Sales", "ProductKey", "Int64", null, "human");
            await f.Engine.CreateTableAsync("Product", "human");
            await f.Engine.CreateColumnAsync("table:Product", "ProductKey", "Int64", null, "human");
            await f.Engine.CreateColumnAsync("table:Product", "Name", "String", null, "human");
            await f.Engine.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await f.Engine.CreateRelationshipAsync("column:Sales/ProductKey", "column:Product/ProductKey", null, true, "human");
            await f.Engine.SetColumnMetadataAsync("column:Sales/ProductKey", null, "Sum", null, null, "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Old Total", "SUM ( Sales[Amount] )", "human");
            await f.SaveAsAsync(file);
            f.UseNull();
            return f;
        }

        // StarterMeasuresFamilyTests.StarAsync, verbatim.
        public static async Task<DecisionFixture> StarAsync(string file)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.SetObjectPropertyAsync("column:Sales/Amount", "FormatString", "#,0.00", "human");
            await e.CreateColumnAsync("table:Sales", "Quantity", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "UnitPrice", "Double", null, "human");
            await e.SetColumnMetadataAsync("column:Sales/UnitPrice", null, "Average", null, null, "human");
            await e.CreateColumnAsync("table:Sales", "Discount", "Double", null, "human");
            await e.SetColumnMetadataAsync("column:Sales/Discount", null, "None", null, null, "human");
            await e.CreateColumnAsync("table:Sales", "Covered", "Double", null, "human");
            await e.CreateMeasureAsync("table:Sales", "Covered So Far", "SUM ( Sales[Covered] )", "human");
            await e.CreateTableAsync("Returns", "human");
            await e.CreateColumnAsync("table:Returns", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Returns", "Qty", "Int64", null, "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, null, "human");
            await e.CreateRelationshipAsync("column:Returns/CustomerKey", "column:Customer/CustomerKey", null, null, "human");
            await f.SaveAsAsync(file);
            f.UseNull();
            return f;
        }

        // A scripted fixture edit the spike adds: Star with every object described except the Customer table's,
        // so the describe row is small and does not overlap most of the plan (the coverage minimum needs the
        // describe family in independent pairs, and on the stock fixtures it writes nearly every object).
        public static async Task<DecisionFixture> StarDescribedAsync(string file)
        {
            var f = await StarAsync(file);
            var refs = await f.Sessions.Current.ReadAsync(m =>
                m.Tables.Where(t => t.Name != "Customer").Select(t => ObjectRefs.For(t))
                 .Concat(m.AllColumns.Where(c => c.Table.Name != "Customer").Select(c => ObjectRefs.For(c)))
                 .Concat(m.AllMeasures.Where(x => x.Table.Name != "Customer").Select(x => ObjectRefs.For(x)))
                 .ToArray());
            foreach (var r in refs) await f.Engine.SetDescriptionAsync(r, "Described by the fixture.", "human");
            await f.Engine.SaveAsync(Path.Combine(f.Workspace, file), "bim", true, "human");
            return f;
        }

        // P0b fixture: many existing objects with gaps, a time-intelligence measure with no marked date table (the
        // datetable row), and an assistant row composed from a seeded line (asst/set_display_folder). Everything is
        // described except the Customer and Product TABLES, so the describe row touches only those two objects.
        public static async Task<DecisionFixture> WideAsync(string file)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            await e.CreateTableAsync("Date", "human");
            await e.CreateColumnAsync("table:Date", "Date", "DateTime", "Date", "human");
            await e.CreateColumnAsync("table:Date", "Year", "Int64", null, "human");
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.SetObjectPropertyAsync("column:Sales/Amount", "FormatString", "#,0.00", "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "ProductKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "OrderDate", "DateTime", null, "human");
            await e.CreateColumnAsync("table:Sales", "Note", "String", null, "human");
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Customer", "City", "String", null, "human");
            await e.CreateColumnAsync("table:Customer", "Country", "String", null, "human");
            await e.CreateTableAsync("Product", "human");
            await e.CreateColumnAsync("table:Product", "ProductKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Product", "Colour", "String", null, "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await e.CreateRelationshipAsync("column:Sales/ProductKey", "column:Product/ProductKey", null, true, "human");
            await e.CreateRelationshipAsync("column:Sales/OrderDate", "column:Date/Date", null, true, "human");
            await e.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await e.CreateMeasureAsync("table:Sales", "Sales YTD", "TOTALYTD ( [Total Sales], 'Date'[Date] )", "human");
            var refs = await f.Sessions.Current.ReadAsync(m =>
                m.AllColumns.Select(c => ObjectRefs.For(c))
                 .Concat(m.AllMeasures.Select(x => ObjectRefs.For(x)))
                 .Concat(m.Tables.Where(t => t.Name != "Customer" && t.Name != "Product").Select(t => ObjectRefs.For(t)))
                 .ToArray());
            foreach (var r in refs) await e.SetDescriptionAsync(r, "Described by the fixture.", "human");
            await f.SaveAsAsync(file);
            f.UseNull();
            return f;
        }

        // UAT 26 Sep (lane m11/plan-waits): "Mark Date as the date table" waited for tasks about Margin %, a ratio with
        // no time intelligence. A date table, a year to date measure and Margin %, everything described, and an
        // assistant row that files Margin % in a folder: the pair the old footprint called dependent.
        public static async Task<DecisionFixture> DateMarkAsync(string file)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            await e.CreateTableAsync("Date", "human");
            await e.CreateColumnAsync("table:Date", "Date", "DateTime", "Date", "human");
            // A Date column the mark does not key (UAT 26 Sep: the mark waited for "Format 1 column in Date").
            await e.CreateColumnAsync("table:Date", "Month", "String", null, "human");
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.SetObjectPropertyAsync("column:Sales/Amount", "FormatString", "#,0.00", "human");
            await e.CreateColumnAsync("table:Sales", "OrderDate", "DateTime", null, "human");
            await e.CreateRelationshipAsync("column:Sales/OrderDate", "column:Date/Date", null, true, "human");
            await e.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await e.CreateMeasureAsync("table:Sales", "Sales YTD", "TOTALYTD ( [Total Sales], 'Date'[Date] )", "human");
            await e.CreateMeasureAsync("table:Sales", "Margin %", "DIVIDE ( [Total Sales], COUNTROWS ( Sales ) )", "human");
            var refs = await f.Sessions.Current.ReadAsync(m =>
                m.AllColumns.Select(c => ObjectRefs.For(c))
                 .Concat(m.AllMeasures.Select(x => ObjectRefs.For(x)))
                 .Concat(m.Tables.Select(t => ObjectRefs.For(t)))
                 .ToArray());
            foreach (var r in refs) await e.SetDescriptionAsync(r, "Described by the fixture.", "human");
            await f.SaveAsAsync(file);
            f.UseNull();
            return f;
        }

        /// <summary>The seeded assistant lines whose object this fixture has (each fixture seeds only its own).</summary>
        public static async Task SeedAssistantAsync(DecisionFixture f)
        {
            var seeds = CommutationRows.AsstSeeds["asst/set_display_folder"];
            var live = await f.Sessions.Current.ReadAsync(m => seeds.Where(x => ObjectRefs.Resolve(m, x.Ref) != null).ToArray());
            foreach (var (r, a) in live)
                await f.Engine.AddPlanItemAsync(r, "set_display_folder", a, null, null, null, "agent");
        }

        /// <summary>The creates fixture (lane m11/plan-waits): Wide plus a calculation group with one item, every object
        /// described but the Customer and Product tables as in Wide. Not one of <see cref="All"/>: its rows are the
        /// seeded creates, whose pairs <c>A_create_and_the_row_it_feeds_are_ordered</c> runs one by one.</summary>
        public static readonly CommutationFixture Creates = new CommutationFixture
        {
            Name = "Creates",
            Build = async file =>
            {
                var f = await WideAsync(file);
                var e = f.Engine;
                await e.CreateCalculationGroupAsync("Scenario", "human");
                await e.CreateCalculationItemAsync("table:Scenario", "Base", "SELECTEDMEASURE ()", "human");
                await e.CreateRelationshipAsync("column:Customer/CustomerKey", "column:Product/ProductKey", null, false, "human");
                var refs = await f.Sessions.Current.ReadAsync(m =>
                    m.Tables["Scenario"].Columns.Select(c => ObjectRefs.For(c)).Append(ObjectRefs.For(m.Tables["Scenario"])).ToArray());
                foreach (var r in refs) await e.SetDescriptionAsync(r, "Described by the fixture.", "human");
                await f.SaveAsAsync(file);
                return f;
            },
            PostOpen = async f =>
            {
                foreach (var id in CommutationRows.SeededItems.Keys) await CommutationRows.SeedItemsAsync(f, id);
            },
        };

        // ModelPlanTests.CollidingModelAsync, verbatim.
        public static async Task<DecisionFixture> CollidingAsync(string file, int groups)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("Contoso", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await f.Engine.CreateLinguisticSchemaAsync(null, "agent");
            for (var i = 1; i <= groups; i++)
            {
                await f.Engine.CreateColumnAsync("table:Sales", "Group" + i + "Key", "String", null, "human");
                await f.Engine.CreateMeasureAsync("table:Sales", "Group" + i + " Key", "SUM ( Sales[Amount] )", "human");
            }
            // Round 4 (THE CLEANUP OFFER RULE, version 2): the four Group keys are key-named text and a lone table relates to
            // nothing, so the row no longer offers any of them. The cleanup row keeps its place in this fixture through a related
            // dimension whose label the row can find and one plain unused text column on it. The fixture changed, never the rule.
            await f.Engine.CreateTableAsync("Segment", "human");
            await f.Engine.CreateColumnAsync("table:Segment", "SegmentKey", "Int64", null, "human");
            await f.Engine.CreateColumnAsync("table:Segment", "Segment", "String", null, "human");
            await f.Engine.CreateColumnAsync("table:Segment", "Notes", "String", null, "human");
            await f.Engine.CreateColumnAsync("table:Sales", "SegmentKey", "Int64", null, "human");
            await f.Engine.CreateRelationshipAsync("column:Sales/SegmentKey", "column:Segment/SegmentKey", null, true, "human");
            await f.SaveAsAsync(file);
            f.UseNull();
            return f;
        }

        // JevFamilyTests.MeasuresModelAsync, verbatim.
        public static async Task<DecisionFixture> MeasuresAsync(string file)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("Contoso", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            var revenue = await f.Engine.CreateMeasureAsync("table:Sales", "Revenue", "SUM ( Sales[Amount] )", "human", "Money");
            await f.Engine.SetMeasureFormatAsync(revenue, "$#,##0.00", "human");
            var orders = await f.Engine.CreateMeasureAsync("table:Sales", "Order Count", "COUNTROWS ( Sales )", "human", "Volume");
            await f.Engine.SetMeasureFormatAsync(orders, "#,##0", "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Margin Pct", "DIVIDE ( [Revenue], [Total Sales] )", "human");
            await f.Engine.CreateTableAsync("Returns", "human");
            await f.Engine.CreateColumnAsync("table:Returns", "Qty", "Int64", null, "human");
            await f.Engine.CreateMeasureAsync("table:Returns", "Return Count", "COUNTROWS ( Returns )", "human");
            await f.SaveAsAsync(file);
            f.UseNull();
            return f;
        }

        // Gappy plus a read report that uses Sales[Total Sales] (ModelPlanTests' check-row setup). A scripted
        // fixture edit the spike adds so the check family is on a plan.
        public static async Task AddReportsAsync(DecisionFixture f)
        {
            var visual = "{ \"visual\": { \"query\": { \"queryState\": { \"Values\": { \"projections\": [ "
                       + "{ \"field\": { \"Measure\": { \"Expression\": { \"SourceRef\": { \"Entity\": \"Sales\" } }, "
                       + "\"Property\": \"Total Sales\" } } } ] } } } } }";
            var reports = Path.Combine(f.Workspace, "Reports");
            var visualDir = Path.Combine(reports, "Sales.Report", "definition", "pages", "p1", "visuals", "v1");
            Directory.CreateDirectory(visualDir);
            File.WriteAllText(Path.Combine(reports, "Sales.Report", "definition", "report.json"), "{}");
            File.WriteAllText(Path.Combine(visualDir, "visual.json"), visual);
            await f.Engine.SetReportScopeAsync(
                new[] { new Semanticus.Engine.Lineage.ReportScopeChoice
                        { Kind = "local", Name = "Sales", Path = reports } }, "human");
            await f.Engine.CheckReportsAsync(Array.Empty<string>(), false, null, null, null, "human", default);
        }
    }
}
