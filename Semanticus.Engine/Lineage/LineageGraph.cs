using System;
using System.Collections.Generic;
using System.Linq;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Utils;

namespace Semanticus.Engine.Lineage
{
    /// <summary>
    /// Assembles the offline (Phase-1) lineage graph and computes forward Impact + the reverse Safe-to-remove sweep,
    /// purely from TOM — the model-internal half of the "Measure Killer" feature. Every method runs ON the dispatcher
    /// thread (called inside <c>Session.ReadAsync</c>) because it walks live wrapper objects. Report usage can be
    /// composed by the report-aware operations; these base results stay explicitly model-only.
    ///
    /// The safe-to-remove sweep is deliberately CONSERVATIVE — it advises DELETION, so it must NEVER report "safe"
    /// for an object that is in use. It accounts for every model-side referencer scope: DAX dependents, relationship
    /// keys, sort-by, hierarchies, variations (date drill-down), AlternateOf (aggregations), GroupByColumns,
    /// Object-Level Security (a metadata control invisible to the DAX graph), RLS row filters, and calculation items
    /// (whose visibility TOM's AnyVisible cannot see). When a referencer's live-ness can't be determined offline it
    /// returns the tri-state "caution" verdict rather than claiming the object dead. Spec: docs/lineage-impact-plan.md.
    /// </summary>
    internal static class LineageGraph
    {
        public const string ModelOnlyCaveat =
            "Model-only. Published-report field usage is excluded from this result. A column or measure used only by a report " +
            "can still appear unused. Analyze the relevant reports or run an impact assessment before deleting.";

        public const string ImpactCaveat =
            "Model-only. Downstream published reports and visuals are excluded from this impact set. Run an impact assessment with the relevant report definitions before changing it.";

        // ---- Full graph (get_lineage) ----------------------------------------------------------------------------

        public static LineageResult Build(Model m)
        {
            var nodes = new List<LineageNode>();
            var nodeRefs = new HashSet<string>(StringComparer.Ordinal);
            void AddNode(LineageNode n) { if (!string.IsNullOrEmpty(n.Ref) && nodeRefs.Add(n.Ref)) nodes.Add(n); }
            var edges = new List<LineageEdge>();

            foreach (var t in m.Tables)
            {
                var tref = ObjectRefs.For(t);
                AddNode(new LineageNode { Ref = tref, Name = t.Name, Kind = TableKind(t), IsHidden = t.IsHidden });

                foreach (var c in t.Columns.Where(c => c.Type != ColumnType.RowNumber))
                {
                    var cref = ObjectRefs.For(c);
                    AddNode(new LineageNode { Ref = cref, Name = c.Name, Kind = c is CalculatedColumn ? "calcColumn" : "column", Table = t.Name, IsHidden = c.IsHidden });
                    edges.Add(new LineageEdge { From = tref, To = cref, Kind = "contains" });
                }
                foreach (var ms in t.Measures)
                {
                    var mref = ObjectRefs.For(ms);
                    AddNode(new LineageNode { Ref = mref, Name = ms.Name, Kind = "measure", Table = t.Name, IsHidden = ms.IsHidden });
                    edges.Add(new LineageEdge { From = tref, To = mref, Kind = "contains" });
                }
            }

            // DAX dependency edges (dependant -> dependency). Backfill a node for any dependency target that isn't
            // already a node (a user-defined Function/UDF or a Calendar) so the edge is SURFACED, not silently dropped
            // by the dangling-edge filter below.
            foreach (var dep in DaxDependantsForGraph(m))
            {
                var fromRef = ObjectRefs.For((ITabularObject)dep);
                if (string.IsNullOrEmpty(fromRef)) continue;
                foreach (var target in dep.DependsOn.Keys)
                {
                    var toRef = ObjectRefs.For(target);
                    if (string.IsNullOrEmpty(toRef) || fromRef == toRef) continue;
                    if (!nodeRefs.Contains(toRef))
                        AddNode(new LineageNode { Ref = toRef, Name = (target as ITabularNamedObject)?.Name, Kind = ObjectRefs.KindOf(target) });
                    edges.Add(new LineageEdge { From = fromRef, To = toRef, Kind = "dependsOn" });
                }
            }

            // Relationship edges (FK column -> PK column).
            foreach (var r in m.Relationships.OfType<SingleColumnRelationship>())
            {
                if (r.FromColumn == null || r.ToColumn == null) continue;
                edges.Add(new LineageEdge { From = ObjectRefs.For(r.FromColumn), To = ObjectRefs.For(r.ToColumn), Kind = "relationship" });
            }

            // Source -> table edges (pragmatic M resolution).
            var (sources, srcEdges) = MReferenceResolver.Resolve(m);
            foreach (var s in sources)
                AddNode(new LineageNode { Ref = s.Ref, Name = s.Name, Kind = s.Kind == "unresolved" ? "unresolved" : "source", Detail = s.Detail });
            foreach (var e in srcEdges)
                edges.Add(new LineageEdge { From = e.SourceRef, To = e.TableRef, Kind = "source" });

            return new LineageResult
            {
                Nodes = nodes.ToArray(),
                Edges = DistinctEdges(edges.Where(e => nodeRefs.Contains(e.From) && nodeRefs.Contains(e.To))),
                Caveat = ModelOnlyCaveat,
            };
        }

        // ---- Forward impact (the impactOf RPC; impact_assessment's modelImpact) --------------------------------

