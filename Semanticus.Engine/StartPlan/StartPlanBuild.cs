// The start plan's build, the parts that need no engine (design-spec-first-start-v2.md S1, S7, S12): the checks that
// stand in front of it, the digest an approval is bound to, the grades a preview and a build must agree on, and the
// seams a test uses. The transaction itself is LocalEngine.StartPlanBuild.cs, because the audit record, the
// activity nudge and the grades are LocalEngine's.
//
// WHAT THE BUILD PROMISES, and where each promise is kept:
//   one undo step ............ ONE MutateAsync on the person's model; nothing in the delegate opens another.
//   approval or no build ..... Refusal(): the stored approval, the digest the caller names and the digest the plan has
//                              now must be the same string. Any edit moves the digest, so any edit voids the approval.
//   nothing lands on a miss .. the projection is compared INSIDE the delegate, before EndUpdate. A mismatch throws, so
//                              the existing rollback leaves no undo entry, no publish and no revision (S7).
//   audit and grades after ... the Verified Edits record and the activity nudge run only once the compare passed.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Semanticus.Analysis;

namespace Semanticus.Engine.StartPlan
{
    /// <summary>Hooks a test uses. Production leaves every one null.</summary>
    internal sealed class StartPlanBuildSeams
    {
        /// <summary>Builds the plan into a model. Null means <see cref="StartPlanBuild.Materialize"/>, which is the
        /// manifest's spec plus the shared executor. The preview and the build run the SAME function, so they cannot differ
        /// by construction; a test injects one to prove the transaction around it.</summary>
        public Action<LocalEngine, TabularEditor.TOMWrapper.Model, Session, StartPlanDoc> Materialize;

        /// <summary>FORCED MISMATCH: rewrites the projection the build just took, before it is compared. Never set outside
        /// a test; it exists so the "a mismatch commits nothing" promise has a way to be tried.</summary>
        public Func<string, string> TamperProjection;

        /// <summary>Runs on the dispatcher inside the build's delegate, after the checks and before the model is touched.
        /// A test uses it to hold the build open while something else is started.</summary>
        public Action InsideBuildForTest;

        /// <summary>Runs inside the build's delegate right after the manifest's structure is built and before the check against the review. A test uses it to
        /// write an object the review did not show.</summary>
        public Action<TabularEditor.TOMWrapper.Model> AfterStructureForTest;

        /// <summary>Runs inside the build's delegate right after the lines are made, before the check on synonyms. A test uses it to write a synonym no line wrote.</summary>
        public Action<TabularEditor.TOMWrapper.Model> AfterLinesForTest;

        /// <summary>Sees each property the structure set (ref|property=value), after the object check. A test reads it to learn what a build writes.</summary>
        public Action<List<string>> JournalForTest;

        /// <summary>Sees each projection the moment it is taken ("preview" or "build"), before any tamper. A test uses it to
        /// write the two texts out when they differ, so a mismatch can be read as a diff and not guessed at.</summary>
        public Action<string, string> Tap;

    }

    /// <summary>A refusal the delegate throws so the batch rolls back. Its message is plain words for the person.</summary>
    internal sealed class StartPlanBuildRefusedException : InvalidOperationException
    {
        public StartPlanBuildRefusedException(string message) : base(message) { }
    }

    internal static class StartPlanBuild
    {
        /// <summary>Build the plan into <paramref name="m"/>: the manifest's structure first, then the kept lines. Runs on the
        /// dispatcher inside a batch and never opens one of its own, so the whole build is one undo step. The preview and the
        /// build run THIS function, so they can differ only if the models they run on differ, which the projection compare
        /// then refuses.
        ///
        /// STRUCTURE is <see cref="StructureManifest.ToSpec"/> through the same BuildSpecInto the two-step path uses, with every
        /// identity the manifest allocated from the document's seed (S3). A build error is a refusal, not a half model.
        ///
        /// LINES go through the Plan's own line executor (S5 and S12): the Needs closure, the apply order that puts renames
        /// after the edits that name the old ref, the late-format follow and ref refresh, the write-time best-practice
        /// refusal, and the waived seeds. They run on CLONES, so a preview or a build never writes a status or a note into
        /// the document. The Plan lets a line it could not apply go with a note; a start plan cannot, because the person
        /// approved a list and a kept line that did not land would make the built model differ from what they read. Any kept
        /// line that is not applied refuses the build, naming it and why.</summary>
        internal static void Materialize(LocalEngine engine, TabularEditor.TOMWrapper.Model m, Session s, StartPlanDoc doc)
        {
            MaterializeStructure(m, doc);
            MaterializeLines(engine, m, s, doc);
        }

