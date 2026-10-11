// The start plan's build transaction and its preview (design-spec-first-start-v2.md section 2, items 3 and 6).
// The pure parts (checks, digest, grades, seams) are StartPlanBuild.cs; this half is the engine's because the audit
// record, the activity nudge and the session are the engine's.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine.StartPlan;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        /// <summary>Test hooks for the build. Null in production.</summary>
        internal StartPlanBuildSeams StartPlanBuildSeamsForTest;

        /// <summary>Build the preview on a SCRATCH session: the plan is built into it exactly as the real build would build
        /// it into the person's model, and the projection and grades are read straight after, inside the same batch. The
        /// scratch is never published and carries a private bus (lane sf-core), so nothing here reaches either door.
        /// Returns what <see cref="StartPlanDoc.Preview"/> stores and the build compares against.</summary>
        internal async Task<StartPreview> PreviewStartPlanAsync(Session scratch, StartPlanDoc doc)
        {
            var seams = StartPlanBuildSeamsForTest;
            string projection = null;
            (int? AiReadiness, string AiGrade, string BestPractice, int? BestPracticeOpen) grades = default;
            await scratch.MutateAsync("semanticus", "preview start plan", m =>
            {
                (seams?.Materialize ?? StartPlanBuild.Materialize)(this, m, scratch, doc);
                projection = StartPlanProjection.Of(scratch);
                seams?.Tap?.Invoke("preview", projection);
            }).ConfigureAwait(false);
            // GRADED AFTER THE BATCH, not inside it: the scorecards read the model as committed (a rule's answer can differ while the batch is still open: the end-to-end
            // run on J8 showed F inside the batch and D once it had committed), so the letter a person is shown is the one the built model will have.
            grades = await scratch.ReadAsync(m => StartPlanBuild.GradesOf(m)).ConfigureAwait(false);
            return new StartPreview
            {
                AiReadiness = grades.AiReadiness, AiReadinessGrade = grades.AiGrade, BestPractice = grades.BestPractice, BestPracticeOpen = grades.BestPracticeOpen,
                ProjectionDigest = StartPlanProjection.Digest(projection), PlanDigest = StartPlanViewBuilder.Digest(doc),
            };
        }

        // ------------------------------------------------------------------ the preview, made in the background

        /// <summary>The last reason a background preview could not be made (a test reads it; the person's page just shows "still working it out").</summary>
        internal string StartPreviewLastErrorForTest;
        private CancellationTokenSource _startPreviewCts;
        private Task _startPreviewTask = Task.CompletedTask;

        /// <summary>Wait until the preview has been made (or found current). Tests only.</summary>
        internal async Task StartPreviewSettledForTest()
        {
            // A run that finishes may have scheduled the next one, so wait until the task waited for is still the latest.
            for (var i = 0; i < 20; i++)
            {
                Task t;
                lock (_startPlanLock) t = _startPreviewTask;
                try { await t.ConfigureAwait(false); } catch { }
                lock (_startPlanLock) if (ReferenceEquals(t, _startPreviewTask)) return;
            }
        }

        /// <summary>Make the plan's preview in the background: the plan built into its own clone of the person's model, the projection and the grades read in
        /// the same batch. Called whenever the plan moves (the start, an edit, words, the sample, the end of a decide). It waits for a decide in flight to finish
        /// first (the decide's own end calls it again), so the preview is made once, on the plan as it settled, and it is stored only if the plan has not moved
        /// since (the digest is the same). A plan that is parked, built, or whose preview is already current does nothing. The build refuses without a current one.</summary>
        /// <summary>Make the preview only when none is being made now (a read of the plan calls this: a plan met after a restart has no preview yet).</summary>
        internal void ScheduleStartPreviewIfIdle()
        {
            lock (_startPlanLock) if (!_startPreviewTask.IsCompleted || _startPlanDisposed) return;
            ScheduleStartPreview();
        }

        internal void ScheduleStartPreview()
        {
            if (_startPlanDisposed) return;
            var cts = new CancellationTokenSource();
            lock (_startPlanLock)
            {
                try { _startPreviewCts?.Cancel(); } catch { }
                _startPreviewCts = cts;
                var before = _startPreviewTask;
                _startPreviewTask = Task.Run(async () =>
                {
                    try { await before.ConfigureAwait(false); } catch { }
                    try { await StartDecideSettledForTest().ConfigureAwait(false); } catch { }
                    try { await RunStartPreviewAsync(cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { StartPreviewLastErrorForTest = ex.ToString(); try { Console.Error.WriteLine("[start-plan] the preview could not be made: " + ex.Message); } catch { } }
                });
            }
        }

        private async Task RunStartPreviewAsync(CancellationToken ct)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                StartPlanDoc doc;
                lock (_startPlanLock) doc = StartPlanStore.Copy(_startPlan);
                if (doc == null || doc.Parked || await StartPlanIsBuiltAsync(doc).ConfigureAwait(false)) return;   // built: the model holds the plan; an UNDONE build is a plan again
                if (string.Equals(doc.Decide?.State, StartDecideState.Deciding, StringComparison.Ordinal)) return;   // the decide's own end schedules this again
                var digest = StartPlanViewBuilder.Digest(doc);
                if (doc.Preview != null && string.Equals(doc.Preview.PlanDigest, digest, StringComparison.Ordinal) && !string.IsNullOrEmpty(doc.Preview.ProjectionDigest)) return;
                var baseline = _sessions.Current;
                if (baseline == null || await StartPlanParkedReasonAsync(doc).ConfigureAwait(false) != null) return;

                StartPreview preview;
                var epoch = Interlocked.Read(ref _startPlanEpoch);
                var scratch = await StartPlanScratch.CloneAsync(baseline, StartPlanStore.PreviewDirFor(doc.Id)).ConfigureAwait(false);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    preview = await PreviewStartPlanAsync(scratch.Session, doc).ConfigureAwait(false);
                }
                finally { try { await scratch.DisposeAsync().ConfigureAwait(false); } catch { } }

                StartPlanView view = null;
                lock (_startPlanLock)
                {
                    var live = _startPlan;
                    if (live != null && string.Equals(live.Id, doc.Id, StringComparison.Ordinal)
                        && Interlocked.Read(ref _startPlanEpoch) == epoch
                        && string.Equals(StartPlanViewBuilder.Digest(live), digest, StringComparison.Ordinal))
                    {
                        // Kept in memory and saved with the plan's next write; it is not written on its own. A preview is made again from the plan, so a restart
                        // loses nothing (a read of the plan makes it again), and no background task writes the plan's file after its owner has gone.
                        live.Preview = preview;
                        view = StartPlanViewBuilder.Build(StartPlanStore.Copy(live), null);
                    }
                }
                if (view != null) { NotifyStartPlanChanged(view, null); return; }
                // The plan moved while the preview was made: look again (the move scheduled its own run too, so this is only a faster second look).
            }
        }

        /// <summary>build_start_plan: build the approved plan into the open model, on either door. The approval itself is the
        /// person's, made on the person-only door (S1); the assistant may run THIS, never make that.
        ///
        /// It holds the start-plan gate for the whole build, so no second start, discard or edit lands in the middle, and it
        /// stops the decide loop first: a loop still rewriting the document while it is built from would build something
        /// nobody approved. When the build lands the plan is finished: the document is deleted and the scratch dropped, and the
        /// model that is open is the person's own to carry on with. A build that is refused changes nothing, keeps the plan and
        /// tells nobody (nothing moved); one that lands calls NotifyStartPlanChanged like every other writer of the plan.</summary>
        public async Task<StartPlanBuildResult> BuildStartPlanAsync(string digest, string origin)
        {
            GuardOutsideWrite(origin);
            await _startPlanGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var doc = LoadStartPlan();
                if (doc == null) return new StartPlanBuildResult { Built = false, Refused = "There is no start plan to build.", View = StartPlanViewBuilder.Empty() };
                var copy = StartPlanStore.Copy(doc);
                var builtNow = await StartPlanIsBuiltAsync(copy).ConfigureAwait(false);
                if (builtNow)
                    return new StartPlanBuildResult { Built = false, Refused = "This plan was already built. Undo the build to change the plan and build again.", View = StartPlanViewBuilder.Build(copy, null, true) };
                var because = await StartPlanParkedReasonAsync(copy).ConfigureAwait(false);
                if (because != null)
                    return new StartPlanBuildResult { Built = false, Refused = because, View = StartPlanViewBuilder.Build(copy, because) };

                CancellationTokenSource loop;
                lock (_startPlanLock) { loop = _startLoop; _startLoop = null; }
                try { loop?.Cancel(); } catch { }
                try { loop?.Dispose(); } catch { }

                var session = _sessions.Current;
                var result = await ApplyStartPlanAsync(session, copy, digest, origin).ConfigureAwait(false);
                if (!result.Built)
                {
                    result.View = StartPlanViewBuilder.Build(copy, null);
                    return result;
                }

                // Built. The plan is KEPT, marked built (not deleted): an undo that takes the build back leaves the person with the same plan, its words and its approval,
                // to build again or change first. The model that is open is the person's own to carry on with, unsaved (a build is an edit like any other, undone with one
                // step). Whatever was still working for the plan stops, and a read after the build says "built", never "no plan".
                Interlocked.Increment(ref _startPlanEpoch);
                StartPlanScratch scratch;
                StartPlanView builtView;
                lock (_startPlanLock)
                {
                    scratch = _startScratch; _startScratch = null;
                    try { _startSampleCts?.Cancel(); } catch { }
                    var live = _startPlan;
                    if (live != null && string.Equals(live.Id, doc.Id, StringComparison.Ordinal))
                    {
                        live.BuiltUtc = DateTime.UtcNow;
                        live.BuiltDigest = digest;
                        live.UpdatedUtc = live.BuiltUtc.Value;
                        try { StartPlanStore.Write(live); } catch { }
                    }
                    builtView = StartPlanViewBuilder.Build(StartPlanStore.Copy(live ?? doc), null, true);
                }
                if (scratch != null) { try { await scratch.DisposeAsync().ConfigureAwait(false); } catch { } }
                DropStartWordsScratch();
                // The sample's facts stay beside the model that was built (they were saved at the start; this keeps them if the plan was parked while it was read).
                try { var sample = StartSampleFor(copy); if (sample != null && session != null) SaveSourceFacts(session, sample, copy.Id); } catch { }
                result.View = builtView;
                NotifyStartPlanChanged(result.View, origin);   // the Studio and any other assistant hear that it was built
                return result;
            }
            // A refused build has stopped the loop (above) and changed nothing: the plan goes back to the loop it had.
            finally { await EnsureStartDecideNotOrphanedAsync().ConfigureAwait(false); _startPlanGate.Release(); }
        }

        /// <summary>True when the plan was built and the model it built into still holds what it built: the model that is open is the plan's, and it has tables. An
        /// undo that takes the build back makes this false, and the plan is a plan again.</summary>
        private async Task<bool> StartPlanIsBuiltAsync(StartPlanDoc doc)
        {
            if (doc == null || doc.BuiltUtc == null) return false;
            var open = _sessions.Current;
            if (open == null || !IsStartDestination(open.SourcePath, doc.Destination)) return false;
            try { return await open.ReadAsync(m => m.Tables.Count).ConfigureAwait(false) > 0; }
            catch { return false; }
        }

        /// <summary>Build the approved plan into the person's model: ONE undoable step.
        ///
        /// Order, and why it is this order:
        ///  1. The checks run once here (fast, no model work) and AGAIN inside the delegate, because only a check made on
        ///     the single-writer thread covers the write; anything earlier is about a model that could still move.
        ///  2. The plan is built into the model, then the projection is taken and compared with the preview's, still
        ///     inside the delegate. A mismatch throws, and the batch's own rollback leaves no undo entry, no publish and no
        ///     revision (S7). A check made after the commit could not take the build back.
        ///  3. Only a build that passed gets its Verified Edits record, its activity nudge and its grades.
        ///
        /// A refusal returns <c>Built = false</c> with the reason in plain words and has changed nothing.</summary>
        internal async Task<StartPlanBuildResult> ApplyStartPlanAsync(Session s, StartPlanDoc doc, string digest, string origin)
        {
            var seams = StartPlanBuildSeamsForTest;
            var early = StartPlanBuild.Refusal(doc, digest, seams);
            if (early != null) return new StartPlanBuildResult { Built = false, Refused = early, Revision = s.Revision };

            var expected = doc.Preview.ProjectionDigest;
            (int? AiReadiness, string AiGrade, string BestPractice, int? BestPracticeOpen) inside = default;
            long rev;
            try
            {
                // The person approved this exact plan on the person-only door, so the commit carries "you" whichever door runs it.
                // The scope covers the commit only: the audit record below is not a change to the model and carries no cause.
                using (ChangeCause.Begin(new ChangeCauseInfo
                {
                    Kind = ChangeCauseKinds.StartPlan, Ids = new[] { doc.Id }, Title = "Build the model from its start plan", ApprovedBy = ChangeBy.You,
                    Declined = StartPlanBuild.RefusedColumnRefs(doc),   // a column the approved plan gives no measure is a decision on the model that was just made (UAT-J7-S2-009)
                }))
                    rev = await s.MutateAsync(origin, "build model from start plan", m =>
                    {
                        seams?.InsideBuildForTest?.Invoke();
                        var again = StartPlanBuild.Refusal(doc, digest, seams);
                        if (again != null) throw new StartPlanBuildRefusedException(again);
                        // THE PLAN THAT IS BUILT IS THE PLAN THAT WAS APPROVED. The copy above was taken when the build began; a writer that does not wait for the gate
                        // (a round of the decide, the sample landing) may have moved the live plan since. It commits only while the live plan's digest is the approved one,
                        // so nothing the person has not seen is built and nothing they added is written over (Sol, join 6, A4).
                        lock (_startPlanLock)
                        {
                            var live = _startPlan;
                            // (A document the engine does not hold, built through here directly by a test, has no live plan to differ from.)
                            if (live != null && string.Equals(live.Id, doc.Id, StringComparison.Ordinal)
                                && (!string.Equals(StartPlanViewBuilder.Digest(live), digest, StringComparison.Ordinal)
                                    || !string.Equals(live.Approval?.Digest, digest, StringComparison.Ordinal)))
                                throw new StartPlanBuildRefusedException("The plan changed while it was being built, so nothing was built and nothing you added was lost. Look at the plan again and approve it again.");
                        }
                        if (m.Tables.Count != 0)
                            throw new StartPlanBuildRefusedException("The new model already has tables in it, so the plan no longer fits it. Start the plan again from the model as it is.");

                        if (seams?.Materialize != null) seams.Materialize(this, m, s, doc);
                        else
                        {
                            // The two halves of StartPlanBuild.Materialize, with the check between them. After the structure the model holds exactly what the manifest's
                            // spec makes (before any line renames or adds anything), so every table, measure and link in it must be one the review listed, and a time
                            // measure must carry the review's formula. The review and the build are ONE list: a build that wrote an unseen object takes itself back (A1).
                            // The same rule one level down (Sol, join 7, UAT-J7-S2-008): every PROPERTY the structure set is on the review too, or a default the check
                            // knows by name and reason. The journal hears each property the build sets, so nothing a build sets can be missed by a list kept by hand.
                            using (var journal = new StartWriteJournal(s))
                            {
                                StartPlanBuild.MaterializeStructure(m, doc);
                                seams?.AfterStructureForTest?.Invoke(m);
                                var unseen = StartPlanBuild.UnreviewedObject(m, doc);
                                if (unseen != null)
                                    throw new StartPlanBuildRefusedException("The build would have added " + unseen + ", which the plan did not show, so nothing was built. Look at the plan again.");
                                seams?.JournalForTest?.Invoke(journal.Net().Select(n => StartPlanReview.RefOf(n.Obj) + "|" + n.Property + "=" + n.New).ToList());
                                var unreviewed = StartPlanReview.UnreviewedWrite(journal, doc);
                                if (unreviewed != null)
                                    throw new StartPlanBuildRefusedException("The build would have set " + unreviewed + ", which the plan did not show, so nothing was built. Look at the plan again.");
                            }
                            StartPlanBuild.MaterializeLines(this, m, s, doc);
                            seams?.AfterLinesForTest?.Invoke(m);
                            var synonym = StartPlanReview.UnreviewedSynonym(m, doc);
                            if (synonym != null)
                                throw new StartPlanBuildRefusedException("The build would have written " + synonym + ", which the plan did not show, so nothing was built. Look at the plan again.");
                        }

                        var projection = StartPlanProjection.Of(s);
                        seams?.Tap?.Invoke("build", projection);
                        if (seams?.TamperProjection != null) projection = seams.TamperProjection(projection);
                        if (!string.Equals(StartPlanProjection.Digest(projection), expected, StringComparison.Ordinal))
                            throw new StartPlanBuildRefusedException(
                                "The model this plan builds is not the one you were shown, so nothing was built. Look at the plan again.");
                    }).ConfigureAwait(false);
                inside = await s.ReadAsync(m => StartPlanBuild.GradesOf(m)).ConfigureAwait(false);
            }
            catch (StartPlanBuildRefusedException refused)
            {
                return new StartPlanBuildResult { Built = false, Refused = refused.Message, Revision = s.Revision };
            }

            // THE PERSON'S "NO" TRAVELS WITH THE BUILD. A suggestion they left unticked in the plan is a decision on the model that was just made, so it goes into the
            // Plan as a drop by the person, and the Plan after the build does not offer it again. Called once the lines have landed (and only then: a refused build
            // changes nothing, and tells the Plan nothing).
            try { HonourStartPlanDeclines(doc); }
            catch (Exception ex) { try { Console.Error.WriteLine("[start-plan] carrying the person's declines into the Plan failed: " + ex.Message); } catch { } }

            // The compare passed and the step is committed. The audit record is appended only now, so a build that was
            // rolled back never leaves one (the same rule apply_plan keeps), and it is deliberately not wrapped: an audit
            // layer that fails silently is worse than a failed op.
            var lines = doc.Lines.FindAll(l => l.Kept);
            await RecordVerifiedEditAsync(s, new VerifiedEditRecord
            {
                SessionId = s.Id, Revision = rev, Origin = origin, Op = "start_plan_build", Verdict = "batch",
                Summary = "Built the model from its start plan: " + lines.Count + " kept lines.",
                Evidence = System.Text.Json.JsonSerializer.Serialize(new
                {
                    planId = doc.Id, digest, projection = expected, kept = lines.Count,
                    aiReadiness = inside.AiReadiness, aiGrade = inside.AiGrade, bestPractice = inside.BestPractice,
                }),
                BodyHash = VerifiedEditsStore.BodyHash(digest),
            }).ConfigureAwait(false);
            try
            {
                await PublishActivityAsync(new ActivityEvent
                {
                    Kind = "start_plan_build", Origin = origin, Label = "Built the model from its start plan", Ok = true,
                    Result = new { kept = lines.Count, aiReadiness = inside.AiReadiness, aiGrade = inside.AiGrade, bestPractice = inside.BestPractice },
                }).ConfigureAwait(false);
            }
            catch { /* a nudge to the UI, never a reason to call a finished build failed */ }

            return new StartPlanBuildResult { Built = true, Revision = rev };
        }
    }
}
