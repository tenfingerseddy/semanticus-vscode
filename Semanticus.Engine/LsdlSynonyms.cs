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
    /// Semanticus-owned replacement for the donor SynonymHelper.SetSynonyms (reads stay on the donor). WHY: on
    /// 15 Jul every synonym write on a live model failed with "An item with the same key has already been added",
    /// and the culprits were entities Power BI had generated for HIDDEN shadow columns duplicating visible names.
    /// Copilot ignores hidden objects, so those entities are pure risk: this writer refuses hidden targets, prunes
    /// entities bound to hidden or deleted objects (self-heal), and backstops the validator's throw with a remedy.
    /// It no longer refuses a write because two VISIBLE objects read alike: nothing recorded shows the validator
    /// refusing that, and Power BI Desktop saves a table beside its own label column, 'Date'[Date], a key on two
    /// tables and 'Sales' beside '# Sales' in Microsoft's own samples (Kane's ruling, 27 Sep). Two visible fields
    /// that read as one word are a Copilot ambiguity, which DAC-QNA-NAME-COLLISIONS reports; the write goes through.
    /// </summary>
    public static class LsdlSynonyms
    {
        /// <summary>Parse a linguistic schema so that writing it back changes only what an edit changed. JObject.Parse
        /// turns every date-shaped string into a DateTime and writes it back in its own format, so Power BI Desktop's
        /// seven-digit stamps ("2020-07-13T13:47:21.8380000Z") came back trimmed (".838Z") in every entity on any
        /// write. Dates stay the strings they were.</summary>
        private static JObject ParseLsdl(string content)
        {
            using var reader = new JsonTextReader(new System.IO.StringReader(content)) { DateParseHandling = DateParseHandling.None };
            var root = JObject.Load(reader);
            if (reader.Read() && reader.TokenType != JsonToken.Comment)
                throw new JsonReaderException("Additional text found in the linguistic schema after its closing brace.");
            return root;
        }

        public static SetOutcome SetSynonyms(TabularNamedObject obj, Culture culture, string synonyms) =>
            SetSynonyms(obj, culture, synonyms, null);

        /// <summary><see cref="SetSynonyms(TabularNamedObject, Culture, string)"/>, where <paramref name="removed"/> names
        /// the words the caller took out of a list it had read (see <see cref="SetTerms"/>).</summary>
        public static SetOutcome SetSynonyms(TabularNamedObject obj, Culture culture, string synonyms, IEnumerable<string> removed)
        {
            var (lsdl, outcome, _) = Compose(obj, culture, synonyms, removed);
            Commit(culture, lsdl);
            return outcome;
        }

        /// <summary>The object's live terms, in schema order, as a write of <paramref name="synonyms"/> would leave them,
        /// read the way find and replace reads them (every live term). Nothing is written. Null when the write would be
        /// refused.</summary>
        public static string PreviewSynonyms(TabularNamedObject obj, Culture culture, string synonyms, IEnumerable<string> removed) =>
            PreviewSynonyms(obj, culture, synonyms, removed, out _);

        /// <summary><see cref="PreviewSynonyms(TabularNamedObject, Culture, string, IEnumerable{string})"/>, with the reason
        /// the write would be refused (null when it would not be).</summary>
        public static string PreviewSynonyms(TabularNamedObject obj, Culture culture, string synonyms, IEnumerable<string> removed, out string refusal)
        {
            refusal = null;
            try
            {
                var (_, _, jEntity) = Compose(obj, culture, synonyms, removed);
                return string.Join(", ", (jEntity["Terms"] as JArray ?? new JArray()).OfType<JObject>().SelectMany(o => o.Properties())
                    .Where(p => p.Value is JObject && IsLive(p.Value)).Select(p => p.Name));
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is JsonException) { refusal = ex.Message; return null; }
        }

        /// <summary>One object's words for <see cref="SetSynonymsMany"/>: the list <see cref="SetSynonyms(TabularNamedObject, Culture, string, IEnumerable{string})"/>
        /// takes, and the words the caller took out of a list it had read (null for none).</summary>
        public sealed class SynonymWrite
        {
            public TabularNamedObject Object;
            public string Synonyms;
            public IEnumerable<string> Removed;
        }

        /// <summary>What <see cref="SetSynonymsMany"/> did: how many items were written, the reason for each item it did
        /// not write (by the item's index), and the prune count and advisory a single write reports.</summary>
        public sealed class SetManyOutcome
        {
            public int Written;
            public int PrunedEntities;
            public string Warning;   // null on the clean path
            public Dictionary<int, string> Refused = new Dictionary<int, string>();
            /// <summary>The items whose entity ended up different from how it began (a new entity counts). When the call pruned dead
            /// entities and no item changed, the first written item stands for the prune.</summary>
            public HashSet<int> Changed = new HashSet<int>();
        }

        /// <summary>
        /// Write many objects' synonyms into one culture with ONE parse and ONE serialise of the linguistic schema. This is
        /// the call for a bulk writer (write_words, any "many objects in one commit" path). WHY: each
        /// <see cref="SetSynonyms(TabularNamedObject, Culture, string)"/> parses, prunes and rewrites the whole schema and
        /// sets Culture.Content once, so looping it over 40 objects in one commit cost 40 parses, 40 serialises and 40
        /// full copies of the schema on the undo stack. Here the schema is read once, every item is applied to the same
        /// document in order, and Culture.Content is set once: one undo action, one copy of the schema, one rescan.
        ///
        /// Each item is written exactly as a single write would write it (same entity lookup, same new-entity shape, same
        /// term rules). Two things are read once for the whole call rather than per item: the prune of dead entities (an
        /// item cannot make a new dead one) and the schema's style (key case, binding shape, stamps), which a single write
        /// reads before its own entity is made, so here no entity made by this call votes on the style of the next one.
        /// An item a single write would refuse (a hidden object, or one that cannot own terms) is not written, and its
        /// reason is in <see cref="SetManyOutcome.Refused"/>; the other items still are. Nothing is written when no item
        /// is. Must run inside the caller's MutateAsync, like every writer here: the one Content change joins its undo
        /// batch. Throws, writing nothing, when the culture has no JSON schema or the schema cannot be read; a refusal by
        /// the validator at the one write throws the same actionable error a single write does.
        /// </summary>
        public static SetManyOutcome SetSynonymsMany(Culture culture, IReadOnlyList<SynonymWrite> items)
        {
            var result = new SetManyOutcome();
            if (items == null || items.Count == 0) return result;
            RequireSchema(culture);
            var draft = Open(culture.Model, culture);
            draft.Owned = OwnedEntities(culture.Model, draft.Entities);
            result.PrunedEntities = draft.Outcome.PrunedEntities;
            result.Warning = draft.Outcome.Warning;
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item?.Object == null) { result.Refused[i] = "set_synonyms: the item names no object."; continue; }
                try
                {
                    draft.Owned.TryGetValue(item.Object, out var existing);
                    var before = existing?.DeepClone();
                    var entity = Apply(draft, item.Object, item.Synonyms, item.Removed);
                    result.Written++;
                    if (before == null || !JToken.DeepEquals(before, entity)) result.Changed.Add(i);
                }
                catch (InvalidOperationException ex) { result.Refused[i] = ex.Message; }
            }
            if (result.Written > 0 && result.Changed.Count == 0 && result.PrunedEntities > 0)
                result.Changed.Add(Enumerable.Range(0, items.Count).First(i => !result.Refused.ContainsKey(i)));
            if (result.Written > 0) Commit(culture, draft.Lsdl);
            return result;
        }

        /// <summary>A parsed schema being edited: the document, its Entities, the style new terms and entities follow,
        /// and (for many writes) which entity each object owns, found once rather than by a walk per item.</summary>
        private sealed class Draft
        {
            public JObject Lsdl, Entities;
            public SetOutcome Outcome;
            public EntityStyle Style;
            public Dictionary<TabularNamedObject, JObject> Owned;
        }

        private static void RequireSchema(Culture culture)
        {
            if (culture == null || culture.ContentType != ContentType.Json || string.IsNullOrEmpty(culture.Content))
                throw new InvalidOperationException($"Can't set synonyms: culture '{culture?.Name}' has no linguistic schema. Run create_linguistic_schema first.");
        }

        /// <summary>The refusal a single write would give for this object, before anything is written: hidden, or not a
        /// table, column, measure, hierarchy or level. A bulk writer calls it per item so a refused item is rolled back
        /// whole, then writes the others with <see cref="SetSynonymsMany"/>. Throws InvalidOperationException.</summary>
        public static void RefuseIfUnwritable(TabularNamedObject obj)
        {
            RefuseHidden(obj);
            if (!(obj is Table || obj is Measure || obj is Column || obj is Hierarchy || obj is Level))
                throw new InvalidOperationException(NoTermsMessage(obj));
        }

        private static string NoTermsMessage(TabularNamedObject obj) =>
            $"set_synonyms: {Describe(obj)} is not a table/column/measure/hierarchy/level, so it can't own linguistic terms.";

        private static void RefuseHidden(TabularNamedObject obj)
        {
            if (IsEffectivelyHidden(obj))
                throw new InvalidOperationException(
                    $"set_synonyms: {Describe(obj)} is hidden. Copilot ignores hidden objects, so synonyms on it are dead weight and a collision risk in the linguistic schema. Unhide the object, or target the visible duplicate instead.");
        }

        private static (JObject Lsdl, SetOutcome Outcome, JObject Entity) Compose(TabularNamedObject obj, Culture culture, string synonyms, IEnumerable<string> removed)
        {
            RequireSchema(culture);
            RefuseHidden(obj);
            var draft = Open(obj.Model, culture);
            var jEntity = Apply(draft, obj, synonyms, removed);
            return (draft.Lsdl, draft.Outcome, jEntity);
        }

        /// <summary>Parse the culture's schema once, prune its dead entities, and read the style new words follow.</summary>
        private static Draft Open(Model model, Culture culture)
        {
            var lsdl = ParseLsdl(culture.Content);
            if (!(lsdl["Entities"] is JObject jEntities)) lsdl["Entities"] = jEntities = new JObject();

            var desktopStamps = SchemaStamps(jEntities);   // before the prune: the scan reads the whole schema
            var outcome = new SetOutcome { PrunedEntities = Prune(model, lsdl, jEntities) };
            if (outcome.PrunedEntities > 0)
                outcome.Warning = PruneNote(outcome.PrunedEntities);

            var style = EntityStyle.Read(model, jEntities);   // read BEFORE a create, so the new entity does not vote
            style.DesktopStamps = desktopStamps;
            // The words this write makes the author's follow the schema's stamp reading and never move it: stamped where
            // it reads as stamped (there a stateless unstamped word is Power BI's, and an unstamped "Authored" outvotes a
            // stamp), even when the only stamps left sit on words since deleted; unstamped where it does not.
            if (desktopStamps) style.AuthoredTerm["LastModified"] = null;   // placeholder: stamped per write
            else style.AuthoredTerm.Remove("LastModified");
            return new Draft { Lsdl = lsdl, Entities = jEntities, Outcome = outcome, Style = style };
        }

        /// <summary>Write one object's terms into the draft: its entity (made in the schema's style when it has none) and
        /// the terms <see cref="SetTerms"/> leaves. Throws InvalidOperationException, touching nothing, for an object a
        /// write refuses.</summary>
        private static JObject Apply(Draft draft, TabularNamedObject obj, string synonyms, IEnumerable<string> removed)
        {
            RefuseHidden(obj);
            JObject jEntity;
            if (draft.Owned == null) jEntity = FindEntity(obj, draft.Entities);
            else draft.Owned.TryGetValue(obj, out jEntity);
            if (jEntity == null)
            {
                jEntity = CreateEntity(obj, draft.Entities, draft.Style);
                if (jEntity == null)
                    throw new InvalidOperationException(NoTermsMessage(obj));
                draft.Owned?.Add(obj, jEntity);
            }
            SetTerms(jEntity, obj, synonyms ?? "", draft.Style,
                removed == null ? null : new HashSet<string>(removed, StringComparer.OrdinalIgnoreCase));
            return jEntity;
        }

        /// <summary>create_linguistic_schema's self-heal: prune dead entities from an EXISTING schema, reassigning Content only
        /// when something was actually pruned. Returns the prune count (0 for an unreadable/absent schema).</summary>
        public static int PruneDeadEntities(Model m, Culture culture)
        {
            if (culture == null || culture.ContentType != ContentType.Json || string.IsNullOrEmpty(culture.Content)) return 0;
            JObject lsdl;
            try { lsdl = ParseLsdl(culture.Content); } catch { return 0; }   // unreadable schema: leave it alone
            if (!(lsdl["Entities"] is JObject jEntities)) return 0;
            var pruned = Prune(m, lsdl, jEntities);
            if (pruned > 0) Commit(culture, lsdl);
            return pruned;
        }

        /// <summary>The one-line advisory both writers surface after a prune.</summary>
        public static string PruneNote(int pruned) =>
            $"Pruned {pruned} linguistic entities bound to hidden or deleted objects; Copilot ignores hidden objects.";

        // ---- rename cascade --------------------------------------------------------------------------------

        /// <summary>After an object rename, follow every JSON culture's linguistic bindings that named the object's
        /// OLD identity onto its new name — otherwise the entity ORPHANS (its Binding names a gone object) and the
        /// next set_synonyms/create_linguistic_schema prunes it, silently deleting the authored synonyms one write later. Must be
        /// called AFTER the TOM rename and INSIDE the caller's MutateAsync: it writes Culture.Content through the
        /// tracked setter (see Commit), so the rewrite joins the rename's undo batch and a dry_run rolls both back
        /// together. Fail-safe like Prune — only bindings whose shape TryParseBinding recognises and whose name(s)
        /// match the old identity are touched; anything unreadable is left byte-for-byte as-is. Entity KEYS (slug
        /// labels) are deliberately left stale: the Binding is the identity (FindEntity/TryResolve match on it), and
        /// re-keying could collide with an unrelated authored entity — which is also why entity-key REFERENCES under
        /// SemanticSlots/Relationships need no rewrite. The entity's Generated name term is NOT left stale: it is what
        /// Copilot matches the object by, so it follows the rename (see RegenerateNameTerm). NON-THROWING: a culture the validator refuses is left
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
                try { lsdl = ParseLsdl(culture.Content); } catch { continue; }   // unreadable schema: leave it alone

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
                // The renamed object's OWN entity also carries its old name as the Generated name term. Left alone,
                // the collision a rename was meant to fix survives in the schema Copilot reads.
                if (lsdl["Entities"] is JObject jEntities)
                {
                    var desktopStamps = SchemaStamps(jEntities);
                    foreach (var e in jEntities.Properties().Select(p => p.Value).OfType<JObject>())
                        if (TryResolve(model, e, out var bound) && ReferenceEquals(bound, obj) && RegenerateNameTerm(e, oldName, obj.Name, desktopStamps))
                            changed++;
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
                          $"Its linguistic entities still reference '{oldName}' and the next set_synonyms/create_linguistic_schema will prune them. Fix the underlying name collision first to keep their synonyms."
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

        /// <summary>Move the entity's NAME terms from the old name onto the new one, in place (same array slot, same
        /// Type/Weight), so the entity keeps Power BI's shape. A name term is a generated term that spells the old name
        /// the way Power BI writes it (see <see cref="IsNameTerm"/>): "customer" for 'Customers', "total child" for
        /// 'Total Children', "customer id" and "CustomerId" for 'CustomerId', "margin %" for 'Margin %'. Each becomes the new name in the form it had: the name as written
        /// when the old term was the name as written (and not also its lower form), Power BI's lower-case form
        /// (<see cref="QnaTerms.NameTerm"/>) otherwise. When two old forms land on the same word (a camelCase name
        /// renamed to a spaced one), the first keeps its slot and the later one goes, so no word is written twice. The
        /// person's own terms are kept, with one exception: a term that already spells the NEW word (say they had
        /// authored "calendar date" and then renamed the column to Calendar Date) is folded into the name term, so
        /// the word stays once. A word Power BI derived from the OLD name and not from the new one ("PO number" when
        /// 'CustomerPONumber' becomes 'Order Reference', see <see cref="QnaTerms.IsDerivedFromName"/>) goes too, as it
        /// would when Desktop regenerates the entity; once the name term has moved nothing records the old name, so no
        /// later write could tell it from Desktop's own words. Power BI's other words (a derived word the new name shares,
        /// "date" on a date column) are left alone.</summary>
        private static bool RegenerateNameTerm(JObject jEntity, string oldName, string newName, bool desktopStamps)
        {
            if (!(jEntity["Terms"] is JArray jTerms)) return false;
            var all = jTerms.OfType<JObject>().SelectMany(o => o.Properties()).ToList();
            var names = all.Where(p => IsNameTerm(p, oldName)).ToList();
            if (names.Count == 0) return false;

            var changed = false;
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in names)
            {
                var asWritten = string.Equals(p.Name, oldName, StringComparison.Ordinal)
                                && !string.Equals(p.Name, QnaTerms.NameTerm(oldName), StringComparison.Ordinal);
                var newKey = asWritten ? newName : QnaTerms.NameTerm(newName);
                if (!written.Add(newKey)) { RemoveTerm(p); changed = true; continue; }   // a second old form of the same new word
                if (string.Equals(p.Name, newKey, StringComparison.Ordinal)) continue;
                p.Replace(new JProperty(newKey, p.Value));
                changed = true;
            }
            // Fold any other term that already spells a new name word (live or tombstoned) into the name term.
            foreach (var twin in all.Where(q => q.Parent != null && !names.Contains(q) && written.Contains(q.Name)).ToList())
            {
                RemoveTerm(twin);
                changed = true;
            }
            // Power BI's words from the old name that the new name does not share.
            foreach (var old in all.Where(q => q.Parent != null && !names.Contains(q) && q.Value is JObject
                                               && QnaTerms.IsPowerBiWord(TermState(q.Value), Stamped(q.Value), desktopStamps)
                                               && QnaTerms.IsDerivedFromName(q.Name, oldName) && !QnaTerms.IsDerivedFromName(q.Name, newName)).ToList())
            {
                RemoveTerm(old);
                changed = true;
            }
            return changed;
        }

        private static void RemoveTerm(JProperty term)
        {
            var holder = term.Parent;
            term.Remove();
            if (holder is JObject h && !h.HasValues) h.Remove();
        }

        /// <summary>A term's State as <see cref="QnaTerms.CountsAsNameTerm"/> reads it: null when it has none, the string
        /// when a string, "?" for anything else (or a term that is not an object). The readiness rules read terms the
        /// same way (ReadinessRules.TermState).</summary>
        private static string TermState(JToken termProps)
        {
            if (!(termProps is JObject props)) return "?";
            var state = props["State"];
            return state == null || state.Type == JTokenType.Null ? null : state.Type == JTokenType.String ? (string)state : "?";
        }

        private static bool Stamped(JToken termProps) => termProps is JObject props && props["LastModified"] != null;

        /// <summary>The object's own name term: generated (State Generated, or Desktop's stateless unstamped copy) and
        /// spelling its name (see <see cref="QnaTerms.CountsAsNameTerm"/>, the one definition the scan also uses).</summary>
        private static bool IsNameTerm(JProperty term, string name) =>
            QnaTerms.CountsAsNameTerm(term.Name, TermState(term.Value), Stamped(term.Value), name);

        /// <summary>The entity's stale name term, or null (see <see cref="QnaTerms.StaleNameTerm"/>, shared with the scan).</summary>
        private static string StaleNameTerm(IEnumerable<JProperty> terms, string name, bool desktopStamps) =>
            QnaTerms.StaleNameTerm(terms.Select(p => (p.Name, TermState(p.Value), Stamped(p.Value), WeightOf(p.Value))), name, desktopStamps);

        /// <summary>Do this schema's authored words carry Desktop's stamps? Read over every term of every entity, the way
        /// the scan reads it (see <see cref="QnaTerms.AuthoredWordsStamped"/>).</summary>
        private static bool SchemaStamps(JObject jEntities) =>
            QnaTerms.AuthoredWordsStamped(jEntities.Properties().Select(p => p.Value).OfType<JObject>()
                .SelectMany(e => (e["Terms"] as JArray ?? new JArray()).OfType<JObject>().SelectMany(o => o.Properties()))
                .Select(t => (TermState(t.Value), Stamped(t.Value))).ToList());

        // Weight when it is a number, else Power BI's default 1.0 (a hand edit can leave a string there).
        private static double WeightOf(JToken termProps) =>
            termProps is JObject props && (props["Weight"]?.Type == JTokenType.Float || props["Weight"]?.Type == JTokenType.Integer) ? (double)props["Weight"] : 1.0;

        private static bool IsLive(JToken termProps)
        {
            var state = (string)(termProps as JObject)?["State"];
            return !string.Equals(state, "Deleted", StringComparison.OrdinalIgnoreCase) && !string.Equals(state, "Suggested", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>create_linguistic_schema's non-fatal advisory: the fields Copilot cannot tell apart by name (null
        /// when there are none). The same reading as DAC-QNA-NAME-COLLISIONS (ReadinessRuleSet.FieldNameClashes), so the
        /// advisory and the scan never disagree. Advisory, never a throw: no synonym write is refused for it.</summary>
        public static string DescribeCollisions(Model m, Culture culture)
        {
            if (culture == null || culture.ContentType != ContentType.Json || string.IsNullOrEmpty(culture.Content)) return null;
            var clashes = ReadinessRuleSet.FieldNameClashes(m);
            if (clashes.Count == 0) return null;
            var shown = clashes.Take(5).Select(c => $"'{c.Key}' ({string.Join(", ", c.Members.Select(x => x.Label))})");
            var more = clashes.Count > 5 ? $" and {clashes.Count - 5} more" : "";
            // A clash that exists only through an old name term is fixed by clearing the old word, not by a rename.
            var byName = clashes.Any(c => !c.OnlyThroughOldTerm);
            var byOldTerm = clashes.Any(c => c.Members.Any(x => x.ViaTerm));
            return $"These visible fields read as the same word to Copilot: {string.Join("; ", shown)}{more}. Synonym writes still work."
                + (byName ? " Rename or hide one of each, or describe each so Copilot knows which to use." : "")
                + (byOldTerm ? " Where a field meets another through its old name term, setting that field's synonyms without the old word clears it." : "");
        }

        // ---- pruning ---------------------------------------------------------------------------------------

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

        /// <summary>Hidden as Copilot sees it: the object itself, or the table carrying it.</summary>
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

        /// <summary>The entity each object owns, as <see cref="FindEntity"/> finds it (the first in the document whose
        /// binding resolves to the object), for every object at once.</summary>
        private static Dictionary<TabularNamedObject, JObject> OwnedEntities(Model m, JObject jEntities)
        {
            var owned = new Dictionary<TabularNamedObject, JObject>(ReferenceEqualityComparer.Instance);
            foreach (var e in jEntities.Properties())
                if (e.Value is JObject jEntity && TryResolve(m, jEntity, out var bound) && bound != null)
                    owned.TryAdd(bound, jEntity);
            return owned;
        }

        private static JObject FindEntity(TabularNamedObject obj, JObject jEntities)
        {
            var m = obj.Model;
            foreach (var e in jEntities.Properties())
                if (e.Value is JObject jEntity && TryResolve(m, jEntity, out var bound) && ReferenceEquals(bound, obj))
                    return jEntity;
            return null;
        }

        /// <summary>A new entity, written the way the model's OTHER entities are written, so a synonym write never
        /// leaves one odd entity behind (the donor always wrote a top-level Binding, no name term and a case-kept key,
        /// whatever the model looked like). Everything is read from the siblings (see <see cref="EntityStyle"/>);
        /// with no siblings to copy, Power BI's own shape: Definition.Binding, State Generated, a lower-case key and a
        /// Generated name term in Power BI's lower-case form. (Power BI also writes entities with no terms at all; one
        /// written here always carries its name term, since a synonym write is a statement about that object.)</summary>
        private static JObject CreateEntity(TabularNamedObject obj, JObject jEntities, EntityStyle style)
        {
            JObject binding; string[] parts;
            switch (obj)
            {
                case Table table:
                    binding = new JObject { ["ConceptualEntity"] = table.Name };
                    parts = new[] { table.Name };
                    break;

                case Measure _:
                case Column _:
                    var tto = (ITabularTableObject)obj;
                    binding = new JObject { ["ConceptualEntity"] = tto.Table.Name, ["ConceptualProperty"] = tto.Name };
                    parts = new[] { tto.Table.Name, tto.Name };
                    break;

                case Hierarchy hierarchy:
                    binding = new JObject { ["ConceptualEntity"] = hierarchy.Table.Name, ["Hierarchy"] = hierarchy.Name };
                    parts = new[] { hierarchy.Table.Name, hierarchy.Name };
                    break;

                case Level level:
                    var h = level.Hierarchy;
                    binding = new JObject { ["ConceptualEntity"] = h.Table.Name, ["Hierarchy"] = h.Name, ["HierarchyLevel"] = level.Name };
                    parts = new[] { h.Table.Name, h.Name, level.Name };
                    break;

                default:
                    return null;
            }

            var entity = style.Wrapped ? new JObject { ["Definition"] = new JObject { ["Binding"] = binding } } : new JObject { ["Binding"] = binding };
            if (style.EntityState) entity["State"] = "Generated";
            var nameTerm = (JObject)style.GeneratedTerm.DeepClone();
            nameTerm["State"] = "Generated";
            entity["Terms"] = new JArray(new JObject { [style.TermCase(obj.Name)] = nameTerm });

            // The slug key is only a label — the Binding is the identity (the same convention as the
            // AI-data-schema writer's UniqueEntityKey). The donor blind-Added and threw Newtonsoft-side on a
            // CleanName collision ([AB] vs [A,B] both clean to "AB"); overwriting instead would destroy an
            // unrelated authored entity, so suffix until the slug is free. A child goes under its table entity's
            // OWN key when the table has one, which is how Power BI keys it: 'Customers' is keyed "customer" and
            // every child "customer.<child>", a prefix no slug of the table name reproduces.
            var tableKey = parts.Length > 1 ? TableEntityKey(obj.Model, jEntities, parts[0]) : null;
            var baseKey = tableKey != null
                ? tableKey + "." + string.Join(".", parts.Skip(1).Select(style.Slug))
                : string.Join(".", parts.Select(style.Slug));
            var key = baseKey;
            for (var i = 1; jEntities.ContainsKey(key); i++) key = $"{baseKey}_{i}";
            jEntities.Add(key, entity);
            return entity;
        }

        private static string CleanName(string name) =>
            name.Replace("\"", "").Replace("\\", "").Replace(",", "").Replace(".", "_").Replace(" ", "_");

        /// <summary>The key of the entity bound to the table itself (table binding only), or null when it has none.</summary>
        private static string TableEntityKey(Model m, JObject jEntities, string tableName)
        {
            foreach (var e in jEntities.Properties())
                if (e.Value is JObject jEntity && TryParseBinding(GetBinding(jEntity), out var entName, out var prop, out var hier, out _)
                    && prop == null && hier == null && string.Equals(entName, tableName, StringComparison.OrdinalIgnoreCase)
                    && TryResolve(m, jEntity, out var bound) && bound is Table)
                    return e.Name;
            return null;
        }

        /// <summary>How the model's existing entities are written, so a new entity or term matches them. Each trait is
        /// a majority vote over the entities that parse (ties and an empty model fall to Power BI's shape). Key style
        /// is decided by replaying each candidate slugger against the siblings' own bindings and keeping the one that
        /// reproduces the most keys, which is how 'Margin %' ends up keyed 'margin_percent' in a model that does that
        /// and 'Margin_%' in one that keeps case. A child's key is replayed under its table entity's own key, the
        /// prefix a new child gets (see CreateEntity), so only the slug of the child's own name is voted on.</summary>
        private sealed class EntityStyle
        {
            public bool Wrapped = true;          // Definition.Binding (Power BI) rather than a top-level Binding (the donor)
            public bool EntityState = true;      // an entity-level "State":"Generated"
            public bool LowerTerms = true;       // the name term is Power BI's lower-case form rather than the name as written
            public JObject GeneratedTerm = new JObject { ["State"] = "Generated" };
            public JObject AuthoredTerm = new JObject { ["State"] = "Authored" };
            public Func<string, string> Slug = LowerSlug;
            /// <summary>The schema's authored words carry Power BI Desktop's LastModified stamp, so a stateless UNSTAMPED
            /// word is Power BI's, not the author's (see <see cref="QnaTerms.IsPowerBiWord"/>).</summary>
            public bool DesktopStamps;

            /// <summary>The name term for a name: Power BI's lower-case form with camelCase split ("product name" for
            /// 'ProductName', the form <see cref="QnaTerms.ReadsAsName"/> reads back as the name), or the name as written
            /// in a model whose name terms keep case.</summary>
            public string TermCase(string name) => LowerTerms ? QnaTerms.NameTerm(name) : name;

            private static string LowerSlug(string n) => CleanName(n).ToLowerInvariant();
            private static string LowerPercentSlug(string n) => CleanName(n).ToLowerInvariant().Replace("%", "percent");
            // Power BI Desktop's own: camelCase split, words joined by '_', every other character dropped
            // ('CustomerId' is keyed "customer_id", 'Margin %' "margin_").
            private static string SplitSlug(string n) =>
                new string(QnaTerms.NameTerm(n).Select(c => c == ' ' ? '_' : c).Where(c => c == '_' || char.IsLetterOrDigit(c)).ToArray());
            private static readonly Func<string, string>[] Sluggers = { LowerSlug, LowerPercentSlug, CleanName, SplitSlug };

            public static EntityStyle Read(Model m, JObject jEntities)
            {
                var style = new EntityStyle();
                int wrapped = 0, flat = 0, withState = 0, parsed = 0, lower = 0, cased = 0;
                var keyHits = new int[Sluggers.Length];
                JObject genSample = null, nameSample = null, authSample = null;
                int authWithState = 0, authWithout = 0, authStamped = 0, authPlain = 0;
                var tableKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in jEntities.Properties())
                    if (e.Value is JObject te && TryParseBinding(GetBinding(te), out var tn, out var tp, out var th, out _) && tp == null && th == null
                        && !tableKeys.ContainsKey(tn) && TryResolve(m, te, out var tb) && tb is Table)
                        tableKeys[tn] = e.Name;
                foreach (var e in jEntities.Properties())
                {
                    if (!(e.Value is JObject jEntity)) continue;
                    var b = GetBinding(jEntity);
                    if (!TryParseBinding(b, out var entName, out var prop, out var hier, out var lvl)) continue;
                    parsed++;
                    if (jEntity["Binding"] is JObject) flat++; else wrapped++;
                    if (jEntity["State"] != null) withState++;

                    var names = new[] { entName, prop, hier, lvl }.Where(x => x != null).ToArray();
                    var tableKey = names.Length > 1 && tableKeys.TryGetValue(entName, out var tk) ? tk : null;
                    for (var i = 0; i < Sluggers.Length; i++)
                    {
                        var slug = tableKey != null
                            ? tableKey + "." + string.Join(".", names.Skip(1).Select(Sluggers[i]))
                            : string.Join(".", names.Select(Sluggers[i]));
                        if (e.Name == slug || (e.Name.StartsWith(slug + "_", StringComparison.Ordinal) && int.TryParse(e.Name.Substring(slug.Length + 1), out _)))
                            keyHits[i]++;
                    }

                    var own = names[names.Length - 1];
                    var sawName = false;
                    foreach (var t in (jEntity["Terms"] as JArray ?? new JArray()).OfType<JObject>().SelectMany(o => o.Properties()))
                    {
                        if (!(t.Value is JObject props)) continue;
                        var state = TermState(props);
                        if (QnaTerms.IsGeneratedState(state)) genSample ??= props;
                        if (IsNameTerm(t, own))
                        {
                            // The entity's FIRST name term is its primary one (a camelCase name's as-written copy comes
                            // after its split form), so only that one votes on case and sets the template.
                            if (sawName) continue;
                            sawName = true;
                            nameSample ??= props;
                            if (t.Name == QnaTerms.NameTerm(own)) lower++;
                            else if (t.Name == own) cased++;
                        }
                        else if (state == null || string.Equals(state, "Authored", StringComparison.OrdinalIgnoreCase))
                        {
                            // A word with no State is the author's (the published contract), so it votes here; a stamped
                            // one is Desktop's authored form and sets the template ahead of an unstamped one, which in a
                            // Desktop file can be a derived word rewritten without its State.
                            var stamped = props["LastModified"] != null;
                            if (authSample == null || (stamped && authSample["LastModified"] == null)) authSample = props;
                            if (state == null) authWithout++; else authWithState++;
                            if (stamped) authStamped++;
                            else if (state != null) authPlain++;
                        }
                    }
                }
                if (parsed == 0) return style;

                style.Wrapped = wrapped >= flat;
                style.EntityState = withState * 2 >= parsed;
                style.LowerTerms = lower >= cased;
                var best = 0;
                for (var i = 1; i < Sluggers.Length; i++) if (keyHits[i] > keyHits[best]) best = i;
                style.Slug = Sluggers[best];

                // Term templates keep the siblings' extra metadata (Type, Weight) but never copy a timestamp: that is
                // re-stamped per write, and only when the model's authored terms carry one. The name-term template
                // comes from a sibling's name term, not from a derived word (Power BI weights those below the name).
                var gen = nameSample ?? genSample;
                if (gen != null)
                {
                    style.GeneratedTerm = new JObject(gen.Properties().Where(p => p.Name != "LastModified" && p.Name != "State").Select(p => new JProperty(p.Name, p.Value.DeepClone())));
                    style.GeneratedTerm["State"] = "Generated";
                }
                if (authSample != null)
                {
                    var t = new JObject(authSample.Properties().Where(p => p.Name != "LastModified" && p.Name != "State").Select(p => new JProperty(p.Name, p.Value.DeepClone())));
                    if (authWithState >= authWithout) t["State"] = "Authored";
                    // Desktop's authored words are stateless and stamped. A stateless unstamped word is a hand-written
                    // schema's synonym (no stamps anywhere) or, beside stamped ones, a derived word Desktop rewrote
                    // without its State, so it never votes against a stamp; an explicit unstamped Authored word does.
                    if (authStamped > 0 && authStamped >= authPlain) t["LastModified"] = null;   // placeholder: stamped per write
                    style.AuthoredTerm = t;
                }
                return style;
            }

            public JObject NewAuthoredTerm()
            {
                var t = (JObject)AuthoredTerm.DeepClone();
                // Desktop's own stamp: UTC, milliseconds, a trailing Z ("2025-08-26T10:57:21.382Z").
                if (t.ContainsKey("LastModified")) t["LastModified"] = Stamp();
                return t;
            }

            /// <summary>Make a word Power BI wrote the author's, in this schema's authored shape, keeping its other
            /// metadata (Type, Weight).</summary>
            public void MarkAuthored(JObject props)
            {
                if (AuthoredTerm.ContainsKey("State")) props["State"] = "Authored"; else props.Remove("State");
                if (AuthoredTerm.ContainsKey("LastModified")) props["LastModified"] = Stamp();
            }

            /// <summary>Bring back a tombstoned or suggested word the author listed, as the author's, without moving the
            /// schema's stamp reading (<see cref="QnaTerms.AuthoredWordsStamped"/>): stamped in the authored shape where the
            /// schema reads as stamped (an unstamped "Authored" there outvotes Desktop's stamps, and the next list takes
            /// Desktop's own words), and an explicit unstamped "Authored" where it does not (a stateless word there leaves
            /// the vote to tip on the author's next deletion, after which a list can no longer remove it).</summary>
            public void Revive(JObject props)
            {
                if (DesktopStamps) { MarkAuthored(props); return; }
                props["State"] = "Authored";
                props.Remove("LastModified");
            }

            private static string Stamp() =>
                DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>The synonyms list governs the AUTHOR's terms: every term except the object's name terms (see
        /// <see cref="IsNameTerm"/>: what Copilot matches the object by, in whichever form Power BI wrote it) and Power
        /// BI's other words ("child" beside "total child", "name" beside "customer name", "date" on a date column), which
        /// are Desktop's words, not the author's, and are left as they are. The donor tombstoned both on every write that
        /// did not repeat them. A word is Power BI's when it is State Generated or, in a schema whose authored words carry
        /// stamps, stateless and unstamped (<see cref="QnaTerms.IsPowerBiWord"/>). Power BI's leftovers of an old name ARE
        /// governed (<see cref="QnaTerms.IsOldWord"/>): the entity's stale name term in every spelling it was written (an
        /// old name left by a rename made elsewhere, see <see cref="QnaTerms.StaleNameTerm"/>, the same reading the scan
        /// uses) and the words made of it that the current name does not share. So a list that leaves one out clears it,
        /// which is the way out of a collision an old word causes, and a list that names one makes it the author's word
        /// (<see cref="EntityStyle.MarkAuthored"/>), so a later list can drop it again. <paramref name="removed"/> is the
        /// words a caller took out of a list it had read (find and replace): those are cleared even when Power BI derived
        /// them from the name, since the person saw them and removed them; the name term itself is always kept. When the
        /// entity has no name term (a donor-written entity, or a stale one) one is added, in the model's term shape. A dropped term is tombstoned
        /// ("State":"Deleted") rather than removed, so a regeneration does not bring it back. No word is ever written
        /// twice in one entity: asking for a word the entity already holds in any state (live, tombstoned or suggested,
        /// any casing) revives or keeps that same term, and the guaranteed name term reuses a copy of the name the
        /// entity already holds (a tombstone included) before it writes one. New terms keep the order given.</summary>
        private static void SetTerms(JObject jEntity, TabularNamedObject obj, string synonyms, EntityStyle style, ISet<string> removed)
        {
            if (!(jEntity["Terms"] is JArray jTerms)) jEntity["Terms"] = jTerms = new JArray();
            List<JProperty> AllTerms() => jTerms.OfType<JObject>().SelectMany(o => o.Properties()).ToList();
            var stale = StaleNameTerm(AllTerms(), obj.Name, style.DesktopStamps);
            var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var governed = new List<(string Word, JObject Props, bool PowerBis)>();   // a list: a hand-edited duplicate is governed in every copy
            var dormant = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var term in AllTerms())
            {
                if (!(term.Value is JObject props)) continue;
                if (!IsLive(props)) { if (!dormant.ContainsKey(term.Name)) dormant[term.Name] = props; continue; }
                live.Add(term.Name);
                if (IsNameTerm(term, obj.Name)) continue;
                var state = TermState(props);
                var powerBis = QnaTerms.IsPowerBiWord(state, Stamped(props), style.DesktopStamps);
                // Power BI's own word stays, unless it is a leftover of the old name (every spelling of the stale name,
                // and the words made of it that the current name does not share) or the caller removed it on purpose.
                if (powerBis && !QnaTerms.IsOldWord(term.Name, obj.Name, stale) && !(removed?.Contains(term.Name) ?? false)) continue;
                governed.Add((term.Name, props, powerBis));
            }

            var newTerms = synonyms.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim())
                .Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            foreach (var newTerm in newTerms.Where(t => !live.Contains(t)))
            {
                if (dormant.TryGetValue(newTerm, out var revived)) { style.Revive(revived); dormant.Remove(newTerm); continue; }
                jTerms.Add(new JObject { [newTerm] = style.NewAuthoredTerm() });
            }

            var keep = new HashSet<string>(newTerms, StringComparer.OrdinalIgnoreCase);
            foreach (var (word, props, powerBis) in governed)
                if (!keep.Contains(word)) props["State"] = "Deleted";
                else if (powerBis) style.MarkAuthored(props);   // the author listed a word Power BI wrote: it is theirs now

            // An entity a synonym write touches keeps its name term; make sure this one still does.
            var after = AllTerms();
            if (after.Any(p => IsLive(p.Value) && QnaTerms.ReadsAsName(p.Name, obj.Name)) || QnaTerms.Normalize(obj.Name).Length == 0) return;
            // A tombstoned or suggested copy of the name (the donor tombstoned it on any write that left the name out; an
            // author's own copy of the name may have just been governed out above) comes back in place rather than being
            // written twice.
            var buried = after.FirstOrDefault(p => p.Value is JObject && QnaTerms.ReadsAsName(p.Name, obj.Name));
            if (buried != null) { ((JObject)buried.Value)["State"] = "Generated"; return; }
            var nameProps = (JObject)style.GeneratedTerm.DeepClone();
            nameProps["State"] = "Generated";
            jTerms.Insert(0, new JObject { [style.TermCase(obj.Name)] = nameProps });
        }

        // ---- commit ----------------------------------------------------------------------------------------

        /// <summary>Assign the edited LSDL back to the culture. Deliberately NOT undo-suspended (the donor
        /// suspended here): Culture.Content is a tracked wrapper property, and recording its change is exactly
        /// what makes the surrounding MutateAsync batch undoable AND what lets a dry_run rehearsal roll the edit
        /// back — a suspended write survives both (UndoManager.Add drops actions while suspended; EndBatch's
        /// rollback replays only recorded actions). Matches the in-repo precedent: ApplyAiInstructions writes
        /// Culture.Content tracked too. The catch is the BACKSTOP: nothing here predicts what the validator refuses
        /// (the only refusal on record was hidden shadow entities, which Prune removes first), so a refusal must
        /// still surface as an actionable error rather than the opaque dictionary throw.</summary>
        private static void Commit(Culture culture, JObject lsdl)
        {
            try { culture.Content = lsdl.ToString(Formatting.None); }
            catch (Exception ex) when (ex.Message.Contains("same key has already been added"))
            {
                var key = ExtractKey(ex.Message);
                throw new InvalidOperationException(
                    $"The linguistic schema requires unique names: the validator reports two objects under the linguistic-schema term {(key == null ? "it could not name" : $"'{key}'")}. Find the two objects whose names read as that term, then rename or hide one of them; hiding also requires the dead linguistic entity to be pruned, which set_synonyms does automatically on the next call.", ex);
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
