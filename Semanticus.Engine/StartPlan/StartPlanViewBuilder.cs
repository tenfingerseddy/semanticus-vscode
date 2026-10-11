// Builds what both doors render from the stored document (contract section 3). A pure function of the document plus two
// facts the engine alone knows (why the plan is parked, whether the model moved), so the Studio and the assistant get
// the same JSON and the digest can be recomputed by anyone.
//
// THE DIGEST covers everything the person approves: where the model goes, the source, the structure, every kept
// line (its object, its value, who decided it) and the pins and answers. It leaves out what is not the plan itself
// (the approval, the decide progress, the preview's numbers, timestamps, line ids), so any edit moves it and a
// round that changes nothing does not (S1: an edit clears the approval).
//
// IT ALSO COVERS what the plan lets Jev and the build use that the person has to know about: whether facts from a sample of
// rows are on (a sample read after the person looked moves it, so a grant made on the old digest is refused as stale), whether
// they chose to carry on without Jev, and each kept line's Args, because the executor writes from them (a synonyms line's
// replaced list) and an approval must cover everything the build will write.

using System;
using Semanticus.Analysis;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Semanticus.Engine.StartPlan
{
    internal static class StartPlanViewBuilder
    {
        public static StartPlanView Empty() => new StartPlanView
        {
            Exists = false,
            CanBuild = false,
            BlockedBecause = "There is no plan yet.",
        };

        /// <param name="parkedBecause">Plain words for why the plan is parked (another model is open, or none), or null.</param>
        /// <param name="builtNow">True when the engine saw the plan's model holding what the plan built (it has tables). Only the engine can look at the model.</param>
        public static StartPlanView Build(StartPlanDoc doc, string parkedBecause = null, bool builtNow = false)
        {
            if (doc == null) return Empty();
            var manifest = doc.Manifest ?? new StructureManifest();
            var digest = Digest(doc);
            var view = new StartPlanView
            {
                Exists = true,
                Id = doc.Id,
                Destination = doc.Destination,
                Source = doc.Source,
                Decide = doc.Decide,
                Words = doc.Words,
                Digest = digest,
                Approval = doc.Approval,
                SampleFacts = doc.SampleFacts,
                Sample = doc.Sample,
                JevDeclined = doc.JevDeclined,
                Built = parkedBecause == null && doc.BuiltUtc != null && builtNow,
                Grades = GradesOf(doc, digest),
            };

            foreach (var id in StartSectionId.All)
                view.Sections.Add(new StartSection { Id = id, Title = TitleOf(id) });
            SectionOf(view, StartSectionId.Tables).Groups.AddRange(TableGroups(manifest, doc));
            SectionOf(view, StartSectionId.Links).Groups.AddRange(LinkGroups(manifest));
            view.Links = BuiltLinks(manifest).Select(l => new StartLinkEdge
            {
                From = l.FromTable, To = l.ToTable, Active = l.Active, Id = "relationship:" + (l.Identity ?? StartPlanEditing.LinkText(l)),
                Note = l.Guessed ? LinkGuesser.Why(l) : l.Note ?? "The source declares this link.",
            }).ToList();
            SectionOf(view, StartSectionId.Measures).Groups.AddRange(MeasureGroups(manifest, doc));
            foreach (var line in doc.Lines ?? new List<StartLine>())
            {
                // Every line the page can press has an id (UAT-J7-S2-007: lines with none all shared one open state, so opening one opened every line).
                if (line.Item != null && string.IsNullOrEmpty(line.Item.Id)) line.Item.Id = ShownId("ln", line.Item.Kind, line.Item.ObjectRef, line.Item.Target);
                var section = SectionOf(view, SectionFor(line));
                var table = TableOf(line.Item?.ObjectRef);
                var group = section.Groups.FirstOrDefault(g => string.Equals(g.Table, table, StringComparison.Ordinal));
                if (group == null) { group = new StartGroup { Table = table }; section.Groups.Add(group); }
                group.Lines.Add(line);
            }
            foreach (var section in view.Sections)
            {
                foreach (var l in section.Groups.SelectMany(g => g.Lines))
                    Count(section.Counts, l.DecidedBy);
                section.Summary = Summarize(section);
            }

            view.NeedsYou.AddRange(Needs(doc, manifest));
            view.NeedsYou.AddRange(StartPlanDecide.NeedsYou(doc));   // lane sf-decide: unsure picks, unsure lines, lines Jev kept changing its mind about
            (view.CanBuild, view.BlockedBecause) = Gate(doc, digest, parkedBecause);
            if (view.Built)
            {
                view.CanBuild = false;
                view.BlockedBecause = "This plan was built into " + (doc.Destination?.Name ?? "the model") + ". Undo the build to change the plan and build it again.";
            }
            return view;
        }

        // ---- the digest ---------------------------------------------------------------------------------------

        public static string Digest(StartPlanDoc doc)
        {
            if (doc == null) return null;
            var body = new
            {
                doc.Destination,
                doc.Source,
                doc.IdentitySeed,
                doc.SampleFacts,
                SampleRefused = (doc.SampleRefused ?? new List<string>()).Select(DisclosedScope.Norm).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                doc.JevDeclined,
                Manifest = doc.Manifest,
                Lines = (doc.Lines ?? new List<StartLine>()).Where(l => l.Kept)
                    .Select(l => new
                    {
                        l.Item?.Kind, l.Item?.ObjectRef, l.Item?.Target, l.Item?.After, l.Item?.Source,
                        l.DecidedBy, l.Word,
                        Args = (l.Item?.Args ?? new Dictionary<string, string>()).OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => a.Key + "=" + a.Value).ToList(),
                    })
                    .OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.ObjectRef, StringComparer.Ordinal)
                    .ThenBy(x => x.Target, StringComparer.Ordinal).ToList(),
                Pins = (doc.Pins ?? new Dictionary<string, string>()).OrderBy(p => p.Key, StringComparer.Ordinal).ToList(),
                Answers = (doc.Answers ?? new Dictionary<string, string>()).OrderBy(p => p.Key, StringComparer.Ordinal).ToList(),
            };
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body)));
            return Convert.ToHexString(bytes).Substring(0, 32).ToLowerInvariant();
        }

        // ---- the grades ---------------------------------------------------------------------------------------

        /// <summary>The grades the preview made, as numbers and letters. "After the build" is PROVISIONAL until the decide has settled (stable, or off for a plan
        /// Jev never decides) AND the preview was made on the plan as it is now: before that it is what the plan would build right now, and it can still move, so
        /// the page must not show it as the answer. The empty model has nothing to grade, so "now" is null until something is built.</summary>
        internal static StartGrades GradesOf(StartPlanDoc doc, string digest)
        {
            var p = doc.Preview;
            var settled = doc.Decide == null || doc.Decide.State == StartDecideState.Stable || doc.Decide.State == StartDecideState.Off;
            var current = p != null && !string.IsNullOrEmpty(p.PlanDigest) && string.Equals(p.PlanDigest, digest, StringComparison.Ordinal);
            return new StartGrades
            {
                Now = null,
                AfterBuild = p?.AiReadiness,
                AfterBuildGrade = p?.AiReadinessGrade,
                AfterBuildBestPractice = p?.BestPractice,
                AfterBuildBestPracticeOpen = p?.BestPracticeOpen,
                AfterBuildProvisional = !(settled && current),
            };
        }

        // ---- the gate -----------------------------------------------------------------------------------------

        private static (bool, string) Gate(StartPlanDoc doc, string digest, string parkedBecause)
        {
            if (!string.IsNullOrEmpty(parkedBecause)) return (false, parkedBecause);
            if (string.Equals(doc.Decide?.State, StartDecideState.Deciding, StringComparison.Ordinal))
                return (false, "Jev is still working on the plan. It will be ready to approve when it stops.");
            if (doc.Approval == null || string.IsNullOrEmpty(doc.Approval.Digest))
                return (false, "You have not approved this plan yet.");
            if (!string.Equals(doc.Approval.Digest, digest, StringComparison.Ordinal))
                return (false, "The plan changed after you approved it. Look it over and approve it again.");
            if (doc.Preview == null || string.IsNullOrEmpty(doc.Preview.ProjectionDigest) || !string.Equals(doc.Preview.PlanDigest, digest, StringComparison.Ordinal))
                return (false, "Still working out what the model will look like. It will be ready to build in a moment.");
            return (true, null);
        }

        // ---- needs you ----------------------------------------------------------------------------------------

        private static IEnumerable<StartNeed> Needs(StartPlanDoc doc, StructureManifest manifest)
        {
            // The sample of rows (K1): while it is being read the plan will change when it lands, and when it could not be read the plan is made from names alone.
            // Said in plain words, as a line, never as an error.
            if (string.Equals(doc.Sample?.State, StartSampleStatus.Reading, StringComparison.Ordinal))
                yield return new StartNeed
                {
                    Id = "sample", Kind = "sample_reading",
                    Text = "Reading a sample of the rows in " + (doc.Source?.Database ?? "the source") + ", so counts and patterns can guide the choices. Rows never leave your computer. "
                           + "The plan will update when it is done.",
                };
            else if (string.Equals(doc.Sample?.State, StartSampleStatus.Refused, StringComparison.Ordinal))
                yield return new StartNeed
                {
                    Id = "sample", Kind = "sample_refused",
                    Text = "A sample of the rows was not read" + (string.IsNullOrWhiteSpace(doc.Sample.Reason) ? "." : ": " + doc.Sample.Reason.Trim().TrimEnd('.') + ".")
                           + " The plan is made from the table and column names alone, so some choices may be less sure."
                           // Said plainly where it matters most: a link guessed with no rows to check it (plan statement check 5, P3).
                           + (manifest.Links.Count(l => l.Guessed && !l.Dropped) is var guessed and > 0
                               ? $" {guessed} {(guessed == 1 ? "link was" : "links were")} guessed from the names alone. Check {(guessed == 1 ? "it" : "each one")} before you build."
                               : ""),
                };
            // The fiscal year the dates suggest (design: a "You" question). Asked while the plan has no fiscal year end of its own and the person has not answered.
            if (!string.IsNullOrEmpty(doc.Sample?.FiscalYearEnd) && string.IsNullOrEmpty(manifest.DateTable?.FiscalYearEnd)
                && !string.IsNullOrWhiteSpace(manifest.DateTable?.Name) && !doc.Answers.ContainsKey("fiscal_year"))
                yield return new StartNeed
                {
                    Id = "fiscal_year", Kind = "fiscal_year",
                    Text = "The dates in " + manifest.DateTable.Name + " look like a fiscal year that ends on " + doc.Sample.FiscalYearEnd
                           + " (month and day). Year to date measures follow it. Is that right, or is it the calendar year?",
                    Options = new[] { doc.Sample.FiscalYearEnd, "calendar" },
                    Recommended = doc.Sample.FiscalYearEnd,
                };
            if (string.Equals(doc.Decide?.State, StartDecideState.WaitingGrant, StringComparison.Ordinal) && !doc.JevDeclined)
                yield return new StartNeed
                {
                    Id = "grant", Kind = "grant",
                    Text = "Jev needs your permission to see the tables of " + (doc.Source?.Database ?? "this source")
                           + " on " + (doc.Source?.Server ?? "this server") + " before it decides anything. " + GrantScopeWords(doc, manifest),
                    Options = new[] { "Allow", "Not now" },
                    Recommended = "Allow",
                };
            else if (doc.JevDeclined && doc.Decide != null && !string.Equals(doc.Decide.State, StartDecideState.Stable, StringComparison.Ordinal))
                yield return new StartNeed
                {
                    Id = "grant", Kind = "grant_declined",
                    Text = "You chose to carry on without Jev. The plan is made the way a free plan is, with your assistant's words. You can allow Jev later.",
                    Options = new[] { "Allow" },
                    Recommended = "Allow",
                };
            foreach (var link in manifest.Links.Where(l => l.Unresolved))
                yield return new StartNeed
                {
                    Id = "composite:" + link.Identity, Kind = "composite_key",
                    Text = link.Note ?? ($"{link.FromTable} joins {link.ToTable} on more than one column ({string.Join(", ", link.FromColumns)}). "
                                         + "A link on two columns is not built. It needs one combined key column."),
                };
        }

        /// <summary>What a grant would let Jev see, in plain words: the tables (names, columns and settings), and whether counts and patterns from a sample of their rows go too, and for which
        /// tables (UAT-J7-S2-002: the band named only the source). Never rows. The same scope the permission dialog lists and the grant is bound to.</summary>
        internal static string GrantScopeWords(StartPlanDoc doc, StructureManifest manifest)
        {
            var included = manifest.Tables.Where(t => t.Include).ToList();
            string Names(IEnumerable<string> names)
            {
                var list = names.ToList();
                return list.Count <= 6 ? string.Join(", ", list) : string.Join(", ", list.Take(6)) + " and " + (list.Count - 6) + " more";
            }
            var refused = new HashSet<string>((doc.SampleRefused ?? new List<string>()).Select(DisclosedScope.Norm), StringComparer.Ordinal);
            var sampled = included.Where(t => !refused.Contains(DisclosedScope.Norm(DisclosedScope.SourceNameOf(t)))).ToList();
            var left = included.Where(t => refused.Contains(DisclosedScope.Norm(DisclosedScope.SourceNameOf(t)))).ToList();
            var scope = "It would see the names, columns and settings of " + (included.Count == 0 ? "no tables" : Names(included.Select(t => t.Name))) + ".";
            string sample;
            if (string.Equals(doc.Sample?.State, StartSampleStatus.Reading, StringComparison.Ordinal))
                sample = "A sample of the rows is being read on your computer; Jev would see only counts and patterns from it, never the rows.";
            else if (!doc.SampleFacts)
                sample = "No sample of the rows was read, so it would see names and settings only.";
            else if (sampled.Count == 0)
                sample = "It would see no sample of the rows, because you refused it for every table.";
            else
                sample = "It would also see counts and patterns from a sample of the rows in " + Names(sampled.Select(t => t.Name)) + ", never the rows themselves."
                         + (left.Count > 0 ? " You refused the sample for " + Names(left.Select(t => t.Name)) + "." : "");
            return scope + " " + sample;
        }

        // ---- sections -----------------------------------------------------------------------------------------

        private static string TitleOf(string id) => id switch
        {
            StartSectionId.Tables => "Tables and columns",
            StartSectionId.Links => "Links and dates",
            StartSectionId.Measures => "Measures",
            StartSectionId.Words => "Words for Copilot",
            _ => "Best practice",
        };

        private static StartSection SectionOf(StartPlanView v, string id) => v.Sections.First(s => s.Id == id);

        private static string SectionFor(StartLine l)
        {
            var kind = l.Item?.Kind ?? "";
            if (l.Word != null) return StartSectionId.Words;
            switch (kind)
            {
                case "set_description":
                case "set_synonyms":
                case "create_linguistic_schema":
                case "set_display_name":
                    return StartSectionId.Words;
                case "set_relationship_crossfilter":
                case "mark_date_table":
                case "create_relationship":
                    return StartSectionId.Links;
                case "set_measure_format":
                case "set_dax":
                case "create_measure":
                    return StartSectionId.Measures;
                case "bpa_fix":
                    return StartSectionId.BestPractice;
                default:
                    return string.Equals(l.Item?.Source, "bpa", StringComparison.Ordinal) ? StartSectionId.BestPractice : StartSectionId.Tables;
            }
        }

        /// <summary>The table part of a ref such as "column:Sales/Qty" or "measure:Sales/Total", or null (model-wide).</summary>
        internal static string TableOf(string objectRef)
        {
            if (string.IsNullOrEmpty(objectRef)) return null;
            var colon = objectRef.IndexOf(':');
            var rest = colon < 0 ? objectRef : objectRef.Substring(colon + 1);
            if (objectRef.StartsWith("relationship:", StringComparison.Ordinal) || objectRef.StartsWith("model", StringComparison.Ordinal)) return null;
            var slash = rest.IndexOf('/');
            return slash < 0 ? rest : rest.Substring(0, slash);
        }

        private static void Count(StartCounts c, string by)
        {
            switch (by)
            {
                case StartDecidedBy.Jev: c.Jev++; break;
                case StartDecidedBy.Assistant: c.Assistant++; break;
                case StartDecidedBy.Person: c.You++; break;
                default: c.Rule++; break;
            }
        }

        private static string Summarize(StartSection s)
        {
            var n = s.Groups.SelectMany(g => g.Lines).Count();
            if (n == 0) return "Nothing here yet.";
            return n == 1 ? "1 change." : n + " changes.";
        }

        // Display lines made from the structure itself, so the person reads tables and links in the same list as the rest.
        private static StartLine Shown(string kind, string objectRef, string name, string title, bool kept, string by, string why, string idKey = null) => new StartLine
        {
            Item = new ChangeItem { Id = ShownId("sh", kind, objectRef, idKey), Kind = kind, ObjectRef = objectRef, ObjectName = name, Title = title, Source = "deterministic", Status = "proposed" },
            DecidedBy = by, Kept = kept, Why = why,
        };

        /// <summary>A stable id for a line the plan does not store (a table, a link, a measure the manifest makes): the same line gets the same id on every read, so
        /// the page keeps one line open and an edit names the line it was pressed on. Not part of the digest.</summary>
        internal static string ShownId(string prefix, params string[] parts)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001f", parts.Select(x => x ?? ""))));
            return prefix + "-" + Convert.ToHexString(bytes).Substring(0, 8).ToLowerInvariant();
        }

        private static StartSet SetOf(string objectRef, string property, string value, string words) =>
            new StartSet { Ref = objectRef, Property = property, Value = value, Words = words };

        private static IEnumerable<StartGroup> TableGroups(StructureManifest m, StartPlanDoc doc = null)
        {
            var refused = new HashSet<string>((doc?.SampleRefused ?? new List<string>()).Select(DisclosedScope.Norm), StringComparer.Ordinal);
            // The build makes the marked date table's date column its key and lets any other key go (StructureManifest.ToSpec), so the column list says the same.
            var dateTable = m.MarkedDateTable();
            foreach (var t in m.Tables)
            {
                var kept = t.Columns.Count(c => c.Include);
                var title = t.Include
                    ? $"Bring in {t.Name} ({kept} of {t.Columns.Count} columns)"
                    : $"Leave out {t.Name}";
                var g = new StartGroup
                {
                    Table = t.Name,
                    SourceName = string.Equals(t.SourceName, t.Name, StringComparison.Ordinal) ? null : t.SourceName,
                    SampleOff = refused.Contains(DisclosedScope.Norm(DisclosedScope.SourceNameOf(t))),
                };
                var tableLine = Shown("include_table", "table:" + t.Name, t.Name, title, t.Include, StartDecidedBy.Rule, "It is in the source.");
                if (t.Include) tableLine.NameFlag = NameFlagOf(doc, "table", "table:" + t.Name, false, t.Name);
                g.Lines.Add(tableLine);
                g.Columns = t.Columns.Select(c => ColumnOf(t, c, doc, ReferenceEquals(t, dateTable) ? m.DateTable.DateColumn : null, ReferenceEquals(t, dateTable))).ToList();
                yield return g;
            }
        }

        /// <summary>One column as the review lists it. The summarize setting is named only when it is NOT Power BI's own default for the column's type (a number adds up,
        /// anything else does not): the default is the same on every import and says nothing a person decides.</summary>
        private static StartColumn ColumnOf(ManifestTable t, ManifestColumn c, StartPlanDoc doc = null, string dateColumn = null, bool dateTable = false)
        {
            var numeric = LocalEngine.IsNumericSpecType(c.DataType);
            var power = numeric ? "Sum" : "None";
            var summarize = string.IsNullOrWhiteSpace(c.SummarizeBy) || string.Equals(c.SummarizeBy, power, StringComparison.OrdinalIgnoreCase) ? null : c.SummarizeBy;
            return new StartColumn
            {
                Ref = "column:" + t.Name + "/" + c.Name, Name = c.Name, SourceName = string.Equals(c.SourceColumn, c.Name, StringComparison.Ordinal) ? null : c.SourceColumn,
                DataType = c.DataType, Included = c.Include, Hidden = c.Hidden, Key = dateTable ? c.Name == dateColumn : c.IsKey, Summarize = summarize,
                NameFlag = c.Include && t.Include ? NameFlagOf(doc, "column", "column:" + t.Name + "/" + c.Name, c.Hidden, c.Name) : null,
            };
        }

        private static IEnumerable<StartGroup> LinkGroups(StructureManifest m)
        {
            var g = new StartGroup { Table = null };
            foreach (var l in m.Links.Where(x => !x.Unresolved))
            {
                var ends = EndIn(m, l.FromTable, l.FromColumns) && EndIn(m, l.ToTable, l.ToColumns);
                var state = !ends ? " (not built: a table or column it needs is left out)" : l.Dropped || l.Active ? "" : " (off)";
                // A guessed link says how its names matched and what makes the other end a key, in the sentence the person reads; a declared one keeps its short line.
                var title = l.Guessed ? LinkGuesser.Words(l) + state : $"{l.FromTable} to {l.ToTable} on {string.Join(", ", l.FromColumns)}" + state;
                var link = Shown("create_relationship", "relationship:" + l.Identity, l.Identity, title,
                    l.Active && ends && !l.Dropped, l.Dropped ? (l.DroppedBy == "assistant" ? StartDecidedBy.Assistant : StartDecidedBy.Person) : StartDecidedBy.Rule,
                    l.Guessed ? LinkGuesser.Why(l) : l.Note ?? "The source declares this link.", StartPlanEditing.LinkText(l));
                // Its own id, so a press on this line is a press on this link (the line is found by "relationship:" and its identity).
                link.Item.Id = "relationship:" + (l.Identity ?? StartPlanEditing.LinkText(l));
                if (l.Guessed) link.Item.RuleId = "LINK-NAME-GUESS";
                if (l.Dropped) { link.Item.Status = "rejected"; link.Item.DroppedBy = l.DroppedBy == "assistant" ? "assistant" : "person"; }
                if (!l.Active && !l.Dropped && ends && l.FromColumns.Length > 0 && l.ToColumns.Length > 0)
                    link.Sets = new List<StartSet> { SetOf("link:" + LinkKey(l.FromTable, l.FromColumns[0], l.ToTable, l.ToColumns[0]), "IsActive", "False", "Switched off, so it is there but filters nothing until you turn it on") };
                g.Lines.Add(link);
            }
            if (!string.IsNullOrWhiteSpace(m.DateTable?.Name))   // Derive always sets a DateTable; an empty Name says there is none
            {
                var date = Shown("mark_date_table", "table:" + m.DateTable.Name, m.DateTable.Name,
                    $"Mark {m.DateTable.Name} as the date table on {m.DateTable.DateColumn}", true, StartDecidedBy.Rule, "Time maths needs one.");
                date.Sets = new List<StartSet> { SetOf("table:" + m.DateTable.Name, "DataCategory", "Time", "Marked as time, so Power BI treats it as the date table") };
                g.Lines.Add(date);
            }
            if (g.Lines.Count > 0) yield return g;
        }

        private static bool EndIn(StructureManifest m, string table, string[] columns)
        {
            var t = m.Tables.FirstOrDefault(x => x.Include && string.Equals(x.Name, table, StringComparison.OrdinalIgnoreCase));
            return t != null && (columns ?? Array.Empty<string>()).All(c => t.Columns.Any(x => x.Include && string.Equals(x.Name, c, StringComparison.OrdinalIgnoreCase)));
        }

        private static IEnumerable<StartGroup> MeasureGroups(StructureManifest m, StartPlanDoc doc = null)
        {
            // Every measure the build writes is a line here: the bases, and under each base measure the time measures that follow it, with their formulas. They are
            // made from the manifest's one list (AllTimeMeasures; TimeMeasures is its kept part), the list the build's own names and formulas come from, so nothing is written unseen (Sol, join 6, A1).
            var times = m.AllTimeMeasures().ToLookup(t => t.Table + "\u001f" + t.BaseMeasure, StringComparer.Ordinal);
            foreach (var byTable in m.BuiltBases().GroupBy(b => b.Table))
            {
                var g = new StartGroup { Table = byTable.Key };
                foreach (var b in byTable)
                {
                    var baseLine = Shown("create_measure", "measure:" + b.Table + "/" + b.Name, b.Name,
                        $"{b.Name}: {b.Pick} of {b.Column}", !string.Equals(b.Pick, "none", StringComparison.OrdinalIgnoreCase),
                        string.IsNullOrEmpty(b.DecidedBy) ? StartDecidedBy.Rule : b.DecidedBy, "A total people can use.");
                    baseLine.Sets = new List<StartSet>
                    {
                        FolderSet(b.Table, b.Name, StructureManifest.BaseMeasuresFolder),
                        SetOf("measure:" + b.Table + "/" + b.Name, "Expression", StarterMeasureTemplates.Dax(b.Pick, b.Table, b.Column), "The formula is " + StarterMeasureTemplates.Dax(b.Pick, b.Table, b.Column)),
                    };
                    baseLine.NameFlag = NameFlagOf(doc, "measure", "measure:" + b.Table + "/" + b.Name, false, b.Name);
                    g.Lines.Add(baseLine);
                    foreach (var t in times[b.Table + "\u001f" + b.Name])
                    {
                        // A time measure the person dropped stays on the review as left out, so it can be kept again; the build writes only the ones that are kept.
                        var line = Shown("create_measure", "measure:" + t.Table + "/" + t.Name, t.Name, $"{t.Name}: {TimeKindWords(t.Kind)} of {t.BaseMeasure}",
                            !t.Dropped, t.Dropped ? (t.DroppedBy ?? StartDecidedBy.Person) : StartDecidedBy.Rule,
                            t.Dropped ? "Dropped." : $"Comes with {t.BaseMeasure}, because {t.Table} has a link to the date table, so time maths works on it.");
                        line.Item.Target = "expression";
                        line.Item.After = t.Dax;
                        line.Derived = true;
                        line.Sets = new List<StartSet>
                        {
                            FolderSet(t.Table, t.Name, StructureManifest.TimeIntelligenceFolder),
                            SetOf("measure:" + t.Table + "/" + t.Name, "Expression", t.Dax, "The formula is " + t.Dax),
                        };
                        if (!string.IsNullOrWhiteSpace(t.Format))
                            line.Sets.Add(SetOf("measure:" + t.Table + "/" + t.Name, "FormatString", t.Format, FormatWords(t.Format)));
                        if (t.Dropped) { line.Item.Status = "rejected"; line.Item.DroppedBy = t.DroppedBy == StartDecidedBy.Assistant ? "assistant" : "person"; }
                        g.Lines.Add(line);
                    }
                }
                yield return g;
            }
        }

        /// <summary>Why the name of a table, a column or a base measure would still be flagged by the naming rules once the plan is built, in plain words, or null: the rules read the name
        /// as they read it in the scan (<see cref="ReadinessRuleSet.NamingRuleForName"/>), and a name somebody kept (the person with "Keep this name", or the assistant) is not flagged
        /// any more, because the build accepts the finding. The page draws its Keep control only where this is set (join 8, K1).</summary>
        internal static string NameFlagOf(StartPlanDoc doc, string objectKind, string objectRef, bool hidden, string name)
        {
            var rule = ReadinessRuleSet.NamingRuleForName(objectKind, hidden, name);
            if (rule == null) return null;
            if (doc != null && StartPlanWords.LineFor(doc, StartPlanWords.Back(doc, objectRef), StartPlanWords.TargetOf(StartPlanWords.KeepName)) != null) return null;
            return ReadinessRuleSet.NamingRuleWhy(rule, name);
        }

        private static StartSet FolderSet(string table, string measure, string folder) =>
            SetOf("measure:" + table + "/" + measure, "DisplayFolder", folder, "Filed in the folder " + folder);

        /// <summary>A number format in plain words: a percent, or the format itself.</summary>
        private static string FormatWords(string format) =>
            format.Contains('%') ? "Shown as a percent (" + format + ")" : "Number format " + format;

        private static string TimeKindWords(string kind) => kind switch
        {
            "YTD" => "year to date",
            "QTD" => "quarter to date",
            "MTD" => "month to date",
            "PY" => "the same period last year",
            "YoY" => "the change from the same period last year",
            "YoYPct" => "the percent change from the same period last year",
            _ => kind,
        };

        /// <summary>Every object the review shows the build writing, by its ref: the tables, the measures and the links whose lines are kept. Made from the same view the person
        /// reads (<see cref="Build"/>), never from a second list, so the build's own check (<see cref="StartPlanBuild.UnreviewedObject"/>) compares the model against
        /// exactly what was on the screen.</summary>
        internal static (HashSet<string> Tables, HashSet<string> Measures, HashSet<string> Links) Reviewed(StartPlanDoc doc)
        {
            var kept = Build(doc).Sections.SelectMany(s => s.Groups).SelectMany(g => g.Lines).Where(l => l.Kept && l.Item != null).ToList();
            HashSet<string> Refs(string kind) => new HashSet<string>(kept.Where(l => l.Item.Kind == kind).Select(l => l.Item.ObjectRef), StringComparer.OrdinalIgnoreCase);
            var tables = Refs("include_table");
            var measures = Refs("create_measure");
            // A link is told apart by its two ends. The review lists every link the build can make (an inactive one reads "(off)" and is still written, switched off),
            // by the same test the line's own wording uses: both of its ends are brought in.
            var links = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in BuiltLinks(doc?.Manifest ?? new StructureManifest())) links.Add(LinkKey(l.FromTable, l.FromColumns[0], l.ToTable, l.ToColumns[0]));
            return (tables, measures, links);
        }

        /// <summary>The links the build writes: not unresolved, not dropped, and both ends brought in (a table or column left out takes its links out of the build with it). The one
        /// list the page's count and drawing (<see cref="StartPlanView.Links"/>) and the build's own check (<see cref="Reviewed"/>) are made from; it is the test <c>StructureManifest.ToSpec</c> applies.</summary>
        internal static IEnumerable<ManifestLink> BuiltLinks(StructureManifest m) =>
            m.Links.Where(l => !l.Unresolved && !l.Dropped && l.FromColumns?.Length > 0 && l.ToColumns?.Length > 0 && EndIn(m, l.FromTable, l.FromColumns) && EndIn(m, l.ToTable, l.ToColumns));

        internal static string LinkKey(string fromTable, string fromColumn, string toTable, string toColumn) => $"{fromTable}.{fromColumn}>{toTable}.{toColumn}";
    }
}
