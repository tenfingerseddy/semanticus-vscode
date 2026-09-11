// ============================================================================================
// The capture harness for the v1-compatibility GOLDENS (WorkflowV1CompatibilityTests).
//
// WHAT IT DOES. It prints the same fingerprint that test computes, but from the parser as it
// stood BEFORE format v2 existed: WorkflowParser.cs and Workflow.cs are taken verbatim from
// `origin/main` and compiled here, in isolation from today's engine.
//
// WHY IT EXISTS AT ALL. A golden regenerated from the parser under test proves nothing: it would
// agree with any change, including a wrong one. The oracle has to be a DIFFERENT implementation,
// and the only honest "different implementation" of "what did v1 do" is the code that did it.
// This harness is retained in the repo so that claim can be checked by anyone, instead of resting
// on a scratch directory somebody deleted and three stubs nobody read (Sol round 6, finding 4).
//
// HOW TO RE-RUN, from the repo root:
//     pwsh -File Semanticus.Tests/tools/prev2-oracle/capture.ps1
// That fetches the pre-v2 sources itself, builds this project, and rewrites both goldens.
//
// THE ONE RULE. The output must always come from `origin/main`'s parser. Never regenerate a
// golden from the parser under test, not even "just to see the diff": the moment you do, the
// guard becomes a mirror. If a golden genuinely has to change, the reason is that the pre-v2
// parser's own answer changed, which for a frozen ruleset means something is wrong.
//
// Fingerprint below is a VERBATIM copy of WorkflowV1CompatibilityTests.Fingerprint. The two must stay
// identical, and the ENFORCEMENT is the golden itself, not a source-text check: a golden is written by
// this copy and compared by the test's copy, so any divergence between them shows up as a failing golden
// comparison naming the exact line. capture.ps1 had a line-based drift check as well; it was deleted in
// round 7 because it could not see a continuation line or Hash(), and a weak check that reads as a strong
// one is the failure this slice keeps repeating. See the long comment in capture.ps1.
// ============================================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Semanticus.Engine;

static class Program
{
    static string Hash(string s)
    {
        if (s == null) return "<null>";
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(s))).Substring(0, 16);
    }

    // ---- BEGIN FINGERPRINT (must match WorkflowV1CompatibilityTests.Fingerprint exactly) ----
    static string Fingerprint(string rel, WorkflowDef d)
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

    /// <summary>Proves BEHAVIOURALLY that the parser compiled into this harness predates format v2, before
    /// a single fingerprint is written.
    ///
    /// The previous check grepped the fetched source for the word `schemaVersion`, which asked how the
    /// parser is SPELLED rather than what it DOES. It was wrong in both directions: a pre-v2 commit whose
    /// comments merely mention the word would be rejected, and a post-v2 parser that reads the constant
    /// from another file would sail through (Sol round 8).
    ///
    /// So ask the parser instead. Both probes below are files that v1 ACCEPTS and v2 REFUSES: an unknown
    /// frontmatter key, which v1 preserves and v2 calls an error, and a version far above anything this
    /// format has, which v1 sees as an ordinary unknown key and v2 refuses outright. A parser that accepts
    /// both cannot be enforcing v2 rules. This cannot be fooled by a comment or by moving a constant,
    /// because it is a statement about behaviour.</summary>
    static void RefuseIfThisIsNotThePreV2Parser()
    {
        var probes = new[]
        {
            ("an unknown frontmatter key", "---\nschemaVersion: 2\nname: t\nbogusKeyV2Refuses: x\n---\n## Step 1: Do\nBody."),
            ("a version above this format",  "---\nschemaVersion: 99\nname: t\n---\n## Step 1: Do\nBody."),
        };
        foreach (var (what, text) in probes)
        {
            var def = WorkflowParser.Parse(text);
            if (def.Error != null)
                throw new InvalidOperationException(
                    $"the pinned parser REFUSED {what}: \"{def.Error}\". A v1 parser accepts that file, so this "
                    + "is not the pre-v2 parser and capturing from it would make the goldens a mirror of the "
                    + "code under test. Fix the pin in capture.ps1; do not edit this check.");
        }
        Console.Error.WriteLine("behavioural check: the pinned parser accepts both files that v2 refuses, as a pre-v2 parser must");
    }

    static int Main(string[] args)
    {
        RefuseIfThisIsNotThePreV2Parser();

        // args are: <outFile> then (absoluteDir, relPrefix) pairs, in the order the golden lists them.
        //
        // The output is written HERE rather than piped to the caller. A shipped workflow's text carries
        // arrows, ellipses and middle dots, and a PowerShell pipeline decodes a child process's stdout with
        // the console code page, which mangled exactly those characters and produced a golden that differed
        // from the parser's real answer on eleven lines. A fingerprint that cannot survive its own transport
        // is not an oracle.
        if (args.Length < 3 || args.Length % 2 == 0)
        {
            Console.Error.WriteLine("usage: prev2-oracle <outFile> <dir> <relPrefix> [<dir> <relPrefix> ...]");
            return 2;
        }
        var parts = new List<string>();
        for (int a = 1; a + 1 < args.Length; a += 2)
        {
            // README.md documents a fixture directory; it is not a fixture. The test applies the same
            // exclusion, and the two must stay identical or the golden covers a different set than the guard.
            var files = Directory.GetFiles(args[a], "*.md")
                .Where(p => !Path.GetFileName(p).StartsWith("README", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal).ToArray();
            foreach (var f in files)
                parts.Add(Fingerprint(args[a + 1] + "/" + Path.GetFileName(f),
                    WorkflowParser.ParseFile(f, "stock")));
        }
        File.WriteAllText(args[0], string.Join("\n", parts) + "\n", new UTF8Encoding(false));
        Console.Error.WriteLine($"wrote {args[0]} ({parts.Count} files)");
        return 0;
    }
}
