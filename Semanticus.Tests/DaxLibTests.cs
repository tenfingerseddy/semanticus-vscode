using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// DaxLib UDF package manager (Studio v2 "Advanced Modelling", 6th area) — the OFFLINE smoke. Two layers:
    /// (1) the pure parsers (functions.tmdl splitter / manifest / search / versions / .daxpkg ZIP) and (2) the full
    /// engine install path driven against a SCRIPTED HttpMessageHandler via the test client-factory seam — NO live
    /// network. Proves: REST-direct works (q=/text= filter, page-loop), the splitter captures the full lambda + doc +
    /// annotations and isn't fooled by indented DAX, install is one atomic+undoable transaction that creates the UDFs,
    /// raises CL to 1702, records provenance, resolves dependencies deps-first, honours skip/replace, is Pro-gated, and
    /// uninstall removes exactly what it owns. All tests live in ONE class so they run serially (the seam is a static).
    /// </summary>
    [Collection("daxlib-serial")]
    public sealed class DaxLibTests : IDisposable
    {
        public void Dispose() => DaxLibRest.ClientFactoryForTests = null;   // always reset the static seam

        // ---- entitlement + handler scaffolding ----
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        private sealed class ScriptedHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, (int status, string ctype, byte[] body)> _responder;
            public readonly List<string> Requests = new();
            public ScriptedHandler(Func<HttpRequestMessage, (int, string, byte[])> responder) => _responder = responder;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            {
                Requests.Add(req.RequestUri!.AbsoluteUri);
                var (status, ctype, body) = _responder(req);
                var resp = new HttpResponseMessage((HttpStatusCode)status)
                {
                    Content = new ByteArrayContent(body ?? Array.Empty<byte>()),
                };
                if (ctype != null) resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(ctype);
                return Task.FromResult(resp);
            }
        }

        private ScriptedHandler Install(Func<HttpRequestMessage, (int, string, byte[])> responder)
        {
            var h = new ScriptedHandler(responder);
            DaxLibRest.ClientFactoryForTests = baseUrl => new HttpClient(h, disposeHandler: false) { BaseAddress = new Uri(baseUrl) };
            return h;
        }

        private static (int, string, byte[]) Json(string s) => (200, "application/json", System.Text.Encoding.UTF8.GetBytes(s));
        private static (int, string, byte[]) Bytes(byte[] b) => (200, "application/octet-stream", b);
        private static (int, string, byte[]) NotFound() => (404, "application/json", System.Text.Encoding.UTF8.GetBytes("{\"title\":\"Not Found\",\"status\":404}"));

        private static byte[] BuildDaxpkg(string manifestJson, string functionsTmdl, string? readme = null)
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                void Add(string name, string content)
                {
                    var e = zip.CreateEntry(name);
                    using var w = new StreamWriter(e.Open());
                    w.Write(content);
                }
                Add("[Content_Types].xml", "<Types/>");
                if (manifestJson != null) Add("manifest.daxlib", manifestJson);
                Add("lib/functions.tmdl", functionsTmdl);
                if (readme != null) Add("README.md", readme);
            }
            return ms.ToArray();
        }

        // A realistic flattened functions.tmdl: bare `function` blocks, /// doc, indented annotations, BOTH a
        // multi-line and an inline lambda, and (in Add) an indented DAX line that LOOKS like a comment — must not split.
        private const string SampleTmdl = @"/// Adds two integers together.
/// @param x the first addend
function 'Sample.Add' =
        (x: INT64, y: INT64) =>
            // a comment that mentions annotation and function inside the body
            x + y

    annotation DAXLIB_PackageId = Sample
    annotation DAXLIB_PackageVersion = 1.0.0

/// Multiplies two integers (inline).
function 'Sample.Mul' = (a: INT64, b: INT64) => a * b

    annotation DAXLIB_PackageId = Sample
    annotation DAXLIB_PackageVersion = 1.0.0
