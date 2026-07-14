using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TOM = Microsoft.AnalysisServices.Tabular;
using AS = Microsoft.AnalysisServices;

namespace Semanticus.Engine
{
    /// <summary>
    /// ALM Toolkit / BISM Normalizer-grade model schema compare, on RAW TOM (NOT the TOMWrapper — so loading a
    /// second model to compare never clobbers the open session's process-wide wrapper Singleton). Generalises
    /// LiveDeploy's LineageTag+name match to any-source ↔ any-source across the full object set, classifying each
    /// object Create / Update / Delete / Equal, with a selective apply. Direction: Action describes what would
    /// change in the RIGHT (target) to make it match the LEFT (source) — the same "deploy source onto target" sense.
    ///
    /// Refs use the ONE ALM grammar (<see cref="AlmRef"/>), escaped per-component so a name containing '/' or ':' can
    /// never make two distinct objects share a ref (which used to tick/delete both). Apply resolves the TARGET object
    /// by the matched right object's IDENTITY (LineageTag), carried on the item — not by the source-keyed name — so a
    /// source table renamed on the target never lands a child on an unrelated same-source-named object, and a
    /// lineage-matched rename applies as a rename rather than duplicate+orphan. A duplicate target tag is surfaced as
    /// Ambiguous (never applied, never deleted) instead of silently first-wins.
    /// </summary>
    internal static class ModelCompare
    {
        // ---- load a raw TOM model from disk (TMDL folder / PBIP / .bim) ----
        internal static TOM.Model LoadRawModel(string path) => LoadRawModelDb(path).Model;

