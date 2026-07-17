using System;
using System.Collections.Generic;
using System.Linq;

namespace Semanticus.Engine
{
    // ============================================================================================
    // E1/E2 — the PURE decision layer over a dax_equivalence comparison (docs v5-engine-contract
    // §E1/§E2). CONSUMES the shared Verified-Edits evidence ladder (DaxBench.ClassifyEquivalence-
    // Evidence — rung order is that contract, never re-derived here) and applies the workflow's
    // pinned/open SHAPE LEDGER on top. PINNED evidence is classified from the PER-SHAPE data, not
    // the top-level aggregates (sol blocker 1): the top-level RowsCompared/Truncated belong to the
    // full cross, which may itself be OPEN — grading pinned evidence off them would let an open
    // cross supply the coverage for a grand-total-only pinned "proof". So the gate re-rides the
    // ladder on a PINNED-SCOPED result: pinned grid count = pinned non-grand-total shapes (zero ⇒
    // thin), coverage = the worst pinned shape, truncation = any pinned shape (open truncation is
    // an observation, never contamination). A pinned-shape mismatch convicts (failed); an open-
    // shape mismatch is recorded, never gated; every no-authoritative-evidence rung is a blocking
    // `unavailable` with the fidelity caveat preserved. Pure over the run state — unit-testable
    // offline with a hand-built EquivalenceResult.
    // ============================================================================================

    public static class EquivalenceGate
    {
        public sealed class GateOutcome
        {
            public string Status { get; set; }             // passed | failed | unavailable
            public string Missing { get; set; }            // on unavailable: the one thing that was absent
            public string Detail { get; set; }
            public ShapeVerifyResult[] Shapes { get; set; } = Array.Empty<ShapeVerifyResult>();
            public MismatchCell[] MismatchCells { get; set; } = Array.Empty<MismatchCell>();
        }

        /// <summary>E1 ADDENDUM — resolve the effective OPEN shape set: the static `openShapes:` key UNIONED with
        /// the ids listed in the run answer named by `openShapesFrom:` (the v5 seed decides the partition PER RUN
        /// from the requirement's context ledger). A declined or unanswered bound input contributes nothing (all
        /// pinned — the fail-closed default). ANY listed id — static or bound — that is not among the EVALUATED
        /// shapes sets <paramref name="error"/> (the caller refuses as `unavailable`, naming the bad id): a stale
        /// static id must not silently no-op any more than a bound typo may silently pin or un-pin a shape.</summary>
        public static string[] ResolveOpenShapes(VerifySpec spec, IReadOnlyDictionary<string, AnswerValue> answers,
            string[] evaluatedShapeIds, out string error)
        {
            error = null;
            var open = new List<string>();
            foreach (var id in spec?.OpenShapes ?? Array.Empty<string>())
            {
                // Grammar was parse-checked; membership can only be judged at run time against the actual grid.
                if (!evaluatedShapeIds.Contains(id, StringComparer.Ordinal))
                {
                    error = $"open-shape id '{id}' (static openShapes) is not among the evaluated shapes [{string.Join(", ", evaluatedShapeIds)}] — fix the workflow's openShapes/equivalenceGrid so they agree, then re-submit.";
                    return null;
                }
                open.Add(id);
            }
            var from = spec?.OpenShapesFrom;
            if (!string.IsNullOrWhiteSpace(from)
                && answers != null && answers.TryGetValue(from, out var a) && a != null && a.Answered)
            {
                var ids = (a.Value ?? "").Trim();
                if (ids.StartsWith("[", StringComparison.Ordinal) && ids.EndsWith("]", StringComparison.Ordinal))
                    ids = ids.Substring(1, ids.Length - 2);
                foreach (var raw in ids.Split(','))
                {
                    var id = raw.Trim().Trim('"', '\'');
                    if (id.Length == 0) continue;
                    if (!WorkflowParser.IsShapeId(id))
                    {
                        error = $"open-shape id '{id}' (from input '{from}') is not one of 'grand_total' | 'cross' | 'axis:<column>' — fix the answer and re-submit.";
                        return null;
                    }
                    if (!evaluatedShapeIds.Contains(id, StringComparer.Ordinal))
                    {
                        error = $"open-shape id '{id}' (from input '{from}') is not among the evaluated shapes [{string.Join(", ", evaluatedShapeIds)}] — check the axis spelling against equivalenceGrid, then re-submit.";
                        return null;
                    }
                    open.Add(id);
                }
            }
            return open.Distinct(StringComparer.Ordinal).ToArray();
        }

