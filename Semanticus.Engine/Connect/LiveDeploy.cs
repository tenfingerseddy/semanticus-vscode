using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TOM = Microsoft.AnalysisServices.Tabular;
using AS = Microsoft.AnalysisServices;

namespace Semanticus.Engine
{
    /// <summary>
    /// Metadata-only "save to the live model": pushes the OPEN SESSION's metadata — names, descriptions,
    /// visibility, data categories, format strings, display folders, summarize-by, measure/calc-column DAX,
    /// partition source expressions (M / calc-table DAX / legacy query), shared M expressions/parameters,
    /// and the linguistic schema (synonyms + AI instructions) — to a live XMLA model via Model.SaveChanges().
    /// The deploy half of <see cref="LiveModelExport"/>: open_live reads metadata out, deploy_live writes it back.
    ///
    /// Objects are matched by LineageTag (rename-safe; falls back to name ONLY when tags are mutually empty or
    /// equal, so a tagged-but-renamed object never maps onto an unrelated same-named live object).
    ///
    /// SAFETY:
    /// - DRY-RUN is a PURE READ-ONLY DIFF (commit=false): it computes + reports the change set and mutates nothing,
    ///   so a preview can never throw from in-memory TOM validation. Only commit=true applies the setters and calls
    ///   SaveChanges (the single atomic boundary — a SaveChanges failure writes nothing and is surfaced via Error).
    /// - METADATA ONLY: no data refresh. Partition source expressions ARE synced (they're metadata — dropping
    ///   them silently was a bug), but the data they produced stays STALE until the partition is refreshed.
    ///   Structural partition changes (add/remove, a source-type change, Direct Lake entity rebinding) are
    ///   REPORTED, never applied.
    /// - ADDS new MEASURES and new CALCULATED COLUMNS onto a matched live table (so authoring a measure and saving
    ///   it reaches the live model). A new CALCULATED TABLE IS created (its columns are engine-derived on the Calculate pass SyncSessionToLive runs after the save); new DATA (import/DirectQuery/Direct Lake) tables, new DATA columns and new cultures are NOT added (they
    ///   need a source/partition/structure binding we don't carry) — they're REPORTED (Unmatched) and left out.
    /// - DELETES follow TWO precise rules, never one loose one:
    ///     • ABSENCE NEVER DELETES. A live object merely missing from the session is REPORTED (LiveOnly) and LEFT
    ///       UNTOUCHED. In a whole-model deploy, absence is not evidence the user meant to drop it from production, so
    ///       the whole-model `deploy_live` path passes NO delete refs and keeps this guarantee byte-for-byte.
    ///     • AN EXPLICITLY-NAMED REF IS REMOVED. A selective push (apply_diff to a workspace target) may pass
    ///       <c>explicitDeleteRefs</c> — the object refs a user ticked as Delete. Those, and ONLY those, are removed
    ///       from the live model, inside the SAME SaveChanges as the adds/updates (a failure writes nothing). A named
    ///       ref that no longer resolves live is a reported NO-OP (someone else already removed it), never a throw.
    ///       Deleting a table with live dependents is rejected atomically by SaveChanges — the error surfaces verbatim.
    /// - Renames are collision-safe: a rename whose target name is already taken by a different live object is
    ///   skipped and reported (Conflicts), never thrown. (Known follow-up: a rename whose DAX dependents live ONLY
    ///   on the server isn't FormulaFixup-corrected here — SaveChanges rejects it atomically and the error surfaces.)
    ///
    /// The token goes via Server.AccessToken (AMO managed auth); a service principal is the reliable XMLA-write principal.
    /// </summary>
    public static class LiveDeploy
    {
        /// <summary>A local Analysis Services instance (Power BI Desktop) vs a cloud XMLA endpoint. Cloud endpoints
        /// always carry a scheme (powerbi:// asazure:// link:// https://); a local instance is a bare loopback
        /// host:port. Drives auth: local deploys with integrated Windows auth (no token), cloud needs a bearer token.
        /// Security note: a cloud endpoint is NEVER misread as local (so a token is never skipped for a real write),
        /// because any "://" classifies as remote.</summary>
        public static bool IsLocalEndpoint(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) return false;
            var e = endpoint.Trim();
            if (e.Contains("://")) return false;   // any scheme = a remote/cloud endpoint
            // Bare IPv6 loopback (with/without brackets) — handled before Split(':') since '::1'.Split(':')[0] is "".
            var lower = e.ToLowerInvariant();
            if (lower == "::1" || lower == "[::1]" || lower.StartsWith("[::1]:")) return true;
            var host = e.Split(':')[0].Trim().ToLowerInvariant();
            return host == "localhost" || host == "127.0.0.1" || host == ".";
        }

        /// <param name="explicitDeleteTargets">IDENTITY-carrying delete targets (see <see cref="LiveDeleteTarget"/>) to
        /// REMOVE from the live model — the ONLY delete path, driven by a selective push where the user explicitly ticked
        /// deletes. Each carries the lineage identity captured at diff time so the removal resolves the RIGHT object on
        /// the live state loaded HERE (a third state neither drift snapshot saw), never a same-named impostor.
        /// Null/empty ⇒ byte-for-byte the historic no-delete behaviour (the whole-model deploy passes nothing).</param>
        public static DeployReport SyncSessionToLive(string sessionBimPath, string endpoint, string database, string token, DateTimeOffset expiresOn, bool commit, IReadOnlyCollection<LiveDeleteTarget> explicitDeleteTargets = null, bool identityStrict = false)
        {
            var src = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(sessionBimPath), null, AS.CompatibilityMode.PowerBI).Model;

            using var server = new TOM.Server();
            // A token = cloud XMLA (AMO managed auth via Server.AccessToken). No token = a LOCAL instance
            // (Power BI Desktop) — connect with the current Windows identity, exactly as Tabular Editor writes
            // back to a running Desktop model. The caller (DeployLiveAsync) decides via IsLocalEndpoint.
            if (!string.IsNullOrEmpty(token))
                server.AccessToken = new AS.AccessToken(token, expiresOn);
            server.Connect("Data Source=" + endpoint);
            var liveDb = server.Databases.FindByName(database) ?? throw new InvalidOperationException($"Database '{database}' not found on the endpoint.");

