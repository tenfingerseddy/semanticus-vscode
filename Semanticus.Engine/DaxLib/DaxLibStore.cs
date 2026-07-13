using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    /// <summary>
    /// Provenance for installed DaxLib packages, persisted on the model as the <c>Semanticus_DaxLibInstalled</c>
    /// annotation (a JSON array — travels with the model, survives reload/deploy, single source of truth for BOTH doors).
    /// Mirrors <see cref="Semanticus.Analysis.WaiverStore"/>. Used for installed-detection (list), uninstall (which
    /// functions did this package add), and upgrade (replace an existing version). Each function ALSO carries
    /// <c>DAXLIB_PackageId</c>/<c>DAXLIB_PackageVersion</c> annotations re-emitted on the UDF itself (the DaxLib
    /// convention), so a model edited elsewhere is still attributable; this annotation is the convenient index.
    /// </summary>
    internal static class DaxLibStore
    {
        public const string Annotation = "Semanticus_DaxLibInstalled";
        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

        public static List<DaxLibInstalledRecord> Load(Model m)
        {
            try
            {
                var raw = (m as IAnnotationObject)?.GetAnnotation(Annotation);
                if (string.IsNullOrWhiteSpace(raw)) return new List<DaxLibInstalledRecord>();
                return JsonSerializer.Deserialize<List<DaxLibInstalledRecord>>(raw, JsonOpts) ?? new List<DaxLibInstalledRecord>();
            }
            catch { return new List<DaxLibInstalledRecord>(); }   // a corrupt/old annotation degrades to "nothing installed"
        }

        public static void Save(Model m, List<DaxLibInstalledRecord> recs)
        {
            if (!(m is IAnnotationObject ao)) return;
            if (recs == null || recs.Count == 0) { if (ao.HasAnnotation(Annotation)) ao.RemoveAnnotation(Annotation); return; }
            ao.SetAnnotation(Annotation, JsonSerializer.Serialize(recs));
        }

        public static DaxLibInstalledRecord Find(IEnumerable<DaxLibInstalledRecord> recs, string id)
            => recs?.FirstOrDefault(r => string.Equals(r.PackageId, id, StringComparison.OrdinalIgnoreCase));

        /// <summary>Add/replace the record for a package id (idempotent on id — an upgrade overwrites the prior version).</summary>
        public static void Add(List<DaxLibInstalledRecord> recs, DaxLibInstalledRecord rec)
        {
            recs.RemoveAll(r => string.Equals(r.PackageId, rec.PackageId, StringComparison.OrdinalIgnoreCase));
            recs.Add(rec);
        }

        public static bool Remove(List<DaxLibInstalledRecord> recs, string id)
            => recs.RemoveAll(r => string.Equals(r.PackageId, id, StringComparison.OrdinalIgnoreCase)) > 0;
    }
}
