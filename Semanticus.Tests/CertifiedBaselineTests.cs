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
    /// CERTIFIED TOTALS (month-end close) — the labeled, PERSISTED, IMMUTABLE, TAMPER-EVIDENT, model-bound
    /// certified baseline + its drift check + the DECLARATION-BOUND baseline_captured gate. Detect drift
    /// against what was certified; never prevent it. Pinned here (offline — the live re-evaluation runs in the
    /// smokes against a real endpoint), one pin per round-2 finding:
    ///   • P1-A the gate binds ref + CONTEXT + VALUE: a figure at the wrong context, or a wrong value, or an
    ///     unparseable declaration, FAILS/REFUSES the gate; it never silently downgrades to attestation;
    ///   • P1-B identity is by UNIQUE tag whose NAME still matches: a same-tag clone is ambiguous, a rename is
    ///     identity-changed, an untagged legacy figure is not-durable — none read HELD;
    ///   • P1-C a corrupt store is preserved aside and the capture REFUSES — never overwritten;
    ///   • P2-D the hash rejects unknown members + duplicate keys; the tolerance is stored;
    ///   • P2-F a context that references a measure is detected (indirect volatility).
    /// </summary>
    public sealed class CertifiedBaselineTests
    {
        private sealed class Pro : IEntitlement { public bool IsPro => true; public EntitlementInfo Info => new EntitlementInfo { Tier = "pro" }; }

        private static string TempFile() =>
            Path.Combine(Path.GetTempPath(), "smx-cert-" + Guid.NewGuid().ToString("N").Substring(0, 8), "certified-baselines.json");

        private static CertifiedContext Ctx(string server = "powerbi://x/y/Finance", string db = "FinanceModel", string model = "Finance", string fp = "fp-abc", string culture = "en-US")
            => new CertifiedContext { Server = server, Database = db, ModelName = model, Fingerprint = fp, Culture = culture };

        private static CertifiedEntry Entry(string reff, string name, string[] filters, string tag, params (string ctx, double? num, string text)[] cells)
            => new CertifiedEntry
            {
                Ref = reff, Name = name, LineageTag = tag, Filters = filters ?? Array.Empty<string>(),
                Cells = cells.Select(c => new CertifiedCell { Context = c.ctx, Number = c.num, Text = c.text }).ToArray(),
            };

        // ---- the persisted store: round-trip + strict hash + tolerance (P2-D) ----

        [Fact]
        public void A_certified_number_round_trips_hashes_and_still_diffs_correctly()
        {
            var file = TempFile();
            try
            {
                var r = CertifiedStore.Upsert(file, "close FY26 · July", 7, Ctx(),
                    new[] { Entry("measure:Finance/Total Revenue", "Total Revenue", new[] { "'Date'[MonthNo]=7" }, "tag-rev", ("(model context)", 87_400_000.0, null)) });
                Assert.Null(r.Refused);

                var bl = CertifiedStore.Find(CertifiedStore.Load(file, out var corrupt), "close FY26 · July");
                Assert.False(corrupt);
                Assert.NotNull(bl);
                Assert.True(CertifiedStore.HashMatches(bl));
                Assert.Equal(CertifiedStore.Tolerance, bl.Tolerance);   // the record is self-describing (P2-D)

                var es = CertifiedStore.ToEntryState(bl.Entries.Single());
                Assert.Equal("unchanged", Baseline.Diff(es, new Dictionary<string, object> { ["(model context)"] = 87_400_000.0 }, false).Verdict);
                var moved = Baseline.Diff(es, new Dictionary<string, object> { ["(model context)"] = 87_500_000.0 }, false);
                Assert.Equal("moved", moved.Verdict);
                Assert.Equal("87400000", moved.Mismatches.Single().ValueA);
                Assert.Equal("87500000", moved.Mismatches.Single().ValueB);
            }
            finally { TryDeleteDir(file); }
        }

        [Fact]
        public void A_certified_blank_survives_and_a_blank_that_becomes_zero_is_caught()
        {
            var file = TempFile();
            try
            {
                CertifiedStore.Upsert(file, "close", 1, Ctx(), new[] { Entry("measure:F/M", "M", null, null, ("(model context)", null, null)) });
                var es = CertifiedStore.ToEntryState(CertifiedStore.Find(CertifiedStore.Load(file, out _), "close").Entries.Single());
                Assert.Null(es.Rows["(model context)"]);
                Assert.Equal("moved", Baseline.Diff(es, new Dictionary<string, object> { ["(model context)"] = 0.0 }, false).Verdict);
            }
            finally { TryDeleteDir(file); }
        }

        // ---- immutability + model binding (P1-3 / P1-5) ----

        [Fact]
        public void Re_capturing_the_same_figure_is_refused_immutable_and_nothing_is_overwritten()
        {
            var file = TempFile();
            try
            {
                CertifiedStore.Upsert(file, "close", 1, Ctx(), new[] { Entry("measure:F/A", "A", null, null, ("(model context)", 10.0, null)) });
                var again = CertifiedStore.Upsert(file, "close", 2, Ctx(), new[] { Entry("measure:F/A", "A", null, null, ("(model context)", 11.0, null)) });
                Assert.NotNull(again.Refused);
                Assert.Contains("immutable", again.Refused);
                var bl = CertifiedStore.Find(CertifiedStore.Load(file, out _), "close");
                Assert.Single(bl.Entries);
                Assert.Equal(10.0, CertifiedStore.ToEntryState(bl.Entries.Single()).Rows["(model context)"]);
            }
            finally { TryDeleteDir(file); }
        }

        [Fact]
        public void A_new_figure_under_the_same_label_is_added_but_the_same_measure_at_a_new_context_is_allowed()
        {
            var file = TempFile();
            try
            {
                CertifiedStore.Upsert(file, "close", 1, Ctx(), new[] { Entry("measure:F/A", "A", new[] { "'D'[M]=7" }, null, ("(model context)", 10.0, null)) });
                var r = CertifiedStore.Upsert(file, "close", 2, Ctx(), new[]
                {
                    Entry("measure:F/B", "B", null, null, ("(model context)", 5.0, null)),
                    Entry("measure:F/A", "A", new[] { "'D'[M]=8" }, null, ("(model context)", 12.0, null)),
                });
                Assert.Null(r.Refused);
                Assert.Equal(3, CertifiedStore.Find(CertifiedStore.Load(file, out _), "close").Entries.Count);
            }
            finally { TryDeleteDir(file); }
        }

        [Fact]
        public void Extending_a_label_from_a_different_model_is_refused()
        {
            var file = TempFile();
            try
            {
                CertifiedStore.Upsert(file, "close", 1, Ctx(server: "powerbi://x/y/Finance"), new[] { Entry("measure:F/A", "A", null, null, ("(model context)", 10.0, null)) });
                var other = CertifiedStore.Upsert(file, "close", 2, Ctx(server: "powerbi://x/y/OTHER"), new[] { Entry("measure:F/B", "B", null, null, ("(model context)", 5.0, null)) });
                Assert.NotNull(other.Refused);
                Assert.Contains("different model", other.Refused);
            }
            finally { TryDeleteDir(file); }
        }

        // ---- tamper evidence + strict decode (P1-3 / P2-D) ----

        [Fact]
        public void A_tampered_file_fails_the_hash_check_loudly()
        {
            var file = TempFile();
            try
            {
                CertifiedStore.Upsert(file, "close", 1, Ctx(), new[] { Entry("measure:F/A", "A", null, null, ("(model context)", 87_400_000.0, null)) });
                File.WriteAllText(file, File.ReadAllText(file).Replace("87400000", "99999999"));
                var bl = CertifiedStore.Find(CertifiedStore.Load(file, out var corrupt), "close");
                Assert.False(corrupt);
                Assert.False(CertifiedStore.HashMatches(bl));
            }
            finally { TryDeleteDir(file); }
        }

        [Fact]
        public void An_unknown_member_reads_as_corrupt()
        {
            var file = TempFile();
            try
            {
                CertifiedStore.Upsert(file, "close", 1, Ctx(), new[] { Entry("measure:F/A", "A", null, null, ("(model context)", 1.0, null)) });
                // Smuggle an extra property — strict UnmappedMemberHandling.Disallow rejects it (P2-D).
                File.WriteAllText(file, File.ReadAllText(file).Replace("\"label\":", "\"smuggled\":true,\"label\":"));
                CertifiedStore.Load(file, out var corrupt);
                Assert.True(corrupt);
            }
            finally { TryDeleteDir(file); }
        }

        [Fact]
        public void A_duplicate_property_reads_as_corrupt()
        {
            var file = TempFile();
            try
            {
                CertifiedStore.Upsert(file, "close", 1, Ctx(), new[] { Entry("measure:F/A", "A", null, null, ("(model context)", 1.0, null)) });
                File.WriteAllText(file, File.ReadAllText(file).Replace("\"label\":", "\"label\":\"shadow\",\"label\":"));
                CertifiedStore.Load(file, out var corrupt);
                Assert.True(corrupt);   // last-wins duplicate would shadow the hash basis
            }
            finally { TryDeleteDir(file); }
        }

        [Fact]
        public void A_corrupt_store_is_preserved_aside_and_the_capture_refuses_without_overwriting()
        {
            var file = TempFile();
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, "{ this is not json");
            try
            {
                var r = CertifiedStore.Upsert(file, "close", 1, Ctx(), new[] { Entry("measure:F/A", "A", null, null, ("(model context)", 1.0, null)) });
                Assert.NotNull(r.Refused);                                 // capture is as loud as compare (P1-C)
                Assert.Contains("preserved aside", r.Refused);
                var dir = Path.GetDirectoryName(file);
                Assert.NotEmpty(Directory.GetFiles(dir, "*.corrupt-*"));    // the corrupt bytes are recoverable
                Assert.False(File.Exists(file));                           // NOT overwritten with a fresh store
            }
            finally { TryDeleteDir(file); }
        }

        // ---- volatility detection (P2-F) ----

        [Theory]
        [InlineData("'Date'[Date] <= TODAY()", "TODAY")]
        [InlineData("FILTER(ALL('Date'), 'Date'[Date] < NOW())", "NOW")]
        [InlineData("'Date'[Y]=2026", null)]
        public void A_literal_time_volatile_context_is_detected(string filter, string expected)
            => Assert.Equal(expected, CertifiedStore.VolatileContext(new[] { filter }));

        [Fact]
        public void A_bare_measure_reference_in_a_context_is_detected_for_indirect_volatility()
        {
            // A table-qualified column is NOT a measure ref; a bare [name] is a candidate the engine confirms.
            var refs = CertifiedStore.BareBracketRefs(new[] { "'Date'[AsOfFlag] = [AsOfToday]" });
            Assert.Contains("AsOfToday", refs);
            Assert.DoesNotContain("AsOfFlag", refs);   // 'Date'[AsOfFlag] is table-qualified → a column, not flagged
        }

        [Theory]
        [InlineData(new[] { "'Date'[MonthNo] = 7" }, "'Date'[MonthNo]=7")]   // incidental spacing collapses
        [InlineData(new string[0], "")]
        public void NormalizeContext_strips_incidental_whitespace(string[] filters, string expected)
            => Assert.Equal(expected, CertifiedStore.NormalizeContext(filters));

        [Fact]
        public void NormalizeContext_preserves_significant_whitespace_inside_identifiers_and_strings()
        {
            // P2-2: whitespace inside [..] and ".." is SIGNIFICANT — different columns / different slices must not collide.
            Assert.NotEqual(CertifiedStore.NormalizeContext(new[] { "'Date'[Month No]=7" }),
                            CertifiedStore.NormalizeContext(new[] { "'Date'[MonthNo]=7" }));
            Assert.NotEqual(CertifiedStore.NormalizeContext(new[] { "Region=\"North East\"" }),
                            CertifiedStore.NormalizeContext(new[] { "Region=\"NorthEast\"" }));
        }

        // ---- context stamp (P2-1): the shape fingerprint must NOT block the check ----

        [Fact]
        public void DiffFrom_ignores_the_shape_fingerprint_but_still_blocks_a_genuinely_different_model()
        {
            Assert.Null(Ctx(fp: "shape-A").DiffFrom(Ctx(fp: "shape-B")));        // an unrelated edit changes the fingerprint — must NOT block
            Assert.NotNull(Ctx(server: "powerbi://a").DiffFrom(Ctx(server: "powerbi://b")));   // a different server IS a different model
            Assert.NotNull(Ctx(db: "A").DiffFrom(Ctx(db: "B")));
            Assert.NotNull(Ctx(model: "A").DiffFrom(Ctx(model: "B")));
            Assert.NotNull(Ctx(culture: "en-US").DiffFrom(Ctx(culture: "fr-FR")));
        }

        [Fact]
        public void A_shape_change_is_reported_as_an_informational_note()
        {
            Assert.Null(Ctx(fp: "same").ShapeChangeNote(Ctx(fp: "same")));
            var note = Ctx(fp: "shape-A").ShapeChangeNote(Ctx(fp: "shape-B"));
            Assert.NotNull(note);
            Assert.Contains("does not block", note);
        }

        // ---- compare_baseline(label:) offline: refusals + loud tamper ----

        [Fact]
        public async Task Compare_by_label_with_no_certified_baseline_is_not_found()
        {
            var ws = NewWorkspace(); var sessions = new SessionManager();
            try
            {
                var e = new LocalEngine(sessions, new Pro(), ws);
                var r = await e.CompareBaselineAsync(null, "close FY26 · July", "human");
                Assert.Equal("not-found", r.Status);
                Assert.Contains("capture_baseline", r.Message);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Compare_by_label_when_certified_but_offline_asks_for_a_live_model()
        {
            var ws = NewWorkspace(); var sessions = new SessionManager();
            try
            {
                var e = new LocalEngine(sessions, new Pro(), ws);
                Seed(ws, "close", Entry("measure:F/Total", "Total", null, null, ("(model context)", 42.0, null)));
                var r = await e.CompareBaselineAsync(null, "close", "human");
                Assert.Equal("no-connection", r.Status);
                Assert.Contains("open_live", r.Message);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Compare_by_label_on_a_tampered_file_refuses_loudly_before_reading_the_model()
        {
            var ws = NewWorkspace(); var sessions = new SessionManager();
            try
            {
                var e = new LocalEngine(sessions, new Pro(), ws);
                Seed(ws, "close", Entry("measure:F/A", "A", null, null, ("(model context)", 87_400_000.0, null)));
                var certFile = Path.Combine(ws, ".semanticus", "certified-baselines.json");
                File.WriteAllText(certFile, File.ReadAllText(certFile).Replace("87400000", "1"));
                var r = await e.CompareBaselineAsync(null, "close", "human");
                Assert.Equal("error", r.Status);
                Assert.Contains("MODIFIED", r.Message);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        // ---- identity resolution (P1-B), on a real CL-1604 model ----

        [Fact]
        public async Task Identity_an_untagged_legacy_figure_is_not_durable_and_an_impostor_is_identity_changed()
        {
            var sm = new SessionManager();
            using var e = new LocalEngine(sm, new Pro());
            await e.CreateModelAsync("m", 1604);
            var t = await e.CreateTableAsync("Finance", "human");
            await e.CreateMeasureAsync(t, "Total Revenue", "1", "human");

            // Untagged (no LineageTag captured) but the name resolves → not-durable (never HELD).
            var legacy = await sm.Current.ReadAsync(m => LocalEngine.ResolveCertifiedIdentity(m,
                new CertifiedEntry { Ref = "measure:Finance/Total Revenue", Name = "Total Revenue", LineageTag = null }));
            Assert.Equal("not-durable", legacy.status);

            // A tag that resolves to nothing, but a measure at the old name exists → impostor → identity-changed.
            var impostor = await sm.Current.ReadAsync(m => LocalEngine.ResolveCertifiedIdentity(m,
                new CertifiedEntry { Ref = "measure:Finance/Total Revenue", Name = "Total Revenue", LineageTag = "no-such-tag" }));
            Assert.Equal("identity-changed", impostor.status);
        }

        [Fact]
        public async Task Identity_a_renamed_measure_keeps_its_tag_but_the_name_no_longer_matches_so_it_is_identity_changed()
        {
            var sm = new SessionManager();
            using var e = new LocalEngine(sm, new Pro());
            await e.CreateModelAsync("m", 1604);
            var t = await e.CreateTableAsync("Finance", "human");
            await e.CreateMeasureAsync(t, "Total Revenue", "1", "human");
            var tag = await sm.Current.ReadAsync(m => (ObjectRefs.Resolve(m, "measure:Finance/Total Revenue") as TabularEditor.TOMWrapper.ILineageTagObject)?.LineageTag);
            Assert.False(string.IsNullOrEmpty(tag));   // CL 1604 → tagged

            await e.RenameObjectAsync("measure:Finance/Total Revenue", "Revenue (net)", "human");   // rename PRESERVES the tag
            // We certified "Total Revenue" (old ref + name + tag); its tag now belongs to a measure with a DIFFERENT name.
            var res = await sm.Current.ReadAsync(m => LocalEngine.ResolveCertifiedIdentity(m,
                new CertifiedEntry { Ref = "measure:Finance/Total Revenue", Name = "Total Revenue", LineageTag = tag }));
            Assert.Equal("identity-changed", res.status);   // tag matches one measure, but its NAME is no longer the certified one
        }

        [Fact]
        public async Task Identity_a_same_tag_clone_is_ambiguous_never_HELD()
        {
            var sm = new SessionManager();
            using var e = new LocalEngine(sm, new Pro());
            await e.CreateModelAsync("m", 1604);
            var t1 = await e.CreateTableAsync("Finance", "human");
            var t2 = await e.CreateTableAsync("Copy", "human");
            await e.CreateMeasureAsync(t1, "Total Revenue", "1", "human");
            var tag = await sm.Current.ReadAsync(m => (ObjectRefs.Resolve(m, "measure:Finance/Total Revenue") as TabularEditor.TOMWrapper.ILineageTagObject)?.LineageTag);

            // Duplicate into ANOTHER table (a different collection → the tag is copied without a per-collection clash).
            await e.DuplicateObjectAsync("measure:Finance/Total Revenue", "Total Revenue", "table:Copy", "human");
            var shared = await sm.Current.ReadAsync(m => m.AllMeasures.Count(mm => string.Equals((mm as TabularEditor.TOMWrapper.ILineageTagObject)?.LineageTag, tag, StringComparison.Ordinal)));
            if (shared < 2) return;   // fixture guard: if this build's duplicate regenerates the tag, the clone vuln does not arise this way

            var res = await sm.Current.ReadAsync(m => LocalEngine.ResolveCertifiedIdentity(m,
                new CertifiedEntry { Ref = "measure:Finance/Total Revenue", Name = "Total Revenue", LineageTag = tag }));
            Assert.Equal("ambiguous", res.status);   // a clone copied the tag → identity cannot be proven → never HELD
        }

        // ---- the baseline_captured gate: bound to ref + CONTEXT + VALUE (P1-A) ----

        private static string GateWf(string controlTotals)
        {
            var sv = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["control_totals"] = controlTotals });
            return "---\nname: certify-inst\ntitle: vehicle\nstrictness: hard\nslot_values: " + sv + "\n---\n" +
                   "## Step 1: Sign off\n```yaml gate\nops: [capture_baseline]\ninputs:\n  - name: certifiedLabel\n    question: \"The label.\"\n    type: text\n    required: required\nverify:\n  - kind: baseline_captured\n    probe: certifiedLabel\n```\n";
        }

        private const string GoodLine = "measure:Finance/Total Revenue ~ 'Date'[MonthNo]=7 ~ 87437221";

        private static async Task<(LocalEngine e, SessionManager sm, string ws)> GateEngine(string controlTotals)
        {
            var ws = NewWorkspace();
            WriteUserWorkflow(ws, "certify-inst.md", GateWf(controlTotals));
            var sm = new SessionManager();
            return (new LocalEngine(sm, new Pro(), ws), sm, ws);
        }

        [Fact]
        public async Task Gate_PASSES_only_when_the_declared_figure_matches_ref_context_and_value()
        {
            var (e, sm, ws) = await GateEngine(GoodLine);
            try
            {
                Seed(ws, "close", Entry("measure:Finance/Total Revenue", "Total Revenue", new[] { "'Date'[MonthNo]=7" }, "tag", ("(model context)", 87437221.0, null)));
                var run = await e.StartWorkflowAsync("certify-inst", "human");
                var done = await e.SubmitWorkflowStepAsync(run.RunId, "step-1", "{\"certifiedLabel\": \"close\"}", "human");
                Assert.Equal("completed", done.Status);
                var v = done.Steps[0].VerifyResults.Single(x => x.Kind == "baseline_captured");
                Assert.Equal("passed", v.Status);
                Assert.Contains("declared contexts", v.Detail);
            }
            finally { sm.Dispose(); Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Gate_FAILS_when_the_declared_figure_was_certified_at_the_WRONG_context()
        {
            var (e, sm, ws) = await GateEngine(GoodLine);   // declares the July slice
            try
            {
                // Certified at the GRAND TOTAL (no filters), not the declared July context → the gate must FAIL.
                Seed(ws, "close", Entry("measure:Finance/Total Revenue", "Total Revenue", null, "tag", ("(model context)", 87437221.0, null)));
                var run = await e.StartWorkflowAsync("certify-inst", "human");
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => e.SubmitWorkflowStepAsync(run.RunId, "step-1", "{\"certifiedLabel\": \"close\"}", "human"));
                Assert.Contains("NOT certified at that context", ex.Message);
            }
            finally { sm.Dispose(); Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Gate_FAILS_when_the_certified_value_does_not_match_the_declared_value()
        {
            var (e, sm, ws) = await GateEngine(GoodLine);
            try
            {
                Seed(ws, "close", Entry("measure:Finance/Total Revenue", "Total Revenue", new[] { "'Date'[MonthNo]=7" }, "tag", ("(model context)", 99999999.0, null)));
                var run = await e.StartWorkflowAsync("certify-inst", "human");
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => e.SubmitWorkflowStepAsync(run.RunId, "step-1", "{\"certifiedLabel\": \"close\"}", "human"));
                Assert.Contains("but the close declared", ex.Message);
            }
            finally { sm.Dispose(); Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Gate_REFUSES_an_unparseable_control_totals_line_never_silently_attesting_it()
        {
            var (e, sm, ws) = await GateEngine("measure:Finance/Total Revenue, 87.4M, from the pack");   // old free-text form
            try
            {
                Seed(ws, "close", Entry("measure:Finance/Total Revenue", "Total Revenue", new[] { "'Date'[MonthNo]=7" }, "tag", ("(model context)", 87437221.0, null)));
                var run = await e.StartWorkflowAsync("certify-inst", "human");
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => e.SubmitWorkflowStepAsync(run.RunId, "step-1", "{\"certifiedLabel\": \"close\"}", "human"));
                Assert.Contains("cannot parse", ex.Message);
            }
            finally { sm.Dispose(); Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Gate_FAILS_on_an_empty_declaration()
        {
            var (e, sm, ws) = await GateEngine("");
            try
            {
                Seed(ws, "close", Entry("measure:Finance/Total Revenue", "Total Revenue", null, "tag", ("(model context)", 1.0, null)));
                var run = await e.StartWorkflowAsync("certify-inst", "human");
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => e.SubmitWorkflowStepAsync(run.RunId, "step-1", "{\"certifiedLabel\": \"close\"}", "human"));
                Assert.Contains("declared no control totals", ex.Message);
            }
            finally { sm.Dispose(); Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Gate_on_a_tampered_baseline_fails_loudly()
        {
            var (e, sm, ws) = await GateEngine(GoodLine);
            try
            {
                Seed(ws, "close", Entry("measure:Finance/Total Revenue", "Total Revenue", new[] { "'Date'[MonthNo]=7" }, "tag", ("(model context)", 87437221.0, null)));
                var certFile = Path.Combine(ws, ".semanticus", "certified-baselines.json");
                File.WriteAllText(certFile, File.ReadAllText(certFile).Replace("87437221", "1"));
                var run = await e.StartWorkflowAsync("certify-inst", "human");
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => e.SubmitWorkflowStepAsync(run.RunId, "step-1", "{\"certifiedLabel\": \"close\"}", "human"));
                Assert.Contains("modified since capture", ex.Message);
            }
            finally { sm.Dispose(); Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Gate_FAILS_a_declared_figure_whose_context_references_a_measure()
        {
            var (e, sm, ws) = await GateEngine(GoodLine);
            try
            {
                // The certified figure matches ref + context + value, but was flagged StabilityProvable=false (its
                // context references a measure) — a hard close must not sign off on a figure nobody can re-check (P2-3).
                var en = Entry("measure:Finance/Total Revenue", "Total Revenue", new[] { "'Date'[MonthNo]=7" }, "tag", ("(model context)", 87437221.0, null));
                en.StabilityProvable = false;
                Seed(ws, "close", en);
                var run = await e.StartWorkflowAsync("certify-inst", "human");
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => e.SubmitWorkflowStepAsync(run.RunId, "step-1", "{\"certifiedLabel\": \"close\"}", "human"));
                Assert.Contains("references a measure", ex.Message);
            }
            finally { sm.Dispose(); Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Gate_REFUSES_a_blank_context_and_a_non_exact_value()
        {
            // P3-1: a blank context must not silently bind to grand total; P3-2: a thousands-separated / currency value is not exact.
            foreach (var line in new[]
            {
                "measure:Finance/Total Revenue ~  ~ 87437221",             // blank context
                "measure:Finance/Total Revenue ~ 'Date'[MonthNo]=7 ~ $1,234.50",   // currency + thousands
                "measure:Finance/Total Revenue ~ 'Date'[MonthNo]=7 ~ (100)",       // parenthesized negative
            })
            {
                var (e, sm, ws) = await GateEngine(line);
                try
                {
                    Seed(ws, "close", Entry("measure:Finance/Total Revenue", "Total Revenue", new[] { "'Date'[MonthNo]=7" }, "tag", ("(model context)", 87437221.0, null)));
                    var run = await e.StartWorkflowAsync("certify-inst", "human");
                    var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                        () => e.SubmitWorkflowStepAsync(run.RunId, "step-1", "{\"certifiedLabel\": \"close\"}", "human"));
                    Assert.Contains("cannot parse", ex.Message);
                }
                finally { sm.Dispose(); Directory.Delete(ws, true); }
            }
        }

        // ---- compare-level pins (P3-4): these branches never touch the live model, so they run offline ----

        [Fact]
        public async Task CertifiedDiffOne_a_stability_not_provable_entry_is_not_comparable_never_HELD()
        {
            using var e = new LocalEngine(new SessionManager(), new Pro());
            var en = Entry("measure:F/M", "M", new[] { "[SomeMeasure]" }, "tag", ("(model context)", 10.0, null));
            en.StabilityProvable = false;
            var d = await e.CertifiedDiffOneAsync(null, null, en);   // returns BEFORE touching the live connection / session
            Assert.Equal("not-comparable", d.Verdict);
            Assert.Contains("stability not provable", d.Note);
        }

        [Fact]
        public async Task CertifiedDiffOne_an_impostor_and_an_untagged_entry_are_not_comparable_never_HELD()
        {
            var sm = new SessionManager();
            using var e = new LocalEngine(sm, new Pro());
            await e.CreateModelAsync("m", 1604);
            var t = await e.CreateTableAsync("Finance", "human");
            await e.CreateMeasureAsync(t, "Total Revenue", "1", "human");

            // Impostor: a bogus tag but a measure at the old name → identity-changed → not-comparable (never HELD).
            var impostor = await e.CertifiedDiffOneAsync(null, sm.Current,
                Entry("measure:Finance/Total Revenue", "Total Revenue", null, "no-such-tag", ("(model context)", 1.0, null)));
            Assert.Equal("not-comparable", impostor.Verdict);

            // Untagged legacy → not-durable → not-comparable (never HELD).
            var legacy = await e.CertifiedDiffOneAsync(null, sm.Current,
                Entry("measure:Finance/Total Revenue", "Total Revenue", null, null, ("(model context)", 1.0, null)));
            Assert.Equal("not-comparable", legacy.Verdict);

            sm.Dispose();
        }

        [Fact]
        public async Task Capture_with_a_label_but_no_live_connection_certifies_nothing_fail_closed()
        {
            var bim = TempCopyOfBim();
            var sessions = new SessionManager();
            try
            {
                var e = new LocalEngine(sessions, new Pro());
                await e.OpenAsync(bim);
                var r = await e.CaptureBaselineAsync("measure:Date/Days In Current Quarter", null, null, false, 25, 2000, "close FY26 · July", "human");
                Assert.Equal("no-connection", r.Status);
                Assert.False(File.Exists(Path.Combine(LayoutStore.DirFor(bim), "certified-baselines.json")));
            }
            finally { sessions.Dispose(); Directory.Delete(Path.GetDirectoryName(bim), true); }
        }

        // ---- helpers ----

        private static void Seed(string ws, string label, params CertifiedEntry[] entries)
            => CertifiedStore.Upsert(Path.Combine(ws, ".semanticus", "certified-baselines.json"), label, 1, Ctx(), entries);

        private static string NewWorkspace()
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-cert-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(ws, ".semanticus", "workflows"));
            return ws;
        }

        private static void WriteUserWorkflow(string ws, string file, string md) =>
            File.WriteAllText(Path.Combine(ws, ".semanticus", "workflows", file), md);

        private static string TempCopyOfBim()
        {
            var dir = Path.Combine(Path.GetTempPath(), "smx-cert-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var bim = Path.Combine(dir, "AdventureWorks.bim");
            File.Copy(TestModels.FindBim(), bim);
            return bim;
        }

        private static void TryDeleteDir(string file)
        {
            try { Directory.Delete(Path.GetDirectoryName(file), true); } catch { }
        }
    }
}
