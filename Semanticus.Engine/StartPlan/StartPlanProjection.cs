// The start plan's projection (design-spec-first-start-v2.md S3 and S7): what a model WOULD save as, in text, so the
// preview scratch and the real build can be compared byte for byte. It is the model's TMDL with TWO written exclusion
// lists: TE2's tool stamps (ToolStamps.Names, ModelSession.cs) and Semanticus's own bookkeeping (Bookkeeping below).
// Nothing else is ignored: a difference in a name, a lineage tag, an annotation or an expression is a real difference
// and must stop the build.
//
// Audit records are appended AFTER the compare (S7), so they are never in a projection. Neither is any marker the
// engine keeps for itself. The consent marker is written into the model whenever the person gives permission, and that
// can happen after the one preview was made and before the build, in any order with the approval and with Jev's rounds.
// A marker in the projection made the preview and the build disagree in exactly that order (join 6, S1), so no marker
// the engine writes for its own use is part of what a person is shown.

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Semanticus.Analysis;
using Semanticus.Engine.Lineage;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Serialization;

namespace Semanticus.Engine.StartPlan
{
    internal static class StartPlanProjection
    {
        /// <summary>The Verified Edits trail the model carries (VerifiedEditsStore). It is appended AFTER a build's compare (S7), so the model a build lands in has none
        /// from this build, but a model whose build was UNDONE still carries the one that build left (an audit record is deliberately not an undo step), while the
        /// preview, cloned before that build, does not. The trail is a record of what was done, not part of the model a person is shown, so it is never part of a
        /// projection: that is what lets an undone build be built again.</summary>
        internal static readonly string[] AuditTrail =
        {
            VerifiedEditsStore.Annotation, VerifiedEditsStore.HeadAnnotation, VerifiedEditsStore.Damaged,
        };

        /// <summary>The model-level annotations the ENGINE keeps for its own use and never because of what a start plan builds: the permission marker, the finding
        /// waivers, the custom readiness rules and the installed-library list. Each is written by a person's or an assistant's own action at any moment while a
        /// plan is open, so a projection that held one would differ between the preview (made before) and the build (made after) for no reason the person was
        /// shown. They are left out of BOTH sides, once, here, so no order of permission, approval and Jev rounds can make the two disagree.
        /// The test Every_engine_annotation_is_classified_for_the_projection keeps this list honest: an annotation the engine starts to write must be put in this list or
        /// in <see cref="BuildWritten"/>, or that test fails.</summary>
        internal static readonly string[] Bookkeeping =
        {
            LocalEngine.ConsentIdAnnotation, WaiverStore.Annotation, CustomReadinessRuleSet.AnnotationName, DaxLibStore.Annotation,
        };

        /// <summary>The engine annotations a build WRITES as part of the model it makes, so they stay in the projection: a difference in one is a real difference.</summary>
        internal static readonly string[] BuildWritten =
        {
            SourceKeys.Annotation, LineageGraph.AddedOnPurposeAnnotation,
        };

        /// <summary>The projection of the model <paramref name="s"/> holds right now. DISPATCHER THREAD ONLY (it runs a
        /// save to a scratch folder, and a save reads the model). It goes through the session's own save, not the TE2
        /// handler's, because that is the path that takes back the stamps a model did not carry before the save; the
        /// handler's save alone would leave one on the model in memory and the next real save would then write it.
        /// The save is not a checkpoint: it marks nothing saved and changes nothing the person can see.</summary>
        internal static string Of(Session s)
        {
            var dir = Path.Combine(Path.GetTempPath(), "sfp" + Guid.NewGuid().ToString("N").Substring(0, 10));
            try
            {
                s.Save(dir, SaveFormat.TMDL, SerializeOptions.Default, resetCheckpoint: false);
                return OfFolder(dir);
            }
            finally
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* a temp folder left behind is noise, never a failed build */ }
            }
        }

        /// <summary>The projection of an already-saved TMDL folder: every file in path order, line ends made alike, the
        /// tool stamps taken out of the model file. Pure over the folder, so a test can feed it two folders.</summary>
        internal static string OfFolder(string folder)
        {
            var sb = new StringBuilder();
            var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Select(f => (Full: f, Rel: Path.GetRelativePath(folder, f).Replace(Path.DirectorySeparatorChar, '/')))
                .OrderBy(f => f.Rel, StringComparer.Ordinal)
                .ToList();
            foreach (var (full, rel) in files)
            {
                var text = Normalize(File.ReadAllBytes(full));
                if (rel.EndsWith(".tmdl", StringComparison.Ordinal))
                    text = WithoutStamps(text);
                sb.Append("=== ").Append(rel).Append('\n').Append(text);
                if (text.Length > 0 && text[text.Length - 1] != '\n') sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>The digest a preview stores and a build compares. Lowercase hex of SHA-256 over the projection text.</summary>
        internal static string Digest(string projection) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(projection ?? ""))).ToLowerInvariant();

        private static string Normalize(byte[] bytes)
        {
            var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            return Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)).Replace("\r\n", "\n");
        }

        /// <summary>Take the tool stamps out of a TMDL file's text, wherever the model file carries them. The strip is the
        /// one the save itself uses (ToolStamps.StripTmdl), run on a copy so the text it is given is never touched.</summary>
        private static string WithoutStamps(string text)
        {
            var tmp = Path.Combine(Path.GetTempPath(), "sfs" + Guid.NewGuid().ToString("N").Substring(0, 10) + ".tmdl");
            try
            {
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                ToolStamps.StripTmdl(tmp, ToolStamps.Names.Concat(AuditTrail).Concat(Bookkeeping).ToArray(), takeMultiLine: true);
                return Normalize(File.ReadAllBytes(tmp));
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        }
    }
}
