using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Semanticus.Analysis;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Linguistics;

namespace Semanticus.Engine
{
    /// <summary>set_synonyms receipt: how many dead entities were self-healed away + the advisory to surface.</summary>
    public sealed class SetOutcome
    {
        public int PrunedEntities;
        public string Warning;   // null on the clean path
    }

    /// <summary>
    /// Semanticus-owned replacement for the donor SynonymHelper.SetSynonyms (reads stay on the donor). WHY:
    /// assigning Culture.Content runs the AS linguistic validator, which folds every entity's bound object name
    /// (lower-case + camel-split + lemmatise) into ONE term dictionary — so two entities whose bound names
    /// normalise identically make EVERY synonym write throw "An item with the same key has already been added",
    /// model-wide, whatever the target. The live culprits are entities generated for hidden shadow columns that
    /// duplicate visible names; Q&amp;A ignores hidden objects, so those entities are pure collision risk. This
    /// writer refuses hidden targets, prunes entities bound to hidden/deleted objects (self-heal), pre-flights
    /// collisions in two tiers (an exact normalised match refuses; a plural-fold match only warns — the fold is
    /// an approximation that must never block a write AS would accept), and backstops the AS throw for lemmas
    /// QnaTerms can't predict. See docs/bug-set_synonyms-duplicate-name-collision.md.
    /// </summary>
    public static class LsdlSynonyms
    {
        /// <summary>The shared normaliser — ONE implementation (Analysis, so the readiness rule sees the same terms).</summary>
        public static string NormalizeQnaTerm(string name) => QnaTerms.NormalizeFolded(name);

        public static SetOutcome SetSynonyms(TabularNamedObject obj, Culture culture, string synonyms)
        {
            if (culture == null || culture.ContentType != ContentType.Json || string.IsNullOrEmpty(culture.Content))
                throw new InvalidOperationException($"Can't set synonyms: culture '{culture?.Name}' has no linguistic schema. Run enable_qna first.");

            if (IsEffectivelyHidden(obj))
                throw new InvalidOperationException(
                    $"set_synonyms: {Describe(obj)} is hidden. Q&A ignores hidden objects, so synonyms on it are dead weight and a collision risk in the linguistic schema. Unhide the object, or target the visible duplicate instead.");

            var model = obj.Model;
            var lsdl = JObject.Parse(culture.Content);
            if (!(lsdl["Entities"] is JObject jEntities)) lsdl["Entities"] = jEntities = new JObject();

            var outcome = new SetOutcome { PrunedEntities = Prune(model, lsdl, jEntities) };
            if (outcome.PrunedEntities > 0)
                outcome.Warning = PruneNote(outcome.PrunedEntities);

            // Tier 1 (hard): an EXACT normalised match is a collision on any reading of the validator — refuse.
            var exact = CollisionGroups(model, jEntities, obj, QnaTerms.Normalize);
            if (exact.Count > 0)
                throw new InvalidOperationException(
                    $"set_synonyms: {CollisionMessage(exact)} Q&A and Copilot treat them as the same term, and the linguistic schema cannot be committed while both exist. Rename or hide one object in each group; hiding also requires its linguistic entity to be pruned, which set_synonyms does automatically on the next call.");

            // Tier 2 (soft): a plural-fold match (Months/Month) is only our APPROXIMATION of the AS lemmatiser —
            // it may be wrong (News is not the plural of New), so it must never block a write the validator would
            // accept. Proceed, warn, and let the commit-time backstop carry the live truth.
            var folded = CollisionGroups(model, jEntities, obj, QnaTerms.NormalizeFolded);
            if (folded.Count > 0)
                AppendWarning(outcome,
                    $"Possible Q&A term collision after plural folding: {CollisionMessage(folded)} The write proceeded; if the validator rejects the schema, rename or hide one of the named objects.");

            var jEntity = FindEntity(obj, jEntities) ?? CreateEntity(obj, jEntities);
            if (jEntity == null)
                throw new InvalidOperationException($"set_synonyms: {Describe(obj)} is not a table/column/measure/hierarchy/level, so it can't own linguistic terms.");
            SetTerms(jEntity, synonyms ?? "");

            Commit(culture, lsdl);
            return outcome;
        }

