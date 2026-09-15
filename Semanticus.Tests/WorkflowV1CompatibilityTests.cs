using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The v1-forever guarantee (docs/workflow-canvas-spec.md §1.2, §2.5; acceptance check 27's concern
    /// applied to the whole shipped corpus). Every workflow file in the repo carries no
    /// <c>schemaVersion</c>, so every one of them must keep parsing under v1 rules with byte-identical
    /// results through the format-v2 work.
    ///
    /// ONE AMENDMENT HAS SINCE BEEN RATIFIED ON TOP OF THAT FREEZE, and it is why this guard cannot just
    /// compare the whole corpus: <c>planItem</c> joined the v1 gate-input types (D-090, commit 4c16b391) so
    /// an author can hand a plan item straight to a step, and the shipped seed <c>governed-rename.md</c>
    /// carries no <c>schemaVersion</c> and uses it. The pinned pre-v2 parser predates the amendment, so it
    /// REFUSES that file while the shipped parser reads it, and NO golden the pin can write will ever equal
    /// today's bytes for it. That one divergence is named in <c>V1Amendments</c> and its bytes pinned beside
    /// the golden, instead of being absorbed by regenerating a golden that cannot contain it.
    ///
    /// The oracle is a GOLDEN fingerprint captured from the parser BEFORE the v2 change, not a re-derivation
    /// of the new parser's own output. A test that compares the new parser to itself proves nothing, which is
    /// this repo's standing defect family. When the golden is absent the run STOPS and names the pinned
    /// oracle to regenerate from; it captures nothing, so there is no run in which this guard compares the
    /// parser to something the parser wrote.
    ///
    /// That sentence used to read "when the golden is absent the test WRITES it and FAILS, so it can never
    /// pass by finding nothing", and it was FALSE in the way that mattered: the first run failed, and the
    /// second run passed against the mirror the first had written. Measured, not argued (F-059). It is left
    /// on the record here because a comment asserting an invariant the code does not hold is the defect, not
    /// the note about it.
    ///
    /// WHAT THIS GUARD PROVES, AND WHAT IT DOES NOT. It proves that the shapes present in these 26 files
    /// parse byte-identically to the pre-v2 parser, and it proves nothing about a shape no shipped file uses.
    /// That gap is real: the shipped corpus declares no version at all, carries no unknown key, no duplicate
    /// key and no comment-shaped value, so for three review rounds the version scan was changed under a guard
    /// that could not see it. The <c>fixtures/workflow-v1-shapes</c> corpus below closes the five gaps that
    /// were judged to matter (an explicit `schemaVersion: 1`; a duplicate version; a `slots:` block with an
    /// unknown property; an unknown top-level key with a value; a comment-shaped value), with its own golden
    /// captured the same way, from the PINNED pre-v2 commit's parser compiled in isolation by
    /// Semanticus.Tests/tools/prev2-oracle. Five fixtures are not "every shape": they are the five whose
    /// absence had already cost something.
    ///
    /// The ratified amendment is not a gap of that kind. It is named, it carries the decision that ratified
    /// it, its bytes are pinned, and the set of names is required to be EXACTLY the set of files that diverge,
    /// compared both ways. A new divergence fails this guard, and a name whose file stops diverging fails it
    /// too, so the list cannot decay into an exemption nobody notices.
    /// </summary>
    public sealed class WorkflowV1CompatibilityTests
    {
        /// <summary>The three shipped corpora, each with the count this repo had when the guard was written.
        /// The counts are asserted so a file added or deleted surfaces here rather than silently shrinking
        /// the corpus the guard covers.</summary>
        private static readonly (string Dir, int Count)[] Corpora =
        {
            ("Semanticus.Engine/workflows", 16),
            ("Semanticus.Engine/workflow-templates", 4),
            ("Semanticus.Engine/workflows-parked", 7),
        };

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Semanticus.sln"))) dir = dir.Parent;
            Assert.True(dir != null, "could not find Semanticus.sln above " + AppContext.BaseDirectory
                + "; this guard reads the workflow sources from the repo, not from the output directory");
            return dir!.FullName;
        }

        private static string GoldenPath() => Path.Combine(RepoRoot(), "Semanticus.Tests", "goldens", "workflow-v1-parse.txt");

        /// <summary>The v1 amendments ratified AFTER the oracle was pinned, and so invisible to it. Each entry
        /// is a file whose parse today intentionally differs from the frozen parser, with the decision that
        /// ratified the change. Today's bytes live in this golden, because the corpus golden carries only what
        /// the PINNED parser writes and the pin refuses these files.
        ///
        /// This is a record, not an exemption. The guard below requires this set to equal the set of files
        /// that actually diverge, in both directions, so it cannot silently widen and it cannot go stale.</summary>
        private static readonly (string Rel, string Why)[] V1Amendments =
        {
            ("Semanticus.Engine/workflows/governed-rename.md",
             "D-090 (4c16b391): planItem joined the v1 gate-input types and this no-schemaVersion seed uses it; "
             + "the pinned pre-v2 parser predates the amendment and refuses the file today's parser reads."),
        };

        private static string AmendmentGoldenPath() => Path.Combine(RepoRoot(), "Semanticus.Tests", "goldens", "workflow-v1-amendments.txt");

        /// <summary>The v1 SHAPES no shipped file happens to use. Not part of the shipped corpus: these
        /// are test fixtures and must never be installed as workflows, which is why they sit under
        /// Semanticus.Tests and have their own golden.</summary>
        private const string ShapesDir = "Semanticus.Tests/fixtures/workflow-v1-shapes";
        private const int ShapesCount = 11;
        private static string ShapesGoldenPath() => Path.Combine(RepoRoot(), "Semanticus.Tests", "goldens", "workflow-v1-shapes.txt");

        private static List<(string Rel, WorkflowDef Def)> LoadEveryShippedFile()
        {
            var root = RepoRoot();
            var all = new List<(string, WorkflowDef)>();
            foreach (var (rel, expected) in Corpora)
            {
                var dir = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(Directory.Exists(dir), "missing corpus directory: " + dir);
                var files = Directory.GetFiles(dir, "*.md").OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal).ToArray();
                Assert.True(files.Length == expected,
                    $"{rel} holds {files.Length} files, the guard was written against {expected}. "
                    + "Re-check the corpus and regenerate the golden deliberately.");
                foreach (var f in files) all.Add((rel + "/" + Path.GetFileName(f), WorkflowParser.ParseFile(f, "stock")));
            }
            return all;
        }

        [Fact]
        public void Every_shipped_workflow_file_parses_clean_under_v1()
        {
            foreach (var (rel, def) in LoadEveryShippedFile())
                Assert.True(def.Error == null, $"{rel} failed to parse: {def.Error}");
        }

        // REGENERATING THE AMENDMENT GOLDEN. There is no helper in this file and there must not be one:
        // F059_the_v1_golden_guard_never_writes_the_thing_it_compares_against asserts over this SOURCE that
        // it contains no write at all, and that guard is worth more than the convenience. When a named
        // amendment's file changes, run the guard below, take the "actual" block it prints for that file,
        // and paste it into goldens/workflow-v1-amendments.txt by hand. The corpus golden is different: it
        // comes from pwsh -File Semanticus.Tests/tools/prev2-oracle/capture.ps1, never from a hand edit.
        [Fact]
        public void Every_shipped_workflow_file_parses_byte_identically_to_the_pre_v2_parser()
        {
            var actual = string.Join("\n", LoadEveryShippedFile().Select(x => Fingerprint(x.Rel, x.Def))) + "\n";
            RequireGolden(actual, GoldenPath(), "the shipped corpus", V1Amendments);
        }

        /// <summary>THE one decision about what a missing golden means, for BOTH goldens.
        ///
        /// It used to CAPTURE one from the parser under test and fail that run. That reads as fail-closed and
        /// is not: the next run compares against the file the last run wrote, so it passes, and the strongest
        /// guard on this work becomes a mirror of the code it exists to check. Deleting one file would have
        /// evaporated every v1-forever proof with the suite still green. A golden may only ever come from the
        /// PINNED pre-v2 oracle, so an absent one is a hard stop that says where to get it, and this method
        /// writes nothing under any circumstances.
        ///
        /// One helper rather than two call sites: the corpus guard had this hole and the shapes guard did not,
        /// which is the drift that happens whenever the same question is answered twice.
        ///
        /// When <paramref name="amendments"/> is given, the golden is compared BLOCK BY BLOCK instead of as one
        /// string, and only the named files may differ. That is the one place the corpus guard and the shapes
        /// guard legitimately disagree: the corpus contains a file the pin refuses, the fixtures do not.</summary>
        internal static void RequireGolden(string actual, string path, string what,
            IReadOnlyList<(string Rel, string Why)> amendments = null)
        {
            Assert.True(File.Exists(path),
                $"no golden for {what} at {path}. A golden is captured from the PINNED pre-v2 parser and never "
                + "from this run, because a golden written by the code under test agrees with that code including "
                + "when it is wrong. Regenerate it with: pwsh -File Semanticus.Tests/tools/prev2-oracle/capture.ps1");
            var oracle = File.ReadAllText(path).Replace("\r\n", "\n");
            if (amendments == null || amendments.Count == 0)
            {
                Assert.Equal(oracle, actual);
                return;
            }
            RequireGoldenAllowingAmendments(oracle, actual, amendments);
        }

        /// <summary>The corpus comparison, block by block, where a block is one file's fingerprint.
        ///
        /// Both directions are checked, and both halves of that matter. An UNLISTED divergence fails, so the
        /// frozen ruleset cannot drift under this guard again. A LISTED file that stops diverging also fails,
        /// because then its entry is a claim about the code that is no longer true, which is this repo's
        /// standing defect family. And a listed file's bytes must equal the pinned ones in
        /// goldens/workflow-v1-amendments.txt, so "it diverges" is never enough on its own.</summary>
        private static void RequireGoldenAllowingAmendments(string oracle, string actual,
            IReadOnlyList<(string Rel, string Why)> amendments)
        {
            var pins = AmendmentPins();
            Assert.Equal(amendments.Select(a => a.Rel).OrderBy(r => r, StringComparer.Ordinal).ToArray(),
                         pins.Keys.OrderBy(r => r, StringComparer.Ordinal).ToArray());
            foreach (var (rel, why) in amendments)
                Assert.False(string.IsNullOrWhiteSpace(why),
                    $"the v1-amendment entry for '{rel}' carries no reason. An unexplained exception is the "
                    + "defect this list exists to prevent.");

            var oracleBlocks = SplitByFile(oracle, "the golden");
            var todayBlocks = SplitByFile(actual, "this run");
            Assert.Equal(oracleBlocks.Select(b => b.Rel).ToArray(), todayBlocks.Select(b => b.Rel).ToArray());

            var diverged = new List<string>();
            for (var i = 0; i < oracleBlocks.Count; i++)
            {
                if (oracleBlocks[i].Body == todayBlocks[i].Body) continue;
                var rel = oracleBlocks[i].Rel;
                diverged.Add(rel);
                Assert.True(amendments.Any(a => a.Rel == rel),
                    $"'{rel}' no longer parses byte-identically to the pre-v2 parser and is not a named v1 "
                    + "amendment. A divergence is a change to the frozen v1 ruleset, so name it, with the decision "
                    + "that ratified it, in V1Amendments and pin today's bytes in "
                    + "Semanticus.Tests/goldens/workflow-v1-amendments.txt. Regenerating the golden cannot hide "
                    + "this: it is written by the PINNED oracle, which never writes today's bytes for a file it "
                    + "refuses.");
                Assert.Equal(pins[rel], todayBlocks[i].Body);
            }
            Assert.Equal(amendments.Select(a => a.Rel).OrderBy(r => r, StringComparer.Ordinal).ToArray(),
                         diverged.OrderBy(r => r, StringComparer.Ordinal).ToArray());
        }

        /// <summary>Today's bytes for each amended file, keyed by relative path. Same block shape as the corpus
        /// golden, so one splitter reads both. This file is written by the parser UNDER TEST, which is the one
        /// thing a golden may not be; it is safe here only because the set of files it may name is the set the
        /// guard already knows about, pinned above, and every name in it must still diverge.</summary>
        private static Dictionary<string, string> AmendmentPins()
        {
            var path = AmendmentGoldenPath();
            Assert.True(File.Exists(path),
                $"no amendment golden at {path}. It holds the bytes TODAY's parser produces for each named v1 "
                + "amendment; the corpus golden holds the PINNED parser's bytes and cannot carry them.");
            return SplitByFile(File.ReadAllText(path).Replace("\r\n", "\n"), "the amendments golden")
                .ToDictionary(b => b.Rel, b => b.Body);
        }

        /// <summary>Split a fingerprint document into one block per <c>FILE</c> line. A block runs from its
        /// FILE line up to the next one, with no trailing newline, so concatenating the bodies in order with a
        /// newline between them reproduces the document exactly: nothing can hide in the seams.</summary>
        private static List<(string Rel, string Body)> SplitByFile(string text, string what)
        {
            var lines = text.TrimEnd('\n').Split('\n');
            var blocks = new List<(string Rel, string Body)>();
            var start = -1;
            for (var i = 0; i <= lines.Length; i++)
            {
                if (i < lines.Length && !lines[i].StartsWith("FILE ", StringComparison.Ordinal)) continue;
                if (start >= 0)
                    blocks.Add((Rel: lines[start].Substring("FILE ".Length),
                                Body: string.Join("\n", lines, start, i - start)));
                start = i;
            }
            Assert.True(blocks.Count > 0 && blocks.All(b => b.Rel.Length > 0), $"no FILE blocks found in {what}");
            return blocks;
        }

        [Fact]
        public void Every_v1_shape_fixture_parses_byte_identically_to_the_pre_v2_parser()
        {
            // Same oracle discipline as the corpus test above, and the golden was captured the same way: by
            // compiling the PINNED pre-v2 commit's WorkflowParser.cs and Workflow.cs in isolation and running
            // this exact Fingerprint over these files. Not derived from the parser under test.
            // Re-run: pwsh -File Semanticus.Tests/tools/prev2-oracle/capture.ps1
            var dir = Path.Combine(RepoRoot(), ShapesDir.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(Directory.Exists(dir), "missing shapes fixture directory: " + dir);
            // README.md documents the directory; it is not a fixture. capture.ps1's harness applies the same
            // exclusion, and the two must stay identical or the golden would cover a different set than this.
            var files = Directory.GetFiles(dir, "*.md")
                .Where(p => !Path.GetFileName(p).StartsWith("README", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal).ToArray();
            Assert.True(files.Length == ShapesCount,
                $"{ShapesDir} holds {files.Length} files, the guard was written against {ShapesCount}. "
                + "Adding a shape means capturing its pre-v2 fingerprint too, not regenerating this golden from today's parser.");

            var actual = string.Join("\n", files.Select(f =>
                Fingerprint(ShapesDir + "/" + Path.GetFileName(f), WorkflowParser.ParseFile(f, "stock")))) + "\n";
            // Routed through the same helper as the corpus golden. This guard was ALREADY fail-closed and the
            // corpus one was not, which is the whole argument for one helper: the same question answered in
            // two places drifts, and here it already had.
            RequireGolden(actual, ShapesGoldenPath(), "the v1 shape fixtures");
        }

        [Fact]
        public void Every_fixture_readme_row_is_true_of_the_code()
        {
            // The README table maps each fixture to the tests that pin it. Twice in this slice a claim like
            // that was made in prose and was false: a control whose input lacked the hazard its name
            // promised, then a fixture whose hazard nothing asserted. The table shipped as the countermeasure
            // for those two and had the same fault in two of its own rows, which Sol showed by mutating a
            // fixture and watching every named test stay green.
            //
            // So the table is checked, not trusted. Four things, and each one closes a way the check itself
            // could pass while saying nothing (Sol round 8):
            //   - rows and files on disk are the SAME SET, compared both ways. Counting them is not enough:
            //     duplicating one row while deleting another keeps the totals equal and loses a fixture.
            //   - a row must name at least one test. A row whose backticks are dropped named none, the loop
            //     ran zero times, and the row passed by describing nothing.
            //   - the named test must exist, and its body must CALL `Fixture("<stem>")`. Mentioning the stem
            //     is not enough: a test can retype the input inline and leave a `// copied from x.md`
            //     comment, and a substring check would bless that forever.
            //   - the body is sliced to the next class member, never brace-counted, so a `{` inside a string
            //     literal cannot run the slice into a later test and let its Fixture call answer for this one.
            //
            // WHAT THIS PROVES AND WHAT IT DOES NOT: it proves the named test reads the fixture from disk, so
            // editing the fixture breaks the test. It cannot prove the test asserts anything useful about
            // what it read, and no static check can.
            var root = RepoRoot();
            var shapes = Path.Combine(root, ShapesDir.Replace('/', Path.DirectorySeparatorChar));
            var onDisk = Directory.GetFiles(shapes, "*.md")
                .Select(Path.GetFileName)
                .Where(f => !f.StartsWith("README", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var problems = ReadmeTableProblems(
                File.ReadAllText(Path.Combine(shapes, "README.md")),
                File.ReadAllText(Path.Combine(root, "Semanticus.Tests", "WorkflowV2SolRoundTwoTests.cs")),
                onDisk);
            Assert.Equal(Array.Empty<string>(), problems.ToArray());
        }

        /// <summary>Every way the README table can be false, as a LIST rather than as a throw, so the rule
        /// itself can be unit-tested against a synthetic table instead of only against the real one.
        ///
        /// It was inline Assert calls until F-071, and that is why the hole below survived: the only input
        /// it could ever be given was the table that happened to be correct, so "what does this do with a
        /// bad row?" was answerable by reading and not by running. The tests beside it now feed it bad rows.
        ///
        /// F-071, the hole itself: names were matched by the PREFIX pattern `[RU]\d+...`, so a row naming a
        /// real test plus `F1_fake_test` passed — the fake matched nothing, was never added to the list, and
        /// a name that matches no test was SKIPPED rather than refused. A pattern that silently ignores what
        /// it does not recognise is the same defect as a guard that passes when it cannot read its input.
        ///
        /// So the prefix convention is gone. The last cell of a row is the "Asserted by" cell and every
        /// backticked token in it is claimed to be a test; each one must EXIST and must CALL Fixture(stem).
        /// Scoped to the last cell because the other cells legitimately carry backticked FORMAT text
        /// (`slots:`, `schemaVersion: 1`), which is prose about the fixture, not a claim about a test.</summary>
        internal static List<string> ReadmeTableProblems(string readme, string source, IReadOnlyCollection<string> onDisk)
        {
            var problems = new List<string>();
            var rows = System.Text.RegularExpressions.Regex.Matches(readme, @"(?m)^\|\s*`([A-Za-z0-9._-]+\.md)`\s*\|(.*)$");
            var rowFiles = rows.Select(r => r.Groups[1].Value).ToList();

            foreach (var dup in rowFiles.GroupBy(f => f, StringComparer.Ordinal).Where(g => g.Count() > 1))
                problems.Add($"the README lists '{dup.Key}' on {dup.Count()} rows. Duplicating one row while deleting another keeps the totals equal and loses a fixture.");
            foreach (var missing in onDisk.Where(f => !rowFiles.Contains(f, StringComparer.Ordinal)).OrderBy(f => f, StringComparer.Ordinal))
                problems.Add($"'{missing}' is on disk but has no README row, so nothing says what it pins.");
            foreach (var ghost in rowFiles.Where(f => !onDisk.Contains(f, StringComparer.Ordinal)).OrderBy(f => f, StringComparer.Ordinal))
                problems.Add($"the README has a row for '{ghost}', which is not on disk.");

            foreach (System.Text.RegularExpressions.Match row in rows)
            {
                var file = row.Groups[1].Value;
                if (!onDisk.Contains(file, StringComparer.Ordinal)) continue;   // already reported as a ghost
                var stem = Path.GetFileNameWithoutExtension(file);

                // The LAST non-empty cell is "Asserted by". Split on the pipe the table is built from; a
                // trailing empty cell comes from the row's closing '|'.
                var cells = row.Groups[2].Value.Split('|').Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
                var assertedBy = cells.Count > 0 ? cells[cells.Count - 1] : "";

                // Anything that could be a C# method name. Deliberately NOT a naming convention: an
                // identifier-shaped token in this cell is a claim about a test, and a claim that names
                // nothing real is the failure, not something to pass over.
                var named = System.Text.RegularExpressions.Regex.Matches(assertedBy, @"`([A-Za-z_][A-Za-z0-9_]*)`")
                    .Select(m => m.Groups[1].Value).ToList();
                if (named.Count == 0)
                {
                    problems.Add($"the README row for '{file}' names no test. A row that names nothing asserts nothing, so it is worse than no row: it reads as coverage. Name a test that calls Fixture(\"{stem}\"), or delete the row.");
                    continue;
                }
                foreach (var test in named)
                {
                    var body = MethodBody(source, test);
                    if (body == null)
                    {
                        problems.Add($"README row for '{file}' names test '{test}', which is not in WorkflowV2SolRoundTwoTests.cs.");
                        continue;
                    }
                    if (!body.Contains($"Fixture(\"{stem}\")", StringComparison.Ordinal))
                        problems.Add($"README row for '{file}' names test '{test}', which does not call Fixture(\"{stem}\"). Mentioning the file is not reading it.");
                }
            }
            return problems;
        }

        /// <summary>The source text of one method, from its signature to the START OF THE NEXT class member.
        ///
        /// Not brace matching, deliberately. A brace walk over raw source counts braces inside string
        /// literals and comments, so a single <c>var marker = "{";</c> in one test would extend the slice
        /// into the tests below it and let their content answer for this one (Sol round 8). Slicing to the
        /// next member cannot do that: every member here begins with an attribute or a modifier at
        /// class-member indentation, so the boundary is always at or before the real end of the method.
        ///
        /// The error direction is the safe one. Ending early can only make a check FAIL when a Fixture call
        /// sits past the boundary, which is loud and fixable; it can never let a test pass by borrowing
        /// another method's text.</summary>
        private static string MethodBody(string source, string methodName)
        {
            var at = source.IndexOf(" " + methodName + "(", StringComparison.Ordinal);
            if (at < 0) return null;
            var open = source.IndexOf('{', at);
            if (open < 0) return null;
            var next = System.Text.RegularExpressions.Regex.Match(
                source.Substring(open), @"(?m)^        (\[|public |private |internal |static )");
            return next.Success ? source.Substring(open, next.Index) : source.Substring(open);
        }

        // ---- BEGIN FINGERPRINT (must match Semanticus.Tests/tools/prev2-oracle/Program.cs exactly).
        // Nothing compares the two source texts, on purpose: the goldens do it better. A golden is WRITTEN
        // by that copy and COMPARED by this one, so any divergence fails the comparison below with the
        // differing line in the diff. The line-based drift check that used to sit in capture.ps1 was
        // deleted in round 7 for being weaker than it looked. ----
        /// <summary>Every v1-observable field of a parsed def, flattened deterministically. Exhaustive over
        /// what <c>ParseFile</c> sets, with ONE stated exclusion: a field left out is a field the guard would
        /// let change silently, and an unstated exclusion is worse still, because the word "exhaustive" then
        /// covers for it.
        ///
        /// <c>Source</c> and <c>FilePath</c> were that unstated pair until Sol round 6. <c>Source</c> is now
        /// fingerprinted. <c>FilePath</c> is fingerprinted as its FILE NAME only, deliberately: the full
        /// value is an absolute path that differs on every machine and in CI, so a golden carrying it could
        /// only ever pass where it was captured. The file name is the part of it that is the parser's answer
        /// rather than the checkout's.
        ///
        /// v2-only members are absent on purpose, because the golden comes from a parser that predates
        /// them.</summary>
        private static string Fingerprint(string rel, WorkflowDef d)
        {
            var sb = new StringBuilder();
            void L(string s) => sb.Append(s).Append('\n');

            L("FILE " + rel);
            L("  error=" + (d.Error ?? "<null>"));
            L("  source=" + (d.Source ?? "<null>") + " file=" + (d.FilePath == null ? "<null>" : Path.GetFileName(d.FilePath)));
            L("  name=" + (d.Name ?? "<null>") + " kind=" + (d.Kind ?? "<null>") + " version=" + d.Version
              + " strictness=" + (d.Strictness ?? "<null>"));
            L("  title=" + (d.Title ?? "<null>"));
            L("  description=" + (d.Description ?? "<null>"));
            L("  whenToUse=" + (d.WhenToUse ?? "<null>"));
            L("  triggers=[" + string.Join("|", d.Triggers) + "]");
            L("  tags=[" + string.Join("|", d.Tags) + "]");
            foreach (var kv in d.Provenance.OrderBy(k => k.Key, StringComparer.Ordinal))
                L("  provenance " + kv.Key + "=" + kv.Value);
            foreach (var s in d.Slots)
                L("  slot name=" + s.Name + " type=" + s.Type + " required=" + s.Required
                  + " default=" + (s.Default ?? "<null>") + " example=" + (s.Example ?? "<null>")
                  + " hint=" + (s.Hint ?? "<null>") + " values=[" + string.Join("|", s.Values) + "]"
                  + " question=" + (s.Question ?? "<null>"));
            foreach (var st in d.Steps)
            {
                L("  step id=" + st.Id + " number=" + st.Number + " title=" + st.Title);
                L("    ops=[" + string.Join("|", st.Ops) + "]");
                L("    instructions.len=" + (st.Instructions?.Length ?? -1)
                  + " instructions.hash=" + Hash(st.Instructions));
                if (st.Gate == null) { L("    gate=<null>"); continue; }
                L("    gate strictness=" + (st.Gate.Strictness ?? "<null>"));
                foreach (var i in st.Gate.Inputs)
                    L("    input name=" + i.Name + " type=" + i.Type + " required=" + i.Required
                      + " daxPurity=" + (i.DaxPurity ?? "<null>") + " question=" + (i.Question ?? "<null>"));
                foreach (var v in st.Gate.Verify)
                    L("    verify kind=" + v.Kind + " when=" + (v.When ?? "<null>") + " probe=" + (v.Probe ?? "<null>")
                      + " scope=" + (v.Scope ?? "<null>") + " intent=" + (v.Intent ?? "<null>")
                      + " pinned=[" + string.Join("|", v.PinnedShapes) + "] open=[" + string.Join("|", v.OpenShapes) + "]"
                      + " openShapesFrom=" + (v.OpenShapesFrom ?? "<null>")
                      + " openMismatch=" + (v.OpenMismatch ?? "<null>") + " anchors=" + (v.Anchors ?? "<null>"));
            }
            return sb.ToString().TrimEnd('\n');
        }
        // ---- END FINGERPRINT ----

        private static string Hash(string s)
        {
            if (s == null) return "<null>";
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(s))).Substring(0, 16);
        }
    }
}
