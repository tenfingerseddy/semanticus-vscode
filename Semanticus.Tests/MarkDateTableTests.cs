using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// mark_date_table must actually reach the state its name promises. It used to accept a <c>dateColumn</c>, validate
    /// that the column existed, and then set only <c>DataCategory = "Time"</c> — never <c>IsKey</c> on that column. So
    /// the op named "mark date table" left the model still failing MODEL_SHOULD_HAVE_A_DATE_TABLE, whose test is
    /// <c>DataCategory == "Time" &amp;&amp; Columns.Any(IsKey == true &amp;&amp; DataType == "DateTime")</c>, and callers
    /// had to reach past the op and set IsKey themselves to get a marked date table.
    ///
    /// Both halves land in ONE <c>MutateAsync</c> batch on purpose: a partial undo that left DataCategory set with
    /// IsKey cleared would be a worse state than either, because the model would look marked and not satisfy the rule.
    /// </summary>
    public sealed class MarkDateTableTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "pro" };
        }

        private static async Task<(LocalEngine engine, SessionManager sm, string table, string col)> DateModelAsync()
        {
            var sm = new SessionManager();
            var engine = new LocalEngine(sm, new Fake());
            await engine.CreateModelAsync("DateMark", 1604);
            var t = await engine.CreateTableAsync("Date", "human");
            var col = await engine.CreateColumnAsync(t, "Date", "DateTime", "Date", "human");
            await engine.SetObjectPropertyAsync(col, "FormatString", "yyyy-mm-dd", "human");
            return (engine, sm, t, col);
        }

        // get_object's Table projection carries no dataCategory, so read the property-grid surface instead — it is
        // the same seam the UI edits through, and it exposes every editable property for both kinds.
        private static async Task<string?> PropAsync(LocalEngine engine, string objRef, string name)
        {
            var props = await engine.GetObjectPropertiesAsync(objRef);
            return props.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;
        }

        // ---- the defect itself ---------------------------------------------------------------------------------------

        [Fact]
        public async Task Mark_date_table_sets_the_named_column_as_the_key()
        {
            var (engine, _, t, col) = await DateModelAsync();
            using (engine)
            {
                Assert.Equal("False", await PropAsync(engine, col, "IsKey"));

                var r = await engine.MarkDateTableAsync(t, "Date", "human");

                Assert.True(r.Changed);
                Assert.Equal("True", await PropAsync(engine, col, "IsKey"));
                Assert.Equal("Time", await PropAsync(engine, t, "DataCategory"));
            }
        }

        // The product-level assertion: the op reaches the state the rule asks about. This is the one that would have
        // caught the defect without anyone having to know what IsKey is.
        [Fact]
        public async Task Mark_date_table_alone_satisfies_the_model_should_have_a_date_table_rule()
        {
            var (engine, _, t, _) = await DateModelAsync();
            using (engine)
            {
                var before = await engine.BpaScanAsync();
                Assert.Contains(before.Violations, v => v.RuleId == "MODEL_SHOULD_HAVE_A_DATE_TABLE");

                await engine.MarkDateTableAsync(t, "Date", "human");

                var after = await engine.BpaScanAsync();
                Assert.DoesNotContain(after.Violations, v => v.RuleId == "MODEL_SHOULD_HAVE_A_DATE_TABLE");
                // ...and the sibling rule that checks a DATE-named table really was marked is satisfied too.
                Assert.DoesNotContain(after.Violations,
                    v => v.RuleId == "DATE/CALENDAR_TABLES_SHOULD_BE_MARKED_AS_A_DATE_TABLE");
            }
        }

        // ---- undo: ONE entry, never a half-marked table -------------------------------------------------------------

        [Fact]
        public async Task Mark_date_table_is_one_undo_entry_that_reverts_both_halves()
        {
            var (engine, _, t, col) = await DateModelAsync();
            using (engine)
            {
                await engine.MarkDateTableAsync(t, "Date", "human");
                Assert.Equal("Time", await PropAsync(engine, t, "DataCategory"));
                Assert.Equal("True", await PropAsync(engine, col, "IsKey"));

                await engine.UndoAsync("human");

                // A SINGLE undo must clear BOTH. A half-undone mark (DataCategory still "Time", IsKey cleared) is worse
                // than either end state: it reads as a marked date table while failing the rule that defines one.
                Assert.NotEqual("Time", await PropAsync(engine, t, "DataCategory"));
                Assert.Equal("False", await PropAsync(engine, col, "IsKey"));
            }
        }

        // ---- dual-drive: exactly one broadcast, origin tagged -------------------------------------------------------

        [Fact]
        public async Task Mark_date_table_broadcasts_once_so_both_doors_see_it()
        {
            var (engine, sm, t, _) = await DateModelAsync();
            using (engine)
            {
                var seen = new List<ChangeNotification>();
                Action<ChangeNotification> handler = n => seen.Add(n);
                sm.Bus.Changed += handler;
                try
                {
                    var before = sm.Current.Revision;
                    var r = await engine.MarkDateTableAsync(t, "Date", "agent");

                    // ONE notification for the whole gesture, not one per property — the UI door must not see a
                    // half-marked table in between.
                    var n = Assert.Single(seen);
                    Assert.Equal("agent", n.Origin);
                    Assert.True(n.Revision > before);
                    Assert.Equal(r.Revision, n.Revision);
                    Assert.NotEmpty(n.Deltas);
                }
                finally { sm.Bus.Changed -= handler; }
            }
        }

        // ---- the omitted-column case: resolve it or refuse instructively, never silently half-mark ------------------

        [Fact]
        public async Task Mark_date_table_resolves_the_only_datetime_column_when_none_is_named()
        {
            var (engine, _, t, col) = await DateModelAsync();
            using (engine)
            {
                await engine.MarkDateTableAsync(t, null, "human");
                Assert.Equal("True", await PropAsync(engine, col, "IsKey"));
                Assert.Equal("Time", await PropAsync(engine, t, "DataCategory"));
            }
        }

        [Fact]
        public async Task Mark_date_table_refuses_when_the_date_column_is_ambiguous_and_changes_nothing()
        {
            var (engine, _, t, col) = await DateModelAsync();
            using (engine)
            {
                await engine.CreateColumnAsync(t, "Shipped Date", "DateTime", "Shipped Date", "human");

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.MarkDateTableAsync(t, null, "human"));
                Assert.Contains("Shipped Date", ex.Message);   // the error names the candidates to choose between

                // Refused means nothing moved: no half-marked table left behind.
                Assert.NotEqual("Time", await PropAsync(engine, t, "DataCategory"));
                Assert.Equal("False", await PropAsync(engine, col, "IsKey"));
            }
        }

        // ---- generate_date_table: the Note must not claim a marking it did not complete -----------------------------

        // The sibling op sets DataCategory = "Time" and nothing else, then said "Marked as a date table." That sentence
        // was false, and a customer reads it. IsKey cannot be set there — a calculated table has no columns until it is
        // deployed and processed — and NOTHING sets it later either: the only IsKey writes in the whole solution are
        // build_model_from_spec, apply_schema_update and mark_date_table, none of which is a refresh path. So the honest
        // Note says the marking is unfinished and names the op that finishes it.
        [Fact]
        public async Task Generate_date_table_note_does_not_claim_a_marking_it_did_not_complete()
        {
            var sm = new SessionManager();
            using var engine = new LocalEngine(sm, new Fake());
            await engine.CreateModelAsync("Gen", 1604);

            var r = await engine.GenerateDateTableAsync("Date", null, null, markAsDate: true, "human");

            // Ground truth first: the model is NOT a marked date table, so any Note claiming it is would be false.
            var card = await engine.BpaScanAsync();
            Assert.Contains(card.Violations, v => v.RuleId == "MODEL_SHOULD_HAVE_A_DATE_TABLE");
            Assert.Contains(card.Violations, v => v.RuleId == "DATE/CALENDAR_TABLES_SHOULD_BE_MARKED_AS_A_DATE_TABLE");

            // Therefore the Note must not say it was marked, must say the marking is unfinished, and must name the op
            // that finishes it rather than leaving the user to discover the gap from a red gate.
            Assert.DoesNotContain("Marked as a date table.", r.Note);
            Assert.Contains("not finished", r.Note, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("mark_date_table", r.Note);
            // And it must not promise that a refresh completes it, because nothing sets IsKey on refresh.
            Assert.DoesNotContain("will be marked", r.Note, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Generate_date_table_without_marking_says_plainly_that_it_did_not_mark()
        {
            var sm = new SessionManager();
            using var engine = new LocalEngine(sm, new Fake());
            await engine.CreateModelAsync("Gen", 1604);

            var r = await engine.GenerateDateTableAsync("Date", null, null, markAsDate: false, "human");

            Assert.DoesNotContain("Marked as a date table.", r.Note);
            Assert.Contains("not", r.Note, StringComparison.OrdinalIgnoreCase);
            // Nothing was attempted, so it must not send the user off to finish a marking they never asked for.
            Assert.DoesNotContain("mark_date_table", r.Note);
        }

        [Fact]
        public async Task Mark_date_table_refuses_a_non_datetime_column_instead_of_marking_it_the_key()
        {
            var (engine, _, t, _) = await DateModelAsync();
            using (engine)
            {
                var text = await engine.CreateColumnAsync(t, "Label", "String", "Label", "human");

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.MarkDateTableAsync(t, "Label", "human"));
                Assert.Contains("Label", ex.Message);
                Assert.Equal("False", await PropAsync(engine, text, "IsKey"));
                Assert.NotEqual("Time", await PropAsync(engine, t, "DataCategory"));
            }
        }

        // ---- the plan door: the SAME mark, or the doors have drifted (F-029) ----------------------------------------

        // Both doors must reach one state, so the plan door's mark_date_table item is asserted against exactly the
        // contract the direct op above is asserted against. The plan item carries the chosen date column in After
        // (RequiresAfter is false for this kind, so omitting it is legal and means "resolve it for me").
        private static async Task<ApplyPlanReport> ApplyMarkItemAsync(LocalEngine engine, string tableRef, string after)
        {
            var plan = await engine.AddPlanItemAsync(tableRef, "mark_date_table", after, null, null, null, "human");
            var item = plan.Items.Single(i => i.Kind == "mark_date_table");
            Assert.Equal("approved", item.Status);   // deterministic kind: no opt-in step to forget
            return await engine.ApplyPlanAsync(new[] { item.Id }, "human");
        }

        [Fact]
        public async Task Plan_mark_date_table_sets_the_named_column_as_the_key()
        {
            var (engine, _, t, col) = await DateModelAsync();
            using (engine)
            {
                var report = await ApplyMarkItemAsync(engine, t, "Date");

                Assert.Equal(1, report.AppliedCount);
                Assert.Equal(0, report.FailedCount);
                // The half the plan door never learned. Without it the item reports success and the rule stays red.
                Assert.Equal("True", await PropAsync(engine, col, "IsKey"));
                Assert.Equal("Time", await PropAsync(engine, t, "DataCategory"));
            }
        }

        // The product-level assertion for the plan door: applying the item clears the REAL BPA finding, which is the
        // thing the deploy gate reads. An item that reports applied while the gate stays red is the defect.
        [Fact]
        public async Task Plan_mark_date_table_satisfies_the_model_should_have_a_date_table_rule()
        {
            var (engine, _, t, _) = await DateModelAsync();
            using (engine)
            {
                var before = await engine.BpaScanAsync();
                Assert.Contains(before.Violations, v => v.RuleId == "MODEL_SHOULD_HAVE_A_DATE_TABLE");

                var report = await ApplyMarkItemAsync(engine, t, "Date");

                Assert.Equal(1, report.AppliedCount);
                var after = await engine.BpaScanAsync();
                Assert.DoesNotContain(after.Violations, v => v.RuleId == "MODEL_SHOULD_HAVE_A_DATE_TABLE");
                Assert.DoesNotContain(after.Violations,
                    v => v.RuleId == "DATE/CALENDAR_TABLES_SHOULD_BE_MARKED_AS_A_DATE_TABLE");
                // The report's own before/after count must move too: it is what the caller is shown.
                Assert.True(report.BpaViolationsAfter < report.BpaViolationsBefore,
                    "BPA " + report.BpaViolationsBefore + " -> " + report.BpaViolationsAfter);
            }
        }

        [Fact]
        public async Task Plan_mark_date_table_resolves_the_only_datetime_column_when_none_is_named()
        {
            var (engine, _, t, col) = await DateModelAsync();
            using (engine)
            {
                var report = await ApplyMarkItemAsync(engine, t, null);

                Assert.Equal(1, report.AppliedCount);
                Assert.Equal("True", await PropAsync(engine, col, "IsKey"));
                Assert.Equal("Time", await PropAsync(engine, t, "DataCategory"));
                // The item named no column, so the note must name the one that was resolved rather than leave the
                // caller to guess which column became the key.
                Assert.Contains("Date", Assert.Single(report.Items).Note);
            }
        }

        [Fact]
        public async Task Plan_mark_date_table_refuses_an_ambiguous_choice_and_changes_nothing()
        {
            var (engine, _, t, col) = await DateModelAsync();
            using (engine)
            {
                await engine.CreateColumnAsync(t, "Shipped Date", "DateTime", "Shipped Date", "human");

                var report = await ApplyMarkItemAsync(engine, t, null);

                var item = Assert.Single(report.Items);
                Assert.Equal("failed", item.Status);
                Assert.Contains("Shipped Date", item.Note);   // the note names the candidates to choose between
                Assert.Equal(0, report.AppliedCount);
                // Refused means nothing moved. This matters MORE on this door than the direct one: the per-item
                // catch in ApplyPlanAsync sits INSIDE the MutateAsync batch, so a half-written item is not rolled
                // back -- it commits. Resolution has to happen before the first write.
                Assert.NotEqual("Time", await PropAsync(engine, t, "DataCategory"));
                Assert.Equal("False", await PropAsync(engine, col, "IsKey"));
            }
        }

        [Fact]
        public async Task Plan_mark_date_table_refuses_a_non_datetime_column_and_changes_nothing()
        {
            var (engine, _, t, col) = await DateModelAsync();
            using (engine)
            {
                var text = await engine.CreateColumnAsync(t, "Label", "String", "Label", "human");

                var report = await ApplyMarkItemAsync(engine, t, "Label");

                var item = Assert.Single(report.Items);
                Assert.Equal("failed", item.Status);
                Assert.Contains("Label", item.Note);
                Assert.Equal("False", await PropAsync(engine, text, "IsKey"));
                Assert.Equal("False", await PropAsync(engine, col, "IsKey"));
                Assert.NotEqual("Time", await PropAsync(engine, t, "DataCategory"));
            }
        }

        [Fact]
        public async Task Plan_mark_date_table_refuses_a_missing_column_and_changes_nothing()
        {
            var (engine, _, t, col) = await DateModelAsync();
            using (engine)
            {
                var report = await ApplyMarkItemAsync(engine, t, "Calendar Date");

                var item = Assert.Single(report.Items);
                Assert.Equal("failed", item.Status);
                Assert.Contains("Calendar Date", item.Note);
                Assert.NotEqual("Time", await PropAsync(engine, t, "DataCategory"));
                Assert.Equal("False", await PropAsync(engine, col, "IsKey"));
            }
        }

        [Fact]
        public async Task Plan_mark_date_table_refuses_a_table_with_no_date_column_and_changes_nothing()
        {
            var sm = new SessionManager();
            using var engine = new LocalEngine(sm, new Fake());
            await engine.CreateModelAsync("NoDate", 1604);
            var t = await engine.CreateTableAsync("Date", "human");
            await engine.CreateColumnAsync(t, "Label", "String", "Label", "human");

            var report = await ApplyMarkItemAsync(engine, t, null);

            var item = Assert.Single(report.Items);
            Assert.Equal("failed", item.Status);
            Assert.Equal(0, report.AppliedCount);
            Assert.NotEqual("Time", await PropAsync(engine, t, "DataCategory"));
        }

        // A refusal must not poison its neighbours either: the other approved items in the same batch still apply,
        // and the date table is the only thing left untouched. That is what "no partial mutation" means here.
        [Fact]
        public async Task Plan_mark_date_table_refusal_leaves_the_rest_of_the_batch_applied()
        {
            var (engine, _, t, col) = await DateModelAsync();
            using (engine)
            {
                await engine.CreateColumnAsync(t, "Shipped Date", "DateTime", "Shipped Date", "human");

                var plan = await engine.AddPlanItemAsync(t, "mark_date_table", null, null, null, null, "human");
                plan = await engine.AddPlanItemAsync(col, "set_description", "The calendar date.", null, null, null, "human");
                var mark = plan.Items.Single(i => i.Kind == "mark_date_table");
                var desc = plan.Items.Single(i => i.Kind == "set_description");

                var report = await engine.ApplyPlanAsync(new[] { mark.Id, desc.Id }, "human");

                Assert.Equal("failed", report.Items.Single(i => i.Id == mark.Id).Status);
                Assert.Equal("applied", report.Items.Single(i => i.Id == desc.Id).Status);
                Assert.Equal(1, report.AppliedCount);
                Assert.Equal(1, report.FailedCount);
                Assert.Equal("The calendar date.", await PropAsync(engine, col, "Description"));
                Assert.NotEqual("Time", await PropAsync(engine, t, "DataCategory"));
                Assert.Equal("False", await PropAsync(engine, col, "IsKey"));
            }
        }

        [Fact]
        public async Task Plan_mark_date_table_is_one_undo_entry_that_reverts_both_halves()
        {
            var (engine, _, t, col) = await DateModelAsync();
            using (engine)
            {
                await ApplyMarkItemAsync(engine, t, "Date");
                Assert.Equal("Time", await PropAsync(engine, t, "DataCategory"));
                Assert.Equal("True", await PropAsync(engine, col, "IsKey"));

                await engine.UndoAsync("human");

                // One undo, both halves gone. A half-undone mark reads as a marked date table while failing the
                // rule that defines one -- worse than either end state, on either door.
                Assert.NotEqual("Time", await PropAsync(engine, t, "DataCategory"));
                Assert.Equal("False", await PropAsync(engine, col, "IsKey"));
            }
        }

        [Fact]
        public async Task Plan_mark_date_table_broadcasts_once_so_both_doors_see_it()
        {
            var (engine, sm, t, _) = await DateModelAsync();
            using (engine)
            {
                var seen = new List<ChangeNotification>();
                Action<ChangeNotification> handler = n => seen.Add(n);
                var plan = await engine.AddPlanItemAsync(t, "mark_date_table", "Date", null, null, null, "human");
                var item = plan.Items.Single(i => i.Kind == "mark_date_table");
                sm.Bus.Changed += handler;
                try
                {
                    var before = sm.Current.Revision;
                    var report = await engine.ApplyPlanAsync(new[] { item.Id }, "agent");

                    // ONE notification for the whole gesture, not one per property.
                    var n = Assert.Single(seen);
                    Assert.Equal("agent", n.Origin);
                    Assert.True(n.Revision > before);
                    Assert.Equal(report.Revision, n.Revision);
                    Assert.NotEmpty(n.Deltas);
                }
                finally { sm.Bus.Changed -= handler; }
            }
        }

        // ---- the canonical category: "time" is not "Time" to the rules that decide the gate ------------------------

        // The shared core used to test the stored category with OrdinalIgnoreCase, so an existing lowercase "time"
        // was left in place. That value is reachable: the public property writer stores a string exactly as typed.
        // Both real rules compare the category exactly ("Time"), so the mark reported success while the findings
        // stayed open, and with the date column already the key the op reported no change at all.
        [Fact]
        public async Task Mark_date_table_makes_a_lowercase_time_category_canonical_and_clears_the_real_findings()
        {
            var (engine, _, t, col) = await DateModelAsync();
            using (engine)
            {
                // The reachable state, reached the way a caller reaches it: through the public property path.
                await engine.SetObjectPropertyAsync(t, "DataCategory", "time", "human");
                await engine.SetObjectPropertyAsync(col, "IsKey", "true", "human");
                Assert.Equal("time", await PropAsync(engine, t, "DataCategory"));

                // Ground truth first: this looks marked and is not. Both real findings are open.
                var before = await engine.BpaScanAsync();
                Assert.Contains(before.Violations, v => v.RuleId == "MODEL_SHOULD_HAVE_A_DATE_TABLE");
                Assert.Contains(before.Violations,
                    v => v.RuleId == "DATE/CALENDAR_TABLES_SHOULD_BE_MARKED_AS_A_DATE_TABLE");

                var r = await engine.MarkDateTableAsync(t, "Date", "human");

                // It did change something, so reporting no change would be a lie about a model that was still red.
                Assert.True(r.Changed);
                Assert.Equal("Time", await PropAsync(engine, t, "DataCategory"));
                Assert.Equal("True", await PropAsync(engine, col, "IsKey"));

                // The product-level proof: the REAL rules, not a field read that restates the assignment.
                var after = await engine.BpaScanAsync();
                Assert.DoesNotContain(after.Violations, v => v.RuleId == "MODEL_SHOULD_HAVE_A_DATE_TABLE");
                Assert.DoesNotContain(after.Violations,
                    v => v.RuleId == "DATE/CALENDAR_TABLES_SHOULD_BE_MARKED_AS_A_DATE_TABLE");

                // Undo returns the model to what the property grid left, not to blank, and reapplying corrects it
                // again: the normalization is repeatable, not a one-shot that only works on a fresh table.
                await engine.UndoAsync("human");
                Assert.Equal("time", await PropAsync(engine, t, "DataCategory"));

                var again = await engine.MarkDateTableAsync(t, "Date", "human");
                Assert.True(again.Changed);
                Assert.Equal("Time", await PropAsync(engine, t, "DataCategory"));
                var reapplied = await engine.BpaScanAsync();
                Assert.DoesNotContain(reapplied.Violations, v => v.RuleId == "MODEL_SHOULD_HAVE_A_DATE_TABLE");
            }
        }

        // The same reachable state on the plan door. This is the worse report of the two: with the column already
        // the key, the item said the table was already marked and nothing changed, while the gate stayed red.
        [Fact]
        public async Task Plan_mark_date_table_makes_a_lowercase_time_category_canonical_and_clears_the_real_findings()
        {
            var (engine, _, t, col) = await DateModelAsync();
            using (engine)
            {
                await engine.SetObjectPropertyAsync(t, "DataCategory", "time", "human");
                await engine.SetObjectPropertyAsync(col, "IsKey", "true", "human");

                var before = await engine.BpaScanAsync();
                Assert.Contains(before.Violations, v => v.RuleId == "MODEL_SHOULD_HAVE_A_DATE_TABLE");
                Assert.Contains(before.Violations,
                    v => v.RuleId == "DATE/CALENDAR_TABLES_SHOULD_BE_MARKED_AS_A_DATE_TABLE");

                var report = await ApplyMarkItemAsync(engine, t, "Date");

                var item = Assert.Single(report.Items);
                Assert.Equal("applied", item.Status);
                // "already marked" is reserved for the genuinely satisfied table. Saying it here hid a red gate.
                Assert.DoesNotContain("already", item.Note ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                Assert.Equal("Time", await PropAsync(engine, t, "DataCategory"));
                Assert.Equal("True", await PropAsync(engine, col, "IsKey"));

                var after = await engine.BpaScanAsync();
                Assert.DoesNotContain(after.Violations, v => v.RuleId == "MODEL_SHOULD_HAVE_A_DATE_TABLE");
                Assert.DoesNotContain(after.Violations,
                    v => v.RuleId == "DATE/CALENDAR_TABLES_SHOULD_BE_MARKED_AS_A_DATE_TABLE");
                Assert.True(report.BpaViolationsAfter < report.BpaViolationsBefore,
                    "BPA " + report.BpaViolationsBefore + " -> " + report.BpaViolationsAfter);

                // One undo entry still covers the whole item, and it lands back on the caller's own value.
                await engine.UndoAsync("human");
                Assert.Equal("time", await PropAsync(engine, t, "DataCategory"));
            }
        }

        // An already-marked table is the satisfied case: the item must not invent a change it did not make. The
        // direct op already reports Changed = false there; the plan door must be equally honest in its note.
        [Fact]
        public async Task Plan_mark_date_table_does_not_invent_a_change_on_an_already_marked_table()
        {
            var (engine, _, t, col) = await DateModelAsync();
            using (engine)
            {
                var first = await engine.MarkDateTableAsync(t, "Date", "human");
                Assert.True(first.Changed);

                var again = await engine.MarkDateTableAsync(t, "Date", "human");
                Assert.False(again.Changed);   // the direct op's satisfied case, restated as the shared contract

                var report = await ApplyMarkItemAsync(engine, t, "Date");

                var item = Assert.Single(report.Items);
                Assert.Equal("applied", item.Status);   // the intent holds; it is not a failure
                Assert.Contains("already", item.Note, StringComparison.OrdinalIgnoreCase);
                // ...and the state it claims is genuinely there.
                Assert.Equal("Time", await PropAsync(engine, t, "DataCategory"));
                Assert.Equal("True", await PropAsync(engine, col, "IsKey"));
            }
        }
    }
}