        /// <summary>Blocker 3 — the PARTITION LOCK. Lock the canonical effective OPEN set for this verify at the
        /// first actually-run comparison; a later submission whose effective set DIFFERS appends a
        /// {before, after, stepId, timestamp} revision receipt, re-locks the NEW set, and returns the block reason
        /// (the caller refuses THAT submission as `unavailable`) — so the next submission evaluates fresh under the
        /// new locked partition while the receipt and the prior verify results stay on the run record (evidence is
        /// never erased). Closes the laundering path: pin a shape, see its mismatch, silently re-submit with it
        /// open. Returns null when the partition is unchanged (or first seen). Caller holds _workflowGate.
        /// <paramref name="verifyIndex"/> is the verify's ordinal within its step (parse order is deterministic):
        /// without it, two same-step verifies sharing a probe would share ONE lock, and differing partitions would
        /// each re-lock against the other — both blocking forever.</summary>
        public static string RegisterPartition(WorkflowRunState run, string stepId, int verifyIndex, string probe, string[] openShapes)
        {
            var key = stepId + "|" + verifyIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + (probe ?? "");
            var canonical = string.Join(",", (openShapes ?? Array.Empty<string>()).OrderBy(x => x, StringComparer.Ordinal));
            if (!run.PartitionLocks.TryGetValue(key, out var locked)) { run.PartitionLocks[key] = canonical; return null; }
            if (string.Equals(locked, canonical, StringComparison.Ordinal)) return null;
            run.PartitionRevisions.Add(new PartitionRevision
            {
                Key = key,
                Before = locked, After = canonical,
                StepId = stepId, TimestampUtc = DateTime.UtcNow.ToString("o"),
            });
            run.PartitionLocks[key] = canonical;   // the NEXT submission evaluates fresh under the new locked partition
            return $"the pinned/open shape partition changed after evidence was seen (was [{locked}], now [{canonical}]) — a partition-revision receipt was recorded and the prior verify results stay on the run; re-submit to evaluate fresh under the new partition.";
        }

