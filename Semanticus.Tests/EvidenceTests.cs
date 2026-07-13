using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Semanticus.Engine.Evidence;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The Evidence library contract (Rule 2 of the complexity doctrine): ONE artifact format + ONE renderer for
    /// everything the product proves. These pin the load-bearing invariants: a stable content hash over canonical
    /// JSON that excludes itself; tamper detection with a plain-language reason; the two STRUCTURAL rules (an
    /// overridden verdict owes a reason; a verdict badge owes coverage); every section type renders; a golden-file
    /// pin; byte determinism; no em-dashes in any emitted literal; and both themes in the stylesheet.
    /// </summary>
    public sealed class EvidenceTests
    {
        private const char EmDash = '—';

        // ---- fixtures ----

        // A rich, fully-fixed document exercising EVERY section type. Fixed id + date + sealed hash, so it doubles
        // as the golden-file fixture and stays deterministic. No em-dashes anywhere in its content, on purpose.
        private static EvidenceDoc FullDoc()
        {
            var doc = new EvidenceDoc
            {
                Id = "ev-0001-golden",
                Kind = "test-suite",
                Title = "Zephyr revenue model review",
                CreatedUtc = "2026-07-10T09:30:00Z",
                Producer = "run_tests",
                ProducerVersion = "0.2.0",
                Origin = "agent",
                ModelName = "Contoso Sales",
                ModelFingerprint = "fp-abc123",
                BaseCommit = "0123456789abcdef0123456789abcdef01234567",
                SessionId = "sess-42",
                Revision = 17,
                Verdict = Verdict.NeedsReview,
                VerdictCounts = new Dictionary<string, int>
                {
                    ["Verified"] = 34,
                    ["NeedsReview"] = 2,
                    ["Broken"] = 0,
                    ["Unknown"] = 5,
                },
                Coverage = new Coverage { Verified = 34, Total = 41, Unknowns = 5 },
                Sections = new List<EvidenceSection>
                {
                    new SummarySection
                    {
                        Title = "Summary",
                        Paragraphs = new List<string>
                        {
                            "The model passed most checks. Five could not be verified without a live connection.",
                            "Two measures need a human to confirm the intended grain.",
                        },
                    },
                    new KeyValueSection
                    {
                        Title = "At a glance",
                        Pairs = new List<KeyValuePairRow>
                        {
                            new KeyValuePairRow { Key = "Tables", Value = "12" },
                            new KeyValuePairRow { Key = "Measures", Value = "41" },
                            new KeyValuePairRow { Key = "Relationships", Value = "15" },
                        },
                    },
                    new FindingsSection
                    {
                        Title = "Findings",
                        Rows = new List<FindingRow>
                        {
                            new FindingRow { Name = "Total Sales returns a number", Verdict = Verdict.Verified, Detail = "Matched the reference within tolerance." },
                            new FindingRow { Name = "Margin grain", Verdict = Verdict.NeedsReview, Detail = "Ambiguous at the product level.", Count = 2 },
                            new FindingRow { Name = "Live row counts", Verdict = Verdict.Unknown, Detail = "No connection was available.", Count = 5 },
                        },
                    },
                    new DiffSection
                    {
                        Title = "Rewrite of Total Sales",
                        Language = "dax",
                        Before = "SUM ( Sales[Amount] )",
                        After = "SUMX ( Sales, Sales[Qty] * Sales[Price] )",
                    },
                    new ProbeSection
                    {
                        Title = "Probes",
                        Probes = new List<ProbeRow>
                        {
                            new ProbeRow { Query = "EVALUATE ROW(\"v\", [Total Sales])", Expected = "1,000", Actual = "1,000", Verdict = Verdict.Verified, DurationMs = 12.5 },
                            new ProbeRow { Query = "EVALUATE ROW(\"v\", [Margin])", Expected = "0.30", Actual = "0.42", Verdict = Verdict.Broken, DurationMs = 9 },
                        },
                    },
                    new StepsSection
                    {
                        Title = "Workflow steps",
                        Steps = new List<StepRow>
                        {
                            new StepRow { Name = "Capture baseline", Verdict = Verdict.Verified, Note = "Snapshot taken.", WhenUtc = "2026-07-10T09:28:00Z" },
                            new StepRow { Name = "Apply rewrite", Verdict = Verdict.Verified, Note = "One measure changed." },
                            new StepRow { Name = "Confirm grain", Verdict = Verdict.NeedsReview, Note = "Handed to a reviewer." },
                        },
                    },
                    new NoteSection
                    {
                        Title = "Heads up",
                        Text = "Run this again with a live connection to clear the unknowns.",
                        Tone = "warning",
                    },
                },
                PrevHash = "00ff00ff00ff00ff00ff00ff00ff00ff00ff00ff00ff00ff00ff00ff00ff00ff",
            };
            doc.ContentHash = EvidenceHash.Compute(doc);   // seal so the footer shows a stable signature
            return doc;
        }

        private static EvidenceDoc SimpleDoc() => new EvidenceDoc
        {
            Id = "ev-simple",
            Kind = "deploy",
            Title = "Deploy record",
            CreatedUtc = "2026-07-10T10:00:00Z",
            Producer = "deploy_live",
            Origin = "human",
            ModelName = "Contoso Sales",
            Verdict = Verdict.Verified,
            Coverage = new Coverage { Verified = 3, Total = 3, Unknowns = 0 },
        };

        // The injection payload, packed into EVERY string-bearing field of XssDoc: a script element, an img/onerror
        // vector, an attribute-breakout attempt, plus a quote, an apostrophe and an ampersand. If the single H()
        // chokepoint ever regresses, one of these escapes to a live tag or attribute and the XSS test fails.
        private const string Payload = "</title><script>alert(1)</script><img src=x onerror=alert(1)>\" onmouseover=\"x\" 'it' & <b>";

        private static EvidenceDoc XssDoc() => new EvidenceDoc
        {
            Id = Payload,
            Kind = Payload,
            Title = Payload,
            CreatedUtc = Payload,
            Producer = Payload,
            ProducerVersion = Payload,
            Origin = Payload,
            ModelName = Payload,
            ModelFingerprint = Payload,
            BaseCommit = Payload,
            SessionId = Payload,
            Verdict = Verdict.Overridden,
            OverrideReason = Payload,
            Coverage = new Coverage { Verified = 1, Total = 2, Unknowns = 1 },
            ContentHash = Payload,
            PrevHash = Payload,
            Sections = new List<EvidenceSection>
            {
                new SummarySection { Title = Payload, Paragraphs = new List<string> { Payload } },
                new KeyValueSection { Title = Payload, Pairs = new List<KeyValuePairRow> { new KeyValuePairRow { Key = Payload, Value = Payload } } },
                new FindingsSection { Title = Payload, Rows = new List<FindingRow> { new FindingRow { Name = Payload, Verdict = Verdict.Verified, Detail = Payload, Count = 1 } } },
                new DiffSection { Title = Payload, Language = Payload, Before = Payload, After = Payload },
                new ProbeSection { Title = Payload, Probes = new List<ProbeRow> { new ProbeRow { Query = Payload, Expected = Payload, Actual = Payload, Verdict = Verdict.Verified, DurationMs = 1 } } },
                new StepsSection { Title = Payload, Steps = new List<StepRow> { new StepRow { Name = Payload, Verdict = Verdict.Verified, Note = Payload, WhenUtc = Payload } } },
                // Tone is NOT injectable: it is a closed vocabulary enforced by Validate (see the tone test), so
                // the payload cannot even enter the document. Everything free-text carries the payload.
                new NoteSection { Title = Payload, Text = Payload, Tone = "warning" },
            },
        };

        // ---- canonical hash ----

        [Fact]
        public void Canonical_hash_is_stable_regardless_of_in_memory_property_order()
        {
            var doc = FullDoc();
            var expected = EvidenceHash.Compute(doc);
            // Same logical document, but every object's keys emitted in REVERSE order. Canonicalization sorts, so
            // the digest must not move. The ONE key kept in place is the polymorphic "$type" discriminator: .NET 8
            // requires it first to deserialize, and our canonicalizer sorts it first anyway ('$' precedes letters),
            // so preserving it here mirrors what a real reordering tool must also do to stay readable.
            var reordered = ReverseKeyOrderJson(doc);
            Assert.Equal(expected, EvidenceHash.HashOfJsonText(reordered));
            // Format matches the Verified Edits idiom: lower-case hex SHA-256.
            Assert.Equal(64, expected.Length);
            Assert.Equal(expected, expected.ToLowerInvariant());
        }

        // Fix 7's documented strictness branch: a section whose "$type" discriminator is NOT first cannot be read on
        // .NET 8, so canonicalization rejects it fail-loud rather than hashing something it could not fully parse.
        [Fact]
        public void A_section_with_a_misplaced_discriminator_is_rejected()
        {
            var outOfOrder = "{\"id\":\"x\",\"kind\":\"k\",\"title\":\"t\",\"sections\":[{\"title\":\"p\",\"$type\":\"summary\",\"paragraphs\":[]}]}";
            Assert.ThrowsAny<Exception>(() => EvidenceHash.HashOfJsonText(outOfOrder));
        }

        [Fact]
        public void Content_hash_excludes_itself()
        {
            var doc = SimpleDoc();
            var h1 = EvidenceHash.Compute(doc);
            doc.ContentHash = "deadbeef-not-a-real-hash";
            var h2 = EvidenceHash.Compute(doc);
            doc.ContentHash = h1;
            var h3 = EvidenceHash.Compute(doc);
            Assert.Equal(h1, h2);   // whatever the field holds, the hash ignores it
            Assert.Equal(h1, h3);
        }

        // ---- write + verify ----

        [Fact]
        public void Write_produces_both_files_and_verifies()
        {
            var dir = FreshDir();
            try
            {
                var (jsonPath, htmlPath) = EvidenceStore.Write(FullDoc(), dir);
                Assert.True(File.Exists(jsonPath));
                Assert.True(File.Exists(htmlPath));
                Assert.True(EvidenceStore.Verify(jsonPath, out var reason), reason);
                Assert.Contains("matches", reason);
            }
            finally { TryDelete(dir); }
        }

        [Fact]
        public void Verify_catches_a_one_character_tamper_with_a_named_reason()
        {
            var dir = FreshDir();
            try
            {
                var (jsonPath, _) = EvidenceStore.Write(FullDoc(), dir);
                var json = File.ReadAllText(jsonPath);
                // Flip exactly one character in a hashed value ("Zephyr" appears only in the title).
                var tampered = json.Replace("Zephyr", "Zephyx");
                Assert.NotEqual(json, tampered);
                File.WriteAllText(jsonPath, tampered);

                Assert.False(EvidenceStore.Verify(jsonPath, out var reason));
                Assert.Contains("changed after it was produced", reason);
            }
            finally { TryDelete(dir); }
        }

        [Fact]
        public void Verify_reports_a_missing_signature()
        {
            var dir = FreshDir();
            try
            {
                var doc = SimpleDoc();
                doc.ContentHash = EvidenceHash.Compute(doc);
                var node = JsonNode.Parse(EvidenceHash.CanonicalJson(doc)).AsObject();
                node.Remove(EvidenceHash.HashProperty);
                var path = Path.Combine(dir, "no-hash.evidence.json");
                File.WriteAllText(path, node.ToJsonString());
                Assert.False(EvidenceStore.Verify(path, out var reason));
                Assert.Contains("no content signature", reason);
            }
            finally { TryDelete(dir); }
        }

        // ---- structural rule: overridden requires a reason (biconditional) ----

        [Fact]
        public void Overridden_verdict_requires_an_override_reason()
        {
            var doc = SimpleDoc();
            doc.Verdict = Verdict.Overridden;
            doc.OverrideReason = null;
            var ex = Assert.Throws<EvidenceValidationException>(() => doc.Validate());
            Assert.Contains("override reason", ex.Message);

            doc.OverrideReason = "Accepted the slower plan for readability, signed off by the data owner.";
            doc.Validate();   // now valid, no throw
        }

        [Fact]
        public void An_override_reason_without_an_overridden_verdict_is_rejected()
        {
            var doc = SimpleDoc();               // Verdict = Verified
            doc.OverrideReason = "stray reason";
            Assert.Throws<EvidenceValidationException>(() => doc.Validate());
        }

        // ---- structural rule: no coverage, no verdict badge ----

        [Fact]
        public void A_verdict_badge_renders_only_when_the_document_carries_coverage()
        {
            var withCoverage = SimpleDoc();      // has Coverage + Verdict
            // Verdict-less: no coverage AND no verdict, so it validates as an ungraded record (the pairing rule
            // forbids one without the other). Nothing in the body may render a chip.
            var withoutCoverage = SimpleDoc();
            withoutCoverage.Coverage = null;
            withoutCoverage.Verdict = null;

            var a = EvidenceRenderer.Render(withCoverage);
            var b = EvidenceRenderer.Render(withoutCoverage);

            Assert.Contains("class=\"ev-verdict\"", a);   // the badge block is body-only; the CSS uses ".ev-verdict"
            Assert.Contains("Verified 3 of 3", a);
            Assert.DoesNotContain("class=\"ev-verdict\"", b);
            // Not just the headline block: NO verdict chip may appear ANYWHERE in a verdict-less document. The chip
            // class prefix is the single tell (rollups, findings, probes, steps all use it).
            Assert.DoesNotContain("<span class=\"v-chip", b);
        }

        // A verdict-bearing section (findings) forces both a doc-level verdict AND coverage: without them the
        // renderer would draw a rollup/finding chip with nothing measured behind it. Validate is the gate.
        [Fact]
        public void A_verdict_bearing_section_requires_a_verdict_and_coverage()
        {
            var doc = new EvidenceDoc
            {
                Id = "x", Kind = "test-suite", Title = "graded but bare",
                Sections = new List<EvidenceSection>
                {
                    new FindingsSection { Title = "Findings", Rows = new List<FindingRow> { new FindingRow { Name = "n", Verdict = Verdict.Verified } } },
                },
            };
            var ex = Assert.Throws<EvidenceValidationException>(() => doc.Validate());
            Assert.Contains("verdict", ex.Message);

            // Give it a verdict but still no coverage: the coverage half of the pairing is still owed.
            doc.Verdict = Verdict.NeedsReview;
            Assert.Throws<EvidenceValidationException>(() => doc.Validate());

            // Both present: now valid.
            doc.Coverage = new Coverage { Verified = 0, Total = 1, Unknowns = 0 };
            doc.Validate();
        }

        // Coverage present but the doc-level verdict absent is rejected: a coverage block owes the word that grades it.
        [Fact]
        public void Coverage_without_a_verdict_is_rejected()
        {
            var doc = SimpleDoc();
            doc.Verdict = null;            // Coverage still present
            Assert.Throws<EvidenceValidationException>(() => doc.Validate());
        }

        // ---- every section type renders ----

        [Fact]
        public void Every_section_type_renders()
        {
            var html = EvidenceRenderer.Render(FullDoc());
            foreach (var marker in new[] { "ev-summary", "ev-kv", "ev-findings", "ev-diff", "ev-probe", "ev-steps", "ev-note" })
                Assert.Contains("ev-sec " + marker, html);

            // The five words appear as chip labels (Needs review is the human form of NeedsReview).
            Assert.Contains(">Verified<", html);
            Assert.Contains(">Needs review<", html);
            Assert.Contains(">Broken<", html);
            Assert.Contains(">Unknown<", html);

            // The rewrite diff was escaped, not injected.
            Assert.Contains("SUMX ( Sales, Sales[Qty] * Sales[Price] )", html);
            Assert.DoesNotContain("<script", html);
        }

        // ---- golden-file pin ----

        [Fact]
        public void Golden_html_pins_the_output()
        {
            var html = EvidenceRenderer.Render(FullDoc());
            var goldenPath = GoldenPath();
            Assert.True(File.Exists(goldenPath), "golden file missing at " + goldenPath);
            var golden = File.ReadAllText(goldenPath).Replace("\r\n", "\n");
            Assert.Equal(golden, html);
        }

        // ---- determinism ----

        [Fact]
        public void Render_is_byte_for_byte_deterministic()
        {
            var doc = FullDoc();
            Assert.Equal(EvidenceRenderer.Render(doc), EvidenceRenderer.Render(doc));
        }

        // ---- em-dash sweep ----

        [Fact]
        public void No_em_dash_appears_in_any_emitted_html()
        {
            // Exercise every section type AND the override + honesty + tamper-evidence literals.
            var full = EvidenceRenderer.Render(FullDoc());
            Assert.DoesNotContain(EmDash, full);

            var overridden = SimpleDoc();
            overridden.Verdict = Verdict.Overridden;
            overridden.OverrideReason = "Accepted a slower plan for clarity.";
            overridden.Coverage = new Coverage { Verified = 2, Total = 3, Unknowns = 1 };
            overridden.ContentHash = EvidenceHash.Compute(overridden);
            var oh = EvidenceRenderer.Render(overridden);
            Assert.DoesNotContain(EmDash, oh);
            Assert.Contains("Override reason", oh);
            Assert.Contains("Unknown is not counted as verified", oh);
        }

        // ---- both themes ----

        [Fact]
        public void The_stylesheet_defines_both_light_and_dark_themes()
        {
            var html = EvidenceRenderer.Render(SimpleDoc());
            Assert.Contains("prefers-color-scheme:dark", html);   // dark theme block present
            Assert.Contains(":root{--bg:#f8fafc", html);          // light defaults present
            Assert.Contains("--accent:#16a34a", html);            // restrained Signal green
        }

        // ---- honest default: absent verdict is Unknown, never Verified ----

        [Fact]
        public void The_zero_verdict_is_Unknown_not_Verified()
        {
            // A deserialized row with no verdict field must land on Unknown (the enum's zero), never green.
            Assert.Equal(Verdict.Unknown, default(Verdict));
            Assert.Equal(0, (int)Verdict.Unknown);

            var json = "{\"id\":\"x\",\"kind\":\"k\",\"title\":\"t\",\"sections\":[{\"$type\":\"findings\",\"title\":\"F\",\"rows\":[{\"name\":\"n\"}]}]}";
            var doc = EvidenceHash.Deserialize(json);
            var row = ((FindingsSection)doc.Sections[0]).Rows[0];
            Assert.Equal(Verdict.Unknown, row.Verdict);   // absent = we do not know, not verified
        }

        // ---- only the five exact tokens round-trip (no integers, no undefined strings) ----

        [Fact]
        public void The_enum_wire_form_accepts_only_the_five_tokens()
        {
            // A bare integer is rejected (allowIntegerValues:false) so a missing verdict cannot masquerade as 0=green
            // via a numeric literal, and an off-vocabulary word is rejected too.
            Assert.Throws<JsonException>(() => EvidenceHash.Deserialize("{\"id\":\"x\",\"kind\":\"k\",\"title\":\"t\",\"verdict\":1}"));
            Assert.Throws<JsonException>(() => EvidenceHash.Deserialize("{\"id\":\"x\",\"kind\":\"k\",\"title\":\"t\",\"verdict\":\"Excellent\"}"));

            // Each of the five exact tokens round-trips.
            foreach (var word in new[] { "Unknown", "Verified", "NeedsReview", "Broken", "Overridden" })
            {
                var doc = EvidenceHash.Deserialize("{\"id\":\"x\",\"kind\":\"k\",\"title\":\"t\",\"verdict\":\"" + word + "\"}");
                Assert.Equal(word, doc.Verdict.ToString());
            }
        }

        // ---- Render validates (a chip/badge can never escape the gate) ----

        [Fact]
        public void Render_validates_before_drawing_anything()
        {
            // Built by object initializer: an Overridden verdict with coverage but NO reason. Render must refuse it,
            // not emit an Overridden badge with no reason behind it.
            var doc = new EvidenceDoc
            {
                Id = "x", Kind = "deploy", Title = "bad override",
                Verdict = Verdict.Overridden,
                Coverage = new Coverage { Verified = 1, Total = 1, Unknowns = 0 },
                OverrideReason = null,
            };
            Assert.Throws<EvidenceValidationException>(() => EvidenceRenderer.Render(doc));
        }

        // ---- coverage / counts cannot contradict each other (Fix 5) ----

        [Fact]
        public void Coverage_and_counts_must_agree()
        {
            // Unknown count 5 while coverage says 0 unknowns would suppress the honesty line: rejected.
            var d1 = SimpleDoc();
            d1.Coverage = new Coverage { Verified = 3, Total = 8, Unknowns = 0 };
            d1.VerdictCounts = new Dictionary<string, int> { ["Verified"] = 3, ["Unknown"] = 5 };
            Assert.Throws<EvidenceValidationException>(() => d1.Validate());

            // A negative count is rejected.
            var d2 = SimpleDoc();
            d2.VerdictCounts = new Dictionary<string, int> { ["Verified"] = -1 };
            Assert.Throws<EvidenceValidationException>(() => d2.Validate());

            // An off-vocabulary key is rejected.
            var d3 = SimpleDoc();
            d3.VerdictCounts = new Dictionary<string, int> { ["Excellent"] = 1 };
            Assert.Throws<EvidenceValidationException>(() => d3.Validate());

            // Verified greater than Total is rejected (total < verified + unknowns).
            var d4 = SimpleDoc();
            d4.Coverage = new Coverage { Verified = 5, Total = 3, Unknowns = 0 };
            Assert.Throws<EvidenceValidationException>(() => d4.Validate());

            // Negative coverage is rejected.
            var d5 = SimpleDoc();
            d5.Coverage = new Coverage { Verified = -1, Total = 3, Unknowns = 0 };
            Assert.Throws<EvidenceValidationException>(() => d5.Validate());

            // The verified coverage tally must equal the verified count when that key is present.
            var d6 = SimpleDoc();
            d6.Coverage = new Coverage { Verified = 3, Total = 4, Unknowns = 0 };
            d6.VerdictCounts = new Dictionary<string, int> { ["Verified"] = 2 };   // 2 != coverage's 3
            Assert.Throws<EvidenceValidationException>(() => d6.Validate());

            // A consistent set validates.
            var ok = SimpleDoc();
            ok.Coverage = new Coverage { Verified = 3, Total = 5, Unknowns = 2 };
            ok.VerdictCounts = new Dictionary<string, int> { ["Verified"] = 3, ["Unknown"] = 2 };
            ok.Validate();
        }

        // ---- Verify rejects a self-hashed non-document (Fix 4) ----

        [Fact]
        public void Verify_rejects_a_correctly_self_hashed_object_that_is_not_a_valid_document()
        {
            var dir = FreshDir();
            try
            {
                // An almost-empty object, sealed with its OWN correct content hash. The hash gate passes; the
                // structural validation must still reject it (no id/kind/title).
                var empty = new EvidenceDoc();
                empty.ContentHash = EvidenceHash.Compute(empty);
                var path = Path.Combine(dir, "self-hashed.evidence.json");
                File.WriteAllText(path, EvidenceHash.CanonicalJson(empty), new UTF8Encoding(false));

                Assert.False(EvidenceStore.Verify(path, out var reason));
                Assert.Contains("not a valid evidence document", reason);
            }
            finally { TryDelete(dir); }
        }

        [Fact]
        public void Verify_rejects_an_unknown_section_kind()
        {
            var dir = FreshDir();
            try
            {
                // A structurally-plausible record (carrying a signature so we reach the read stage) whose section
                // uses a discriminator we do not know. Strict deserialization must refuse it as unreadable rather
                // than silently skipping the section.
                var json = "{\"id\":\"x\",\"kind\":\"k\",\"title\":\"t\",\"contentHash\":\"deadbeef\",\"sections\":[{\"$type\":\"mystery\",\"title\":\"?\"}]}";
                var path = Path.Combine(dir, "unknown-kind.evidence.json");
                File.WriteAllText(path, json, new UTF8Encoding(false));
                Assert.False(EvidenceStore.Verify(path, out var reason));
                Assert.Contains("not readable as an evidence document", reason);
            }
            finally { TryDelete(dir); }
        }

        // ---- Verify does not throw on a wrongly typed signature (Fix 8) ----

        [Fact]
        public void Verify_handles_a_non_string_content_signature_without_throwing()
        {
            var dir = FreshDir();
            try
            {
                foreach (var badHash in new[] { "{}", "123", "true", "[]" })
                {
                    var json = "{\"id\":\"x\",\"kind\":\"k\",\"title\":\"t\",\"contentHash\":" + badHash + "}";
                    var path = Path.Combine(dir, "bad-hash.evidence.json");
                    File.WriteAllText(path, json, new UTF8Encoding(false));
                    // Must return false with a named reason, NEVER throw.
                    Assert.False(EvidenceStore.Verify(path, out var reason));
                    Assert.Contains("signature is not a string", reason);
                }
            }
            finally { TryDelete(dir); }
        }

        // ---- transactional pair: the report must match the verified record (Fix 6) ----

        [Fact]
        public void Verify_rejects_a_report_that_does_not_match_the_record()
        {
            var dir = FreshDir();
            try
            {
                var (jsonPath, htmlPath) = EvidenceStore.Write(FullDoc(), dir);
                // Change ONLY the human-facing report, leaving the record of truth untouched. The JSON still hashes
                // and validates, but the report no longer matches its deterministic re-render.
                var html = File.ReadAllText(htmlPath);
                File.WriteAllText(htmlPath, html.Replace("Zephyr", "Zephyx"), new UTF8Encoding(false));

                Assert.False(EvidenceStore.Verify(jsonPath, out var reason));
                Assert.Contains("does not match the verified record", reason);
            }
            finally { TryDelete(dir); }
        }

        [Fact]
        public void Verify_accepts_a_record_whose_report_sibling_is_absent()
        {
            var dir = FreshDir();
            try
            {
                var (jsonPath, htmlPath) = EvidenceStore.Write(FullDoc(), dir);
                File.Delete(htmlPath);   // the JSON is the record of truth; an absent report is not a failure
                Assert.True(EvidenceStore.Verify(jsonPath, out var reason), reason);
                Assert.Contains("matches", reason);
            }
            finally { TryDelete(dir); }
        }

        // ---- canonicalization through the schema (Fix 7) ----

        [Fact]
        public void Lexically_different_but_equal_json_hashes_the_same()
        {
            // 1 vs 1.0 vs 1e0 for a numeric leaf (durationMs): the schema projection deserializes each to the same
            // double and re-serializes identically, so the digests match.
            string Probe(string dur) =>
                "{\"id\":\"x\",\"kind\":\"k\",\"title\":\"t\",\"sections\":[{\"$type\":\"probe\",\"title\":\"p\",\"probes\":[{\"verdict\":\"Verified\",\"durationMs\":" + dur + "}]}]}";
            var h1 = EvidenceHash.HashOfJsonText(Probe("1"));
            Assert.Equal(h1, EvidenceHash.HashOfJsonText(Probe("1.0")));
            Assert.Equal(h1, EvidenceHash.HashOfJsonText(Probe("1e0")));

            // Explicit null vs an absent optional field: both project to the same document (nulls are omitted).
            var withNull = "{\"id\":\"x\",\"kind\":\"k\",\"title\":\"t\",\"modelFingerprint\":null,\"sections\":[]}";
            var absent = "{\"id\":\"x\",\"kind\":\"k\",\"title\":\"t\",\"sections\":[]}";
            Assert.Equal(EvidenceHash.HashOfJsonText(absent), EvidenceHash.HashOfJsonText(withNull));

            // Escaped vs literal string content (a unicode escape and an escaped solidus) canonicalize equal.
            var escaped = "{\"id\":\"x\",\"kind\":\"k\",\"title\":\"caf\\u00e9 A\\/B\",\"sections\":[]}";
            var literal = "{\"id\":\"x\",\"kind\":\"k\",\"title\":\"café A/B\",\"sections\":[]}";
            Assert.Equal(EvidenceHash.HashOfJsonText(literal), EvidenceHash.HashOfJsonText(escaped));
        }

        // ---- unknown members are rejected EVERYWHERE, not only at the root ----

        [Fact]
        public void An_unknown_member_inside_any_nested_object_makes_verify_fail()
        {
            // An appended "approved":true inside a nested object must fail as unreadable, never be silently
            // dropped from the hashed projection (Verify would otherwise bless a visibly modified file). One case
            // per nesting site: coverage, each of the seven section kinds, and the row types (the keyValue /
            // findings / probe / steps cases put the junk on the ROW object).
            string Doc(string section) =>
                "{\"id\":\"x\",\"kind\":\"k\",\"title\":\"t\",\"sections\":[" + section + "],\"contentHash\":\"aa\"}";
            var cases = new[]
            {
                "{\"id\":\"x\",\"kind\":\"k\",\"title\":\"t\",\"verdict\":\"Verified\",\"coverage\":{\"verified\":1,\"total\":1,\"unknowns\":0,\"approved\":true},\"contentHash\":\"aa\"}",
                Doc("{\"$type\":\"summary\",\"title\":\"s\",\"paragraphs\":[\"p\"],\"approved\":true}"),
                Doc("{\"$type\":\"keyValue\",\"title\":\"s\",\"pairs\":[{\"key\":\"k\",\"value\":\"v\",\"approved\":true}]}"),
                Doc("{\"$type\":\"findings\",\"title\":\"s\",\"rows\":[{\"name\":\"n\",\"verdict\":\"Verified\",\"approved\":true}]}"),
                Doc("{\"$type\":\"diff\",\"title\":\"s\",\"before\":\"b\",\"after\":\"a\",\"approved\":true}"),
                Doc("{\"$type\":\"probe\",\"title\":\"s\",\"probes\":[{\"query\":\"q\",\"verdict\":\"Verified\",\"approved\":true}]}"),
                Doc("{\"$type\":\"steps\",\"title\":\"s\",\"steps\":[{\"name\":\"n\",\"verdict\":\"Verified\",\"approved\":true}]}"),
                Doc("{\"$type\":\"note\",\"title\":\"s\",\"text\":\"t\",\"tone\":\"info\",\"approved\":true}"),
            };
            var dir = FreshDir();
            try
            {
                var path = Path.Combine(dir, "junk.evidence.json");
                foreach (var json in cases)
                {
                    File.WriteAllText(path, json, new UTF8Encoding(false));
                    Assert.False(EvidenceStore.Verify(path, out var reason), "should have failed: " + json);
                    Assert.Contains("not readable as an evidence document", reason);
                }
            }
            finally { TryDelete(dir); }
        }

        // ---- duplicate JSON properties are rejected before anything is read ----

        [Fact]
        public void Duplicate_json_properties_are_rejected_at_every_depth()
        {
            // Deserialization is last-wins: a shadow "id":"evil" before the real id would keep the original hash
            // and verify healthy while a human (or a first-wins reader) sees the shadow value. The preflight
            // rejects the file outright, before the signature is even extracted.
            var cases = new[]
            {
                // top level
                "{\"id\":\"evil\",\"id\":\"x\",\"kind\":\"k\",\"title\":\"t\",\"contentHash\":\"aa\"}",
                // inside a section
                "{\"id\":\"x\",\"kind\":\"k\",\"title\":\"t\",\"sections\":[{\"$type\":\"summary\",\"title\":\"a\",\"title\":\"b\",\"paragraphs\":[]}],\"contentHash\":\"aa\"}",
                // inside a row
                "{\"id\":\"x\",\"kind\":\"k\",\"title\":\"t\",\"sections\":[{\"$type\":\"findings\",\"title\":\"s\",\"rows\":[{\"name\":\"evil\",\"name\":\"n\",\"verdict\":\"Verified\"}]}],\"contentHash\":\"aa\"}",
                // the signature itself duplicated
                "{\"id\":\"x\",\"kind\":\"k\",\"title\":\"t\",\"contentHash\":\"aa\",\"contentHash\":\"bb\"}",
            };
            var dir = FreshDir();
            try
            {
                var path = Path.Combine(dir, "dup.evidence.json");
                foreach (var json in cases)
                {
                    File.WriteAllText(path, json, new UTF8Encoding(false));
                    Assert.False(EvidenceStore.Verify(path, out var reason), "should have failed: " + json);
                    Assert.Contains("duplicate JSON properties", reason);
                    // The hash path refuses the same text, so no caller can hash what Verify would not read.
                    Assert.Throws<FormatException>(() => EvidenceHash.HashOfJsonText(json));
                }
            }
            finally { TryDelete(dir); }
        }

        // ---- count-bounds arithmetic cannot wrap ----

        [Fact]
        public void Count_bounds_arithmetic_does_not_overflow_int()
        {
            // Verified=int.MaxValue + Unknowns=1 wraps negative under int addition, so Total=0 would "exceed" it
            // and a contradictory coverage line could render. Long arithmetic keeps the bound honest.
            var d1 = SimpleDoc();
            d1.Coverage = new Coverage { Verified = int.MaxValue, Total = 0, Unknowns = 1 };
            Assert.Throws<EvidenceValidationException>(() => d1.Validate());

            // The counts-sum bound wraps the same way: two int.MaxValue counts sum to -2 under int.
            var d2 = SimpleDoc();
            d2.Coverage = new Coverage { Verified = int.MaxValue, Total = int.MaxValue, Unknowns = 0 };
            d2.VerdictCounts = new Dictionary<string, int> { ["Verified"] = int.MaxValue, ["Broken"] = int.MaxValue };
            Assert.Throws<EvidenceValidationException>(() => d2.Validate());
        }

        // ---- the verdict wire form is case- and whitespace-exact ----

        [Fact]
        public void The_verdict_wire_form_is_case_and_whitespace_exact()
        {
            // A case-insensitive reader would let "verified" normalize and hash as if it were canonical; comma
            // syntax would let flags-style combinations in. Only the five exact ordinal tokens may round-trip.
            foreach (var bad in new[] { "\"verified\"", "\"VERIFIED\"", "\" Verified\"", "\"Verified \"", "\"Verified, Broken\"" })
                Assert.Throws<JsonException>(() =>
                    EvidenceHash.Deserialize("{\"id\":\"x\",\"kind\":\"k\",\"title\":\"t\",\"verdict\":" + bad + "}"));
        }

        // ---- note tone is a closed vocabulary, not free text ----

        [Fact]
        public void A_note_tone_outside_the_vocabulary_is_rejected()
        {
            var doc = SimpleDoc();
            doc.Sections = new List<EvidenceSection>
            {
                new NoteSection { Title = "n", Text = "t", Tone = "x\" onmouseover=\"alert(1)" },
            };
            var ex = Assert.Throws<EvidenceValidationException>(() => doc.Validate());
            Assert.Contains("tone", ex.Message);
            // Render validates first, so an off-vocabulary tone can never reach class/attribute context.
            Assert.Throws<EvidenceValidationException>(() => EvidenceRenderer.Render(doc));

            // Case matters: the vocabulary is exactly "info" | "warning" (plus absent, which defaults to info).
            ((NoteSection)doc.Sections[0]).Tone = "Warning";
            Assert.Throws<EvidenceValidationException>(() => doc.Validate());

            foreach (var ok in new[] { "info", "warning", null })
            {
                ((NoteSection)doc.Sections[0]).Tone = ok;
                doc.Validate();
            }
        }

        // ---- XSS regression lock: real payloads in EVERY string-bearing field (Fix 9) ----

        [Fact]
        public void Every_string_field_is_html_escaped_against_injection()
        {
            var html = EvidenceRenderer.Render(XssDoc());

            // No live script element and no live image element survived from any payload.
            Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);

            // No raw payload markup survived verbatim in any field.
            Assert.DoesNotContain("<script>alert(1)</script>", html);
            Assert.DoesNotContain("<img src=x onerror=alert(1)>", html);
            Assert.DoesNotContain("</title><script", html);

            // No on* event-handler attribute exists on any REAL tag. Template tags carry none; escaped payloads are
            // not tags at all. This regex only matches an on-handler inside an opening tag.
            Assert.DoesNotMatch(new Regex("<[a-zA-Z][^>]*\\son[a-z]+\\s*="), html);

            // Positive proof the escaping chokepoint actually ran on the payloads (they were not simply dropped).
            Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
            Assert.Contains("onerror=alert(1)&gt;", html);   // rendered as inert text, its closing bracket encoded
        }

        // ---- helpers ----

        // Serialize the doc, then re-emit every object's keys in reverse order (recursively) so the raw JSON has a
        // different key order than canonical. DeepClone detaches children so they can be re-parented.
        private static string ReverseKeyOrderJson(EvidenceDoc doc)
        {
            var node = JsonSerializer.SerializeToNode(doc, EvidenceHash.SerializerOptions);
            return Reverse(node).ToJsonString();
        }

        private static JsonNode Reverse(JsonNode n)
        {
            switch (n)
            {
                case JsonObject o:
                    var keys = o.Select(kv => kv.Key).ToList();
                    keys.Reverse();
                    // Keep the polymorphic discriminator first - .NET 8 requires it, and the canonicalizer sorts it
                    // first regardless, so this reordering still exercises every OTHER key being out of order.
                    keys.Remove("$type");
                    var no = new JsonObject();
                    if (o.ContainsKey("$type")) no["$type"] = Reverse(o["$type"]?.DeepClone());
                    foreach (var k in keys) no[k] = Reverse(o[k]?.DeepClone());
                    return no;
                case JsonArray a:
                    var na = new JsonArray();
                    foreach (var item in a) na.Add(Reverse(item?.DeepClone()));
                    return na;
                default:
                    return n?.DeepClone();
            }
        }

        private static string GoldenPath()
            => Path.Combine(AppContext.BaseDirectory, "TestData", "golden-evidence.html");

        private static string FreshDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "sem-evidence-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void TryDelete(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