        public static ImpactResult Impact(Model m, string objRef)
        {
            var root = ObjectRefs.Resolve(m, objRef)
                ?? throw new InvalidOperationException($"{objRef} not found.");

            var ordered = WalkImpact(new List<ITabularObject> { root });
            return new ImpactResult
            {
                Root = ObjectRefs.For(root),
                RootName = (root as ITabularNamedObject)?.Name,
                RootKind = ObjectRefs.KindOf(root),
                Impacted = ordered,
                Measures = ordered.Count(n => n.Kind == "measure"),
                Columns = ordered.Count(n => n.Kind == "column" || n.Kind == "calcColumn"),
                Tables = ordered.Count(n => n.Kind == "table"),
                Relationships = ordered.Count(n => n.Kind == "relationship"),
                Other = ordered.Count(n => n.Kind != "measure" && n.Kind != "column" && n.Kind != "calcColumn" && n.Kind != "table" && n.Kind != "relationship"),
                Caveat = ImpactCaveat,
            };
        }

        /// <summary>Multi-source blast radius (the health probe's per-commit path): ONE BFS seeded with EVERY
        /// resolvable root — a single shared seen/queue makes the cost O(graph) regardless of how many roots a
        /// mega-batch commit produced, so there is no root cap and no truncation that could silently suppress a
        /// real blast radius (the old per-root loop walked only the first 64 roots in arbitrary delta order).
        /// All roots are seeded as SEEN before the walk, so a root downstream of another root is never counted as
        /// its impact — the same "an edited object is the change, not its impact" rule the single-root path applies.
        /// Unresolvable roots (deleted objects) are skipped — the same v1 posture as the single-root catch.</summary>
        public static ImpactNode[] ImpactFrom(Model m, IEnumerable<string> objRefs)
        {
            var roots = new List<ITabularObject>();
            foreach (var r in objRefs)
            {
                var o = ObjectRefs.Resolve(m, r);
                if (o != null) roots.Add(o);
            }
            return WalkImpact(roots);
        }

