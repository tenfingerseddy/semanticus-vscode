using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Semanticus.Analysis;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    // What a standard DOES to a model (design notes section 3). One pass, written once, used by both doors:
    //   - preview_standard runs the pass inside a rehearsal that is rolled back, so the counts and examples are exactly what
    //     apply would do and nothing is written;
    //   - apply_standard runs the same pass inside one MutateAsync, so it is one undo step.
    // Sure changes (a format for a known kind of measure, a folder, a key hidden, an id that should not add up) are made now.
    // Anything that needs words or could break a report built on the model (a rename, a description, other names, the AI
    // instructions) is NOT written: it comes back as a Plan task for the person's own assistant. The engine writes no words
    // (golden rule 1). Grades are absolute: nothing here changes how a grade is worked out.
    internal static class StandardApply
    {
        internal const int ExamplesPerPart = 3;
        private const int BriefObjects = 40;

        public static StandardWork Run(Model m, StandardSpec spec, IReadOnlyList<ReadinessFinding> findings, bool hasInstructions)
        {
            var w = new StandardWork();
            Checks(w, spec, findings);
            // The keys are hidden BEFORE the names and descriptions are counted: a hidden field is not renamed or described, so a count taken
            // first said 36 on the Plan row while the Names line said 32 once the keys were hidden. The part keeps its place in the list.
            KeysAndTotals(m, w, spec);
            var keysPart = w.Parts[w.Parts.Count - 1];
            w.Parts.RemoveAt(w.Parts.Count - 1);
            Names(m, w, spec);
            Formats(m, w, spec);
            Folders(m, w, spec);
            Descriptions(m, w, spec);
            Synonyms(w, spec, findings);
            w.Parts.Add(keysPart);
            AiInstructions(w, spec, hasInstructions);
            return w;
        }

        private static StandardPartResult Part(StandardWork w, string part, string setting)
        {
            var p = new StandardPartResult { Part = part, Setting = setting };
            w.Parts.Add(p);
            return p;
        }

        // ---- checks -------------------------------------------------------------------------------

        private static void Checks(StandardWork w, StandardSpec s, IReadOnlyList<ReadinessFinding> findings)
        {
            var level = StandardValues.Norm(s.Checks?.Level);
            var p = Part(w, StandardParts.Checks, level.Length == 0 ? "balanced" : level);
            var open = (findings ?? new ReadinessFinding[0]).Where(f => f != null && !f.Waived).ToList();
            var kept = open.Count(f => StandardChecksFilter.Keeps(s.Checks, f.RuleId, f.Severity));
            var off = open.Count - kept;
            p.Tasks = 0;   // The Plan raises these on its own; apply adds no row for them.
            var offIds = s.Checks?.Off ?? new string[0];
            var mustIds = s.Checks?.Must ?? new string[0];
            p.Note = open.Count == 0
                ? "No open checks on this model."
                : kept + " of " + open.Count + " open checks reach the Plan" + (off > 0 ? ", " + off + " are left off by this standard." : ".");
            var ex = new List<string>();
            if (offIds.Length > 0) ex.Add("Never a task: " + string.Join(", ", offIds.Take(4)));
            if (mustIds.Length > 0) ex.Add("Always a task: " + string.Join(", ", mustIds.Take(4)));
            p.Examples = ex.Take(ExamplesPerPart).ToArray();
        }

        // ---- names --------------------------------------------------------------------------------

        private static void Names(Model m, StandardWork w, StandardSpec s)
        {
            var mode = StandardValues.Norm(s.Names);
            var p = Part(w, StandardParts.Names, mode.Length == 0 ? "leave" : mode);
            if (mode == "" || mode == "leave") return;
            var noPrefix = mode == "title-case-no-prefix";
            var lines = new List<(string refId, string from, string to)>();
            foreach (var t in m.Tables.Where(t => !t.IsHidden))
            {
                var to = StandardNames.TitleCase(t.Name, noPrefix, isTable: true);
                if (to != t.Name && !NameTaken(m, t, to)) lines.Add((ObjectRefs.For(t), t.Name, to));
                foreach (var c in t.Columns.Where(c => !c.IsHidden && c.Type != ColumnType.RowNumber))
                {
                    var cto = StandardNames.TitleCase(c.Name, noPrefix, isTable: false);
                    if (cto != c.Name && !t.Columns.Any(o => !ReferenceEquals(o, c) && string.Equals(o.Name, cto, StringComparison.OrdinalIgnoreCase)))
                        lines.Add((ObjectRefs.For(c), c.Name, cto));
                }
                foreach (var me in t.Measures.Where(x => !x.IsHidden))
                {
                    var mto = StandardNames.TitleCase(me.Name, noPrefix, isTable: false);
                    if (mto != me.Name && !m.AllMeasures.Any(o => !ReferenceEquals(o, me) && string.Equals(o.Name, mto, StringComparison.OrdinalIgnoreCase)))
                        lines.Add((ObjectRefs.For(me), me.Name, mto));
                }
            }
            p.Tasks = lines.Count;
            p.Examples = lines.Take(ExamplesPerPart).Select(l => l.from + " to " + l.to).ToArray();
            if (lines.Count == 0) return;
            p.Note = "A rename can break a report built on the model, so these go to the Plan and are not made now.";
            w.Tasks.Add(new StandardPlanTask
            {
                Part = StandardParts.Names, Key = "standard/names",
                Words = "Rename " + lines.Count + (lines.Count == 1 ? " object" : " objects") + " to the standard (" + (noPrefix ? "title case, no prefix" : "title case") + ")",
                Brief = "The standard names objects in " + (noPrefix ? "title case with no Dim, Fact or tbl prefix" : "title case") + ". Rename these " + lines.Count
                        + " objects with rename_object. A rename can break a report built on this model, so check list_reports first and keep the old name working where a report uses it. Then say what you changed.\n"
                        + string.Join("\n", lines.Take(BriefObjects).Select(l => l.refId + " -> " + l.to))
                        + (lines.Count > BriefObjects ? "\n... and " + (lines.Count - BriefObjects) + " more. Ask get_standard and preview_standard for the rest." : ""),
                Count = lines.Count, Objects = lines.Take(BriefObjects).Select(l => l.refId).ToArray(),
            });
        }

        private static bool NameTaken(Model m, Table t, string to) =>
            m.Tables.Any(o => !ReferenceEquals(o, t) && string.Equals(o.Name, to, StringComparison.OrdinalIgnoreCase));

        // ---- formats ------------------------------------------------------------------------------

        private static void Formats(Model m, StandardWork w, StandardSpec s)
        {
            var wanted = (s.Formats ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Value) && StandardValues.Norm(kv.Value) != "leave")
                .ToDictionary(kv => StandardValues.Norm(kv.Key), kv => kv.Value, StringComparer.Ordinal);
            var p = Part(w, StandardParts.Formats, wanted.Count == 0 ? "leave"
                : string.Join(", ", wanted.Select(kv => kv.Key + " " + kv.Value)));
            if (wanted.Count == 0) return;

            var changed = 0; var unsure = 0;
            var ex = new List<string>();
            foreach (var me in m.AllMeasures.ToList())
            {
                var kind = StandardMeasureKinds.KindOf(me);
                if (kind == null) { if (!me.IsHidden && string.IsNullOrEmpty(me.FormatString) && string.IsNullOrEmpty(me.FormatStringExpression)) unsure++; continue; }
                if (!wanted.TryGetValue(kind, out var target)) continue;
                if (me.FormatString == target || ShowsNumbersTheSameWay(me.FormatString, target)) continue;
                // A blank format is only ever filled by the format fill rule (a proved whole number, the whole-number format).
                if (string.IsNullOrEmpty(me.FormatString) && kind == "count" && !FormatFill.MayFill(me, target)) { unsure++; continue; }
                var before = string.IsNullOrEmpty(me.FormatString) ? "no format" : me.FormatString;
                me.FormatString = target;
                changed++;
                w.Applied.Add("Set the " + kind + " format " + target + " on [" + me.Name + "]");
                if (ex.Count < ExamplesPerPart) ex.Add(me.Name + ": " + before + " to " + target);
            }
            p.ChangesNow = changed;
            p.Examples = ex.ToArray();
            if (unsure > 0) p.Note = unsure + (unsure == 1 ? " measure has no format and Semanticus cannot tell what it shows, so it is left for you." : " measures have no format and Semanticus cannot tell what they show, so they are left for you.");
        }

        // A format the person wrote with more sections than the standard names ("0.0%;-0.0%;0.0%" against "0.0%") already shows a
        // positive number the way the standard asks, and keeps their own negative and zero. Rewriting it is churn: nothing the person
        // sees changes, and the three-part form is the one the PERCENTAGE_FORMATTING check itself asks for. Compare the sections the
        // standard names; the extra sections are the person's.
        internal static bool ShowsNumbersTheSameWay(string current, string target)
        {
            if (string.IsNullOrEmpty(current) || string.IsNullOrEmpty(target)) return false;
            var have = FormatSections(current); var want = FormatSections(target);
            if (have.Count < want.Count) return false;
            for (var i = 0; i < want.Count; i++)
                if (!string.Equals(have[i], want[i], StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        // Split a format string on ';' outside quotes, escapes and [brackets] (a literal ";" in text is not a section break).
        private static List<string> FormatSections(string f)
        {
            var parts = new List<string>(); var sb = new StringBuilder();
            bool quote = false, bracket = false;
            for (var i = 0; i < f.Length; i++)
            {
                var c = f[i];
                if (c == '\\' && i + 1 < f.Length) { sb.Append(c).Append(f[++i]); continue; }
                if (c == '"' && !bracket) quote = !quote;
                else if (c == '[' && !quote) bracket = true;
                else if (c == ']' && !quote) bracket = false;
                if (c == ';' && !quote && !bracket) { parts.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
            parts.Add(sb.ToString());
            return parts;
        }

        // ---- folders ------------------------------------------------------------------------------

        private static void Folders(Model m, StandardWork w, StandardSpec s)
        {
            StandardValues.TryFolders(s.Folders, out var on, out var over, out _);
            var p = Part(w, StandardParts.Folders, !on ? "none" : over == 0 ? "by subject, always" : "by subject, over " + over);
            if (!on) return;
            var loose = m.AllMeasures.Where(me => !me.IsHidden && string.IsNullOrWhiteSpace(me.DisplayFolder)).ToList();
            if (loose.Count == 0 || (over > 0 && loose.Count <= over)) { if (loose.Count > 0) p.Note = loose.Count + " measures have no folder, which is " + over + " or fewer, so none are made."; return; }
            var ex = new List<string>();
            var changed = 0;
            foreach (var me in loose)
            {
                var subject = StandardMeasureKinds.SubjectOf(m, me);
                if (string.IsNullOrWhiteSpace(subject)) continue;
                me.DisplayFolder = subject;
                changed++;
                w.Applied.Add("Put [" + me.Name + "] in the folder " + subject);
                if (ex.Count < ExamplesPerPart) ex.Add(me.Name + " to folder " + subject);
            }
            p.ChangesNow = changed;
            p.Examples = ex.ToArray();
        }

        // ---- descriptions -------------------------------------------------------------------------

        private static void Descriptions(Model m, StandardWork w, StandardSpec s)
        {
            var mode = StandardValues.Norm(s.Descriptions);
            var p = Part(w, StandardParts.Descriptions, mode.Length == 0 ? "none" : mode);
            if (mode == "" || mode == "none") return;
            var objs = new List<(string refId, string label)>();
            foreach (var t in m.Tables.Where(t => !t.IsHidden))
            {
                if ((mode == "measures, tables" || mode == "all-visible") && string.IsNullOrWhiteSpace(t.Description)) objs.Add((ObjectRefs.For(t), "table " + t.Name));
                foreach (var me in t.Measures.Where(x => !x.IsHidden && string.IsNullOrWhiteSpace(x.Description))) objs.Add((ObjectRefs.For(me), "measure " + me.Name));
                if (mode == "all-visible")
                    foreach (var c in t.Columns.Where(x => !x.IsHidden && x.Type != ColumnType.RowNumber && string.IsNullOrWhiteSpace(x.Description))) objs.Add((ObjectRefs.For(c), "column " + t.Name + "." + c.Name));
            }
            p.Tasks = objs.Count;
            p.Examples = objs.Take(ExamplesPerPart).Select(o => o.label + ": no description").ToArray();
            if (objs.Count == 0) return;
            w.Tasks.Add(new StandardPlanTask
            {
                Part = StandardParts.Descriptions, Key = "standard/descriptions",
                Words = "Write " + objs.Count + (objs.Count == 1 ? " description" : " descriptions") + " the standard asks for",
                Brief = "The standard asks for descriptions on " + mode + ". Write a short plain description for each of these " + objs.Count + " objects and set it with set_description. Read each object first (get_object); say what it is and how it is used in business words. Then say what you wrote.\n"
                        + string.Join("\n", objs.Take(BriefObjects).Select(o => o.refId))
                        + (objs.Count > BriefObjects ? "\n... and " + (objs.Count - BriefObjects) + " more. Ask preview_standard for the rest." : ""),
                Count = objs.Count, Objects = objs.Take(BriefObjects).Select(o => o.refId).ToArray(),
            });
        }

        // ---- synonyms -----------------------------------------------------------------------------

        private static void Synonyms(StandardWork w, StandardSpec s, IReadOnlyList<ReadinessFinding> findings)
        {
            StandardValues.TrySynonyms(s.Synonyms, out var count, out var allVisible, out _);
            var p = Part(w, StandardParts.Synonyms, count == 0 ? "none" : count + ", " + (allVisible ? "all visible" : "measures and tables"));
            if (count == 0) return;
            var rows = (findings ?? new ReadinessFinding[0])
                .Where(f => f != null && !f.Waived && (f.RuleId ?? "").StartsWith("SYN-", StringComparison.OrdinalIgnoreCase))
                .Where(f => allVisible || (f.ObjectRef ?? "").StartsWith("measure:", StringComparison.OrdinalIgnoreCase) || (f.ObjectRef ?? "").StartsWith("table:", StringComparison.OrdinalIgnoreCase))
                .ToList();
            p.Tasks = rows.Count;
            p.Examples = rows.Take(ExamplesPerPart).Select(f => (f.ObjectName ?? f.ObjectRef) + ": needs " + count + " other " + (count == 1 ? "name" : "names")).ToArray();
            if (rows.Count == 0) return;
            w.Tasks.Add(new StandardPlanTask
            {
                Part = StandardParts.Synonyms, Key = "standard/synonyms",
                Words = "Add other names to " + rows.Count + (rows.Count == 1 ? " object" : " objects") + " the standard asks for",
                Brief = "The standard asks for " + count + " other " + (count == 1 ? "name" : "names") + " on " + (allVisible ? "every visible field" : "measures and tables") + ". Add them with write_words or set_synonyms: the words a person would really use to ask for each one. Then say what you added.\n"
                        + string.Join("\n", rows.Take(BriefObjects).Select(f => f.ObjectRef)),
                Count = rows.Count, Objects = rows.Take(BriefObjects).Select(f => f.ObjectRef).ToArray(),
            });
        }

        // ---- keys and totals ----------------------------------------------------------------------

        private static void KeysAndTotals(Model m, StandardWork w, StandardSpec s)
        {
            var mode = StandardValues.Norm(s.KeysAndTotals);
            var p = Part(w, StandardParts.KeysAndTotals, mode.Length == 0 ? "leave" : mode);
            if (mode == "" || mode == "leave") return;
            var hiddenBefore = new HashSet<Column>(m.AllColumns.Where(c => c.IsHidden));
            var applied = SafeFixes.Apply(m, new HashSet<string>(StringComparer.Ordinal) { "VIS-FK", "FMT-SUMMARIZE", "SUMMARIZE-DIMENSION" });
            var extra = new List<string>();
            foreach (var t in m.Tables)
                foreach (var c in t.Columns.Where(c => !c.IsHidden && c.Type != ColumnType.RowNumber).ToList())
                {
                    var key = c.IsKey;
                    var tech = mode.StartsWith("hide keys and technical", StringComparison.Ordinal) && StandardMeasureKinds.IsTechnicalName(c.Name);
                    if (!key && !tech) continue;
                    c.IsHidden = true;
                    extra.Add((key ? "Hid the key column [" : "Hid the technical column [") + t.Name + "].[" + c.Name + "]");
                }
            // A hidden column that nothing sorts by or groups is what ISAVAILABLEINMDX_FALSE_NONATTRIBUTE_COLUMNS asks to switch off. Hiding
            // one without it made the standard raise the finding itself ("Health: 4 new issues" on a change that improved the model). So the
            // columns this pass hid get the write that rule asks for, in the same undo step. It adds no line of its own: it is part of the hide.
            foreach (var c in m.AllColumns.Where(c => c.IsHidden && !hiddenBefore.Contains(c)))
                if (c.IsAvailableInMDX && !c.UsedInSortBy.Any() && !c.UsedInHierarchies.Any() && !c.UsedInVariations.Any() && c.SortByColumn == null)
                    c.IsAvailableInMDX = false;
            w.Applied.AddRange(applied);
            w.Applied.AddRange(extra);
            p.ChangesNow = applied.Count + extra.Count;
            p.Examples = applied.Concat(extra).Take(ExamplesPerPart).ToArray();
        }

        // ---- the AI instructions ------------------------------------------------------------------

        private static void AiInstructions(StandardWork w, StandardSpec s, bool hasInstructions)
        {
            var file = (s.AiInstructions ?? string.Empty).Trim();
            var none = file.Length == 0 || StandardValues.Norm(file) == "none";
            var p = Part(w, StandardParts.AiInstructions, none ? "none" : file);
            if (none) return;
            if (hasInstructions) { p.Note = "This model already has AI instructions, so none are asked for."; return; }
            p.Tasks = 1;
            p.Examples = new[] { "The model has no AI instructions. The template is " + file + "." };
            w.Tasks.Add(new StandardPlanTask
            {
                Part = StandardParts.AiInstructions, Key = "standard/ai-instructions",
                Words = "Write the AI instructions from the standard's template",
                Brief = "The standard has a template for AI instructions: " + file + ". Read it, fill in its blanks ({fiscal year start}, {currency}) from this model (read get_model_facts and the date table), and set the result with set_ai_instructions. Keep it under 10,000 characters. Then say what you wrote.",
                Count = 1,
            });
        }
    }

    /// <summary>Which findings the standard lets through to the Plan. The grade is never filtered: this is applied to what becomes a
    /// task, after the scan, and nowhere else.</summary>
    public static class StandardChecksFilter
    {
        public static bool Keeps(StandardChecks c, string ruleId, string severity)
        {
            if (c == null) return true;
            if (Has(c.Off, ruleId)) return false;
            if (Has(c.Must, ruleId)) return true;
            // light: only checks that change answers (High and Critical). balanced and strict: every check, as shipped.
            if (StandardValues.Norm(c.Level) == "light")
                return string.Equals(severity, "High", StringComparison.OrdinalIgnoreCase) || string.Equals(severity, "Critical", StringComparison.OrdinalIgnoreCase);
            return true;
        }

        /// <summary>A rule the standard turns off: never a task, never on the Plan. Best-practice rules only have this (they carry no AI severity to take a level from).</summary>
        public static bool IsOff(StandardChecks c, string ruleId) => c != null && Has(c.Off, ruleId);

        private static bool Has(string[] ids, string id) =>
            ids != null && ids.Any(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Title case that a person would accept: words split from camel case and underscores, small words kept low, acronyms and
    /// names with their own capitals left alone.</summary>
    public static class StandardNames
    {
        private static readonly HashSet<string> Small = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "a", "an", "and", "as", "at", "but", "by", "for", "in", "of", "on", "or", "per", "the", "to", "vs" };
        private static readonly Regex Prefix = new Regex("^(dim|fact|tbl|vw|d|f)[ _-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex CamelEdge = new Regex("(?<=[a-z0-9])(?=[A-Z][a-z])|(?<=[a-z])(?=[A-Z]{2,})", RegexOptions.CultureInvariant);

        public static string TitleCase(string name, bool noPrefix, bool isTable)
        {
            if (string.IsNullOrWhiteSpace(name)) return name;
            var text = name.Trim().Replace('_', ' ');
            // "DimCustomer" and "dim_customer" both become words first, so the prefix test sees "Dim Customer".
            text = CamelEdge.Replace(text, " ");
            text = Regex.Replace(text, " {2,}", " ");
            if (noPrefix && isTable)
            {
                var stripped = Prefix.Replace(text, string.Empty);
                if (stripped.Length > 0) text = stripped;
            }
            var words = text.Split(' ');
            var sb = new StringBuilder();
            for (var i = 0; i < words.Length; i++)
            {
                var w = words[i];
                if (w.Length == 0) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(Word(w, first: sb.Length == 0 || i == words.Length - 1));
            }
            var result = sb.ToString();
            return result.Length == 0 ? name : result;
        }

        private static string Word(string w, bool first)
        {
            if (!char.IsLetter(w[0])) return w;                                  // "%", "#", "2024", "(USD)"
            if (w.Skip(1).Any(char.IsUpper)) return w;                           // YoY, USD, iPhone: its own capitals
            if (!first && Small.Contains(w)) return w.ToLowerInvariant();
            return char.ToUpperInvariant(w[0]) + w.Substring(1);
        }
    }

    /// <summary>What kind of number a measure shows, only where the model proves it, and which subject it is about.</summary>
    internal static class StandardMeasureKinds
    {
        private static readonly Regex SumOne = new Regex(@"^\s*SUM\s*\(\s*(?:'(?<t>[^']+)'|(?<t>[A-Za-z_][\w ]*?))\s*\[(?<c>[^\]]+)\]\s*\)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex TableRef = new Regex(@"(?:'(?<t>[^']+)'|(?<t>[A-Za-z_][\w]*))\s*\[", RegexOptions.CultureInvariant);
        private static readonly Regex Technical = new Regex(@"(^|[ _])(sk|bk|rowversion|etl\w*|batchid|loadid|loaddate)$|guid$|^_",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static bool IsTechnicalName(string name) => !string.IsNullOrEmpty(name) && Technical.IsMatch(name);

        private static bool IsMoneyFormat(string f) =>
            !string.IsNullOrEmpty(f) && (f.IndexOfAny(new[] { '$', '€', '£', '¥' }) >= 0 || f.StartsWith("Currency", StringComparison.OrdinalIgnoreCase));

        // A % outside quotes and not escaped multiplies by 100; inside quotes or after a backslash it is only a character.
        private static bool HasPercentOperator(string f) => PercentSigns(f).op;
        private static bool HasLiteralPercent(string f) => PercentSigns(f).literal;

        private static (bool op, bool literal) PercentSigns(string f)
        {
            bool op = false, literal = false, quoted = false;
            for (var i = 0; i < (f ?? "").Length; i++)
            {
                var c = f[i];
                if (c == '"') quoted = !quoted;
                else if (c == (char)92 && !quoted) { if (i + 1 < f.Length && f[i + 1] == '%') literal = true; i++; }
                else if (c == '%') { if (quoted) literal = true; else op = true; }
            }
            return (op, literal);
        }

        /// <summary>money | percent | count, or null when the model does not prove it. A format string expression (a dynamic format) is never classified.</summary>
        public static string KindOf(Measure me)
        {
            if (!string.IsNullOrEmpty(me.FormatStringExpression)) return null;
            var f = me.FormatString ?? string.Empty;
            if (HasPercentOperator(f)) return "percent";
            if (HasLiteralPercent(f)) return null;   // 0.0"%" shows 50 as 50.0%: the values are already percent points, so the standard's 0.0% would show 5000.0%
            if (IsMoneyFormat(f)) return "money";
            if (f.Length == 0)
            {
                // A blank format: money only when it sums one column that is formatted as money; count only when a count is proved.
                var sum = SumOne.Match(me.Expression ?? string.Empty);
                if (sum.Success && me.Model.Tables.Contains(sum.Groups["t"].Value.Trim()))
                {
                    var t = me.Model.Tables[sum.Groups["t"].Value.Trim()];
                    var c = t.Columns.Contains(sum.Groups["c"].Value) ? t.Columns[sum.Groups["c"].Value] : null;
                    if (c != null && IsMoneyFormat(c.FormatString)) return "money";
                }
                return FormatFill.ProvenWholeNumber(me) != null ? "count" : null;
            }
            return FormatFill.IsWholeNumberFormat(f) && FormatFill.ProvenWholeNumber(me) != null ? "count" : null;
        }

        /// <summary>The table a measure is about: the first model table its formula reads a column from, else its own table.</summary>
        public static string SubjectOf(Model m, Measure me)
        {
            foreach (Match x in TableRef.Matches(me.Expression ?? string.Empty))
            {
                var name = x.Groups["t"].Value.Trim();
                if (m.Tables.Contains(name)) return m.Tables[name].Name;
            }
            return me.Table?.Name;
        }
    }
}