        /// <summary>The first half of <see cref="Materialize"/>: the manifest's spec built into the model. The build checks the model against the review between
        /// the two halves (<see cref="UnreviewedObject"/>), while the names are still the manifest's own.</summary>
        internal static void MaterializeStructure(TabularEditor.TOMWrapper.Model m, StartPlanDoc doc)
        {
            var spec = (doc.Manifest ?? new StructureManifest()).ToSpec();
            var created = new List<string>(); var skipped = new List<string>(); var errors = new List<string>(); var notes = new List<string>();
            LocalEngine.BuildSpecInto(m, spec, created, skipped, errors, notes);
            if (errors.Count > 0)
                throw new StartPlanBuildRefusedException("The model could not be built from this plan: " + errors[0]);
        }

        internal static void MaterializeLines(LocalEngine engine, TabularEditor.TOMWrapper.Model m, Session s, StartPlanDoc doc)
        {
            // A hand-made line whose object the model does not have (its table, column, measure or link was left out or replaced) is HELD: kept on the plan, shown,
            // and not built, exactly as the scratch the plan was decided on treats it (StartPlanHold). Building it would refuse the whole build for a line the
            // person already knows is waiting for its object.
            var present = StartPlanHold.PresentRefs(doc, m);
            var lines = (doc.Lines ?? new List<StartLine>()).Where(l => l?.Item != null && !StartPlanHold.IsHeld(l, present)).ToList();
            if (!lines.Any(l => l.Kept)) return;
            var clones = lines.Select(l => (Line: l, Item: l.Item.Clone())).ToList();
            foreach (var (line, item) in clones) item.Status = line.Kept ? "approved" : "rejected";
            // A kept name is not a change to the model: it is an answer. It is carried out below, as the acceptance of the naming finding the name raises.
            var keeps = clones.Where(c => c.Line.Kept && IsKeptName(c.Item)).ToList();
            var toApply = clones.Where(c => c.Line.Kept && !IsKeptName(c.Item)).Select(c => c.Item).ToList();
            PlanLineExecutor.ApplyInBatch(engine, m, s, toApply,
                new PlanLineOptions { CloseNeedsAgainst = clones.Where(c => !IsKeptName(c.Item)).Select(c => c.Item).ToList() });
            var lost = toApply.FirstOrDefault(i => !string.Equals(i.Status, "applied", StringComparison.OrdinalIgnoreCase));
            if (lost != null)
                throw new StartPlanBuildRefusedException("One change in the plan could not be made, so nothing was built. "
                    + (string.IsNullOrWhiteSpace(lost.ObjectName) ? lost.ObjectRef : lost.ObjectName) + ": " + (lost.Note ?? "it could not be made."));
            AcceptKeptNames(doc, m, keeps);
        }

        /// <summary>Carry out the plan's kept names: accept the naming finding each name raises, so the scan counts it as passed. Runs after the lines that rename, because a
        /// name is kept as it is NOW, and the acceptance follows the object whatever it is called later. The build, the preview and the decide's scratch all run it, so the
        /// words list and the grades the person is shown already have these findings accepted, and the built model agrees with them.</summary>
        internal static void AcceptKeptNames(StartPlanDoc doc, TabularEditor.TOMWrapper.Model m, IEnumerable<(StartLine Line, ChangeItem Item)> keeps)
        {
            foreach (var (line, item) in keeps)
            {
                TabularEditor.TOMWrapper.ITabularNamedObject obj = null;
                foreach (var r in new[] { StartPlanWords.Forward(doc, item.ObjectRef), item.ObjectRef })
                {
                    try { obj = ObjectRefs.Resolve(m, r) as TabularEditor.TOMWrapper.ITabularNamedObject; } catch { obj = null; }
                    if (obj != null) break;
                }
                if (obj == null) continue;
                var by = string.Equals(line.DecidedBy, StartDecidedBy.Person, StringComparison.Ordinal) ? "human" : "agent";
                ReadinessRuleSet.AcceptKeptName(m, obj, by, "The name was kept as it is when the model was started from its plan.");
            }
        }

        internal static bool IsKeptName(ChangeItem i) => string.Equals(i?.Kind, "keep_name", StringComparison.OrdinalIgnoreCase);

