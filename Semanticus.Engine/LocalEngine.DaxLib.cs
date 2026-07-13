using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    /// <summary>
    /// The DaxLib UDF package-manager ops (Studio v2 Advanced Modelling, 6th area) — dual-drive (RPC + MCP), the same
    /// as every other capability. Browse/versions/info are anonymous READ-ONLY network calls (no session, no model
    /// needed); install/uninstall/list operate on the open model. <b>install is the bulk primitive → Pro-gated</b>
    /// (it authors many UDFs as one atomic, undoable transaction; Free authors them one at a time with create_function).
    /// All network I/O happens BEFORE the model lock — <see cref="ModelSession.MutateAsync"/> only ever runs synchronous
    /// TOM mutation. Once installed, the functions are ordinary TOM UDFs with no runtime DaxLib dependency.
    /// </summary>
    public sealed partial class LocalEngine
    {
        private static readonly CancellationToken DaxLibCt = CancellationToken.None;   // the feed has its own 100s timeout

        public Task<DaxLibPackage[]> DaxLibSearchAsync(string text, int skip, int take)
            => DaxLibRest.SearchAsync(text, skip, take, DaxLibCt);

        public Task<string[]> DaxLibVersionsAsync(string id)
            => DaxLibRest.VersionsAsync(id, DaxLibCt);

        public async Task<DaxLibPackageDetail> DaxLibPackageInfoAsync(string id, string version)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A package id is required — daxlib_search finds packages and returns the id to pass here.");
            var ver = await ResolveVersionAsync(id, version, DaxLibCt).ConfigureAwait(false);
            DaxLibMetadata meta = null;
            try { meta = await DaxLibRest.MetadataAsync(id, ver, DaxLibCt).ConfigureAwait(false); }
            catch { /* metadata.json is best-effort enrichment; the package content below is the source of truth */ }
            var content = await DaxLibRest.GetContentAsync(id, ver, DaxLibCt).ConfigureAwait(false);
            var manifest = DaxLibRest.ParseManifest(content.ManifestJson);
            var functions = DaxLibRest.SplitFunctions(content.FunctionsTmdl);
            return new DaxLibPackageDetail
            {
                Id = id,
                Version = ver,
                Description = meta?.Description ?? manifest.Description,
                Authors = (meta?.Authors?.Length > 0 ? meta.Authors : manifest.Authors) ?? Array.Empty<string>(),
                Tags = meta?.Tags ?? Array.Empty<string>(),
                Downloads = meta?.Downloads ?? 0,
                ReleaseNotes = meta?.ReleaseNotes,
                ProjectUrl = meta?.ProjectUrl,
                RepositoryUrl = meta?.RepositoryUrl,
                Published = meta?.Published,
                Dependencies = manifest.Dependencies ?? Array.Empty<DaxLibDependency>(),
                FunctionNames = functions.Select(f => f.Name).ToArray(),
                FunctionCount = functions.Length,
            };
        }

        public async Task<DaxLibInstallResult> DaxLibInstallAsync(string id, string version, bool replaceExisting, string origin)
        {
            // FREE (Kane, 2026-07-04 — was Pro-gated at launch of the lane): installing community UDFs is
            // top-of-funnel adoption, not the enforcement/bulk-referee value Pro charges for. Still one
            // atomic, undoable batch.
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A package id is required — daxlib_search finds packages and returns the id to pass here.");

            var s = _sessions.Require();   // a model must be open to install into
            var ver = await ResolveVersionAsync(id, version, DaxLibCt).ConfigureAwait(false);

            // Resolve the whole dependency graph (deps-first) + fetch/parse all content OFF the model lock.
            var plan = await BuildInstallPlanAsync(id, ver, DaxLibCt).ConfigureAwait(false);

            var installedRefs = new List<string>();
            var skipped = new List<string>();
            var depsInstalled = new List<string>();
            var warnings = plan.Warnings;

            var rev = await s.MutateAsync(origin, $"Install DaxLib package {id} {ver}", m =>
            {
                if (m.Database == null) throw new InvalidOperationException("Model has no database.");
                if (m.Database.CompatibilityLevel < 1702) m.Database.CompatibilityLevel = 1702;   // UDFs need CL>=1702 (one-way upgrade)

                var prov = DaxLibStore.Load(m);
                foreach (var pkg in plan.Packages)
                {
                    var fnNames = new List<string>();
                    foreach (var fn in pkg.Functions)
                    {
                        var existing = m.Functions.FirstOrDefault(x => string.Equals(x.Name, fn.Name, StringComparison.OrdinalIgnoreCase));
                        if (existing != null)
                        {
                            if (!replaceExisting) { skipped.Add(fn.Name); fnNames.Add(fn.Name); continue; }
                            existing.Delete();
                        }
                        var f = m.AddFunction(fn.Name);
                        f.Expression = fn.Expression ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(fn.Description)) f.Description = fn.Description;
                        // Re-emit the DaxLib provenance on the UDF itself (create_function carries none) so a function
                        // stays attributable even if the model is edited outside Semanticus.
                        f.SetAnnotation("DAXLIB_PackageId", pkg.PackageId);
                        f.SetAnnotation("DAXLIB_PackageVersion", pkg.Version);
                        installedRefs.Add(ObjectRefs.For(f));
                        fnNames.Add(fn.Name);
                    }
                    DaxLibStore.Add(prov, new DaxLibInstalledRecord
                    {
                        PackageId = pkg.PackageId,
                        Version = pkg.Version,
                        Functions = fnNames.ToArray(),
                        Authors = pkg.Authors,
                        By = string.IsNullOrWhiteSpace(origin) ? "human" : origin,
                        When = DateTimeOffset.UtcNow.ToString("o"),
                    });
                    if (!string.Equals(pkg.PackageId, id, StringComparison.OrdinalIgnoreCase))
                        depsInstalled.Add(pkg.PackageId + " " + pkg.Version);
                }
                DaxLibStore.Save(m, prov);
            }).ConfigureAwait(false);

            return new DaxLibInstallResult
            {
                Revision = rev,
                Functions = installedRefs.ToArray(),
                Skipped = skipped.ToArray(),
                DependenciesInstalled = depsInstalled.ToArray(),
                Warning = warnings.Count > 0 ? string.Join("; ", warnings) : null,
            };
        }

        public Task<DaxLibInstalledRecord[]> DaxLibListInstalledAsync()
        {
            var s = _sessions.Require();
            return s.ReadAsync(m => DaxLibStore.Load(m).ToArray());
        }

        // Removing functions is cleanup, not the value primitive — never gated (a user could delete_object each one for
        // free; trapping installed UDFs behind a paywall would be hostile, like un-waiving a finding is never gated).
        public async Task<SetResult> DaxLibUninstallAsync(string id, string origin)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A package id is required — daxlib_list_installed shows the packages recorded in this model and their ids.");
            var s = _sessions.Require();
            var changed = false;
            var rev = await s.MutateAsync(origin, $"Uninstall DaxLib package {id}", m =>
            {
                var prov = DaxLibStore.Load(m);
                var rec = DaxLibStore.Find(prov, id)
                    ?? throw new InvalidOperationException($"DaxLib package '{id}' is not recorded as installed in this model — daxlib_list_installed shows what is installed here (check the id).");
                foreach (var fnName in rec.Functions ?? Array.Empty<string>())
                {
                    var f = m.Functions.FirstOrDefault(x => string.Equals(x.Name, fnName, StringComparison.OrdinalIgnoreCase));
                    if (f != null) { f.Delete(); changed = true; }
                }
                DaxLibStore.Remove(prov, id);
                DaxLibStore.Save(m, prov);
                changed = true;
            }).ConfigureAwait(false);
            return new SetResult { Revision = rev, Changed = changed };
        }

        // ---- helpers ----

        // Newest version if not pinned: prefer the newest STABLE (no '-prerelease' tag); fall back to the newest overall
        // (the feed lists newest-first). A pinned version is taken verbatim.
        private static async Task<string> ResolveVersionAsync(string id, string version, CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(version)) return version.Trim();
            var versions = await DaxLibRest.VersionsAsync(id, ct).ConfigureAwait(false);
            var pick = versions.FirstOrDefault(v => v.IndexOf('-') < 0) ?? versions.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(pick)) throw new InvalidOperationException($"DaxLib package '{id}' has no published versions — confirm the id with daxlib_search, or list the versions with daxlib_versions.");
            return pick;
        }

        private sealed class InstallPkg
        {
            public string PackageId;
            public string Version;
            public string Authors;
            public DaxLibFunction[] Functions;
        }

        // Depth-first, post-order so a dependency is installed BEFORE the package that needs it; dedupes by id, breaks
        // cycles, and surfaces (non-fatal) version conflicts. Bounded so a pathological graph can't run away.
        private async Task<(List<InstallPkg> Packages, List<string> Warnings)> BuildInstallPlanAsync(string rootId, string rootVersion, CancellationToken ct)
        {
            var ordered = new List<InstallPkg>();
            var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // id → chosen version
            var inProgress = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var warnings = new List<string>();

            async Task Visit(string id, string version)
            {
                if (string.IsNullOrWhiteSpace(id)) return;
                if (resolved.TryGetValue(id, out var chosen))
                {
                    if (!string.IsNullOrWhiteSpace(version) && !string.Equals(chosen, version, StringComparison.OrdinalIgnoreCase))
                        warnings.Add($"dependency '{id}' wanted {version} but {chosen} was already selected (kept {chosen}).");
                    return;
                }
                if (!inProgress.Add(id)) { warnings.Add($"dependency cycle broken at '{id}'."); return; }
                if (ordered.Count >= 50) throw new InvalidOperationException("DaxLib dependency graph exceeded 50 packages — refusing to install.");

                var ver = await ResolveVersionAsync(id, version, ct).ConfigureAwait(false);
                var content = await DaxLibRest.GetContentAsync(id, ver, ct).ConfigureAwait(false);
                var manifest = DaxLibRest.ParseManifest(content.ManifestJson);
                foreach (var dep in manifest.Dependencies ?? Array.Empty<DaxLibDependency>())
                    await Visit(dep.Id, dep.Version).ConfigureAwait(false);

                var functions = DaxLibRest.SplitFunctions(content.FunctionsTmdl);
                if (functions.Length == 0) warnings.Add($"'{id}' {ver} defined no functions.");
                ordered.Add(new InstallPkg
                {
                    PackageId = manifest.Id ?? id,
                    Version = ver,
                    Authors = manifest.Authors?.Length > 0 ? string.Join(", ", manifest.Authors) : null,
                    Functions = functions,
                });
                resolved[id] = ver;
                inProgress.Remove(id);
            }

            await Visit(rootId, rootVersion).ConfigureAwait(false);
            return (ordered, warnings);
        }
    }
}
