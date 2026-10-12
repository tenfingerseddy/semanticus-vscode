// A person's keep must land in the build (M11 spec-first start, fix round after integration check 2, defect D2).
//
// THE FAULT. Jev was unsure of a format, so the line sits in Needs you. The person answers keep: the line read "Kept by you", the person approved, the build
// succeeded, and the measure had NO format, and a held line could say "nobody ticked the line" when somebody had. The executor writes a format that is not
// proven only for a line a PERSON kept (ChangeItem.KeptByPerson with the digest of the object they looked at), which the Plan sets when its tick is pressed
// and the start plan's keeps never did.
//
// THE CLASS. Every way a person accepts a start line must land the line in the build, or say truthfully why not. This is ONE table over every accept path and
// every line family the executor gates (the measure format and the column format are the two families whose write depends on a person's keep), run through
// the build's own line step (StartPlanBuild.MaterializeLines) on a model whose objects are not provably whole numbers. The paths that are NOT a person's
// accept (an assistant's keep, a knock-on of another keep) must NOT build an unproven format: that is the other half of the rule, and the table pins it.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.StartPlan;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class StartPlanKeepGateTests
    {
        private const string SalesItem = "jev.column-summarize.table:Sales";
        private const string MeasureRef = "measure:Sales/Total Amount";
        private const string ColumnRef = "column:Sales/Amount";

        public enum Family { MeasureFormat, ColumnFormat }

        /// <summary>One way a person (or someone else) accepts a line, and whether the format must be in the model afterwards.</summary>
        public sealed class Path
        {
            public string Name;
            public bool Built;
            public bool MeasureOnly;
            public Action<StartPlanDoc, string> Do;
            public override string ToString() => Name;
        }

        private static StartEditOutcome Edit(StartPlanDoc doc, string origin, string op, string target, string value = null)
        {
            var o = StartPlanEditing.Apply(doc, new StartPlanEdit { Op = op, Target = target, Value = value }, origin);
            Assert.True(o.Ok, op + " refused: " + o.Refused);
            return o;
        }

        private static string NeedId(StartPlanDoc doc, string lineRef) =>
            StartPlanDecide.NeedsYou(doc).Single(n => n.Kind == StartNeedKind.UnsureLine && n.Id.Contains(lineRef, StringComparison.OrdinalIgnoreCase)).Id;

        private static string KeyOf(StartPlanDoc doc, string lineRef) => StartPlanDecide.LineKey(doc.Lines.Single(l => l.Item.ObjectRef == lineRef));

        public static IEnumerable<object[]> Cases()
        {
            var paths = new[]
            {
                new Path { Name = "answer keep, the person", Built = true, Do = (d, r) => Edit(d, "human", "answer", NeedId(d, r), "keep") },
                new Path { Name = "keep_line (the tick), the person", Built = true, Do = (d, r) => Edit(d, "human", "keep_line", KeyOf(d, r)) },
                new Path { Name = "a pinned value, the person", Built = true, Do = (d, r) => Edit(d, "human", "pin", KeyOf(d, r), "#,##0") },
                new Path { Name = "dropped, then kept again, the person", Built = true, Do = (d, r) => { Edit(d, "human", "drop_line", KeyOf(d, r)); Edit(d, "human", "keep_line", KeyOf(d, r)); } },
                new Path { Name = "answer keep, the assistant", Built = false, Do = (d, r) => Edit(d, "agent", "answer", NeedId(d, r), "keep") },
                new Path { Name = "keep_line, the assistant", Built = false, Do = (d, r) => Edit(d, "agent", "keep_line", KeyOf(d, r)) },
                new Path { Name = "the person ticks it, then the assistant keeps it", Built = false, Do = (d, r) => { Edit(d, "human", "keep_line", KeyOf(d, r)); Edit(d, "agent", "keep_line", KeyOf(d, r)); } },
                // The person kept the MEASURE again; its unsure format line came back as a knock-on. They never saw that line as a question.
                new Path { Name = "a knock-on of keeping the measure, the person", Built = false, MeasureOnly = true,
                           Do = (d, r) => { Edit(d, "human", "set_pick", "Sales/Amount", "none"); Edit(d, "human", "keep_line", MeasureRef); } },
            };
            foreach (var p in paths)
                foreach (var fam in new[] { Family.MeasureFormat, Family.ColumnFormat })
                    if (!(p.MeasureOnly && fam == Family.ColumnFormat)) yield return new object[] { fam, p };
        }

        /// <summary>Sales[Amount] (a Double: not provably whole) and one measure over it. The doc is the start plan that made them, with the format line of the family
        /// under test sitting in Needs you: Jev worked it out and was not sure.</summary>
        private static async Task<(DecisionFixture F, StartPlanDoc Doc, string Ref, string Format)> RigAsync(Family family)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            try
            {
                var e = f.Engine;
                await e.CreateModelAsync("Keep", 1702);
                await e.CreateTableAsync("Sales", "human");
                await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
                await e.CreateMeasureAsync("table:Sales", "Total Amount", "SUM ( Sales[Amount] )", "human");
                var doc = StartPlanDecidePureTests.Doc();
                StartPlanDecide.FoldPicks(doc.Manifest, new[]
                {
                    new LocalEngine.SummarizePick { TableName = "Sales", ColumnName = "Amount", ColumnRef = ColumnRef, Pick = "sum", Sure = true, MeasureName = "Total Amount" },
                });
                var measure = family == Family.MeasureFormat;
                var objectRef = measure ? MeasureRef : ColumnRef;
                var digest = await f.Sessions.Require().ReadAsync(m => FormatFill.ComputeDigest(ObjectRefs.Resolve(m, objectRef)));
                doc.Lines.Add(new StartLine
                {
                    DecidedBy = StartDecidedBy.Jev, Point = measure ? DecisionRegistry.MeasureFolderAndFormat : DecisionRegistry.ColumnFormat, Kept = false,
                    Why = "Jev was not sure, so it is left out until you say.",
                    Item = new ChangeItem
                    {
                        Id = "fmt-" + Guid.NewGuid().ToString("N").Substring(0, 6), ObjectRef = objectRef, ObjectName = objectRef.Substring(objectRef.LastIndexOf('/') + 1),
                        Kind = measure ? "set_measure_format" : "set_format_string", Target = "formatString", Before = "(none)", After = "#,##0",
                        Source = "jev", JevItem = SalesItem, DecisionPoint = measure ? DecisionRegistry.MeasureFolderAndFormat : DecisionRegistry.ColumnFormat,
                        Status = "rejected", WorkedOutDigest = digest, Title = "Format as #,##0", Risk = "safe",
                        Needs = measure ? JevFamily.NeedsKey("create_measure", MeasureRef) : null,
                    },
                });
                doc.Decide.State = StartDecideState.Stable;
                return (f, doc, objectRef, "#,##0");
            }
            catch { f.Dispose(); throw; }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(Cases))]
        public async Task Every_way_a_person_accepts_a_format_line_lands_it_in_the_build_and_nothing_else_does(Family family, Path path)
        {
            var (f, doc, objectRef, format) = await RigAsync(family);
            using (f)
            {
                // A copy through the store, as every real round trips the plan: the keep must survive being saved and read back.
                path.Do(doc, objectRef);
                doc = StartPlanStore.Copy(doc);
                var line = doc.Lines.Single(l => l.Item.ObjectRef == objectRef);
                var s = f.Sessions.Require();

                Exception refused = null;
                try { await s.MutateAsync("human", "build lines", m => StartPlanBuild.MaterializeLines(f.Engine, m, s, doc)); }
                catch (StartPlanBuildRefusedException ex) { refused = ex; }

                var live = await s.ReadAsync(m => ObjectRefs.Resolve(m, objectRef) is var o && o is TabularEditor.TOMWrapper.Measure me ? me.FormatString : ((TabularEditor.TOMWrapper.Column)o).FormatString);
                if (path.Built)
                {
                    Assert.True(refused == null, "the person's keep was refused at the build: " + refused?.Message);
                    Assert.Equal(format, live);
                    Assert.True(line.Kept && line.DecidedBy == StartDecidedBy.Person);
                }
                else
                {
                    Assert.True(string.IsNullOrEmpty(live), "a format nobody who looked at it ticked went into the model: '" + live + "'");
                    Assert.NotNull(refused);   // and the build says so: it never builds half of what a person approved in silence
                    Assert.False(refused.Message.Contains("you ticked", StringComparison.OrdinalIgnoreCase));
                }
            }
        }

        // ============================================================ a kept line on the name a rename gives the object

        /// <summary>The assistant's rename of a measure runs in the same build, after the lines that name the old spelling, and a line the loop made is stored against the
        /// NEW name (it was made on a scratch where the renames had run). The build looked for that name in the model BEFORE the renames, did not find it, read the line as
        /// "held" and skipped it without a word, while the plan still said "Kept by you". Found by the integration check on J8 (a person's kept format on "Total Hrs Mtd Value").</summary>
        [Fact]
        public async Task A_kept_format_stored_against_the_new_name_of_a_renamed_measure_lands_in_the_build()
        {
            var (f, doc, _, format) = await RigAsync(Family.MeasureFormat);
            using (f)
            {
                const string newName = "Total Amount Value", newRef = "measure:Sales/Total Amount Value";
                var line = doc.Lines.Single();
                line.Item.ObjectRef = newRef; line.Item.ObjectName = newName;   // as the loop stored it, on the scratch
                line.Item.Needs = null;
                doc.Lines.Add(new StartLine
                {
                    DecidedBy = StartDecidedBy.Assistant, Kept = true,
                    Item = new ChangeItem { Id = "rn-1", ObjectRef = MeasureRef, ObjectName = "Total Amount", Kind = "rename", Target = "name", Before = "Total Amount", After = newName, Source = "deterministic", Status = "approved", Risk = "rename" },
                });
                Edit(doc, "human", "keep_line", KeyOf(doc, newRef));
                doc = StartPlanStore.Copy(doc);
                var s = f.Sessions.Require();
                await s.MutateAsync("human", "build lines", m => StartPlanBuild.MaterializeLines(f.Engine, m, s, doc));
                var built = await s.ReadAsync(m => ObjectRefs.Resolve(m, newRef) is TabularEditor.TOMWrapper.Measure me ? me.FormatString : "(measure not found under its new name)");
                Assert.Equal(format, built);
            }
        }

        // ============================================================ one question per object after the assistant renamed a time measure

        /// <summary>The assistant gives a time measure a business name. The manifest cannot make that rename (a time measure comes from its base), so it stays a line for the
        /// executor, and Jev's unsure lines on the old name and on the new name were BOTH in Needs you: two questions about one measure. Renames are folded first.</summary>
        [Fact]
        public void Needs_you_asks_one_question_for_a_time_measure_the_assistant_renamed()
        {
            var doc = StartPlanDecidePureTests.Doc();
            doc.Decide.State = StartDecideState.Stable;
            StartLine Unsure(string objectRef, string kind, string target, string after) => new StartLine
            {
                DecidedBy = StartDecidedBy.Jev, Point = DecisionRegistry.MeasureFolderAndFormat, Kept = false, Why = "Jev was not sure, so it is left out until you say.",
                Item = new ChangeItem
                {
                    ObjectRef = objectRef, ObjectName = objectRef.Substring(objectRef.LastIndexOf('/') + 1), Kind = kind, Target = target, After = after, Source = "jev", JevItem = SalesItem,
                    DecisionPoint = DecisionRegistry.MeasureFolderAndFormat, Status = "rejected", Title = kind,
                },
            };
            const string oldRef = "measure:Sales/Total Amount MTD", newRef = "measure:Sales/Total Amount Mtd Value";
            doc.Lines.Add(Unsure(oldRef, "set_measure_format", "formatString", "#,##0"));
            doc.Lines.Add(Unsure(newRef, "set_measure_format", "formatString", "#,##0"));
            doc.Lines.Add(Unsure(oldRef, "set_description", "description", "The total to date."));   // another property of the same object is another question
            doc.Lines.Add(new StartLine
            {
                DecidedBy = StartDecidedBy.Assistant, Kept = true,
                Item = new ChangeItem { ObjectRef = oldRef, Kind = "rename", Target = "name", Before = "Total Amount MTD", After = "Total Amount Mtd Value", Source = "deterministic", Status = "approved" },
            });

            var needs = StartPlanDecide.NeedsYou(doc);
            Assert.Equal(2, needs.Count);
            var format = Assert.Single(needs, n => n.Id.Contains("set_measure_format", StringComparison.Ordinal));
            Assert.Contains("Total Amount Mtd Value", format.Id);   // the question is about the measure as it will be called, not its old name
            Assert.DoesNotContain("MTD|", format.Id);
            Assert.Single(needs, n => n.Id.Contains("set_description", StringComparison.Ordinal));

            // With no twin on the new name, the old line is still the one question, and answering it works.
            doc.Lines.RemoveAt(1);
            Assert.Equal(2, StartPlanDecide.NeedsYou(doc).Count);
        }

        // ============================================================ a tick set aside when the object changed is told in words, and never "nobody ticked it"

        [Fact]
        public async Task A_keep_whose_object_changed_since_Jev_looked_is_set_aside_in_words_that_say_so()
        {
            var (f, doc, objectRef, format) = await RigAsync(Family.MeasureFormat);
            using (f)
            {
                Edit(doc, "human", "keep_line", KeyOf(doc, objectRef));
                // The measure computes something else by the time it is built.
                await f.Engine.SetDaxAsync(MeasureRef, "SUM ( Sales[Amount] ) * 2", "human");
                var s = f.Sessions.Require();
                var ex = await Assert.ThrowsAsync<StartPlanBuildRefusedException>(() => s.MutateAsync("human", "build lines", m => StartPlanBuild.MaterializeLines(f.Engine, m, s, doc)));
                Assert.DoesNotContain("nobody ticked", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