            // The SERVER-FREE orchestration lives in SyncAndApply (offline-testable). We pass the live model's real
            // SaveChanges + calc-recalc as callbacks — a test passes in-memory recorders instead.
            return SyncAndApply(src, liveDb.Model, commit, endpoint, database, explicitDeleteTargets,
                saveChanges: () => liveDb.Model.SaveChanges(),
                recalcCalcTables: names =>
                {
                    foreach (var name in names) liveDb.Model.Tables.Find(name)?.RequestRefresh(TOM.RefreshType.Calculate);
                    liveDb.Model.SaveChanges();
                },
                identityStrict: identityStrict);
        }

        /// <summary>The server-free orchestration of a session→live push (offline-testable — the SaveChanges + calc-
        /// recalc are callbacks, so a test drives the abort/commit decision on an in-memory model). Applies the metadata
        /// (SyncModels), then the explicit deletes, then commits via <paramref name="saveChanges"/>.
        ///
        /// ATOMIC DELETES: a REFUSED delete means the live model drifted in a way we cannot reconcile — the diff the user
        /// approved no longer describes it — so we ABORT THE WHOLE PUSH (write nothing) rather than commit a partial
        /// selection (the metadata update WITHOUT the delete). We detect this with a non-mutating pass BEFORE the single
        /// SaveChanges. This is NOT the drift-override path: overrideReason covers drift on the SELECTED refs detected
        /// A-vs-B; a refusal is a different, later signal (seen only against the third live state loaded here) and is
        /// deliberately NOT overridable. An Absent delete is benign (someone already removed it; the intent is
        /// satisfied) and never aborts.</summary>
        internal static DeployReport SyncAndApply(TOM.Model src, TOM.Model live, bool commit, string endpoint, string database,
            IReadOnlyCollection<LiveDeleteTarget> explicitDeleteTargets, Action saveChanges, Action<IReadOnlyCollection<string>> recalcCalcTables, bool identityStrict = false)
        {
            var rep = SyncModels(src, live, commit, endpoint, database, identityStrict);

            // DETECTION PASS (never mutates): any Refused ⇒ abort before SaveChanges — nothing is committed.
            if (explicitDeleteTargets != null && explicitDeleteTargets.Count > 0)
            {
                var probe = new DeployReport();
                RemoveExplicit(live, explicitDeleteTargets, apply: false, probe);
                if (probe.DeletesRefused.Length > 0)
                {
                    rep.DeletesRefused = probe.DeletesRefused;
                    rep.Error = "Delete refused — the live model drifted since the diff: " + string.Join(", ", probe.DeletesRefused)
                        + " no longer resolve to the object you diffed (recreated / retagged / a different object now bears the name). NOTHING was pushed — re-diff against the current model. (This drift is NOT overridable; overrideReason covers only the drift detected at diff time.)";
                    return rep;   // abort BEFORE any SaveChanges — rep.Committed stays false
                }
            }

            // Clean: apply the deletes for real, inside the SAME change set as the adds/updates (one SaveChanges).
            RemoveExplicit(live, explicitDeleteTargets, commit, rep);
            if (commit && rep.TotalChanges > 0)
            {
                // The single atomic boundary — a SaveChanges failure writes nothing and is surfaced via Error.
                try { saveChanges(); rep.Committed = true; }
                catch (Exception ex) { rep.Error = "SaveChanges failed — nothing was committed: " + ex.Message; }
            }
            // NEW calculated tables are created EMPTY by the metadata save above; a Calculate pass populates their
            // engine-derived columns/rows. Recalc the ones we added in a SECOND commit — a recalc failure leaves them
            // created-but-empty and is surfaced (the metadata IS already committed), never thrown across the door.
            if (rep.Committed && rep.CalcTablesAdded.Length > 0)
            {
                try { recalcCalcTables(rep.CalcTablesAdded); }
                catch (Exception ex)
                {
                    rep.Error = $"Metadata committed, but the Calculate recalc of new table(s) [{string.Join(", ", rep.CalcTablesAdded)}] failed — they exist but stay empty until refreshed: {ex.Message}";
                }
            }
            return rep;
        }

        private enum RemoveOutcome { Deleted, Absent, Refused }

        /// <summary>Remove the EXPLICITLY-ticked objects from the live model — the ONLY delete path (a selective push).
        /// Never derives a delete from absence. Resolution is by IDENTITY (see <see cref="LiveDeleteTarget"/>): a carried
        /// lineage tag is authoritative and a MISS is TERMINAL — never a name-guess onto a same-named impostor (the
        /// TOCTOU wrong-object hazard on the only op with no undo). Three outcomes, distinct to the caller:
        ///   • Deleted — the identity resolved; removed (or would be on a dry run) within the caller's SaveChanges.
        ///   • Absent  — the object is genuinely GONE (no identity match AND no same-named object) — a benign no-op.
        ///   • Refused — the identity is gone BUT a DIFFERENT object now bears the name — we REFUSED to delete the wrong
        ///     object (a real signal: re-diff), never a silent no-op and never a wrong deletion.
        /// Server-free + in-memory (the offline-testable seam); the caller owns the SaveChanges boundary. apply=false
        /// reports without mutating. A REF IS FOR REPORTING, NEVER A RESOLVER.</summary>
        internal static void RemoveExplicit(TOM.Model live, IReadOnlyCollection<LiveDeleteTarget> targets, bool apply, DeployReport rep)
        {
            if (targets == null || targets.Count == 0) return;
            var deleted = new List<string>();
            var absent = new List<string>();
            var refused = new List<string>();
            var log = new List<string>();
            foreach (var t in targets)
            {
                if (t == null || string.IsNullOrEmpty(t.Kind)) continue;
                switch (TryRemoveLive(live, t, apply))
                {
                    case RemoveOutcome.Deleted: deleted.Add(t.Ref); log.Add("delete " + t.Ref); break;
                    case RemoveOutcome.Refused: refused.Add(t.Ref); log.Add("delete REFUSED (identity gone; a different object now bears this name) " + t.Ref); break;
                    default: absent.Add(t.Ref); log.Add("delete (already absent) " + t.Ref); break;
                }
            }
            rep.Deleted += deleted.Count;
            rep.DeletedRefs = rep.DeletedRefs.Concat(deleted).ToArray();
            rep.DeletesAlreadyAbsent = rep.DeletesAlreadyAbsent.Concat(absent).ToArray();
            rep.DeletesRefused = rep.DeletesRefused.Concat(refused).ToArray();
            rep.TotalChanges += deleted.Count;   // real removals are part of the SaveChanges change set
            if (log.Count > 0) rep.Changes = rep.Changes.Concat(log).Take(120).ToArray();
        }

        // Resolve a delete target to its live TOM object by IDENTITY and (apply) remove it. A carried tag is
        // authoritative; a miss is TERMINAL (Refused if a same-named impostor exists, else Absent). Raw-TOM removal
        // mirrors ModelCompare.Apply's delete paths — the parity reference.
        private static RemoveOutcome TryRemoveLive(TOM.Model live, LiveDeleteTarget t, bool apply)
        {
            switch (t.Kind)
            {
                case "table":
                    if (!string.IsNullOrEmpty(t.Tag))
                    {
                        var byTag = live.Tables.Cast<TOM.Table>().FirstOrDefault(x => x.LineageTag == t.Tag);
                        if (byTag != null) { if (apply) live.Tables.Remove(byTag); return RemoveOutcome.Deleted; }
                        return (!string.IsNullOrEmpty(t.Name) && live.Tables.Find(t.Name) != null) ? RemoveOutcome.Refused : RemoveOutcome.Absent;
                    }
                    else   // our snapshot's table carried no LineageTag → NAME is the identity
                    {
                        // A tag is OPTIONAL, caller-supplied, mutable and not necessarily a GUID; MS docs: "If semantic
                        // model objects don't have a lineage tag, Power BI defaults to using the object name" — so both-
                        // sides-untagged name resolution is the documented NORMAL case (TE2 models, and ANY model at
                        // compatibility level < 1540, where LineageTag is unsupported and reads as "" — verified: it does
                        // NOT throw). Never require a tag.
                        var byName = string.IsNullOrEmpty(t.Name) ? null : live.Tables.Find(t.Name);
                        if (byName == null) return RemoveOutcome.Absent;
                        // ASYMMETRY: we refuse when our UNTAGGED target resolves to a TAGGED live object — a tagged live
                        // object is not one we could have diffed untagged (recreated, retagged, or an impostor took the
                        // name). This is a STRICTLY STRONGER guarantee than the platform's (MS's own fallback is plain
                        // name resolution, with the rename hazard). Whether a delete+recreate always mints a fresh tag is
                        // NOT documented — the guard is correct either way because it FAILS CLOSED. (Both untagged →
                        // proceeds; no capability loss.)
                        if (!string.IsNullOrEmpty(byName.LineageTag)) return RemoveOutcome.Refused;
                        if (apply) live.Tables.Remove(byName); return RemoveOutcome.Deleted;
                    }
                case "measure":   return RemoveChild(live, t, apply, tb => tb.Measures.Cast<TOM.NamedMetadataObject>(), (tb, o) => tb.Measures.Remove((TOM.Measure)o));
                case "column":    return RemoveChild(live, t, apply, tb => tb.Columns.Cast<TOM.NamedMetadataObject>(), (tb, o) => tb.Columns.Remove((TOM.Column)o));
                case "hierarchy": return RemoveChild(live, t, apply, tb => tb.Hierarchies.Cast<TOM.NamedMetadataObject>(), (tb, o) => tb.Hierarchies.Remove((TOM.Hierarchy)o));
                case "partition": return RemoveChild(live, t, apply, tb => tb.Partitions.Cast<TOM.NamedMetadataObject>(), (tb, o) => tb.Partitions.Remove((TOM.Partition)o));
                // A calculation item is a name-keyed child of a table's CalculationGroup (issue #124). RemoveChild resolves
                // the owning table by TableTag (authoritative) then the item by NAME within it (calc items are tag-less, so
                // the asymmetry guard never fires). Without this case a ticked calc-item delete hit the `default` below →
                // RemoveOutcome.Absent → a SILENTLY DROPPED delete (worse than the diff being blind). A null CalculationGroup
                // yields an empty child set → a benign Absent no-op (nothing to remove).
                case "calculationitem": return RemoveChild(live, t, apply, tb => tb.CalculationGroup?.CalculationItems.Cast<TOM.NamedMetadataObject>() ?? Enumerable.Empty<TOM.NamedMetadataObject>(), (tb, o) => tb.CalculationGroup?.CalculationItems.Remove((TOM.CalculationItem)o));
                case "role":        return RemoveTop(live.Roles.Cast<TOM.NamedMetadataObject>(), t, apply, o => live.Roles.Remove((TOM.ModelRole)o));
                case "perspective": return RemoveTop(live.Perspectives.Cast<TOM.NamedMetadataObject>(), t, apply, o => live.Perspectives.Remove((TOM.Perspective)o));
                case "culture":     return RemoveTop(live.Cultures.Cast<TOM.NamedMetadataObject>(), t, apply, o => live.Cultures.Remove((TOM.Culture)o));
                case "datasource":  return RemoveTop(live.DataSources.Cast<TOM.NamedMetadataObject>(), t, apply, o => live.DataSources.Remove((TOM.DataSource)o));
                case "expression":  return RemoveTop(live.Expressions.Cast<TOM.NamedMetadataObject>(), t, apply, o => live.Expressions.Remove((TOM.NamedExpression)o));
                case "relationship":
                {
                    // A relationship's identity is its structural endpoint signature (name-independent), carried whole in
                    // t.Name and compared via RelSig — no tag, no name-guess to degrade.
                    var o = live.Relationships.Cast<TOM.Relationship>().FirstOrDefault(r => RelSig(r) == t.Name);
                    if (o == null) return RemoveOutcome.Absent;
                    if (apply) live.Relationships.Remove(o); return RemoveOutcome.Deleted;
                }
                default: return RemoveOutcome.Absent;   // unknown kind = unresolvable no-op, never throw
            }
        }

        // Resolve a table child by identity. The OWNING TABLE is resolved by TableTag (authoritative; a miss is terminal
        // — Refused if a same-named table now exists, else Absent). Then the CHILD: a tagged child kind (measure/column/
        // hierarchy) by its own tag (terminal on miss); a tag-less child (partition) by name within the resolved table.
        private static RemoveOutcome RemoveChild(TOM.Model live, LiveDeleteTarget t, bool apply,
            Func<TOM.Table, IEnumerable<TOM.NamedMetadataObject>> children, Action<TOM.Table, TOM.NamedMetadataObject> remove)
        {
            TOM.Table table;
            if (!string.IsNullOrEmpty(t.TableTag))
            {
                table = live.Tables.Cast<TOM.Table>().FirstOrDefault(x => x.LineageTag == t.TableTag);
                if (table == null)
                    return (!string.IsNullOrEmpty(t.Table) && live.Tables.Find(t.Table) != null) ? RemoveOutcome.Refused : RemoveOutcome.Absent;
            }
            else
            {
                table = string.IsNullOrEmpty(t.Table) ? null : live.Tables.Find(t.Table);
                if (table == null) return RemoveOutcome.Absent;
                // ASYMMETRY (owning table): our ref carried no table tag, but the name-resolved live table IS tagged ⇒
                // not the table we diffed — recreated/retagged, or an impostor. Refuse rather than reach inside it.
                if (!string.IsNullOrEmpty(table.LineageTag)) return RemoveOutcome.Refused;
            }
            if (!string.IsNullOrEmpty(t.Tag))   // tagged child kind → tag authoritative, miss terminal
            {
                var byTag = children(table).FirstOrDefault(c => LineageOf(c) == t.Tag);
                if (byTag != null) { if (apply) remove(table, byTag); return RemoveOutcome.Deleted; }
                return (!string.IsNullOrEmpty(t.Name) && children(table).Any(c => c.Name == t.Name)) ? RemoveOutcome.Refused : RemoveOutcome.Absent;
            }
            var byName = string.IsNullOrEmpty(t.Name) ? null : children(table).FirstOrDefault(c => c.Name == t.Name);   // tag-less child (partition) → name
            if (byName == null) return RemoveOutcome.Absent;
            // ASYMMETRY (child): our target child carried no tag, but the name-resolved live child IS tagged ⇒
            // recreated/retagged. Refuse. Partitions carry no lineage tag (LineageOf → null) → never refused here.
            if (!string.IsNullOrEmpty(LineageOf(byName))) return RemoveOutcome.Refused;
            if (apply) remove(table, byName); return RemoveOutcome.Deleted;
        }

        // Top-level kinds: role / perspective / culture / datasource / expression. Resolution is INTERFACE-driven (via
        // LineageOf), not a hardcoded tagged/tag-less split. role/perspective/culture/datasource do NOT implement
        // IMetadataObjectWithLineage → LineageOf is null → the tag path never engages and they resolve by NAME (a rename
        // reads as Delete+Create). NamedExpression DOES implement it, so a tagged expression resolves TAG-authoritatively
        // (a miss is terminal, exactly like a measure), with name resolution + the untagged→tagged asymmetry guard when
        // no tag is carried. This closes the "NamedExpression is taggable but treated as tag-less" wrong-object gap.
        private static RemoveOutcome RemoveTop(IEnumerable<TOM.NamedMetadataObject> coll, LiveDeleteTarget t, bool apply, Action<TOM.NamedMetadataObject> remove)
        {
            if (!string.IsNullOrEmpty(t.Tag))   // tag-authoritative (a tagged NamedExpression) — a miss is TERMINAL
            {
                var byTag = coll.FirstOrDefault(x => LineageOf(x) == t.Tag);
                if (byTag != null) { if (apply) remove(byTag); return RemoveOutcome.Deleted; }
                return (!string.IsNullOrEmpty(t.Name) && coll.Any(x => x.Name == t.Name)) ? RemoveOutcome.Refused : RemoveOutcome.Absent;
            }
            var o = string.IsNullOrEmpty(t.Name) ? null : coll.FirstOrDefault(x => x.Name == t.Name);
            if (o == null) return RemoveOutcome.Absent;
            // ASYMMETRY: our untagged target resolved to a TAGGED live object ⇒ not the object we diffed (only
            // NamedExpression can trigger this — LineageOf is null for role/perspective/culture/datasource, so it never
            // fires for them). Refuse. Fails closed.
            if (!string.IsNullOrEmpty(LineageOf(o))) return RemoveOutcome.Refused;
            if (apply) remove(o); return RemoveOutcome.Deleted;
        }

        // Lineage identity is INTERFACE-driven, not a hardcoded kind list (a list that drifts from reality is a
        // wrong-object delete). A TOM object supports a lineage tag IFF it implements IMetadataObjectWithLineage
        // (Table.LineageTag et al. are declared on that interface). Empirically, in AMO 19.114 (verified 2026-07-09),
        // these IMPLEMENT it: Table, Column (Data/Calculated), Measure, Hierarchy, Level, NamedExpression; these do NOT:
        // Partition, Relationship, ModelRole, Perspective, Culture, DataSource, CalculationItem. So a Partition (a
        // tag-less child) returns null here → the child asymmetry guard never fires for it → name resolution, correct.
        // NOTE: IMetadataObjectWithLineage also exposes SourceLineageTag — a DIFFERENT thing that binds a composite /
        // Direct Lake model to an object in a SOURCE model. It is NEVER an identity signal here; do not read it.
        private static string LineageOf(TOM.MetadataObject o) => (o as TOM.IMetadataObjectWithLineage)?.LineageTag;

        // LineageTag is unsupported below compatibility level 1540 — READING it returns "" (safe), but SETTING it throws
        // CompatibilityViolationException (measured, AMO 19.114). We guard tag WRITES (new tables / calc columns /
        // measures / expressions) on the TARGET's level, not the source's, so pushing a tagged modern source into an
        // older live model creates the object UNTAGGED — a lossless degrade — instead of throwing across the door
        // (SyncModels runs OUTSIDE the SaveChanges try, so an escape would not be a clean DeployReport.Error).
        private static bool SupportsLineage(TOM.Model live)
        {
            var cl = live.Database?.CompatibilityLevel ?? 0;
            // UNKNOWN (0 — a detached model, or a level we couldn't read) ⇒ do NOT write the tag. Deliberate SAFE-degrade
            // (option a): the object is still created, just untagged (lossless) — the same degrade as CL<1540. The unsafe
            // alternative (attempt + hope) can throw CompatibilityViolationException OUTSIDE the SaveChanges try (SyncModels
            // runs there) and escape across the door. In production `live` is always a real attached DB with a real level,
            // so this only affects detached-model paths; failing closed is correct.
            return cl >= 1540;
        }

        // The endpoint signature ModelCompare.Diff carries in a relationship ref — name-independent, so a delete finds
        // the structurally-equal live relationship even when the two models named it differently. Kept BYTE-FOR-BYTE in
        // sync with ModelCompare.RelSig (the ref producer), incl. the AlmRef.EscRel escaping that makes the sig
        // injective (a name with '[' ']' '->' can't forge a boundary → no two relationships share a sig → the delete
        // never lands on the wrong one). A non-single-column relationship (rare) falls back to its (escaped) name.
        private static string RelSig(TOM.Relationship r)
            => r is TOM.SingleColumnRelationship sc && sc.FromColumn != null && sc.ToColumn != null
                ? AlmRef.EscRel(sc.FromColumn.Table?.Name) + "[" + AlmRef.EscRel(sc.FromColumn.Name) + "]->" + AlmRef.EscRel(sc.ToColumn.Table?.Name) + "[" + AlmRef.EscRel(sc.ToColumn.Name) + "]"
                : AlmRef.EscRel(r.Name);

        /// <summary>The server-free diff/apply core (offline-testable): computes the change set between the
        /// session model and a live model and (apply=true) mutates the live TOM tree IN MEMORY — the caller
        /// owns the SaveChanges boundary. Dry-run (apply=false) never mutates. <paramref name="identityStrict"/> gates
        /// the LineageTag match provenance (see <see cref="Match{T}(string,string,Dictionary{string,T},Func{string,T},Func{T,string},bool,out T)"/>):
        /// false (deploy_live, arbitrary session) name-falls-back; true (selective push, src = a live snapshot) makes a
        /// non-empty tag miss TERMINAL and reports the same-named live object as unmatched instead of mutating it.</summary>
        internal static DeployReport SyncModels(TOM.Model src, TOM.Model live, bool apply, string endpoint = "", string database = "", bool identityStrict = false)
        {
            // Retagged/replaced-under-us reason (strict only): a same-named live object carries a DIFFERENT lineage tag.
            string RetagConflict(string objRef) => objRef + " (a same-named live object carries a DIFFERENT lineage tag — republished/retagged under you; NOT synced to avoid mutating the wrong object. Re-diff against the current model.)";
            var rep = new DeployReport { Endpoint = endpoint, Database = database, Committed = false };
            var changes = new List<string>();
            var unmatched = new List<string>();
            var liveOnly = new List<string>();
            // Live-only CHILDREN (of matched tables) are collected as (table, kind, childName) and their ref STRINGS are
            // built AFTER the renames loop — so a ref under a renamed table follows the table's FINAL name (post-rename,
            // or the OLD name if the rename collided and was skipped). Building them inline would freeze the pre-rename
            // name into a ref that resolves to nothing (invariant (a): a ref is for reporting; it must name what live holds).
            var liveOnlyChildren = new List<(TOM.Table table, string kind, string child)>();
            var conflicts = new List<string>();
            // Object-level, SOURCE-keyed refs this deploy actually carried (adds + updates). The caller reconciles a
            // local merge against this so a merged-but-not-deployed object (e.g. a relationship/role this path doesn't
            // sync) is never reported as a live success. Uses the SAME ref grammar ModelCompare.Diff emits (source
            // names), so it lines up with the outcome.Applied refs a selective push produced.
            var syncedRefs = new HashSet<string>(StringComparer.Ordinal);
            // Refs WITHHELD from SyncedRefs: some PART of the object did not deploy (an unapplied refresh policy, a
            // refused ordinal, an unsyncable residual). The parts that did apply are still counted/logged, but the
            // object-level ref must NOT claim a full apply — the caller reconciles ref-by-ref, and a claimed ref with a
            // silently-dropped part is the false-success family. Removed AFTER the renames loop (a rename re-adds the
            // table ref), right before rep.SyncedRefs is built.
            var heldRefs = new HashSet<string>(StringComparer.Ordinal);
            var calcTablesAdded = new List<string>();   // new calc tables to Calculate-recalc after the metadata save
            int desc = 0, ren = 0, vis = 0, cat = 0, fmt = 0, fold = 0, expr = 0, sum = 0, cult = 0, part = 0, nexpr = 0, added = 0, calc = 0, metadata = 0;
            var renames = new List<(TOM.NamedMetadataObject obj, string oldName, string newName, Func<string, TOM.NamedMetadataObject> findSibling, string id)>();

            // Model-container metadata is a first-class diff item now (#135). Arbitrary non-audit annotations have
            // a safe live carrier; any other model-shell difference is named and withheld instead of silently lost.
            var sourceModelState = ModelCompare.LiveModelState(src);
            var liveModelState = ModelCompare.LiveModelState(live);
            if (!JsonSemanticEqual(sourceModelState.Residual, liveModelState.Residual))
            {
                unmatched.Add("model (carries authored model-level metadata this live push cannot sync; deploy via TMDL/XMLA)");
                heldRefs.Add("model");
            }
            if (!JsonSemanticEqual(sourceModelState.Supplement, liveModelState.Supplement))
            {
                try
                {
                    if (apply) ModelCompare.CopyLiveModelSupplement(src, live);
                    metadata++; changes.Add("metadata model"); syncedRefs.Add("model");
                }
                catch (Exception ex) { unmatched.Add("model (metadata was not applied: " + ex.Message + ")"); heldRefs.Add("model"); }
            }

            var liveTablesByTag = ByTag(live.Tables.Cast<TOM.Table>(), t => t.LineageTag);
            var matchedTables = new HashSet<TOM.Table>();   // by REFERENCE — survives a rename (don't key on mutable Name)

            foreach (var st in src.Tables)
            {
                var lt = Match(st.LineageTag, st.Name, liveTablesByTag, n => live.Tables.Find(n), t => t.LineageTag, identityStrict, out var tConflict);
                if (lt == null && tConflict != null) { unmatched.Add(RetagConflict(AlmRef.Top("table", st.Name))); continue; }
                if (lt == null)
                {
                    // NEW table. A CALCULATED table is self-contained DAX — the engine derives its columns on a
                    // Calculate pass — so we can create it live + recalc it (bounded, all-proven primitives). A
                    // data-bearing (import / DirectQuery / Direct Lake) table needs a source/M/entity binding we
                    // don't carry here, so it stays REPORTED (add via TMDL/XMLA), never silently dropped.
                    var only = st.Partitions.Count == 1 ? st.Partitions[0] : null;
                    if (only?.Source is TOM.CalculatedPartitionSource ccs)
                    {
                        var tableRef = AlmRef.Top("table", st.Name);
                        var nt = new TOM.Table { Name = st.Name };
                        if (SupportsLineage(live) && !string.IsNullOrEmpty(st.LineageTag)) nt.LineageTag = st.LineageTag;
                        nt.Description = st.Description; nt.IsHidden = st.IsHidden; nt.DataCategory = st.DataCategory;
                        nt.Partitions.Add(new TOM.Partition { Name = only.Name, Source = new TOM.CalculatedPartitionSource { Expression = ccs.Expression } });
                        ModelCompare.CopyLiveTableSupplement(st, nt);
                        var newTableSourceState = ModelCompare.LiveTableState(st);
                        var newTableState = ModelCompare.LiveTableState(nt);
                        if (!JsonSemanticEqual(newTableSourceState.Residual, newTableState.Residual)
                            || !RefreshPolicyEqual(st.RefreshPolicy, nt.RefreshPolicy))
                        {
                            unmatched.Add(tableRef + " (carries authored table metadata this live push cannot create; deploy via TMDL/XMLA)");
                            heldRefs.Add(tableRef);
                            continue;
                        }
                        if (apply)
                        {
                            live.Tables.Add(nt); matchedTables.Add(nt);   // created — keep it out of the live-only sweep
                        }
                        added++; calcTablesAdded.Add(st.Name); changes.Add("add calcTable:" + st.Name); syncedRefs.Add(tableRef);
                    }
                    else
                        unmatched.Add(AlmRef.Top("table", st.Name) + $" (new {(only == null ? "table" : "data")} table — not deployable here; add via TMDL/XMLA)");
                    continue;
                }
                matchedTables.Add(lt);

                // syncedRefs on NON-rename property changes here; a rename is recorded only when it actually applies
                // (the renames loop below), since a rename can be SKIPPED on a live name collision (→ Conflicts).
                int tChanges = changes.Count;
                var tid = AlmRef.Top("table", lt.Name);
                PlanRename(st.Name, lt, n => live.Tables.Find(n), tid, renames);
                SetStr(st.Description, () => lt.Description, v => lt.Description = v, apply, ref desc, "description " + tid, changes);
                SetBool(st.IsHidden, () => lt.IsHidden, v => lt.IsHidden = v, apply, ref vis, "visibility " + tid, changes);
                SetStr(st.DataCategory, () => lt.DataCategory, v => lt.DataCategory = v, apply, ref cat, "dataCategory " + tid, changes);
                if (changes.Count > tChanges) syncedRefs.Add(AlmRef.Top("table", st.Name));

                // The table shell covers detail rows, annotations and calc-group selection expressions in addition
                // to the long-standing display props. Carry the safe supplement; explicitly report/withhold every
                // residual property so a selective push cannot reconcile a partly-carried table as fully applied.
                var sourceTableRef = AlmRef.Top("table", st.Name);
                var sourceTableState = ModelCompare.LiveTableState(st);
                var liveTableState = ModelCompare.LiveTableState(lt);
                if (!JsonSemanticEqual(sourceTableState.Residual, liveTableState.Residual))
                {
                    unmatched.Add(sourceTableRef + " (carries authored table-level metadata this live push cannot sync; deploy via TMDL/XMLA)");
                    heldRefs.Add(sourceTableRef);
                }
                if (!JsonSemanticEqual(sourceTableState.Supplement, liveTableState.Supplement))
                {
                    try
                    {
                        if (apply) ModelCompare.CopyLiveTableSupplement(st, lt);
                        metadata++; changes.Add("metadata " + tid); syncedRefs.Add(sourceTableRef);
                    }
                    catch (Exception ex) { unmatched.Add(sourceTableRef + " (metadata was not applied: " + ex.Message + ")"); heldRefs.Add(sourceTableRef); }
                }

                var liveColsByTag = ByTag(lt.Columns.Cast<TOM.Column>(), c => c.LineageTag);
                var matchedCols = new HashSet<TOM.Column>();
                foreach (var sc in st.Columns)
                {
                    if (sc.Type == TOM.ColumnType.RowNumber) continue;
                    var capturedTable = lt;
                    var lc = Match(sc.LineageTag, sc.Name, liveColsByTag, n => capturedTable.Columns.Find(n), c => c.LineageTag, identityStrict, out var cConflict);
                    if (lc == null && cConflict != null) { unmatched.Add(RetagConflict(AlmRef.Child("column", lt.Name, sc.Name))); continue; }
                    if (lc == null)
                    {
                        // NEW column: a CALCULATED column is just an expression, so we can add it. A new DATA column
                        // needs a source/M binding we don't have here — report it (add it via TMDL/XMLA instead).
                        if (sc is TOM.CalculatedColumn scNew)
                        {
                            var newColumnRef = AlmRef.Child("column", st.Name, sc.Name);
                            var ncc = new TOM.CalculatedColumn { Name = sc.Name, Expression = scNew.Expression };
                            if (SupportsLineage(live) && !string.IsNullOrEmpty(sc.LineageTag)) ncc.LineageTag = sc.LineageTag;
                            ncc.Description = sc.Description; ncc.IsHidden = sc.IsHidden; ncc.DataCategory = sc.DataCategory;
                            ncc.FormatString = sc.FormatString; ncc.DisplayFolder = sc.DisplayFolder; ncc.SummarizeBy = sc.SummarizeBy;
                            ModelCompare.CopyLiveColumnSupplement(sc, ncc);
                            var newColumnSourceState = ModelCompare.LiveColumnState(sc);
                            var newColumnState = ModelCompare.LiveColumnState(ncc);
                            if (!JsonSemanticEqual(newColumnSourceState.Residual, newColumnState.Residual))
                            {
                                unmatched.Add(newColumnRef + " (carries authored column metadata this live push cannot create; deploy via TMDL/XMLA)");
                                heldRefs.Add(newColumnRef);
                                continue;
                            }
                            if (apply) { lt.Columns.Add(ncc); matchedCols.Add(ncc); }
                            added++; changes.Add($"add calcColumn:{lt.Name}/{sc.Name}"); syncedRefs.Add(newColumnRef);
                        }
                        else unmatched.Add(AlmRef.Child("column", lt.Name, sc.Name) + " (new data column — not deployable here; add via TMDL/XMLA)");
                        continue;
                    }
                    matchedCols.Add(lc);
                    var id = AlmRef.Child("column", lt.Name, lc.Name);
                    int cChanges = changes.Count;
                    PlanRename(sc.Name, lc, n => capturedTable.Columns.Find(n), id, renames);
                    SetStr(sc.Description, () => lc.Description, v => lc.Description = v, apply, ref desc, "description " + id, changes);
                    SetBool(sc.IsHidden, () => lc.IsHidden, v => lc.IsHidden = v, apply, ref vis, "visibility " + id, changes);
                    SetStr(sc.DataCategory, () => lc.DataCategory, v => lc.DataCategory = v, apply, ref cat, "dataCategory " + id, changes);
                    SetStr(sc.FormatString, () => lc.FormatString, v => lc.FormatString = v, apply, ref fmt, "format " + id, changes);
                    SetStr(sc.DisplayFolder, () => lc.DisplayFolder, v => lc.DisplayFolder = v, apply, ref fold, "folder " + id, changes);
                    if (sc.SummarizeBy != lc.SummarizeBy) { if (apply) lc.SummarizeBy = sc.SummarizeBy; sum++; changes.Add("summarizeBy " + id); }
                    if (sc is TOM.CalculatedColumn scc)
                    {
                        if (lc is TOM.CalculatedColumn lcc) SetExpr(scc.Expression, () => lcc.Expression, v => lcc.Expression = v, apply, ref expr, "expression " + id, changes);
                        else unmatched.Add($"expression-type-mismatch {id} (session CalculatedColumn, live {lc.GetType().Name} — DAX not deployed)");
                    }
                    var columnRef = AlmRef.Child("column", st.Name, sc.Name);
                    var sourceColumnState = ModelCompare.LiveColumnState(sc);
                    var liveColumnState = ModelCompare.LiveColumnState(lc);
                    if (!JsonSemanticEqual(sourceColumnState.Supplement, liveColumnState.Supplement))
                    {
                        try
                        {
                            if (apply) ModelCompare.CopyLiveColumnSupplement(sc, lc);
                            metadata++; changes.Add("metadata " + id);
                        }
                        catch (Exception ex) { unmatched.Add(columnRef + " (metadata was not applied: " + ex.Message + ")"); heldRefs.Add(columnRef); }
                    }
                    if (!JsonSemanticEqual(sourceColumnState.Residual, liveColumnState.Residual))
                    {
                        unmatched.Add(columnRef + " (carries authored column metadata this live push cannot sync; deploy via TMDL/XMLA)");
                        heldRefs.Add(columnRef);
                    }
                    if (changes.Count > cChanges) syncedRefs.Add(columnRef);
                }

                var liveMeasByTag = ByTag(lt.Measures.Cast<TOM.Measure>(), m => m.LineageTag);
                var matchedMeas = new HashSet<TOM.Measure>();
                foreach (var sm in st.Measures)
                {
                    var capturedTable = lt;
                    var lm = Match(sm.LineageTag, sm.Name, liveMeasByTag, n => capturedTable.Measures.Find(n), m => m.LineageTag, identityStrict, out var mConflict);
                    if (lm == null && mConflict != null) { unmatched.Add(RetagConflict(AlmRef.Child("measure", lt.Name, sm.Name))); continue; }
                    if (lm == null)
                    {
                        // NEW measure → ADD it to the matched live table (carries the property set we sync on updates).
                        var newMeasureRef = AlmRef.Child("measure", st.Name, sm.Name);
                        var nm = new TOM.Measure { Name = sm.Name, Expression = sm.Expression };
                        if (SupportsLineage(live) && !string.IsNullOrEmpty(sm.LineageTag)) nm.LineageTag = sm.LineageTag;
                        nm.Description = sm.Description; nm.IsHidden = sm.IsHidden; nm.FormatString = sm.FormatString;
                        nm.DisplayFolder = sm.DisplayFolder;
                        ModelCompare.CopyLiveMeasureSupplement(sm, nm);
                        var newMeasureSourceState = ModelCompare.LiveMeasureState(sm);
                        var newMeasureState = ModelCompare.LiveMeasureState(nm);
                        if (!JsonSemanticEqual(newMeasureSourceState.Residual, newMeasureState.Residual))
                        {
                            unmatched.Add(newMeasureRef + " (carries authored measure metadata this live push cannot create; deploy via TMDL/XMLA)");
                            heldRefs.Add(newMeasureRef);
                            continue;
                        }
                        if (apply) { lt.Measures.Add(nm); matchedMeas.Add(nm); }
                        added++; changes.Add($"add measure:{lt.Name}/{sm.Name}"); syncedRefs.Add(newMeasureRef);
                        continue;
                    }
                    matchedMeas.Add(lm);
                    var id = AlmRef.Child("measure", lt.Name, lm.Name);
                    int mChanges = changes.Count;
                    PlanRename(sm.Name, lm, n => capturedTable.Measures.Find(n), id, renames);
                    SetStr(sm.Description, () => lm.Description, v => lm.Description = v, apply, ref desc, "description " + id, changes);
                    SetBool(sm.IsHidden, () => lm.IsHidden, v => lm.IsHidden = v, apply, ref vis, "visibility " + id, changes);
                    SetStr(sm.FormatString, () => lm.FormatString, v => lm.FormatString = v, apply, ref fmt, "format " + id, changes);
                    SetStr(sm.DisplayFolder, () => lm.DisplayFolder, v => lm.DisplayFolder = v, apply, ref fold, "folder " + id, changes);
                    SetExpr(sm.Expression, () => lm.Expression, v => lm.Expression = v, apply, ref expr, "expression " + id, changes);
                    var measureRef = AlmRef.Child("measure", st.Name, sm.Name);
                    var sourceMeasureState = ModelCompare.LiveMeasureState(sm);
                    var liveMeasureState = ModelCompare.LiveMeasureState(lm);
                    if (!JsonSemanticEqual(sourceMeasureState.Supplement, liveMeasureState.Supplement))
                    {
                        try
                        {
                            if (apply) ModelCompare.CopyLiveMeasureSupplement(sm, lm);
                            metadata++; changes.Add("metadata " + id);
                        }
                        catch (Exception ex) { unmatched.Add(measureRef + " (metadata was not applied: " + ex.Message + ")"); heldRefs.Add(measureRef); }
                    }
                    if (!JsonSemanticEqual(sourceMeasureState.Residual, liveMeasureState.Residual))
                    {
                        unmatched.Add(measureRef + " (carries authored measure metadata this live push cannot sync; deploy via TMDL/XMLA)");
                        heldRefs.Add(measureRef);
                    }
                    if (changes.Count > mChanges) syncedRefs.Add(measureRef);
                }

                // Partitions: the source EXPRESSION (M / calc-table DAX / legacy query) is metadata and syncs on
                // name-matched partitions — an M edit must reach live or be REPORTED, never silently
                // dropped (the data it produced stays stale until the partition refreshes; no refresh here).
                // Structural changes (add/remove, a source-type change, entity rebinding) report, never apply.
                var matchedParts = new HashSet<TOM.Partition>();
                foreach (var sp in st.Partitions)
                {
                    var lp = lt.Partitions.Find(sp.Name);
                    if (lp == null) { unmatched.Add(AlmRef.Child("partition", lt.Name, sp.Name) + " (new partition — not deployable here; add via TMDL/XMLA)"); continue; }
                    matchedParts.Add(lp);
                    var pid = AlmRef.Child("partition", lt.Name, lp.Name);
                    var (sKind, sScript) = PartitionScript(sp);
                    var (lKind, lScript) = PartitionScript(lp);
                    if (sKind != lKind) { unmatched.Add($"source-type-mismatch {pid} (session {sKind ?? "none"}, live {lKind ?? "none"} — expression not deployed)"); continue; }
                    if (sKind == "entity")
                    {
                        // Direct Lake: rebinding a partition to another lakehouse entity is structural — report it.
                        if (!string.Equals(sScript, lScript, StringComparison.Ordinal))
                            unmatched.Add($"entity-rebinding {pid} ({lScript} -> {sScript} — not deployable here; change via TMDL/XMLA)");
                        continue;
                    }
                    if (sScript == null && lScript == null) continue;   // no comparable script on this source kind
                    if (!string.Equals(sScript, lScript, StringComparison.Ordinal))
                    {
                        if (apply) SetPartitionScript(lp, sScript);
                        part++; changes.Add("expression " + pid); syncedRefs.Add(AlmRef.Child("partition", st.Name, sp.Name));
                    }
                }
                foreach (var lp in lt.Partitions.Cast<TOM.Partition>().Where(p => !matchedParts.Contains(p)))
                    liveOnlyChildren.Add((lt, "partition", lp.Name));   // ref built post-rename (see below)

                // Calculation-group ITEMS of a matched calc-group table (issue #124). A calc item is a NAME-KEYED child
                // of the table's CalculationGroup (tag-less — verified AMO 19.114), so match by name, mirroring
                // partitions. WITHOUT this loop the live push walked columns/measures/partitions only: a calc-item
                // create/update never reached live (the merge claimed it, the deploy dropped it → reconciled to Failed),
                // and a RENAME was DESTRUCTIVE — Diff emits Create(new)+Delete(old); the delete channel removed the old
                // live item while nothing created the new one, so the push deleted without creating. Create/update/
                // ordinal/format-string/description sync here now; a rename is the Create leg here + the Delete leg via
                // the explicit channel (name-keyed, exactly like a partition). Live-only items are reported, untouched.
                if (st.CalculationGroup != null || lt.CalculationGroup != null)
                {
                    var srcItems = st.CalculationGroup?.CalculationItems.Cast<TOM.CalculationItem>().ToList() ?? new List<TOM.CalculationItem>();
                    if (lt.CalculationGroup == null)
                    {
                        // A calc item can only land on a live table that HAS a calculation group. A plain live table
                        // receiving one is a table-KIND transition (structural) — report it (SOURCE-keyed ref, so the
                        // selective push reconciles it against the diff's refs), never silently drop it.
                        foreach (var sci in srcItems)
                            unmatched.Add(AlmRef.Child("calculationitem", st.Name, sci.Name) + " (live table has no calculation group — add via TMDL/XMLA)");
                    }
                    else
                    {
                        var group = lt.CalculationGroup;
                        // CLASSIFY first — the ordinal projection needs the WHOLE picture (matched / new / live-only)
                        // before anything mutates.
                        var matchedPairs = new List<(TOM.CalculationItem sci, TOM.CalculationItem lci)>();
                        var newItems = new List<TOM.CalculationItem>();
                        var matchedCalcItems = new HashSet<TOM.CalculationItem>();
                        foreach (var sci in srcItems)
                        {
                            var lci = group.CalculationItems.Find(sci.Name);
                            if (lci == null) newItems.Add(sci);
                            else { matchedPairs.Add((sci, lci)); matchedCalcItems.Add(lci); }
                        }
                        var liveOnlyItems = group.CalculationItems.Cast<TOM.CalculationItem>().Where(c => !matchedCalcItems.Contains(c)).ToList();

                        // ORDINAL COLLISION GUARD: ordinal uniqueness is validated by the SERVER, not client TOM (set/
                        // add/serialize all accept duplicates — probed AMO 19.114), so a colliding assignment sails
                        // through here and kills the whole atomic SaveChanges. Live-only items are PRESERVED (absence
                        // never deletes), so a source op that would DUPLICATE a preserved live-only ordinal is REFUSED
                        // per item (unmatched, named; ref withheld) instead of pushed into a doomed commit. Ordinal -1
                        // is EXEMPT: it means "no explicit order" (the TOM default, omitted from serialization) and any
                        // number of items legally share it. Source-internal duplicates mirror the source verbatim —
                        // inventing ordinals would silently diverge; the server's verdict surfaces atomically.
                        string OrdinalClash(int ordinal) => ordinal < 0 ? null : liveOnlyItems.FirstOrDefault(c => c.Ordinal == ordinal)?.Name;

                        var reorders = new List<(TOM.CalculationItem lci, int final)>();
                        foreach (var (sci, lci) in matchedPairs)
                        {
                            // Name-keyed match ⇒ names are equal (a real rename is Create+Delete, handled below/channel).
                            var ciRef = AlmRef.Child("calculationitem", st.Name, sci.Name);   // SOURCE-keyed (reconciliation)
                            var ciId = AlmRef.Child("calculationitem", lt.Name, lci.Name);    // LIVE-keyed (change log)
                            int ciChanges = changes.Count;
                            var held = false;   // withheld from syncedRefs — some part of THIS item did not deploy
                            SetStr(sci.Description, () => lci.Description, v => lci.Description = v, apply, ref desc, "description " + ciId, changes);
                            SetExpr(sci.Expression, () => lci.Expression, v => lci.Expression = v, apply, ref expr, "expression " + ciId, changes);
                            SetCalcItemFormat(sci, lci, apply, ref fmt, "format " + ciId, changes);
                            if (lci.Ordinal != sci.Ordinal)
                            {
                                var clash = OrdinalClash(sci.Ordinal);
                                if (clash != null) { unmatched.Add(ciRef + $" (ordinal {sci.Ordinal} would duplicate live-only calculation item '{clash}' — ordinal not applied; deploy via TMDL/XMLA)"); held = true; }
                                else { if (apply) reorders.Add((lci, sci.Ordinal)); calc++; changes.Add("ordinal " + ciId); }
                            }
                            // RESIDUAL guard: the diff compared the WHOLE serialized item; this sync carries an
                            // enumerated surface (COMPLETE in AMO 19.114 — CalculationItem has no annotations/extended
                            // properties, reflected 2026-07-10). If a future AMO grows the object, the residual surfaces
                            // here and the ref is WITHHELD (named) instead of claiming an apply that dropped metadata.
                            if (!CalcItemResidualEqual(sci, lci)) { unmatched.Add(ciRef + " (carries authored metadata this live push cannot sync — deploy via TMDL/XMLA)"); held = true; }
                            if (held) heldRefs.Add(ciRef);
                            else if (changes.Count > ciChanges) syncedRefs.Add(ciRef);
                        }
                        // TWO-PHASE reorder: park every mover at a disjoint temp (above ALL current + target ordinals),
                        // then assign finals — an A↔B swap never passes through a transient duplicate, so this holds
                        // even if a future TOM validates eagerly on set instead of deferring to the server.
                        if (apply && reorders.Count > 0)
                        {
                            var tempOrdinal = group.CalculationItems.Cast<TOM.CalculationItem>().Select(c => c.Ordinal)
                                .Concat(srcItems.Select(s => s.Ordinal)).Max() + 1;
                            foreach (var (lci, _) in reorders) lci.Ordinal = tempOrdinal++;
                            foreach (var (lci, final) in reorders) lci.Ordinal = final;
                        }
                        foreach (var sci in newItems)
                        {
                            var ciRef = AlmRef.Child("calculationitem", st.Name, sci.Name);
                            var clash = OrdinalClash(sci.Ordinal);
                            if (clash != null) { unmatched.Add(ciRef + $" (ordinal {sci.Ordinal} would duplicate live-only calculation item '{clash}' — not created; deploy via TMDL/XMLA)"); heldRefs.Add(ciRef); continue; }
                            // NEW calc item → FULL Clone (name/expression/ordinal/description/format string — and any
                            // member a future AMO adds — carried BY CONSTRUCTION, so a hand-built constructor can never
                            // silently drop authored metadata again). The clone is detached; calc items carry no
                            // LineageTag (verified AMO 19.114), so there is no tag collision to clear. Also the CREATE
                            // leg of a rename — the paired Delete of the old name arrives via the explicit channel.
                            // Added AFTER the two-phase reorder, so its ordinal can't transiently collide with a mover.
                            if (apply) group.CalculationItems.Add((TOM.CalculationItem)sci.Clone());
                            added++; changes.Add($"add calcItem:{lt.Name}/{sci.Name}"); syncedRefs.Add(ciRef);
                        }
                        // Live-only calc items on this matched table — reported, left untouched (mirrors partitions/columns).
                        foreach (var lci in liveOnlyItems) liveOnlyChildren.Add((lt, "calculationitem", lci.Name));
                        // Calc-group evaluation Precedence — an authored table-level property (issue #124). Sync only when
                        // BOTH sides are calc-group tables (a kind transition is handled per-item above: reported, not synced).
                        if (st.CalculationGroup != null && group.Precedence != st.CalculationGroup.Precedence)
                        {
                            if (apply) group.Precedence = st.CalculationGroup.Precedence;
                            calc++; changes.Add("precedence " + AlmRef.Top("table", lt.Name)); syncedRefs.Add(AlmRef.Top("table", st.Name));
                        }
                    }
                }

                // An incremental-refresh POLICY difference is reported, not applied — a policy change re-shapes
                // the partition scheme on the next service refresh, so it's deployed deliberately (TMDL/XMLA). The
                // entry is TABLE-ref-keyed and the table's ref is WITHHELD from syncedRefs (below): a combined update
                // (precedence/description/rename + policy) used to reconcile as fully Applied on the other parts while
                // the policy silently stayed behind — the ref must not claim an apply that dropped part of the item.
                if (!RefreshPolicyEqual(st.RefreshPolicy, lt.RefreshPolicy))
                {
                    unmatched.Add(AlmRef.Top("table", st.Name) + " (refresh policy changed locally — not carried by a live metadata push; deploy it via TMDL/XMLA)");
                    heldRefs.Add(AlmRef.Top("table", st.Name));
                }

                // Live-only children on this matched table — reported, left untouched. Deferred so the ref follows the
                // table's FINAL name (the table may be renamed in the renames loop below).
                foreach (var lc in lt.Columns.Cast<TOM.Column>().Where(c => c.Type != TOM.ColumnType.RowNumber && !matchedCols.Contains(c)))
                    liveOnlyChildren.Add((lt, "column", lc.Name));
                foreach (var lm in lt.Measures.Cast<TOM.Measure>().Where(m => !matchedMeas.Contains(m)))
                    liveOnlyChildren.Add((lt, "measure", lm.Name));
            }

            // Live-only tables (reference identity — a renamed-and-matched table never reappears here).
            foreach (var lt in live.Tables.Cast<TOM.Table>().Where(t => !matchedTables.Contains(t)))
                liveOnly.Add("table:" + lt.Name);

            // Cultures: synonyms + AI instructions — UPDATE an existing culture only (no structural add).
            foreach (var sCult in src.Cultures)
            {
                var slm = sCult.LinguisticMetadata;
                if (slm == null || string.IsNullOrEmpty(slm.Content)) continue;
                var lc = live.Cultures.Find(sCult.Name);
                if (lc == null) { unmatched.Add(AlmRef.Top("culture", sCult.Name)); continue; }
                if (!JsonSemanticEqual(lc.LinguisticMetadata?.Content, slm.Content))
                {
                    if (apply)
                    {
                        var lm = new TOM.LinguisticMetadata();
                        lm.ContentType = slm.ContentType;   // set ContentType (Json) BEFORE Content — the Content setter validates against it
                        lm.Content = slm.Content;
                        lc.LinguisticMetadata = lm;
                    }
                    cult++; changes.Add("linguistic schema (synonyms + AI instructions) culture:" + sCult.Name); syncedRefs.Add(AlmRef.Top("culture", sCult.Name));
                }
            }

            // Shared M expressions / parameters (model.Expressions): an M parameter or shared query edited in the session
            // must deploy like measure DAX does. NamedExpression IS taggable (implements IMetadataObjectWithLineage —
            // AMO 19.114), so it is matched by LineageTag exactly like a measure (rename-safe; strict-terminal under a
            // selective push so a republished/retagged live expression is never mutated by name) — NOT by name. A NEW one
            // is self-contained metadata → ADDED. Live-only expressions are reported, left untouched.
            var liveExprByTag = ByTag(live.Expressions.Cast<TOM.NamedExpression>(), e => LineageOf(e));
            var matchedExprs = new HashSet<TOM.NamedExpression>();   // by REFERENCE — a tag-matched RENAME survives (don't key on the mutable Name)
            foreach (var se in src.Expressions)
            {
                var le = Match(LineageOf(se), se.Name, liveExprByTag, n => live.Expressions.Find(n), e => LineageOf(e), identityStrict, out var eConflict);
                if (le == null && eConflict != null) { unmatched.Add(RetagConflict(AlmRef.Top("expression", se.Name))); continue; }
                if (le == null)
                {
                    if (apply)
                    {
                        var ne = new TOM.NamedExpression { Name = se.Name, Kind = se.Kind, Expression = se.Expression };
                        if (SupportsLineage(live) && !string.IsNullOrEmpty(LineageOf(se))) ne.LineageTag = LineageOf(se);
                        if (!string.IsNullOrEmpty(se.Description)) ne.Description = se.Description;
                        live.Expressions.Add(ne); matchedExprs.Add(ne);   // register the new object so the live-only sweep doesn't re-flag it (mirrors matchedTables/Cols/Meas on their add paths)
                    }
                    // ModelCompare emits a named expression as "expression:<name>" — record it under THAT ref grammar
                    // (not the "namedExpression:" change-log label) so it reconciles against the diff/apply refs.
                    added++; changes.Add("add namedExpression:" + se.Name); syncedRefs.Add(AlmRef.Top("expression", se.Name));
                    continue;
                }
                matchedExprs.Add(le);
                if (se.Kind != le.Kind) { unmatched.Add("kind-mismatch " + AlmRef.Top("expression", se.Name) + $" (session {se.Kind}, live {le.Kind} — not deployed)"); continue; }
                var nid = AlmRef.Top("expression", le.Name);   // LIVE-name id (rename-safe), like a measure's id
                int eChanges = changes.Count;
                // A tag-matched expression can be RENAMED (source name != live name) — route it through the same
                // collision-safe rename machinery as a measure/table, not a silent name-based overwrite. (The old code
                // never renamed, so it reported expression:<newName> synced while the live model kept <oldName>.)
                PlanRename(se.Name, le, n => live.Expressions.Find(n), nid, renames);
                SetStr(se.Description, () => le.Description, v => le.Description = v, apply, ref desc, "description " + nid, changes);
                SetExpr(se.Expression, () => le.Expression, v => le.Expression = v, apply, ref nexpr, "expression " + nid, changes);
                if (changes.Count > eChanges) syncedRefs.Add(AlmRef.Top("expression", se.Name));
            }
            // Live-only expressions — MATCH-SET based (a tag-matched-and-renamed live expression is NOT live-only). The
            // old name-based sweep double-reported a just-renamed expression as live-only. (Tables/columns/measures/
            // partitions already use their matched-sets; expressions were the only name-based sweep — now fixed.)
            foreach (var le in live.Expressions.Cast<TOM.NamedExpression>().Where(e => !matchedExprs.Contains(e)))
                liveOnly.Add("namedExpression:" + le.Name);

            // Renames: collision-safe. The target-name check is read-only (runs in dry-run too); only a non-colliding
            // rename is counted as a change and (on commit) applied. A collision is reported, never thrown.
            foreach (var r in renames)
            {
                var existing = r.findSibling(r.newName);
                if (existing != null && !ReferenceEquals(existing, r.obj))
                {
                    conflicts.Add($"{r.id}: target name '{r.newName}' already exists on live — rename skipped");
                    continue;
                }
                ren++; changes.Add($"rename {r.id} ({r.oldName} -> {r.newName})");
                if (apply)
                {
                    try { r.obj.Name = r.newName; }
                    catch (Exception ex) { conflicts.Add($"{r.id}: rename failed ({ex.Message})"); ren--; continue; }
                }
                // A rename that actually stuck IS a real sync of that object — record the SOURCE-keyed ref (r.newName is
                // the source name; the ref's kind/parent come from r.id) so a rename-only change reconciles as applied.
                syncedRefs.Add(RenameSyncRef(r.id, r.newName));
            }

            // Build the live-only CHILD refs NOW — after the renames loop — so each names its table's FINAL name: the new
            // name if the rename applied, or the OLD name if it collided/was skipped (or on a dry-run). The ref follows
            // what live actually contains, never the pre-rename intent.
            foreach (var (table, kind, child) in liveOnlyChildren)
                liveOnly.Add(AlmRef.Child(kind, table.Name, child));

            // Enforce the withhold LAST — a rename of a policy-held table would have re-added its ref above.
            foreach (var h in heldRefs) syncedRefs.Remove(h);

            rep.Descriptions = desc; rep.Renames = ren; rep.Visibility = vis; rep.DataCategories = cat;
            rep.Formats = fmt; rep.Folders = fold; rep.Expressions = expr; rep.SummarizeBy = sum; rep.Cultures = cult;
            rep.Partitions = part; rep.NamedExpressions = nexpr; rep.CalcGroup = calc;
            rep.Metadata = metadata;
            rep.Added = added;
            rep.TotalChanges = desc + ren + vis + cat + fmt + fold + expr + sum + cult + part + nexpr + added + calc + metadata;
            rep.Changes = changes.Take(80).ToArray();
            rep.Unmatched = unmatched.ToArray();
            rep.LiveOnly = liveOnly.ToArray();
            rep.Conflicts = conflicts.ToArray();
            rep.CalcTablesAdded = calcTablesAdded.ToArray();
            rep.SyncedRefs = syncedRefs.ToArray();

            if (apply && rep.TotalChanges > 0)
            {
                // Verified Edits: carry the model's audit-trail annotations along, so the override record
                // deploy_live appended BEFORE serializing this .bim ships INSIDE the artifact it authorized.
                // Ride-along only — audit state is never counted as a change and never triggers a deploy by
                // itself (a metadata-identical model with TotalChanges==0 skips SaveChanges entirely, and the
                // trail simply travels with the next real deploy).
                foreach (var sa in src.Annotations.Cast<TOM.Annotation>()
                             .Where(a => a.Name != null && a.Name.StartsWith("Semanticus_VerifiedEdits", StringComparison.Ordinal)).ToList())
                {
                    if (live.Annotations.Contains(sa.Name)) live.Annotations[sa.Name].Value = sa.Value;
                    else live.Annotations.Add(new TOM.Annotation { Name = sa.Name, Value = sa.Value });
                }
            }
            return rep;
        }

        /// <summary>The deployable text a partition source carries, tagged by kind. M / legacy-query / calc-table
        /// DAX are all metadata text we can push; a Direct Lake entity binding is identified but report-only
        /// (rebinding is structural). An unknown source kind carries no script (compares equal, never synced).</summary>
        private static (string Kind, string Script) PartitionScript(TOM.Partition p) => p.Source switch
        {
            TOM.MPartitionSource m => ("m", m.Expression),
            TOM.CalculatedPartitionSource c => ("calculated", c.Expression),
            TOM.QueryPartitionSource q => ("query", q.Query),
            TOM.EntityPartitionSource e => ("entity", e.EntityName),
            null => (null, null),
            _ => (p.Source.GetType().Name, null)
        };

        private static void SetPartitionScript(TOM.Partition p, string script)
        {
            switch (p.Source)
            {
                case TOM.MPartitionSource m: m.Expression = script; break;
                case TOM.CalculatedPartitionSource c: c.Expression = script; break;
                case TOM.QueryPartitionSource q: q.Query = script; break;
            }
        }

        private static bool RefreshPolicyEqual(TOM.RefreshPolicy a, TOM.RefreshPolicy b)
        {
            if (a == null || b == null) return a == b;
            // Same non-Basic subtype ⇒ UNEQUAL: we can't compare its fields, and equal-on-type-alone silently drops a
            // real change (false-EQUAL — the dangerous direction; here it only over-reports an unmatched policy).
            // Unreachable in AMO 19.114 (Basic is RefreshPolicy's only subtype — reflected 2026-07-10, pinned by
            // RefreshPolicy_subtype_catalog_is_pinned); arms on a future TOM bump. Mirrors ModelCompare; keep in step.
            if (!(a is TOM.BasicRefreshPolicy pa) || !(b is TOM.BasicRefreshPolicy pb)) return false;
            return pa.IncrementalPeriods == pb.IncrementalPeriods && pa.IncrementalGranularity == pb.IncrementalGranularity
                && pa.RollingWindowPeriods == pb.RollingWindowPeriods && pa.RollingWindowGranularity == pb.RollingWindowGranularity
                && pa.IncrementalPeriodsOffset == pb.IncrementalPeriodsOffset && pa.Mode == pb.Mode
                && string.Equals(pa.SourceExpression, pb.SourceExpression, StringComparison.Ordinal)
                && string.Equals(pa.PollingExpression, pb.PollingExpression, StringComparison.Ordinal);
        }

        private static Dictionary<string, T> ByTag<T>(IEnumerable<T> items, Func<T, string> tag)
        {
            var d = new Dictionary<string, T>();
            foreach (var i in items) { var t = tag(i); if (!string.IsNullOrEmpty(t) && !d.ContainsKey(t)) d[t] = i; }
            return d;
        }

        private static T Match<T>(string srcTag, string name, Dictionary<string, T> byTag, Func<string, T> byName, Func<T, string> liveTag, bool identityStrict) where T : class
            => Match(srcTag, name, byTag, byName, liveTag, identityStrict, out _);

        /// <summary>Match by LineageTag, with a PROVENANCE-gated strictness (see <paramref name="identityStrict"/>):
        /// <list type="bullet">
        /// <item>LENIENT (identityStrict=false) — used by whole-model <c>deploy_live</c>, whose <c>src</c> is the open
        /// SESSION of arbitrary provenance (often an untagged file-opened model deploying into a tagged live model). A
        /// non-empty srcTag falls back to name when the live object is untagged or the tags are equal — so an untagged
        /// session still deploys. This is byte-for-byte the historic behaviour.</item>
        /// <item>STRICT (identityStrict=true) — used ONLY by the SELECTIVE PUSH, whose <c>src</c> is snapshot B taken from
        /// the LIVE target seconds earlier. A non-empty srcTag that does NOT exact-match a live tag is TERMINAL: NO name
        /// fallback. If a same-named live object exists (different/empty tag) it is the object republished/retagged under
        /// us — reported via <paramref name="conflict"/> so the caller marks it unmatched, never mutating/adding over it.
        /// A wrong-object mutation on a published model is exactly what this must prevent.</item></list>
        /// The asymmetry is symmetric across the tag-presence axis under STRICT: an EMPTY srcTag that name-resolves to a
        /// TAGGED live object is ALSO terminal (the live object was retagged/replaced under us) — the exact mirror of the
        /// delete path's untagged→tagged refusal. Both sides untagged ⇒ name match (capability preserved). LENIENT keeps
        /// today's behaviour in every case, so an untagged deploy_live session still deploys into a tagged live model.</summary>
        private static T Match<T>(string srcTag, string name, Dictionary<string, T> byTag, Func<string, T> byName, Func<T, string> liveTag, bool identityStrict, out T conflict) where T : class
        {
            conflict = null;
            if (!string.IsNullOrEmpty(srcTag))
            {
                if (byTag.TryGetValue(srcTag, out var hit)) return hit;   // exact tag match — always wins
                if (identityStrict) { conflict = byName(name); return null; }   // STRICT: tag miss is TERMINAL; a same-named object is a conflict
                // LENIENT: fall back to name only if the live object is untagged or its tag equals ours.
                var candL = byName(name);
                if (candL == null) return null;
                var ctagL = liveTag(candL);
                if (string.IsNullOrEmpty(ctagL) || string.Equals(srcTag, ctagL, StringComparison.Ordinal)) return candL;
                conflict = candL;   // a genuine retag (different non-empty tag) — don't name-match even when lenient
                return null;
            }
            // srcTag EMPTY (untagged source object). Name is the identity — BUT under STRICT, a name-resolved live object
            // that now carries a NON-EMPTY tag is not one we could have diffed untagged: it was replaced/retagged under
            // us. Refuse (mirror of the delete path's untagged→tagged asymmetry). Both untagged ⇒ match; lenient ⇒ match.
            var candU = byName(name);
            if (identityStrict && candU != null && !string.IsNullOrEmpty(liveTag(candU))) { conflict = candU; return null; }
            return candU;
        }

        // Rebuild the SOURCE-keyed object ref (ModelCompare's escaped grammar) from a rename's id + the new (source)
        // name: replace the id's last ESCAPED name segment with the escaped new name. "table:X"→"table:<new>";
        // "measure:T/Y"→"measure:T/<new>". The id is already an AlmRef, so split on the last UNESCAPED '/' (or the
        // kind ':') and re-escape only the replacement. (Renames are only planned for tables/columns/measures; the
        // parent-table segment stays live-named, which equals the source name unless the parent was ALSO renamed — a
        // combination we don't try to reconcile.)
        private static string RenameSyncRef(string id, string newName)
        {
            var slash = AlmRef.LastIndexOfUnescaped(id, '/');
            if (slash >= 0) return id.Substring(0, slash + 1) + AlmRef.Esc(newName);
            var colon = AlmRef.IndexOfUnescaped(id, ':', 0);
            return colon >= 0 ? id.Substring(0, colon + 1) + AlmRef.Esc(newName) : AlmRef.Esc(newName);
        }

        private static void PlanRename(string srcName, TOM.NamedMetadataObject obj, Func<string, TOM.NamedMetadataObject> findSibling, string id, List<(TOM.NamedMetadataObject, string, string, Func<string, TOM.NamedMetadataObject>, string)> renames)
        {
            if (!string.Equals(obj.Name, srcName, StringComparison.Ordinal))
                renames.Add((obj, obj.Name, srcName, findSibling, id));
        }

        // For description / format / folder / data-category, "" and null are the same (don't churn).
        private static void SetStr(string srcVal, Func<string> get, Action<string> set, bool apply, ref int counter, string label, List<string> log)
        {
            var s = string.IsNullOrEmpty(srcVal) ? null : srcVal;
            var cur = string.IsNullOrEmpty(get()) ? null : get();
            if (!string.Equals(s, cur, StringComparison.Ordinal)) { if (apply) set(s); counter++; log.Add(label); }
        }

        // For DAX expressions, push the value verbatim (do NOT coerce "" to null — let SaveChanges validate).
        private static void SetExpr(string srcVal, Func<string> get, Action<string> set, bool apply, ref int counter, string label, List<string> log)
        {
            if (!string.Equals(srcVal, get(), StringComparison.Ordinal)) { if (apply) set(srcVal); counter++; log.Add(label); }
        }

        private static void SetBool(bool srcVal, Func<bool> get, Action<bool> set, bool apply, ref int counter, string label, List<string> log)
        {
            if (get() != srcVal) { if (apply) set(srcVal); counter++; log.Add(label); }
        }

        // A calc item's dynamic format string lives in FormatStringDefinition.Expression (null when none). Mirror SetStr's
        // ""==null semantics and manage the FormatStringDefinition wrapper (the raw-TOM shape): create it to set a
        // non-empty expression, null it to clear — so a live push carries a format-string add/change/clear.
        private static void SetCalcItemFormat(TOM.CalculationItem src, TOM.CalculationItem live, bool apply, ref int counter, string label, List<string> log)
        {
            var s = string.IsNullOrEmpty(src.FormatStringDefinition?.Expression) ? null : src.FormatStringDefinition.Expression;
            var cur = string.IsNullOrEmpty(live.FormatStringDefinition?.Expression) ? null : live.FormatStringDefinition.Expression;
            if (string.Equals(s, cur, StringComparison.Ordinal)) return;
            if (apply) live.FormatStringDefinition = s == null ? null : new TOM.FormatStringDefinition { Expression = s };
            counter++; log.Add(label);
        }

        private static bool CalcItemResidualEqual(TOM.CalculationItem a, TOM.CalculationItem b)
            => CalcItemResidualEqual(TOM.JsonSerializer.SerializeObject(a), TOM.JsonSerializer.SerializeObject(b));

        // Is anything left of the two serialized calc items once the members the sync CARRIES (name / description /
        // expression / ordinal / formatStringDefinition.expression) and the server-runtime members are stripped? The
        // diff that drove this push compared the WHOLE object, so any remainder is authored metadata the enumerated
        // sync would silently drop — the caller withholds the ref instead of claiming it. Empty today (the carried set
        // IS CalculationItem's complete authored surface in AMO 19.114); this detects the drift when a TOM bump grows
        // the object. String-level and internal so the drift detector itself is unit-testable with a synthetic member.
        internal static bool CalcItemResidualEqual(string aJson, string bJson)
        {
            static Newtonsoft.Json.Linq.JObject Strip(string json)
            {
                var o = Newtonsoft.Json.Linq.JObject.Parse(json);
                foreach (var p in new[] { "name", "description", "expression", "ordinal",
                    "modifiedTime", "structureModifiedTime", "refreshedTime", "lastProcessed", "lastUpdate", "lastSchemaUpdate", "state", "errorMessage" })
                    o.Remove(p);
                if (o["formatStringDefinition"] is Newtonsoft.Json.Linq.JObject f)
                {
                    foreach (var p in new[] { "expression", "modifiedTime", "state", "errorMessage" }) f.Remove(p);
                    if (!f.HasValues) o.Remove("formatStringDefinition");   // fully-carried FSD ≡ no FSD (don't fabricate a residual)
                }
                return o;
            }
            try { return Newtonsoft.Json.Linq.JToken.DeepEquals(Strip(aJson), Strip(bJson)); }
            catch { return false; }   // unparseable ⇒ assume a residual and withhold — fail toward honesty, never a silent claim
        }

        /// <summary>Order-insensitive JSON equality (object key order / whitespace ignored) for the LSDL content,
        /// so a serialize round-trip that only reformats the JSON isn't mistaken for a real change.</summary>
        private static bool JsonSemanticEqual(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.Ordinal)) return true;
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { return Newtonsoft.Json.Linq.JToken.DeepEquals(Newtonsoft.Json.Linq.JToken.Parse(a), Newtonsoft.Json.Linq.JToken.Parse(b)); }
            catch { return false; }
        }
    }
}
