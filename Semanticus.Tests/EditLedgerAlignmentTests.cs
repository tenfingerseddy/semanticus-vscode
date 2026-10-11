using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;
using Xunit.Abstractions;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Tests
{
    /// <summary>A small model for the ledger tests: one M table with a column and twelve measures, and one
    /// shared M expression. Written as a .bim in the caller's folder, so the ledger has a sidecar.</summary>
    internal static class LedgerFixture
    {
        public static string Write(string dir, string name = "Model.bim")
        {
            var db = new TOM.Database("Ledger") { CompatibilityLevel = 1600, Model = new TOM.Model() };
            var t = new TOM.Table { Name = "Sales", LineageTag = "tag-sales" };
            t.Partitions.Add(new TOM.Partition { Name = "Sales", Source = new TOM.MPartitionSource { Expression = "let Source = 1 in Source" } });
            t.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Decimal, SourceColumn = "Amount", LineageTag = "c-amount" });
            for (var i = 1; i <= 12; i++)
                t.Measures.Add(new TOM.Measure { Name = "M" + i, Expression = "SUM(Sales[Amount]) * " + i, LineageTag = "m-" + i });
            db.Model.Tables.Add(t);
            db.Model.Expressions.Add(new TOM.NamedExpression { Name = "Server", Kind = TOM.ExpressionKind.M, Expression = "\"contoso-sql\" meta [IsParameterQuery=true, Type=\"Text\", IsParameterQueryRequired=true]", LineageTag = "e-server" });
            var path = Path.Combine(dir, name);
            File.WriteAllText(path, TOM.JsonSerializer.SerializeDatabase(db));
            return path;
        }
    }

    /// <summary>
    /// The edit ledger's one load-bearing promise (Plan-heart contract 8.5 and 8.6): the entries it calls undoable
    /// are exactly the steps on TE2's undo stack, one entry per step, in stack order, and "undo to entry k" in one
    /// commit puts the model back byte for byte where it stood just before k was made. A ledger that drifts one
    /// step from the stack would offer "Undo to here" on the wrong row and take back the wrong edits, so this is a
    /// property test, not a handful of examples: a seeded random run of 500 operations mixing tracked edits,
    /// batches, a batch that throws and rolls back, rehearsals, undo, redo, undo to a point, redo to a point and
    /// new edits after an undo, then a straight run of 610 edits so the undo stack itself passes the ledger's 500
    /// entry cap (Sol 24 Sep) and one undo to the first of them takes all 610 back in one commit.
    ///
    /// The oracle after EVERY step reads TE2 directly (<c>UndoSteps</c> and <c>RedoSteps</c>,
    /// <c>UndoManager.cs:254-257</c>) and the model itself (a hash of the serialised database), never the ledger's
    /// own opinion of itself.
    /// </summary>
    public sealed class EditLedgerAlignmentTests : IAsyncLifetime
    {
        private readonly ITestOutputHelper _out;
        private SessionManager _sessions = null!;
        private LocalEngine _engine = null!;
        private string _dir = null!;
        private string _table = null!;

        public EditLedgerAlignmentTests(ITestOutputHelper output) => _out = output;

        public async Task InitializeAsync()
        {
            // A private copy: the ledger persists beside the model, and the shared fixture must stay untouched.
            _dir = Path.Combine(Path.GetTempPath(), "sem-ledger-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            // A small model: the oracle serialises the whole database after every one of about 1,700 steps.
            var bim = LedgerFixture.Write(_dir);
            _sessions = new SessionManager();
            _engine = new LocalEngine(_sessions, TestEntitlements.Pro);
            await _engine.OpenAsync(bim);
            _table = (await _engine.ListMeasuresAsync()).First().Table;
        }

        public Task DisposeAsync()
        {
            _engine.Dispose();
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
            return Task.CompletedTask;
        }

        private Session S => _sessions.Current;

        private async Task<string> HashAsync()
        {
            var s = S;
            var json = await s.ReadAsync(_ => TOM.JsonSerializer.SerializeDatabase(s.TomDatabase));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        }

        private async Task<UndoState> StackAsync()
        {
            var s = S;
            return await s.ReadAsync(_ => s.UndoStateNow());
        }

        // ---- the run's own record of what the model looked like around each entry --------------------------

        private readonly Dictionary<string, string> _before = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _after = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> _known = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> _createdMeasures = new List<string>();
        private string _hash = null!;       // the model's hash after the previous step
        private string _baseHash = null!;   // the model's hash with nothing on the undo stack from this run
        private int _baseSteps;             // TE2's step count when the run began
        private int _edits;                 // tracked edits that became ledger entries
        private int _maxSteps;

        /// <summary>After a step: file any new entry's before and after hashes, then check the whole alignment.</summary>
        private async Task SettleAsync(string op, int step)
        {
            var now = await HashAsync();
            var entries = _sessions.Ledger.Entries(S.Id);
            foreach (var e in entries)
            {
                if (!_known.Add(e.EntryId)) continue;
                _before[e.EntryId] = _hash;
                _after[e.EntryId] = now;
                _edits++;
            }
            _hash = now;
            await AssertAlignedAsync(op, step);
        }

        private async Task AssertAlignedAsync(string op, int step)
        {
            var st = await StackAsync();
            _maxSteps = Math.Max(_maxSteps, st.Steps);
            var undoable = _sessions.Ledger.Undoable(S.Id, st.Steps);
            var redoable = _sessions.Ledger.Redoable(S.Id, st.Steps, st.RedoSteps);
            var where = $"step {step} ({op}): TE2 has {st.Steps} undo and {st.RedoSteps} redo steps";

            // One undoable entry per step above the run's base, in stack order; the same for the redo side.
            Assert.True(Enumerable.Range(_baseSteps + 1, st.Steps - _baseSteps).SequenceEqual(undoable.Select(e => e.StepIndex)),
                $"{where}; the ledger's undoable steps are [{string.Join(",", undoable.Select(e => e.StepIndex))}]");
            Assert.True(Enumerable.Range(st.Steps + 1, st.RedoSteps).SequenceEqual(redoable.Select(e => e.StepIndex)),
                $"{where}; the ledger's redoable steps are [{string.Join(",", redoable.Select(e => e.StepIndex))}]");

            // LaterCount is the number of undoable entries newer than the row, so the newest reads 0.
            // Checked on the oldest, the newest and one in the middle: every row would make the check quadratic.
            foreach (var i in new[] { 0, undoable.Count / 2, undoable.Count - 1 }.Where(i => i >= 0 && i < undoable.Count).Distinct())
                Assert.Equal(undoable.Count - 1 - i, _sessions.Ledger.LaterCount(undoable[i].EntryId, S.Id, st.Steps));

            // The model is exactly what the newest undoable entry left behind.
            var top = undoable.LastOrDefault();
            Assert.True((top == null ? _baseHash : _after[top.EntryId]) == _hash,
                $"{where}; the model does not match the state the newest undoable entry ({top?.Label}) left");
        }

        // ---- the operations ---------------------------------------------------------------------------------

        private async Task<string> RandomMeasureAsync(Random rng)
        {
            var s = S;
            return await s.ReadAsync(m =>
            {
                var all = m.AllMeasures.ToList();
                return all[rng.Next(all.Count)].Name;
            });
        }

        private async Task EditAsync(Random rng, int step)
        {
            var name = await RandomMeasureAsync(rng);
            await S.MutateAsync(rng.Next(2) == 0 ? "human" : "agent", "describe " + name,
                m => m.AllMeasures.First(x => x.Name == name).Description = "ledger " + step);
        }

        private async Task CreateAsync(Random rng, int step)
        {
            var name = "Led_" + step;
            await _engine.CreateMeasureAsync("table:" + _table, name, step.ToString(), rng.Next(2) == 0 ? "human" : "agent");
            _createdMeasures.Add(name);
        }

        private async Task DaxAsync(int step)
        {
            if (_createdMeasures.Count == 0) return;
            var s = S;
            var live = await s.ReadAsync(m => _createdMeasures.Where(n => m.AllMeasures.Any(x => x.Name == n)).ToList());
            if (live.Count == 0) return;
            var name = live[step % live.Count];
            await _engine.SetDaxAsync("measure:" + _table + "/" + name, "1 + " + step, "agent");
        }

        private async Task DeleteAsync(Random rng)
        {
            // Deleting a measure the run made; when none is on the model now the commit changes nothing and must
            // leave no ledger entry and no step.
            var name = _createdMeasures.Count == 0 ? "missing" : _createdMeasures[rng.Next(_createdMeasures.Count)];
            await S.MutateAsync("agent", "delete " + name, m => m.AllMeasures.FirstOrDefault(x => x.Name == name)?.Delete());
        }

        private async Task BatchAsync(Random rng, int step)
        {
            var names = new List<string>();
            for (var k = 0; k < 2 + rng.Next(3); k++) names.Add(await RandomMeasureAsync(rng));
            await S.MutateAsync("agent", "batch " + step, m =>
            {
                var i = 0;
                foreach (var n in names) m.AllMeasures.First(x => x.Name == n).Description = $"batch {step}.{i++}";
            });
        }

        private async Task ThrowingBatchAsync(Random rng, int step)
        {
            var name = await RandomMeasureAsync(rng);
            await Assert.ThrowsAsync<InvalidOperationException>(() => S.MutateAsync("agent", "refused " + step, m =>
            {
                m.AllMeasures.First(x => x.Name == name).Description = "never " + step;
                throw new InvalidOperationException("refused on purpose");
            }));
        }

        private async Task RehearseAsync(Random rng, int step)
        {
            var name = await RandomMeasureAsync(rng);
            DryRunScope.Current = new DryRunCollector();
            try { await S.MutateAsync("agent", "rehearse " + step, m => m.AllMeasures.First(x => x.Name == name).Description = "rehearsed " + step); }
            finally { DryRunScope.Current = null; }
        }

        private async Task<int> CountCommitsAsync(Func<Task> act)
        {
            var seen = 0;
            Action<ChangeNotification> h = n => seen++;
            _sessions.Bus.Changed += h;
            try { await act(); }
            finally { _sessions.Bus.Changed -= h; }
            return seen;
        }

        private async Task UndoToAsync(EditLedgerEntry target, string op, int step)
        {
            var revision = S.Revision;
            var commits = await CountCommitsAsync(() => _engine.UndoToEntryAsync(target.EntryId, "human"));
            Assert.True(commits == 1, $"step {step} ({op}): undo to a point broadcast {commits} times, not once");
            Assert.Equal(revision + 1, S.Revision);
            var now = await HashAsync();
            Assert.True(_before[target.EntryId] == now,
                $"step {step} ({op}): undo to '{target.Label}' did not put the model back where it was before that change");
        }

        private async Task RedoToAsync(EditLedgerEntry target, string op, int step)
        {
            var commits = await CountCommitsAsync(() => _engine.RedoToEntryAsync(target.EntryId, "human"));
            Assert.True(commits == 1, $"step {step} ({op}): redo to a point broadcast {commits} times, not once");
            var now = await HashAsync();
            Assert.True(_after[target.EntryId] == now,
                $"step {step} ({op}): redo to '{target.Label}' did not bring back the state that change made");
        }

        // Lean suite (8 Oct): the full run (500 mixed operations, then 610 edits, the oracle read after every one of about 1,700 steps) took 86 s.
        // The default run keeps the same property on 45 mixed operations, then 60 edits, with the oracle read after the first and the last of
        // the edits and one undo to the first of them. Crossing the 500 entry mark costs about 20 s of commits, so only the full run does it (the sweep).
        [Fact]
        public Task The_ledger_stays_aligned_with_the_undo_stack_through_a_seeded_random_run_sample() => AlignedThroughARunAsync(45, 60, checkEveryEdit: false);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public Task The_ledger_stays_aligned_with_the_undo_stack_through_a_seeded_random_run_past_the_cap() => AlignedThroughARunAsync(500, 610, checkEveryEdit: true, pastCap: true);

        private async Task AlignedThroughARunAsync(int mixedSteps, int runLength, bool checkEveryEdit, bool pastCap = false)
        {
            const int seed = 7_240_924;
            var rng = new Random(seed);
            _out.WriteLine("seed " + seed);
            _baseSteps = (await StackAsync()).Steps;
            _hash = _baseHash = await HashAsync();
            await AssertAlignedAsync("start", 0);

            // Phase A: mixed operations.
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var step = 1; step <= mixedSteps; step++)
            {
                var st = await StackAsync();
                var roll = rng.Next(100);
                string op;
                if (roll < 22) { op = "edit"; await EditAsync(rng, step); }
                else if (roll < 30) { op = "create"; await CreateAsync(rng, step); }
                else if (roll < 35) { op = "dax"; await DaxAsync(step); }
                else if (roll < 38) { op = "delete"; await DeleteAsync(rng); }
                else if (roll < 46) { op = "batch"; await BatchAsync(rng, step); }
                else if (roll < 51) { op = "throw"; await ThrowingBatchAsync(rng, step); }
                else if (roll < 56) { op = "rehearse"; await RehearseAsync(rng, step); }
                else if (roll < 70) { op = "undo"; await _engine.UndoAsync("human"); }
                else if (roll < 80) { op = "redo"; await _engine.RedoAsync("agent"); }
                else if (roll < 91)
                {
                    op = "undo-to";
                    var undoable = _sessions.Ledger.Undoable(S.Id, st.Steps);
                    if (undoable.Count > 0) await UndoToAsync(undoable[rng.Next(undoable.Count)], op, step);
                    else op = "undo-to (none)";
                }
                else
                {
                    op = "redo-to";
                    var redoable = _sessions.Ledger.Redoable(S.Id, st.Steps, st.RedoSteps);
                    if (redoable.Count > 0) await RedoToAsync(redoable[rng.Next(redoable.Count)], op, step);
                    else op = "redo-to (none)";
                }
                counts[op] = counts.TryGetValue(op, out var c) ? c + 1 : 1;
                await SettleAsync(op, step);
            }
            _out.WriteLine("phase A: " + string.Join(", ", counts.OrderBy(k => k.Key).Select(k => k.Key + " " + k.Value)));

            // Every kind of operation the property names really ran.
            foreach (var op in new[] { "edit", "create", "dax", "delete", "batch", "throw", "rehearse", "undo", "redo", "undo-to", "redo-to" })
                Assert.True(counts.TryGetValue(op, out var n) && n > 0, $"the seeded run never did '{op}'");

            // Phase B: a run of edits in a row, so the stack itself holds more than the ledger's 500 cap, then one undo
            // back to the first of them in one commit, and one redo forward to the last.
            var firstB = (string)null;
            for (var k = 1; k <= runLength; k++)
            {
                await EditAsync(rng, 1000 + k);
                // The sample reads the oracle after the first edit (so the first one's before and after are on record) and after the last
                // (so the last one's are); the edits between them are still ledger entries and are still counted below.
                if (checkEveryEdit || k == 1 || k == runLength) await SettleAsync("run", 1000 + k);
                if (k == 1) firstB = _sessions.Ledger.Undoable(S.Id, (await StackAsync()).Steps).Last().EntryId;
            }
            var stack = await StackAsync();
            Assert.True(!pastCap || stack.Steps - _baseSteps > 500, $"the stack only reached {stack.Steps} steps");
                        Assert.True(_edits > runLength - 10, $"only {_edits} edits became ledger entries");
            var lastB = _sessions.Ledger.Undoable(S.Id, stack.Steps).Last();
            var first = _sessions.Ledger.Entries(S.Id).Single(e => e.EntryId == firstB);
            Assert.Equal(runLength - 1, _sessions.Ledger.LaterCount(firstB, S.Id, stack.Steps));

            await UndoToAsync(first, "undo-to first of the run", 2000);
            await SettleAsync("undo-to first of the run", 2000);
            Assert.Equal(runLength, _sessions.Ledger.Redoable(S.Id, (await StackAsync()).Steps, (await StackAsync()).RedoSteps).Count);

            await RedoToAsync(lastB, "redo-to last of the run", 2001);
            await SettleAsync("redo-to last of the run", 2001);
            _out.WriteLine($"edits {_edits}, deepest stack {_maxSteps}, ledger rows {_sessions.Ledger.Entries(S.Id).Count}");
        }

        [Fact]
        public async Task Undo_to_an_entry_that_is_not_undoable_is_refused_with_a_sentence_and_changes_nothing()
        {
            _baseSteps = (await StackAsync()).Steps;
            await S.MutateAsync("human", "describe one", m => m.AllMeasures.First().Description = "one");
            var entry = _sessions.Ledger.Entries(S.Id).Single();
            await _engine.UndoAsync("human");
            var revision = S.Revision;

            // Undone already: there is nothing left to undo down to.
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _engine.UndoToEntryAsync(entry.EntryId, "human"));
            Assert.Contains("already undone", ex.Message);
            // Unknown id.
            ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _engine.UndoToEntryAsync("ed-00000000", "human"));
            Assert.Contains("history", ex.Message);
            Assert.Equal(revision, S.Revision);
        }
    }
}