        /// <summary>enable_qna's self-heal: prune dead entities from an EXISTING schema, reassigning Content only
        /// when something was actually pruned. Returns the prune count (0 for an unreadable/absent schema).</summary>
        public static int PruneDeadEntities(Model m, Culture culture)
        {
            if (culture == null || culture.ContentType != ContentType.Json || string.IsNullOrEmpty(culture.Content)) return 0;
            JObject lsdl;
            try { lsdl = JObject.Parse(culture.Content); } catch { return 0; }   // unreadable schema: leave it alone
            if (!(lsdl["Entities"] is JObject jEntities)) return 0;
            var pruned = Prune(m, lsdl, jEntities);
            if (pruned > 0) Commit(culture, lsdl);
            return pruned;
        }

        /// <summary>The one-line advisory both writers surface after a prune.</summary>
        public static string PruneNote(int pruned) =>
            $"Pruned {pruned} linguistic entities bound to hidden or deleted objects; Q&A ignores hidden objects.";

        // ---- rename cascade --------------------------------------------------------------------------------

        /// <summary>After an object rename, follow every JSON culture's linguistic bindings that named the object's
        /// OLD identity onto its new name — otherwise the entity ORPHANS (its Binding names a gone object) and the
        /// next set_synonyms/enable_qna prunes it, silently deleting the authored synonyms one write later. Must be
        /// called AFTER the TOM rename and INSIDE the caller's MutateAsync: it writes Culture.Content through the
        /// tracked setter (see Commit), so the rewrite joins the rename's undo batch and a dry_run rolls both back
        /// together. Fail-safe like Prune — only bindings whose shape TryParseBinding recognises and whose name(s)
        /// match the old identity are touched; anything unreadable is left byte-for-byte as-is. Entity KEYS (slug
        /// labels) are deliberately left stale: the Binding is the identity (FindEntity/TryResolve match on it), and
        /// re-keying could collide with an unrelated authored entity — which is also why entity-key REFERENCES under
        /// SemanticSlots/Relationships need no rewrite. NON-THROWING: a culture the validator refuses is left
        /// untouched and reported via the out warning. Returns the cultures changed.</summary>
        public static int CascadeRename(TabularNamedObject obj, string oldName) => CascadeRename(obj, oldName, out _);

        /// <inheritdoc cref="CascadeRename(TabularNamedObject, string)"/>
        public static int CascadeRename(TabularNamedObject obj, string oldName, out string warning)
        {
            warning = null;
            if (obj == null || string.IsNullOrEmpty(oldName) || string.Equals(oldName, obj.Name, StringComparison.Ordinal)) return 0;
            var model = obj.Model;
            if (model == null) return 0;

            var cultures = 0;
            foreach (var culture in model.Cultures)
            {
                if (culture.ContentType != ContentType.Json || string.IsNullOrEmpty(culture.Content)) continue;
                JObject lsdl;
                try { lsdl = JObject.Parse(culture.Content); } catch { continue; }   // unreadable schema: leave it alone

                var changed = 0;
                // Walk the WHOLE document, not just Entities: Relationships/SemanticSlots phrasings carry the same
                // Binding shape (a relationship binds its ConceptualEntity by table name), and a rename must follow
                // those too. Every "Binding" property anywhere is a candidate; the TryParseBinding shape-gate rejects
                // anything that only shares the name. ToList: RewriteBinding mutates values under the walked tree.
                foreach (var jp in lsdl.Descendants().OfType<JProperty>().Where(p => p.Name == "Binding" && p.Value is JObject).ToList())
                {
                    var b = (JObject)jp.Value;
                    if (!TryParseBinding(b, out var entName, out var prop, out var hier, out var lvl)) continue;
                    if (RewriteBinding(obj, oldName, b, entName, prop, hier, lvl)) changed++;
                }
                if (changed == 0) continue;
                // NON-THROWING per culture: apply_plan catches per-item failures INSIDE its MutateAsync batch, so a
                // throw here would strand the applied TOM rename with a half-cascaded LSDL (and multi-culture models
                // partially cascaded) instead of rolling anything back. A validator rejection means THIS culture's
                // schema was already uncommittable — leave it untouched and surface the honest consequence.
                try
                {
                    var fault = CascadeCommitFault?.Invoke(culture);
                    if (fault != null) throw fault;
                    Commit(culture, lsdl);
                    cultures++;
                }
                catch (Exception ex)
                {
                    // Prescribe the collision remedy ONLY when the failure positively IS the duplicate-key validator
                    // refusal (the raw AS throw, or Commit's rewrap of it); every other failure gets the neutral
                    // consequence — prescribing "fix the name collision" for a collision that doesn't exist is a
                    // dead-end remediation.
                    var collision = ex.Message.Contains("same key has already been added")
                                 || ex.Message.Contains("The linguistic schema requires unique names");
                    // Name the qualified object (table + name), not just the old name: two same-named objects on
                    // different tables produce identical old-name text, and a batch must be able to tell them apart.
                    var who = Describe(obj);
                    var note = collision
                        ? $"Culture '{culture.Name}' rejected the linguistic-schema update after renaming {who} (was '{oldName}'): {ex.Message} " +
                          $"Its linguistic entities still reference '{oldName}' and the next set_synonyms/enable_qna will prune them. Fix the underlying name collision first to keep their synonyms."
                        : $"Culture '{culture.Name}' could not be updated after renaming {who} (was '{oldName}'): {ex.Message} " +
                          $"Its linguistic entities still reference '{oldName}' and may be pruned by the next synonym write.";
                    warning = warning == null ? note : warning + " " + note;
                }
            }
            return cultures;
        }

