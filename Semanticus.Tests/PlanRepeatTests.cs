// Repeat cards (Plan at the heart, lane RP; contract section 1.7, Kane 24 Sep: "yes agree").
//
// What these tests hold, one line each:
//   a cadence steps whole periods, and missed periods land on the first date after now
//   a repeat comes due once per period, as one ordinary row with source "repeat"
//   missed periods make one card, not a pile
//   an open card blocks the next; the next comes after that one is done (or set aside) and the next date passes
//   nothing is applied, started or offered beyond an ordinary row
//   free stops the due check, shows the Pro line, refuses add and change, and still deletes
//   a restart does not double a card
//   the stock repeats are offered, never added for the person
//   pause, edit and delete; the open card stays when its repeat is deleted
//   the three controls are person-only: an agent connection is refused each

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.Entitlement;
using StreamJsonRpc;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class PlanRepeatTests
    {
        private const string Challenge = "plan-repeat-test-challenge-0123456789abcdef";

        // A fixed moment and zone, so every date below is the same on every machine. 25 September 2026 is a Friday.
        private static readonly DateTimeOffset Friday = new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);
        private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

        private static DateTimeOffset Day(int y, int m, int d) => new DateTimeOffset(y, m, d, 0, 0, 0, TimeSpan.Zero);

        private static PlanRepeatCadence Weekly(string day, int every = 1) =>
            new PlanRepeatCadence { Kind = PlanCadences.Weekly, Day = day, Every = every };

        private static PlanRepeatCadence Monthly(int date, int every = 1) =>
            new PlanRepeatCadence { Kind = PlanCadences.Monthly, Date = date, Every = every };

        // ---- the cadence -------------------------------------------------------------------------------------

        [Fact]
        public void A_weekly_repeat_first_comes_due_on_its_next_day_and_steps_whole_weeks()
        {
            var monday = Weekly("monday");
            Assert.Equal(Day(2026, 9, 28), PlanRepeats.First(monday, Friday, Utc));
            // Friday itself is not "after today": the first Friday is a week on.
            Assert.Equal(Day(2026, 10, 2), PlanRepeats.First(Weekly("friday"), Friday, Utc));

            var fortnight = Weekly("monday", 2);
            var first = PlanRepeats.First(fortnight, Friday, Utc);
            Assert.Equal(Day(2026, 9, 28), first);
            Assert.Equal(Day(2026, 10, 12), PlanRepeats.NextAfter(fortnight, first, first.AddHours(1), Utc));
        }

        [Fact]
        public void Missed_periods_land_on_the_first_date_after_now_not_the_next_step()
        {
            var monday = Weekly("monday");
            var due = Day(2026, 9, 28);
            // Five weeks and a bit late: the next date is the Monday after now, in phase with the cadence.
            var late = new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);   // a Tuesday
            Assert.Equal(Day(2026, 11, 9), PlanRepeats.NextAfter(monday, due, late, Utc));

            var fortnight = Weekly("monday", 2);
            Assert.Equal(Day(2026, 11, 9), PlanRepeats.NextAfter(fortnight, due, late, Utc));
            Assert.Equal(Day(2026, 11, 23), PlanRepeats.NextAfter(fortnight, due, Day(2026, 11, 9).AddHours(1), Utc));
        }

        [Fact]
        public void A_monthly_repeat_on_a_date_a_month_lacks_comes_due_on_its_last_day()
        {
            var on31 = Monthly(31);
            Assert.Equal(Day(2026, 9, 30), PlanRepeats.First(on31, Friday, Utc));
            Assert.Equal(Day(2026, 10, 31), PlanRepeats.NextAfter(on31, Day(2026, 9, 30), Day(2026, 9, 30).AddHours(1), Utc));
            Assert.Equal(Day(2027, 2, 28), PlanRepeats.NextAfter(on31, Day(2027, 1, 31), Day(2027, 1, 31).AddHours(1), Utc));
            // The date is the 31st again after a short month, not stuck on the 28th.
            Assert.Equal(Day(2027, 3, 31), PlanRepeats.NextAfter(on31, Day(2027, 2, 28), Day(2027, 2, 28).AddHours(1), Utc));

            Assert.Equal(Day(2026, 10, 5), PlanRepeats.First(Monthly(5), Friday, Utc));
            Assert.Equal(Day(2026, 9, 26), PlanRepeats.First(new PlanRepeatCadence { Kind = PlanCadences.Daily }, Friday, Utc));
        }

        // ---- daylight saving (B12) ------------------------------------------------------------------------------

        /// <summary>Sydney moves its clocks forward at 02:00 on Sunday 4 October 2026 (UTC+10 to UTC+11) and back at
        /// 03:00 on Sunday 4 April 2027. A repeat comes due at the start of ITS day in the zone, so across the change the
        /// moment in UTC moves by an hour and the local time stays midnight. The IANA id works on Windows too (.NET 8
        /// maps it through ICU).</summary>
        private static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");

        private static DateTimeOffset At(int y, int m, int d, int h) => new DateTimeOffset(y, m, d, h, 0, 0, TimeSpan.Zero);

        private static void AssertLocalMidnight(DateTimeOffset due, int y, int m, int d)
        {
            var local = TimeZoneInfo.ConvertTime(due, Sydney);
            Assert.Equal(new DateTime(y, m, d), local.Date);
            Assert.Equal(TimeSpan.Zero, local.TimeOfDay);
        }

        [Fact]
        public void A_repeat_across_the_October_clock_change_stays_on_local_midnight()
        {
            // Friday 2 October 2026, 10:00 in Sydney (still UTC+10).
            var friday = At(2026, 10, 2, 0);
            var monday = Weekly("monday");
            var first = PlanRepeats.First(monday, friday, Sydney);
            AssertLocalMidnight(first, 2026, 10, 5);
            Assert.Equal(At(2026, 10, 4, 13), first);                  // UTC+11 after the change, so 13:00 the day before

            // Stepping from a due date before the change to one after it: 28 September was 14:00 UTC the day before.
            var before = PlanRepeats.First(monday, At(2026, 9, 25, 0), Sydney);
            Assert.Equal(At(2026, 9, 27, 14), before);
            Assert.Equal(At(2026, 10, 4, 13), PlanRepeats.NextAfter(monday, before, before.AddHours(1), Sydney));

            // A daily repeat over the short day itself: the 4th starts at +10, the 5th at +11, 23 hours apart.
            var daily = new PlanRepeatCadence { Kind = PlanCadences.Daily };
            var sat = PlanRepeats.First(daily, At(2026, 10, 3, 1), Sydney);   // Saturday 3 October, 11:00 local
            AssertLocalMidnight(sat, 2026, 10, 4);
            var sun = PlanRepeats.NextAfter(daily, sat, sat.AddMinutes(1), Sydney);
            AssertLocalMidnight(sun, 2026, 10, 5);
            Assert.Equal(TimeSpan.FromHours(23), sun - sat);

            // And back in April: the long day is 25 hours, and the monthly date still lands on local midnight.
            var apr = PlanRepeats.NextAfter(daily, At(2027, 4, 3, 13), At(2027, 4, 3, 14), Sydney);
            AssertLocalMidnight(apr, 2027, 4, 5);
            Assert.Equal(TimeSpan.FromHours(25), apr - At(2027, 4, 3, 13));
            AssertLocalMidnight(PlanRepeats.NextAfter(Monthly(5), At(2027, 3, 4, 13), At(2027, 3, 4, 14), Sydney), 2027, 4, 5);
        }

        [Fact]
        public void A_cadence_is_checked_and_filled_and_said_in_plain_words()
        {
            var today = Friday.UtcDateTime.Date;
            Assert.Equal("friday", PlanRepeats.Normalise(new PlanRepeatCadence { Kind = "Weekly" }, today).Day);
            Assert.Equal(25, PlanRepeats.Normalise(new PlanRepeatCadence { Kind = "monthly" }, today).Date);

            Assert.Throws<ArgumentException>(() => PlanRepeats.Normalise(null, today));
            Assert.Throws<ArgumentException>(() => PlanRepeats.Normalise(new PlanRepeatCadence { Kind = "hourly" }, today));
            Assert.Throws<ArgumentException>(() => PlanRepeats.Normalise(new PlanRepeatCadence { Kind = "daily", Every = 0 }, today));
            Assert.Throws<ArgumentException>(() => PlanRepeats.Normalise(new PlanRepeatCadence { Kind = "daily", Every = 13 }, today));
            Assert.Throws<ArgumentException>(() => PlanRepeats.Normalise(Weekly("someday"), today));
            Assert.Throws<ArgumentException>(() => PlanRepeats.Normalise(Monthly(32), today));

            Assert.Equal("Every day", PlanRepeats.CadenceText(new PlanRepeatCadence { Kind = "daily" }));
            Assert.Equal("Every 3 days", PlanRepeats.CadenceText(new PlanRepeatCadence { Kind = "daily", Every = 3 }));
            Assert.Equal("Every week, on Monday", PlanRepeats.CadenceText(Weekly("monday")));
            Assert.Equal("Every 2 weeks, on Monday", PlanRepeats.CadenceText(Weekly("monday", 2)));
            Assert.Equal("Every month, on the 1st", PlanRepeats.CadenceText(Monthly(1)));
            Assert.Equal("Every 2 months, on the 22nd", PlanRepeats.CadenceText(Monthly(22, 2)));
            Assert.Equal("Every month, on the 31st or the last day", PlanRepeats.CadenceText(Monthly(31)));
        }

        // ---- the engine --------------------------------------------------------------------------------------

        /// <summary>The gappy model the plan tests use, saved so the repeats are kept in the Plan's own file.</summary>
        private static async Task<DecisionFixture> GappyAsync(string file, bool pro = true)
        {
            var f = new DecisionFixture(pro ? TestEntitlements.Pro : TestEntitlements.Free);
            await f.Engine.CreateModelAsync("Contoso", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await f.Engine.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await f.Engine.CreateTableAsync("Customer", "human");
            await f.Engine.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await f.Engine.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            if (file != null) await f.SaveAsAsync(file);
            f.UseNull();
            Clock(f.Engine, Friday);
            return f;
        }

        private static void Clock(LocalEngine engine, DateTimeOffset now)
        {
            engine.PlanRepeatClock = () => now;
            engine.PlanRepeatZone = Utc;
        }

        /// <summary>The same workspace and file opened by a new engine, as after a restart.</summary>
        private static async Task<LocalEngine> RestartAsync(DecisionFixture f, string file, bool pro, DateTimeOffset now)
        {
            var sessions = new SessionManager();
            var again = new LocalEngine(sessions, pro ? TestEntitlements.Pro : TestEntitlements.Free, f.Workspace);
            again.UseDecisionsForTest(new DecisionService(NullDecisionOracle.Instance));
            Clock(again, now);
            await again.OpenAsync(Path.Combine(f.Workspace, file));
            return again;
        }

        private static ModelPlanItem[] Cards(ModelPlanView plan, string repeatId = null) =>
            plan.Items.Where(i => i.Source == PlanSources.Repeat).ToArray();

        private static async Task<string> AddWeeklyAsync(IEngine engine, string words = "Check the Sales descriptions",
                                                          string doer = PlanDoers.Assistant) =>
            (await engine.AddPlanRepeatAsync(new PlanRepeatRequest { Words = words, Doer = doer, Cadence = Weekly("monday") })).ItemId;

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_repeat_comes_due_once_per_period_as_one_ordinary_row()
        {
            using var f = await GappyAsync("RpOnce.bim");
            var added = await f.Engine.AddPlanRepeatAsync(new PlanRepeatRequest
            {
                Words = "Check the Sales descriptions", Doer = PlanDoers.Assistant, Cadence = Weekly("monday"),
            });
            Assert.Matches("^rp-[0-9a-f]{8}$", added.ItemId);
            Assert.Empty(Cards(added.Plan));   // not due yet
            var saved = Assert.Single(added.Plan.Repeats);
            Assert.Equal(Day(2026, 9, 28), saved.NextDueUtc);
            Assert.Equal("Every week, on Monday", saved.CadenceText);
            Assert.Null(saved.OpenItemId);

            Clock(f.Engine, Day(2026, 9, 28).AddHours(9));
            var plan = await f.Engine.GetModelPlanAsync();
            var card = Assert.Single(Cards(plan));
            Assert.Matches("^pt-[0-9a-f]{8}$", card.Id);
            Assert.Equal("Check the Sales descriptions", card.Title);
            Assert.Equal(PlanDoers.Assistant, card.Doer);
            Assert.Equal(PlanStateWords.Ready, card.StateWord);
            Assert.Equal(PlanDoneWhen.AssistantSays, card.DoneWhen);
            var repeat = Assert.Single(plan.Repeats);
            Assert.Equal(card.Id, repeat.OpenItemId);
            Assert.Equal(Day(2026, 10, 5), repeat.NextDueUtc);

            // A second read in the same period adds nothing.
            Assert.Single(Cards(await f.Engine.GetModelPlanAsync()));
        }

        /// <summary>B12: the Plan's timing on a second fixture, a model with other tables and no measures, read in
        /// Sydney across the October change. The card is not there a minute before local midnight on Monday 5 October
        /// and is there at it; the next due is the following local midnight, an hour earlier in UTC than the first
        /// would have been before the change.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task On_a_second_model_a_repeat_comes_due_at_local_midnight_across_daylight_saving()
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            using (f)
            {
                await f.Engine.CreateModelAsync("Contoso Returns", 1604);
                await f.Engine.CreateTableAsync("Returns", "human");
                await f.Engine.CreateColumnAsync("table:Returns", "Qty", "Int64", null, "human");
                await f.Engine.CreateColumnAsync("table:Returns", "Return Date", "DateTime", null, "human");
                await f.SaveAsAsync("RpSydney.bim");
                f.UseNull();
                void SydneyClock(DateTimeOffset now) { f.Engine.PlanRepeatClock = () => now; f.Engine.PlanRepeatZone = Sydney; }

                SydneyClock(At(2026, 10, 2, 0));                               // Friday 2 October, 10:00 in Sydney
                var added = await f.Engine.AddPlanRepeatAsync(new PlanRepeatRequest
                {
                    Words = "Check the Returns counts", Doer = PlanDoers.Assistant, Cadence = Weekly("monday"),
                });
                Assert.Equal(At(2026, 10, 4, 13), Assert.Single(added.Plan.Repeats).NextDueUtc);

                SydneyClock(At(2026, 10, 4, 12).AddMinutes(59));               // 23:59 on Sunday, local
                Assert.Empty(Cards(await f.Engine.GetModelPlanAsync()));

                SydneyClock(At(2026, 10, 4, 13));                              // 00:00 on Monday, local
                var plan = await f.Engine.GetModelPlanAsync();
                Assert.Equal("Check the Returns counts", Assert.Single(Cards(plan)).Title);
                Assert.Equal(At(2026, 10, 11, 13), Assert.Single(plan.Repeats).NextDueUtc);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Missed_periods_make_one_card_not_a_pile()
        {
            using var f = await GappyAsync("RpMissed.bim");
            await AddWeeklyAsync(f.Engine);

            var late = new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);   // five weeks on
            Clock(f.Engine, late);
            var plan = await f.Engine.GetModelPlanAsync();
            Assert.Single(Cards(plan));
            Assert.Equal(Day(2026, 11, 9), Assert.Single(plan.Repeats).NextDueUtc);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_open_card_blocks_the_next_which_comes_only_after_it_is_done_and_the_date_passes()
        {
            using var f = await GappyAsync("RpBlock.bim");
            await AddWeeklyAsync(f.Engine, doer: PlanDoers.You);

            Clock(f.Engine, Day(2026, 9, 28).AddHours(9));
            var first = Assert.Single(Cards(await f.Engine.GetModelPlanAsync()));

            // The next Monday passes with the first card still open: nothing is added, and the date moves on.
            Clock(f.Engine, Day(2026, 10, 5).AddHours(9));
            var blocked = await f.Engine.GetModelPlanAsync();
            Assert.Single(Cards(blocked));
            Assert.Equal(Day(2026, 10, 12), Assert.Single(blocked.Repeats).NextDueUtc);

            // Done, but the next date has not passed: still no new card.
            await f.Engine.CompletePlanTaskAsync(first.Id, null);
            var done = await f.Engine.GetModelPlanAsync();
            Assert.Equal(PlanStateWords.Done, Assert.Single(Cards(done)).StateWord);
            Assert.Null(Assert.Single(done.Repeats).OpenItemId);

            // The date passes: one new card, and the first stays done.
            Clock(f.Engine, Day(2026, 10, 12).AddHours(9));
            var next = await f.Engine.GetModelPlanAsync();
            var cards = Cards(next);
            Assert.Equal(2, cards.Length);
            Assert.Equal(PlanStateWords.Done, cards.Single(c => c.Id == first.Id).StateWord);
            Assert.Equal(PlanStateWords.NeedsYou, cards.Single(c => c.Id != first.Id).StateWord);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_card_set_aside_no_longer_blocks_the_next()
        {
            using var f = await GappyAsync("RpAside.bim");
            await AddWeeklyAsync(f.Engine);
            Clock(f.Engine, Day(2026, 9, 28).AddHours(9));
            var first = Assert.Single(Cards(await f.Engine.GetModelPlanAsync()));

            await f.Engine.SkipModelPlanItemAsync(first.Id, "Not this week", "human");
            Clock(f.Engine, Day(2026, 10, 5).AddHours(9));
            var plan = await f.Engine.GetModelPlanAsync();
            Assert.Equal(2, Cards(plan).Length);
            Assert.Equal(PlanStateWords.SetAside, Cards(plan).Single(c => c.Id == first.Id).StateWord);
        }

        [Fact]
        public async Task Nothing_is_applied_started_or_offered_beyond_an_ordinary_row()
        {
            using var f = await GappyAsync("RpNothing.bim");
            await f.Engine.SaveWorkflowAsync("monthly-tidy", TidyMd, "human");
            var wf = await f.Engine.AddPlanRepeatAsync(new PlanRepeatRequest { Workflow = "monthly-tidy", Cadence = Weekly("monday") });
            await AddWeeklyAsync(f.Engine);

            var before = await f.Engine.GetModelPlanAsync();
            var revision = f.Sessions.Current.Revision;

            Clock(f.Engine, Day(2026, 9, 28).AddHours(9));
            var plan = await f.Engine.GetModelPlanAsync();
            var cards = Cards(plan);
            Assert.Equal(2, cards.Length);

            // The model did not move, Run the plan's set did not change, and no run was started.
            Assert.Equal(revision, f.Sessions.Current.Revision);
            Assert.Equal(revision, plan.Revision);
            Assert.Equal(before.ReadyDigest, plan.ReadyDigest);
            Assert.DoesNotContain(plan.Items, i => i.Id != null && i.Id.StartsWith("run/", StringComparison.Ordinal));
            Assert.All(cards, c => Assert.NotEqual(PlanDoers.Semanticus, c.Doer));

            // The workflow card waits for a person to press Start, on the Workflows page.
            var workflowCard = cards.Single(c => c.Id == Assert.Single(plan.Repeats, r => r.Id == wf.ItemId).OpenItemId);
            Assert.Equal(PlanDoers.You, workflowCard.Doer);
            Assert.Equal(PlanStateWords.NeedsYou, workflowCard.StateWord);
            Assert.Equal(PlanRepeats.StartItNote, workflowCard.StateNote);
            Assert.Equal("workflows", workflowCard.Jump?.Tool);

            // The assistant's card is offered like any task for it: once, in the ordinary list.
            var assistantCard = cards.Single(c => c.Doer == PlanDoers.Assistant);
            var offered = await f.Engine.ListAssistantRequestsAsync();
            Assert.Single(offered.Requests.Where(r => r.RequestId == assistantCard.Id));
        }

        private const string TidyMd = @"---
name: monthly-tidy
title: Monthly tidy
version: 1
strictness: hard
---

## Step 1: Verify

```yaml gate
verify:
  - kind: bpa_clean
    scope: model
```
";

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Free_stops_the_due_check_shows_the_Pro_line_refuses_add_and_change_and_still_deletes()
        {
            string id, openCard;
            var f = await GappyAsync("RpFree.bim");
            using (f)
            {
                id = await AddWeeklyAsync(f.Engine);
                Clock(f.Engine, Day(2026, 9, 28).AddHours(9));
                openCard = Assert.Single(Cards(await f.Engine.GetModelPlanAsync())).Id;

                // The same model, on free, well past the next date.
                using var free = await RestartAsync(f, "RpFree.bim", pro: false, now: Day(2026, 10, 20).AddHours(9));
                var plan = await free.GetModelPlanAsync();
                Assert.Equal(PlanRepeats.ProLine, plan.RepeatsNote);
                Assert.Empty(plan.RepeatStarters);
                Assert.Equal(openCard, Assert.Single(Cards(plan)).Id);   // the open card stays an ordinary row
                Assert.Equal(Day(2026, 10, 5), Assert.Single(plan.Repeats).NextDueUtc);   // not moved: nothing came due

                await Assert.ThrowsAsync<EntitlementException>(() => free.AddPlanRepeatAsync(
                    new PlanRepeatRequest { Words = "Another", Cadence = Weekly("monday") }));
                await Assert.ThrowsAsync<EntitlementException>(() => free.SetPlanRepeatAsync(id, new PlanRepeatRequest { Held = true }));

                var deleted = await free.DeletePlanRepeatAsync(id);
                Assert.Empty(deleted.Repeats);
                Assert.Null(deleted.RepeatsNote);
                Assert.Equal(openCard, Assert.Single(Cards(deleted)).Id);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_restart_does_not_double_a_card()
        {
            using var f = await GappyAsync("RpRestart.bim");
            await AddWeeklyAsync(f.Engine);
            var due = Day(2026, 9, 28).AddHours(9);
            Clock(f.Engine, due);
            var card = Assert.Single(Cards(await f.Engine.GetModelPlanAsync()));

            using var again = await RestartAsync(f, "RpRestart.bim", pro: true, now: due);
            var plan = await again.GetModelPlanAsync();
            Assert.Equal(card.Id, Assert.Single(Cards(plan)).Id);
            Assert.Equal(Day(2026, 10, 5), Assert.Single(plan.Repeats).NextDueUtc);
        }

        [Fact]
        public async Task The_stock_repeats_are_offered_and_never_added_for_the_person()
        {
            using var f = await GappyAsync("RpStock.bim");
            var plan = await f.Engine.GetModelPlanAsync();
            Assert.Empty(plan.Repeats);
            Assert.Equal(new[] { "Update model notes", "Review AI instructions" }, plan.RepeatStarters.Select(s => s.Words).ToArray());
            Assert.Equal(new[] { "Every 2 weeks, on Friday", "Every month, on the 25th" },
                         plan.RepeatStarters.Select(s => s.CadenceText).ToArray());

            // Even long after, nothing comes due that the person did not set up.
            Clock(f.Engine, Friday.AddDays(90));
            Assert.Empty(Cards(await f.Engine.GetModelPlanAsync()));

            Clock(f.Engine, Friday);
            var notes = await f.Engine.AddPlanRepeatAsync(new PlanRepeatRequest { Stock = PlanRepeats.StockModelNotes });
            var saved = Assert.Single(notes.Plan.Repeats);
            Assert.Equal("Update model notes", saved.Words);
            Assert.Equal(PlanDoers.Assistant, saved.Doer);
            Assert.Equal(PlanRepeats.StockModelNotes, saved.Stock);
            Assert.Equal(Day(2026, 10, 9), saved.NextDueUtc);   // two weeks on, a Friday
            Assert.Equal(new[] { "Review AI instructions" }, notes.Plan.RepeatStarters.Select(s => s.Words).ToArray());

            Clock(f.Engine, Day(2026, 10, 9).AddHours(9));
            var card = Assert.Single(Cards(await f.Engine.GetModelPlanAsync()));
            Assert.Equal("knowledge", card.Jump?.Tool);

            await Assert.ThrowsAsync<ArgumentException>(() => f.Engine.AddPlanRepeatAsync(new PlanRepeatRequest { Stock = "nightly-rebuild" }));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Pause_holds_it_back_an_edit_changes_it_and_a_delete_keeps_the_open_card()
        {
            using var f = await GappyAsync("RpEdit.bim");
            var id = await AddWeeklyAsync(f.Engine);

            var paused = await f.Engine.SetPlanRepeatAsync(id, new PlanRepeatRequest { Held = true });
            Assert.True(Assert.Single(paused.Repeats).Held);
            Clock(f.Engine, Day(2026, 9, 28).AddHours(9));
            Assert.Empty(Cards(await f.Engine.GetModelPlanAsync()));

            // Resumed after its date: it comes due at once, one card.
            var resumed = await f.Engine.SetPlanRepeatAsync(id, new PlanRepeatRequest { Held = false });
            var card = Assert.Single(Cards(resumed));

            var edited = await f.Engine.SetPlanRepeatAsync(id, new PlanRepeatRequest
            {
                Words = "Check every description", Doer = PlanDoers.You, Cadence = Monthly(1),
            });
            var repeat = Assert.Single(edited.Repeats);
            Assert.Equal("Check every description", repeat.Words);
            Assert.Equal(PlanDoers.You, repeat.Doer);
            Assert.Equal(Day(2026, 10, 1), repeat.NextDueUtc);
            // The card already out is the person's to finish; an edit changes the cards to come.
            Assert.Equal("Check the Sales descriptions", Assert.Single(Cards(edited)).Title);

            var deleted = await f.Engine.DeletePlanRepeatAsync(id);
            Assert.Empty(deleted.Repeats);
            Assert.Equal(card.Id, Assert.Single(Cards(deleted)).Id);

            await Assert.ThrowsAsync<ArgumentException>(() => f.Engine.DeletePlanRepeatAsync(id));
            await Assert.ThrowsAsync<ArgumentException>(() => f.Engine.SetPlanRepeatAsync("rp-00000000", new PlanRepeatRequest { Held = true }));
        }

        /// <summary>The lane's finish line. A fix repeat mints no row of its own: its card IS the found row, marked,
        /// so Run the plan applies it unchanged and nothing counts twice.</summary>
        [Fact]
        public async Task A_weekly_repeat_of_a_Semanticus_fix_marks_one_Ready_row_and_only_Run_the_plan_changes_the_model()
        {
            using var f = await GappyAsync("RpFix.bim");
            var start = await f.Engine.GetModelPlanAsync();
            var fix = start.Items.First(i => i.Doer == PlanDoers.Semanticus && i.StateWord == PlanStateWords.Ready
                                             && i.Id.StartsWith("safefix/", StringComparison.Ordinal));
            var added = await f.Engine.AddPlanRepeatAsync(new PlanRepeatRequest { Fix = fix.Id, Cadence = Weekly("monday") });
            var saved = Assert.Single(added.Plan.Repeats);
            Assert.Equal(fix.Id, saved.Fix);
            Assert.Equal(PlanDoers.Semanticus, saved.Doer);
            Assert.Equal(fix.Title, saved.Words);
            Assert.Null(saved.OpenItemId);
            Assert.NotEqual(PlanRepeats.FixMarkNote, Row(added.Plan, fix.Id).StateNote);

            // Past its date: the found row is the one Ready card, marked; no second row, and the model did not move.
            var revision = f.Sessions.Current.Revision;
            Clock(f.Engine, Day(2026, 9, 28).AddHours(9));
            var due = await f.Engine.GetModelPlanAsync();
            var card = Row(due, fix.Id);
            Assert.Equal(PlanStateWords.Ready, card.StateWord);
            Assert.Equal(PlanRepeats.FixMarkNote, card.StateNote);
            Assert.Contains(card.Why, w => w.StartsWith("A repeat you set up brought this back: every week, on Monday", StringComparison.Ordinal));
            Assert.Empty(Cards(due));
            Assert.Equal(fix.Id, Assert.Single(due.Repeats).OpenItemId);
            Assert.Equal(revision, f.Sessions.Current.Revision);
            Assert.Equal(start.ReadyDigest.Length, due.ReadyDigest.Length);

            // Run the plan is what changes the model, and the card closes.
            var run = await f.Engine.ApplyPlanItemsAsync(null, "human", due.ReadyDigest);
            Assert.True(run.Performed, run.Sentence);
            Assert.NotEqual(revision, f.Sessions.Current.Revision);
            var after = await f.Engine.GetModelPlanAsync();
            Assert.DoesNotContain(after.Items, i => i.Id == fix.Id && PlanStateWords.IsOpen(i.StateWord));
            Assert.Null(Assert.Single(after.Repeats).OpenItemId);

            // The gap comes back before the next date: the row is back, and it is not marked.
            await f.Engine.UndoToEntryAsync(run.EntryId, "human");   // the receipt's Undo: the whole press, every wave
            var back = await f.Engine.GetModelPlanAsync();
            Assert.Equal(PlanStateWords.Ready, Row(back, fix.Id).StateWord);
            Assert.NotEqual(PlanRepeats.FixMarkNote, Row(back, fix.Id).StateNote);
            Assert.Null(Assert.Single(back.Repeats).OpenItemId);

            // The next date passes: marked again.
            Clock(f.Engine, Day(2026, 10, 5).AddHours(9));
            var next = await f.Engine.GetModelPlanAsync();
            Assert.Equal(PlanRepeats.FixMarkNote, Row(next, fix.Id).StateNote);
            Assert.Equal(fix.Id, Assert.Single(next.Repeats).OpenItemId);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_fix_repeat_names_a_row_Semanticus_does_that_is_on_the_plan_now()
        {
            using var f = await GappyAsync("RpFixRefuse.bim");
            await Assert.ThrowsAsync<ArgumentException>(() => f.Engine.AddPlanRepeatAsync(
                new PlanRepeatRequest { Fix = "safefix/NoSuchRow", Cadence = Weekly("monday") }));
            var fix = (await f.Engine.GetModelPlanAsync()).Items.First(i => i.Doer == PlanDoers.Semanticus
                                                                             && i.Id.StartsWith("safefix/", StringComparison.Ordinal));
            await Assert.ThrowsAsync<ArgumentException>(() => f.Engine.AddPlanRepeatAsync(
                new PlanRepeatRequest { Fix = fix.Id, Doer = PlanDoers.You, Cadence = Weekly("monday") }));
            Assert.Empty((await f.Engine.GetModelPlanAsync()).Repeats);
        }

        private static ModelPlanItem Row(ModelPlanView plan, string id) => Assert.Single(plan.Items.Where(i => i.Id == id));

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_repeat_that_cannot_be_kept_or_done_is_refused_with_a_sentence()
        {
            using var f = await GappyAsync("RpRefuse.bim");
            Task Add(PlanRepeatRequest r) => f.Engine.AddPlanRepeatAsync(r);
            await Assert.ThrowsAsync<ArgumentException>(() => Add(new PlanRepeatRequest { Cadence = Weekly("monday") }));
            await Assert.ThrowsAsync<ArgumentException>(() => Add(new PlanRepeatRequest { Words = new string('x', 281), Cadence = Weekly("monday") }));
            await Assert.ThrowsAsync<ArgumentException>(() => Add(new PlanRepeatRequest { Words = "No cadence" }));
            await Assert.ThrowsAsync<ArgumentException>(() => Add(new PlanRepeatRequest { Words = "By itself", Doer = PlanDoers.Semanticus, Cadence = Weekly("monday") }));
            await Assert.ThrowsAsync<ArgumentException>(() => Add(new PlanRepeatRequest { Workflow = "no-such-workflow", Cadence = Weekly("monday") }));
            Assert.Empty((await f.Engine.GetModelPlanAsync()).Repeats);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_model_with_nowhere_to_keep_repeats_keeps_them_for_the_session()
        {
            using var f = await GappyAsync(file: null);
            var added = await f.Engine.AddPlanRepeatAsync(new PlanRepeatRequest { Words = "Check the Sales descriptions", Cadence = Weekly("monday") });
            Assert.Contains(PlanTasks.NotKeptSentence, added.Sentence, StringComparison.Ordinal);
            Clock(f.Engine, Day(2026, 9, 28).AddHours(9));
            Assert.Single(Cards(await f.Engine.GetModelPlanAsync()));
            Assert.Single(Cards(await f.Engine.GetModelPlanAsync()));
        }

        // ---- both doors (Kane, 10 Oct 2026: the person's word in chat is enough) --------------------------------------

        private static readonly string[] Controls = { "addPlanRepeat", "setPlanRepeat", "deletePlanRepeat" };

        [Fact]
        public void The_repeat_controls_are_on_the_pipe_target_and_have_MCP_tools()
        {
            foreach (var name in Controls)
            {
                Assert.NotNull(typeof(EngineRpcTarget).GetMethod(name));
                Assert.Null(typeof(HumanGovernanceRpcTarget).GetMethod(name));
            }
            var toolNames = typeof(McpTools).Assembly.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
                .Where(n => n != null).ToArray();
            foreach (var tool in new[] { "add_plan_repeat", "set_plan_repeat", "delete_plan_repeat" })
                Assert.Contains(tool, toolNames);

            // Add and change are Pro in the Workflows group; delete is free.
            Assert.True(FeatureMap.TryEngineMethod(nameof(IEngine.AddPlanRepeatAsync), out var add, out _));
            Assert.True(FeatureMap.TryEngineMethod(nameof(IEngine.SetPlanRepeatAsync), out var set, out _));
            Assert.Equal(ProFeature.Workflows, add);
            Assert.Equal(ProFeature.Workflows, set);
            Assert.Contains(nameof(IEngine.DeletePlanRepeatAsync), FeatureMap.FreeEngineMethods);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_repeat_tools_do_what_the_buttons_do_through_the_gated_MCP_door()
        {
            using var f = await GappyAsync(file: null);
            var gated = AssistantEngine.ForAssistant(f.Engine);
            var added = await McpTools.AddPlanRepeat(gated, new PlanRepeatRequest { Words = "Check the Sales descriptions", Cadence = Weekly("monday") });
            Assert.Matches("^rp-[0-9a-f]{8}$", added.ItemId);
            var held = await McpTools.SetPlanRepeat(gated, added.ItemId, new PlanRepeatRequest { Held = true });
            Assert.True(Assert.Single(held.Repeats).Held);
            var gone = await McpTools.DeletePlanRepeat(gated, added.ItemId);
            Assert.Empty(gone.Repeats);
        }

        [Fact]
        public async Task An_agent_connection_adds_changes_and_deletes_a_repeat()
        {
            var f = await GappyAsync("RpPipe.bim");
            var pipeName = TestPipe.Mint("semanticus-rp-");
            using var server = new RpcServer(f.Sessions, f.Engine, pipeName, Challenge);
            using var stop = new CancellationTokenSource();
            var serving = server.RunAsync(stop.Token);
            RpcClient agent = null;
            RemoteEngine remote = null;
            try
            {
                agent = await RpcClient.ConnectAsync(pipeName, "agent");
                remote = await RemoteEngine.ConnectAsync(pipeName);

                var request = new PlanRepeatRequest { Words = "From Claude", Cadence = Weekly("monday") };
                var added = await agent.InvokeAsync<ModelPlanActionResult>("addPlanRepeat", request);
                Assert.Matches("^rp-[0-9a-f]{8}$", added.ItemId);
                var held = await remote.SetPlanRepeatAsync(added.ItemId, new PlanRepeatRequest { Held = true });
                Assert.True(Assert.Single(held.Repeats).Held);
                var resumed = await agent.InvokeAsync<ModelPlanView>("setPlanRepeat", added.ItemId, new PlanRepeatRequest { Held = false });
                Assert.False(Assert.Single(resumed.Repeats).Held);
                var gone = await remote.DeletePlanRepeatAsync(added.ItemId);
                Assert.Empty(gone.Repeats);
            }
            finally
            {
                remote?.Dispose();
                agent?.Dispose();
                stop.Cancel();
                try { await serving; } catch (OperationCanceledException) { }
                f.Dispose();
            }
        }

        /// <summary>A bare pipe client that speaks the handshake as a person or an agent (as PlanPersonControlsTests does).</summary>
        private sealed class RpcClient : IDisposable
        {
            private readonly NamedPipeClientStream _pipe;
            private readonly JsonRpc _rpc;

            private RpcClient(NamedPipeClientStream pipe, JsonRpc rpc) { _pipe = pipe; _rpc = rpc; }

            internal static async Task<RpcClient> ConnectAsync(string pipeName, string role, string challenge = null)
            {
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000);
                await RpcHandshake.WriteAsync(pipe, role == "human" ? RpcConnectionRole.Human : RpcConnectionRole.Agent, challenge);
                await RpcHandshake.ReadAcceptedAsync(pipe);
                var rpc = new JsonRpc(RpcServer.CreateHandler(pipe));
                rpc.StartListening();
                return new RpcClient(pipe, rpc);
            }

            internal Task<T> InvokeAsync<T>(string method, params object[] args) => _rpc.InvokeAsync<T>(method, args);

            public void Dispose()
            {
                try { _rpc.Dispose(); } catch { }
                try { _pipe.Dispose(); } catch { }
            }
        }
    }
}
