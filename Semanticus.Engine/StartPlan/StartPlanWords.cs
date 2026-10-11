// The assistant's words for a start plan (design-spec-first-start-v2.md section 2.4, lane sf-words).
//
// get_start_words hands the person's own assistant what is still open over the PREVIEW model (the hidden scratch the plan
// is decided on), one page at a time. write_start_words turns what it writes into ASSISTANT lines on the plan: the person
// reads them in the Words section and approves them with everything else, and one build step writes them. The engine
// writes no words itself (golden rule 1); it only checks them and keeps them.
//
// WHAT A WORD IS CHECKED FOR, per item, with the same checks the direct tools and the composer's draft checks make:
//   description   no formula text, at most 200 characters (the composer's description limit)
//   display_name  no formula text, a name no sibling already has (the clashing name is named), at most 100 characters, a name the naming rules would
//                 not flag (ReadinessRuleSet.NamingRuleFor: the scan after the build must agree with what the check accepted), and a change (a change of
//                 case only is a change)
//   keep_name     the object's own name is right as it is: no text is needed (a reason may be sent). It closes the name item, and the build accepts the
//                 naming finding the name raises, so the scan does not ask again. Either door may send it; the person's keep is the person's own
//   synonyms      no formula text in any term, the object is not hidden (Copilot ignores hidden objects, the set_synonyms rule)
//   format        a measure, no filter-driven format beside it, and FormatStringRules.Problem (set_measure_format's own check)
//   instructions  at most 10,000 characters (set_ai_instructions' own limit)
// A refused item is named and explained and never stops the rest of the batch. Free and Pro write the same way: the words
// come from the person's own assistant, so there is nothing to gate (K2).
//
// THREE RULES ABOUT A LINE THAT IS ALREADY THERE
//   - The person's word stands. A line the person decided, dropped, or pinned is never written over, and says so.
//   - The assistant's own earlier line is replaced by its later one (one line per object and property).
//   - A line for an object that has gone from the preview (its table was left out) is HELD, never deleted: nothing here
//     removes a line, so including the table again brings the words back. IsHeld is how the build tells them apart.
//
// REFS. A line is stored against the ref the object has BEFORE the plan's renames, because an assistant's words run first
// in the build and renames run after them (LocalEngine.ApplyOrder). The assistant may name an object by either name, so
// Back() turns a ref into its pre-rename form and Forward() into its post-rename form, and both are looked for.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine.StartPlan;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine.StartPlan
{
    internal static class StartPlanWords
    {
        public const string Description = "description";
        public const string Synonyms = "synonyms";
        public const string DisplayName = "display_name";
        public const string Format = "format";
        public const string Instructions = "instructions";
        public const string KeepName = "keep_name";

        /// <summary>The composer's own limit for a description (DraftRehearsal max_chars, description kind).</summary>
        internal const int DescriptionChars = 200;
        /// <summary>The longest business name a word may give. A name longer than this is not a name people read.</summary>
        internal const int NameChars = 100;

        /// <summary>The word kind the doors speak for a queue kind, or null for a queue item that is not a word.</summary>
        internal static string WordKindOfQueue(WordsQueueItem x) => x?.Kind switch
        {
            "describe" => Description,
            "name" => DisplayName,
            "synonym" => string.Equals(x.Tool, "set_synonyms", StringComparison.Ordinal) ? Synonyms : null,   // the schema itself is not words
            "instructions" => Instructions,
            // A measure's format only. The Plan's words queue also lists a key or date column's format (set_property, psc-plan), which a start plan's words
            // cannot write ("Only a measure has a format I can write here") and whose format the start plan's own Jev lines carry (join 6).
            "format" => string.Equals(x.Tool, "set_measure_format", StringComparison.Ordinal) ? Format : null,
            _ => null,
        };

        /// <summary>The kind a caller wrote, mapped to ours, or null. The queue's own names are accepted too, so an assistant
        /// that copies a queue item's kind back is not refused for it.</summary>
        internal static string NormalizeKind(string kind) => (kind ?? "").Trim().ToLowerInvariant() switch
        {
            "description" or "describe" => Description,
            "synonyms" or "synonym" => Synonyms,
            "display_name" or "displayname" or "name" or "rename" => DisplayName,
            "format" or "format_string" => Format,
            "instructions" or "ai_instructions" => Instructions,
            "keep_name" or "keep" or "keepname" or "keep_this_name" => KeepName,
            _ => null,
        };

        internal static string OpOf(string kind) => kind switch
        {
            Description => "set_description", DisplayName => "rename", Synonyms => "set_synonyms",
            Format => "set_measure_format", KeepName => "keep_name", _ => "set_ai_instructions",
        };

        /// <summary>The property a line writes: what de-duplicates two lines about one object (ChangeItem.Target).</summary>
        internal static string TargetOf(string kind) => kind switch
        {
            Description => "description", DisplayName => "name", Synonyms => "synonyms", Format => "formatstring", KeepName => "name_kept", _ => "instructions",
        };

        // ---------------------------------------------------------------- refs, through the plan's renames

        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>The one ref a model-level word is stored and matched under: what ObjectRefs.For gives the Model.</summary>
        internal const string ModelRef = "model:";

        /// <summary>True for every spelling of "the model itself": no ref, "model", "model:" (any suffix) and the analyzer's
        /// "obj:Model/&lt;name&gt;", which is the ref the words list hands over for the AI instructions. The queue, the assistant's
        /// own answer and a stored line each spell it differently, so every one is read through here.</summary>
        internal static bool IsModelRef(string objRef) =>
            string.IsNullOrWhiteSpace(objRef) || Same(objRef.Trim(), "model") || objRef.Trim().StartsWith("model:", StringComparison.OrdinalIgnoreCase)
            || Same(objRef.Trim(), "obj:Model") || objRef.Trim().StartsWith("obj:Model/", StringComparison.OrdinalIgnoreCase);

        /// <summary>"measure:Sales/Total" into its kind, table and name. A table has no table part. False for a ref with no name.</summary>
        internal static bool Split(string objRef, out string kind, out string table, out string name)
        {
            kind = table = name = null;
            if (string.IsNullOrEmpty(objRef) || IsModelRef(objRef)) return false;   // the model is no table named "model"
            var colon = objRef.IndexOf(':');
            if (colon < 0) { kind = "table"; name = objRef; return true; }
            kind = objRef.Substring(0, colon);
            var rest = objRef.Substring(colon + 1);
            if (kind == "table") { name = rest; return true; }
            if (kind != "column" && kind != "measure" && kind != "hierarchy") return false;
            var slash = rest.IndexOf('/');
            if (slash <= 0) return false;
            table = rest.Substring(0, slash);
            name = rest.Substring(slash + 1);
            return name.Length > 0;
        }

        private static string Join(string kind, string table, string name) => kind == "table" ? "table:" + name : kind + ":" + table + "/" + name;

        /// <summary>The renames the plan will make, as (kind, table as before, old name, new name): every kept rename line,
        /// whoever decided it.</summary>
        private static List<(string Kind, string Table, string Old, string New)> Renames(StartPlanDoc doc) =>
            (doc?.Lines ?? new List<StartLine>())
                .Where(l => l.Kept && l.Item != null && string.Equals(l.Item.Kind, "rename", StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(l.Item.After) && Split(l.Item.ObjectRef, out _, out _, out _))
                .Select(l => { Split(l.Item.ObjectRef, out var k, out var t, out var n); return (k, t, n, l.Item.After.Trim()); })
                .ToList();

        /// <summary>The ref an object has once the plan's renames are made (its table's new name, then its own).</summary>
        internal static string Forward(StartPlanDoc doc, string objRef)
        {
            if (IsModelRef(objRef)) return ModelRef;
            if (!Split(objRef, out var kind, out var table, out var name)) return objRef;
            var renames = Renames(doc);
            var (_, _, _, newName) = renames.FirstOrDefault(r => r.Kind == kind && Same(r.Table, table) && Same(r.Old, name));
            var newTable = table == null ? null : renames.Where(r => r.Kind == "table" && Same(r.Old, table)).Select(r => r.New).FirstOrDefault();
            return Join(kind, newTable ?? table, newName ?? name);
        }

        /// <summary>The ref an object has BEFORE the plan's renames: the form a line is stored against.</summary>
        internal static string Back(StartPlanDoc doc, string objRef)
        {
            if (IsModelRef(objRef)) return ModelRef;
            if (!Split(objRef, out var kind, out var table, out var name)) return objRef;
            var renames = Renames(doc);
            var oldTable = table == null ? null : renames.Where(r => r.Kind == "table" && Same(r.New, table)).Select(r => r.Old).FirstOrDefault();
            var before = oldTable ?? table;
            var (_, _, oldName, _) = renames.FirstOrDefault(r => r.Kind == kind && Same(r.Table, before) && Same(r.New, name));
            return Join(kind, before, oldName ?? name);
        }

        /// <summary>True when a kept or dropped line already speaks for this property of this object.</summary>
        internal static StartLine LineFor(StartPlanDoc doc, string canonicalRef, string target) =>
            (doc?.Lines ?? new List<StartLine>()).FirstOrDefault(l =>
                l.Item != null && Same(l.Item.Target ?? "", target) && Same(Back(doc, l.Item.ObjectRef ?? ""), canonicalRef));

        /// <summary>The queue items still open: the ones no line (kept or dropped) already speaks for. A line the person dropped
        /// stays dropped, so its item is not asked again.</summary>
        internal static List<(WordsQueueItem Item, string Kind)> Open(StartPlanDoc doc, IEnumerable<WordsQueueItem> queue)
        {
            var open = new List<(WordsQueueItem, string)>();
            foreach (var x in queue)
            {
                var kind = WordKindOfQueue(x);
                if (kind == null) continue;
                var canonical = Back(doc, x.ObjectRef ?? "");
                if (LineFor(doc, canonical, TargetOf(kind)) != null) continue;
                // A name the assistant or the person kept is answered: the item is not asked again.
                if (kind == DisplayName && LineFor(doc, canonical, TargetOf(KeepName)) != null) continue;
                open.Add((x, kind));
            }
            return open;
        }

        /// <summary>True when a hand-made line (the assistant's words, or what the person chose) is about an object the preview no longer has (its table was left
        /// out, its column, its measure, its link): it is kept, not applied. <paramref name="present"/> holds the canonical refs the preview has.
        /// The rule lives in <see cref="StartPlanHold"/>, the one place that holds a line; this name stays for the words' own callers.</summary>
        internal static bool IsHeld(StartLine line, ISet<string> present) => StartPlanHold.IsHeld(line, present);

        /// <summary>The refs of the hand-made lines that the preview still has (<see cref="StartPlanHold.PresentRefs"/>).</summary>
        internal static HashSet<string> PresentRefs(StartPlanDoc doc, Model preview) => StartPlanHold.PresentRefs(doc, preview);

        // ---------------------------------------------------------------- the checks (what the preview model knows)

        /// <summary>What one word's own text and object say, worked out on the preview model with no document locks held.</summary>
        internal sealed class Facts
        {
            public StartWord Word;
            public string Kind;
            public string Refusal;
            public string FoundRef;
            public string Name;
            public string ObjectType;
            public string Before;
            public string Text;
            /// <summary>What was said with a keep_name (why the name is right), or null.</summary>
            public string Reason;
        }

        private static string Plain(string objRef) => objRef ?? "(no ref)";

        /// <summary>Look at every word on the preview model. Never throws for a bad word: the refusal is the answer. Business
        /// names are read first, and each one that passes is added to a working copy of the plan, so a format or a description
        /// written in the same batch under the object's NEW name finds it. The answers come back in the order the words came.</summary>
        internal static List<Facts> ReadFacts(Model m, StartPlanDoc doc, StartWord[] words, Func<string, string> formulaTextIn)
        {
            var list = (words ?? Array.Empty<StartWord>()).ToArray();
            var results = new Facts[list.Length];
            var working = StartPlanStore.Copy(doc) ?? new StartPlanDoc();
            working.Lines ??= new List<StartLine>();
            foreach (var i in Enumerable.Range(0, list.Length).OrderBy(i => NormalizeKind(list[i]?.Kind) == DisplayName ? 0 : 1))
            {
                var f = new Facts { Word = list[i] };
                try { Check(m, working, list[i], f, formulaTextIn); }
                catch (Exception ex) { f.Refusal = "Could not be checked: " + ex.Message; }
                results[i] = f;
                if (f.Refusal == null && f.Kind == DisplayName)
                    working.Lines.Add(new StartLine
                    {
                        Kept = true, DecidedBy = StartDecidedBy.Assistant,
                        Item = new ChangeItem { Kind = "rename", ObjectRef = Back(working, f.FoundRef), After = f.Text, Target = "name" },
                    });
            }
            return results.ToList();
        }

        private static void Check(Model m, StartPlanDoc doc, StartWord w, Facts f, Func<string, string> formulaTextIn)
        {
            if (w == null) { f.Refusal = "An empty entry was skipped."; return; }
            f.Kind = NormalizeKind(w.Kind);
            if (f.Kind == null)
            {
                f.Refusal = "'" + w.Kind + "' is not a kind of words I can write. Use description, synonyms, display_name or format.";
                return;
            }
            f.Text = (w.Text ?? "").Trim();
            if (f.Kind == KeepName) { f.Reason = f.Text.Length == 0 ? null : f.Text; f.Text = ""; }   // the name kept is the object's own, read below
            else if (f.Text.Length == 0) { f.Refusal = "There are no words to write."; return; }

            if (f.Kind == Instructions)
            {
                if (f.Text.Length > LocalEngine.AiInstructionsLimit)
                    f.Refusal = $"The AI instructions are {f.Text.Length} characters. The limit is {LocalEngine.AiInstructionsLimit}. Shorten them.";
                f.ObjectType = "model";
                if (!IsModelRef(w.Ref)) { f.Refusal = "AI instructions belong to the model, not to " + w.Ref + ". Leave the ref empty or use model:."; return; }
                f.FoundRef = ModelRef;
                return;
            }

            // Find the object under either of its names (before or after the plan's renames).
            ITabularNamedObject obj = null;
            foreach (var candidate in string.IsNullOrWhiteSpace(w.Ref) ? Array.Empty<string>() : new[] { w.Ref, Forward(doc, w.Ref), Back(doc, w.Ref) })
            {
                if (string.IsNullOrEmpty(candidate)) continue;
                try { obj = ObjectRefs.Resolve(m, candidate); } catch { obj = null; }
                if (obj != null) { f.FoundRef = candidate; break; }
            }
            if (obj == null)
            {
                f.Refusal = "It is not in the plan right now. It may be left out, or its name may have changed. Read the words list again.";
                return;
            }
            f.Name = obj.Name;
            f.ObjectType = obj switch { Table _ => "table", Column _ => "column", Measure _ => "measure", Hierarchy _ => "hierarchy", _ => "other" };

            switch (f.Kind)
            {
                case Description:
                {
                    if (obj.GetType().GetProperty("Description", BindingFlags.Public | BindingFlags.Instance)?.CanWrite != true)
                    { f.Refusal = "It is not something that can have a description."; return; }
                    if (f.Text.Length > DescriptionChars)
                    { f.Refusal = $"The description is {f.Text.Length} characters. The limit is {DescriptionChars}. Shorten it."; return; }
                    if (formulaTextIn(f.Text) is { } formula) { f.Refusal = "It contains formula text: " + formula + ". Use plain words."; return; }
                    f.Before = (string)obj.GetType().GetProperty("Description")?.GetValue(obj);
                    return;
                }
                case DisplayName:
                {
                    if (f.ObjectType == "other" || f.ObjectType == "model") { f.Refusal = "Only tables, columns and measures can be given a business name."; return; }
                    if (f.Text.Length > NameChars) { f.Refusal = $"The name is {f.Text.Length} characters. The limit is {NameChars}. Shorten it."; return; }
                    if (formulaTextIn(f.Text) is { } formula) { f.Refusal = "It contains formula text: " + formula + ". A name is plain words."; return; }
                    // A change of case only ("Average BILL RATE" to "Average Bill Rate") is a change: it is the commonest way a cryptic name is put right.
                    if (string.Equals(f.Text, obj.Name, StringComparison.Ordinal))
                    { f.Refusal = "It already has that name. If the name is right as it is, send kind keep_name for it, and the question is closed."; return; }
                    var clash = ClashingName(obj, f.Text);
                    if (clash != null) { f.Refusal = clash; return; }
                    // The words check and the naming rules agree: a name this check accepts is a name the scan does not flag after the build.
                    var flagged = ReadinessRuleSet.NamingRuleFor(obj, f.Text);
                    if (flagged != null)
                    { f.Refusal = ReadinessRuleSet.NamingRuleWhy(flagged, f.Text) + " Choose a plainer name, or send kind keep_name to keep the name it has."; return; }
                    f.Before = obj.Name;
                    return;
                }
                case KeepName:
                {
                    if (f.ObjectType == "other" || f.ObjectType == "model") { f.Refusal = "Only tables, columns and measures have a name to keep."; return; }
                    f.Text = obj.Name;
                    f.Before = obj.Name;
                    return;
                }
                case Synonyms:
                {
                    if (f.ObjectType == "other") { f.Refusal = "Only tables, columns and measures can have synonyms."; return; }
                    var hidden = obj switch { Table t => t.IsHidden, Column c => c.IsHidden || c.Table.IsHidden, Measure me => me.IsHidden || me.Table.IsHidden, _ => false };
                    if (hidden) { f.Refusal = "It is hidden. Copilot ignores hidden objects, so synonyms on it would do nothing."; return; }
                    var terms = Terms(f.Text);
                    if (terms.Count == 0) { f.Refusal = "There are no words to write."; return; }
                    var tooLong = terms.FirstOrDefault(t => t.Length > NameChars);
                    if (tooLong != null) { f.Refusal = $"The synonym '{tooLong.Substring(0, 30)}...' is {tooLong.Length} characters. The limit is {NameChars}."; return; }
                    foreach (var t in terms)
                        if (formulaTextIn(t) is { } formula) { f.Refusal = "'" + t + "' contains formula text: " + formula + ". A synonym is a plain word."; return; }
                    f.Text = string.Join(", ", terms);
                    return;
                }
                case Format:
                {
                    if (!(obj is Measure me)) { f.Refusal = "Only a measure has a format I can write here."; return; }
                    if (!string.IsNullOrWhiteSpace(me.FormatStringExpression))
                    { f.Refusal = "Its format changes with the filter (a format string expression). A fixed format beside it is not allowed."; return; }
                    var problem = FormatStringRules.Problem(f.Text);
                    if (problem != null) { f.Refusal = problem; return; }
                    f.Before = me.FormatString;
                    return;
                }
            }
        }

        /// <summary>The comma, semicolon or line separated terms of a synonyms word, trimmed and without repeats.</summary>
        internal static List<string> Terms(string text) =>
            (text ?? "").Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>The refusal for a name another object in its place already has, naming it, or null. The same sibling rules
        /// find and replace and the starter measures use: a table among tables; a column among the table's columns, measures
        /// and hierarchies; a measure among every measure in the model and its table's columns.</summary>
        private static string ClashingName(ITabularNamedObject obj, string name)
        {
            switch (obj)
            {
                case Table t:
                {
                    var other = t.Model.Tables.FirstOrDefault(x => x != t && Same(x.Name, name));
                    return other == null ? null : $"The model already has a table named {other.Name}, so {t.Name} cannot take that name.";
                }
                case Column c:
                {
                    var col = c.Table.Columns.FirstOrDefault(x => x != c && Same(x.Name, name));
                    if (col != null) return $"{c.Table.Name} already has a column named {col.Name}, so {c.Name} cannot take that name.";
                    var me = c.Table.Measures.FirstOrDefault(x => Same(x.Name, name));
                    if (me != null) return $"{c.Table.Name} already has a measure named {me.Name}, so {c.Name} cannot take that name.";
                    var h = c.Table.Hierarchies.FirstOrDefault(x => Same(x.Name, name));
                    return h == null ? null : $"{c.Table.Name} already has a hierarchy named {h.Name}, so {c.Name} cannot take that name.";
                }
                case Measure me2:
                {
                    var other = me2.Model.AllMeasures.FirstOrDefault(x => x != me2 && Same(x.Name, name));
                    if (other != null) return $"The model already has a measure named {other.Name}, so {me2.Name} cannot take that name.";
                    var col = me2.Table.Columns.FirstOrDefault(x => Same(x.Name, name));
                    return col == null ? null : $"{me2.Table.Name} already has a column named {col.Name}, so {me2.Name} cannot take that name.";
                }
                default:
                    return null;
            }
        }

        // ---------------------------------------------------------------- the document (decided under the document's lock)

        /// <summary>The kinds of rename that can collide with each other are the ones in one place: tables with tables, the
        /// objects of one table together, and measures across the model. True when two names would be the same.</summary>
        private static string PendingClash(StartPlanDoc doc, string canonicalRef, string kind, string table, string newName)
        {
            foreach (var l in doc.Lines ?? new List<StartLine>())
            {
                if (!l.Kept || l.Item == null || !string.Equals(l.Item.Kind, "rename", StringComparison.OrdinalIgnoreCase)) continue;
                if (Same(l.Item.ObjectRef, canonicalRef) || !Same(l.Item.After ?? "", newName)) continue;
                if (!Split(l.Item.ObjectRef, out var k2, out var t2, out var n2)) continue;
                var sameSpace = kind == "table" ? k2 == "table"
                    : kind == "measure" ? k2 == "measure" || (k2 == "column" && Same(t2, table))
                    : (k2 == "column" || k2 == "measure" || k2 == "hierarchy") && Same(t2, table);
                if (sameSpace) return $"The plan already renames {n2} to {l.Item.After}, so {newName} is taken.";
            }
            return null;
        }

        private static string PinKey(string objRef, string target) => objRef + "|" + target;

        /// <summary>Make the lines for the words that passed their checks, in <paramref name="doc"/>, which the caller holds
        /// the lock of. Returns how many were written and why each other was refused.</summary>
        internal static (int Written, List<string> Refused, bool Changed) Apply(StartPlanDoc doc, List<Facts> facts, int round, string by = StartDecidedBy.Assistant)
        {
            // WHO IS WRITING. The assistant's door writes ASSISTANT lines and never writes over the person's. The person's own door (Studio's edit box on a word line)
            // writes PERSON lines, and a person's words replace the assistant's, their own earlier ones, and a line they dropped (writing words is keeping them). A line from
            // Jev or the rules is not replaced here for either door: the person pins a value on it (edit_start_plan pin), which is the one way a value is chosen over a rule's.
            var person = string.Equals(by, StartDecidedBy.Person, StringComparison.Ordinal);
            var refused = new List<string>();
            var written = 0;
            var changed = false;
            doc.Lines ??= new List<StartLine>();
            // Names first, so a word written under an object's new name in the same batch is stored against its old one.
            foreach (var f in facts.OrderBy(f => f.Kind == DisplayName ? 0 : 1))
            {
                var refText = Plain(f.Word?.Ref);
                if (f.Refusal != null) { refused.Add(refText + ": " + f.Refusal); continue; }
                var canonical = Back(doc, f.FoundRef);
                var target = TargetOf(f.Kind);
                var existing = LineFor(doc, canonical, target);

                if (existing != null && !existing.Kept && !person)
                { refused.Add(refText + ": You dropped this line, so it stays dropped until you keep it again."); continue; }
                if (existing != null && existing.DecidedBy == StartDecidedBy.Person && !person)
                { refused.Add(refText + ": You already chose this yourself, so your choice stands."); continue; }
                if (existing != null && existing.DecidedBy != StartDecidedBy.Assistant && existing.DecidedBy != StartDecidedBy.Person)
                { refused.Add(refText + ": The plan already has this from " + (existing.DecidedBy == StartDecidedBy.Jev ? "Jev" : "Semanticus") + ". " + (person ? "Pin your own value on that line to change it." : "Change it in the plan if you want something else.")); continue; }
                if (!person && doc.Pins != null && (doc.Pins.ContainsKey(PinKey(canonical, target)) || doc.Pins.ContainsKey(canonical)))
                { refused.Add(refText + ": You pinned this value, so it stays."); continue; }
                // THE PERSON'S EDIT NAMES THE WORDS IT STARTED FROM. Words written since (the assistant's next batch) are not covered by a save made over the old ones.
                if (person && f.Word?.Expected != null && existing != null
                    && !string.Equals((existing.Word ?? existing.Item?.After ?? "").Trim(), f.Word.Expected.Trim(), StringComparison.Ordinal))
                { refused.Add(refText + ": These words changed while you were editing them, so yours were not saved. Look at the new words and edit again."); continue; }
                if (f.Kind == DisplayName)
                {
                    Split(canonical, out var k, out var t, out _);
                    var clash = PendingClash(doc, canonical, k, t, f.Text);
                    if (clash != null) { refused.Add(refText + ": " + clash); continue; }
                    // A new name settles the question the other way: a name the assistant had kept is not the name any more. One the person kept stands.
                    var kept = LineFor(doc, canonical, TargetOf(KeepName));
                    if (kept != null && kept.DecidedBy == StartDecidedBy.Person) { refused.Add(refText + ": You kept this name yourself, so it stays."); continue; }
                    if (kept != null) { doc.Lines.Remove(kept); changed = true; }
                }
                if (f.Kind == KeepName)
                {
                    // The check read the name the preview has. A rename written in this same batch is not in it yet, so the name it would keep is about to go.
                    Split(Forward(doc, canonical), out _, out _, out var nameNow);
                    if (!Same(nameNow ?? "", f.Name ?? "")) { refused.Add(refText + ": This batch renames it, so there is no name to keep. Send the new name or keep_name, not both."); continue; }
                }

                var item = existing?.Item ?? new ChangeItem { Id = "sw-" + Guid.NewGuid().ToString("N").Substring(0, 8) };
                item.ObjectRef = canonical;
                item.ObjectName = f.Kind == Instructions ? "the model" : f.Name;
                item.Kind = OpOf(f.Kind);
                item.Target = target;
                item.Source = "ai";
                item.Generated = true;
                item.Risk = f.Kind == DisplayName ? "rename" : f.Kind == KeepName ? "safe" : "ai";
                item.Status = "proposed";
                item.Title = TitleOf(f);
                item.Rationale = person ? "Written by you." : "Written by your assistant.";
                // What the line was written OVER stays what it was. The preview the check read has this line's own earlier words applied, so f.Before is those words, not the model's,
                // and a second write (the person's edit of the assistant's words) would be refused by the build as "changed after the plan was built".
                item.Before = existing != null ? item.Before : f.Before;
                var before = (existing?.Item?.After, existing?.Word);
                item.After = f.Text;
                if (existing == null)
                {
                    doc.Lines.Add(new StartLine
                    {
                        Item = item, DecidedBy = by, Word = f.Text, Round = round, Kept = true,
                        Why = WhyOf(f, person),
                    });
                    changed = true;
                }
                else if (before.After != f.Text || before.Word != f.Text || existing.DecidedBy != by || !existing.Kept)
                {
                    existing.Word = f.Text;
                    existing.Round = round;
                    existing.DecidedBy = by;
                    existing.Kept = true;
                    existing.Why = WhyOf(f, person);
                    existing.Item.Status = "proposed";
                    existing.Item.DroppedBy = null;
                    changed = true;
                }
                written++;
            }
            return (written, refused, changed);
        }

        /// <summary>The line's own account of itself: a kept name says it was kept (and why), every other word says who wrote it.</summary>
        private static string WhyOf(Facts f, bool person) => f.Kind == KeepName
            ? (person ? "You kept this name." : "Your assistant kept this name.") + (string.IsNullOrWhiteSpace(f.Reason) ? "" : " " + f.Reason.Trim())
            : (person ? "You wrote this " : "Your assistant wrote this ") + KindWords(f.Kind) + ".";

        private static string KindWords(string kind) => kind switch
        {
            Description => "description", DisplayName => "business name", Synonyms => "set of synonyms", Format => "format", KeepName => "kept name", _ => "set of AI instructions",
        };

        private static string TitleOf(Facts f) => f.Kind switch
        {
            Description => "Describe " + f.Name,
            DisplayName => "Rename " + f.Name + " to " + f.Text,
            Synonyms => "Synonyms for " + f.Name,
            Format => "Format for " + f.Name,
            KeepName => "Keep the name " + f.Name,
            _ => "AI instructions for the model",
        };

        /// <summary>How many words the plan has and how many are still open: the open ones are in the queue and no line speaks
        /// for them; the rest are assistant lines the preview still has. Held lines are in neither.</summary>
        internal static StartWords Tally(StartPlanDoc doc, int open, ISet<string> present)
        {
            var landed = (doc.Lines ?? new List<StartLine>()).Count(l =>
                l.Kept && l.Word != null && StartPlanDecide.IsHandMade(l.DecidedBy) && !IsHeld(l, present));
            return new StartWords { Open = open, Total = open + landed };
        }
    }
}

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        /// <summary>The plan's words changed (its id). The decide loop listens: a new name or description reopens the work
        /// for that object (S10), and nothing here can cancel or restart a loop itself.</summary>
        internal event Action<string> StartPlanWordsWritten;

        /// <summary>Test seam: where the preview model comes from. In production it is the plan's hidden scratch.</summary>
        internal interface IStartPreviewReader { Task<T> ReadAsync<T>(Func<Model, T> read); }

        internal IStartPreviewReader StartPlanPreviewReaderForTest;

        /// <summary>The first piece of formula text in a word, or null. The composer's no_formula_text check, run on the same
        /// patterns: a [Bracket] reference, or a name followed by a bracket that DAX knows as a function.</summary>
        internal static string FormulaTextIn(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var bracket = ReBracketRef.Match(text);
            if (bracket.Success) return bracket.Value;
            foreach (System.Text.RegularExpressions.Match call in ReCallToken.Matches(text))
                if (DaxValidator.IsKnownFunction(call.Groups[1].Value)) return call.Groups[1].Value;
            return null;
        }

        // The words preview: a copy of the person's model with THE PLAN BUILT INTO IT (the manifest's structure and every kept line), so the words list names what the
        // model will hold and a column the person renamed or left out is already reflected. It is the engine's own, kept until the plan moves (its digest),
        // and it is NOT the decide loop's scratch: the loop replaces and disposes its own every round, and a words read that held that one would find it gone.
        // A plan Jev never decides (Free) has no loop at all, so this is the only place its words list could come from.
        private readonly SemaphoreSlim _startWordsScratchGate = new SemaphoreSlim(1, 1);
        private StartScratch _startWordsScratch;
        private string _startWordsScratchDigest;

        /// <summary>Drop the words preview (the plan was parked, discarded, built over, or moved). Never throws.</summary>
        private void DropStartWordsScratch()
        {
            StartScratch old;
            lock (_startPlanLock) { old = _startWordsScratch; _startWordsScratch = null; _startWordsScratchDigest = null; }
            try { old?.Dispose(); } catch { }
        }

        private async Task<StartScratch> StartWordsScratchAsync(bool rebuild)
        {
            await _startWordsScratchGate.WaitAsync().ConfigureAwait(false);
            try
            {
                StartPlanDoc doc;
                lock (_startPlanLock) doc = StartPlanStore.Copy(_startPlan);
                if (doc == null) throw new InvalidOperationException("There is no plan to write words for. Start one first.");
                var digest = StartPlanViewBuilder.Digest(doc);
                lock (_startPlanLock)
                    if (!rebuild && _startWordsScratch != null && string.Equals(_startWordsScratchDigest, digest, StringComparison.Ordinal)) return _startWordsScratch;
                DropStartWordsScratch();
                var host = new EngineStartScratchHost(this, () => CloneStartScratchForAsync(".words"));
                var built = await host.BuildAsync(doc, CancellationToken.None).ConfigureAwait(false);
                lock (_startPlanLock) { _startWordsScratch = built; _startWordsScratchDigest = digest; }
                return built;
            }
            finally { _startWordsScratchGate.Release(); }
        }

        /// <summary>Read the plan's PREVIEW model for the words (see above). A copy that went stale while the read waited is built again, once more at most.</summary>
        private async Task<T> ReadStartPreviewAsync<T>(Func<Model, T> read)
        {
            if (StartPlanPreviewReaderForTest != null) return await StartPlanPreviewReaderForTest.ReadAsync(read).ConfigureAwait(false);
            for (var attempt = 0; ; attempt++)
            {
                var scratch = await StartWordsScratchAsync(attempt > 0).ConfigureAwait(false);
                try { return await scratch.Session.ReadAsync(read).ConfigureAwait(false); }
                catch (Exception) when (attempt < 2) { /* the copy was replaced or retired while this read waited: build a new one */ }
            }
        }

        private async Task<StartPlanDoc> LiveStartPlanForWordsAsync()
        {
            var doc = LoadStartPlan() ?? throw new InvalidOperationException("There is no plan to write words for. Start one first.");
            StartPlanDoc copy;
            lock (_startPlanLock) copy = StartPlanStore.Copy(doc);
            var because = await StartPlanParkedReasonAsync(copy).ConfigureAwait(false);
            if (because != null) throw new InvalidOperationException(because);
            return doc;
        }

        public async Task<StartWordsPage> GetStartWordsAsync(int max, int offset)
        {
            var doc = await LiveStartPlanForWordsAsync().ConfigureAwait(false);
            StartPlanDoc snapshot;
            lock (_startPlanLock) snapshot = StartPlanStore.Copy(doc);
            var size = WordsPageSize(max);
            var read = await ReadStartPreviewAsync(m =>
            {
                var open = StartPlanWords.Open(snapshot, ReadWordsQueueItems(m, out _));
                var page = PageStartWords(m, open, size, offset);
                return (page, open.Count, StartPlanWords.PresentRefs(snapshot, m));
            }).ConfigureAwait(false);
            RefreshWordsTally(doc, read.Item2, read.Item3);
            return read.page;
        }

        /// <summary>Keep the plan's own count of words true: it is what both doors show beside the Words section.</summary>
        private void RefreshWordsTally(StartPlanDoc doc, int open, ISet<string> present)
        {
            lock (_startPlanLock)
            {
                if (!ReferenceEquals(_startPlan, doc)) return;
                var tally = StartPlanWords.Tally(doc, open, present);
                if (doc.Words != null && doc.Words.Open == tally.Open && doc.Words.Total == tally.Total) return;
                doc.Words = tally;
                doc.UpdatedUtc = DateTime.UtcNow;
                try { StartPlanStore.Write(doc); } catch { }
            }
        }

        /// <summary>One page of the open words, never more than <see cref="WordsPageChars"/> characters as the JSON goes on the
        /// wire (and never fewer than one item): an item carries its message and, the first time its object is on the page, the
        /// object's own facts (its formula, format, description and sibling names), cut the same way the open-model page cuts them.</summary>
        private static StartWordsPage PageStartWords(Model m, List<(WordsQueueItem Item, string Kind)> open, int max, int offset)
        {
            var total = open.Count;
            var from = Math.Max(0, Math.Min(offset, total));
            var shown = new List<StartWordItem>();
            var grounded = new HashSet<string>(StringComparer.Ordinal);
            StartWordsPage Page() => new StartWordsPage { Total = total, Offset = from, Items = shown };
            var used = JsonChars(new StartWordsPage { Total = total, Offset = from });
            for (var at = from; at < total && shown.Count < max; at++)
            {
                var (x, kind) = open[at];
                var key = x.ObjectRef ?? "";
                var fresh = !grounded.Contains(key);
                var item = new StartWordItem
                {
                    Ref = x.ObjectRef, Kind = kind,
                    Context = StartWordContext(CutForPage(x), fresh ? CutForPage(BuildGrounding(m, x.ObjectRef)) : null),
                };
                var cost = JsonChars(item) + 1;
                if (shown.Count > 0 && used + cost > WordsPageChars) break;
                used += cost;
                if (fresh) grounded.Add(key);
                shown.Add(item);
            }
            var page = Page();
            // The sum is an estimate of the whole; the page itself has the last word. Drop the last item until it fits.
            while (shown.Count > 1 && JsonChars(page) > WordsPageChars)
            {
                shown.RemoveAt(shown.Count - 1);
                page = Page();
            }
            return page;
        }

        private static string StartWordContext(WordsQueueItem x, GroundingBundle g)
        {
            var sb = new StringBuilder();
            sb.Append(x.Message);
            if (StartPlanWords.WordKindOfQueue(x) == StartPlanWords.DisplayName) sb.Append(" If the name it has is right, send keep_name for it instead of a new name.");
            if (g == null) return sb.Append(" (This object's details are on an earlier item of this page.)").ToString();
            sb.Append("\nObject: ").Append(g.Kind).Append(' ').Append(g.Name);
            if (!string.IsNullOrEmpty(g.Table) && !string.Equals(g.Table, g.Name, StringComparison.Ordinal)) sb.Append(" in table ").Append(g.Table);
            if (!string.IsNullOrEmpty(g.DataType)) sb.Append(", type ").Append(g.DataType);
            if (!string.IsNullOrEmpty(g.FormatString)) sb.Append("\nFormat now: ").Append(g.FormatString);
            if (!string.IsNullOrEmpty(g.ExistingDescription)) sb.Append("\nDescription now: ").Append(g.ExistingDescription);
            if (!string.IsNullOrEmpty(g.Expression)) sb.Append("\nFormula: ").Append(g.Expression);
            if (g.SiblingNames != null && g.SiblingNames.Length > 0) sb.Append("\nOther names beside it: ").Append(string.Join(", ", g.SiblingNames));
            if (g.Calendars != null && g.Calendars.Length > 0) sb.Append("\nCalendars: ").Append(string.Join(" | ", g.Calendars));
            return sb.ToString();
        }

        private bool _startWordsUnsaved;

        /// <summary>write_start_words, both doors. The whole write (the preview read, the check, the save, the kicks) holds the start-plan gate that the build, an
        /// edit, an approval and a discard hold, so a build can never take its copy of the plan while words are on their way into it, and words can never land in a plan
        /// a build has already taken (Sol, join 6, A4: the words were accepted, the build wrote the plan without them, and the restart retired the only saved copy).
        /// Whichever of the two comes second sees the other's result: the build refuses a plan that moved, and a words write over a built model is refused.</summary>
        public async Task<StartWordsResult> WriteStartWordsAsync(StartWord[] words, string origin)
        {
            GuardOutsideWrite(origin);
            await _startPlanGate.WaitAsync().ConfigureAwait(false);
            try { return await WriteStartWordsGatedAsync(words, origin).ConfigureAwait(false); }
            // The gated write stops the decide in flight before it looks at the words. A batch that changes nothing (every word refused), or one that throws,
            // must not leave the plan "deciding" with no loop (plan statement check 3, defect 1): the loop starts again here.
            finally { await EnsureStartDecideNotOrphanedAsync().ConfigureAwait(false); _startPlanGate.Release(); }
        }

        private async Task<StartWordsResult> WriteStartWordsGatedAsync(StartWord[] words, string origin)
        {
            var doc = await LiveStartPlanForWordsAsync().ConfigureAwait(false);
            if (words == null || words.Length == 0)
                return new StartWordsResult { Written = 0, View = StartPlanViewBuilder.Build(StartPlanStore.Copy(doc)) };
            StartPlanDoc snapshot;
            lock (_startPlanLock) snapshot = StartPlanStore.Copy(doc);

            // What the preview model says about each word, then what the document says: the first needs no lock, the second must
            // be decided with the lock held so two doors writing together lose nothing and cannot both take one name.
            var read = await ReadStartPreviewAsync(m => (
                Facts: StartPlanWords.ReadFacts(m, snapshot, words, FormulaTextIn),
                Queue: ReadWordsQueueItems(m, out _),
                Present: StartPlanWords.PresentRefs(snapshot, m)
            )).ConfigureAwait(false);

            // THE LOOP IS STOPPED BEFORE THE WORDS LAND, like an edit stops it: a round that is running holds a copy of the plan from before these words, and
            // would publish its lines over them (the words would be lost). It starts again below, over the plan that has them.
            await CancelStartDecideAsync().ConfigureAwait(false);

            (int Written, List<string> Refused, bool Changed) outcome;
            StartPlanDoc after;
            string because = null;
            lock (_startPlanLock)
            {
                if (!ReferenceEquals(_startPlan, doc)) throw new InvalidOperationException("The plan changed while the words were being checked. Read the words list again.");
                var digestBefore = StartPlanViewBuilder.Digest(doc);
                outcome = StartPlanWords.Apply(doc, read.Facts, doc.Decide?.Round ?? 0, IsAssistantOrigin(origin) ? StartDecidedBy.Assistant : StartDecidedBy.Person);
                if (outcome.Changed)
                {
                    if (!string.Equals(StartPlanViewBuilder.Digest(doc), digestBefore, StringComparison.Ordinal)) doc.Approval = null;   // any edit clears the approval (S1)
                    doc.Version++;
                    doc.UpdatedUtc = DateTime.UtcNow;
                }
                // The open count is the queue as the preview shows it, minus what the lines now speak for. An object a word was
                // just written for was found in the preview, so its line is present.
                var present = new HashSet<string>(read.Present, StringComparer.OrdinalIgnoreCase);
                foreach (var f in read.Facts.Where(f => f.Refusal == null)) present.Add(StartPlanWords.Back(doc, f.FoundRef));
                var open = StartPlanWords.Open(doc, read.Queue).Count;
                var tally = StartPlanWords.Tally(doc, open, present);
                var tallyMoved = doc.Words == null || doc.Words.Open != tally.Open || doc.Words.Total != tally.Total;
                doc.Words = tally;
                // A save that failed is tried again by the next call even when it has nothing new to add, so a retry of the same
                // words is not told "written" for a file that never got them.
                if (outcome.Changed || tallyMoved || _startWordsUnsaved)
                    try { StartPlanStore.Write(doc); _startWordsUnsaved = false; } catch (Exception ex) { because = ex.Message; _startWordsUnsaved = true; }
                after = StartPlanStore.Copy(doc);
            }
            if (outcome.Changed)
            {
                try { StartPlanWordsWritten?.Invoke(after.Id); } catch { }
                // Words reopen the work for what they name (S10): the loop runs again over the plan that has them (Pro, with a grant; anything else does nothing), and
                // the preview is made again. Both doors hear that the plan moved.
                try { await KickStartDecideOnPlanAsync().ConfigureAwait(false); }
                catch (Exception ex) { try { Console.Error.WriteLine("[start-plan] starting the decide after words failed: " + ex.Message); } catch { } }
                ScheduleStartPreview();
            }
            var view = StartPlanViewBuilder.Build(after, null);
            if (outcome.Changed) NotifyStartPlanChanged(view, origin);
            var result = new StartWordsResult { Written = outcome.Written, Refused = outcome.Refused, View = view };
            if (because != null)
            {
                // The words are in this session's plan only. Say they were not saved, and do not count them as written.
                result.Written = 0;
                result.Refused.Add("The words were not saved. The plan file could not be written: " + because + " Send them again once that is fixed.");
            }
            return result;
        }
    }
}