        /// <summary>Test seam (InternalsVisibleTo): non-null simulates the live AS validator refusing a culture's
        /// recommit inside <see cref="CascadeRename(TabularNamedObject, string, out string)"/> — that throw path is
        /// unreachable offline (Culture.Content only validates against live AS). Return null to commit normally.</summary>
        internal static Func<Culture, Exception> CascadeCommitFault;

        /// <summary>Rewrite the single binding slot that carried the renamed object's OLD name, matched to the
        /// object's kind. A table rename moves the ConceptualEntity anchor on the table's OWN entity AND on every
        /// column/measure/hierarchy/level entity that carries the table name (leaving their property/hierarchy/level
        /// names, which didn't change). A column/measure rewrites ConceptualProperty; a hierarchy rewrites the
        /// Hierarchy slot (which its level entities also carry); a level rewrites HierarchyLevel. Only a positive,
        /// case-insensitive name match on the (unchanged) parent scope touches anything.</summary>
        private static bool RewriteBinding(TabularNamedObject obj, string oldName, JObject b,
            string entName, string prop, string hier, string lvl)
        {
            static bool Eq(string a, string c) => string.Equals(a, c, StringComparison.OrdinalIgnoreCase);
            switch (obj)
            {
                case Table t:
                    if (!Eq(entName, oldName)) return false;
                    b["ConceptualEntity"] = t.Name;
                    return true;

                case Measure _:
                case Column _:
                    var tto = (ITabularTableObject)obj;
                    if (prop == null || !Eq(entName, tto.Table.Name) || !Eq(prop, oldName)) return false;
                    b["ConceptualProperty"] = tto.Name;
                    return true;

                case Hierarchy h:
                    if (hier == null || !Eq(entName, h.Table.Name) || !Eq(hier, oldName)) return false;
                    b["Hierarchy"] = h.Name;
                    return true;

                case Level level:
                    var lh = level.Hierarchy;
                    if (lvl == null || lh == null || !Eq(entName, lh.Table?.Name) || !Eq(hier, lh.Name) || !Eq(lvl, oldName)) return false;
                    b["HierarchyLevel"] = level.Name;
                    return true;
            }
            return false;
        }

