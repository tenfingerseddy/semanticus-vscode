using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

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
        public const int MaxStoredMismatchCoordinatesPerShape = 10;

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
                    error = $"open-shape id '{id}' (static openShapes) is not among the evaluated shapes [{string.Join(", ", evaluatedShapeIds)}]. Fix the workflow's openShapes/equivalenceGrid so they agree, then re-submit.";
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
                        error = $"open-shape id '{id}' (from input '{from}') is not one of 'grand_total' | 'cross' | 'axis:<column>'. Fix the answer and re-submit.";
                        return null;
                    }
                    if (!evaluatedShapeIds.Contains(id, StringComparer.Ordinal))
                    {
                        error = $"open-shape id '{id}' (from input '{from}') is not among the evaluated shapes [{string.Join(", ", evaluatedShapeIds)}]. Check the axis spelling against equivalenceGrid, then re-submit.";
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
            return $"the pinned/open shape partition changed after evidence was seen (was [{locked}], now [{canonical}]). A partition-revision receipt was recorded and the prior verify results stay on the run; re-submit to evaluate fresh under the new partition.";
        }

        /// <summary>A one-column grid has one physical non-total query, but v7 coverage still treats its axis and
        /// cross grains as separate logical obligations. Duplicate that result under the logical cross id only for
        /// coverage-backed runs; legacy callers retain the physical dedup payload.</summary>
        public static EquivalenceResult PreserveLogicalCross(EquivalenceResult eq, int effectiveGridCount)
        {
            if (eq == null || effectiveGridCount != 1 || !string.IsNullOrEmpty(eq.Error)) return eq;
            var shapes = eq.Shapes ?? Array.Empty<ShapeComparison>();
            if (shapes.Any(s => s.ShapeId == "cross")) return eq;
            var axis = shapes.FirstOrDefault(s => s.ShapeId != "grand_total" && s.ShapeId.StartsWith("axis:", StringComparison.Ordinal));
            if (axis == null) return eq;
            eq.Shapes = shapes.Concat(new[]
            {
                new ShapeComparison
                {
                    ShapeId = "cross",
                    RowsCompared = axis.RowsCompared,
                    MismatchCount = axis.MismatchCount,
                    Truncated = axis.Truncated,
                    Fidelity = axis.Fidelity,
                    Mismatches = (axis.Mismatches ?? Array.Empty<EquivalenceMismatch>()).Select(m => new EquivalenceMismatch
                    {
                        Context = m.Context,
                        ContextParts = m.ContextParts?.Select(p => p.Clone()).ToArray() ?? Array.Empty<MismatchContextPart>(),
                        ValueA = m.ValueA,
                        ValueB = m.ValueB,
                    }).ToArray(),
                },
            }).ToArray();
            return eq;
        }

        /// <summary>The exact identity an agent copies from an open-mismatch refusal into the countersign answer.
        /// The URL-safe base64 payload is the deterministic JSON encoding of structured name/type/value triples. Human
        /// display text is deliberately excluded, so delimiter-bearing values cannot alias another coordinate.</summary>
        public static string CanonicalMismatchCoordinate(string shapeId, string context,
            MismatchContextPart[] contextParts = null)
        {
            var parts = contextParts != null && contextParts.Length > 0
                ? contextParts.Select(p => new[] { p?.Name ?? "", p?.Type ?? "", p?.Value ?? "" }).ToArray()
                : new[] { new[] { "", "", context ?? "" } };
            var payload = JsonSerializer.Serialize(new { version = 2, shape = shapeId ?? "", members = parts });
            return "cell:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        internal static string SanitizeMismatchDisplay(string value, int maxLength = 160)
        {
            var source = value ?? "";
            var rendered = new System.Text.StringBuilder(Math.Min(source.Length, maxLength));
            foreach (var c in source)
            {
                var piece = c switch
                {
                    '\r' => "\\r",
                    '\n' => "\\n",
                    '\t' => "\\t",
                    _ when char.IsControl(c) => "\\u" + ((int)c).ToString("x4"),
                    _ => c.ToString(),
                };
                if (rendered.Length + piece.Length > maxLength)
                {
                    rendered.Append("...");
                    break;
                }
                rendered.Append(piece);
            }
            return rendered.ToString();
        }

        /// <summary>Update the cumulative run ledger from every evaluated shape. A zero-mismatch evaluation is the
        /// deliberate fix exit and clears that shape's active cells. Exact prior countersigns suppress only the same
        /// OPEN coordinate with the same candidate and witness values; pinned observations remain ordinary failures.</summary>
        public static void RecordOpenMismatches(WorkflowRunState run, ShapeVerifyResult[] shapes)
        {
            if (run == null) return;
            foreach (var shape in shapes ?? Array.Empty<ShapeVerifyResult>())
            {
                if (!run.ShapeMismatchLedger.TryGetValue(shape.ShapeId, out var entry))
                {
                    entry = new ShapeMismatchLedgerEntry { ShapeId = shape.ShapeId };
                    run.ShapeMismatchLedger[shape.ShapeId] = entry;
                }

                entry.Open = !shape.Pinned;
                entry.LastMismatchCount = shape.MismatchCount;
                if (shape.MismatchCount <= 0)
                {
                    entry.LastMismatchReconciledCount = 0;
                    entry.Cells = Array.Empty<ShapeMismatchLedgerCell>();
                    entry.State = entry.Open ? "OPEN-CLEAN" : "PINNED-CLEAN";
                    continue;
                }

                entry.TotalMismatchCount += shape.MismatchCount;
                var active = (entry.Cells ?? Array.Empty<ShapeMismatchLedgerCell>()).ToList();
                var reconciled = 0;
                var currentCoordinates = new HashSet<string>(StringComparer.Ordinal);
                foreach (var mismatch in shape.Sample ?? Array.Empty<MismatchCell>())
                {
                    var coordinate = CanonicalMismatchCoordinate(shape.ShapeId, mismatch.Context, mismatch.ContextParts);
                    if (!currentCoordinates.Add(coordinate)) continue;
                    var alreadySigned = entry.Open && run.ShapeMismatchCountersigns.Any(c =>
                        string.Equals(c.ShapeId, shape.ShapeId, StringComparison.Ordinal)
                        && string.Equals(c.Coordinate, coordinate, StringComparison.Ordinal)
                        && string.Equals(c.CandidateValue, mismatch.ValueB, StringComparison.Ordinal)
                        && string.Equals(c.WitnessValue, mismatch.ValueA, StringComparison.Ordinal));
                    var existing = active.FindIndex(c => string.Equals(c.Coordinate, coordinate, StringComparison.Ordinal));
                    if (alreadySigned)
                    {
                        reconciled++;
                        if (existing >= 0) active.RemoveAt(existing);
                        continue;
                    }

                    var cell = new ShapeMismatchLedgerCell
                    {
                        Coordinate = coordinate,
                        DisplayContext = mismatch.Context,
                        CandidateValue = mismatch.ValueB,
                        WitnessValue = mismatch.ValueA,
                    };
                    if (existing >= 0) { active[existing] = cell; reconciled++; }
                    else if (active.Count < MaxStoredMismatchCoordinatesPerShape) { active.Add(cell); reconciled++; }
                }
                entry.LastMismatchReconciledCount = reconciled;
                entry.Cells = active.OrderBy(c => c.Coordinate, StringComparer.Ordinal).ToArray();
                entry.State = !entry.Open ? "PINNED-MISMATCH"
                    : entry.Cells.Length == 0 && entry.LastMismatchCount <= entry.LastMismatchReconciledCount
                        ? "COUNTERSIGNED" : "DISPUTED";
            }
        }

        /// <summary>Apply the def-gated countersign rung after the ordinary equivalence gate has classified pinned
        /// evidence. Pinned failures remain failures. An exact countersign stores immutable cell/value receipts and
        /// leaves any independent unavailable rung intact.</summary>
        public static GateOutcome ApplyOpenMismatchCountersign(WorkflowRunState run, string stepId,
            GateOutcome outcome, AnswerValue answer)
        {
            if (outcome == null || run == null || outcome.Status == "failed") return outcome;
            var disputedEntries = run.ShapeMismatchLedger.Values
                .Where(e => e.Open && string.Equals(e.State, "DISPUTED", StringComparison.Ordinal))
                .OrderBy(e => e.ShapeId, StringComparer.Ordinal)
                .ToArray();
            var incomplete = disputedEntries
                .Where(e => e.LastMismatchCount > e.LastMismatchReconciledCount)
                .ToArray();
            if (incomplete.Length > 0)
            {
                var counts = string.Join("; ", incomplete.Select(e =>
                    $"{SanitizeMismatchDisplay(e.ShapeId)} has {e.LastMismatchCount} disagreements but only {e.LastMismatchReconciledCount} stored coordinates"));
                return OpenMismatchUnavailable(outcome,
                    $"OPEN-shape disagreement requires a decision. {counts}. There are too many disagreements to acknowledge individually; fix a side instead. Exit A: fix the candidate or witness and rerun the full grid; a zero-mismatch evaluation clears that shape.");
            }
            var disputed = disputedEntries
                .SelectMany(e => (e.Cells ?? Array.Empty<ShapeMismatchLedgerCell>())
                    .OrderBy(c => c.Coordinate, StringComparer.Ordinal)
                    .Select(c => (Entry: e, Cell: c)))
                .ToArray();
            if (disputed.Length == 0) return outcome;

            var expected = disputed.Select(x => x.Cell.Coordinate).ToArray();
            var refusal = BuildCountersignRefusal(disputed, expected);
            if (answer == null || !answer.Answered)
                return OpenMismatchUnavailable(outcome, refusal);

            if (!TryParseCountersign(answer.Value, out var submitted, out var stated, out var parseError))
                return OpenMismatchUnavailable(outcome, parseError + " " + refusal);

            var expectedSet = new HashSet<string>(expected, StringComparer.Ordinal);
            var submittedSet = new HashSet<string>(submitted, StringComparer.Ordinal);
            var missing = expectedSet.Where(x => !submittedSet.Contains(x)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var extra = submittedSet.Where(x => !expectedSet.Contains(x)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (missing.Length > 0 || extra.Length > 0)
            {
                var diff = "the countersign set does not exactly match the current disputes."
                    + (missing.Length == 0 ? " Missing: []." : " Missing: [" + string.Join("; ", missing) + "].")
                    + (extra.Length == 0 ? " Extra: []." : " Extra: [" + string.Join("; ", extra) + "].");
                return OpenMismatchUnavailable(outcome, diff + " " + refusal);
            }

            var now = DateTime.UtcNow.ToString("o");
            foreach (var item in disputed)
            {
                run.ShapeMismatchCountersigns.Add(new ShapeMismatchCountersign
                {
                    ShapeId = item.Entry.ShapeId,
                    Coordinate = item.Cell.Coordinate,
                    CandidateValue = item.Cell.CandidateValue,
                    WitnessValue = item.Cell.WitnessValue,
                    Stated = stated,
                    StepId = stepId,
                    TimestampUtc = now,
                });
            }
            foreach (var entry in disputed.Select(x => x.Entry).Distinct())
            {
                entry.Cells = Array.Empty<ShapeMismatchLedgerCell>();
                entry.State = "COUNTERSIGNED";
            }
            outcome.Detail = (outcome.Detail ?? "") + $" Countersigned {disputed.Length} open mismatch cell(s). Stated: {stated}";
            return outcome;
        }

        private static GateOutcome OpenMismatchUnavailable(GateOutcome outcome, string detail) => new GateOutcome
        {
            Status = "unavailable",
            Missing = "a fix or exact countersign for every open-shape mismatch",
            Detail = detail,
            Shapes = outcome.Shapes,
            MismatchCells = outcome.MismatchCells,
        };

        private static string BuildCountersignRefusal(
            (ShapeMismatchLedgerEntry Entry, ShapeMismatchLedgerCell Cell)[] disputed, string[] expected)
        {
            var cells = string.Join(" ", disputed.Select(x =>
                $"Shape {SanitizeMismatchDisplay(x.Entry.ShapeId)}, coordinate {x.Cell.Coordinate}, context {SanitizeMismatchDisplay(x.Cell.DisplayContext)}: "
                + $"candidate={SanitizeMismatchDisplay(x.Cell.CandidateValue)}; witness={SanitizeMismatchDisplay(x.Cell.WitnessValue)}."));
            var skeleton = JsonSerializer.Serialize(new
            {
                cells = expected,
                stated = "<one sentence explaining why the requirement leaves these cells open>",
            });
            return $"OPEN-shape disagreement requires a decision. {cells} Exit A: fix the candidate or witness and rerun the full grid; a zero-mismatch evaluation clears that shape. Exit B: answer the countersign input with this exact-set skeleton: {skeleton}";
        }

        private static bool TryParseCountersign(string raw, out string[] cells, out string stated, out string error)
        {
            cells = Array.Empty<string>();
            stated = null;
            error = null;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(raw ?? ""); }
            catch (Exception ex)
            {
                error = "the countersign answer is not valid JSON: " + ex.Message + ".";
                return false;
            }
            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    error = "the countersign answer must be an object with only 'cells' and 'stated'.";
                    return false;
                }
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    if (!seen.Add(property.Name))
                    {
                        error = $"the countersign answer declares '{property.Name}' more than once.";
                        return false;
                    }
                    if (property.Name != "cells" && property.Name != "stated")
                    {
                        error = $"the countersign answer has unknown property '{property.Name}'. Only 'cells' and 'stated' are allowed.";
                        return false;
                    }
                }
                if (!doc.RootElement.TryGetProperty("cells", out var listed) || listed.ValueKind != JsonValueKind.Array)
                {
                    error = "the countersign answer needs a 'cells' array copied from the blocking message.";
                    return false;
                }
                var parsed = new List<string>();
                var unique = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in listed.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                    {
                        error = "every countersign cells entry must be a non-empty coordinate string copied from the blocking message.";
                        return false;
                    }
                    var coordinate = item.GetString();
                    if (!unique.Add(coordinate))
                    {
                        error = $"the countersign cells array repeats '{coordinate}'. List each disputed coordinate once.";
                        return false;
                    }
                    parsed.Add(coordinate);
                }
                if (!doc.RootElement.TryGetProperty("stated", out var reason)
                    || reason.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(reason.GetString()))
                {
                    error = "the countersign answer needs one non-empty free-text sentence in 'stated'.";
                    return false;
                }
                if (reason.GetString().IndexOfAny(new[] { '\r', '\n' }) >= 0)
                {
                    error = "the countersign 'stated' field must be one sentence on one line.";
                    return false;
                }
                cells = parsed.ToArray();
                stated = reason.GetString();
                return true;
            }
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
                    .Select(m => new MismatchCell
                    {
                        Context = SanitizeMismatchDisplay(m.Context),
                        ContextParts = m.ContextParts?.Select(p => p.Clone()).ToArray() ?? Array.Empty<MismatchContextPart>(),
                        ValueA = m.ValueA,
                        ValueB = m.ValueB,
                    }).ToArray(),
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
                    Missing = "a full-fidelity comparison: " + (eq.Fidelity ?? "the comparison ran degraded"),
                    Detail = note + openNote + " Fix the fidelity gap (open the model live so the target's identity is known) and re-submit, or skip_workflow_step with a reason (recorded).",
                    Shapes = shapes, MismatchCells = cells,
                };
            if (!string.IsNullOrEmpty(eq.Fidelity))   // fidelity + zero/truncated rides the unverified rung — still degraded evidence
                return new GateOutcome
                {
                    Status = "unavailable",
                    Missing = "a full-fidelity comparison: " + eq.Fidelity,
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
                                .Select(m => new MismatchCell
                                {
                                    Context = SanitizeMismatchDisplay(m.Context),
                                    ContextParts = m.ContextParts?.Select(p => p.Clone()).ToArray() ?? Array.Empty<MismatchContextPart>(),
                                    ValueA = m.ValueA,
                                    ValueB = m.ValueB,
                                }).ToArray(),
                        };
                    case "thin":
                        return new GateOutcome { Status = "unavailable", Missing = "a per-context equivalence grid", Detail = note + " Zero effective pinned coverage. Set `equivalenceGrid` to the metric's natural grain axes, then re-submit." };
                    default:
                        return Unavailable(eq, note, shapes, cells);
                }
            }

            // Blocker 1 — the LEDGER-AWARE rungs, classified from PINNED evidence only.
            if (pinnedBad.Length > 0)
            {
                var lead = string.Join("; ", pinnedBad.Select(s => $"{s.ShapeId} ({s.MismatchCount})"));
                var eg = cells.Length == 0 ? "" : " e.g. " + string.Join("; ", cells.Take(3).Select(c =>
                    $"{SanitizeMismatchDisplay(c.Context)}: {SanitizeMismatchDisplay(c.ValueA)} vs {SanitizeMismatchDisplay(c.ValueB)}"));
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
                    Detail = "every evaluated shape is OPEN. Nothing is enforced, so there is nothing this gate can prove." + openNote + " Pin at least the diverging shape, then re-submit.",
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
                        Detail = pNote + " Zero effective pinned coverage. Pin the metric's grain axes (or set `equivalenceGrid` so the diverging shape is evaluated and pinned), then re-submit." + openNote,
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
