using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json.Linq;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Linguistics;

namespace Semanticus.Analysis
{
    /// <summary>
    /// One culture's linguistic schema, parsed ONCE and then looked in, so that reading every object's synonyms costs
    /// one parse plus a lookup per object. WHY: the donor reader (<c>SynonymHelper.GetSynonyms</c>) parses the whole
    /// schema again for every object it is asked about. SYN-FIELD and SYN-TABLE ask about every visible field and table
    /// on every commit (the health probe's rescan), so one synonym write on a 900-field model cost about 930 full parses,
    /// each larger than the last: Kane's session on 11 Oct 2026 fell from 21 to 4.8 writes a minute. QueryCatalog met
    /// the same cost on a catalog read and fixed it the same way.
    ///
    /// The answer is the donor's, exactly, including how it fails. An object is matched by its binding (Binding at the
    /// top level, or under Definition when Binding is not an object); a binding with one key is a table's, two keys
    /// ConceptualEntity and ConceptualProperty a column's or a measure's, ConceptualEntity and Hierarchy a hierarchy's,
    /// and three keys a level's; names match exactly; the first entity in the document wins. The donor walks the
    /// entities in order and gives up (null) at the first entity it cannot read (a value that is not an object, or a
    /// Definition that is not an object while Binding is missing), so an entity after that one is never found. Here the
    /// walk stops at the same place. A matched entity whose terms cannot be read reads as null, as there.
    ///
    /// Indexes are kept by schema text: the same text gives the same answer, so a scan's rules, and the next scan of an
    /// unchanged schema, share one parse. A write makes new text and so a new index; nothing can read a stale one.
    /// </summary>
    public sealed class SynonymIndex
    {
        private static int _parses;
        private const int Kept = 4;
        private static readonly object Gate = new object();
        private static readonly LinkedList<(string Content, SynonymIndex Index)> Recent = new LinkedList<(string, SynonymIndex)>();

        /// <summary>How many times a linguistic schema was parsed here since the process started. A test seam: a scan
        /// must parse a schema a bounded number of times, not once per object.</summary>
        public static int Parses => Volatile.Read(ref _parses);

        private readonly Dictionary<string, string> _tables = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<(string, string), string> _members = new Dictionary<(string, string), string>();
        private readonly Dictionary<(string, string), string> _hierarchies = new Dictionary<(string, string), string>();
        private readonly Dictionary<(string, string, string), string> _levels = new Dictionary<(string, string, string), string>();
        private static readonly SynonymIndex None = new SynonymIndex();

        private SynonymIndex() { }

        /// <summary>The object's terms joined with ", ", or null when it has none: the donor's
        /// <c>SynonymHelper.GetSynonyms(o, culture)</c> result, from one parse of the culture's schema.</summary>
        public static string Terms(TabularNamedObject o, Culture culture) => For(culture).TermsOf(o);

        /// <summary>The index of <paramref name="culture"/>'s schema as it reads now. A culture with no JSON schema, or
        /// one the donor cannot read, gives an index in which nothing has synonyms.</summary>
        public static SynonymIndex For(Culture culture)
        {
            if (culture == null || culture.ContentType != ContentType.Json || string.IsNullOrEmpty(culture.Content)) return None;
            var content = culture.Content;
            lock (Gate)
                for (var n = Recent.First; n != null; n = n.Next)
                    if (ReferenceEquals(n.Value.Content, content) || string.Equals(n.Value.Content, content, StringComparison.Ordinal))
                    {
                        if (n != Recent.First) { Recent.Remove(n); Recent.AddFirst(n); }
                        return n.Value.Index;
                    }
            var index = Build(content);
            lock (Gate)
            {
                Recent.AddFirst((content, index));
                while (Recent.Count > Kept) Recent.RemoveLast();
            }
            return index;
        }

        /// <summary>The object's terms, or null when it has none (see <see cref="Terms"/>).</summary>
        public string TermsOf(TabularNamedObject o)
        {
            string terms = null;
            switch (o)
            {
                case Table t: _tables.TryGetValue(t.Name, out terms); break;
                case Measure m: _members.TryGetValue((m.Table.Name, m.Name), out terms); break;
                case Column c: _members.TryGetValue((c.Table.Name, c.Name), out terms); break;
                case Hierarchy h: _hierarchies.TryGetValue((h.Table.Name, h.Name), out terms); break;
                case Level l: _levels.TryGetValue((l.Hierarchy.Table.Name, l.Hierarchy.Name, l.Name), out terms); break;
            }
            return terms;
        }

        private static SynonymIndex Build(string content)
        {
            Interlocked.Increment(ref _parses);
            JObject entities;
            try { entities = JObject.Parse(content)["Entities"] as JObject; }
            catch { return None; }
            if (entities == null) return None;
            var s = new SynonymIndex();
            foreach (var e in entities)
            {
                // Where the donor's walk throws (and so answers null for every object not yet found), this walk stops.
                if (!(e.Value is JObject entity)) break;
                JObject b;
                try { b = entity["Binding"] as JObject ?? entity["Definition"]?["Binding"] as JObject; }
                catch { break; }
                if (b == null) continue;
                var ent = b["ConceptualEntity"] as JValue;
                if (ent == null) continue;
                var table = ent.ToString();
                if (b.Count == 1) Add(s._tables, table, entity);
                else if (b.Count == 2 && b["ConceptualProperty"] is JValue p) Add(s._members, (table, p.ToString()), entity);
                else if (b.Count == 2 && b["Hierarchy"] is JValue h) Add(s._hierarchies, (table, h.ToString()), entity);
                else if (b.Count == 3 && b["Hierarchy"] is JValue lh && b["HierarchyLevel"] is JValue lv) Add(s._levels, (table, lh.ToString(), lv.ToString()), entity);
            }
            return s;
        }

        private static void Add<TKey>(Dictionary<TKey, string> into, TKey key, JObject entity)
        {
            if (!into.ContainsKey(key)) into[key] = ReadTerms(entity);
        }

        /// <summary>The donor's term read for one matched entity: live terms (not Deleted, not Suggested) joined with
        /// ", ", and null on anything it cannot read.</summary>
        private static string ReadTerms(JObject entity)
        {
            try
            {
                var names = new List<string>();
                foreach (JObject term in (JArray)entity["Terms"])
                    foreach (var kv in term)
                    {
                        var p = ((JObject)kv.Value).ToObject<TermProperties>();
                        if (p.State != State.Deleted && p.State != State.Suggested) names.Add(kv.Key);
                    }
                return string.Join(", ", names);
            }
            catch { return null; }
        }
    }
}