        /// <summary>The first object the build wrote that the review did not show, in plain words, or null. Runs inside the build's delegate, after the plan is built into
        /// the model and before the projection is compared, so a build that wrote an unseen table, measure or link takes itself back. The review is
        /// <see cref="StartPlanViewBuilder.Reviewed"/>, made from the view the person read, and a time measure's formula must be the one the review showed. The projection
        /// compare alone could not say this: the preview is made by the same code, so both would carry the same unseen object (Sol, join 6, A1).</summary>
        internal static string UnreviewedObject(TabularEditor.TOMWrapper.Model m, StartPlanDoc doc)
        {
            var (tables, measures, links) = StartPlanViewBuilder.Reviewed(doc);
            var timeFormulas = (doc.Manifest ?? new StructureManifest()).TimeMeasures().ToDictionary(t => "measure:" + t.Table + "/" + t.Name, t => t.Dax, StringComparer.OrdinalIgnoreCase);
            foreach (var t in m.Tables)
                if (!tables.Contains("table:" + t.Name)) return "the table " + t.Name;
            foreach (var x in m.AllMeasures)
            {
                var key = "measure:" + x.Table.Name + "/" + x.Name;
                if (!measures.Contains(key)) return "the measure " + x.Name;
                if (timeFormulas.TryGetValue(key, out var dax) && !string.Equals(Squash(x.Expression), Squash(dax), StringComparison.Ordinal))
                    return "the formula of " + x.Name;
            }
            foreach (var r in m.Relationships.OfType<TabularEditor.TOMWrapper.SingleColumnRelationship>())
                if (!links.Contains(StartPlanViewBuilder.LinkKey(r.FromTable?.Name, r.FromColumn?.Name, r.ToTable?.Name, r.ToColumn?.Name)))
                    return "a link from " + r.FromTable?.Name + " to " + r.ToTable?.Name;
            return null;
        }