";

        private static async Task<(LocalEngine engine, SessionManager sessions)> NewProModelAsync(int cl = 1604)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(true));   // Pro — install is gated
            await engine.CreateModelAsync("DL", cl);
            return (engine, sessions);
        }

        // ================= pure parsers (no network) =================

        [Fact]
        public void SplitFunctions_captures_name_lambda_doc_and_annotations()
        {
            var fns = DaxLibRest.SplitFunctions(SampleTmdl);
            Assert.Equal(2, fns.Length);

            var add = fns.Single(f => f.Name == "Sample.Add");
            Assert.Contains("(x: INT64, y: INT64) =>", add.Expression);
            Assert.Contains("x + y", add.Expression);
            Assert.Contains("// a comment that mentions annotation and function", add.Expression);   // indented body line kept
            Assert.DoesNotContain("DAXLIB_PackageId", add.Expression);                                 // annotations are NOT part of the expression
            Assert.Contains("Adds two integers", add.Description);
            Assert.Equal("Sample", add.Annotations["DAXLIB_PackageId"]);
            Assert.Equal("1.0.0", add.Annotations["DAXLIB_PackageVersion"]);

            var mul = fns.Single(f => f.Name == "Sample.Mul");
            Assert.Equal("(a: INT64, b: INT64) => a * b", mul.Expression);   // inline lambda, verbatim
        }

        [Fact]
        public void SplitFunctions_does_not_treat_a_dax_body_line_starting_with_annotation_as_a_tmdl_annotation()
        {
            // A multi-line lambda whose body has an indented line beginning with the bare token 'annotation' (no '=')
            // must stay part of the verbatim expression — not be mistaken for a TMDL annotation and truncate the lambda.
            var tmdl = "/// doc\nfunction 'Pkg.Tricky' =\n        (tbl) =>\n            FILTER (\n                annotation IN { 1, 2 },\n                TRUE ()\n            )\n\n    annotation DAXLIB_PackageId = Pkg\n";
            var f = Assert.Single(DaxLibRest.SplitFunctions(tmdl));
            Assert.Contains("annotation IN { 1, 2 }", f.Expression);   // body line kept (not a TMDL annotation)
            Assert.Contains("TRUE ()", f.Expression);                  // lines AFTER it survive (no early truncation)
            Assert.Equal("Pkg", f.Annotations["DAXLIB_PackageId"]);    // the REAL trailing annotation still parses
        }

        [Fact]
        public void ExtractFromDaxpkg_rejects_an_oversized_entry()
        {
            // A decompression-bomb guard: an entry larger than the cap is refused rather than read into memory.
            var huge = new string('x', DaxLibRest.MaxEntryBytes + 1);
            var bytes = BuildDaxpkg("{\"id\":\"Bomb\"}", huge);
            var ex = Assert.Throws<InvalidOperationException>(() => DaxLibRest.ExtractFromDaxpkg(bytes));
            Assert.Contains("refusing to read", ex.Message);
        }

        [Fact]
        public void SplitFunctions_tolerates_empty_and_no_functions()
        {
            Assert.Empty(DaxLibRest.SplitFunctions(null));
            Assert.Empty(DaxLibRest.SplitFunctions(""));
            Assert.Empty(DaxLibRest.SplitFunctions("// just a comment\nsome text"));
        }

        [Fact]
        public void ParseManifest_reads_dependencies_leniently()
        {
            // object form, "id:version" string form, bare "id" string form — all tolerated; extra fields ignored.
            var json = @"{ ""id"":""Child"", ""version"":""2.0.0"", ""authors"":[""A"",""B""],
                ""dependencies"": [ {""id"":""Base"",""version"":""1.0.0""}, ""Other:3.1.0"", ""Loose"" ] }";
            var man = DaxLibRest.ParseManifest(json);
            Assert.Equal("Child", man.Id);
            Assert.Equal(3, man.Dependencies.Length);
            Assert.Equal("Base", man.Dependencies[0].Id);
            Assert.Equal("1.0.0", man.Dependencies[0].Version);
            Assert.Equal("Other", man.Dependencies[1].Id);
            Assert.Equal("3.1.0", man.Dependencies[1].Version);
            Assert.Equal("Loose", man.Dependencies[2].Id);
            Assert.Null(man.Dependencies[2].Version);

            // a manifest with no dependencies field → empty, never null
            Assert.Empty(DaxLibRest.ParseManifest(@"{""id"":""X""}").Dependencies);
            Assert.Empty(DaxLibRest.ParseManifest("not json").Dependencies);
        }

        [Fact]
        public void ParseSearch_and_ParseVersions_map_leniently()
        {
            var search = DaxLibRest.ParseSearch(@"{ ""data"": [
                { ""id"":""Pkg.One"", ""version"":""1.2.3"", ""description"":""d"", ""authors"":[""Me""], ""downloads"":42 },
                { ""id"":""Pkg.Two"", ""latestVersion"":""0.9.0"", ""tags"":[""util""] } ] }");
            Assert.Equal(2, search.Count);
            Assert.Equal("Pkg.One", search[0].Id);
            Assert.Equal("1.2.3", search[0].Version);
            Assert.Equal(42, search[0].Downloads);
            Assert.Equal("0.9.0", search[1].Version);   // latestVersion alias

            var versions = DaxLibRest.ParseVersions(@"{""versions"":[""2.0.0"",""1.0.0-beta"",""1.0.0""]}");
            Assert.Equal(new[] { "2.0.0", "1.0.0-beta", "1.0.0" }, versions);
            Assert.Empty(DaxLibRest.ParseVersions("garbage"));
        }

        [Fact]
        public void ExtractFromDaxpkg_reads_the_zip_parts()
        {
            var bytes = BuildDaxpkg("{\"id\":\"Sample\"}", SampleTmdl, "# readme");
            var content = DaxLibRest.ExtractFromDaxpkg(bytes);
            Assert.Contains("Sample.Add", content.FunctionsTmdl);
            Assert.Contains("\"id\":\"Sample\"", content.ManifestJson);
            Assert.Equal("# readme", content.Readme);

            // a zip with no lib/functions.tmdl is a hard error (nothing to install)
            Assert.Throws<InvalidOperationException>(() => DaxLibRest.ExtractFromDaxpkg(BuildZipWithout()));
        }

        private static byte[] BuildZipWithout()
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var e = zip.CreateEntry("manifest.daxlib");
                using var w = new StreamWriter(e.Open()); w.Write("{}");
            }
            return ms.ToArray();
        }

        // ================= REST layer (scripted handler, no network) =================

        [Fact]
        public async Task SearchAsync_sends_both_filter_params_and_loops_pages()
        {
            var page0 = "{\"data\":[" + string.Join(",", Enumerable.Range(0, 100).Select(i => $"{{\"id\":\"P{i}\",\"version\":\"1.0.0\"}}")) + "]}";
            var page1 = "{\"data\":[{\"id\":\"P100\",\"version\":\"1.0.0\"}]}";
            var h = Install(req =>
            {
                var q = req.RequestUri!.Query;
                if (q.Contains("skip=0")) return Json(page0);
                if (q.Contains("skip=100")) return Json(page1);
                return Json("{\"data\":[]}");
            });
            using var http = new HttpClient(h, false) { BaseAddress = new Uri(DaxLibRest.ApiBase) };
            var all = await DaxLibRest.SearchAsync(http, "calendar", 0, 0, CancellationToken.None);
            Assert.Equal(101, all.Count);
            Assert.Contains(h.Requests, r => r.Contains("q=calendar") && r.Contains("text=calendar"));   // both aliases sent
        }

        [Fact]
        public async Task SearchAsync_stops_when_skip_is_ignored_by_the_feed()
        {
            // a broken feed that returns the same full page regardless of skip would loop forever without de-dupe.
            var page = "{\"data\":[" + string.Join(",", Enumerable.Range(0, 100).Select(i => $"{{\"id\":\"Same{i}\",\"version\":\"1.0.0\"}}")) + "]}";
            var h = Install(_ => Json(page));
            using var http = new HttpClient(h, false) { BaseAddress = new Uri(DaxLibRest.ApiBase) };
            var all = await DaxLibRest.SearchAsync(http, null, 0, 0, CancellationToken.None);
            Assert.Equal(100, all.Count);                 // de-duped to one page's worth
            Assert.True(h.Requests.Count <= 3);           // and it did not spin
        }

        [Fact]
        public async Task GetContentAsync_falls_back_to_github_raw_when_the_api_download_fails()
        {
            var h = Install(req =>
            {
                var u = req.RequestUri!.AbsoluteUri;
                if (u.StartsWith(DaxLibRest.ApiBase) && u.Contains(".daxpkg")) return NotFound();          // API download down
                if (u.StartsWith(DaxLibRest.RawBase) && u.EndsWith("manifest.daxlib")) return Json("{\"id\":\"Sample\"}");
                if (u.StartsWith(DaxLibRest.RawBase) && u.EndsWith("lib/functions.tmdl")) return Json(SampleTmdl);
                return NotFound();
            });
            var content = await DaxLibRest.GetContentAsync("Sample", "1.0.0", CancellationToken.None);
            Assert.Contains("Sample.Add", content.FunctionsTmdl);
            // the raw path layout is packages/{firstletter}/{id-lower}/{version}/...
            Assert.Contains(h.Requests, r => r.Contains("/packages/s/sample/1.0.0/lib/functions.tmdl"));
        }

        // ================= engine install path (scripted handler, real in-memory model) =================

        private void ServeSamplePackage()
        {
            var pkg = BuildDaxpkg("{\"id\":\"Sample\",\"version\":\"1.0.0\",\"authors\":[\"Daxlib\"]}", SampleTmdl);
            Install(req =>
            {
                var u = req.RequestUri!.AbsoluteUri;
                if (u.Contains("/Sample/versions.json")) return Json("{\"versions\":[\"1.0.0\"]}");
                if (u.Contains("/Sample/1.0.0/metadata.json")) return Json("{\"id\":\"Sample\",\"version\":\"1.0.0\",\"description\":\"A sample\",\"downloads\":7}");
                if (u.Contains(".daxpkg")) return Bytes(pkg);
                return NotFound();
            });
        }

        [Fact]
        public async Task Install_creates_the_udfs_raises_cl_records_provenance_and_is_one_undo()
        {
            var (engine, sessions) = await NewProModelAsync(1604);
            using (engine)
            {
                ServeSamplePackage();
                var res = await engine.DaxLibInstallAsync("Sample", null, false, "human");

                Assert.Equal(2, res.Functions.Length);
                Assert.Empty(res.Skipped);

                var snap = await sessions.Require().ReadAsync(m => new
                {
                    HasAdd = m.Functions.Any(f => f.Name == "Sample.Add"),
                    HasMul = m.Functions.Any(f => f.Name == "Sample.Mul"),
                    Cl = m.Database.CompatibilityLevel,
                    AddExpr = m.Functions.First(f => f.Name == "Sample.Add").Expression,
                    AddPkg = m.Functions.First(f => f.Name == "Sample.Add").GetAnnotation("DAXLIB_PackageId"),
                });
                Assert.True(snap.HasAdd && snap.HasMul);
                Assert.True(snap.Cl >= 1702);                        // UDFs need CL>=1702 — raised in the same transaction
                Assert.Contains("x + y", snap.AddExpr);
                Assert.Equal("Sample", snap.AddPkg);                 // provenance re-emitted on the UDF itself

                var installed = await engine.DaxLibListInstalledAsync();
                Assert.Single(installed);
                Assert.Equal("Sample", installed[0].PackageId);
                Assert.Equal(2, installed[0].Functions.Length);

                await engine.UndoAsync("human");                     // the whole install collapses to one undo
                var after = await sessions.Require().ReadAsync(m => m.Functions.Count);
                Assert.Equal(0, after);
                Assert.Empty(await engine.DaxLibListInstalledAsync());
            }
        }

        [Fact]
        public async Task Install_skips_or_replaces_existing_functions()
        {
            var (engine, sessions) = await NewProModelAsync(1702);
            using (engine)
            {
                ServeSamplePackage();
                await engine.DaxLibInstallAsync("Sample", "1.0.0", false, "human");

                // second install, no replace → both skipped, nothing created
                var again = await engine.DaxLibInstallAsync("Sample", "1.0.0", false, "human");
                Assert.Empty(again.Functions);
                Assert.Equal(2, again.Skipped.Length);

                // with replace → overwritten (still 2 functions, not duplicated)
                var replaced = await engine.DaxLibInstallAsync("Sample", "1.0.0", true, "human");
                Assert.Equal(2, replaced.Functions.Length);
                Assert.Empty(replaced.Skipped);
                var count = await sessions.Require().ReadAsync(m => m.Functions.Count);
                Assert.Equal(2, count);
            }
        }

        [Fact]
        public async Task Install_resolves_dependencies_deps_first()
        {
            var (engine, sessions) = await NewProModelAsync(1702);
            using (engine)
            {
                var basePkg = BuildDaxpkg("{\"id\":\"Base\",\"version\":\"1.0.0\"}",
                    "/// base\nfunction 'Base.One' = () => 1\n\n    annotation DAXLIB_PackageId = Base\n");
                var childPkg = BuildDaxpkg("{\"id\":\"Child\",\"version\":\"2.0.0\",\"dependencies\":[{\"id\":\"Base\",\"version\":\"1.0.0\"}]}",
                    "/// child\nfunction 'Child.Two' = () => Base.One() + 1\n\n    annotation DAXLIB_PackageId = Child\n");
                Install(req =>
                {
                    var u = req.RequestUri!.AbsoluteUri;
                    if (u.Contains("/Base/versions.json")) return Json("{\"versions\":[\"1.0.0\"]}");
                    if (u.Contains("/Child/versions.json")) return Json("{\"versions\":[\"2.0.0\"]}");
                    if (u.Contains("/Base/") && u.Contains(".daxpkg")) return Bytes(basePkg);
                    if (u.Contains("/Child/") && u.Contains(".daxpkg")) return Bytes(childPkg);
                    return NotFound();
                });

                var res = await engine.DaxLibInstallAsync("Child", null, false, "human");
                Assert.Contains(res.DependenciesInstalled, d => d.StartsWith("Base"));
                var snap = await sessions.Require().ReadAsync(m => new
                {
                    HasBase = m.Functions.Any(f => f.Name == "Base.One"),
                    HasChild = m.Functions.Any(f => f.Name == "Child.Two"),
                });
                Assert.True(snap.HasBase && snap.HasChild);

                var installed = await engine.DaxLibListInstalledAsync();
                Assert.Equal(2, installed.Length);                   // both packages recorded
            }
        }

        [Fact]
        public async Task Install_works_on_the_free_tier()
        {
            // Kane 2026-07-04: DaxLib install is FREE (adoption funnel, not the bulk-referee value) —
            // pin that a free-tier engine installs a package end to end.
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(false));   // FREE
            using (engine)
            {
                await engine.CreateModelAsync("DL", 1702);
                ServeSamplePackage();
                var res = await engine.DaxLibInstallAsync("Sample", "1.0.0", false, "human");
                Assert.True(res.Functions.Length > 0);
            }
        }

        [Fact]
        public async Task Uninstall_removes_only_the_packages_functions_and_provenance()
        {
            var (engine, sessions) = await NewProModelAsync(1702);
            using (engine)
            {
                ServeSamplePackage();
                await engine.DaxLibInstallAsync("Sample", "1.0.0", false, "human");
                // a hand-authored function NOT owned by the package must survive uninstall
                await engine.CreateFunctionAsync("Mine.Keep", "() => 99", "human");

                var res = await engine.DaxLibUninstallAsync("Sample", "human");
                Assert.True(res.Changed);
                var snap = await sessions.Require().ReadAsync(m => new
                {
                    HasSample = m.Functions.Any(f => f.Name.StartsWith("Sample.")),
                    HasMine = m.Functions.Any(f => f.Name == "Mine.Keep"),
                });
                Assert.False(snap.HasSample);
                Assert.True(snap.HasMine);
                Assert.Empty(await engine.DaxLibListInstalledAsync());

                await Assert.ThrowsAsync<InvalidOperationException>(() => engine.DaxLibUninstallAsync("Nope", "human"));
            }
        }

        // ================= package_info =================

        [Fact]
        public async Task PackageInfo_returns_metadata_dependencies_and_function_preview()
        {
            ServeSamplePackage();
            var info = await new LocalEngine(new SessionManager(), new Fake(true)).DaxLibPackageInfoAsync("Sample", null);
            Assert.Equal("Sample", info.Id);
            Assert.Equal("1.0.0", info.Version);
            Assert.Equal("A sample", info.Description);     // from metadata.json
            Assert.Equal(2, info.FunctionCount);
            Assert.Contains("Sample.Add", info.FunctionNames);
        }
    }
}