        /// <summary>Grade a completed comparison: the shared evidence ladder decides the rungs, the shape ledger
        /// refines them with PINNED-SCOPED evidence (see the file header). Default (both lists empty) = every shape
        /// PINNED (any mismatch fails). A shape is OPEN iff it is in <paramref name="openShapes"/> and NOT in
        /// <paramref name="pinnedShapes"/> (pinned wins the overlap). <paramref name="effectiveGridCount"/> is the
        /// NORMALIZED grid length (DaxBench.NormalizeGroupBy) — a raw length would let a whitespace-only grid grade
        /// as a per-context proof.</summary>
        public static GateOutcome Evaluate(EquivalenceResult eq, string[] pinnedShapes, string[] openShapes, int effectiveGridCount)
        {
            // THE rung decision — consumed from the one shared ladder, never re-derived:
            // error → degraded_mismatch → failed → zero-rows → truncated → degraded → thin → proven.
            var (state, note) = DaxBench.ClassifyEquivalenceEvidence(eq, effectiveGridCount);

            var openSet = new HashSet<string>(openShapes ?? Array.Empty<string>(), StringComparer.Ordinal);
            foreach (var p in pinnedShapes ?? Array.Empty<string>()) openSet.Remove(p);   // pinned wins the overlap

            var shapes = (eq?.Shapes ?? Array.Empty<ShapeComparison>()).Select(s => new ShapeVerifyResult
            {
                ShapeId = s.ShapeId,
                Pinned = !openSet.Contains(s.ShapeId),
                RowsCompared = s.RowsCompared,
                MismatchCount = s.MismatchCount,
                Truncated = s.Truncated,
                Sample = (s.Mismatches ?? Array.Empty<EquivalenceMismatch>())
                    .Select(m => new MismatchCell { Context = m.Context, ValueA = m.ValueA, ValueB = m.ValueB }).ToArray(),
            }).ToArray();
            var pinned = shapes.Where(s => s.Pinned).ToArray();
            var open = shapes.Where(s => !s.Pinned).ToArray();
            var pinnedBad = pinned.Where(s => s.MismatchCount > 0).ToArray();

            // Engine-reported mismatching cells for adjudication (E3c): pinned first (they decide the gate), then
            // open (observed only), bounded so the payload stays token-lean.
            var cells = pinnedBad.SelectMany(s => s.Sample)
                .Concat(open.Where(s => s.MismatchCount > 0).SelectMany(s => s.Sample)).Take(10).ToArray();
            // Blocker 2 — open shapes are ALWAYS named as observations (matched or not), never counted as proof.
            var openNote = open.Length == 0 ? "" : " OPEN shapes (observed, not gated): " + string.Join("; ",
                open.Select(s => $"{s.ShapeId} ({s.RowsCompared} row(s), {s.MismatchCount} mismatch(es){(s.Truncated ? ", truncated" : "")}")) + ".";

            // Global rungs first — a failed RUN or a degraded comparison is judged whole (no shape refinement:
            // under reduced fidelity even an open-shape observation is unattributable to the DAX).
            if (eq == null || !string.IsNullOrEmpty(eq.Error))
                return Unavailable(eq, note, shapes, cells);
            if (state == "degraded_mismatch" || state == "degraded")
                return new GateOutcome
                {
                    Status = "unavailable",
                    Missing = "a full-fidelity comparison — " + (eq.Fidelity ?? "the comparison ran degraded"),
                    Detail = note + openNote + " Fix the fidelity gap (open the model live so the target's identity is known) and re-submit, or skip_workflow_step with a reason (recorded).",
                    Shapes = shapes, MismatchCells = cells,
                };
            if (!string.IsNullOrEmpty(eq.Fidelity))   // fidelity + zero/truncated rides the unverified rung — still degraded evidence
                return new GateOutcome
                {
                    Status = "unavailable",
                    Missing = "a full-fidelity comparison — " + eq.Fidelity,
                    Detail = note + openNote,
                    Shapes = shapes, MismatchCells = cells,
                };

            // No per-shape data (a hand-built or legacy result): the global rung IS the pinned evidence — every
            // shape is implicitly pinned, which is exactly what the top-level aggregates describe.
            if (shapes.Length == 0)
            {
                switch (state)
                {
                    case "proven":
                        return new GateOutcome { Status = "passed", Detail = $"candidate ≡ witness across {eq.RowsCompared} pinned context(s)." };
                    case "failed":
                        return new GateOutcome
                        {
                            Status = "failed",
                            Detail = note + " Adjudicate cell-by-cell against a raw-row extract; rewrite only a convicted side, then re-submit.",
                            MismatchCells = (eq.Mismatches ?? Array.Empty<EquivalenceMismatch>()).Take(10)
                                .Select(m => new MismatchCell { Context = m.Context, ValueA = m.ValueA, ValueB = m.ValueB }).ToArray(),
                        };
                    case "thin":
                        return new GateOutcome { Status = "unavailable", Missing = "a per-context equivalence grid", Detail = note + " Zero effective pinned coverage — set `equivalenceGrid` to the metric's natural grain axes, then re-submit." };
                    default:
                        return Unavailable(eq, note, shapes, cells);
                }
            }

            // Blocker 1 — the LEDGER-AWARE rungs, classified from PINNED evidence only.
            if (pinnedBad.Length > 0)
            {
                var lead = string.Join("; ", pinnedBad.Select(s => $"{s.ShapeId} ({s.MismatchCount})"));
                var eg = cells.Length == 0 ? "" : " e.g. " + string.Join("; ", cells.Take(3).Select(c => $"{c.Context}: {c.ValueA} vs {c.ValueB}"));
                return new GateOutcome
                {
                    Status = "failed",
                    Detail = $"candidate and witness DIVERGE on pinned shape(s): {lead}.{eg}{openNote} Adjudicate cell-by-cell against a raw-row extract; rewrite only a convicted side, then re-submit.",
                    Shapes = shapes, MismatchCells = cells,
                };
            }
            if (pinned.Length == 0)
                return new GateOutcome
                {
                    Status = "unavailable",
                    Missing = "at least one pinned shape",
                    Detail = "every evaluated shape is OPEN — nothing is enforced, so there is nothing this gate can prove." + openNote + " Pin at least the diverging shape, then re-submit.",
                    Shapes = shapes, MismatchCells = cells,
                };

            // Re-ride the ladder on the PINNED-SCOPED result: grid count = pinned non-grand-total shapes; coverage =
            // the WORST pinned shape (every pinned shape must have rows); truncation = any pinned shape (plus the
            // top-level flag when nothing is open — a legacy top-level flag can then only describe pinned shapes).
            var pinnedGridCount = pinned.Count(s => s.ShapeId != "grand_total");
            var zeroShape = pinned.FirstOrDefault(s => s.RowsCompared == 0);
            var truncShape = pinned.FirstOrDefault(s => s.Truncated);
            var pinnedScoped = new EquivalenceResult
            {
                AllMatch = true, MismatchCount = 0,
                RowsCompared = pinned.Min(s => s.RowsCompared),
                Truncated = truncShape != null || (open.Length == 0 && eq.Truncated),
            };
            var (pState, pNote) = DaxBench.ClassifyEquivalenceEvidence(pinnedScoped, pinnedGridCount);
            switch (pState)
            {
                case "proven":
                    // Blocker 2 — the pass counts ONLY pinned contexts; open shapes were named above as observations.
                    var pinnedContexts = pinned.Sum(s => s.RowsCompared);
                    return new GateOutcome
                    {
                        Status = "passed",
                        Detail = $"candidate ≡ witness on {pinned.Length} pinned shape(s) [{string.Join(", ", pinned.Select(s => s.ShapeId))}] across {pinnedContexts} pinned context(s).{openNote}",
                        Shapes = shapes, MismatchCells = cells,
                    };
                case "thin":
                    return new GateOutcome
                    {
                        Status = "unavailable",
                        Missing = "a per-context equivalence grid with at least one pinned non-grand-total shape",
                        Detail = pNote + " Zero effective pinned coverage — pin the metric's grain axes (or set `equivalenceGrid` so the diverging shape is evaluated and pinned), then re-submit." + openNote,
                        Shapes = shapes, MismatchCells = cells,
                    };
                default:   // "unverified" — pinned zero coverage or pinned truncation
                    return new GateOutcome
                    {
                        Status = "unavailable",
                        Missing = zeroShape != null ? $"rows to compare on pinned shape '{zeroShape.ShapeId}'"
                            : truncShape != null ? $"complete coverage on pinned shape '{truncShape.ShapeId}' (row cap hit)"
                            : "complete coverage (row cap hit)",
                        Detail = pNote + (zeroShape != null ? $" Pinned shape '{zeroShape.ShapeId}' compared 0 rows." : "") + openNote,
                        Shapes = shapes, MismatchCells = cells,
                    };
            }
        }

        // The unverified rungs, mapped to `unavailable` with a token-lean Missing naming the absent thing. The
        // WORDING keys off the result's own fields; the RUNG stays the ladder's.
        private static GateOutcome Unavailable(EquivalenceResult eq, string note, ShapeVerifyResult[] shapes, MismatchCell[] cells)
        {
            var missing =
                eq == null || !string.IsNullOrEmpty(eq.Error)
                    ? (eq?.AuthFailed == true ? "an authenticated connection" : "a completed comparison query")
                : eq.RowsCompared <= 0 ? "rows to compare on the requested grid"
                : eq.Truncated ? "complete coverage (row cap hit)"
                : "authoritative evidence";
            return new GateOutcome { Status = "unavailable", Missing = missing, Detail = note, Shapes = shapes, MismatchCells = cells };
        }
    }
}