        // The shared BFS core: seeds every root (relationship roots contribute their endpoint columns — a
        // relationship has no DAX branch of its own; its impact surface is its endpoints and everything that
        // propagates through them), then walks DAX dependants + structural column usage + table containment.
        private static ImpactNode[] WalkImpact(IReadOnlyList<ITabularObject> roots)
        {
            var impacted = new List<ImpactNode>();
            var seen = new HashSet<ITabularObject>(roots);
            var queue = new Queue<(ITabularObject obj, int depth)>();

            foreach (var root in roots)
            {
                if (root is SingleColumnRelationship rel)
                {
                    foreach (var col in new[] { rel.FromColumn, rel.ToColumn })
                        if (col != null && seen.Add(col)) { impacted.Add(NodeOf(col, 1, "relationship")); queue.Enqueue((col, 1)); }
                }
                else
                {
                    queue.Enqueue((root, 0));
                }
            }

            while (queue.Count > 0)
            {
                var (obj, d) = queue.Dequeue();

                if (obj is IDaxObject dax)                              // transitive DAX dependants (+ RLS filters)
                {
                    foreach (var r in dax.ReferencedBy)
                    {
                        var ro = (ITabularObject)r;
                        if (seen.Add(ro))
                        {
                            impacted.Add(NodeOf(ro, d + 1, r is TablePermission ? "rls" : "dax", obj));
                            queue.Enqueue((ro, d + 1));
                        }
                    }
                }

                if (obj is Column col)                                  // structural dependants (columns only)
                {
                    foreach (var r in col.UsedInRelationships) if (seen.Add(r)) impacted.Add(NodeOf(r, d + 1, "relationship", obj));
                    foreach (var h in col.UsedInHierarchies) if (seen.Add(h)) impacted.Add(NodeOf(h, d + 1, "hierarchy", obj));
                    foreach (var sc in col.UsedInSortBy) if (seen.Add(sc)) { impacted.Add(NodeOf(sc, d + 1, "sortBy", obj)); queue.Enqueue((sc, d + 1)); }
                    foreach (var v in col.UsedInVariations) if (seen.Add(v)) impacted.Add(NodeOf(v, d + 1, "variation", obj));
                    foreach (var a in col.UsedInAlternateOfs) if (seen.Add(a)) impacted.Add(NodeOf(a, d + 1, "aggregation", obj));
                }

                if (obj is Table tbl)                                   // a table root impacts its own children (+ their dependants)
                {
                    foreach (var c in tbl.Columns.Where(c => c.Type != ColumnType.RowNumber))
                        if (seen.Add(c)) { impacted.Add(NodeOf(c, d + 1, "contains", obj)); queue.Enqueue((c, d + 1)); }
                    foreach (var ms in tbl.Measures)
                        if (seen.Add(ms)) { impacted.Add(NodeOf(ms, d + 1, "contains", obj)); queue.Enqueue((ms, d + 1)); }
                    if (tbl is CalculationGroupTable cg)
                        foreach (var ci in cg.CalculationItems)
                            if (seen.Add(ci)) { impacted.Add(NodeOf(ci, d + 1, "contains", obj)); queue.Enqueue((ci, d + 1)); }
                }
            }

            return impacted.OrderBy(n => n.Depth).ThenBy(n => n.Kind).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        // ---- Reverse safe-to-remove sweep (unused_objects) -------------------------------------------------------

        /// <summary>ADDED ON PURPOSE (round 3 of m11/m4-columns-fair, 26 September). Semanticus marks what it adds on
        /// purpose, a base measure the Plan wrote for a column or a time-intelligence measure it generated, with this
        /// annotation, whose value is the plain reason. Such an object is new, so nothing uses it yet, and the sweep used
        /// to offer it straight back for deletion ("Remove 35 unused things" became 36 on UAT lane n). It is never a
        /// candidate: not in unused_objects, not in the Plan's cleanup row, not removed by remove_safe_objects. The mark
        /// is set in the same undo step that adds the object, so undo takes both. A person can still delete it.</summary>
        public const string AddedOnPurposeAnnotation = "Semanticus_AddedOnPurpose";

        /// <summary>Why Semanticus added this object on purpose, or null when it did not.</summary>
        public static string AddedOnPurpose(ITabularObject obj)
        {
            try { return string.IsNullOrWhiteSpace((obj as IAnnotationObject)?.GetAnnotation(AddedOnPurposeAnnotation)) ? null : ((IAnnotationObject)obj).GetAnnotation(AddedOnPurposeAnnotation); }
            catch { return null; }
        }

        /// <summary>Mark an object as added on purpose, with the plain reason.</summary>
        public static void MarkAddedOnPurpose(ITabularObject obj, string reason)
        {
            if (obj is IAnnotationObject a && !string.IsNullOrWhiteSpace(reason)) a.SetAnnotation(AddedOnPurposeAnnotation, reason);
        }

        public static UnusedResult Unused(Model m) => Unused(m, null, ModelOnlyCaveat);

        // reportUsedRefs (optional): model object refs that published/local reports use — any candidate in this set is
        // excluded (a report uses it), closing the model-only blind spot for descriptive columns no measure references.
        public static UnusedResult Unused(Model m, ISet<string> reportUsedRefs, string caveat)
        {
            // Pre-index every NON-DAX structural column usage ONCE (O(R + C + H)) so per-column evaluation is an O(1)
            // set lookup — avoids the O(columns x relationships) rescan and covers all the structural referencer
            // scopes a column can have: relationship keys, sort-by targets, hierarchy levels, variation default
            // columns, aggregation (AlternateOf) base columns, and grouping (GroupByColumns) members.
            var structural = new HashSet<Column>();
            foreach (var r in m.Relationships.OfType<SingleColumnRelationship>())
            {
                if (r.FromColumn != null) structural.Add(r.FromColumn);
                if (r.ToColumn != null) structural.Add(r.ToColumn);
            }
            foreach (var t in m.Tables)
            {
                foreach (var c in t.Columns)
                {
                    if (c.SortByColumn != null) structural.Add(c.SortByColumn);
                    foreach (var v in c.Variations) if (v.DefaultColumn != null) structural.Add(v.DefaultColumn);
                    if (c.AlternateOf?.BaseColumn != null) structural.Add(c.AlternateOf.BaseColumn);
                    // A column mapped to a base column by its OWN aggregation is structural too: the mapping is how the
                    // aggregation table answers, and removing the column stops it answering (verify, 29 Sep, N15).
                    if (c.AlternateOf != null) structural.Add(c);
                    if (c.GroupByColumns != null) foreach (var gbc in c.GroupByColumns) structural.Add(gbc);
                }
                foreach (var h in t.Hierarchies)
                    foreach (var lvl in h.Levels) if (lvl.Column != null) structural.Add(lvl.Column);
            }

            // Dimension and date tables, by the same classifier the Plan's Map uses (LocalEngine.KindOf), so the two
            // never disagree about a table. Their visible columns are what people slice and filter by, which the
            // model never references, so "nothing uses it" is not "safe" there.
            var dimensions = new HashSet<Table>(m.Tables.Where(t =>
                LocalEngine.KindOf(t) is ModelPlanTableKinds.Dimension or ModelPlanTableKinds.DateTable));

            var items = new List<UnusedItem>();
            foreach (var t in m.Tables)
            {
                foreach (var ms in t.Measures)
                {
                    var v = EvaluateMeasure(ms, reportUsedRefs);
                    if (v != null) items.Add(v);
                }
                foreach (var c in t.Columns.Where(c => c.Type != ColumnType.RowNumber))
                {
                    var v = EvaluateColumn(c, structural, reportUsedRefs);
                    if (v != null && v.Verdict == "safe" && !c.IsHidden && !t.IsHidden && dimensions.Contains(t))
                    {
                        v.Verdict = "visibleAttribute";
                        v.Reason = VisibleAttributeReason;
                    }
                    if (v != null) items.Add(v);
                }
            }

            var ordered = items
                .OrderBy(i => i.Verdict == "safe" ? 0 : i.Verdict == "usedByUnusedOnly" ? 1 : i.Verdict == "caution" ? 2 : 3)
                .ThenBy(i => i.Table, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new UnusedResult
            {
                Items = ordered,
                SafeCount = ordered.Count(i => i.Verdict == "safe"),
                UsedByUnusedOnlyCount = ordered.Count(i => i.Verdict == "usedByUnusedOnly"),
                CautionCount = ordered.Count(i => i.Verdict == "caution"),
                VisibleAttributeCount = ordered.Count(i => i.Verdict == "visibleAttribute"),
                Caveat = caveat,
            };
        }

        /// <summary>The verdict's own sentence. It says what the verdict MEANS — Semanticus never sweeps a visible
        /// attribute away by itself — and it must not say the person cannot have it removed, because they can: an
        /// approved delete_if_unused (the Impact card's Propose removal, or add_plan_item + apply_plan) removes it,
        /// and so does delete_object. Only the sweep keeps it (dogfood round 2, D3). The old sentence said "not
        /// offered for removal" while sitting beside an enabled Propose removal button, and the apply then blamed a
        /// safety check that had not failed.</summary>
        internal const string VisibleAttributeReason =
            "Visible attribute, no model reference. People filter and group by columns like this in reports, Q&A and Copilot, so Semanticus never removes it on its own. Ask the people who use the model, or hide it first. If you decide it should go, propose its removal and approve that change.";

        private static UnusedItem EvaluateMeasure(Measure ms, ISet<string> reportUsedRefs)
        {
            // Only what TOM can delete on its own is ever a candidate (see EvaluateColumn).
            if (!ms.CanDelete()) return null;
            if (AddedOnPurpose(ms) != null) return null;   // Semanticus added it on purpose; nothing uses it YET
            if (reportUsedRefs != null && reportUsedRefs.Contains(ObjectRefs.For(ms))) return null;   // a report uses it
            var rb = ms.ReferencedBy;
            var deep = rb.Count == 0 ? null : rb.Deep();
            if (deep != null && (UsedInRls(deep) || UsedInCalcItem(deep) || AnyVisibleReferencer(deep))) return null;
            return Verdict(ms, "measure", ms.Table?.Name, ms.IsHidden, rb, deep,
                reportUsedRefs != null
                    ? "Nothing in the model uses this measure, and it was not found in the reports that were checked."
                    : "Nothing in the model uses this measure. No report has been checked.");
        }

        private static UnusedItem EvaluateColumn(Column c, HashSet<Column> structural, ISet<string> reportUsedRefs)
        {
            // ONLY WHAT TOM CAN DELETE ON ITS OWN IS A CANDIDATE. A column its table's formula makes (a calculated
            // table's, a field parameter's) cannot be deleted alone: TOM says CanDelete false and DeleteResolvedObject
            // refuses it. Offering one put a line on the Plan's cleanup that could never apply, and approving the set
            // put the whole set back (m11 M4 lane n). The remedy is the formula, which is not a removal.
            if (!c.CanDelete()) return null;
            if (AddedOnPurpose(c) != null) return null;   // Semanticus added it on purpose; nothing uses it YET
            // A CALCULATION GROUP'S OWN COLUMNS ARE THE GROUP'S INTERFACE, not data. The visible one is how a person
            // picks a calculation and the hidden Ordinal one is what sorts them; nothing in the model references
            // either by name, which is true and entirely beside the point. Calling the picker "safe" let the sweep
            // delete it, leaving a calculation group that exists and that nothing can filter by, and the model saved
            // that way (verifier, dogfood round 2). Structural, like a relationship key or a hierarchy level.
            if (c.Table is CalculationGroupTable) return null;
            // Hard STRUCTURAL use (independent of DAX): relationship key / sort-by / hierarchy / variation default /
            // aggregation base / grouping member. Any of these means the column is in use regardless of references.
            if (structural.Contains(c)) return null;
            // Object-Level Security is a governance control stored as METADATA (not DAX), so it never appears in
            // ReferencedBy — guard it explicitly so an OLS-secured column is never reported "safe to remove".
            if (HasObjectLevelSecurity(c)) return null;
            if (reportUsedRefs != null && reportUsedRefs.Contains(ObjectRefs.For(c))) return null;   // a report uses it

            var rb = c.ReferencedBy;
            var deep = rb.Count == 0 ? null : rb.Deep();
            if (deep != null && (UsedInRls(deep) || UsedInCalcItem(deep) || AnyVisibleReferencer(deep))) return null;
            return Verdict(c, c is CalculatedColumn ? "calcColumn" : "column", c.Table?.Name, c.IsHidden, rb, deep,
                reportUsedRefs != null
                    ? "Nothing in the model uses this column, and it was not found in the reports that were checked."
                    : "Nothing in the model uses this column. No report has been checked.");
        }

        private static UnusedItem Verdict(ITabularNamedObject obj, string kind, string table, bool hidden,
            ReferencedByList rb, HashSet<IDaxDependantObject> deep, string safeReason)
        {
            string verdict, reason;
            if (rb.Count == 0) { verdict = "safe"; reason = safeReason; }
            else if (deep != null && HasUnclassifiableReferencer(deep))
            {
                verdict = "caution";
                reason = "Used by a data-loading step. Open that step to check whether it needs this field.";
            }
            else
            {
                verdict = "usedByUnusedOnly";
                var by = ReferrerLabels(rb);
                reason = by.Length > 0
                    ? "Used by " + string.Join(", ", by.Take(3)) + ". No use of " + (by.Length == 1 ? "it" : "those") + " was found in these checks. Recheck after removing " + (by.Length == 1 ? "it" : "them") + "."
                    : "Used only by things that are themselves unused. Recheck after removing them.";
            }

            return new UnusedItem
            {
                Ref = ObjectRefs.For(obj), Name = obj.Name, Kind = kind, Table = table, IsHidden = hidden,
                Verdict = verdict, RefCount = rb.Count, BlockedBy = ReferrerLabels(rb), Reason = reason,
            };
        }

        // ---- Report-aware analysis (Phase 3 — local PBIR; cloud getDefinition feeds the SAME parser) -------------

        public static ReportAnalysisResult AnalyzeReports(Model m, IReadOnlyList<(string path, ReportDefinitionReader.ParseResult result)> parsed)
            => AnalyzeReports(m, parsed.Select(x => (x.path, (string)null, x.result)).ToList());

        // Error-aware overload (the cloud transport): each report may carry a scrubbed `error` (RDL/paginated, a
        // getDefinition blocked by an encrypted sensitivity label, or a fetch failure). An errored report counts as
        // unreadable and degrades the caveat — fail-loud, so the safe-to-remove verdict is never silently overstated.
        public static ReportAnalysisResult AnalyzeReports(Model m, IReadOnlyList<(string path, string error, ReportDefinitionReader.ParseResult result)> parsed)
            => AnalyzeReports(m, parsed, null);

        /// <summary>The same reading, with each report's stable CHOICE ID carried onto its usage. <paramref
        /// name="choiceIds"/> is positional with <paramref name="parsed"/>; a caller that has no choice ids passes
        /// null. Two chosen reports in different workspaces can share a display name, so anything that joins a
        /// reading back to a chosen report has to join on the id and never on the name.</summary>
        public static ReportAnalysisResult AnalyzeReports(Model m,
            IReadOnlyList<(string path, string error, ReportDefinitionReader.ParseResult result)> parsed,
            IReadOnlyList<string> choiceIds)
        {
            var reports = new List<ReportUsage>();
            var index = -1;
            var allUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unresolvedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int read = 0, unreadable = 0, totalUnresolved = 0, totalSkipped = 0, measuresRead = 0, measureRefsChecked = 0;

            foreach (var (path, error, pr) in parsed)
            {
                index++;
                var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var ext = new List<string>();
                foreach (var f in pr.Fields)
                {
                    if (f.IsExtension) { if (!string.IsNullOrEmpty(f.Property)) ext.Add(f.Property); continue; }
                    foreach (var modelRef in ReconcileAll(m, f))
                        resolved.Add(modelRef);   // a name miss just means the report uses a field not in THIS model — ignore
                }
                // The report's own measures: their DAX reads model names, and each one is a real use.
                var measureUsages = ReadReportMeasures(m, pr, resolved, unresolvedNames, ext, out var measureUnresolved, out var measureChecked);
                measuresRead += measureUsages.Length; measureRefsChecked += measureChecked;
                bool ok = pr.DefinitionFound && error == null;
                if (ok) read++; else unreadable++;
                foreach (var r in resolved) allUsed.Add(r);
                totalUnresolved += pr.Unresolved + measureUnresolved;
                foreach (var n in pr.UnresolvedNames) unresolvedNames.Add(n);
                totalSkipped += pr.SkippedParts;

                // Page+visual drill-down: group the page/visual-stamped occurrences (those that carry a page) and
                // reconcile each visual's distinct model refs. A visual that references no model field is dropped.
                var visuals = new List<ReportVisualUsage>();
                foreach (var grp in pr.Occurrences.Where(o => o.Page != null)
                             .GroupBy(o => (o.Page, o.Visual)))
                {
                    var vrefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    string vtype = null;
                    foreach (var f in grp)
                    {
                        if (f.VisualType != null) vtype = f.VisualType;
                        if (f.IsExtension) continue;
                        foreach (var mr in ReconcileAll(m, f)) vrefs.Add(mr);
                    }
                    if (vrefs.Count == 0) continue;
                    visuals.Add(new ReportVisualUsage
                    {
                        Page = grp.Key.Page, Visual = grp.Key.Visual, VisualType = vtype,
                        UsedRefs = vrefs.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
                    });
                }

                reports.Add(new ReportUsage
                {
                    Path = path, Name = ReportName(path), Read = ok,
                    ChoiceId = choiceIds != null && index < choiceIds.Count ? choiceIds[index] : null,
                    FieldCount = resolved.Count, Unresolved = pr.Unresolved + measureUnresolved,
                    ReportMeasures = measureUsages,
                    UsedRefs = resolved.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
                    ExtensionMeasures = ext.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
                    Visuals = visuals.OrderBy(v => v.Page, StringComparer.OrdinalIgnoreCase).ThenBy(v => v.Visual, StringComparer.OrdinalIgnoreCase).ToArray(),
                    Error = error,
                });
            }

            // Coverage honesty (fail-loud): a "safe to remove" verdict is only trustworthy when report coverage is
            // COMPLETE. Two ways it isn't — (a) some reports couldn't be read (paginated/RDL, sensitivity-label block,
            // fetch error) so a field they use is invisible; (b) reports WERE read but NONE of their fields matched the
            // open model (read > 0 yet allUsed empty) — almost always the wrong model is open, so the "usage" the
            // caveat claims is illusory. In either case a would-be "safe" item must NOT assert safe.
            bool nothingMatched = read > 0 && allUsed.Count == 0;
            // Coverage is incomplete when some reports couldn't be read at all (unreadable), when NONE of the read
            // fields matched the open model (nothingMatched = wrong model), OR when a read report had PARTS we couldn't
            // parse/read (totalSkipped) — a field used only in a skipped part is invisible, so "safe" would overstate.
            // (Unresolved refs do NOT blanket-demote — measured near-zero on well-formed PBIR — but they are REAL
            // usages we couldn't place, so the NAME-matching would-be-safe items are demoted individually below.)
            bool coverageIncomplete = read > 0 && (unreadable > 0 || nothingMatched || totalSkipped > 0);

            var caveat = read == 0
                ? "No readable PBIR report definition was found. (Legacy .pbix / PBIRLegacy / paginated reports are not parsed.) The safe-to-remove list below is MODEL-ONLY."
                : $"Includes field usage from {read} report(s)"
                  + (unreadable > 0 ? $"; {unreadable} report(s) could not be read (paginated/RDL, blocked by a sensitivity label, or unreadable)" : "")
                  + (totalSkipped > 0 ? $"; {totalSkipped} report part(s) could not be parsed" : "")
                  + (totalUnresolved > 0 ? $"; {totalUnresolved} report reference(s) could not be attributed (verify 'safe' items)" : "")
                  + (nothingMatched ? "; but NONE of their fields matched the open model (likely the wrong model is open), so the list below is effectively model-only" : "")
                  + ". A field used only by a report NOT included here can still appear as unused.";

            var unused = Unused(m, read == 0 ? null : allUsed, caveat);
            // Demote bare "safe" to "caution" when coverage is incomplete — the machine-readable verdict (what BOTH
            // doors act on) must never overstate safe just because the prose caveat warns. usedByUnusedOnly/caution stay.
            if (coverageIncomplete)
                unused = DemoteSafeToCaution(unused, nothingMatched
                    ? "Report coverage is incomplete: no report field matched the open model (likely the wrong model is open), so a report may use this. Verify before removing."
                    : "Report coverage is incomplete: a report definition could not be fully read or parsed, so a report may use this. Verify before removing.");
            // The unresolved RESIDUE: a reference whose table we could not place still carries a parseable NAME. A
            // would-be-safe item with that name may be the very field the report uses (a filter card, a formatting
            // measure), so it must not assert "safe" — demote the name-matches only, a scalpel where the blanket
            // demote above would erase "safe" from the product.
            if (read > 0 && unresolvedNames.Count > 0)
                unused = DemoteUnresolvedNameMatchesToCaution(unused, unresolvedNames);

            return new ReportAnalysisResult
            {
                Reports = reports.ToArray(),
                ReportsRead = read,
                ReportsUnreadable = unreadable,
                ModelFieldsUsed = allUsed.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
                Unused = unused,
                Caveat = caveat,
                ReportMeasuresRead = measuresRead,
                ReportMeasureReferencesChecked = measureRefsChecked,
            };
        }

        /// <summary>Which of these reports (and which of their report measures) use a model object, for a warning that
        /// names them. <paramref name="touches"/> says whether a resolved object ref is the object, or part of it (a
        /// table's columns, a hierarchy's level columns). Called BEFORE the change, while the names still match.
        /// Cheap on purpose: it resolves references only and never runs the unused sweep.</summary>
        internal static List<(string Report, string[] ReportMeasures)> ReportUsesOf(Model m,
            IReadOnlyList<(string path, string error, ReportDefinitionReader.ParseResult result)> parsed, Func<string, bool> touches)
        {
            var hits = new List<(string, string[])>();
            foreach (var (path, _, pr) in parsed)
            {
                var direct = pr.Fields.Any(f => !f.IsExtension && ReconcileAll(m, f).Any(touches));
                var measures = ReadReportMeasures(m, pr, new HashSet<string>(), new HashSet<string>(), new List<string>(), out _, out _)
                    .Where(u => u.UsedRefs.Any(touches)).Select(u => u.Name).ToArray();
                if (direct || measures.Length > 0) hits.Add((ReportName(path), measures));
            }
            return hits.OrderBy(h => h.Item1, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // Resolve what each report measure's DAX reads against the open model (by name, as every report reference is).
        // Table[Name] is a column or a measure of that table; a bare [Name] is a measure, else a column (every table's
        // column of that name counts: over-protecting is the safe error). Another report measure of this report is not a
        // gap. A name that matches nothing is a real use we could not place: it goes to UnresolvedNames, the same
        // caution path an unattributable visual field takes, so a would-be-safe object of that name is never "safe".
        private static ReportMeasureUsage[] ReadReportMeasures(Model m, ReportDefinitionReader.ParseResult pr, HashSet<string> resolved,
            HashSet<string> unresolvedNames, List<string> ext, out int unresolvedCount, out int checkedCount)
        {
            unresolvedCount = 0; checkedCount = 0;
            if (pr.ReportMeasures.Count == 0) return Array.Empty<ReportMeasureUsage>();
            var siblings = new HashSet<string>(pr.ReportMeasures.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
            var usages = new List<ReportMeasureUsage>();
            foreach (var rm in pr.ReportMeasures)
            {
                ext.Add(rm.Name);
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int unresolved = 0;
                foreach (var r in rm.Refs)
                {
                    checkedCount++;
                    var hits = ResolveName(m, r).ToList();
                    if (hits.Count > 0) { foreach (var h in hits) used.Add(h); continue; }
                    if (r.MaybeOwn || (r.Table == null && siblings.Contains(r.Name))
                        || (r.Table != null && siblings.Contains(r.Name) && pr.ReportMeasures.Any(x => string.Equals(x.Name, r.Name, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(x.Entity, r.Table, StringComparison.OrdinalIgnoreCase)))) continue;
                    unresolved++; unresolvedNames.Add(r.Name);
                }
                unresolvedCount += unresolved;
                foreach (var u in used) resolved.Add(u);
                usages.Add(new ReportMeasureUsage
                {
                    Name = rm.Name, ReferencesChecked = rm.Refs.Count, Unresolved = unresolved,
                    UsedRefs = used.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
                });
            }
            return usages.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static IEnumerable<string> ResolveName(Model m, ReportMeasureReferences.NameRef r)
        {
            if (r.Table != null)
            {
                if (!m.Tables.Contains(r.Table)) yield break;
                var t = m.Tables[r.Table];
                var col = t.Columns.FirstOrDefault(x => string.Equals(x.Name, r.Name, StringComparison.OrdinalIgnoreCase));
                if (col != null) yield return ObjectRefs.For(col);
                var me = t.Measures.FirstOrDefault(x => string.Equals(x.Name, r.Name, StringComparison.OrdinalIgnoreCase));
                if (me != null) yield return ObjectRefs.For(me);
                yield break;
            }
            var measure = m.Tables.SelectMany(t => t.Measures).FirstOrDefault(x => string.Equals(x.Name, r.Name, StringComparison.OrdinalIgnoreCase));
            if (measure != null) { yield return ObjectRefs.For(measure); yield break; }
            foreach (var t in m.Tables)
                foreach (var c in t.Columns)
                    if (string.Equals(c.Name, r.Name, StringComparison.OrdinalIgnoreCase)) yield return ObjectRefs.For(c);
        }

        // When report coverage is incomplete, a "safe" verdict can't be honoured (a report we couldn't read — or the
        // reports for a different model — might use the field). Flip those items to the tri-state "caution" (the same
        // posture the offline sweep uses for an unclassifiable referencer) and recount, so neither door can act on a
        // false "safe". Leaves usedByUnusedOnly / already-caution items untouched.
        /// <summary>R1: a chosen report that has not been read is MISSING coverage, and a sweep computed without it
        /// must not present its "safe" set as safe. This is the same demotion an unreadable report already causes;
        /// it is internal so the report scope can apply it for a report nobody has read yet.</summary>
        internal static UnusedResult WithMissingCoverage(UnusedResult r, string reason, string caveat)
        {
            var demoted = DemoteSafeToCaution(r, reason);
            demoted.Caveat = string.IsNullOrEmpty(caveat) ? demoted.Caveat : caveat;
            return demoted;
        }

        private static UnusedResult DemoteSafeToCaution(UnusedResult r, string reason)
        {
            foreach (var i in r.Items)
                if (i.Verdict is "safe" or "visibleAttribute") { i.Verdict = "caution"; i.Reason = reason; }   // a visible attribute is only never swept; a report that may use it still protects it
            return Recount(r);
        }

        // The targeted variant: only would-be-safe items whose NAME matches an unresolved report reference demote —
        // the reference is a real usage whose table we couldn't place, and this item may be its target.
        private static UnusedResult DemoteUnresolvedNameMatchesToCaution(UnusedResult r, ISet<string> unresolvedNames)
        {
            foreach (var i in r.Items)
                if ((i.Verdict is "safe" or "visibleAttribute") && i.Name != null && unresolvedNames.Contains(i.Name))
                {
                    i.Verdict = "caution";
                    i.Reason = $"A report references something named '{i.Name}' that could not be fully attributed, and it may be this object. Verify before removing.";
                }
            return Recount(r);
        }

        private static UnusedResult Recount(UnusedResult r) => new UnusedResult
        {
            Items = r.Items,
            SafeCount = r.Items.Count(i => i.Verdict == "safe"),
            UsedByUnusedOnlyCount = r.Items.Count(i => i.Verdict == "usedByUnusedOnly"),
            CautionCount = r.Items.Count(i => i.Verdict == "caution"),
            VisibleAttributeCount = r.Items.Count(i => i.Verdict == "visibleAttribute"),
            Caveat = r.Caveat,
        };

        // Map a parsed (Entity, Property, kind) reference to a model object ref BY NAME (TOM is the source of truth —
        // the report layer has no LineageTag). Returns null when no model object matches (a different/renamed field).
        // A report reference resolves to 0..n model objects: a column/measure to at most one; a HIERARCHY to each of
        // its levels' columns. The report names only hierarchy (+ level) — which columns that binds is a MODEL fact,
        // so it resolves here; ALL level columns are protected (deleting any of them alters or errors every visual
        // bound to the hierarchy). Level columns are also structurally protected in Unused(), so the value here is
        // the usage attribution both doors read (visuals drill-down, impact, the report layer on the graph).
        private static IEnumerable<string> ReconcileAll(Model m, ReportDefinitionReader.FieldRef f)
        {
            if (f.Kind == "hierarchy")
            {
                if (string.IsNullOrEmpty(f.Entity) || string.IsNullOrEmpty(f.Property) || !m.Tables.Contains(f.Entity)) yield break;
                var h = m.Tables[f.Entity].Hierarchies.FirstOrDefault(x => string.Equals(x.Name, f.Property, StringComparison.OrdinalIgnoreCase));
                if (h == null) yield break;   // a name miss (hierarchy not in THIS model) — same posture as a field miss
                foreach (var lvl in h.Levels) if (lvl.Column != null) yield return ObjectRefs.For(lvl.Column);
                yield break;
            }
            var one = Reconcile(m, f);
            if (one != null) yield return one;
        }

        private static string Reconcile(Model m, ReportDefinitionReader.FieldRef f)
        {
            if (string.IsNullOrEmpty(f.Property)) return null;
            if (f.Kind == "measure")
            {
                // Prefer the measure on its stated home table; else any measure of that name (measures are model-global).
                Measure hit = null;
                if (!string.IsNullOrEmpty(f.Entity) && m.Tables.Contains(f.Entity))
                    hit = m.Tables[f.Entity].Measures.FirstOrDefault(x => string.Equals(x.Name, f.Property, StringComparison.OrdinalIgnoreCase));
                hit = hit ?? m.Tables.SelectMany(t => t.Measures).FirstOrDefault(x => string.Equals(x.Name, f.Property, StringComparison.OrdinalIgnoreCase));
                return hit != null ? ObjectRefs.For(hit) : null;
            }
            if (!string.IsNullOrEmpty(f.Entity) && m.Tables.Contains(f.Entity))
            {
                var col = m.Tables[f.Entity].Columns.FirstOrDefault(x => string.Equals(x.Name, f.Property, StringComparison.OrdinalIgnoreCase));
                return col != null ? ObjectRefs.For(col) : null;
            }
            return null;
        }

        private static string ReportName(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            var name = System.IO.Path.GetFileName(path.TrimEnd('/', '\\'));
            return string.IsNullOrEmpty(name) ? path : name;
        }

        // ---- referencer-visibility helpers (all take a pre-computed transitive Deep() set; one walk per object) ---

        // Referenced (directly or transitively) by an RLS row-filter — a governance use that AnyVisible can't see
        // (a TablePermission isn't a hideable object). Guards a false "safe".
        private static bool UsedInRls(HashSet<IDaxDependantObject> deep) => deep.OfType<TablePermission>().Any();

        // Referenced by a calculation item — a live use AnyVisible can't see (CalculationItem is IDaxDependantObject
        // but NOT IHideableObject, so it never counts as "visible"). Calc groups are commonly hidden, which made the
        // whole chain read as dead. Guards a false "usedByUnusedOnly"/"safe".
        private static bool UsedInCalcItem(HashSet<IDaxDependantObject> deep) => deep.OfType<CalculationItem>().Any();

        // TOM's ReferencedByList.AnyVisible applies COLUMN visibility (table-hidden => invisible) to EVERY referencer,
        // which wrongly demotes a measure on a hidden _Measures/KPIs home table (a measure is visible iff !IsHidden,
        // regardless of its table — Measure.IsVisible). Re-evaluate with the correct per-kind rule.
        private static bool AnyVisibleReferencer(HashSet<IDaxDependantObject> deep)
        {
            foreach (var o in deep)
            {
                if (o is Measure msr) { if (!msr.IsHidden) return true; continue; }       // measure: !IsHidden (table irrelevant)
                if ((o as KPI)?.Measure is Measure km && !km.IsHidden) return true;        // KPI rides its measure's visibility
                if ((o as IHideableObject)?.IsHidden == false
                    && (o as ITabularTableObject)?.Table?.IsHidden == false) return true;  // column-style: object + table visible
            }
            return false;
        }

        // After RLS / calc-item / visible checks have cleared the object from candidacy, anything left in the
        // transitive referencer set that we can't classify for visibility (not a hideable object, not a KPI) —
        // chiefly a Partition's DataCoverageDefinition — means we cannot honestly call the object dead => CAUTION.
        private static bool HasUnclassifiableReferencer(HashSet<IDaxDependantObject> deep)
            => deep.Any(o => !(o is IHideableObject) && !(o is KPI));

        // Any role applies non-default column-level OLS to this column (CL>=1400 only; the indexer is null below that).
        private static bool HasObjectLevelSecurity(Column c)
        {
            var ols = c.ObjectLevelSecurity;
            if (ols == null) return false;
            return c.Model.Roles.Any(r => ols[r] != MetadataPermission.Default);
        }

        // ---- shared helpers --------------------------------------------------------------------------------------

        private static string[] ReferrerLabels(ReferencedByList rb) =>
            rb.OfType<ITabularNamedObject>()
              .Select(o => o.Name)
              .Where(s => !string.IsNullOrEmpty(s))
              .Distinct(StringComparer.OrdinalIgnoreCase)
              .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
              .Take(12)
              .ToArray();

        // `through` is the object this one was reached THROUGH, at depth > 1. The page says "via Margin" with it,
        // instead of the old "depth 2" - which told a person nothing they could act on.
        private static ImpactNode NodeOf(ITabularObject o, int depth, string via, ITabularObject through = null) => new ImpactNode
        {
            Ref = ObjectRefs.For(o),
            Name = (o as ITabularNamedObject)?.Name,
            Kind = o is CalculatedColumn ? "calcColumn" : ObjectRefs.KindOf(o),   // match the graph-side kind + UI glyph
            Depth = depth,
            Via = via,
            ViaName = depth > 1 ? (through as ITabularNamedObject)?.Name : null,
            Table = (o as ITabularTableObject)?.Table?.Name,
        };

        private static string TableKind(Table t) =>
            t is CalculationGroupTable ? "calcGroup" : t is CalculatedTable ? "calcTable" : "table";

        // Dependants whose DependsOn endpoints are graph nodes (calc tables / calc columns / measures).
        private static IEnumerable<IDaxDependantObject> DaxDependantsForGraph(Model m)
        {
            foreach (var t in m.Tables)
            {
                if (t is IDaxDependantObject td) yield return td;                  // calculated tables
                foreach (var c in t.Columns)
                    if (c is IDaxDependantObject cd) yield return cd;              // calculated columns
                foreach (var ms in t.Measures) yield return ms;                   // measures
            }
        }

        // Dedup by the (kind, from, to) tuple — no string delimiter, so an object name containing '/' or ':' can
        // never cause a boundary-ambiguous key collision (which would silently drop a distinct edge).
        private static LineageEdge[] DistinctEdges(IEnumerable<LineageEdge> edges)
        {
            var seen = new HashSet<(string, string, string)>();
            var outp = new List<LineageEdge>();
            foreach (var e in edges)
                if (seen.Add((e.Kind, e.From, e.To))) outp.Add(e);
            return outp.ToArray();
        }
    }
}