        /// <summary>enable_qna's non-fatal advisory: name the collisions among the SURVIVING entities (null when
        /// clean; the fold-tier normaliser, so possible collisions surface too). Advisory, never a throw — keeping
        /// a schema enabled is always safe; the set_synonyms guard and the DAC-QNA-NAME-COLLISIONS readiness rule
        /// carry the enforcement.</summary>
        public static string DescribeCollisions(Model m, Culture culture)
        {
            if (culture == null || culture.ContentType != ContentType.Json || string.IsNullOrEmpty(culture.Content)) return null;
            JObject lsdl;
            try { lsdl = JObject.Parse(culture.Content); } catch { return null; }
            if (!(lsdl["Entities"] is JObject jEntities)) return null;
            var groups = CollisionGroups(m, jEntities, null, QnaTerms.NormalizeFolded);
            if (groups.Count == 0) return null;

            // Split each fold-tier group by the EXACT normalised term: an exact SUBGROUP of 2+ is the hard
            // collision set_synonyms REFUSES; the group's remaining fold-level overlap is only our approximation
            // of the AS lemmatiser, so set_synonyms still WRITES and the commit-time backstop carries the live
            // truth. Splitting per member matters: a group of two 'Cycle Months' plus one 'Cycle Month' must name
            // only the exact pair as refused — the singular member belongs in the warning sentence, not the refusal.
            var exact = new List<(string Term, List<TabularNamedObject> Members)>();
            var foldOnly = new List<(string Term, List<TabularNamedObject> Members)>();
            foreach (var g in groups)
            {
                var subs = g.Members.GroupBy(o => QnaTerms.Normalize(o.Name), StringComparer.Ordinal).ToList();
                foreach (var sub in subs.Where(x => x.Count() > 1))
                    exact.Add((sub.Key, sub.ToList()));
                // 2+ distinct exact terms folding together = a possible fold collision. Name ONE representative per
                // exact term (naming a refused pair twice would re-blur the tiers the split just separated).
                if (subs.Count > 1)
                    foldOnly.Add((g.Term, subs.Select(x => x.First()).ToList()));
            }

            var parts = new List<string>();
            if (exact.Count > 0)
                parts.Add($"{CollisionMessage(exact)} Q&A requires unique names; set_synonyms refuses writes until you rename or hide one object in each group.");
            if (foldOnly.Count > 0)
                parts.Add($"After plural folding these may also collide: {CollisionMessage(foldOnly)} set_synonyms still writes them — the commit-time backstop reports it only if the validator actually rejects the schema.");
            return parts.Count == 0 ? null : string.Join(" ", parts);
        }

        private static void AppendWarning(SetOutcome outcome, string text) =>
            outcome.Warning = outcome.Warning == null ? text : outcome.Warning + " " + text;

        // ---- pruning + collision pre-flight ---------------------------------------------------------------

        /// <summary>Remove every entity whose binding POSITIVELY resolves to a deleted or hidden object. Fail-safe
        /// twice over: a binding we can't read or a shape we don't recognise is KEPT (never delete what you don't
        /// understand), and an entity whose key is referenced anywhere in SemanticSlots/Relationships is KEPT —
        /// we don't parse those sections' schemas, and pruning their target would leave a dangling reference.</summary>
        private static int Prune(Model m, JObject lsdl, JObject jEntities)
        {
            var referenced = ReferencedEntityKeys(lsdl);
            var dead = new List<string>();
            foreach (var e in jEntities.Properties())
            {
                if (referenced.Contains(e.Name)) continue;                          // pinned by a slot/relationship
                if (!(e.Value is JObject jEntity)) continue;
                if (!TryResolve(m, jEntity, out var bound)) continue;               // unreadable: keep
                if (bound == null || IsEffectivelyHidden(bound)) dead.Add(e.Name);  // orphan or hidden: dead weight
            }
            foreach (var k in dead) jEntities.Remove(k);
            return dead.Count;
        }

        /// <summary>Property names under SemanticSlots/Relationships whose VALUE is an enum/metadata token, never an
        /// entity-key reference — excluded so a token like "State":"Generated" doesn't pin an unrelated entity keyed
        /// "Generated". Everything else stays collected: still conservative (over-pin, never orphan a real reference),
        /// just no longer pinned by well-known non-reference slots.</summary>
        private static readonly HashSet<string> NonReferenceProps = new(StringComparer.OrdinalIgnoreCase)
            { "State", "Type", "Weight", "LastModified", "Version", "Language", "DynamicImprovement" };

