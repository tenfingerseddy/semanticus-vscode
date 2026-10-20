using System;
using System.Linq;
using System.Reflection;
using Semanticus.Engine.Composer;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Every sentence a draft shows is the engine's, from DraftCopy (composer contract 10.3, 10.6 and 5.4). Each shape
    /// is held byte for byte against the contract, in the singular and the plural, and none carries an em dash.
    /// </summary>
    public sealed class DraftCopyTests
    {
        [Theory]
        [InlineData(0, 0, "Runs by itself.")]
        [InlineData(0, 1, "Your assistant writes 1 part, then it runs by itself.")]
        [InlineData(0, 2, "Your assistant writes 2 parts, then it runs by itself.")]
        [InlineData(1, 0, "You answer 1 question, then it runs by itself.")]
        [InlineData(3, 0, "You answer 3 questions, then it runs by itself.")]
        [InlineData(1, 2, "You answer 1 question and your assistant writes 2 parts, then it runs by itself.")]
        [InlineData(2, 1, "You answer 2 questions and your assistant writes 1 part, then it runs by itself.")]
        public void The_summary_sentence_has_only_the_contracts_shapes(int questions, int parts, string expected) =>
            Assert.Equal(expected, DraftCopy.Summary(questions, parts));

        [Fact]
        public void The_done_shapes_read_as_the_frames()
        {
            Assert.Equal("Done. One Undo takes back all 7 steps.", DraftCopy.DoneAll(7));
            Assert.Equal("Done. One Undo takes back the step.", DraftCopy.DoneAll(1));
            Assert.Equal("Done. One Undo takes back steps 1 to 7.", DraftCopy.DoneSteps(7));
            Assert.Equal("Done. One Undo takes back step 1.", DraftCopy.DoneSteps(1));
            Assert.Equal("Done. One Undo takes back the change to the model, including the refreshed Sales data. Publishing to Test happened after it, so Undo does not reach it. Undo publish does.",
                DraftCopy.DonePublished("Sales", "Test"));
            Assert.Equal("Ready. It runs as one change.", DraftCopy.ReadySentence);
            Assert.Equal("Running as one change", DraftCopy.RunningSentence);
            Assert.Equal("Every blank is filled and checked.", DraftCopy.RunningNote);
        }

        [Fact]
        public void The_notes_read_as_the_frames()
        {
            Assert.Equal("Loading the data runs after the change. Undo removes the table and its data.", DraftCopy.LoadingNote);
            Assert.Equal("2 checks happen after it runs; if one fails, one Undo takes the change back.", DraftCopy.AfterChecksNote(2));
            Assert.Equal("1 check happens after it runs; if it fails, one Undo takes the change back.", DraftCopy.AfterChecksNote(1));
            Assert.Equal("Publishing to Test happens after the change. Undo does not reach it. Undo publish does.", DraftCopy.PublishNote("Test"));
            Assert.Equal("Both parts passed their checks; 1 answer is filled in for you to check.", DraftCopy.PartsPassed(2, 1));
            Assert.Equal("The part passed its checks.", DraftCopy.PartsPassed(1, 0));
            Assert.Equal("All 3 parts passed their checks; 2 answers are filled in for you to check.", DraftCopy.PartsPassed(3, 2));
            Assert.Equal("2 checks ran after it; both passed.", DraftCopy.AfterChecksRan(2, 2));
            Assert.Equal("1 check ran after it; it passed.", DraftCopy.AfterChecksRan(1, 1));
            Assert.Equal("3 checks ran after it; 2 passed.", DraftCopy.AfterChecksRan(3, 2));
            Assert.Equal("Nothing has changed yet. It runs by itself when both parts pass their checks.", DraftCopy.WaitingNote(2));
            Assert.Equal("Nothing has changed yet. It runs by itself when the part passes its checks.", DraftCopy.WaitingNote(1));
            Assert.Equal("Your assistant sees this on its next call. Or copy the request.", DraftCopy.OnNextCall);
        }

        [Fact]
        public void The_blank_lines_read_as_the_frames()
        {
            Assert.Equal("Waiting to be written", DraftCopy.BlankEmpty);
            Assert.Equal("Your assistant sees this on its next call. Or copy the request.", DraftCopy.BlankQueued);
            Assert.Equal("Written by your assistant, checked: SUM ( Returns[Quantity] )", DraftCopy.BlankFilled("SUM ( Returns[Quantity] )"));
            Assert.Equal("Written by you, checked: Share of units.", DraftCopy.BlankSelf("Share of units."));
            Assert.Equal("Needs you", DraftCopy.BlankNeeds);
            Assert.Equal("Your assistant asks: Which column?", DraftCopy.AssistantAsks("Which column?"));
        }

        [Fact]
        public void The_new_copy_reads_as_contract_10_6()
        {
            Assert.Equal("Rules about who can see which data wait for you to read them. Read the parts, then press Approve and run.", DraftCopy.HeldRowSecurity);
            Assert.Equal("This workflow publishes after the change, so your assistant's writing waits for you to read it. Read the parts, then press Approve and run.", DraftCopy.HeldPublish);
            Assert.Equal("Sales changed while this waited.", DraftCopy.EvidenceMoved("Sales"));
            Assert.Equal("This run ended when Semanticus restarted. Your draft is kept. Run it again.", DraftCopy.Restarted);
            Assert.Equal("Your assistant changes the model here. This step is its own undo step.", DraftCopy.DirectStep);
            Assert.Equal("Your assistant also changed 3 things outside this run", DraftCopy.OutsideRun(3));
            Assert.Equal("Your assistant also changed 1 thing outside this run", DraftCopy.OutsideRun(1));
            Assert.Equal("This one waits for the person because it sets who can see which data. When they say yes, call approve_draft.",
                DraftCopy.HeldRefusal(DraftHolds.RowSecurity));
            Assert.Equal("This one waits for the person because it publishes after the change. When they say yes, call approve_draft.",
                DraftCopy.HeldRefusal(DraftHolds.Publish));
            Assert.Equal("Also dropped: needs step 1", DraftCopy.AlsoDropped(new[] { 1 }));
            Assert.Equal("Also dropped: needs steps 1 and 3", DraftCopy.AlsoDropped(new[] { 1, 3 }));
            Assert.Equal("Semanticus has 2 parts for you to write in \"Add return measures\".", DraftCopy.CopyHead(2, "Add return measures"));
            Assert.Equal("Semanticus has 1 part for you to write in \"Add return measures\".", DraftCopy.CopyHead(1, "Add return measures"));
        }

        [Fact]
        public void The_banners_read_as_the_frames()
        {
            Assert.Equal("One check failed after the change", DraftCopy.CheckFailedTitle);
            Assert.Equal("Return rate is 134%. The check needs 0% to 100%. One Undo takes back steps 1 to 7, and the table goes with its data.",
                DraftCopy.CheckFailedText(DraftCopy.RangeFailed("Return rate", 1.34, 0, 1), 7, true));
            Assert.Equal("Stopped at step 2 of 5", DraftCopy.StoppedTitle(2, 5));
            Assert.Equal("So far: Step 1 ran and is kept as one change. Steps 3 to 5 did not run.", DraftCopy.SoFar(new[] { 1 }, 2, 5));
            Assert.Equal("So far: Nothing changed. Step 2 did not run.", DraftCopy.SoFar(new int[0], 1, 2));
        }

        [Fact]
        public void A_steps_changes_line_counts_its_ops()
        {
            Assert.Equal("Adds 1 table and 7 columns", DraftCopy.Changes(new[] { "create_import_table" }.Concat(Enumerable.Repeat("create_column", 7))));
            Assert.Equal("Changes 4 types", DraftCopy.Changes(Enumerable.Repeat("set_column_data_type", 4)));
            Assert.Equal("Sets 1 description", DraftCopy.Changes(new[] { "set_description" }));
            Assert.Equal("Adds 1 measure; Sets 1 format", DraftCopy.Changes(new[] { "create_measure", "set_measure_format" }));
        }

        /// <summary>No em dash in any constant, and none in any shape above for the counts a draft can reach.</summary>
        [Fact]
        public void No_draft_sentence_carries_an_em_dash()
        {
            var consts = typeof(DraftCopy).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()).ToList();
            Assert.NotEmpty(consts);
            for (var n = 0; n <= 4; n++)
                for (var m = 0; m <= 4; m++)
                {
                    consts.Add(DraftCopy.Summary(n, m));
                    consts.Add(DraftCopy.PartsPassed(Math.Max(1, n), m));
                    consts.Add(DraftCopy.AfterChecksRan(Math.Max(1, n), Math.Min(Math.Max(1, n), m)));
                }
            foreach (var s in consts)
            {
                Assert.DoesNotContain('\u2014', s);
                Assert.DoesNotContain('\u2013', s);
            }
        }
    }
}
