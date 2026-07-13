using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The model-wide workflow-ENFORCEMENT toggle (Kane's "pro mode" button): a user doing a quick task can
    /// turn gate enforcement off wholesale without editing any workflow file. Pinned here: the global mode
    /// tops the WHOLE strictness resolution (above per-gate 'hard' — the stock seeds carry those, so anything
    /// weaker would be a dead switch); off ⇒ gated workflows list as un-gated, start FREE (enforcement is what's
    /// paid), and gates are skipped with the honest note; the settings write merges (per-workflow overrides
    /// survive); and the run freezes the mode at start (a mid-run toggle can't tear a run).
    /// </summary>
    public sealed class WorkflowEnforcementTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        private const string GatedMd = @"---
name: gated-toggle
title: Gated toggle vehicle
strictness: warn
---
## Step 1: Ask
Ask the one question.
```yaml gate
strictness: hard
inputs:
  - name: confirmed
    question: ""Did you check?""
    type: text
    required: required
```
";

        private static string NewWorkspace()
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-wfenf-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(ws, ".semanticus", "workflows"));
            File.WriteAllText(Path.Combine(ws, ".semanticus", "workflows", "gated-toggle.md"), GatedMd);
            return ws;
        }

        // ---- resolution: the global mode tops per-gate, per-workflow AND frontmatter -------------------------------

        [Fact]
        public void Global_mode_tops_the_strictness_resolution()
        {
            var def = new WorkflowDef { Strictness = "warn" };
            var gate = new GateSpec { Strictness = "hard" };
            Assert.Equal("off", WorkflowRunner.EffectiveStrictness(def, gate, "warn", "off"));   // global beats per-gate hard
            Assert.Equal("hard", WorkflowRunner.EffectiveStrictness(def, gate, "warn", null));   // no global → per-gate wins
            Assert.Equal("warn", WorkflowRunner.EffectiveStrictness(def, null, "warn", null));   // settings beat frontmatter
            Assert.Equal("hard", WorkflowRunner.EffectiveStrictness(new WorkflowDef(), null, null, null)); // engine default
        }

        // ---- the toggle end-to-end: list flips, free start, gate skipped honestly ----------------------------------

        [Fact]
        public async Task Enforcement_off_ungates_the_library_frees_the_start_and_skips_the_gate()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: false), ws);   // FREE tier on purpose
            try
            {
                using (engine)
                {
                    // Enforced by default: the per-gate 'hard' gates it, so a free start is refused.
                    var before = (await engine.ListWorkflowsAsync()).First(w => w.Name == "gated-toggle");
                    Assert.True(before.Gated);
                    await Assert.ThrowsAsync<EntitlementException>(() => engine.StartWorkflowAsync("gated-toggle", "human"));

                    // Toggle off: reported honestly, library re-lists un-gated.
                    var off = await engine.SetWorkflowEnforcementAsync("off", "human");
                    Assert.Equal("off", off.Mode);
                    Assert.False(off.Enforced);
                    var after = (await engine.ListWorkflowsAsync()).First(w => w.Name == "gated-toggle");
                    Assert.False(after.Gated);

                    // A free start now succeeds, and the gate is SKIPPED with the honest note — not silently passed.
                    var run = await engine.StartWorkflowAsync("gated-toggle", "human");
                    var done = await engine.SubmitWorkflowStepAsync(run.RunId, "step-1", "{}", "human");   // no answers needed: gate off
                    Assert.Equal("completed", done.Status);
                    Assert.Equal("passed", done.Steps[0].Status);
                    Assert.Contains("off", done.Steps[0].Note ?? "");
                    Assert.Equal("off", done.Steps[0].EffectiveStrictness);

                    // Restore: 'default' clears the override and the gate bites again.
                    var restored = await engine.SetWorkflowEnforcementAsync("default", "human");
                    Assert.Null(restored.Mode);
                    Assert.True(restored.Enforced);
                    Assert.True((await engine.ListWorkflowsAsync()).First(w => w.Name == "gated-toggle").Gated);
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        // ---- the settings write merges: per-workflow overrides survive the toggle ----------------------------------

        [Fact]
        public async Task Set_enforcement_preserves_per_workflow_overrides_and_rejects_junk()
        {
            var ws = NewWorkspace();
            var settingsFile = Path.Combine(ws, ".semanticus", "workflow-settings.json");
            File.WriteAllText(settingsFile, "{ \"workflows\": { \"gated-toggle\": { \"strictness\": \"warn\" } } }");
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(false), ws);
            try
            {
                using (engine)
                {
                    await engine.SetWorkflowEnforcementAsync("off", "human");
                    var json = File.ReadAllText(settingsFile);
                    Assert.Contains("\"strictness\": \"off\"", json);      // the global override landed…
                    Assert.Contains("gated-toggle", json);                  // …and the per-workflow subtree survived

                    await engine.SetWorkflowEnforcementAsync(null, "human");   // null == 'default' == clear
                    json = File.ReadAllText(settingsFile);
                    Assert.DoesNotContain("\"strictness\": \"off\"", json);
                    Assert.Contains("gated-toggle", json);

                    await Assert.ThrowsAsync<ArgumentException>(() => engine.SetWorkflowEnforcementAsync("sometimes", "human"));
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        // ---- a run freezes the mode at start: a mid-run toggle can't tear the run ----------------------------------

        [Fact]
        public async Task A_running_workflow_keeps_the_enforcement_it_started_with()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: true), ws);
            try
            {
                using (engine)
                {
                    var run = await engine.StartWorkflowAsync("gated-toggle", "human");   // enforced at start
                    await engine.SetWorkflowEnforcementAsync("off", "human");             // toggled mid-run

                    // The frozen run still enforces: an unanswered required input is refused.
                    await Assert.ThrowsAsync<InvalidOperationException>(
                        () => engine.SubmitWorkflowStepAsync(run.RunId, "step-1", "{}", "human"));
                    var view = await engine.GetWorkflowRunAsync(run.RunId);
                    Assert.Equal("active", view.Status);
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        // ---- FAIL SAFE on write: a corrupt file is preserved aside BYTE-FOR-BYTE, never reset to empty -------------
        // Audit finding: every settings writer did `try Parse … catch { root = new() }`, so a parse failure RESET the
        // file to empty — destroying every binding/override. Review follow-up (sol): the aside must preserve the EXACT
        // bytes — a ReadAllText→WriteAllText round-trip TRANSCODES (drops a BOM, mangles invalid sequences), defeating
        // the preservation. Neuter: write the aside from the decoded string and the byte compare fails.
        [Fact]
        public async Task A_settings_write_preserves_a_corrupt_file_aside_byte_for_byte()
        {
            var ws = NewWorkspace();
            var settingsFile = Path.Combine(ws, ".semanticus", "workflow-settings.json");
            // A hand-corrupted file with a UTF-8 BOM AND an invalid UTF-8 byte (0xFF) — any string round-trip
            // would strip the BOM and replace the bad byte, so only true byte preservation passes.
            var corrupt = new byte[] { 0xEF, 0xBB, 0xBF }
                .Concat(System.Text.Encoding.UTF8.GetBytes("{ this is not valid json ]"))
                .Concat(new byte[] { 0xFF }).ToArray();
            File.WriteAllBytes(settingsFile, corrupt);
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(false), ws);
            try
            {
                using (engine)
                {
                    // The write succeeds (it repairs the file) but does NOT silently lose the old bytes.
                    await engine.SetWorkflowEnforcementAsync("off", "human");
                    Assert.Contains("\"strictness\": \"off\"", File.ReadAllText(settingsFile));   // fresh, valid content

                    // The unreadable bytes were copied aside to a .corrupt-<hex> sibling BEFORE the overwrite — exactly.
                    var aside = Directory.EnumerateFiles(Path.Combine(ws, ".semanticus"), "workflow-settings.json.corrupt-*").ToList();
                    Assert.Single(aside);
                    Assert.True(corrupt.SequenceEqual(File.ReadAllBytes(aside[0])), "the aside must be the EXACT original bytes");
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        // ---- and the flip side: a VALID file in a non-UTF-8-no-BOM encoding is parsed, not flagged corrupt ---------
        // The corrupt check decodes BOM-aware (matching File.ReadAllText): a valid UTF-16 settings file (PowerShell
        // 5's Out-File default on Windows) must merge normally. Neuter: decode with plain UTF-8 (no BOM detection)
        // and the UTF-16 file reads as garbage → mis-flagged corrupt → an aside appears — this test fails.
        [Fact]
        public async Task A_valid_utf16_settings_file_is_parsed_not_treated_as_corrupt()
        {
            var ws = NewWorkspace();
            var settingsFile = Path.Combine(ws, ".semanticus", "workflow-settings.json");
            File.WriteAllText(settingsFile, "{ \"workflows\": { \"gated-toggle\": { \"strictness\": \"warn\" } } }",
                System.Text.Encoding.Unicode);   // UTF-16LE with BOM
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(false), ws);
            try
            {
                using (engine)
                {
                    await engine.SetWorkflowEnforcementAsync("off", "human");
                    var json = File.ReadAllText(settingsFile);
                    Assert.Contains("\"strictness\": \"off\"", json);   // the write landed…
                    Assert.Contains("gated-toggle", json);              // …as a MERGE — the seeded override survived
                    Assert.Empty(Directory.EnumerateFiles(Path.Combine(ws, ".semanticus"), "workflow-settings.json.corrupt-*"));
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        // ---- a bad byte INSIDE a JSON string is corruption, not silently swallowed by replacement decoding --------
        // Review follow-up (sol): the BOM-aware decode used the default replacement fallback, so an invalid UTF-8 byte
        // inside a string value decoded to U+FFFD, the file PARSED clean, and the rewrite destroyed the original bytes
        // with NO aside. Strict (throwing) decoding now surfaces the bad byte as corruption. Neuter: decode with the
        // default Encoding.UTF8 (replacement fallback) and the file parses clean → no aside appears → this test fails.
        [Fact]
        public async Task A_bad_byte_inside_a_json_string_is_preserved_aside_not_silently_repaired()
        {
            var ws = NewWorkspace();
            var settingsFile = Path.Combine(ws, ".semanticus", "workflow-settings.json");
            // Syntactically-valid JSON EXCEPT one invalid UTF-8 byte (0xFF) INSIDE a string value. A replacement
            // decode would turn it into U+FFFD and parse this as a healthy object; only strict decoding sees the flaw.
            var corrupt = System.Text.Encoding.ASCII.GetBytes("{ \"workflows\": { \"gated-toggle\": { \"strictness\": \"wa")
                .Concat(new byte[] { 0xFF })
                .Concat(System.Text.Encoding.ASCII.GetBytes("rn\" } } }")).ToArray();
            File.WriteAllBytes(settingsFile, corrupt);
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(false), ws);
            try
            {
                using (engine)
                {
                    await engine.SetWorkflowEnforcementAsync("off", "human");                    // the write still succeeds…
                    Assert.Contains("\"strictness\": \"off\"", File.ReadAllText(settingsFile));   // …repairing the file

                    var aside = Directory.EnumerateFiles(Path.Combine(ws, ".semanticus"), "workflow-settings.json.corrupt-*").ToList();
                    Assert.Single(aside);
                    Assert.True(corrupt.SequenceEqual(File.ReadAllBytes(aside[0])), "the aside must be the EXACT original bytes");
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        // ---- the strict decode must survive BOM detection: a bad sequence in a BOM'd file is still corruption -----
        // Review follow-up (sol, round 4): handing a throwing UTF8Encoding to StreamReader with
        // detectEncodingFromByteOrderMarks:true is a trap — on a UTF-16/UTF-32 BOM the reader SWAPS IN framework
        // encodings with REPLACEMENT fallback, so an invalid sequence in a BOM'd file still decoded to U+FFFD, parsed
        // clean, and was rewritten with no aside (exactly the file family the BOM path exists for). The BOM is now
        // sniffed by us and the remainder decoded with a THROWING instance of the matching encoding. Neuter: go back
        // to StreamReader-with-BOM-detection and the unpaired surrogate is silently repaired → no aside → this fails.
        [Fact]
        public async Task A_bad_sequence_inside_a_utf16_bom_file_is_preserved_aside_not_silently_repaired()
        {
            var ws = NewWorkspace();
            var settingsFile = Path.Combine(ws, ".semanticus", "workflow-settings.json");
            // UTF-16LE BOM + syntactically-valid JSON EXCEPT an UNPAIRED HIGH SURROGATE (U+D800, LE bytes 00 D8)
            // inside a string value. A replacement decode turns it into U+FFFD and the file parses as healthy;
            // only a strict UTF-16 decode sees the flaw.
            var corrupt = new byte[] { 0xFF, 0xFE }
                .Concat(System.Text.Encoding.Unicode.GetBytes("{ \"workflows\": { \"gated-toggle\": { \"strictness\": \"wa"))
                .Concat(new byte[] { 0x00, 0xD8 })
                .Concat(System.Text.Encoding.Unicode.GetBytes("rn\" } } }")).ToArray();
            File.WriteAllBytes(settingsFile, corrupt);
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(false), ws);
            try
            {
                using (engine)
                {
                    await engine.SetWorkflowEnforcementAsync("off", "human");                    // the write still succeeds…
                    Assert.Contains("\"strictness\": \"off\"", File.ReadAllText(settingsFile));   // …repairing the file

                    var aside = Directory.EnumerateFiles(Path.Combine(ws, ".semanticus"), "workflow-settings.json.corrupt-*").ToList();
                    Assert.Single(aside);
                    Assert.True(corrupt.SequenceEqual(File.ReadAllBytes(aside[0])), "the aside must be the EXACT original bytes");
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        // ---- table-driven: EVERY strict-decode branch rejects bad input and accepts clean input --------------------
        // Review follow-up (sol, round 5): only the UTF-16LE branch was pinned — a regression in a constructor flag
        // (throwOnInvalidBytes/Characters) or a preamble skip in any OTHER branch would silently restore replacement
        // decoding with green tests. One row per encoding: label, BOM preamble, a BOM-less encoder, and an invalid
        // sequence in that encoding (an unpaired surrogate code unit, or a bare invalid byte for UTF-8).
        public static IEnumerable<object[]> BomEncodings() => new[]
        {
            new object[] { "utf8-bom", new byte[] { 0xEF, 0xBB, 0xBF },       System.Text.Encoding.UTF8,             new byte[] { 0xFF } },
            new object[] { "utf16be",  new byte[] { 0xFE, 0xFF },             System.Text.Encoding.BigEndianUnicode, new byte[] { 0xD8, 0x00 } },
            new object[] { "utf32le",  new byte[] { 0xFF, 0xFE, 0x00, 0x00 }, new System.Text.UTF32Encoding(bigEndian: false, byteOrderMark: false), new byte[] { 0x00, 0xD8, 0x00, 0x00 } },
            new object[] { "utf32be",  new byte[] { 0x00, 0x00, 0xFE, 0xFF }, new System.Text.UTF32Encoding(bigEndian: true,  byteOrderMark: false), new byte[] { 0x00, 0x00, 0xD8, 0x00 } },
        };
        public static IEnumerable<object[]> BomEncodingsValid() => BomEncodings().Select(r => new[] { r[0], r[1], r[2] });

        [Theory]
        [MemberData(nameof(BomEncodings))]
        public async Task A_bad_sequence_in_every_bom_encoding_is_preserved_aside(string label, byte[] bom, System.Text.Encoding enc, byte[] invalid)
        {
            var ws = NewWorkspace();
            var settingsFile = Path.Combine(ws, ".semanticus", "workflow-settings.json");
            // Syntactically-valid JSON except the invalid sequence INSIDE a string value — replacement decoding
            // would repair it to U+FFFD and parse clean; only the strict branch for this encoding sees the flaw.
            var corrupt = bom
                .Concat(enc.GetBytes("{ \"workflows\": { \"gated-toggle\": { \"strictness\": \"wa"))
                .Concat(invalid)
                .Concat(enc.GetBytes("rn\" } } }")).ToArray();
            File.WriteAllBytes(settingsFile, corrupt);
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(false), ws);
            try
            {
                using (engine)
                {
                    await engine.SetWorkflowEnforcementAsync("off", "human");                    // the write still succeeds…
                    Assert.Contains("\"strictness\": \"off\"", File.ReadAllText(settingsFile));   // …repairing the file

                    var aside = Directory.EnumerateFiles(Path.Combine(ws, ".semanticus"), "workflow-settings.json.corrupt-*").ToList();
                    Assert.True(aside.Count == 1, $"[{label}] expected exactly one corrupt aside, found {aside.Count}");
                    Assert.True(corrupt.SequenceEqual(File.ReadAllBytes(aside[0])), $"[{label}] the aside must be the EXACT original bytes");
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        [Theory]
        [MemberData(nameof(BomEncodingsValid))]
        public async Task A_valid_file_in_every_bom_encoding_is_parsed_not_treated_as_corrupt(string label, byte[] bom, System.Text.Encoding enc)
        {
            var ws = NewWorkspace();
            var settingsFile = Path.Combine(ws, ".semanticus", "workflow-settings.json");
            var valid = bom.Concat(enc.GetBytes("{ \"workflows\": { \"gated-toggle\": { \"strictness\": \"warn\" } } }")).ToArray();
            File.WriteAllBytes(settingsFile, valid);
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(false), ws);
            try
            {
                using (engine)
                {
                    await engine.SetWorkflowEnforcementAsync("off", "human");
                    var json = File.ReadAllText(settingsFile);
                    Assert.Contains("\"strictness\": \"off\"", json);   // the write landed…
                    Assert.Contains("gated-toggle", json);              // …as a MERGE — the strict decoder accepted the clean file
                    Assert.True(!Directory.EnumerateFiles(Path.Combine(ws, ".semanticus"), "workflow-settings.json.corrupt-*").Any(),
                        $"[{label}] a valid file must not be flagged corrupt");
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        // ---- concurrent writers don't lose updates: the lock + atomic write keeps every mutation ------------------
        [Fact]
        public async Task Concurrent_settings_writes_do_not_lose_updates()
        {
            var ws = NewWorkspace();
            var settingsFile = Path.Combine(ws, ".semanticus", "workflow-settings.json");
            // Seed a per-workflow override that both concurrent writers must preserve.
            File.WriteAllText(settingsFile, "{ \"workflows\": { \"gated-toggle\": { \"strictness\": \"warn\" } } }");
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(true), ws);
            try
            {
                using (engine)
                {
                    // Two writers touching DIFFERENT keys race; the serialized read-modify-write means neither is lost
                    // and the seeded override survives both.
                    await Task.WhenAll(
                        engine.SetWorkflowEnforcementAsync("hard", "human"),
                        engine.SetWorkflowEnabledAsync("gated-toggle", false, "human"));

                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsFile));   // valid, not half-written
                    var root = doc.RootElement;
                    Assert.Equal("hard", root.GetProperty("strictness").GetString());               // the enforcement update landed
                    var wf = root.GetProperty("workflows").GetProperty("gated-toggle");
                    Assert.False(wf.GetProperty("enabled").GetBoolean());                            // the enabled update landed
                    Assert.Equal("warn", wf.GetProperty("strictness").GetString());                  // the seeded override survived both
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }
    }
}
