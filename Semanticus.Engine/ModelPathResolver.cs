using System;
using System.IO;
using System.Linq;

namespace Semanticus.Engine
{
    /// <summary>
    /// Normalises a user-supplied "open my model" path to the path the TOM loader can actually open.
    ///
    /// A modern Power BI **TMDL PBIP** nests the model under <c>&lt;name&gt;.SemanticModel/definition/</c> (the TMDL
    /// root: database.tmdl + model.tmdl + tables/), with <c>definition.pbism</c> / VerifiedAnswers / diagramLayout.json
    /// beside it in the <c>.SemanticModel</c> folder and a <c>&lt;name&gt;.pbip</c> at the project root. The vendored
    /// TE2 handler can't reach that TMDL root from any of those natural entry points: pointed at the project folder or
    /// the <c>.SemanticModel</c> folder it walks PARENTS (never the <c>definition</c> child) and falls back to the
    /// legacy split-model loader ("no database.json"); pointed at the <c>.pbip</c> it assumes a legacy <c>model.bim</c>
    /// ("model.bim not found"). Both doors advertise "open a PBIP folder", so this closes the contract-vs-behaviour gap.
    ///
    /// Strategy: redirect ONLY when we can positively identify a modern-PBIP TMDL root; otherwise return the path
    /// unchanged so every existing format (a flat TMDL folder the engine itself saved, a <c>.bim</c> file, a legacy
    /// split-model folder, an <c>.tmdl</c> file) reaches TE2 exactly as before. The downstream file-based readers
    /// (<c>PrepForAiReader.ResolveModelFolder</c>, the layout sidecar, VerifiedAnswers) already walk UP from the opened
    /// path to find the <c>.SemanticModel</c> folder, so opening the inner <c>definition</c> folder keeps them correct.
    /// </summary>
    public static class ModelPathResolver
    {
        /// <summary>Resolve <paramref name="path"/> to a TOM-openable path, redirecting a modern TMDL PBIP to its
        /// inner <c>definition</c> folder. Pure + side-effect-free apart from read-only filesystem probing; returns
        /// the input unchanged when it isn't a recognisable nested PBIP (so existing behaviour is preserved).</summary>
        public static string Resolve(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;

            // A .pbip file → its sibling <name>.SemanticModel/definition (the project root holds exactly one .pbip).
            if (File.Exists(path))
            {
                if (path.EndsWith(".pbip", System.StringComparison.OrdinalIgnoreCase))
                {
                    var fromProject = DefinitionUnderProjectFolder(Path.GetDirectoryName(path));
                    if (fromProject != null) return fromProject;
                }
                return path; // .bim / .tmdl / database.json — TE2 handles directly
            }

            if (!Directory.Exists(path)) return path; // let TE2 produce its normal not-found error

            // The folder IS already a TMDL root (e.g. the engine's own flat TMDL save) → open as-is.
            if (IsTmdlRoot(path)) return path;

            // A .SemanticModel folder whose definition/ is the TMDL root.
            var def = Path.Combine(path, "definition");
            if (IsTmdlRoot(def)) return def;

            // A project root: exactly one <name>.SemanticModel/definition, or a single .pbip beside it.
            var fromFolder = DefinitionUnderProjectFolder(path);
            if (fromFolder != null) return fromFolder;

            return path; // legacy split-model folder / anything else → unchanged
        }

        /// <summary>Find a single <c>*.SemanticModel/definition</c> TMDL root directly under a project folder. Returns
        /// null if the folder isn't a recognisable PBIP project root (so the caller falls back to the path as given).</summary>
        private static string DefinitionUnderProjectFolder(string projectFolder)
        {
            if (string.IsNullOrEmpty(projectFolder) || !Directory.Exists(projectFolder)) return null;
            var semModels = Directory.EnumerateDirectories(projectFolder, "*.SemanticModel")
                .Select(d => Path.Combine(d, "definition"))
                .Where(IsTmdlRoot)
                .ToList();
            return semModels.Count == 1 ? semModels[0] : null;
        }

        /// <summary>True when <paramref name="path"/> is a Power BI PBIP's inner <c>definition</c> TMDL root — a TMDL
        /// root directory literally named "definition" whose parent (the <c>.SemanticModel</c> folder) holds the
        /// required <c>definition.pbism</c>. The inverse of <see cref="Resolve"/>: it lets a SAVE recognise a
        /// PBIP-origin model so it can keep the <c>definition/</c> tree TMDL-only (never a folder-JSON clobber) and
        /// place engine sidecars in the <c>.SemanticModel</c> parent rather than polluting the publishable tree.
        /// Structural (no dependency on the optional <c>.platform</c>), so it holds for any well-formed TMDL PBIP.</summary>
        public static bool IsPbipDefinitionFolder(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return false;
            var di = new DirectoryInfo(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.Equals(di.Name, "definition", StringComparison.OrdinalIgnoreCase)
                && IsTmdlRoot(di.FullName)
                && di.Parent != null
                && File.Exists(Path.Combine(di.Parent.FullName, "definition.pbism"));
        }

        /// <summary>Mirrors TE2's <c>IsRootTmdlDirectory</c> (which is private in the vendored handler): a directory is
        /// a TMDL root iff it directly contains database.tmd[l] or model.tmd[l]. Public so the save path can refuse to
        /// write a folder/JSON format into ANY existing TMDL tree (mixing the two formats corrupts the folder).</summary>
        public static bool IsTmdlRoot(string dir) =>
            Directory.Exists(dir) && (
                File.Exists(Path.Combine(dir, "database.tmdl")) ||
                File.Exists(Path.Combine(dir, "database.tmd")) ||
                File.Exists(Path.Combine(dir, "model.tmdl")) ||
                File.Exists(Path.Combine(dir, "model.tmd")));
    }
}
