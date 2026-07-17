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
    ///   it reaches the live model). A new CALCULATED TABLE IS created (its columns are engine-derived on the Calculate
    ///   pass SyncSessionToLive runs after the save). A new DIRECT LAKE data table IS created (columns + Entity
    ///   partition bound to the LIVE shared expression, Mode carried) and announced EMPTY — its rows load on the next
    ///   refresh; if its shared expression is missing on both sides it is REFUSED (not half-created). A new DATA
    ///   COLUMN on an existing table IS added with its source binding. A new IMPORT/DirectQuery table (needs an M
    ///   binding we don't carry) and new cultures are still REPORTED (Unmatched), not added.
    /// - STRUCTURAL classes deploy too (added 2026-07-09 after the ETS incident — an A/100-offline model read B/80.6
    ///   in the service because these had silently never deployed and nothing said so): a table's DataCategory (the
    ///   date-table mark), SortByColumn (bound to the LIVE key column; a cleared session sort-by clears it live),
    ///   HIERARCHIES (created/re-levelled, levels bound to LIVE columns; a live-only one is reported, never deleted),
    ///   RELATIONSHIPS (matched by ENDPOINT signature not the guid name; new ones resolve endpoints in the live tree;
    ///   crossfilter/active/cardinality drift deploys; a live-only one is reported, never deleted), and the OWNED
    ///   annotations — finding waivers (Semanticus_Waivers) + per-object BPA ignore rules. A FOREIGN annotation we do
    ///   not own is never read, counted, or touched.
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
            // BLOCKER 1: the live objects THIS deploy wrote (created / updated in place). SyncModels fills it; the explicit-
            // delete channel refuses a delete that lands on one (an endpoint-rename relationship the same push updated) —
            // deleting it would undo the sync. Only the selective-push path carries deletes, so only it needs the set.
            var changedLive = new HashSet<TOM.MetadataObject>();
            var rep = SyncModels(src, live, commit, endpoint, database, identityStrict, changedLive);

            // BLOCKER 1 (replacement coupling): the refs the deploy ACTUALLY synced, plus the relationship refs PRESENT in
            // the pushed model. A relationship Delete that is the old half of an endpoint re-point (it carries the paired
            // Create's ref) is safe ONLY if that Create landed live. If the Create was attempted (its ref is in the pushed
            // model) but did NOT sync (its new endpoint no longer resolves live), removing the old relationship would leave
            // NO relationship where the diff promised a replacement — so the delete channel refuses it as a conflict.
            var syncedRefs = new HashSet<string>(rep.SyncedRefs ?? System.Array.Empty<string>(), StringComparer.Ordinal);
            // MINOR 2: relationship refs that matched live but needed no write — present live, so they satisfy a paired
            // re-point Delete's replacement requirement exactly like a synced ref (a converged push must not falsely refuse).
            var matchedRelRefs = new HashSet<string>(rep.MatchedRefs ?? System.Array.Empty<string>(), StringComparer.Ordinal);
            var pushedRelRefs = new HashSet<string>(
                src.Relationships.Cast<TOM.Relationship>().Select(r => "relationship:" + RelSig(r)), StringComparer.Ordinal);

            // DETECTION PASS (never mutates): any Refused ⇒ abort before SaveChanges — nothing is committed.
            if (explicitDeleteTargets != null && explicitDeleteTargets.Count > 0)
            {
                var probe = new DeployReport();
                RemoveExplicit(live, explicitDeleteTargets, apply: false, probe, changedLive, syncedRefs, pushedRelRefs, matchedRelRefs);
                if (probe.DeletesRefused.Length > 0)
                {
                    rep.DeletesRefused = probe.DeletesRefused;
                    rep.Error = "Delete refused: the live model drifted since the diff. " + string.Join(", ", probe.DeletesRefused)
                        + " no longer resolve to the object you diffed (recreated / retagged / a different object now bears the name). NOTHING was pushed. Re-diff against the current model. (This drift is NOT overridable; overrideReason covers only the drift detected at diff time.)";
                    return rep;   // abort BEFORE any SaveChanges — rep.Committed stays false
                }
                // BLOCKER 1: a delete that would undo this deploy or drop a relationship with no replacement. Two shapes,
                // both routed through DeletesRefusedConflict: (a) the delete lands on an object THIS deploy just identity-
                // matched/updated (the endpoint-rename third-state race), so committing both would silently drop what we
                // synced; (b) the delete is the old half of an endpoint re-point whose new relationship was attempted but
                // did NOT land live, so removing the old one leaves no relationship at all. Either way abort the whole push
                // and report an honest conflict. Re-diff against the current model.
                if (probe.DeletesRefusedConflict.Length > 0)
                {
                    rep.DeletesRefusedConflict = probe.DeletesRefusedConflict;
                    rep.Error = "Delete refused to avoid data loss: " + string.Join(", ", probe.DeletesRefusedConflict)
                        + ". This deploy either just updated the same object (an endpoint rename re-projected onto its old signature), or a relationship's replacement did not land in this push (its new endpoint no longer resolves live) so removing the old one would leave no relationship. NOTHING was pushed. Re-diff against the current model.";
                    return rep;   // abort BEFORE any SaveChanges — rep.Committed stays false
                }
            }

            // Clean: apply the deletes for real, inside the SAME change set as the adds/updates (one SaveChanges).
            RemoveExplicit(live, explicitDeleteTargets, commit, rep, changedLive, syncedRefs, pushedRelRefs, matchedRelRefs);
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

        private enum RemoveOutcome { Deleted, Absent, Refused, RefusedMatched, RefusedReplacementUnmet }

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
        internal static void RemoveExplicit(TOM.Model live, IReadOnlyCollection<LiveDeleteTarget> targets, bool apply, DeployReport rep,
            HashSet<TOM.MetadataObject> changedLive = null, HashSet<string> syncedRefs = null, HashSet<string> pushedRelRefs = null, HashSet<string> matchedRelRefs = null)
        {
            if (targets == null || targets.Count == 0) return;
            var deleted = new List<string>();
            var absent = new List<string>();
            var refused = new List<string>();
            var refusedMatched = new List<string>();
            var log = new List<string>();
            foreach (var t in targets)
            {
                if (t == null || string.IsNullOrEmpty(t.Kind)) continue;
                switch (TryRemoveLive(live, t, apply, changedLive, syncedRefs, pushedRelRefs, matchedRelRefs))
                {
                    case RemoveOutcome.Deleted: deleted.Add(t.Ref); log.Add("delete " + t.Ref); break;
                    case RemoveOutcome.Refused: refused.Add(t.Ref); log.Add("delete REFUSED (identity gone; a different object now bears this name) " + t.Ref); break;
                    // BLOCKER 1: the delete resolved to an object this SAME deploy just wrote/kept (an endpoint-rename
                    // relationship the identity match updated in place). Removing it would undo what we just synced — an
                    // honest conflict, not a silent no-op and not a wrong deletion.
                    case RemoveOutcome.RefusedMatched: refusedMatched.Add(t.Ref); log.Add("delete REFUSED (this deploy just updated the same object; deleting it would undo that) " + t.Ref); break;
                    // BLOCKER 1 (replacement coupling): the delete is the old half of an endpoint re-point whose new
                    // relationship was attempted but did NOT land live — deleting the old one would leave no relationship.
                    // Same honest conflict, same DeletesRefusedConflict channel (aborts the whole push).
                    case RemoveOutcome.RefusedReplacementUnmet: refusedMatched.Add(t.Ref); log.Add("delete REFUSED (its replacement relationship was not created in this push; deleting the old one would leave no relationship) " + t.Ref); break;
                    default: absent.Add(t.Ref); log.Add("delete (already absent) " + t.Ref); break;
                }
            }
            rep.Deleted += deleted.Count;
            rep.DeletedRefs = rep.DeletedRefs.Concat(deleted).ToArray();
            rep.DeletesAlreadyAbsent = rep.DeletesAlreadyAbsent.Concat(absent).ToArray();
            rep.DeletesRefused = rep.DeletesRefused.Concat(refused).ToArray();
            rep.DeletesRefusedConflict = rep.DeletesRefusedConflict.Concat(refusedMatched).ToArray();
            rep.TotalChanges += deleted.Count;   // real removals are part of the SaveChanges change set
            if (log.Count > 0) rep.Changes = rep.Changes.Concat(log).Take(120).ToArray();
        }

        // Resolve a delete target to its live TOM object by IDENTITY and (apply) remove it. A carried tag is
        // authoritative; a miss is TERMINAL (Refused if a same-named impostor exists, else Absent). Raw-TOM removal
        // mirrors ModelCompare.Apply's delete paths — the parity reference.
        private static RemoveOutcome TryRemoveLive(TOM.Model live, LiveDeleteTarget t, bool apply, HashSet<TOM.MetadataObject> changedLive,
            HashSet<string> syncedRefs = null, HashSet<string> pushedRelRefs = null, HashSet<string> matchedRelRefs = null)
        {
            switch (t.Kind)
            {
                case "table":
                    if (!string.IsNullOrEmpty(t.Tag))
                    {
                        var byTag = live.Tables.Cast<TOM.Table>().FirstOrDefault(x => x.LineageTag == t.Tag);
                        if (byTag != null) { if (JustWritten(changedLive, byTag)) return RemoveOutcome.RefusedMatched; if (apply) live.Tables.Remove(byTag); return RemoveOutcome.Deleted; }
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
                        if (JustWritten(changedLive, byName)) return RemoveOutcome.RefusedMatched;
                        if (apply) live.Tables.Remove(byName); return RemoveOutcome.Deleted;
                    }
                case "measure":   return RemoveChild(live, t, apply, changedLive, tb => tb.Measures.Cast<TOM.NamedMetadataObject>(), (tb, o) => tb.Measures.Remove((TOM.Measure)o));
                case "column":    return RemoveChild(live, t, apply, changedLive, tb => tb.Columns.Cast<TOM.NamedMetadataObject>(), (tb, o) => tb.Columns.Remove((TOM.Column)o));
                case "hierarchy": return RemoveChild(live, t, apply, changedLive, tb => tb.Hierarchies.Cast<TOM.NamedMetadataObject>(), (tb, o) => tb.Hierarchies.Remove((TOM.Hierarchy)o));
                case "partition": return RemoveChild(live, t, apply, changedLive, tb => tb.Partitions.Cast<TOM.NamedMetadataObject>(), (tb, o) => tb.Partitions.Remove((TOM.Partition)o));
                // A calculation item is a name-keyed child of a table's CalculationGroup (issue #124). RemoveChild resolves
                // the owning table by TableTag (authoritative) then the item by NAME within it (calc items are tag-less, so
                // the asymmetry guard never fires). Without this case a ticked calc-item delete hit the `default` below →
                // RemoveOutcome.Absent → a SILENTLY DROPPED delete (worse than the diff being blind). A null CalculationGroup
                // yields an empty child set → a benign Absent no-op (nothing to remove).
                case "calculationitem": return RemoveChild(live, t, apply, changedLive, tb => tb.CalculationGroup?.CalculationItems.Cast<TOM.NamedMetadataObject>() ?? Enumerable.Empty<TOM.NamedMetadataObject>(), (tb, o) => tb.CalculationGroup?.CalculationItems.Remove((TOM.CalculationItem)o));
                case "role":        return RemoveTop(live.Roles.Cast<TOM.NamedMetadataObject>(), t, apply, changedLive, o => live.Roles.Remove((TOM.ModelRole)o));
                case "perspective": return RemoveTop(live.Perspectives.Cast<TOM.NamedMetadataObject>(), t, apply, changedLive, o => live.Perspectives.Remove((TOM.Perspective)o));
                case "culture":     return RemoveTop(live.Cultures.Cast<TOM.NamedMetadataObject>(), t, apply, changedLive, o => live.Cultures.Remove((TOM.Culture)o));
                case "datasource":  return RemoveTop(live.DataSources.Cast<TOM.NamedMetadataObject>(), t, apply, changedLive, o => live.DataSources.Remove((TOM.DataSource)o));
                case "expression":  return RemoveTop(live.Expressions.Cast<TOM.NamedMetadataObject>(), t, apply, changedLive, o => live.Expressions.Remove((TOM.NamedExpression)o));
                case "relationship":
                {
                    // A relationship's identity is its structural endpoint signature (name-independent), carried whole in
                    // t.Name and compared via RelSig — no tag, no name-guess to degrade.
                    var o = live.Relationships.Cast<TOM.Relationship>().FirstOrDefault(r => RelSig(r) == t.Name);
                    if (o == null) return RemoveOutcome.Absent;
                    // BLOCKER 1: refuse if THIS deploy just identity-matched/updated this very relationship (the endpoint-
                    // rename third-state race) — deleting it would undo the sync. Reported as an honest conflict.
                    if (JustWritten(changedLive, o)) return RemoveOutcome.RefusedMatched;
                    // BLOCKER 1 (replacement coupling): this delete is the old half of an endpoint re-point. It carries the
                    // REQUIRED replacement Create ref(s) — one for an unambiguous one-to-one re-point, or the WHOLE ambiguous
                    // candidate group (all its Creates). The delete is safe only if EVERY required Create that was ATTEMPTED
                    // in this push (its ref is present in the pushed model) actually LANDED — either synced (a write) or, MINOR
                    // 2, matched already-present live (identical, no write). If ANY required-and-pushed Create did neither,
                    // removing the old relationship would leave the model with NO relationship (or, in an ambiguous group, a
                    // relationship whose real replacement vanished) where the diff promised one. Refuse as a conflict. (A
                    // required Create NOT in this selection isn't in the pushed model, so a genuine standalone delete still
                    // proceeds; an ambiguous group whose every replacement landed is trivially safe.)
                    if (t.ReplacementNewRefs != null && pushedRelRefs != null)
                    {
                        foreach (var rc in t.ReplacementNewRefs)
                            if (!string.IsNullOrEmpty(rc) && pushedRelRefs.Contains(rc)
                                && (syncedRefs == null || !syncedRefs.Contains(rc))
                                && (matchedRelRefs == null || !matchedRelRefs.Contains(rc)))
                                return RemoveOutcome.RefusedReplacementUnmet;
                    }
                    if (apply) live.Relationships.Remove(o); return RemoveOutcome.Deleted;
                }
                default: return RemoveOutcome.Absent;   // unknown kind = unresolvable no-op, never throw
            }
        }

        // Resolve a table child by identity. The OWNING TABLE is resolved by TableTag (authoritative; a miss is terminal
        // — Refused if a same-named table now exists, else Absent). Then the CHILD: a tagged child kind (measure/column/
        // hierarchy) by its own tag (terminal on miss); a tag-less child (partition) by name within the resolved table.
        private static RemoveOutcome RemoveChild(TOM.Model live, LiveDeleteTarget t, bool apply, HashSet<TOM.MetadataObject> changedLive,
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
                if (byTag != null) { if (JustWritten(changedLive, byTag)) return RemoveOutcome.RefusedMatched; if (apply) remove(table, byTag); return RemoveOutcome.Deleted; }
                return (!string.IsNullOrEmpty(t.Name) && children(table).Any(c => c.Name == t.Name)) ? RemoveOutcome.Refused : RemoveOutcome.Absent;
            }
            var byName = string.IsNullOrEmpty(t.Name) ? null : children(table).FirstOrDefault(c => c.Name == t.Name);   // tag-less child (partition) → name
            if (byName == null) return RemoveOutcome.Absent;
            // ASYMMETRY (child): our target child carried no tag, but the name-resolved live child IS tagged ⇒
            // recreated/retagged. Refuse. Partitions carry no lineage tag (LineageOf → null) → never refused here.
            if (!string.IsNullOrEmpty(LineageOf(byName))) return RemoveOutcome.Refused;
            if (JustWritten(changedLive, byName)) return RemoveOutcome.RefusedMatched;
            if (apply) remove(table, byName); return RemoveOutcome.Deleted;
        }

        // BLOCKER 1: did THIS deploy already write (create / update in place) this exact live object? A membership test
        // by REFERENCE (the set holds the live TOM instances SyncModels touched). Used to refuse an explicit delete that
        // would undo a sync in the same push — the endpoint-rename third-state race. Null set ⇒ no protection (deploy_live
        // carries no deletes; only a selective push does, via SyncAndApply which supplies the set).
        private static bool JustWritten(HashSet<TOM.MetadataObject> changedLive, TOM.MetadataObject o)
            => changedLive != null && o != null && changedLive.Contains(o);

        // Top-level kinds: role / perspective / culture / datasource / expression. Resolution is INTERFACE-driven (via
        // LineageOf), not a hardcoded tagged/tag-less split. role/perspective/culture/datasource do NOT implement
        // IMetadataObjectWithLineage → LineageOf is null → the tag path never engages and they resolve by NAME (a rename
        // reads as Delete+Create). NamedExpression DOES implement it, so a tagged expression resolves TAG-authoritatively
        // (a miss is terminal, exactly like a measure), with name resolution + the untagged→tagged asymmetry guard when
        // no tag is carried. This closes the "NamedExpression is taggable but treated as tag-less" wrong-object gap.
        private static RemoveOutcome RemoveTop(IEnumerable<TOM.NamedMetadataObject> coll, LiveDeleteTarget t, bool apply, HashSet<TOM.MetadataObject> changedLive, Action<TOM.NamedMetadataObject> remove)
        {
            if (!string.IsNullOrEmpty(t.Tag))   // tag-authoritative (a tagged NamedExpression) — a miss is TERMINAL
            {
                var byTag = coll.FirstOrDefault(x => LineageOf(x) == t.Tag);
                if (byTag != null) { if (JustWritten(changedLive, byTag)) return RemoveOutcome.RefusedMatched; if (apply) remove(byTag); return RemoveOutcome.Deleted; }
                return (!string.IsNullOrEmpty(t.Name) && coll.Any(x => x.Name == t.Name)) ? RemoveOutcome.Refused : RemoveOutcome.Absent;
            }
            var o = string.IsNullOrEmpty(t.Name) ? null : coll.FirstOrDefault(x => x.Name == t.Name);
            if (o == null) return RemoveOutcome.Absent;
            // ASYMMETRY: our untagged target resolved to a TAGGED live object ⇒ not the object we diffed (only
            // NamedExpression can trigger this — LineageOf is null for role/perspective/culture/datasource, so it never
            // fires for them). Refuse. Fails closed.
            if (!string.IsNullOrEmpty(LineageOf(o))) return RemoveOutcome.Refused;
            if (JustWritten(changedLive, o)) return RemoveOutcome.RefusedMatched;
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
                ? RelSig(sc.FromColumn, sc.ToColumn)
                : AlmRef.EscRel(r.Name);

        // The endpoint signature built straight from two COLUMNS — used to project a session relationship's identity onto
        // the LIVE tree (via identity-resolved endpoints) so matching survives an endpoint rename and never lands on a
        // same-named impostor. Byte-for-byte the format RelSig(Relationship) and ModelCompare.RelSig emit.
        private static string RelSig(TOM.Column from, TOM.Column to)
            => AlmRef.EscRel(from.Table?.Name) + "[" + AlmRef.EscRel(from.Name) + "]->" + AlmRef.EscRel(to.Table?.Name) + "[" + AlmRef.EscRel(to.Name) + "]";

        // Project a SESSION relationship's identity onto the LIVE tree: resolve both endpoints by the shared lineage-first
        // identity discipline (never a same-named impostor, and following a tag-matched rename even before the rename's
        // Name lands live), then build the signature from those LIVE endpoints. This is what makes a matched relationship
        // findable when an endpoint was renamed (the name-based signature would drift and duplicate the relationship) and
        // what stops drift landing on the wrong (impostor-bound) relationship. Returns the resolved live endpoints (single-
        // column only) + the projected sig; a non-single-column relationship falls back to its escaped name; an endpoint
        // that doesn't resolve yields a null sig (a genuine new / unresolvable relationship).
        private static (TOM.Column from, TOM.Column to, string sig) ProjectRelSig(TOM.Relationship srel, TOM.Model live, bool identityStrict, HashSet<TOM.Column> projected)
        {
            if (srel is TOM.SingleColumnRelationship sc && sc.FromColumn != null && sc.ToColumn != null)
            {
                var from = ResolveLiveColumnByIdentity(live, sc.FromColumn, identityStrict, projected);
                var to = ResolveLiveColumnByIdentity(live, sc.ToColumn, identityStrict, projected);
                return (from, to, from != null && to != null ? RelSig(from, to) : null);
            }
            return (null, null, AlmRef.EscRel(srel.Name));
        }

        // A level's SOURCE-keyed ref — nested table/hierarchy/level, escaped like every other AlmRef so a name with a
        // delimiter can't forge ref structure. (AlmRef has no 3-segment builder; a level is the only such nesting here.)
        private static string LevelRef(string table, string hierarchy, string level)
            => "level:" + AlmRef.Esc(table) + "/" + AlmRef.Esc(hierarchy) + "/" + AlmRef.Esc(level);

        /// <summary>The server-free diff/apply core (offline-testable): computes the change set between the
        /// session model and a live model and (apply=true) mutates the live TOM tree IN MEMORY — the caller
        /// owns the SaveChanges boundary. Dry-run (apply=false) never mutates. <paramref name="identityStrict"/> gates
        /// the LineageTag match provenance (see <see cref="Match{T}(string,string,Dictionary{string,T},Func{string,T},Func{T,string},bool,out T)"/>):
        /// false (deploy_live, arbitrary session) name-falls-back; true (selective push, src = a live snapshot) makes a
        /// non-empty tag miss TERMINAL and reports the same-named live object as unmatched instead of mutating it.</summary>
        internal static DeployReport SyncModels(TOM.Model src, TOM.Model live, bool apply, string endpoint = "", string database = "", bool identityStrict = false,
            HashSet<TOM.MetadataObject> changedLive = null)
        {
            // BLOCKER 1 (third-state delete race): the live objects THIS deploy actually WROTE — created, or updated in
            // place. The explicit-delete channel (RemoveExplicit) refuses a delete that lands on one of these: a deploy
            // that just updated an object must never delete it in the same push. The one kind where this can genuinely
            // collide is a RELATIONSHIP — its identity is a name-INDEPENDENT endpoint signature, so an endpoint rename
            // makes ModelCompare emit Create(new sig)+Delete(old sig), and if the rename is skipped live (a same-named
            // impostor collision) ProjectRelSig re-projects the session relationship onto the OLD sig, updating the same
            // live relationship the Delete(old sig) then targets. (Tag-resolved kinds cannot collide: a tag-matched object
            // is an Update, never a Delete, so no Delete ref ever carries a matched object's tag — the retag/republish
            // pair is refused upstream. Created objects likewise bear a fresh identity no Delete ref names.) We still
            // register created objects of every kind here as defence-in-depth; the guard is a no-op for them.
            void Mark(TOM.MetadataObject o) { if (o != null) changedLive?.Add(o); }
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
            // MINOR 2 (replacement coupling): SOURCE-keyed relationship refs that MATCHED live but needed NO write (already
            // present, identical). They are NOT synced (nothing changed → not in syncedRefs) but they ARE present live, so a
            // paired endpoint-re-point Delete must count them as a satisfied replacement — otherwise a converged push (the
            // replacement introduced independently in the live model) would falsely refuse the Delete. Exported as MatchedRefs.
            var matchedRefs = new HashSet<string>(StringComparer.Ordinal);
            // Refs WITHHELD from SyncedRefs: some PART of the object did not deploy (an unapplied refresh policy, a
            // refused ordinal, an unsyncable residual). The parts that did apply are still counted/logged, but the
            // object-level ref must NOT claim a full apply — the caller reconciles ref-by-ref, and a claimed ref with a
            // silently-dropped part is the false-success family. Removed AFTER the renames loop (a rename re-adds the
            // table ref), right before rep.SyncedRefs is built.
            var heldRefs = new HashSet<string>(StringComparer.Ordinal);
            var calcTablesAdded = new List<string>();   // new calc tables to Calculate-recalc after the metadata save
            var dataTablesAdded = new List<string>();    // new Direct Lake DATA tables created — announced EMPTY (rows load on the next service refresh)
            // The SESSION columns THIS deploy will create (new columns on matched tables + columns of new tables). A
            // cross-binding (sort-by / hierarchy level / relationship endpoint) to one of these resolves via this set in
            // BOTH dry-run and commit — under commit the object is already in the live tree, under dry-run it isn't, so
            // without this the resolver would REFUSE under dry-run what commit carries (MAJOR 3: dry-run==commit report).
            var projectedCreated = new HashSet<TOM.Column>();
            // Live-only preserved hierarchy LEVELS — ref built AFTER the renames loop so it follows the table's FINAL name
            // and uses the escaped ref grammar (MINOR 5), mirroring liveOnlyChildren.
            var liveOnlyLevels = new List<(TOM.Table table, string hier, string level)>();
            int desc = 0, ren = 0, vis = 0, cat = 0, fmt = 0, fold = 0, expr = 0, sum = 0, cult = 0, part = 0, nexpr = 0, added = 0, calc = 0, metadata = 0;
            // The STRUCTURAL classes (added 2026-07-09 after the ETS incident: an A/100-offline model scored B/80.6 in
            // the service because its sort-by column, Calendar hierarchy, date-table mark and finding waivers had never
            // deployed and nothing said so). Each answer-affecting; each now carried and counted.
            int sortBy = 0, hier = 0, rel = 0, ann = 0;
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
            if (SupplementIntroducesChange(sourceModelState.Supplement, liveModelState.Supplement))
            {
                try
                {
                    if (apply) ModelCompare.CopyLiveModelSupplement(src, live);
                    metadata++; changes.Add("metadata model"); syncedRefs.Add("model");
                }
                catch (Exception ex) { unmatched.Add("model (metadata was not applied: " + ex.Message + ")"); heldRefs.Add("model"); }
            }
            // OWNED annotations (finding waivers + BPA ignore rules) deploy on their own accounted channel — the ETS
            // model's waivers had silently never reached the service. Excluded from the generic supplement above (so no
            // double-count); a FOREIGN annotation is never read or touched here.
            ann += DeployOwnedAnnotations(src.Annotations, live.Annotations, apply, "model", changes, syncedRefs);

            var liveTablesByTag = ByTag(live.Tables.Cast<TOM.Table>(), t => t.LineageTag);
            var matchedTables = new HashSet<TOM.Table>();   // by REFERENCE — survives a rename (don't key on mutable Name)
            // Shared-expression identity index + match set, built BEFORE the tables loop so a new Direct Lake table can
            // resolve its shared-expression binding by IDENTITY (not a same-named impostor) and a co-authored expression
            // added mid-loop stays out of the live-only sweep. The expression pass below reuses both.
            var liveExprByTag = ByTag(live.Expressions.Cast<TOM.NamedExpression>(), e => LineageOf(e));
            var matchedExprs = new HashSet<TOM.NamedExpression>();   // by REFERENCE — a tag-matched RENAME survives (don't key on the mutable Name)

            foreach (var st in src.Tables)
            {
                var lt = Match(st.LineageTag, st.Name, liveTablesByTag, n => live.Tables.Find(n), t => t.LineageTag, identityStrict, out var tConflict);
                if (lt == null && tConflict != null) { unmatched.Add(RetagConflict(AlmRef.Top("table", st.Name))); continue; }
                if (lt == null)
                {
                    // NEW table. A CALCULATED table is self-contained DAX — the engine derives its columns on a
                    // Calculate pass — so we can create it live + recalc it (bounded, all-proven primitives). A new
                    // DIRECT LAKE table is also deployable: its columns + Entity partition are pure metadata, bound to
                    // the LIVE shared expression (the rows load on the next refresh — announced EMPTY). An IMPORT /
                    // DirectQuery table needs a source/M binding we don't carry, so it stays REPORTED (add via TMDL/XMLA).
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
                            live.Tables.Add(nt); matchedTables.Add(nt); Mark(nt);   // created — keep it out of the live-only sweep
                        }
                        // Owned annotations ride along (dry-run parity: nt.Annotations is empty in both modes, so the
                        // count matches). Any authored child a new-table creation can't carry is NAMED, never dropped.
                        ann += DeployOwnedAnnotations(st.Annotations, nt.Annotations, apply, tableRef, changes, syncedRefs);
                        var calcUncarried = ReportUncarriedNewTableChildren(st, unmatched);
                        added++; calcTablesAdded.Add(st.Name); changes.Add("add calcTable:" + st.Name); syncedRefs.Add(tableRef);
                        if (calcUncarried) heldRefs.Add(tableRef);   // an authored child couldn't be carried — don't claim a full apply
                    }
                    else if (only?.Source is TOM.EntityPartitionSource deps && only.Mode == TOM.ModeType.DirectLake)
                    {
                        var tableRef = AlmRef.Top("table", st.Name);
                        // A Direct Lake partition binds to a shared (M) expression. Resolve the live expression by the
                        // SAME lineage-first identity discipline as everything else (never a same-named impostor a later
                        // strict pass would refuse). The prerequisite must exist on the live model, or be co-deploying in
                        // this same push. Missing on BOTH sides ⇒ REFUSE, don't half-create: the service rejects an Entity
                        // partition with no source.
                        var sessionExpr = deps.ExpressionSource;
                        var exprName = sessionExpr?.Name;
                        TOM.NamedExpression liveExprMatch = null, exprConflict = null;
                        if (!string.IsNullOrEmpty(exprName))
                            liveExprMatch = Match(LineageOf(sessionExpr), exprName, liveExprByTag, n => live.Expressions.Find(n), e => LineageOf(e), identityStrict, out exprConflict);
                        // IDENTITY, not name (BLOCKER 1a): a same-named live expression with a DIFFERENT lineage tag is
                        // NOT the prerequisite this partition binds to — Match refused it. Binding by name would grab the
                        // exact impostor Match rejected, so REFUSE the whole table (the established refusal doctrine), never
                        // fall back to the name (the later expression pass would then report a retag conflict while the
                        // table silently stayed bound to the wrong object).
                        if (exprConflict != null)
                        {
                            unmatched.Add(tableRef + $" (its shared expression '{exprName}' resolves to a same-named live object with a DIFFERENT lineage tag - republished/retagged under you; NOT bound to avoid the wrong object. Re-diff, or deploy via TMDL/XMLA)");
                            heldRefs.Add(tableRef);
                            continue;
                        }
                        var willHaveExpr = liveExprMatch != null
                            || (!string.IsNullOrEmpty(exprName) && src.Expressions.Find(exprName) != null);
                        if (!willHaveExpr)
                        {
                            unmatched.Add(tableRef + $" (Direct Lake table binds to shared expression '{exprName}' that does not exist on the live model - deploy the expression first, or add the table via TMDL/XMLA)");
                            heldRefs.Add(tableRef);
                            continue;
                        }
                        // Build the (detached) new table shell + columns in BOTH modes so owned annotations count
                        // identically under dry-run and apply. Only the live.Tables.Add is gated on apply.
                        var nt = new TOM.Table { Name = st.Name };
                        if (SupportsLineage(live) && !string.IsNullOrEmpty(st.LineageTag)) nt.LineageTag = st.LineageTag;
                        nt.Description = st.Description; nt.IsHidden = st.IsHidden; nt.DataCategory = st.DataCategory;
                        var dlColPairs = new List<(TOM.Column s, TOM.Column l)>();
                        foreach (var sc in st.Columns.Cast<TOM.Column>().Where(c => c.Type != TOM.ColumnType.RowNumber))
                        {
                            var ndc = CloneDeployColumn(sc, SupportsLineage(live));
                            ModelCompare.CopyLiveColumnSupplement(sc, ndc);
                            nt.Columns.Add(ndc);
                            dlColPairs.Add((sc, ndc));
                        }
                        ModelCompare.CopyLiveTableSupplement(st, nt);
                        // RESIDUAL guards (BLOCKER 2): the DL branch used to silently drop authored table/column metadata
                        // the clone can't carry (a table IsPrivate, a column IsKey). Mirror the calc-table + new-column
                        // paths: an uncarryable residual REFUSES the whole table (not half-created), named + ref withheld.
                        if (!JsonSemanticEqual(ModelCompare.LiveTableState(st).Residual, ModelCompare.LiveTableState(nt).Residual)
                            || !RefreshPolicyEqual(st.RefreshPolicy, nt.RefreshPolicy))
                        {
                            unmatched.Add(tableRef + " (carries authored table metadata this live push cannot create; deploy via TMDL/XMLA)");
                            heldRefs.Add(tableRef);
                            continue;
                        }
                        var dlColBad = dlColPairs.FirstOrDefault(p => !JsonSemanticEqual(ModelCompare.LiveColumnState(p.s).Residual, ModelCompare.LiveColumnState(p.l).Residual));
                        if (dlColBad.s != null)
                        {
                            unmatched.Add(AlmRef.Child("column", st.Name, dlColBad.s.Name) + " (carries authored column metadata this live push cannot create; deploy via TMDL/XMLA)");
                            heldRefs.Add(tableRef);
                            continue;
                        }
                        // Resolve or (apply) co-author the live expression the partition binds to. On dry-run we don't add
                        // it — the expression pass below reports it as an Add instead — so the count matches either way.
                        TOM.NamedExpression liveExpr = liveExprMatch ?? live.Expressions.Find(exprName);
                        if (apply && liveExpr == null)
                        {
                            var se2 = src.Expressions.Find(exprName);
                            liveExpr = new TOM.NamedExpression { Name = se2.Name, Kind = se2.Kind, Expression = se2.Expression };
                            if (SupportsLineage(live) && !string.IsNullOrEmpty(LineageOf(se2))) liveExpr.LineageTag = LineageOf(se2);
                            if (!string.IsNullOrEmpty(se2.Description)) liveExpr.Description = se2.Description;
                            live.Expressions.Add(liveExpr); Mark(liveExpr);
                            if (!string.IsNullOrEmpty(LineageOf(se2))) liveExprByTag[LineageOf(se2)] = liveExpr;   // keep the index fresh for a 2nd table
                            matchedExprs.Add(liveExpr);   // the expression pass matches it (no re-count) and the live-only sweep skips it
                            // AUDIT the co-authored add the same way the normal expression pass would (else it deployed
                            // unreported: added early, then seen as "matched" below, so no Added/SyncedRefs entry).
                            added++; changes.Add("add namedExpression:" + se2.Name); syncedRefs.Add(AlmRef.Top("expression", se2.Name));
                        }
                        // Mode MUST ride along: the service rejects an Entity partition at Import mode
                        // ("Partitions of type Entity must be in DirectQuery mode or Direct Lake mode").
                        nt.Partitions.Add(new TOM.Partition
                        {
                            Name = only.Name,
                            Mode = TOM.ModeType.DirectLake,
                            Source = new TOM.EntityPartitionSource { EntityName = deps.EntityName, SchemaName = deps.SchemaName, ExpressionSource = liveExpr }
                        });
                        // Owned annotations (table + each column) ride along; register each new column in the projected
                        // set so a cross-binding to it (a relationship endpoint on this new table) resolves identically in
                        // dry-run and commit (MAJOR 3).
                        foreach (var (sc, ndc) in dlColPairs)
                        {
                            ann += DeployOwnedAnnotations(sc.Annotations, ndc.Annotations, apply, AlmRef.Child("column", st.Name, sc.Name), changes, syncedRefs);
                            projectedCreated.Add(sc);
                        }
                        ann += DeployOwnedAnnotations(st.Annotations, nt.Annotations, apply, tableRef, changes, syncedRefs);
                        var dlUncarried = ReportUncarriedNewTableChildren(st, unmatched);
                        if (apply) { live.Tables.Add(nt); matchedTables.Add(nt); Mark(nt); }
                        added++; dataTablesAdded.Add(st.Name);
                        changes.Add($"add dataTable:{st.Name} (created EMPTY until refreshed; Direct Lake rows load on the next service refresh)");
                        syncedRefs.Add(tableRef);
                        if (dlUncarried) heldRefs.Add(tableRef);   // an authored child couldn't be carried — don't claim a full apply
                    }
                    else
                        unmatched.Add(AlmRef.Top("table", st.Name) + $" (new {(only == null ? "table" : "data")} table - not deployable here; add via TMDL/XMLA)");
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
                if (SupplementIntroducesChange(sourceTableState.Supplement, liveTableState.Supplement))
                {
                    try
                    {
                        if (apply) ModelCompare.CopyLiveTableSupplement(st, lt);
                        metadata++; changes.Add("metadata " + tid); syncedRefs.Add(sourceTableRef);
                    }
                    catch (Exception ex) { unmatched.Add(sourceTableRef + " (metadata was not applied: " + ex.Message + ")"); heldRefs.Add(sourceTableRef); }
                }
                ann += DeployOwnedAnnotations(st.Annotations, lt.Annotations, apply, sourceTableRef, changes, syncedRefs);

                var liveColsByTag = ByTag(lt.Columns.Cast<TOM.Column>(), c => c.LineageTag);
                var matchedCols = new HashSet<TOM.Column>();
                var colPairs = new List<(TOM.Column s, TOM.Column l)>();   // matched (session, live) columns — the SortByColumn pass runs after ALL columns exist so a new sort-key column can be its target
                foreach (var sc in st.Columns)
                {
                    if (sc.Type == TOM.ColumnType.RowNumber) continue;
                    var capturedTable = lt;
                    var lc = Match(sc.LineageTag, sc.Name, liveColsByTag, n => capturedTable.Columns.Find(n), c => c.LineageTag, identityStrict, out var cConflict);
                    if (lc == null && cConflict != null) { unmatched.Add(RetagConflict(AlmRef.Child("column", lt.Name, sc.Name))); continue; }
                    if (lc == null)
                    {
                        // NEW column: a CALCULATED column is just an expression; a DATA column carries a source-column
                        // binding (SourceColumn) — pure metadata a Direct Lake / import table can receive. Both are
                        // created; anything with authored metadata we can't carry is refused (residual guard), never
                        // half-created. (Was report-only for data columns — the ETS gap: a new source column vanished.)
                        var newColumnRef = AlmRef.Child("column", st.Name, sc.Name);
                        if (sc is TOM.CalculatedColumn scNew)
                        {
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
                            if (apply) { lt.Columns.Add(ncc); matchedCols.Add(ncc); Mark(ncc); }
                            added++; changes.Add($"add calcColumn:{lt.Name}/{sc.Name}"); syncedRefs.Add(newColumnRef);
                            ann += DeployOwnedAnnotations(sc.Annotations, ncc.Annotations, apply, newColumnRef, changes, syncedRefs);
                            // A new column joins colPairs so its OWN sort-by deploys (MAJOR 3); register it in the
                            // projected set so another column's sort-by targeting it resolves identically dry-run/commit.
                            colPairs.Add((sc, ncc)); projectedCreated.Add(sc);
                        }
                        else if (sc is TOM.DataColumn scData)
                        {
                            var ndc = CloneDeployColumn(scData, SupportsLineage(live));
                            ModelCompare.CopyLiveColumnSupplement(sc, ndc);
                            var newColumnSourceState = ModelCompare.LiveColumnState(sc);
                            var newColumnState = ModelCompare.LiveColumnState(ndc);
                            if (!JsonSemanticEqual(newColumnSourceState.Residual, newColumnState.Residual))
                            {
                                unmatched.Add(newColumnRef + " (carries authored column metadata this live push cannot create; deploy via TMDL/XMLA)");
                                heldRefs.Add(newColumnRef);
                                continue;
                            }
                            if (apply) { lt.Columns.Add(ndc); matchedCols.Add(ndc); Mark(ndc); }
                            added++; changes.Add($"add dataColumn:{lt.Name}/{sc.Name}"); syncedRefs.Add(newColumnRef);
                            ann += DeployOwnedAnnotations(sc.Annotations, ndc.Annotations, apply, newColumnRef, changes, syncedRefs);
                            // A new column joins colPairs so its OWN sort-by deploys (MAJOR 3); register it in the
                            // projected set so another column's sort-by targeting it resolves identically dry-run/commit.
                            colPairs.Add((sc, ndc)); projectedCreated.Add(sc);
                        }
                        else unmatched.Add(newColumnRef + " (new column of an unsupported kind - not deployable here; add via TMDL/XMLA)");
                        continue;
                    }
                    matchedCols.Add(lc);
                    colPairs.Add((sc, lc));
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
                    if (SupplementIntroducesChange(sourceColumnState.Supplement, liveColumnState.Supplement))
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
                    ann += DeployOwnedAnnotations(sc.Annotations, lc.Annotations, apply, columnRef, changes, syncedRefs);
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
                        if (apply) { lt.Measures.Add(nm); matchedMeas.Add(nm); Mark(nm); }
                        added++; changes.Add($"add measure:{lt.Name}/{sm.Name}"); syncedRefs.Add(newMeasureRef);
                        ann += DeployOwnedAnnotations(sm.Annotations, nm.Annotations, apply, newMeasureRef, changes, syncedRefs);
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
                    if (SupplementIntroducesChange(sourceMeasureState.Supplement, liveMeasureState.Supplement))
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
                    ann += DeployOwnedAnnotations(sm.Annotations, lm.Annotations, apply, measureRef, changes, syncedRefs);
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

                // SORT-BY: SortByColumn re-orders a visual's axis, so a drift changes the answers — the ETS "Month Name"
                // column sorted alphabetically in the service because its sort-by (Month Number) never deployed. Resolve
                // the target through the ONE shared IDENTITY resolver (lineage-first, name fallback) so it binds to the
                // right LIVE column even when a rename hasn't applied yet and a same-named impostor exists; a cleared
                // session sort-by clears it live. After the columns loop so a brand-new key can be the target.
                foreach (var (scol, lcol) in colPairs)
                {
                    var srcSort = scol.SortByColumn;
                    var desired = srcSort == null ? null : ResolveLiveColumnByIdentity(live, srcSort, identityStrict, projectedCreated);
                    if (srcSort != null && desired == null)
                    {
                        // The sort-key column has no live counterpart — can't bind; report, never guess an endpoint.
                        unmatched.Add(AlmRef.Child("column", lt.Name, lcol.Name) + $" (sort-by column '{srcSort.Name}' has no live counterpart - sort-by not deployed)");
                        heldRefs.Add(AlmRef.Child("column", st.Name, scol.Name));
                        continue;
                    }
                    if (ReferenceEquals(desired, lcol.SortByColumn)) continue;   // already bound to the same live column (identity, not name)
                    if (apply) lcol.SortByColumn = desired;
                    sortBy++; changes.Add("sortBy " + AlmRef.Child("column", lt.Name, lcol.Name)); syncedRefs.Add(AlmRef.Child("column", st.Name, scol.Name));
                }

                // HIERARCHIES: a user-facing drill path (the ETS Calendar hierarchy never deployed). Matched by NAME; a
                // NEW hierarchy is created with its levels bound to LIVE columns (resolved by identity); a DRIFTED one is
                // reconciled LEVEL-BY-LEVEL — matched levels keep their authored metadata, session-new levels are added,
                // and a LIVE-ONLY level (e.g. an authored "Day" the session doesn't carry) is PRESERVED and reported
                // (absence never deletes, one level down); a live-only hierarchy is likewise reported, never deleted.
                var matchedHiers = new HashSet<TOM.Hierarchy>();
                foreach (var sh in st.Hierarchies)
                {
                    var lh = lt.Hierarchies.Find(sh.Name);
                    var href = AlmRef.Child("hierarchy", st.Name, sh.Name);
                    if (lh == null)
                    {
                        if (!HierarchyLevelsResolvable(sh, live, identityStrict, projectedCreated, out var missing))
                        {
                            unmatched.Add(href + $" (hierarchy level column '{missing}' has no live counterpart - not created)");
                            heldRefs.Add(href); continue;
                        }
                        // Build the hierarchy + levels in BOTH modes so owned annotations count identically (dry-run
                        // parity); attach + bind columns + add to the live tree only under apply. Owned annotations on the
                        // new hierarchy AND its levels ride along (MAJOR 4).
                        var nh = new TOM.Hierarchy { Name = sh.Name, Description = sh.Description, IsHidden = sh.IsHidden, DisplayFolder = sh.DisplayFolder };
                        if (SupportsLineage(live) && !string.IsNullOrEmpty(sh.LineageTag)) nh.LineageTag = sh.LineageTag;
                        if (apply) { lt.Hierarchies.Add(nh); Mark(nh); }   // attach to the table FIRST so a Level.Column can bind to the table's live columns
                        foreach (var sl in sh.Levels.Cast<TOM.Level>().OrderBy(l => l.Ordinal))
                        {
                            var nl = new TOM.Level { Name = sl.Name, Ordinal = sl.Ordinal };
                            if (apply)
                            {
                                nl.Column = ResolveLiveColumnByIdentity(live, sl.Column, identityStrict, projectedCreated);
                                if (SupportsLineage(live) && !string.IsNullOrEmpty(sl.LineageTag)) nl.LineageTag = sl.LineageTag;
                                if (!string.IsNullOrEmpty(sl.Description)) nl.Description = sl.Description;
                                nh.Levels.Add(nl);
                            }
                            ann += DeployOwnedAnnotations(sl.Annotations, nl.Annotations, apply, LevelRef(st.Name, sh.Name, sl.Name), changes, syncedRefs);
                        }
                        if (apply) matchedHiers.Add(nh);
                        ann += DeployOwnedAnnotations(sh.Annotations, nh.Annotations, apply, href, changes, syncedRefs);
                        hier++; changes.Add($"add hierarchy:{lt.Name}/{sh.Name}"); syncedRefs.Add(href);
                        continue;
                    }
                    matchedHiers.Add(lh);
                    // MAJOR 4: an existing hierarchy's + its matched levels' owned annotations (BPA ignore rules) must
                    // deploy even when the level structure is unchanged — before the equal-fast-path so drift isn't hidden.
                    ann += DeployHierarchyOwnedAnnotations(sh, lh, apply, href, lt.Name, changes, syncedRefs);
                    if (HierarchyLevelsEqual(sh, lh, live, identityStrict, projectedCreated)) continue;
                    if (!HierarchyLevelsResolvable(sh, live, identityStrict, projectedCreated, out var missing2))
                    {
                        unmatched.Add(href + $" (hierarchy level column '{missing2}' has no live counterpart - not re-levelled)");
                        heldRefs.Add(href); continue;
                    }
                    var changed = ReconcileHierarchyLevels(sh, lh, live, apply, identityStrict, projectedCreated, href, lt.Name, changes, syncedRefs, ref ann, out var relRefusal, out var preservedLevels);
                    foreach (var lvl in preservedLevels)
                        liveOnlyLevels.Add((lt, sh.Name, lvl));   // ref built post-rename with the escaped grammar (MINOR 5)
                    if (relRefusal != null)
                    {
                        unmatched.Add(href + $" ({relRefusal} - not re-levelled; deploy via TMDL/XMLA)");
                        heldRefs.Add(href); continue;
                    }
                    if (changed) { hier++; changes.Add($"relevel hierarchy:{lt.Name}/{sh.Name}"); syncedRefs.Add(href); }
                }
                foreach (var lh in lt.Hierarchies.Cast<TOM.Hierarchy>().Where(h => !matchedHiers.Contains(h)))
                    liveOnlyChildren.Add((lt, "hierarchy", lh.Name));   // ref built post-rename (follows the table's final name)

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

            // RELATIONSHIPS: crossfilter / active / cardinality change the ANSWERS, so a relationship deploys. Matched by
            // ENDPOINT SIGNATURE (a relationship's guid name is NOT its identity — the same star join renamed is the same
            // relationship), so drift updates in place and a rename never duplicates. A NEW relationship is created with
            // its endpoints resolved in the LIVE tree (never the session columns). A live-only relationship is REPORTED,
            // never deleted (absence never deletes). Runs after the tables loop so every endpoint column exists live.
            var liveRelsBySig = new Dictionary<string, TOM.Relationship>(StringComparer.Ordinal);
            foreach (var lrel in live.Relationships.Cast<TOM.Relationship>())
            { var s = RelSig(lrel); if (!liveRelsBySig.ContainsKey(s)) liveRelsBySig[s] = lrel; }
            // Live relationship NAMES — a new relationship reuses the session name only when it's free live, else a fresh
            // GUID: a duplicate name would throw inside the atomic SaveChanges (BLOCKER 1c — duplicate-add impossible).
            var liveRelNames = new HashSet<string>(
                live.Relationships.Cast<TOM.Relationship>().Select(r => r.Name).Where(n => !string.IsNullOrEmpty(n)), StringComparer.Ordinal);
            var matchedRels = new HashSet<TOM.Relationship>();
            foreach (var srel in src.Relationships.Cast<TOM.Relationship>())
            {
                // Project the session relationship's identity onto the LIVE tree via IDENTITY-resolved endpoints (BLOCKER
                // 1c): this matches a relationship whose endpoint was RENAMED (the name-based signature would drift → a
                // duplicate add, often with a duplicate GUID name) and stops drift landing on a wrong (impostor-bound)
                // relationship. syncedRefs uses the SESSION-keyed name signature (what ModelCompare emits) so a selective
                // push reconciles it.
                var (fromCol, toCol, projSig) = ProjectRelSig(srel, live, identityStrict, projectedCreated);
                var syncedSig = RelSig(srel);
                if (projSig != null && liveRelsBySig.TryGetValue(projSig, out var lr))
                {
                    matchedRels.Add(lr);
                    // MINOR 2: this source relationship is PRESENT live (matched by projected endpoint signature) — record it
                    // as matched-live whether or not any prop needed a write, so a paired endpoint-re-point Delete counts the
                    // replacement as satisfied on a converged push (already-present, identical) even when syncedRefs stays empty.
                    matchedRefs.Add("relationship:" + syncedSig);
                    int before = rel;
                    rel += SyncRelationshipProps(srel, lr, apply, changes);
                    if (rel > before) syncedRefs.Add("relationship:" + syncedSig);
                    int annBefore = ann;
                    ann += DeployOwnedAnnotations(srel.Annotations, lr.Annotations, apply, "relationship:" + syncedSig, changes, syncedRefs);
                    // BLOCKER 1: protect this matched live relationship from an explicit Delete(old sig) the SAME
                    // endpoint-rename diff carries. Two triggers, both meaning "we kept/wrote this relationship, so the
                    // delete would contradict us":
                    //   • a property / owned-annotation change was applied (rel/ann advanced), OR
                    //   • an ENDPOINT RENAME is in play — the session's own name-signature differs from where identity
                    //     projected it (syncedSig != projSig), i.e. a renamed endpoint didn't land live (a same-named
                    //     impostor collision skipped it), so the OLD name-signature still resolves to this very
                    //     relationship. A matched-and-UNTOUCHED relationship with a stable signature (a normal delete of a
                    //     genuinely-removed relationship) is NOT marked, so that delete still proceeds.
                    if (rel > before || ann > annBefore || !string.Equals(projSig, syncedSig, StringComparison.Ordinal)) Mark(lr);
                    continue;
                }
                // NEW: only a single-column relationship carries endpoints we can resolve in the live tree.
                if (srel is TOM.SingleColumnRelationship ssc && ssc.FromColumn != null && ssc.ToColumn != null)
                {
                    if (fromCol == null || toCol == null)
                    {
                        unmatched.Add("relationship:" + RelDisplay(srel) + " (an endpoint column has no live counterpart - not created; add via TMDL/XMLA)");
                        continue;
                    }
                    // Build in BOTH modes so owned annotations count identically (dry-run parity); bind endpoints + add to
                    // the live tree only under apply.
                    var relName = (!string.IsNullOrEmpty(srel.Name) && !liveRelNames.Contains(srel.Name)) ? srel.Name : System.Guid.NewGuid().ToString();
                    var nr = new TOM.SingleColumnRelationship
                    {
                        Name = relName,
                        FromColumn = apply ? fromCol : null, ToColumn = apply ? toCol : null,
                        FromCardinality = ssc.FromCardinality, ToCardinality = ssc.ToCardinality,
                        CrossFilteringBehavior = ssc.CrossFilteringBehavior, IsActive = ssc.IsActive,
                        SecurityFilteringBehavior = ssc.SecurityFilteringBehavior,
                        RelyOnReferentialIntegrity = ssc.RelyOnReferentialIntegrity,
                        JoinOnDateBehavior = ssc.JoinOnDateBehavior
                    };
                    if (apply) { live.Relationships.Add(nr); matchedRels.Add(nr); liveRelNames.Add(relName); Mark(nr); }
                    rel++; changes.Add("add relationship:" + RelDisplay(srel));
                    ann += DeployOwnedAnnotations(srel.Annotations, nr.Annotations, apply, "relationship:" + syncedSig, changes, syncedRefs);
                    syncedRefs.Add("relationship:" + syncedSig);
                }
                else unmatched.Add("relationship:" + RelDisplay(srel) + " (not a single-column relationship - not deployable here; add via TMDL/XMLA)");
            }
            foreach (var lrel in live.Relationships.Cast<TOM.Relationship>().Where(r => !matchedRels.Contains(r)))
                liveOnly.Add("relationship:" + RelDisplay(lrel));

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
                        live.Expressions.Add(ne); matchedExprs.Add(ne); Mark(ne);   // register the new object so the live-only sweep doesn't re-flag it (mirrors matchedTables/Cols/Meas on their add paths)
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
            // Preserved live-only hierarchy LEVELS — built post-rename with the escaped ref grammar (MINOR 5), so the ref
            // follows the table's FINAL name and can't be forged by a delimiter in a name.
            foreach (var (table, hierName, level) in liveOnlyLevels)
                liveOnly.Add(LevelRef(table.Name, hierName, level) + " (live-only, preserved)");

            // Enforce the withhold LAST — a rename of a policy-held table would have re-added its ref above.
            foreach (var h in heldRefs) syncedRefs.Remove(h);

            rep.Descriptions = desc; rep.Renames = ren; rep.Visibility = vis; rep.DataCategories = cat;
            rep.Formats = fmt; rep.Folders = fold; rep.Expressions = expr; rep.SummarizeBy = sum; rep.Cultures = cult;
            rep.Partitions = part; rep.NamedExpressions = nexpr; rep.CalcGroup = calc;
            rep.Metadata = metadata;
            rep.Added = added;
            rep.SortBy = sortBy; rep.Hierarchies = hier; rep.Relationships = rel; rep.Annotations = ann;
            rep.TotalChanges = desc + ren + vis + cat + fmt + fold + expr + sum + cult + part + nexpr + added + calc + metadata
                + sortBy + hier + rel + ann;
            rep.Changes = changes.Take(80).ToArray();
            rep.Unmatched = unmatched.ToArray();
            rep.LiveOnly = liveOnly.ToArray();
            rep.Conflicts = conflicts.ToArray();
            rep.CalcTablesAdded = calcTablesAdded.ToArray();
            rep.DataTablesAdded = dataTablesAdded.ToArray();
            rep.SyncedRefs = syncedRefs.ToArray();
            rep.MatchedRefs = matchedRefs.ToArray();

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

        // DIRECTIONAL supplement comparison: does the SOURCE introduce a property the live model lacks or holds
        // differently? Unlike a symmetric equality, a live-ONLY supplement extra does NOT fire — so a deploy neither
        // counts a phantom change nor (via the additive copy) removes it. "absence never deletes" applied to the metadata
        // supplement, RECURSIVELY: a NESTED object (the calculationGroup) is compared member-wise with the same
        // discipline, so a live-only nested member (e.g. a MultipleOrEmptySelectionExpression the session cleared to
        // null) neither fires the change nor gets nulled out. Owned/foreign annotations no longer appear in these state
        // strings at all (they're off-supplement), so the annotation-array branch is a defensive no-op today.
        private static bool SupplementIntroducesChange(string srcJson, string liveJson)
        {
            if (JsonSemanticEqual(srcJson, liveJson)) return false;
            Newtonsoft.Json.Linq.JObject src, live;
            try { src = Newtonsoft.Json.Linq.JObject.Parse(string.IsNullOrWhiteSpace(srcJson) ? "{}" : srcJson); }
            catch { return !JsonSemanticEqual(srcJson, liveJson); }
            try { live = Newtonsoft.Json.Linq.JObject.Parse(string.IsNullOrWhiteSpace(liveJson) ? "{}" : liveJson); }
            catch { return true; }
            return ObjectIntroducesChange(src, live);
        }

        // The recursive core of the directional comparison — see SupplementIntroducesChange.
        private static bool ObjectIntroducesChange(Newtonsoft.Json.Linq.JObject src, Newtonsoft.Json.Linq.JObject live)
        {
            foreach (var p in src.Properties())
            {
                if (string.Equals(p.Name, "annotations", StringComparison.OrdinalIgnoreCase) && p.Value is Newtonsoft.Json.Linq.JArray sAnns)
                {
                    var lAnns = live.Property("annotations", StringComparison.OrdinalIgnoreCase)?.Value as Newtonsoft.Json.Linq.JArray
                                ?? new Newtonsoft.Json.Linq.JArray();
                    foreach (var sa in sAnns.OfType<Newtonsoft.Json.Linq.JObject>())
                    {
                        var name = (string)sa["name"];
                        var la = lAnns.OfType<Newtonsoft.Json.Linq.JObject>().FirstOrDefault(x => (string)x["name"] == name);
                        if (la == null || !Newtonsoft.Json.Linq.JToken.DeepEquals(la, sa)) return true;
                    }
                }
                else if (p.Value is Newtonsoft.Json.Linq.JObject sObj)
                {
                    // NESTED object (the calculationGroup): live-only members inside it must NOT count as a change — recurse.
                    if (!(live.Property(p.Name, StringComparison.OrdinalIgnoreCase)?.Value is Newtonsoft.Json.Linq.JObject lObj))
                    { if (sObj.HasValues) return true; }
                    else if (ObjectIntroducesChange(sObj, lObj)) return true;
                }
                else
                {
                    var lv = live.Property(p.Name, StringComparison.OrdinalIgnoreCase)?.Value;
                    if (lv == null || !Newtonsoft.Json.Linq.JToken.DeepEquals(lv, p.Value)) return true;
                }
            }
            return false;
        }

        // The annotation families Semanticus itself authors and must carry across a deploy — the finding-waiver store
        // (Semanticus.Analysis.Waivers.Annotation) and TabularEditor's per-object BPA ignore list (+ its historical typo,
        // which TE still reads). Deployed on a dedicated accounted channel; a FOREIGN annotation is never read or touched
        // by it. (The ETS model's waivers had silently never reached the service — an A/100 offline read as B/80.6 live.)
        internal static bool IsOwnedAnnotation(string name)
            => name == "Semanticus_Waivers"
            || name == "BestPracticeAnalyzer_IgnoreRules"
            || name == "BestPractizeAnalyzer_IgnoreRules";

        // Deploy the OWNED annotations from one annotation-bearing object to its live counterpart: add or update each
        // owned annotation the session carries; NEVER delete a live-only one (absence never deletes) and never read a
        // foreign one. Returns the count applied (each counts once). On dry-run it counts + logs but mutates nothing.
        // The collections are dynamic — the raw-TOM annotation collection type differs per owner (model/table/column/
        // measure); the strong-typed Annotation values flow through unchanged (mirrors ModelCompare.CopyAnnotations).
        private static int DeployOwnedAnnotations(dynamic src, dynamic live, bool apply, string objRef, List<string> changes, HashSet<string> synced)
        {
            int n = 0;
            foreach (var sa in ((System.Collections.IEnumerable)src).Cast<TOM.Annotation>().Where(a => a?.Name != null && IsOwnedAnnotation(a.Name)))
            {
                TOM.Annotation la = (bool)live.Contains(sa.Name) ? (TOM.Annotation)live[sa.Name] : null;
                if (la != null && string.Equals(la.Value, sa.Value, StringComparison.Ordinal)) continue;   // already in sync
                if (apply)
                {
                    if (la != null) la.Value = sa.Value;
                    else live.Add(new TOM.Annotation { Name = sa.Name, Value = sa.Value });
                }
                n++; changes.Add("annotation " + objRef + ":" + sa.Name);
                synced?.Add(objRef);
            }
            return n;
        }

        // Deploy the owned annotations of a MATCHED hierarchy and its matched levels (paired by name to the live
        // hierarchy). A structural creation (new hierarchy / new level) deploys its own annotations where it is built;
        // this covers the drift case (an existing hierarchy/level whose BPA-ignore rules changed) so those never vanish
        // while the object reports synced (MAJOR 4). A session-new level has no live counterpart here → skipped (the
        // reconcile deploys it). Idempotent; counts once per changed annotation.
        private static int DeployHierarchyOwnedAnnotations(TOM.Hierarchy sh, TOM.Hierarchy lh, bool apply, string href, string table, List<string> changes, HashSet<string> synced)
        {
            int n = DeployOwnedAnnotations(sh.Annotations, lh.Annotations, apply, href, changes, synced);
            int levelChanges = 0;
            foreach (var sl in sh.Levels.Cast<TOM.Level>())
            {
                var ll = lh.Levels.Find(sl.Name);
                if (ll != null) levelChanges += DeployOwnedAnnotations(sl.Annotations, ll.Annotations, apply, LevelRef(table, sh.Name, sl.Name), changes, synced);
            }
            // MAJOR 2: selective reconciliation only understands the PARENT hierarchy ref — ModelCompare exposes a
            // hierarchy change as hierarchy:<t>/<h>, NEVER level:<t>/<h>/<l>. A level-only owned-annotation change (on a
            // structurally-equal hierarchy, whose fast path exits without claiming the hierarchy ref) would otherwise
            // claim only the level ref, and the selected hierarchy ref reconciles as FAILED though the annotation
            // deployed. Claim the parent hierarchy ref whenever any LEVEL annotation deploys. (The level ref is kept too —
            // harmless, and precise for a future level-aware reconciler. A hierarchy-level annotation already claims href
            // via its own DeployOwnedAnnotations objRef=href above.)
            if (levelChanges > 0) synced?.Add(href);
            return n + levelChanges;
        }

        // Build a detached deployable copy of a session column for a NEW live column/table — copies only the metadata a
        // live push carries (the source binding + display props), never the SortByColumn reference (that points into the
        // SESSION tree; sort-by binds to the LIVE column separately). Lineage rides only where the target CL supports it.
        private static TOM.Column CloneDeployColumn(TOM.Column sc, bool supportsLineage)
        {
            TOM.Column c;
            if (sc is TOM.CalculatedColumn cc) c = new TOM.CalculatedColumn { Expression = cc.Expression };
            else { var d = (TOM.DataColumn)sc; c = new TOM.DataColumn { SourceColumn = d.SourceColumn, DataType = d.DataType }; }
            c.Name = sc.Name;
            if (supportsLineage && !string.IsNullOrEmpty(sc.LineageTag)) c.LineageTag = sc.LineageTag;
            c.Description = sc.Description; c.IsHidden = sc.IsHidden; c.DataCategory = sc.DataCategory;
            c.FormatString = sc.FormatString; c.DisplayFolder = sc.DisplayFolder; c.SummarizeBy = sc.SummarizeBy;
            return c;
        }

        // Every level of a session hierarchy resolves — by the ONE shared identity discipline — to a column that exists on
        // the LIVE model (levels bind to the live instance, never the session one, and never a same-named impostor). A
        // miss ⇒ we refuse to create/re-level (report it), never guess a binding.
        private static bool HierarchyLevelsResolvable(TOM.Hierarchy sh, TOM.Model live, bool identityStrict, HashSet<TOM.Column> projected, out string missing)
        {
            foreach (var sl in sh.Levels.Cast<TOM.Level>())
            {
                if (sl.Column == null || ResolveLiveColumnByIdentity(live, sl.Column, identityStrict, projected) == null)
                { missing = sl.Column?.Name ?? "(unbound)"; return false; }
            }
            missing = null; return true;
        }

        // Are two hierarchies' level sets identical (ordinal, name, and — by IDENTITY, not name — bound column)? A
        // fast-path so an unchanged hierarchy skips reconciliation; any difference routes to the level-by-level reconcile.
        // The column is compared against the IDENTITY-RESOLVED live column (never its name): a live level bound to a same-
        // named IMPOSTOR must NOT read equal (it would never be reconciled onto the real lineage-matched column).
        private static bool HierarchyLevelsEqual(TOM.Hierarchy sh, TOM.Hierarchy lh, TOM.Model live, bool identityStrict, HashSet<TOM.Column> projected)
        {
            var s = sh.Levels.Cast<TOM.Level>().OrderBy(l => l.Ordinal).ToList();
            var l = lh.Levels.Cast<TOM.Level>().OrderBy(x => x.Ordinal).ToList();
            if (s.Count != l.Count) return false;
            for (int i = 0; i < s.Count; i++)
            {
                if (s[i].Ordinal != l[i].Ordinal || !string.Equals(s[i].Name, l[i].Name, StringComparison.Ordinal)) return false;
                // The live level's column must BE the identity-resolved counterpart of the session level's column — a
                // name-only match (a same-named impostor bound live) is NOT equal, so it routes to reconcile + rebind.
                if (!ReferenceEquals(l[i].Column, ResolveLiveColumnByIdentity(live, s[i].Column, identityStrict, projected))) return false;
            }
            return true;
        }

        // Reconcile a live hierarchy's levels to the session's WITHOUT the destructive Clear()+rebuild that deleted a
        // live-only level (an authored "Day" the session didn't carry) and dropped every matched level's authored
        // metadata (description / annotations / lineage). Matched by NAME: update ordinal + bound column (by identity),
        // preserve everything else. Session-new levels are added. Live-only levels are PRESERVED (kept at their original
        // ordinals) and reported. If a session level's target ordinal would collide with a preserved live-only level's
        // ordinal we REFUSE the re-level (honest report) rather than corrupt the drill path. Returns whether anything
        // changed; <paramref name="refusal"/> is set on a collision.
        private static bool ReconcileHierarchyLevels(TOM.Hierarchy sh, TOM.Hierarchy lh, TOM.Model live, bool apply, bool identityStrict,
            HashSet<TOM.Column> projected, string href, string table, List<string> changes, HashSet<string> synced, ref int ann,
            out string refusal, out List<string> preservedLiveOnly)
        {
            refusal = null;
            preservedLiveOnly = new List<string>();
            var srcLevels = sh.Levels.Cast<TOM.Level>().OrderBy(l => l.Ordinal).ToList();
            var srcNames = new HashSet<string>(srcLevels.Select(l => l.Name), StringComparer.Ordinal);
            var liveByName = new Dictionary<string, TOM.Level>(StringComparer.Ordinal);
            foreach (var ll in lh.Levels.Cast<TOM.Level>()) if (!liveByName.ContainsKey(ll.Name)) liveByName[ll.Name] = ll;
            var liveOnly = lh.Levels.Cast<TOM.Level>().Where(l => !srcNames.Contains(l.Name)).ToList();
            preservedLiveOnly.AddRange(liveOnly.Select(l => l.Name));

            // Collision guard: a session target ordinal equal to a preserved live-only level's current ordinal.
            var liveOnlyOrdinals = new HashSet<int>(liveOnly.Select(l => l.Ordinal));
            var collide = srcLevels.FirstOrDefault(sl => liveOnlyOrdinals.Contains(sl.Ordinal));
            if (collide != null)
            {
                var clashName = liveOnly.First(l => l.Ordinal == collide.Ordinal).Name;
                refusal = $"level '{collide.Name}' ordinal {collide.Ordinal} collides with preserved live-only level '{clashName}'";
                return false;
            }

            // Does anything actually change? (matched-level ordinal/column drift, or a session-new level)
            bool changed = srcLevels.Any(sl => !liveByName.TryGetValue(sl.Name, out var ll)
                || ll.Ordinal != sl.Ordinal
                || !ReferenceEquals(ll.Column, ResolveLiveColumnByIdentity(live, sl.Column, identityStrict, projected)));
            if (!changed) return false;

            if (apply)
            {
                // Two-phase ordinal move: park EVERY existing level at a disjoint-high temp ordinal so no transient
                // duplicate arises, then assign session ordinals; live-only levels are restored to their originals
                // (guaranteed collision-free by the guard above).
                var origLiveOnly = liveOnly.ToDictionary(l => l, l => l.Ordinal);
                int temp = lh.Levels.Cast<TOM.Level>().Select(l => l.Ordinal).Concat(srcLevels.Select(s => s.Ordinal)).DefaultIfEmpty(0).Max() + 1;
                foreach (var l in lh.Levels.Cast<TOM.Level>().ToList()) l.Ordinal = temp++;
                foreach (var sl in srcLevels)
                {
                    if (liveByName.TryGetValue(sl.Name, out var ll))
                    {
                        ll.Ordinal = sl.Ordinal;
                        ll.Column = ResolveLiveColumnByIdentity(live, sl.Column, identityStrict, projected);   // rebind; keep description/annotations/lineage
                    }
                    else
                    {
                        var nl = new TOM.Level { Name = sl.Name, Ordinal = sl.Ordinal, Column = ResolveLiveColumnByIdentity(live, sl.Column, identityStrict, projected) };
                        if (SupportsLineage(live) && !string.IsNullOrEmpty(sl.LineageTag)) nl.LineageTag = sl.LineageTag;
                        if (!string.IsNullOrEmpty(sl.Description)) nl.Description = sl.Description;
                        lh.Levels.Add(nl);
                    }
                }
                foreach (var kv in origLiveOnly) kv.Key.Ordinal = kv.Value;   // preserved live-only levels keep their ordinals
            }
            // A session-NEW level's owned annotations (BPA ignore rules) must ride along — else the level reports synced
            // while its ignore rules vanish (MAJOR 4). Deployed against the real live level under apply, a detached one
            // under dry-run so the count matches (dry-run parity). Matched-level + hierarchy-object annotations are
            // deployed by the caller (DeployHierarchyOwnedAnnotations) before the equal-fast-path.
            foreach (var sl in srcLevels.Where(l => !liveByName.ContainsKey(l.Name)))
            {
                var target = apply ? lh.Levels.Find(sl.Name) : new TOM.Level();
                ann += DeployOwnedAnnotations(sl.Annotations, target.Annotations, apply, LevelRef(table, sh.Name, sl.Name), changes, synced);
            }
            return true;
        }

        // Sync a matched relationship's answer-affecting properties in place — each differing property counts once (a
        // crossfilter flip + an active flip = 2). The endpoints are the identity (already equal by signature), so they
        // are never re-pointed here; only the behaviour props drift.
        private static int SyncRelationshipProps(TOM.Relationship s, TOM.Relationship l, bool apply, List<string> changes)
        {
            int n = 0;
            var disp = RelDisplay(l);
            if (s.IsActive != l.IsActive) { if (apply) l.IsActive = s.IsActive; n++; changes.Add("relationship active:" + disp); }
            if (s is TOM.SingleColumnRelationship ss && l is TOM.SingleColumnRelationship ls)
            {
                if (ss.CrossFilteringBehavior != ls.CrossFilteringBehavior) { if (apply) ls.CrossFilteringBehavior = ss.CrossFilteringBehavior; n++; changes.Add("relationship crossfilter:" + disp); }
                if (ss.SecurityFilteringBehavior != ls.SecurityFilteringBehavior) { if (apply) ls.SecurityFilteringBehavior = ss.SecurityFilteringBehavior; n++; changes.Add("relationship security-filter:" + disp); }
                if (ss.RelyOnReferentialIntegrity != ls.RelyOnReferentialIntegrity) { if (apply) ls.RelyOnReferentialIntegrity = ss.RelyOnReferentialIntegrity; n++; changes.Add("relationship rely-on-ri:" + disp); }
                if (ss.FromCardinality != ls.FromCardinality) { if (apply) ls.FromCardinality = ss.FromCardinality; n++; changes.Add("relationship from-cardinality:" + disp); }
                if (ss.ToCardinality != ls.ToCardinality) { if (apply) ls.ToCardinality = ss.ToCardinality; n++; changes.Add("relationship to-cardinality:" + disp); }
                if (ss.JoinOnDateBehavior != ls.JoinOnDateBehavior) { if (apply) ls.JoinOnDateBehavior = ss.JoinOnDateBehavior; n++; changes.Add("relationship join-on-date:" + disp); }
            }
            return n;
        }

        // THE ONE shared identity resolver for every cross-binding site (sort-by target, hierarchy level column,
        // relationship endpoint): resolve a SESSION column to its LIVE counterpart by the SAME lineage-first discipline
        // the rest of the deploy uses (tag match, then name fallback — gated by identityStrict), scoped to the live table
        // that matches the session column's OWNING table by identity. This is what stops a structural binding from
        // landing on a same-named IMPOSTOR and makes it follow a lineage-matched rename even before that rename's Name
        // has been applied to the live tree (the renames loop runs later). Every binding site routes through here so the
        // identity discipline can't drift per-site.
        private static TOM.Column ResolveLiveColumnByIdentity(TOM.Model live, TOM.Column sessionCol, bool identityStrict, HashSet<TOM.Column> projected = null)
        {
            var stbl = sessionCol?.Table;
            if (stbl == null) return null;
            var lt = Match(stbl.LineageTag, stbl.Name, ByTag(live.Tables.Cast<TOM.Table>(), t => t.LineageTag),
                           n => live.Tables.Find(n), t => t.LineageTag, identityStrict, out _);
            if (lt != null)
            {
                var lc = Match(sessionCol.LineageTag, sessionCol.Name, ByTag(lt.Columns.Cast<TOM.Column>(), c => c.LineageTag),
                               n => lt.Columns.Find(n), c => c.LineageTag, identityStrict, out _);
                if (lc != null) return lc;
            }
            // DRY-RUN/COMMIT PARITY: the target is a column THIS SAME deploy creates (a new column on a matched table, or
            // a column of a new table) but has not yet added to the live tree — under commit the object is already added
            // (the live lookup above succeeds), but under DRY-RUN it isn't, so a cross-binding (sort-by / hierarchy level /
            // relationship endpoint) to a would-be-created object must still resolve, or dry-run REFUSES what commit
            // carries. The session object is the stand-in (a non-null marker); it is never written into the live tree
            // (every mutation site is apply-guarded and, under apply, uses the real live lookup above).
            if (projected != null && projected.Contains(sessionCol)) return sessionCol;
            return null;
        }

        // NAME every authored child a NEW-table creation path does NOT carry, so a new table with measures / hierarchies /
        // a sort-by / calc-group items REPORTS them instead of silently dropping them (the false-success family the ETS
        // incident exposed). The table shell + the columns/partition it DID carry are already counted; these are the
        // residual, deployable via TMDL/XMLA.
        // Returns TRUE if it reported any uncarried child — the caller then WITHHOLDS the table ref from SyncedRefs so a
        // selective push cannot reconcile the table Create as fully applied while an authored child (e.g. a measure) is
        // absent (BLOCKER 2 — the false-success family). The table shell + the columns/partition it DID carry stay
        // counted/logged; only the object-level ref is withheld.
        private static bool ReportUncarriedNewTableChildren(TOM.Table st, List<string> unmatched)
        {
            int before = unmatched.Count;
            foreach (var sm in st.Measures.Cast<TOM.Measure>())
                unmatched.Add(AlmRef.Child("measure", st.Name, sm.Name) + " (measure on a newly created table - not carried here; deploy via TMDL/XMLA)");
            foreach (var sh in st.Hierarchies.Cast<TOM.Hierarchy>())
                unmatched.Add(AlmRef.Child("hierarchy", st.Name, sh.Name) + " (hierarchy on a newly created table - not carried here; deploy via TMDL/XMLA)");
            foreach (var sc in st.Columns.Cast<TOM.Column>().Where(c => c.Type != TOM.ColumnType.RowNumber && c.SortByColumn != null))
                unmatched.Add(AlmRef.Child("column", st.Name, sc.Name) + " (sort-by on a newly created table - not carried here; deploy via TMDL/XMLA)");
            var items = st.CalculationGroup?.CalculationItems;
            if (items != null)
                foreach (var ci in items.Cast<TOM.CalculationItem>())
                    unmatched.Add(AlmRef.Child("calculationitem", st.Name, ci.Name) + " (calculation item on a newly created table - not carried here; deploy via TMDL/XMLA)");
            return unmatched.Count > before;
        }

        // Human-readable relationship label (ASCII arrow) for reporting — "Sales[DateKey] -> Date[Date]". Distinct from
        // RelSig (the escaped, name-independent MATCH signature); this one is for LiveOnly/change display, never a key.
        // Shows whatever endpoints are resolvable (a partial single-column relationship renders the known side rather
        // than degrading the whole label to the internal guid name); a genuinely non-single-column one falls back to name.
        private static string RelDisplay(TOM.Relationship r)
        {
            if (r is TOM.SingleColumnRelationship sc && (sc.FromColumn != null || sc.ToColumn != null))
            {
                string End(TOM.Column c) => c == null ? "?" : $"{c.Table?.Name}[{c.Name}]";
                return $"{End(sc.FromColumn)} -> {End(sc.ToColumn)}";
            }
            // A non-single-column relationship's Name is an internal GUID, not a human identity — render an honest
            // generic descriptor rather than leaking the guid into a report (RelSig, the match KEY, still uses the name).
            return "(non-single-column relationship)";
        }
    }
}
