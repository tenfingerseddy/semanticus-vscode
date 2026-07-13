using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    /// <summary>
    /// The safe-to-remove sweep's ACT half (remove_safe_objects) — the Lineage &amp; Impact tab's "Measure Killer"
    /// headline. The DETECT half (unused_objects / analyze_reports) stays free and read-only; this op deletes the
    /// verified-safe set as ONE undoable transaction through the normal tracked mutation path (never a raw TOM
    /// bypass), with the safety spine the feature is sold on:
    ///  • the safe set is RECOMPUTED server-side and each candidate RE-VERIFIED at apply time, INSIDE the
    ///    single-writer mutation delegate — a verdict that went stale since the caller's scan (a new referencer
    ///    from either door, a report now using the field) downgrades the item to SKIPPED, never a stale delete;
    ///  • one append-only audit record carries the evidence (what was removed, what was skipped and why);
    ///  • removing MORE THAN ONE item at once is Pro (the bulk/atomic primitive); a single item stays free —
    ///    same per-item path delete_object offers.
    /// Dry-runnable by design: the pre-pass is a read, the deletes ride one MutateAsync (the dry_run chokepoint),
    /// and the audit/activity writers are DryRunScope-guarded, so dry_run(remove_safe_objects) rehearses the whole
    /// sweep — including the would-be removed/skipped report — and leaves nothing.
    /// </summary>
    public sealed partial class LocalEngine
    {
        public async Task<RemoveSafeReport> RemoveSafeObjectsAsync(string[] refs, string[] reportPaths, string origin)
        {
            var s = _sessions.Require();

            // Report definitions are parsed OFF the dispatcher once (file IO, same leg as analyze_reports); the
            // candidate pass and the at-apply re-verification then run the SAME report-aware sweep, so "safe"
            // means the same thing at scan and at apply. No paths = the model-only sweep (with its honesty caveat).
            var parsed = (reportPaths ?? Array.Empty<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => (path: p, result: Lineage.ReportDefinitionReader.ReadLocalPbir(p)))
                .ToList();
            UnusedResult Sweep(Model m) => parsed.Count == 0
                ? Lineage.LineageGraph.Unused(m)
                : Lineage.LineageGraph.AnalyzeReports(m, parsed).Unused;
            // The verification label counts reports actually READ (DefinitionFound), never paths supplied — a
            // sweep must not claim report-awareness on the strength of unreadable files.
            var readable = parsed.Count(p => p.result.DefinitionFound);
            var verification = parsed.Count == 0 ? "model-only"
                : $"report-aware ({readable} of {parsed.Count} report file(s) readable)";

            var requested = (refs ?? Array.Empty<string>())
                .Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            // FAIL-LOUD: the caller passed report paths for PROTECTION, and none could be read — a "report-aware"
            // sweep would silently degrade to model-only and could delete a field the intended report uses. Refuse
            // the whole sweep before anything is touched. (With PARTIAL readability the sweep still cannot delete:
            // AnalyzeReports demotes every bare "safe" to "caution" when coverage is incomplete, so the candidate
            // set is empty — each requested item skips with the coverage reason instead.)
            if (parsed.Count > 0 && readable == 0)
                return new RemoveSafeReport
                {
                    Revision = s.Revision,
                    Skipped = requested.Select(r => new SkippedObject
                    {
                        Ref = r,
                        Reason = "none of the supplied report files could be read, so report-aware safety could not be verified.",
                    }).ToArray(),
                    Count = 0,
                    Verification = verification,
                    Note = "None of the supplied report files could be read — nothing was removed. Fix the paths (a PBIR " +
                           "'<Report>.Report' folder, its 'definition' folder, a .pbip file, or a project root; legacy .pbix " +
                           "is not parsed), or omit reportPaths for a model-only sweep.",
                };

            // Candidate pass (read-only): the CURRENT verified-safe set, narrowed to the caller's refs when given.
            // A requested item that is not verified safe RIGHT NOW is skipped with the reason — the caller's scan
            // is treated as a proposal, never as authority to delete.
            var skipped = new List<SkippedObject>();
            var (candidates, caveat) = await s.ReadAsync(m =>
            {
                var sweep = Sweep(m);
                var byRef = sweep.Items.ToDictionary(i => i.Ref, StringComparer.OrdinalIgnoreCase);
                var safeNow = sweep.Items.Where(i => i.Verdict == "safe").ToList();
                if (requested.Count == 0) return (safeNow, sweep.Caveat);

                var picked = new List<UnusedItem>();
                foreach (var r in requested)
                {
                    if (byRef.TryGetValue(r, out var item) && item.Verdict == "safe") { picked.Add(item); continue; }
                    skipped.Add(new SkippedObject { Ref = r, Reason = NotSafeReason(m, r, byRef, parsed.Count > 0) });
                }
                return (picked, sweep.Caveat);
            });

            // Pro gate — the bulk/atomic primitive. Thrown BEFORE any mutation, so a refusal leaves the model
            // intact; a single item (or the per-item delete_object path) stays free.
            if (candidates.Count > 1)
                Entitlement.EntitlementGuard.RequirePro(_entitlement,
                    $"Removing {candidates.Count} objects in one sweep",
                    $"Each item can be deleted one at a time free (delete_object); Pro removes all {candidates.Count} verified-safe items in one undoable step.");

            if (candidates.Count == 0)
                return new RemoveSafeReport
                {
                    Revision = s.Revision, Skipped = skipped.ToArray(), Count = 0,
                    Verification = verification, Caveat = caveat,
                    Note = requested.Count > 0
                        ? "Nothing removed: no requested item is currently verified safe — each skipped entry says why. Re-run unused_objects for a fresh scan."
                        : "Nothing to remove: the model has no verified-safe items right now.",
                };

            // ONE undoable transaction on the single-writer dispatcher. The safe set is recomputed INSIDE the
            // delegate, immediately before the deletes — nothing can interleave between this re-verification and
            // the removal, so a "safe" that held at the top of the delegate still holds at each delete (removing
            // a zero-referencer object can only shrink other objects' referencer sets, never grow them).
            var removed = new List<RemovedObject>();
            var rev = await s.MutateAsync(origin, $"remove {candidates.Count} safe-to-remove object(s)", m =>
            {
                var fresh = Sweep(m);
                var freshByRef = fresh.Items.ToDictionary(i => i.Ref, StringComparer.OrdinalIgnoreCase);
                foreach (var c in candidates)
                {
                    if (!freshByRef.TryGetValue(c.Ref, out var now) || now.Verdict != "safe")
                    {
                        // The stale-verdict downgrade the feature promises: changed since the scan ⇒ skipped, never deleted.
                        skipped.Add(new SkippedObject { Ref = c.Ref, Reason = "its status changed between the scan and the apply: " + NotSafeReason(m, c.Ref, freshByRef, parsed.Count > 0) });
                        continue;
                    }
                    var obj = ObjectRefs.Resolve(m, c.Ref);
                    if (obj == null)
                    {
                        skipped.Add(new SkippedObject { Ref = c.Ref, Reason = "not found at apply time. It may have been deleted or renamed since the scan." });
                        continue;
                    }
                    try
                    {
                        obj.Delete();
                        removed.Add(new RemovedObject { Ref = c.Ref, Name = c.Name, Kind = c.Kind, Table = c.Table });
                    }
                    // A per-item delete failure (e.g. a column kind the wrapper refuses to delete) must not abort the
                    // whole sweep — it is reported as skipped with the wrapper's reason; the batch stays one undo step.
                    catch (Exception ex)
                    {
                        skipped.Add(new SkippedObject { Ref = c.Ref, Reason = "delete failed: " + ex.Message });
                    }
                }
            });

            var report = new RemoveSafeReport
            {
                Revision = rev,
                Removed = removed.ToArray(),
                Skipped = skipped.ToArray(),
                Count = removed.Count,
                Verification = verification,
                Caveat = caveat,
                // No undo claim when nothing was removed — a "removed 0, undo restores them" line would mislead
                // audit/activity consumers into thinking a mutation happened.
                Note = removed.Count == 0
                    ? $"Nothing removed: all {candidates.Count} candidate(s) were skipped at apply time (each skipped entry says why)."
                    : $"Removed {removed.Count} of {candidates.Count} verified-safe item(s) in one undoable step"
                      + (skipped.Count > 0 ? $"; {skipped.Count} skipped (each skipped entry says why)" : "")
                      + ". Undo restores them all at once (undo_change).",
            };

            // Audit: ONE record for the sweep, carrying the evidence — what was removed, what was skipped and why,
            // and which verification basis vouched for "safe". Only a batch that actually changed the model is
            // recorded (matching apply_plan); the append is DryRunScope-guarded inside RecordVerifiedEditAsync.
            if (removed.Count > 0)
                await RecordVerifiedEditAsync(s, new VerifiedEditRecord
                {
                    SessionId = s.Id, Revision = rev, Origin = origin, Op = "remove_safe_objects",
                    Verdict = "batch",
                    Summary = $"{removed.Count} verified-safe object(s) removed, {skipped.Count} skipped — each re-verified {verification} at apply time",
                    Evidence = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        removed = removed.Take(200).Select(r => r.Ref).ToArray(),
                        removedTruncated = removed.Count > 200,
                        skipped = skipped.Take(100).Select(k => new { k.Ref, k.Reason }).ToArray(),
                        skippedTruncated = skipped.Count > 100,
                        verification, caveat,
                    }),
                });

            // Learning Loop L0 ride-along (best-effort; never breaks the sweep). Guarded: a rehearsal must not
            // emit a phantom activity event — this op IS dry-runnable, unlike the denied composites.
            if (DryRunScope.Current == null)
            {
                try
                {
                    await PublishActivityAsync(new ActivityEvent
                    {
                        Kind = "remove_safe_objects", Origin = origin, Label = report.Note, Ok = true,
                        Result = new { removed = removed.Count, skipped = skipped.Count, verification },
                    });
                }
                catch { }
            }
            return report;
        }

        // Why a ref is NOT deletable right now, in plain analyst English that names the recovery path.
        // reportAware: the sweep included report usage, so an existing-but-unlisted object may be report-used
        // (not only model-used) — the reason must not misattribute it to a model dependent.
        private static string NotSafeReason(Model m, string objRef, IReadOnlyDictionary<string, UnusedItem> sweepByRef, bool reportAware)
        {
            if (sweepByRef.TryGetValue(objRef, out var item))
            {
                if (item.Verdict == "usedByUnusedOnly")
                {
                    var by = item.BlockedBy != null && item.BlockedBy.Length > 0 ? " (" + string.Join(", ", item.BlockedBy.Take(5)) + ")" : "";
                    return $"objects that are themselves unused still reference it{by}. Remove those first, or delete it individually with delete_object.";
                }
                return "its safety could not be fully verified. " + item.Reason;
            }
            if (ObjectRefs.Resolve(m, objRef) == null)
                return "not found. It may already be deleted or renamed. Run unused_objects for a fresh scan.";
            return reportAware
                ? "something in the model, or one of the analyzed reports, uses it, so it is not safe to remove. Run impact_of / analyze_reports to see the usage."
                : "something in the model uses it, so it is not safe to remove. Run impact_of to see its dependents.";
        }
    }
}