        /// <summary>The columns the approved plan gives NO measure on purpose: every base measure whose pick is none because the person, their assistant or Jev said so
        /// (a pick the rules made for a column that gets no measure is not a refusal), by the column's ref. By column and not by measure name, because each Plan family
        /// names its measures its own way ("Total Amount", "Total Sales Amount"), and what was refused is the column's measure, whatever it would be called. Null when none.</summary>
        internal static string[] RefusedColumnRefs(StartPlanDoc doc)
        {
            var refs = new List<string>();
            foreach (var b in (doc?.Manifest?.BaseMeasures ?? new List<ManifestBaseMeasure>()).Where(b => doc.Manifest.IsBuilt(b)))
            {
                if (!string.Equals(b.Pick, StarterMeasureTemplates.None, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrEmpty(b.DecidedBy) || string.Equals(b.DecidedBy, StartDecidedBy.Rule, StringComparison.Ordinal)) continue;
                var columnRef = "column:" + b.Table + "/" + b.Column;
                refs.Add(columnRef);
                // The assistant's business names are lines that rename the column as the build is made, so the model's column is called by its new name and the Plan asks about that
                // one (join 8, K14): both names are kept, the manifest's and the one the model has.
                refs.Add(StartPlanWords.Forward(doc, columnRef));
            }
            return refs.Count == 0 ? null : refs.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static string Squash(string text) => string.Concat((text ?? "").Where(c => !char.IsWhiteSpace(c)));

        // ------------------------------------------------------------------ the checks in front of the build

        /// <summary>Why the build must not run, in plain words, or null when it may. Pure over the document, so the same
        /// function answers outside the transaction (fast) and inside it (the one that counts).
        ///
        /// "THE MODEL IS UNCHANGED SINCE THE BASELINE" is NOT a revision check. The start records the empty model's
        /// revision (StartPlanDoc.BaselineRevision), but the permission marker is written into that model AFTER it, as a
        /// tracked edit, so a Pro plan whose person has given permission would never match it again. The guarantee is
        /// kept where it cannot be bypassed instead: the delegate refuses a model that already has tables, and the
        /// projection compare refuses ANY other difference between the model the preview was made on and the model being
        /// built into, which a revision number could not see (a person's own edit that left no table).</summary>
        internal static string Refusal(StartPlanDoc doc, string digest, StartPlanBuildSeams seams = null)
        {
            if (doc == null) return "There is no start plan to build.";
            if (doc.Parked) return "This plan is parked because another model was opened. Open the new model's folder again to carry on.";
            if (doc.Approval == null || string.IsNullOrEmpty(doc.Approval.Digest))
                return "This plan has not been approved yet. Look it over and approve it first.";
            var now = DigestOf(doc);
            if (string.IsNullOrEmpty(digest) || !string.Equals(digest, now, StringComparison.Ordinal))
                return "The plan has changed since you looked at it. Look at it again and approve it again.";
            if (!string.Equals(doc.Approval.Digest, now, StringComparison.Ordinal))
                return "The plan has changed since you approved it. Approve it again to build it.";
            if (string.IsNullOrEmpty(doc.Preview?.ProjectionDigest))
                return "This plan has no finished preview yet, so there is nothing to check the build against. Wait for it to finish.";
            return NeedsGap(doc);
        }

        /// <summary>A kept line that needs another line (a hide needs its measure) when that line is in the plan but dropped.
        /// The plan's edits close these as they go (S12), so a gap here means the document was changed some other way, and the
        /// honest answer is to stop, not to build something the person never looked at. Null when every need is met.</summary>
        internal static string NeedsGap(StartPlanDoc doc)
        {
            var all = doc.Lines ?? new List<StartLine>();
            foreach (var line in all.Where(l => l?.Kept == true && l.Item != null && JevFamily.IsJevLine(l.Item) && l.Item.Needs != null))
            {
                var needed = all.Where(o => !ReferenceEquals(o, line) && o?.Item != null && JevFamily.IsJevLine(o.Item)
                                            && string.Equals(o.Item.JevItem, line.Item.JevItem, StringComparison.Ordinal)
                                            && string.Equals(JevFamily.NeedsKey(o.Item.Kind, o.Item.ObjectRef), line.Item.Needs, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (needed.Count > 0 && needed.All(o => !o.Kept))
                {
                    var n = needed[0].Item;
                    return "\"" + (line.Item.Title ?? line.Item.Kind) + "\" on " + (line.Item.ObjectName ?? line.Item.ObjectRef)
                           + " needs \"" + (n.Title ?? n.Kind) + "\" on " + (n.ObjectName ?? n.ObjectRef)
                           + ", which is left out. Put it back or leave both out.";
                }
            }
            return null;
        }

        // ------------------------------------------------------------------ the digest an approval is bound to

        /// <summary>The digest the approval is bound to. It is the view builder's own (StartPlanViewBuilder.Digest, lane
        /// sf-core), so the digest the person sees on the view, the one the approval stores and the one the build checks are
        /// ONE function and cannot drift. It covers where the model goes, the source, the structure, every kept line with
        /// its object, value and who decided it, and the pins and answers; the approval itself, the decide progress, the
        /// preview's numbers and the timestamps are left out. The preview is guarded by the projection compare instead: a
        /// stale preview never matches what is built, so the build refuses it.</summary>
        internal static string DigestOf(StartPlanDoc doc) => StartPlanViewBuilder.Digest(doc);

        // ------------------------------------------------------------------ the grades a preview and a build agree on

        /// <summary>The grades a preview shows and a build must reproduce: AI readiness (0 to 100, rounded) with its letter, and the
        /// Best practice LETTER (BpaGrading). Dispatcher thread only. The same function runs on the scratch and on the built
        /// model, so "grades after equal the preview" is a property of the model being equal, not of two code paths agreeing.</summary>
        internal static (int? AiReadiness, string AiGrade, string BestPractice, int? BestPracticeOpen) GradesOf(TabularEditor.TOMWrapper.Model m)
        {
            int? ai = null, open = null; string aiGrade = null, bpa = null;
            try { var card = new ReadinessAnalyzer().Analyze(m); ai = (int)Math.Round(card.Overall); aiGrade = card.Grade; } catch { /* a grade that cannot be read is unknown, never zero */ }
            // The LETTER the person is shown (BpaGrading), never the violation count: the count includes info notes the letter ignores.
            // The open errors and warnings the SAME scan counts (what the letter weighs; an accepted finding and an info note are not open), so the plan can say how many
            // stay open beside the letter without counting its own lines, which are not the scan (plan statement check 3, defect 3).
            try
            {
                var scan = BpaAnalyzer.Analyze(m, LocalEngine.GetBpaRules(m));
                bpa = scan.BestPractice;
                open = scan.Violations.Count(v => !v.IsAccepted() && v.Severity >= 2);
            }
            catch { }
            return (ai, aiGrade, bpa, open);
        }
    }
}