        /// <summary>Every string value anywhere under SemanticSlots/Relationships, minus the values of well-known
        /// non-reference properties (see <see cref="NonReferenceProps"/>). Any surviving string that names an entity
        /// key pins that entity against pruning — cheaper and safer than modelling those sections' schemas.</summary>
        private static HashSet<string> ReferencedEntityKeys(JObject lsdl)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var section in new[] { "SemanticSlots", "Relationships" })
                if (lsdl[section] is JContainer c)
                    foreach (var v in c.DescendantsAndSelf().OfType<JValue>())
                    {
                        if (v.Type != JTokenType.String || string.IsNullOrEmpty((string)v)) continue;
                        if (v.Parent is JProperty p && NonReferenceProps.Contains(p.Name)) continue;   // enum/metadata token, not a ref
                        keys.Add((string)v);
                    }
            return keys;
        }

        /// <summary>Group the objects bound by the remaining entities (plus the write target, when given) by the
        /// given normalised term; only groups of 2+ distinct objects return. Unresolvable entities can't be named,
        /// so they don't participate — the commit-time backstop still catches whatever they collide with. LEVEL
        /// entities don't participate either: level names duplicate their source column by construction, and
        /// whether level terms share the validator's dictionary is unverified — grouping them would refuse writes
        /// on healthy models, and the backstop covers the live truth.</summary>
        private static List<(string Term, List<TabularNamedObject> Members)> CollisionGroups(
            Model m, JObject jEntities, TabularNamedObject target, Func<string, string> normalize)
        {
            var byTerm = new Dictionary<string, List<TabularNamedObject>>(StringComparer.Ordinal);
            void Add(TabularNamedObject o)
            {
                if (o is Level) return;
                var term = normalize(o.Name);
                if (term.Length == 0) return;
                if (!byTerm.TryGetValue(term, out var list)) byTerm[term] = list = new List<TabularNamedObject>();
                if (!list.Contains(o)) list.Add(o);
            }
            foreach (var e in jEntities.Properties())
                if (e.Value is JObject jEntity && TryResolve(m, jEntity, out var bound) && bound != null)
                    Add(bound);
            if (target != null) Add(target);
            return byTerm.Where(kv => kv.Value.Count > 1).Select(kv => (kv.Key, kv.Value)).ToList();
        }

        private static string CollisionMessage(List<(string Term, List<TabularNamedObject> Members)> groups)
        {
            var shown = groups.Take(5).Select(g => $"'{g.Term}' ({string.Join(", ", g.Members.Select(Describe))})");
            var more = groups.Count > 5 ? $" and {groups.Count - 5} more groups" : "";
            return $"these objects normalise to the same Q&A term: {string.Join("; ", shown)}{more}.";
        }

        // ---- binding resolution ---------------------------------------------------------------------------

        // The donor's binding fallback: Binding lives at the top level or under Definition.
        private static JObject GetBinding(JObject jEntity) =>
            jEntity["Binding"] as JObject ?? jEntity["Definition"]?["Binding"] as JObject;

        /// <summary>Parse a binding into the donor's four recognised slots (ConceptualEntity, optional
        /// ConceptualProperty / Hierarchy / HierarchyLevel). Returns false — the "don't understand it" signal —
        /// for a missing/empty entity name, an unknown extra key, or an illegal combination (property + hierarchy,
        /// or a level without its hierarchy). The single gate that keeps every consumer fail-safe: an entity whose
        /// shape doesn't parse is never resolved, never pruned, never rewritten.</summary>
        private static bool TryParseBinding(JObject b, out string entName, out string prop, out string hier, out string lvl)
        {
            entName = prop = hier = lvl = null;
            if (b == null) return false;
            entName = (b["ConceptualEntity"] as JValue)?.ToString();
            if (string.IsNullOrEmpty(entName)) return false;
            prop = (b["ConceptualProperty"] as JValue)?.ToString();
            hier = (b["Hierarchy"] as JValue)?.ToString();
            lvl = (b["HierarchyLevel"] as JValue)?.ToString();
            // Only the donor's four shapes are recognised; extra/unknown keys mean we don't understand the entity.
            var known = 1 + (prop != null ? 1 : 0) + (hier != null ? 1 : 0) + (lvl != null ? 1 : 0);
            if (b.Count != known) return false;
            if (prop != null && (hier != null || lvl != null)) return false;
            if (lvl != null && hier == null) return false;
            return true;
        }

        /// <summary>Resolve an entity's binding to the live object it names. Three outcomes: resolved
        /// (<paramref name="bound"/> set), POSITIVELY dead (returns true, bound null — the table/property/
        /// hierarchy/level was looked up and is not there), or unreadable (returns false — a shape with keys we
        /// don't recognise is never treated as dead).</summary>
        private static bool TryResolve(Model m, JObject jEntity, out TabularNamedObject bound)
        {
            bound = null;
            if (!TryParseBinding(GetBinding(jEntity), out var entName, out var prop, out var hier, out var lvl)) return false;

            var table = m.Tables.Contains(entName) ? m.Tables[entName] : null;
            if (table == null) return true;   // positively dead: the bound table is gone
            if (prop != null)
            {
                bound = table.Columns.Contains(prop) ? (TabularNamedObject)table.Columns[prop]
                      : table.Measures.Contains(prop) ? table.Measures[prop] : null;
                return true;                  // null = the bound column/measure is gone
            }
            if (hier != null)
            {
                var h = table.Hierarchies.Contains(hier) ? table.Hierarchies[hier] : null;
                if (h == null) return true;
                if (lvl == null) { bound = h; return true; }
                // TOM name lookups (Contains/indexer above) are case-insensitive — AS names are. Levels have no
                // TOM-backed lookup here, so match that explicitly: a case-changed level must not read as dead.
                bound = h.Levels.FirstOrDefault(l => string.Equals(l.Name, lvl, StringComparison.OrdinalIgnoreCase));
                return true;
            }
            bound = table;
            return true;
        }

        /// <summary>Hidden as Q&amp;A sees it: the object itself, or the table carrying it.</summary>
        private static bool IsEffectivelyHidden(TabularNamedObject obj) => obj switch
        {
            Table t => t.IsHidden,
            Column c => c.IsHidden || (c.Table?.IsHidden ?? false),
            Measure me => me.IsHidden || (me.Table?.IsHidden ?? false),
            Hierarchy h => h.IsHidden || (h.Table?.IsHidden ?? false),
            Level l => l.Hierarchy == null || l.Hierarchy.IsHidden || (l.Hierarchy.Table?.IsHidden ?? false),
            _ => false,
        };

        private static string Describe(TabularNamedObject obj) => obj switch
        {
            Table t => $"table '{t.Name}'",
            Column c => $"column '{c.Table?.Name}'[{c.Name}]",
            Measure me => $"measure '{me.Table?.Name}'[{me.Name}]",
            Hierarchy h => $"hierarchy '{h.Table?.Name}'[{h.Name}]",
            Level l => $"level '{l.Hierarchy?.Table?.Name}'[{l.Hierarchy?.Name}].[{l.Name}]",
            _ => $"'{obj.Name}'",
        };

        // ---- entity find/create + terms (donor port) --------------------------------------------------------

        private static JObject FindEntity(TabularNamedObject obj, JObject jEntities)
        {
            var m = obj.Model;
            foreach (var e in jEntities.Properties())
                if (e.Value is JObject jEntity && TryResolve(m, jEntity, out var bound) && ReferenceEquals(bound, obj))
                    return jEntity;
            return null;
        }

        private static JObject CreateEntity(TabularNamedObject obj, JObject jEntities)
        {
            JObject entity = null; string key = null;
            switch (obj)
            {
                case Table table:
                    entity = JObject.FromObject(new { Binding = new { ConceptualEntity = table.Name }, State = "Generated", Terms = new object[0] });
                    key = CleanName(table.Name);
                    break;

                case Measure _:
                case Column _:
                    var tto = (ITabularTableObject)obj;
                    entity = JObject.FromObject(new { Binding = new { ConceptualEntity = tto.Table.Name, ConceptualProperty = tto.Name }, State = "Generated", Terms = new object[0] });
                    key = $"{CleanName(tto.Table.Name)}.{CleanName(tto.Name)}";
                    break;

                case Hierarchy hierarchy:
                    entity = JObject.FromObject(new { Binding = new { ConceptualEntity = hierarchy.Table.Name, Hierarchy = hierarchy.Name }, State = "Generated", Terms = new object[0] });
                    key = $"{CleanName(hierarchy.Table.Name)}.{CleanName(hierarchy.Name)}";
                    break;

                case Level level:
                    var h = level.Hierarchy;
                    entity = JObject.FromObject(new { Binding = new { ConceptualEntity = h.Table.Name, Hierarchy = h.Name, HierarchyLevel = level.Name }, State = "Generated", Terms = new object[0] });
                    key = $"{CleanName(h.Table.Name)}.{CleanName(h.Name)}.{CleanName(level.Name)}";
                    break;
            }

            if (entity != null)
            {
                // The slug key is only a label — the Binding is the identity (the same convention as the
                // AI-data-schema writer's UniqueEntityKey). The donor blind-Added and threw Newtonsoft-side on a
                // CleanName collision ([AB] vs [A,B] both clean to "AB"); overwriting instead would destroy an
                // unrelated authored entity, so suffix until the slug is free.
                var baseKey = key;
                for (var i = 1; jEntities.ContainsKey(key); i++) key = $"{baseKey}_{i}";
                jEntities.Add(key, entity);
            }
            return entity;
        }

        private static string CleanName(string name) =>
            name.Replace("\"", "").Replace("\\", "").Replace(",", "").Replace(".", "_").Replace(" ", "_");

        private static void SetTerms(JObject jEntity, string synonyms)
        {
            if (!(jEntity["Terms"] is JArray jTerms)) jEntity["Terms"] = jTerms = new JArray();
            var existingTerms = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var jTerm in jTerms.OfType<JObject>())
                foreach (var kvp in jTerm)
                {
                    if (!(kvp.Value is JObject jTermProp)) continue;
                    var termProp = jTermProp.ToObject<TermProperties>();
                    if (termProp.State != State.Deleted && termProp.State != State.Suggested)
                        existingTerms[kvp.Key] = jTermProp;   // index (donor used Add): a hand-edited duplicate term must not throw
                }

            var newTerms = new HashSet<string>(synonyms.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()), StringComparer.OrdinalIgnoreCase);

            foreach (var newTerm in newTerms.Except(existingTerms.Keys))
                jTerms.Add(new JObject { [newTerm] = JObject.FromObject(new { LastModified = DateTime.Now }) });

            foreach (var existingTerm in existingTerms.Keys.Except(newTerms))
                existingTerms[existingTerm]["State"] = "Deleted";
        }

        // ---- commit ----------------------------------------------------------------------------------------

        /// <summary>Assign the edited LSDL back to the culture. Deliberately NOT undo-suspended (the donor
        /// suspended here): Culture.Content is a tracked wrapper property, and recording its change is exactly
        /// what makes the surrounding MutateAsync batch undoable AND what lets a dry_run rehearsal roll the edit
        /// back — a suspended write survives both (UndoManager.Add drops actions while suspended; EndBatch's
        /// rollback replays only recorded actions). Matches the in-repo precedent: ApplyAiInstructions writes
        /// Culture.Content tracked too. The catch is the BACKSTOP: QnaTerms approximates the closed AS lemmatiser,
        /// so a collision it can't predict must still surface as an actionable error rather than the opaque
        /// dictionary throw.</summary>
        private static void Commit(Culture culture, JObject lsdl)
        {
            try { culture.Content = lsdl.ToString(Formatting.None); }
            catch (Exception ex) when (ex.Message.Contains("same key has already been added"))
            {
                var key = ExtractKey(ex.Message);
                throw new InvalidOperationException(
                    $"The linguistic schema requires unique names: two objects normalise to the Q&A term {(key == null ? "reported by the validator" : $"'{key}'")}, which the pre-flight normalisation could not predict. Find the two objects whose names read as that term, then rename or hide one of them; hiding also requires the dead linguistic entity to be pruned, which set_synonyms does automatically on the next call.", ex);
            }
        }

        private static string ExtractKey(string message)
        {
            var i = message.LastIndexOf("Key:", StringComparison.Ordinal);
            if (i < 0) return null;
            var key = message.Substring(i + 4).Trim().TrimEnd('.');
            return key.Length == 0 ? null : key;
        }
    }
}