        internal static TOM.Database LoadRawModelDb(string path, int knownCompatibilityLevel = 0)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A model path is required.");
            // A path pasted WITH surrounding quotes ("C:\...\model.tmdl") is otherwise treated as RELATIVE by
            // Path.GetFullPath — it resolves against the process CWD (the VS Code install dir) and fails with a
            // "No TMDL/.bim model found at ...\Microsoft VS Code"C:\...\model.tmdl"" path. A double quote is an illegal
            // Windows filename character, so trimming quotes (and surrounding whitespace) is always safe.
            path = path.Trim().Trim('"').Trim();
            var full = Path.GetFullPath(path);
            if (File.Exists(full) && full.EndsWith(".bim", StringComparison.OrdinalIgnoreCase))
                return TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(full), null, AS.CompatibilityMode.PowerBI);
            if (Directory.Exists(full))
            {
                var tmdl = ResolveTmdlFolder(full);
                if (tmdl != null)
                {
                    var db = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(tmdl);
                    // DeserializeDatabaseFromFolder leaves CompatibilityMode = Unknown, so TOM serialization validates
                    // Power-BI-only properties (a calendar RelatedColumnDetails column, CL 1701+) in AnalysisServices
                    // mode and throws "compatibility level of -2 is below the minimal compatibility level of -2" (a
                    // mode-unsupported sentinel, not a real level gap). Force PowerBI to match BOTH .bim branches, which
                    // set it unconditionally — this is a Power BI workbench, and it keeps all three load paths (and so
                    // the two compare sides) on ONE mode rather than mixing PowerBI with a determined AnalysisServices.
                    // The == Unknown guard respects an already-declared mode if a TMDL folder ever carries one.
                    if (db.CompatibilityMode == AS.CompatibilityMode.Unknown)
                        db.CompatibilityMode = AS.CompatibilityMode.PowerBI;
                    if (db.CompatibilityLevel <= 0 && knownCompatibilityLevel > 0)
                        db.CompatibilityLevel = knownCompatibilityLevel;
                    return db;
                }
                var bim = Path.Combine(full, "model.bim");
                if (File.Exists(bim)) return TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(bim), null, AS.CompatibilityMode.PowerBI);
            }
            throw new InvalidOperationException("No TMDL/.bim model found at: " + full);
        }

        // Serialize a raw TOM model back to disk in the SAME format it was loaded from (for apply-to-file).
        internal static void SaveRawModel(TOM.Database db, string path)
        {
            var full = Path.GetFullPath(path);
            if ((File.Exists(full) && full.EndsWith(".bim", StringComparison.OrdinalIgnoreCase)))
            {
                File.WriteAllText(full, TOM.JsonSerializer.SerializeDatabase(db));
                return;
            }
            var tmdl = Directory.Exists(full) ? (ResolveTmdlFolder(full) ?? full) : full;
            TOM.TmdlSerializer.SerializeDatabaseToFolder(db, tmdl);
        }

        private static string ResolveTmdlFolder(string dir)
        {
            if (File.Exists(Path.Combine(dir, "database.tmdl")) || File.Exists(Path.Combine(dir, "model.tmdl"))) return dir;
            var def = Path.Combine(dir, "definition");
            if (Directory.Exists(def)) return def;
            return null;
        }

        // ---- the diff ----
        // includeEqual: by default the payload carries only the DIFFERING objects (Create/Update/Delete) — the common
        // case, and much smaller. The UI's "Only differences" toggle asks for the full set (equal objects too, with
        // their before/after text) so it can list identical objects, not just count them. Apply paths never pass it.
        internal static ModelDiff Diff(TOM.Model left, TOM.Model right, string leftLabel, string rightLabel, bool includeEqual = false)
        {
            var items = new List<ModelDiffItem>();

            // Compare the MODEL container itself, not just its child collections. The old walker started at Tables,
            // so an authored model property (culture/collation/default mode/custom annotations/etc.) could change
            // while the diff still said EQUAL. ModelShell removes only the collections walked below and runtime/audit
            // noise; everything else remains visible, including properties a future TOM version adds.
            var leftModelShell = ModelShell(left); var rightModelShell = ModelShell(right);
            if (!JsonSemanticEqual(leftModelShell, rightModelShell))
                items.Add(Mk("model", "Model", left.Name ?? "Model", null, "Update", leftModelShell, rightModelShell, false));

            var rtList = right.Tables.Cast<TOM.Table>().ToList();
            var rightTablesByTag = ByTag(rtList, t => t.LineageTag);
            var dupTableTags = DuplicateTags(rtList, t => t.LineageTag);
            var matchedRT = new HashSet<TOM.Table>();
            foreach (var lt in left.Tables.Cast<TOM.Table>())
            {
                var ltag = lt.LineageTag;
                if (!string.IsNullOrEmpty(ltag) && dupTableTags.Contains(ltag))
                {
                    // Ambiguous target: shield ALL twins from the delete sweep, surface one Ambiguous item, apply nothing.
                    foreach (var twin in rtList.Where(x => x.LineageTag == ltag)) matchedRT.Add(twin);
                    items.Add(Ambiguous(AlmRef.Top("table", lt.Name), "Table", lt.Name, null, Ser(lt),
                        $"the target has multiple tables sharing LineageTag '{ltag}' — cannot resolve which to update"));
                    continue;
                }
                var rt = Match(lt.LineageTag, lt.Name, rightTablesByTag, n => rtList.FirstOrDefault(x => x.Name == n), t => t.LineageTag, out var retagRT);
                if (rt == null)
                {
                    if (retagRT != null)
                    {
                        // RETAG/REPUBLISH: a same-named live table carries a different tag. Emit the Create (left) + a
                        // flagged Delete (the live table), both marked so the delete path refuses by default — a
                        // delete-then-recreate would drop the live table and its data.
                        matchedRT.Add(retagRT);   // keep it out of the plain Delete sweep
                        var cRef = AlmRef.Top("table", lt.Name);
                        var dRef = AlmRef.Top("table", retagRT.Name);
                        items.Add(Republish(Mk(cRef, "Table", lt.Name, null, "Create", Ser(lt), null, false), dRef));
                        items.Add(Republish(Target(Mk(dRef, "Table", retagRT.Name, null, "Delete", null, Ser(retagRT), false), retagRT.LineageTag, retagRT.Name, null, null), cRef));
                        continue;
                    }
                    items.Add(Mk(AlmRef.Top("table", lt.Name), "Table", lt.Name, null, "Create", Ser(lt), null, false)); continue;
                }
                matchedRT.Add(rt);
                // Equality is the normalized TABLE SHELL, not a hand-maintained display allowlist. Child collections
                // are removed because they are walked independently below; the remaining shell includes detail rows,
                // annotations, extended properties, table flags, RefreshPolicy and the calc-group shell (without its
                // items). This closes the false-EQUAL class and automatically exposes new TOM properties after a bump.
                var leftTableShell = TableShell(lt); var rightTableShell = TableShell(rt);
                if (!JsonSemanticEqual(leftTableShell, rightTableShell))
                    items.Add(Target(Mk(AlmRef.Top("table", lt.Name), "Table", lt.Name, null, "Update", TableDisplay(lt, leftTableShell), TableDisplay(rt, rightTableShell), false), rt.LineageTag, rt.Name, null, null));
                CompareCollection(lt.Columns.Cast<TOM.Column>().Where(NotRowNumber), rt.Columns.Cast<TOM.Column>().Where(NotRowNumber), c => c.Name, c => c.LineageTag, "Column", lt.Name, rt.Name, rt.LineageTag, items, false);
                CompareCollection(lt.Measures.Cast<TOM.Measure>(), rt.Measures.Cast<TOM.Measure>(), m => m.Name, m => m.LineageTag, "Measure", lt.Name, rt.Name, rt.LineageTag, items, false);
                // Calculation-group partitions are runtime plumbing TOMWrapper materializes when an otherwise
                // equivalent .bim omits them. They are not authored data partitions; comparing one made Push Changes
                // show a Create against the model's own file. Real M/query/entity partitions remain in the diff.
                CompareCollection(lt.Partitions.Cast<TOM.Partition>().Where(NotCalculationGroupPartition),
                    rt.Partitions.Cast<TOM.Partition>().Where(NotCalculationGroupPartition),
                    p => p.Name, p => null, "Partition", lt.Name, rt.Name, rt.LineageTag, items, true);
                CompareCollection(lt.Hierarchies.Cast<TOM.Hierarchy>(), rt.Hierarchies.Cast<TOM.Hierarchy>(), h => h.Name, h => h.LineageTag, "Hierarchy", lt.Name, rt.Name, rt.LineageTag, items, false);
                // Calculation-group ITEMS of a MATCHED table ARE walked per-item (issue #124). Previously a matched calc-
                // group table was compared ONLY on TableProps above, so a changed calc-item DAX expression (or a right-only
                // item) yielded ZERO diff — a false-EQUAL that silently drops a real change on deploy (data loss). Calculation
                // Item is NOT taggable (name-keyed, like Partition — verified AMO 19.114, so the tag lambda is null +
                // matchedByName:true), and Ser() serializes the whole item so Expression/FormatStringDefinition/Ordinal/
                // Description all fall out of the JsonSemanticEqual. Guard the nulls: a plain table has no CalculationGroup.
                // ApplyOne (file apply) and LiveDeploy's delete channel carry the matching Create/Update/Delete.
                CompareCollection(lt.CalculationGroup?.CalculationItems.Cast<TOM.CalculationItem>() ?? Enumerable.Empty<TOM.CalculationItem>(),
                    rt.CalculationGroup?.CalculationItems.Cast<TOM.CalculationItem>() ?? Enumerable.Empty<TOM.CalculationItem>(),
                    ci => ci.Name, ci => null, "CalculationItem", lt.Name, rt.Name, rt.LineageTag, items, true);
            }
            foreach (var rt in rtList.Where(t => !matchedRT.Contains(t)))
                // Delete refs are TARGET-keyed (rt.Name) — the object lives on the target, and a source-keyed ref would
                // resolve an unrelated same-source-named object on the live model (a wrong-object DELETE).
                items.Add(Target(Mk(AlmRef.Top("table", rt.Name), "Table", rt.Name, null, "Delete", null, Ser(rt), false), rt.LineageTag, rt.Name, null, null));

            // Relationships: structural (endpoint) match — see CompareRelationships. The remaining top-level types are
            // identified by name (a role/perspective/etc. IS its name), so name-matching them is correct; flag them
            // (matchedByName: true) only so the UI can note they're name-keyed.
            CompareRelationships(left, right, items);
            CompareCollection(left.Roles.Cast<TOM.ModelRole>(), right.Roles.Cast<TOM.ModelRole>(), r => r.Name, r => null, "Role", null, null, null, items, true);
            CompareCollection(left.Perspectives.Cast<TOM.Perspective>(), right.Perspectives.Cast<TOM.Perspective>(), p => p.Name, p => null, "Perspective", null, null, null, items, true);
            CompareCollection(left.Cultures.Cast<TOM.Culture>(), right.Cultures.Cast<TOM.Culture>(), c => c.Name, c => null, "Culture", null, null, null, items, true);
            CompareCollection(left.DataSources.Cast<TOM.DataSource>(), right.DataSources.Cast<TOM.DataSource>(), d => d.Name, d => null, "DataSource", null, null, null, items, true);
            // NamedExpression IS taggable (it implements IMetadataObjectWithLineage — AMO 19.114), unlike role/
            // perspective/culture/datasource, so match shared M expressions by TAG (rename-safe; a retag surfaces as a
            // flagged Delete+Create like any tagged kind). Name is the fallback when the tag is empty on both sides.
            CompareCollection(left.Expressions.Cast<TOM.NamedExpression>(), right.Expressions.Cast<TOM.NamedExpression>(), e => e.Name, e => LineageOf(e), "Expression", null, null, null, items, false);

            return new ModelDiff
            {
                LeftLabel = leftLabel,
                RightLabel = rightLabel,
                Created = items.Count(i => i.Action == "Create"),
                Updated = items.Count(i => i.Action == "Update"),
                Deleted = items.Count(i => i.Action == "Delete"),
                Equal = items.Count(i => i.Action == "Equal"),
                Items = (includeEqual ? items : items.Where(i => i.Action != "Equal")).ToArray(),
            };
        }

        // type = the display ObjectType ("Measure"…); the ref kind is its lowercase form ("measure"…). srcTable/tgtTable
        // are null for the top-level (name-keyed) collections. matchedByName = the object is keyed across the two models
        // by NAME ONLY (tag is null) — flags the name-keyed groups in the UI. Lineage-tagged types pass matchedByName:false.
        private static void CompareCollection<T>(IEnumerable<T> leftItems, IEnumerable<T> rightItems,
            Func<T, string> name, Func<T, string> tag, string type, string srcTable, string tgtTable, string tgtTableTag,
            List<ModelDiffItem> items, bool matchedByName)
            where T : TOM.MetadataObject
        {
            var kind = type.ToLowerInvariant();
            var rl = rightItems.ToList();
            var rightByTag = ByTag(rl, tag);
            var dupTags = DuplicateTags(rl, tag);
            var matched = new HashSet<T>();
            foreach (var l in leftItems)
            {
                var ltag = tag(l);
                if (!string.IsNullOrEmpty(ltag) && dupTags.Contains(ltag))
                {
                    foreach (var twin in rl.Where(x => tag(x) == ltag)) matched.Add(twin);
                    items.Add(Ambiguous(RefFor(kind, srcTable, name(l)), type, name(l), srcTable, Ser(l),
                        $"the target has multiple {type.ToLowerInvariant()}s sharing LineageTag '{ltag}' — cannot resolve which to update"));
                    continue;
                }
                var r = Match(ltag, name(l), rightByTag, n => rl.FirstOrDefault(x => name(x) == n), tag, out var retagR);
                if (r == null)
                {
                    // Create: no target CHILD (no TargetTag/TargetName), but carry the matched PARENT table's identity so
                    // ApplyTableChild resolves the add-target by tag — a child added under a target-renamed parent lands
                    // on the right table, not an unrelated same-source-named one.
                    var mk = Mk(RefFor(kind, srcTable, name(l)), type, name(l), srcTable, "Create", Ser(l), null, matchedByName);
                    mk.TargetTable = tgtTable;
                    mk.TargetTableTag = string.IsNullOrEmpty(tgtTableTag) ? null : tgtTableTag;
                    if (retagR != null)
                    {
                        // RETAG/REPUBLISH (same name, different tag): emit the Create + a flagged Delete of the live
                        // child, both marked so the delete path refuses by default (drop-then-recreate loses live data).
                        matched.Add(retagR);
                        var dRef = RefFor(kind, tgtTable, name(retagR));
                        items.Add(Republish(mk, dRef));
                        items.Add(Republish(Target(Mk(dRef, type, name(retagR), tgtTable, "Delete", null, Ser(retagR), matchedByName), tag(retagR), name(retagR), tgtTable, tgtTableTag), mk.Ref));
                        continue;
                    }
                    items.Add(mk);
                    continue;
                }
                matched.Add(r);
                var ls = Ser(l); var rs = Ser(r);
                items.Add(Target(Mk(RefFor(kind, srcTable, name(l)), type, name(l), srcTable, JsonSemanticEqual(ls, rs) ? "Equal" : "Update", ls, rs, matchedByName),
                    tag(r), name(r), tgtTable, tgtTableTag));
            }
            // Right-only ⇒ Delete, keyed by the TARGET table (see the table-delete note above).
            foreach (var r in rl.Where(x => !matched.Contains(x)))
                items.Add(Target(Mk(RefFor(kind, tgtTable, name(r)), type, name(r), tgtTable, "Delete", null, Ser(r), matchedByName),
                    tag(r), name(r), tgtTable, tgtTableTag));
        }

        // Relationships are matched STRUCTURALLY — by their (fromTable[fromColumn] → toTable[toColumn]) endpoints —
        // NOT by name. Power BI auto-assigns GUID-ish relationship names, so two independently-authored models hold
        // the same logical relationship under different names; a name match would show it as a spurious Delete+Create.
        // The endpoint signature is stable across models, so the same relationship matches and only real property
        // changes (cross-filter / active / RI) read as Update. The ref carries the signature so apply can re-find it.
        private static void CompareRelationships(TOM.Model left, TOM.Model right, List<ModelDiffItem> items)
        {
            var rl = right.Relationships.Cast<TOM.Relationship>().ToList();
            var rightBySig = new Dictionary<string, TOM.Relationship>();
            foreach (var r in rl) { var s = RelSig(r); if (!rightBySig.ContainsKey(s)) rightBySig[s] = r; }   // first wins (endpoint pairs are unique in practice)
            var matched = new HashSet<TOM.Relationship>();
            foreach (var l in left.Relationships.Cast<TOM.Relationship>())
            {
                var sig = RelSig(l);
                var refStr = "relationship:" + sig;
                if (rightBySig.TryGetValue(sig, out var r) && !matched.Contains(r))
                {
                    matched.Add(r);
                    items.Add(Mk(refStr, "Relationship", RelDisplay(l), null, RelEqual(l, r) ? "Equal" : "Update", Ser(l), Ser(r), false));
                }
                else items.Add(Mk(refStr, "Relationship", RelDisplay(l), null, "Create", Ser(l), null, false));
            }
            foreach (var r in rl.Where(x => !matched.Contains(x)))
                items.Add(Mk("relationship:" + RelSig(r), "Relationship", RelDisplay(r), null, "Delete", null, Ser(r), false));
        }

        // Endpoint signature (the relationship's structural identity, name-independent). Non-single-column
        // relationships (rare) fall back to the name. Endpoint NAME parts are escaped (AlmRef.EscRel) so a name that
        // contains '[' ']' '->' can't forge a boundary — the sig is INJECTIVE, so no two distinct relationships share
        // one (which used to delete the wrong relationship on a first-match). Compared whole; never re-split.
        private static string RelSig(TOM.Relationship r)
            => r is TOM.SingleColumnRelationship sc && sc.FromColumn != null && sc.ToColumn != null
                ? AlmRef.EscRel(sc.FromColumn.Table?.Name) + "[" + AlmRef.EscRel(sc.FromColumn.Name) + "]->" + AlmRef.EscRel(sc.ToColumn.Table?.Name) + "[" + AlmRef.EscRel(sc.ToColumn.Name) + "]"
                : AlmRef.EscRel(r.Name);
        private static string RelDisplay(TOM.Relationship r)
            => r is TOM.SingleColumnRelationship sc && sc.FromColumn != null && sc.ToColumn != null
                ? sc.FromColumn.Table?.Name + "[" + sc.FromColumn.Name + "] → " + sc.ToColumn.Table?.Name + "[" + sc.ToColumn.Name + "]"
                : r.Name;
        // Equal ignores the (auto-assigned) name — only the meaningful behaviour props decide Equal vs Update. Note:
        // the signature is endpoints-only (NOT IsActive), so toggling a single relationship active↔inactive reads as
        // an Update here (correct) rather than a false Create+Delete; two relationships on the EXACT same column pair
        // (one active + one inactive) would share a signature, but Power BI disallows duplicate column-pair
        // relationships (role-playing dims use DIFFERENT columns → different signatures), so that can't arise.
        private static bool RelEqual(TOM.Relationship a, TOM.Relationship b)
        {
            if (a.IsActive != b.IsActive) return false;
            if (a is TOM.SingleColumnRelationship sa && b is TOM.SingleColumnRelationship sb)
                return sa.CrossFilteringBehavior == sb.CrossFilteringBehavior
                    && sa.SecurityFilteringBehavior == sb.SecurityFilteringBehavior
                    && sa.RelyOnReferentialIntegrity == sb.RelyOnReferentialIntegrity
                    && sa.FromCardinality == sb.FromCardinality
                    && sa.ToCardinality == sb.ToCardinality
                    && sa.JoinOnDateBehavior == sb.JoinOnDateBehavior;
            // Non-single-column relationships (rare) matched by NAME — a full serialize compare is accurate, never
            // assume Equal (so a real change surfaces as Update rather than being silently dropped).
            return JsonSemanticEqual(Ser(a), Ser(b));
        }

        internal sealed class ApplyOutcome
        {
            public List<string> Applied { get; } = new();
            public List<(string Ref, string Reason)> Failed { get; } = new();   // selected but not applied (threw, or target/parent missing)
        }

        // ---- selective apply: make RIGHT match LEFT for the chosen items ----
        // Records every selected item that did NOT apply (so the caller never reports a silent partial success), and
        // orders the work so a partial selection can't reference a not-yet-created / already-deleted object: on
        // Create, containers (tables/roles/…) before relationships (which point at columns); on Delete, the reverse.
        internal static ApplyOutcome Apply(TOM.Model left, TOM.Model right, ModelDiff diff, ISet<string> selected)
        {
            var outcome = new ApplyOutcome();
            // Selection contract: selected == null ⇒ apply ALL differences; an EMPTY set ⇒ apply NOTHING (it contains
            // no ref). (The old "empty == all" sentinel was a loaded gun — a caller building a computed selection could
            // apply the whole diff by accident; callers no longer need to guard.) Only Create/Update/Delete are
            // applied — Equal is a no-op and Ambiguous is refuse-and-report (never applied, never deleted).
            var todo = diff.Items
                .Where(it => (it.Action == "Create" || it.Action == "Update" || it.Action == "Delete")
                             && (selected == null || selected.Contains(it.Ref)))
                .OrderBy(ApplyRank).ToList();
            foreach (var it in todo)
            {
                try
                {
                    if (ApplyOne(left, right, it)) outcome.Applied.Add(it.Ref);
                    else outcome.Failed.Add((it.Ref, "target object not found (e.g. an unsupported table rename, or a parent that wasn't applied)"));
                }
                catch (Exception ex) { outcome.Failed.Add((it.Ref, ex.Message)); }
            }
            return outcome;
        }

        private static int ApplyRank(ModelDiffItem it)
        {
            var rel = it.ObjectType == "Relationship";
            if (it.Action == "Delete") return rel ? 0 : 1;   // drop relationships before the tables they bind
            return rel ? 2 : 1;                               // add/update relationships after their tables/columns
        }

        private static bool ApplyOne(TOM.Model left, TOM.Model right, ModelDiffItem it)
        {
            switch (it.ObjectType)
            {
                case "Model":
                    // Preflight BEFORE mutating: shell serialization deliberately detects every authored property,
                    // but this carrier supports a bounded set. If an unsupported residual differs, refuse the whole
                    // item honestly instead of half-applying it and reporting success.
                    if (!JsonSemanticEqual(UnsupportedModelShell(left), UnsupportedModelShell(right)))
                        throw new InvalidOperationException("the model carries authored shell metadata this apply path cannot copy yet; deploy the model through TMDL/XMLA instead");
                    CopyModelShell(left, right);
                    return true;
                case "Table":
                    // A top-level TABLE's own name is it.Name; it.Table (the OWNING-table field) is null for a table, so
                    // the source lookup MUST key on it.Name — left.Tables.Find(it.Table) is Find(null), which THROWS
                    // ArgumentNullException("key") on TOM's name-dictionary and made every real-diff table Create/Update
                    // fail with a cryptic "Value cannot be null (key)". (Children resolve by it.Table correctly — that's
                    // their parent — but a table has no parent.)
                    // Create adds the source table fresh — no target to resolve. Route the clone through CloneNamed
                    // (issue #120): a cherry-picked table whose LineageTag already sits on a DIFFERENT table in the
                    // destination would make the raw .Clone()+Add throw a duplicate-lineage-tag ArgumentException;
                    // CloneNamed clears the clone's tag ONLY on that collision (a fresh identity), preserving it otherwise.
                    if (it.Action == "Create") { var ltC = left.Tables.Find(it.Name); if (ltC == null) return false; right.Tables.Add((TOM.Table)CloneNamed(ltC, right.Tables.Cast<TOM.NamedMetadataObject>())); return true; }
                    // Update/Delete resolve the target by IDENTITY. A carried tag that no longer resolves is TERMINAL:
                    // refuse (a clear Failed reason) — never fall back to a same-named table (the wrong-object mutation).
                    var rtT = FindTableBy(right, it.TargetTag, it.TargetName, it.Table);
                    if (rtT == null)
                    {
                        if (!string.IsNullOrEmpty(it.TargetTag))
                            throw new InvalidOperationException($"the table this change targeted (lineage '{it.TargetTag}') no longer exists on the target — re-diff against the current model");
                        return false;
                    }
                    if (it.Action == "Delete") { right.Tables.Remove(rtT); return true; }
                    var ltT = left.Tables.Find(it.Name);   // it.Name is the source table's name (see the Create note above)
                    if (ltT == null) return false;
                    // A plain-table ↔ calculation-group transition changes the table's KIND — the whole CalculationGroup
                    // (its special column + items) appears or disappears. That's a structural rebuild, not a property
                    // copy, so REFUSE it honestly (issue #124): the throw is caught into outcome.Failed, so Apply never
                    // reports a kind-changed table as success. (A silent half-apply would leave the paired calc-item
                    // creates to fail against a table that still has no calc group.) The user recreates the table.
                    if ((ltT.CalculationGroup == null) != (rtT.CalculationGroup == null))
                        throw new InvalidOperationException($"table '{rtT.Name}' changed kind (plain table ↔ calculation group) — recreate it rather than updating it in place");
                    if (!JsonSemanticEqual(UnsupportedTableShell(ltT), UnsupportedTableShell(rtT)))
                        throw new InvalidOperationException($"table '{rtT.Name}' carries authored shell metadata this apply path cannot copy yet; deploy it through TMDL/XMLA instead");
                    rtT.Description = ltT.Description; rtT.IsHidden = ltT.IsHidden; rtT.DataCategory = ltT.DataCategory;
                    if (rtT.Name != ltT.Name) rtT.Name = ltT.Name;
                    // Precedence (calc-group evaluation order) and RefreshPolicy (incremental refresh) are authored table
                    // props the diff now flags (issue #124) — but they were reported as an Update and NEVER copied here,
                    // so Apply claimed success while the change silently vanished. Copy both. RefreshPolicy.Clone() is
                    // lossless (raw TOM, AMO 19.114) and detaches the policy from the source table; null clears it.
                    if (ltT.CalculationGroup != null && rtT.CalculationGroup != null)
                    {
                        rtT.CalculationGroup.Precedence = ltT.CalculationGroup.Precedence;
                        rtT.CalculationGroup.Description = ltT.CalculationGroup.Description;
                        rtT.CalculationGroup.NoSelectionExpression = ltT.CalculationGroup.NoSelectionExpression == null ? null : (TOM.CalculationGroupExpression)ltT.CalculationGroup.NoSelectionExpression.Clone();
                        rtT.CalculationGroup.MultipleOrEmptySelectionExpression = ltT.CalculationGroup.MultipleOrEmptySelectionExpression == null ? null : (TOM.CalculationGroupExpression)ltT.CalculationGroup.MultipleOrEmptySelectionExpression.Clone();
                        CopyAnnotations(ltT.CalculationGroup.Annotations.Cast<TOM.Annotation>(), rtT.CalculationGroup.Annotations);
                    }
                    rtT.RefreshPolicy = ltT.RefreshPolicy == null ? null : (TOM.RefreshPolicy)ltT.RefreshPolicy.Clone();
                    rtT.DefaultDetailRowsDefinition = ltT.DefaultDetailRowsDefinition == null ? null : (TOM.DetailRowsDefinition)ltT.DefaultDetailRowsDefinition.Clone();
                    rtT.IsPrivate = ltT.IsPrivate;
                    rtT.ShowAsVariationsOnly = ltT.ShowAsVariationsOnly;
                    rtT.ExcludeFromModelRefresh = ltT.ExcludeFromModelRefresh;
                    rtT.ExcludeFromAutomaticAggregations = ltT.ExcludeFromAutomaticAggregations;
                    rtT.AlternateSourcePrecedence = ltT.AlternateSourcePrecedence;
                    rtT.DirectLakeIndexingBehavior = ltT.DirectLakeIndexingBehavior;
                    rtT.SourceLineageTag = ltT.SourceLineageTag;
                    CopyAnnotations(ltT.Annotations.Cast<TOM.Annotation>(), rtT.Annotations);
                    return true;
                case "Measure": return ApplyTableChild(left, right, it, t => t.Measures.Cast<TOM.NamedMetadataObject>(), (t, o) => t.Measures.Add((TOM.Measure)o), (t, n) => { var x = t.Measures.Find(n); if (x != null) t.Measures.Remove(x); });
                case "Column": return ApplyTableChild(left, right, it, t => t.Columns.Cast<TOM.NamedMetadataObject>(), (t, o) => t.Columns.Add((TOM.Column)o), (t, n) => { var x = t.Columns.Find(n); if (x != null) t.Columns.Remove(x); });
                case "Partition": return ApplyTableChild(left, right, it, t => t.Partitions.Cast<TOM.NamedMetadataObject>(), (t, o) => t.Partitions.Add((TOM.Partition)o), (t, n) => { var x = t.Partitions.Find(n); if (x != null) t.Partitions.Remove(x); });
                // A calculation item is a name-keyed child of a table's CalculationGroup (issue #124), mirroring "Partition".
                // A target table with NO calc group can't receive a calc item — fail with a CLEAR reason (the throw is caught
                // into outcome.Failed by Apply, so it's reported, never a raw NullReference across a door).
                case "CalculationItem": return ApplyTableChild(left, right, it,
                    t => t.CalculationGroup?.CalculationItems.Cast<TOM.NamedMetadataObject>() ?? Enumerable.Empty<TOM.NamedMetadataObject>(),
                    (t, o) => { if (t.CalculationGroup == null) throw new InvalidOperationException($"cannot add calculation item '{o.Name}' — the target table '{t.Name}' has no calculation group"); t.CalculationGroup.CalculationItems.Add((TOM.CalculationItem)o); },
                    (t, n) => { var x = t.CalculationGroup?.CalculationItems.Find(n); if (x != null) t.CalculationGroup.CalculationItems.Remove(x); });
                case "Hierarchy": return ApplyTableChild(left, right, it, t => t.Hierarchies.Cast<TOM.NamedMetadataObject>(), (t, o) => t.Hierarchies.Add((TOM.Hierarchy)o), (t, n) => { var x = t.Hierarchies.Find(n); if (x != null) t.Hierarchies.Remove(x); });
                case "Relationship": return ApplyRelationship(left, right, it);
                case "Role": return ApplyTopLevel(left.Roles.Cast<TOM.NamedMetadataObject>(), right.Roles.Cast<TOM.NamedMetadataObject>(), it, o => right.Roles.Add((TOM.ModelRole)o), o => right.Roles.Remove((TOM.ModelRole)o));
                case "Perspective": return ApplyTopLevel(left.Perspectives.Cast<TOM.NamedMetadataObject>(), right.Perspectives.Cast<TOM.NamedMetadataObject>(), it, o => right.Perspectives.Add((TOM.Perspective)o), o => right.Perspectives.Remove((TOM.Perspective)o));
                case "Culture": return ApplyTopLevel(left.Cultures.Cast<TOM.NamedMetadataObject>(), right.Cultures.Cast<TOM.NamedMetadataObject>(), it, o => right.Cultures.Add((TOM.Culture)o), o => right.Cultures.Remove((TOM.Culture)o));
                case "DataSource": return ApplyTopLevel(left.DataSources.Cast<TOM.NamedMetadataObject>(), right.DataSources.Cast<TOM.NamedMetadataObject>(), it, o => right.DataSources.Add((TOM.DataSource)o), o => right.DataSources.Remove((TOM.DataSource)o));
                case "Expression": return ApplyTopLevel(left.Expressions.Cast<TOM.NamedMetadataObject>(), right.Expressions.Cast<TOM.NamedMetadataObject>(), it, o => right.Expressions.Add((TOM.NamedExpression)o), o => right.Expressions.Remove((TOM.NamedExpression)o));
                default: return false;
            }
        }

        private static bool ApplyTableChild(TOM.Model left, TOM.Model right, ModelDiffItem it,
            Func<TOM.Table, IEnumerable<TOM.NamedMetadataObject>> children, Action<TOM.Table, TOM.NamedMetadataObject> add, Action<TOM.Table, string> remove)
        {
            // Resolve the OWNING TABLE by the matched right table's IDENTITY (its LineageTag / current name), NOT the
            // source name. A carried owning-table tag that no longer resolves is TERMINAL — refuse, never land on a
            // same-named table (Create children carry the parent identity too, so this holds for the add-target).
            var rt = FindTableBy(right, it.TargetTableTag, it.TargetTable, it.Table);
            if (rt == null)
            {
                if (!string.IsNullOrEmpty(it.TargetTableTag))
                    throw new InvalidOperationException($"the owning table this change targeted (lineage '{it.TargetTableTag}') no longer exists on the target — re-diff against the current model");
                return false;
            }
            // The CHILD OBJECT itself is resolved by identity too. If the item carried a child tag (Update/Delete of a
            // tagged child) and it no longer resolves, the target child was replaced/retagged since the diff → REFUSE
            // (never a name-guess that could hit / delete an unrelated same-named child). A tag-less child kind
            // (partitions) or a Create (no target child) resolves by name, which is legitimate.
            var childHasIdentity = !string.IsNullOrEmpty(it.TargetTag);
            if (it.Action == "Delete")
            {
                var cur = ResolveChild(children(rt), it.TargetTag, childHasIdentity ? null : (it.TargetName ?? it.Name));
                if (cur == null)
                {
                    if (childHasIdentity)
                        throw new InvalidOperationException($"the object this change targeted (lineage '{it.TargetTag}') no longer exists on the target — re-diff against the current model");
                    return false;   // tag-less + absent by name ⇒ a reported no-op, never a guess
                }
                remove(rt, cur.Name); return true;
            }
            var lt = left.Tables.Find(it.Table);
            var lo = lt == null ? null : children(lt).FirstOrDefault(x => x.Name == it.Name);
            if (lo == null) return false;
            // Replace-by-clone. On a lineage-matched RENAME the target child still carries its OLD name; resolve it by
            // identity and remove THAT — not it.Name (the NEW name), which would no-op and leave a duplicate + orphan.
            var existing = ResolveChild(children(rt), it.TargetTag, childHasIdentity ? null : (it.TargetName ?? it.Name));
            if (existing == null && childHasIdentity)
                throw new InvalidOperationException($"the object this change targeted (lineage '{it.TargetTag}') no longer exists on the target — re-diff against the current model");
            if (existing != null) remove(rt, existing.Name);
            add(rt, CloneNamed(lo, children(rt)));   // children(rt) is post-removal — the collision check sees the current state
            return true;
        }

        private static bool ApplyTopLevel(IEnumerable<TOM.NamedMetadataObject> leftColl, IEnumerable<TOM.NamedMetadataObject> rightColl, ModelDiffItem it,
            Action<TOM.NamedMetadataObject> add, Action<TOM.NamedMetadataObject> remove)
        {
            // Resolution is IDENTITY-aware, not blanket name-keyed. Per AMO 19.114 reflection: role / perspective /
            // culture / datasource do NOT implement IMetadataObjectWithLineage → NAME identity (a rename is Delete+Create),
            // so the item carries no TargetTag and we resolve by name. NamedExpression DOES implement it → the item
            // carries a TargetTag, and we resolve the target by TAG with a TERMINAL miss (a republished/retagged live
            // expression is not the object we diffed — refuse rather than mutate/delete it by name).
            TOM.NamedMetadataObject ro;
            if (!string.IsNullOrEmpty(it.TargetTag))
            {
                ro = rightColl.FirstOrDefault(x => LineageOf(x) == it.TargetTag);
                if (it.Action == "Delete") { if (ro != null) { remove(ro); return true; } return false; }   // tag miss ⇒ no-op, never a name-guess
                if (ro == null)
                    throw new InvalidOperationException($"the object this change targeted (lineage '{it.TargetTag}') no longer exists on the target — re-diff against the current model");
            }
            else
            {
                var targetName = it.TargetName ?? it.Name;
                ro = rightColl.FirstOrDefault(x => x.Name == targetName);
                if (it.Action == "Delete") { if (ro != null) { remove(ro); return true; } return false; }
            }
            var lo = leftColl.FirstOrDefault(x => x.Name == it.Name);
            if (lo == null) return false;
            if (ro != null) remove(ro);
            add(CloneNamed(lo, rightColl));   // rightColl re-enumerates post-removal — the collision check sees the current state
            return true;
        }

        // Relationships are matched by their endpoint signature (carried in the ref), not by name — so a cross-model
        // apply finds the structurally-equal target relationship even when the two models named it differently. The sig
        // is escaped (RelSig) and compared WHOLE — never re-split — so no unescape is needed here.
        private static bool ApplyRelationship(TOM.Model left, TOM.Model right, ModelDiffItem it)
        {
            var sig = it.Ref.StartsWith("relationship:") ? it.Ref.Substring("relationship:".Length) : it.Ref;
            var ro = right.Relationships.Cast<TOM.Relationship>().FirstOrDefault(x => RelSig(x) == sig);
            if (it.Action == "Delete") { if (ro != null) { right.Relationships.Remove(ro); return true; } return false; }
            var lo = left.Relationships.Cast<TOM.Relationship>().FirstOrDefault(x => RelSig(x) == sig);
            if (lo == null) return false;
            // The cloned relationship's endpoints must resolve in the TARGET, or we'd write a dangling relationship
            // that serializes fine but is invalid. Guard BEFORE removing the matched target (a failed Create must not
            // first delete it) → an unresolvable endpoint becomes a FailedRef, never silent corruption.
            if (lo is TOM.SingleColumnRelationship slo)
            {
                var ft = slo.FromColumn?.Table?.Name == null ? null : right.Tables.Find(slo.FromColumn.Table.Name);
                var tt = slo.ToColumn?.Table?.Name == null ? null : right.Tables.Find(slo.ToColumn.Table.Name);
                if (ft == null || tt == null || ft.Columns.Find(slo.FromColumn.Name) == null || tt.Columns.Find(slo.ToColumn.Name) == null)
                    return false;
            }
            if (ro != null) right.Relationships.Remove(ro);       // replace the structurally-equal one (name may differ)
            right.Relationships.Add((TOM.Relationship)lo.Clone());
            return true;
        }

        // Clone() lives on the concrete TOM types, not the NamedMetadataObject base — dispatch on the runtime type.
        // NOTE (AMO 19.114, 2026-07-09): raw TOM .Clone() copies the LineageTag VERBATIM, and the subsequent Add throws
        // ArgumentException("An object with lineage-tag 'X' already exists in the collection") — fail-FAST (not deferred
        // to serialize) for table/measure/column alike. (TE2's own wrapper Clone mints a fresh GUID; this raw path does
        // not.) FIX #120: when the clone's tag already sits on a DIFFERENT object in the DESTINATION collection, CLEAR the
        // clone's tag ("" = unset) so it takes a FRESH identity and Add succeeds, instead of throwing. Cleared ONLY on an
        // actual collision — a non-colliding tag is preserved (rename-safety) and the incumbent that owns the tag is never
        // touched. ApplyTableChild removes-before-add so its Update path rarely collides; a cross-model copy / stale-diff
        // Create is where a live target independently bears the same tag (the case that used to surface as a caught Failed).
        // Setting "" here is safe at any compatibility level: the clone is detached (no Database), so the CL<1540 setter
        // guard doesn't engage — and a collision only exists when both tags are non-empty (i.e. a CL>=1540 model anyway).
        private static TOM.NamedMetadataObject CloneNamed(TOM.NamedMetadataObject o, IEnumerable<TOM.NamedMetadataObject> destination)
        {
            var clone = (TOM.NamedMetadataObject)((dynamic)o).Clone();
            var tag = LineageOf(clone);
            if (!string.IsNullOrEmpty(tag) && destination.Any(d => !ReferenceEquals(d, clone) && LineageOf(d) == tag))
                ((TOM.IMetadataObjectWithLineage)clone).LineageTag = "";   // collision → fresh identity; the incumbent keeps its tag
            return clone;
        }

        // ---- resolution-by-identity helpers ----
        // Find a table by IDENTITY. CRITICAL: identity that degrades to name on a miss IS name resolution — the exact
        // wrong-object bug this fix exists to kill. So a carried tag is AUTHORITATIVE: if `tag` is non-empty and no live
        // table carries it, resolution is TERMINAL → null (the caller turns that into a Failed item, never a name-guess
        // onto an unrelated same-named table). Name resolution (nameIfNoTag → fallbackIfNoTag) is legitimate ONLY when
        // the item carried NO tag at all — a table with no LineageTag. The invariant is now in the CONTROL FLOW.
        // Scans m.Tables (ONE collection). Table lineage tags are effectively model-unique because Model.Tables is a
        // single scope and the spec requires within-scope uniqueness — so FirstOrDefault(tag) resolves at most one.
        private static TOM.Table FindTableBy(TOM.Model m, string tag, string nameIfNoTag, string fallbackIfNoTag)
        {
            if (!string.IsNullOrEmpty(tag))
                return m.Tables.Cast<TOM.Table>().FirstOrDefault(t => t.LineageTag == tag);   // tag miss ⇒ TERMINAL (null), never name
            if (!string.IsNullOrEmpty(nameIfNoTag)) { var byName = m.Tables.Find(nameIfNoTag); if (byName != null) return byName; }
            return string.IsNullOrEmpty(fallbackIfNoTag) ? null : m.Tables.Find(fallbackIfNoTag);
        }

        // Same rule for a table child: a carried tag is AUTHORITATIVE and a miss is TERMINAL (null). Name resolution is
        // legitimate only for a tag-less child kind (partitions carry no LineageTag) — the caller passes nameIfNoTag
        // only when the child has no tag, so a tagged miss can never silently name-match.
        // SCOPE: this scans a SINGLE parent's `children` — uniqueness is PER-COLLECTION, not per-model (two columns in
        // DIFFERENT tables may legally share a LineageTag, verified AMO 19.114). Because we resolve within one table's
        // children, a cross-table duplicate tag can never mis-resolve. Never build a model-wide tag→child dictionary.
        private static TOM.NamedMetadataObject ResolveChild(IEnumerable<TOM.NamedMetadataObject> children, string tag, string nameIfNoTag)
        {
            if (!string.IsNullOrEmpty(tag))
                return children.FirstOrDefault(c => LineageOf(c) == tag);   // tag miss ⇒ TERMINAL (null), never name
            return string.IsNullOrEmpty(nameIfNoTag) ? null : children.FirstOrDefault(c => c.Name == nameIfNoTag);
        }

        // INTERFACE-driven, not a hardcoded kind list: a TOM object carries a lineage tag IFF it implements
        // IMetadataObjectWithLineage (AMO 19.114, verified 2026-07-09: Table/Column/Measure/Hierarchy/Level/
        // NamedExpression do; Partition/Relationship/Role/Perspective/Culture/DataSource/CalculationItem do NOT).
        // Partitions therefore return null → resolved by name. (SourceLineageTag on the same interface is a DIFFERENT,
        // source-binding thing — never an identity signal here.)
        private static string LineageOf(TOM.MetadataObject o) => (o as TOM.IMetadataObjectWithLineage)?.LineageTag;

        // ---- helpers (mirror LiveDeploy's match) ----
        private static bool NotRowNumber(TOM.Column c) => c.Type != TOM.ColumnType.RowNumber;
        private static bool NotCalculationGroupPartition(TOM.Partition p) => p.SourceType != TOM.PartitionSourceType.CalculationGroup;
        private const int PbiFeatureFloor = 1701;

        private static string Ser(TOM.MetadataObject o)
        {
            // The context-free overload validates in AnalysisServices mode. Always carry the owning database's
            // context, with a Power BI fallback for model-only/lossy snapshots whose database metadata was absent.
            var db = o.Model?.Database;
            var mode = db != null && db.CompatibilityMode != AS.CompatibilityMode.Unknown
                ? db.CompatibilityMode : AS.CompatibilityMode.PowerBI;
            var level = Math.Max(db?.CompatibilityLevel ?? 0, PbiFeatureFloor);
            return Normalize(TOM.JsonSerializer.SerializeObject(o, null, level, mode), o);
        }

        // Properties the SERVER populates. A model read from an XMLA endpoint carries them; one read from TMDL or a
        // .bim never does — so a raw TOM-JSON compare of two semantically identical models called every object
        // Updated (210/210 on a real client model — issue #121). Scrubbing lives here, not in the UI, because both
        // doors and apply_diff share this serializer, and Ser's output is also each item's Left/RightText.
        private static readonly HashSet<string> RuntimeProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "modifiedTime", "structureModifiedTime", "refreshedTime", "lastProcessed", "lastUpdate", "lastSchemaUpdate", "state" };

        private static string Normalize(string json, TOM.MetadataObject rootObject)
        {
            if (string.IsNullOrEmpty(json)) return json;
            try
            {
                var tok = Newtonsoft.Json.Linq.JToken.Parse(json);
                Scrub(tok);
                if (tok is Newtonsoft.Json.Linq.JObject root)
                {
                    if (rootObject is TOM.Measure) root.Remove("dataType");
                    // TMDL writes these semantic defaults explicitly while a valid .bim may omit them. Scrub only
                    // the default values on their exact root types: non-zero ordering remains an authored change.
                    if (rootObject is TOM.CalculationItem && (int?)root["ordinal"] == 0) root.Remove("ordinal");
                    if (rootObject is TOM.NamedExpression
                        && string.Equals((string)root["kind"], "m", StringComparison.OrdinalIgnoreCase)) root.Remove("kind");
                }
                return tok.ToString(Newtonsoft.Json.Formatting.Indented);
            }
            catch { return json; }   // a normalizer failure must never SWALLOW a diff — fall back to the raw text
        }

        private static void Scrub(Newtonsoft.Json.Linq.JToken tok)
        {
            if (tok is Newtonsoft.Json.Linq.JArray arr) { foreach (var c in arr) Scrub(c); return; }
            if (!(tok is Newtonsoft.Json.Linq.JObject o)) return;
            foreach (var p in RuntimeProps) o.Remove(p);

            // dataType on a MEASURE is inferred by the server from the expression (absent offline), so drop it.
            if (o["measures"] is Newtonsoft.Json.Linq.JArray ms)
                foreach (var m in ms.OfType<Newtonsoft.Json.Linq.JObject>()) m.Remove("dataType");

            // A CALCULATED column's dataType is ALSO server-inferred from its DAX (isDataTypeInferred:true) — the
            // XMLA-loaded side carries the computed type + marker, the offline/TMDL working-copy side does not, so a
            // compare-to-self flagged every calculated column Updated. Discriminate by the MARKER, never the collection:
            // when isDataTypeInferred is true the type is derived (drop it, like a measure); an AUTHORED type
            // (isDataTypeInferred:false) KEEPS its dataType so a real breaking change still surfaces. Always drop the
            // runtime-only marker itself — its mere presence/absence on one side must never forge a diff. (T94/e384c41)
            if (o["isDataTypeInferred"] is Newtonsoft.Json.Linq.JValue inf && inf.Type == Newtonsoft.Json.Linq.JTokenType.Boolean && (bool)inf)
                o.Remove("dataType");
            o.Remove("isDataTypeInferred");

            foreach (var p in o.Properties().ToList()) Scrub(p.Value);

            // attributeHierarchy on a column is pure runtime state. Drop it only once the scrub above has emptied it,
            // rather than removing it wholesale, so an authored annotation inside it still counts as a difference.
            if (o["attributeHierarchy"] is Newtonsoft.Json.Linq.JObject ah && !ah.HasValues) o.Remove("attributeHierarchy");
        }

        // ---- normalized container shells ---------------------------------------------------------------
        // Child collections are removed only when the walker above compares them independently. Everything else
        // remains: this is intentionally future-facing, so a TOM bump may create a new Update (safe over-reporting)
        // but can never leave a newly-authored property invisible (unsafe false-EQUAL).
        private static string TableShell(TOM.Table table)
        {
            var root = ParseObject(Ser(table));
            Remove(root, "columns", "measures", "partitions", "hierarchies", "lineageTag", "changedProperties");
            if (Get(root, "calculationGroup") is Newtonsoft.Json.Linq.JObject group)
                Remove(group, "calculationItems");
            CanonicalizeNamedArrays(root);
            return root.ToString(Newtonsoft.Json.Formatting.Indented);
        }

        private static string ModelShell(TOM.Model model)
        {
            var root = ParseObject(Ser(model));
            Remove(root, "tables", "relationships", "roles", "perspectives", "cultures", "dataSources", "expressions");
            // The accountable audit chain is a deploy ride-along, not semantic model behaviour. Comparing it would
            // make an audit append recursively demand another deploy and would break compare-to-self.
            RemoveAuditAnnotations(root);
            CanonicalizeNamedArrays(root);
            return root.ToString(Newtonsoft.Json.Formatting.Indented);
        }

        internal static string UnsupportedTableShell(TOM.Table table)
        {
            var root = ParseObject(TableShell(table));
            Remove(root, "name", "description", "isHidden", "dataCategory", "refreshPolicy",
                "defaultDetailRowsDefinition", "isPrivate", "showAsVariationsOnly", "excludeFromModelRefresh",
                "excludeFromAutomaticAggregations", "alternateSourcePrecedence", "directLakeIndexingBehavior",
                "sourceLineageTag", "annotations");
            if (Get(root, "calculationGroup") is Newtonsoft.Json.Linq.JObject group)
            {
                Remove(group, "precedence", "description", "noSelectionExpression", "multipleOrEmptySelectionExpression", "annotations");
                if (!group.HasValues) Remove(root, "calculationGroup");
            }
            return root.ToString(Newtonsoft.Json.Formatting.None);
        }

        internal static string UnsupportedModelShell(TOM.Model model)
        {
            var root = ParseObject(ModelShell(model));
            Remove(root, "name", "description", "culture", "collation", "defaultMode", "defaultDataView",
                "defaultDirectLakeIndexingBehavior", "defaultPowerBIDataSourceVersion", "directLakeBehavior",
                "discourageCompositeModels", "discourageImplicitMeasures",
                "discourageReportMeasures", "forceUniqueNames", "mAttributes", "maxParallelismPerQuery",
                "maxParallelismPerRefresh", "metadataAccessPolicy", "selectionExpressionBehavior",
                "sourceQueryCulture", "storageLocation", "valueFilterBehavior", "dataSourceDefaultMaxConnections",
                "dataSourceVariablesOverrideBehavior", "annotations");
            return root.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static Newtonsoft.Json.Linq.JObject ParseObject(string json)
            => Newtonsoft.Json.Linq.JObject.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);

        private static Newtonsoft.Json.Linq.JToken Get(Newtonsoft.Json.Linq.JObject o, string name)
            => o.Properties().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;

        private static void Remove(Newtonsoft.Json.Linq.JObject o, params string[] names)
        {
            foreach (var name in names)
            {
                var p = o.Properties().FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                p?.Remove();
            }
        }

        private static void RemoveAuditAnnotations(Newtonsoft.Json.Linq.JObject root)
        {
            if (!(Get(root, "annotations") is Newtonsoft.Json.Linq.JArray annotations)) return;
            foreach (var a in annotations.OfType<Newtonsoft.Json.Linq.JObject>().ToList())
            {
                var name = (string)Get(a, "name");
                if (IsAuditAnnotation(name)) a.Remove();
            }
            if (!annotations.HasValues) Remove(root, "annotations");
        }

        private static void CanonicalizeNamedArrays(Newtonsoft.Json.Linq.JToken token)
        {
            if (token is Newtonsoft.Json.Linq.JObject obj)
            {
                foreach (var p in obj.Properties().ToList()) CanonicalizeNamedArrays(p.Value);
                return;
            }
            if (!(token is Newtonsoft.Json.Linq.JArray arr)) return;
            foreach (var child in arr.ToList()) CanonicalizeNamedArrays(child);
            if (arr.All(x => x is Newtonsoft.Json.Linq.JObject && Get((Newtonsoft.Json.Linq.JObject)x, "name") != null))
            {
                var sorted = arr.OrderBy(x => (string)Get((Newtonsoft.Json.Linq.JObject)x, "name"), StringComparer.Ordinal).ToList();
                arr.RemoveAll(); foreach (var child in sorted) arr.Add(child);
            }
        }

        internal static (string Supplement, string Residual) LiveTableState(TOM.Table table)
        {
            var root = ParseObject(TableShell(table));
            var selected = Select(root, "defaultDetailRowsDefinition", "annotations");
            if (Get(root, "calculationGroup") is Newtonsoft.Json.Linq.JObject group)
            {
                var selectedGroup = Select(group, "description", "noSelectionExpression", "multipleOrEmptySelectionExpression", "annotations");
                if (selectedGroup.HasValues) selected["calculationGroup"] = selectedGroup;
            }
            var residual = (Newtonsoft.Json.Linq.JObject)root.DeepClone();
            Remove(residual, "name", "description", "isHidden", "dataCategory", "refreshPolicy", "defaultDetailRowsDefinition", "annotations");
            if (Get(residual, "calculationGroup") is Newtonsoft.Json.Linq.JObject residualGroup)
            {
                Remove(residualGroup, "precedence", "description", "noSelectionExpression", "multipleOrEmptySelectionExpression", "annotations");
                if (!residualGroup.HasValues) Remove(residual, "calculationGroup");
            }
            return (selected.ToString(Newtonsoft.Json.Formatting.None), residual.ToString(Newtonsoft.Json.Formatting.None));
        }

        internal static (string Supplement, string Residual) LiveModelState(TOM.Model model)
        {
            var root = ParseObject(ModelShell(model));
            var selected = Select(root, "annotations");
            Remove(root, "annotations");
            return (selected.ToString(Newtonsoft.Json.Formatting.None), root.ToString(Newtonsoft.Json.Formatting.None));
        }

        internal static (string Supplement, string Residual) LiveColumnState(TOM.Column column)
        {
            var root = ParseObject(Ser(column));
            var selected = Select(root, "annotations");
            Remove(root, "type", "name", "description", "isHidden", "dataCategory", "formatString",
                "displayFolder", "summarizeBy", "expression", "lineageTag", "changedProperties", "annotations");
            return (selected.ToString(Newtonsoft.Json.Formatting.None), root.ToString(Newtonsoft.Json.Formatting.None));
        }

        internal static (string Supplement, string Residual) LiveMeasureState(TOM.Measure measure)
        {
            var root = ParseObject(Ser(measure));
            var selected = Select(root, "annotations");
            Remove(root, "name", "description", "isHidden", "formatString", "displayFolder", "expression",
                "lineageTag", "changedProperties", "annotations");
            return (selected.ToString(Newtonsoft.Json.Formatting.None), root.ToString(Newtonsoft.Json.Formatting.None));
        }

        private static Newtonsoft.Json.Linq.JObject Select(Newtonsoft.Json.Linq.JObject source, params string[] names)
        {
            var result = new Newtonsoft.Json.Linq.JObject();
            foreach (var name in names)
            {
                var p = source.Properties().FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (p != null) result[p.Name] = p.Value.DeepClone();
            }
            return result;
        }

        internal static void CopyModelShell(TOM.Model source, TOM.Model target)
        {
            target.Name = source.Name; target.Description = source.Description;
            target.Culture = source.Culture; target.Collation = source.Collation;
            target.DefaultMode = source.DefaultMode; target.DefaultDataView = source.DefaultDataView;
            target.DefaultDirectLakeIndexingBehavior = source.DefaultDirectLakeIndexingBehavior;
            target.DefaultPowerBIDataSourceVersion = source.DefaultPowerBIDataSourceVersion;
            target.DirectLakeBehavior = source.DirectLakeBehavior;
            target.DiscourageCompositeModels = source.DiscourageCompositeModels;
            target.DiscourageImplicitMeasures = source.DiscourageImplicitMeasures;
            target.DiscourageReportMeasures = source.DiscourageReportMeasures;
            target.ForceUniqueNames = source.ForceUniqueNames; target.MAttributes = source.MAttributes;
            target.MaxParallelismPerQuery = source.MaxParallelismPerQuery;
            target.MaxParallelismPerRefresh = source.MaxParallelismPerRefresh;
            target.MetadataAccessPolicy = source.MetadataAccessPolicy;
            target.SelectionExpressionBehavior = source.SelectionExpressionBehavior;
            target.SourceQueryCulture = source.SourceQueryCulture; target.StorageLocation = source.StorageLocation;
            target.ValueFilterBehavior = source.ValueFilterBehavior;
            target.DataSourceDefaultMaxConnections = source.DataSourceDefaultMaxConnections;
            target.DataSourceVariablesOverrideBehavior = source.DataSourceVariablesOverrideBehavior;
            CopyAnnotations(source.Annotations.Cast<TOM.Annotation>().Where(a => !IsAuditAnnotation(a?.Name)), target.Annotations,
                preserve: a => IsAuditAnnotation(a?.Name));
        }

        internal static void CopyLiveTableSupplement(TOM.Table source, TOM.Table target)
        {
            target.DefaultDetailRowsDefinition = source.DefaultDetailRowsDefinition == null ? null : (TOM.DetailRowsDefinition)source.DefaultDetailRowsDefinition.Clone();
            CopyAnnotations(source.Annotations.Cast<TOM.Annotation>(), target.Annotations);
            if (source.CalculationGroup != null && target.CalculationGroup != null)
            {
                target.CalculationGroup.Description = source.CalculationGroup.Description;
                target.CalculationGroup.NoSelectionExpression = source.CalculationGroup.NoSelectionExpression == null ? null : (TOM.CalculationGroupExpression)source.CalculationGroup.NoSelectionExpression.Clone();
                target.CalculationGroup.MultipleOrEmptySelectionExpression = source.CalculationGroup.MultipleOrEmptySelectionExpression == null ? null : (TOM.CalculationGroupExpression)source.CalculationGroup.MultipleOrEmptySelectionExpression.Clone();
                CopyAnnotations(source.CalculationGroup.Annotations.Cast<TOM.Annotation>(), target.CalculationGroup.Annotations);
            }
        }

        internal static void CopyLiveModelSupplement(TOM.Model source, TOM.Model target)
            => CopyAnnotations(source.Annotations.Cast<TOM.Annotation>().Where(a => !IsAuditAnnotation(a?.Name)), target.Annotations,
                preserve: a => IsAuditAnnotation(a?.Name));

        internal static void CopyLiveColumnSupplement(TOM.Column source, TOM.Column target)
            => CopyAnnotations(source.Annotations.Cast<TOM.Annotation>(), target.Annotations);

        internal static void CopyLiveMeasureSupplement(TOM.Measure source, TOM.Measure target)
            => CopyAnnotations(source.Annotations.Cast<TOM.Annotation>(), target.Annotations);

        private static bool IsAuditAnnotation(string name)
            => name != null && (name.StartsWith("Semanticus_VerifiedEdits", StringComparison.Ordinal)
                || name == "TabularEditor_SerializeOptions" || name == "__TEdtr");

        // All TOM annotation collections expose the same named collection contract but use different concrete
        // generic collection types (model/table/calc-group). Dynamic is deliberately confined to this tiny adapter;
        // the values remain strongly-typed Annotation clones and the tests exercise every owner type.
        private static void CopyAnnotations(IEnumerable<TOM.Annotation> source, dynamic target, Func<TOM.Annotation, bool> preserve = null)
        {
            var src = source.Where(a => a?.Name != null).ToDictionary(a => a.Name, StringComparer.Ordinal);
            foreach (var existing in ((IEnumerable<TOM.Annotation>)target).ToList())
                if (!src.ContainsKey(existing.Name) && !(preserve?.Invoke(existing) == true)) target.Remove(existing);
            foreach (var annotation in src.Values)
            {
                if (target.Contains(annotation.Name)) target[annotation.Name].Value = annotation.Value;
                else target.Add(new TOM.Annotation { Name = annotation.Name, Value = annotation.Value });
            }
        }

        private static string TableDisplay(TOM.Table table, string shell) => TableProps(table) + Environment.NewLine + shell;

        private static string TableProps(TOM.Table t) => $"name={t.Name}; hidden={t.IsHidden}; dataCategory={t.DataCategory}; description={t.Description}"
            + $"; precedence={t.CalculationGroup?.Precedence.ToString() ?? "n/a"}; refreshPolicy={RefreshPolicyText(t) ?? "none"}";

        // Human-readable, order-stable text of a table's RefreshPolicy (null when absent) — the DISPLAY form ONLY,
        // used as the before/after text in TableProps. Equality is decided by the structured normalized table shell,
        // NEVER by comparing this string: the two free-text fields (source/polling) are concatenated unescaped here, so
        // distinct policies can render identically — fine to SHOW, unsafe to COMPARE (issue #124, the unescaped-concat
        // trap). A non-Basic policy (rare) degrades to its type name.
        private static string RefreshPolicyText(TOM.Table t)
        {
            if (t.RefreshPolicy == null) return null;
            if (!(t.RefreshPolicy is TOM.BasicRefreshPolicy p)) return t.RefreshPolicy.GetType().Name;
            return $"mode={p.Mode};incPeriods={p.IncrementalPeriods};incGran={p.IncrementalGranularity};rollPeriods={p.RollingWindowPeriods};rollGran={p.RollingWindowGranularity};incOffset={p.IncrementalPeriodsOffset};source={p.SourceExpression};polling={p.PollingExpression}";
        }

        // Build a ref via the ONE ALM grammar (escaped components): table==null ⇒ top-level, else a table-qualified child.
        private static string RefFor(string kind, string table, string name)
            => string.IsNullOrEmpty(table) ? AlmRef.Top(kind, name) : AlmRef.Child(kind, table, name);

        private static ModelDiffItem Mk(string refStr, string type, string name, string table, string action, string left, string right, bool matchedByName)
            => new ModelDiffItem { Ref = refStr, ObjectType = type, Name = name, Table = table, Action = action, LeftText = left, RightText = right, MatchedByName = matchedByName };

        // Attach the matched RIGHT object's identity so Apply resolves the target by identity, not the source name.
        private static ModelDiffItem Target(ModelDiffItem it, string tgtTag, string tgtName, string tgtTable, string tgtTableTag)
        {
            it.TargetTag = string.IsNullOrEmpty(tgtTag) ? null : tgtTag;
            it.TargetName = tgtName;
            it.TargetTable = tgtTable;
            it.TargetTableTag = string.IsNullOrEmpty(tgtTableTag) ? null : tgtTableTag;
            return it;
        }

        private static ModelDiffItem Ambiguous(string refStr, string type, string name, string table, string left, string reason)
        {
            var it = Mk(refStr, type, name, table, "Ambiguous", left, null, false);
            it.Reason = reason + " — nothing applied or deleted for this tag";
            return it;
        }

        // Flag a retag/republish half (a Create or the paired Delete): a same-named object with a DIFFERENT tag on the
        // other side. counterpart = the paired item's ref. The delete path refuses a flagged Delete by default.
        private static ModelDiffItem Republish(ModelDiffItem it, string counterpart)
        {
            it.LikelyRepublished = true;
            it.RetaggedCounterpart = counterpart;
            return it;
        }

        // Index by LineageTag. Sound because tags are UNIQUE within scope BY SPECIFICATION: "Lineage tags must be
        // unique within their scope; for instance, two tables in the same semantic model can't have the same lineage
        // tag." (learn.microsoft.com/en-us/analysis-services/tom/lineage-tags-for-power-bi-semantic-models, ms.date
        // 2025-01-06). So FirstOrDefault(tag) resolves at most one object.
        private static Dictionary<string, T> ByTag<T>(IEnumerable<T> items, Func<T, string> tag)
        {
            var d = new Dictionary<string, T>();
            foreach (var i in items) { var t = tag(i); if (!string.IsNullOrEmpty(t) && !d.ContainsKey(t)) d[t] = i; }
            return d;
        }

        // Tags that appear on MORE THAN ONE object. DEFENSE-IN-DEPTH against an invariant the PLATFORM PROMISES: the
        // spec (above) requires within-scope uniqueness, and raw TOM enforces it (rejects duplicate lineage tags on
        // both Add and deserialize), so this is normally empty and the Ambiguous branch never fires. It exists so that
        // IF a dupe ever reached this irreversible-delete lane, we refuse-and-report (Ambiguous) rather than silently
        // first-wins onto one twin and Delete the other.
        private static HashSet<string> DuplicateTags<T>(IEnumerable<T> items, Func<T, string> tag)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var dup = new HashSet<string>(StringComparer.Ordinal);
            foreach (var i in items) { var t = tag(i); if (string.IsNullOrEmpty(t)) continue; if (!seen.Add(t)) dup.Add(t); }
            return dup;
        }

        private static T Match<T>(string srcTag, string name, Dictionary<string, T> byTag, Func<string, T> byName, Func<T, string> liveTag) where T : class
            => Match(srcTag, name, byTag, byName, liveTag, out _);

        // Match by LineageTag; fall back to name ONLY when the tags are mutually empty or equal — so a tagged source
        // object never maps onto an unrelated same-named live object that carries a DIFFERENT tag. When it fails for
        // exactly that reason (same name, both tags non-empty, unequal), <paramref name="retagCandidate"/> reports the
        // colliding live object: that is a RETAG / REPUBLISH, not a delete+create, and the caller flags both halves.
        //
        // PROVENANCE — LEAVE THE ctag-EMPTY NAME FALLBACK AS IS (do not "fix" it to terminal). This is the DIFF pairing
        // for arbitrary source↔target compares: the LEFT artifact is a git TMDL / .bim / another workspace of ARBITRARY
        // provenance, legitimately untagged (git TMDL commonly is) OR tagged differently. An untagged source against a
        // tagged live target — or the reverse (srcTag set, ctag empty) — MUST still pair by name, or the diff shows the
        // ENTIRE model as Delete+Create for the whole source-control audience. The STRICT (terminal-on-miss) variant
        // lives in LiveDeploy.Match, gated on identityStrict, and is used ONLY by the selective push (where src is a live
        // snapshot, not an arbitrary artifact). Different provenance ⇒ different rule; do not unify them.
        private static T Match<T>(string srcTag, string name, Dictionary<string, T> byTag, Func<string, T> byName, Func<T, string> liveTag, out T retagCandidate) where T : class
        {
            retagCandidate = null;
            if (!string.IsNullOrEmpty(srcTag) && byTag.TryGetValue(srcTag, out var hit)) return hit;
            var cand = byName(name);
            if (cand == null) return null;
            var ctag = liveTag(cand);
            if (string.IsNullOrEmpty(srcTag) || string.IsNullOrEmpty(ctag) || string.Equals(srcTag, ctag, StringComparison.Ordinal)) return cand;
            retagCandidate = cand;   // same name, both tagged, tags DIFFER → do NOT pair (stays strict); report the retag
            return null;
        }

        private static bool JsonSemanticEqual(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.Ordinal)) return true;
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { return Newtonsoft.Json.Linq.JToken.DeepEquals(Newtonsoft.Json.Linq.JToken.Parse(a), Newtonsoft.Json.Linq.JToken.Parse(b)); }
            catch { return false; }
        }
    }
}
