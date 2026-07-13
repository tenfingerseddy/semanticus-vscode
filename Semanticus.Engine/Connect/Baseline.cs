using System;
using System.Collections.Generic;
using System.Linq;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    // ============================================================================================
    // Value-capture-at-edit-start (Verified Edits, the RESTRUCTURE pipeline's load-bearing primitive
    // — docs/verified-edits-plan.md "Honest gaps" #2). capture_baseline freezes the MEASURED values
    // of the blast radius (the lineage-downstream measures of an object about to change) over an
    // agent-chosen SUMMARIZECOLUMNS grid; compare_baseline re-evaluates the same grid on the current
    // model and reports exactly which numbers moved. Measures are evaluated BY REFERENCE ([Name]),
    // not by frozen body — after a structural edit (rename + FormulaFixup, relationship change) the
    // question is "does the MEASURE still produce the same numbers", not "does the old body". Same
    // honesty rules as equivalence: Truncated coverage is disclosed (a match on a truncated grid is
    // not proof), a vanished measure is an IMPACT (verdict "missing"), never silently skipped, and
    // because both halves read the LIVE model, compare discloses session edits made since capture —
    // an UNDEPLOYED local edit is not validated by an "unchanged" verdict (no false-safe).
    // ============================================================================================

    /// <summary>One captured measure in a baseline (wire view — values stay engine-held).</summary>
    public sealed class BaselineEntryInfo
    {
        public string Ref { get; set; }
        public string Name { get; set; }
        public int RowCount { get; set; }
        public bool Truncated { get; set; }
        public string Error { get; set; }              // capture-time eval error (kept, honest — compare marks it not-comparable)
    }

    public sealed class BaselineCaptureResult
    {
        public string Status { get; set; }             // ok | no-connection | error
        public string CaptureId { get; set; }
        public string When { get; set; }               // UTC ISO-8601
        public long Revision { get; set; }             // session revision at capture (the "edit-start" anchor)
        public string Root { get; set; }               // the object whose blast radius was captured
        public string[] GroupBy { get; set; } = Array.Empty<string>();
        public string[] Filters { get; set; } = Array.Empty<string>();
        public BaselineEntryInfo[] Entries { get; set; } = Array.Empty<BaselineEntryInfo>();
        public string[] Skipped { get; set; } = Array.Empty<string>();   // over-cap refs — reported, never silent
        public string Message { get; set; }
    }

    /// <summary>Per-measure compare verdict. "moved" carries the exact contexts + before→after values.</summary>
    public sealed class BaselineDiff
    {
        public string Ref { get; set; }
        public string Name { get; set; }
        public string Verdict { get; set; }            // unchanged | moved | missing | not-comparable | error
        public int RowsCompared { get; set; }
        public int MismatchCount { get; set; }
        public bool Truncated { get; set; }            // either side hit the row cap — a match is NOT proof
        public EquivalenceMismatch[] Mismatches { get; set; } = Array.Empty<EquivalenceMismatch>();   // ValueA=captured, ValueB=now
        public string Note { get; set; }
    }

    public sealed class BaselineCompareResult
    {
        public string Status { get; set; }             // ok | no-connection | not-found | error
        public string CaptureId { get; set; }
        public string Root { get; set; }
        public string CapturedWhen { get; set; }
        public long CapturedRevision { get; set; }
        public long ComparedRevision { get; set; }
        public bool Safe { get; set; }                 // every measure unchanged, none missing/errored, coverage complete
        public int MovedCount { get; set; }
        public int MissingCount { get; set; }
        public BaselineDiff[] Diffs { get; set; } = Array.Empty<BaselineDiff>();
        public string Message { get; set; }
    }

    // ---- engine-held state (values never cross the wire whole; compare reports the diff) ----------

    internal sealed class BaselineEntryState
    {
        public string Ref;
        public string Name;
        public string LineageTag;                         // rename-safe identity; preferred over a reused old name
        public Dictionary<string, object> Rows;        // context key -> measured value (null = BLANK)
        public bool Truncated;
        public string Error;
    }

    internal sealed class BaselineState
    {
        public string Id;
        public DateTime WhenUtc;
        public long Revision;
        public string Root;
        public string[] GroupBy;
        public string[] Filters;
        public List<BaselineEntryState> Entries = new List<BaselineEntryState>();
    }

    /// <summary>Session-held captures (PlanStore's pattern: guarded by the engine's _baselineGate, no
    /// internal lock). Bounded: the oldest capture is dropped past <see cref="MaxHeld"/> — the drop is
    /// returned so the caller can REPORT it (no silent caps).</summary>
    internal sealed class BaselineStore
    {
        public const int MaxHeld = 8;
        private readonly List<BaselineState> _captures = new List<BaselineState>();

        public string Add(BaselineState s)
        {
            _captures.Add(s);
            if (_captures.Count <= MaxHeld) return null;
            var dropped = _captures[0].Id;
            _captures.RemoveAt(0);
            return dropped;
        }

        /// <summary>Null/empty id = the most recent capture (the common "I just captured, now compare" flow).</summary>
        public BaselineState Get(string id)
            => string.IsNullOrEmpty(id) ? (_captures.Count > 0 ? _captures[_captures.Count - 1] : null)
                                        : _captures.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));

        public int Count => _captures.Count;

        /// <summary>Drop all held captures — called when the model is replaced, so a baseline frozen against the
        /// PREVIOUS model can never be compared against a newly-opened one (a false regression/pass).</summary>
        public void Clear() => _captures.Clear();
    }

    internal static class Baseline
    {
        /// <summary>Resolve a captured measure after a structural edit. A lineage tag survives a rename and wins
        /// over an impostor later created at the old name; tag collisions are refused rather than guessed.</summary>
        internal static (Measure Measure, string Error) ResolveMeasure(Model m, BaselineEntryState entry)
        {
            if (!string.IsNullOrWhiteSpace(entry?.LineageTag))
            {
                var matches = m.Tables.SelectMany(t => t.Measures)
                    .Where(x => string.Equals(x.LineageTag, entry.LineageTag, StringComparison.Ordinal)).ToArray();
                if (matches.Length == 1) return (matches[0], null);
                if (matches.Length > 1) return (null, $"lineage tag '{entry.LineageTag}' resolves to {matches.Length} measures; refusing to guess after the rename");
                return (null, null);
            }
            return (ObjectRefs.Resolve(m, entry?.Ref) as Measure, null);
        }

        /// <summary>The blast radius as capture targets: the object itself when it's a measure, then every
        /// lineage-downstream measure (transitive, LineageGraph.Impact). Deterministic order, capped with the
        /// overflow REPORTED. Throws on an unresolvable ref (same contract as the other ref-taking ops).</summary>
        public static (List<(string Ref, string Name)> Targets, List<string> Skipped) Targets(Model m, string objRef, bool includeDependents, int maxMeasures)
        {
            var root = ObjectRefs.Resolve(m, objRef);   // throws with the standard message when not found
            var targets = new List<(string Ref, string Name)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (root is Measure rm && seen.Add(ObjectRefs.For(rm))) targets.Add((ObjectRefs.For(rm), rm.Name));
            if (includeDependents)
            {
                foreach (var n in Lineage.LineageGraph.Impact(m, objRef).Impacted.Where(n => n.Kind == "measure"))
                    if (seen.Add(n.Ref)) targets.Add((n.Ref, n.Name));
            }
            var skipped = new List<string>();
            if (maxMeasures > 0 && targets.Count > maxMeasures)
            {
                skipped.AddRange(targets.Skip(maxMeasures).Select(t => t.Ref + " (over the " + maxMeasures + "-measure cap)"));
                targets = targets.Take(maxMeasures).ToList();
            }
            return (targets, skipped);
        }

        /// <summary>A measure evaluated by REFERENCE, bracket-escaped ("]"→"]]").</summary>
        public static string MeasureRefExpr(string name) => "[" + (name ?? "").Replace("]", "]]") + "]";

        /// <summary>Key a probe ResultSet into context→value. Columns: [axis…], v, __present (BuildProbeQuery's
        /// shape — the sentinel keeps all-blank rows). Context key mirrors DaxBench's mismatch format.</summary>
        public static Dictionary<string, object> KeyRows(ResultSet rs, int axisCount)
        {
            var rows = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var r in rs.Rows)
            {
                var key = axisCount > 0
                    ? string.Join(", ", Enumerable.Range(0, axisCount).Select(k => $"{rs.Columns[k].Name}={DaxBench.Fmt(r[k])}"))
                    : "(model context)";
                rows[key] = r.Length > axisCount ? r[axisCount] : null;
            }
            return rows;
        }

        /// <summary>The pure diff: union of contexts, values compared with equivalence semantics
        /// (DaxBench.ValuesEqual — numeric tolerance, blank≠0). A context present on only one side IS a
        /// moved number ("(no row)" on the other side).</summary>
        public static BaselineDiff Diff(BaselineEntryState captured, Dictionary<string, object> current, bool currentTruncated)
        {
            var mismatches = new List<EquivalenceMismatch>();
            var keys = captured.Rows.Keys.Union(current.Keys, StringComparer.Ordinal).ToList();
            foreach (var key in keys)
            {
                var hasBefore = captured.Rows.TryGetValue(key, out var before);
                var hasAfter = current.TryGetValue(key, out var after);
                if (hasBefore && hasAfter && DaxBench.ValuesEqual(before, after)) continue;
                if (mismatches.Count < 20)
                    mismatches.Add(new EquivalenceMismatch
                    {
                        Context = key,
                        ValueA = hasBefore ? DaxBench.Fmt(before) : "(no row)",
                        ValueB = hasAfter ? DaxBench.Fmt(after) : "(no row)",
                    });
                else break;
            }
            var moved = keys.Count(k =>
            {
                var hb = captured.Rows.TryGetValue(k, out var b);
                var ha = current.TryGetValue(k, out var a);
                return !(hb && ha && DaxBench.ValuesEqual(b, a));
            });
            var truncated = captured.Truncated || currentTruncated;
            return new BaselineDiff
            {
                Ref = captured.Ref,
                Name = captured.Name,
                Verdict = moved == 0 ? "unchanged" : "moved",
                RowsCompared = keys.Count,
                MismatchCount = moved,
                Truncated = truncated,
                Mismatches = mismatches.ToArray(),
                Note = truncated ? "coverage incomplete (row cap hit) — a match here is evidence, not proof" : null,
            };
        }
    }
}
