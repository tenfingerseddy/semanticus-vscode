using System;
using System.Collections.Generic;
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
        /// root directory literally named "definition". The inverse of <see cref="Resolve"/>: it lets a SAVE recognise a
        /// PBIP-origin model so it can keep the <c>definition/</c> tree TMDL-only (never a folder-JSON clobber) and
        /// place engine sidecars in the parent rather than polluting the publishable tree. A frozen export often has
        /// no <c>definition.pbism</c>; requiring that file would write sidecars inside <c>definition/</c>.</summary>
        public static bool IsPbipDefinitionFolder(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return false;
            var di = new DirectoryInfo(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.Equals(di.Name, "definition", StringComparison.OrdinalIgnoreCase)
                && IsTmdlRoot(di.FullName)
                && di.Parent != null;
        }

        /// <summary>The kinds of model a folder can be, as the code that OPENS models sees them: TE2's handler constructor
        /// (<c>TabularModelHandler.FileHandling.cs</c>) and <see cref="Resolve"/>. A folder can be several at once.</summary>
        [Flags]
        public enum ModelShape
        {
            None = 0,
            /// <summary>database.tmd[l] or model.tmd[l] directly in it (TE2 <c>IsRootTmdlDirectory</c>).</summary>
            TmdlRoot = 1,
            /// <summary>A <c>definition/</c> folder in it that is a TMDL root (what <see cref="Resolve"/> redirects to).</summary>
            DefinitionFolder = 2,
            /// <summary>A Power BI <c>*.SemanticModel</c> folder.</summary>
            SemanticModelFolder = 4,
            /// <summary>A <c>.pbip</c> directly in it: TE2 opens the folder as that project's model.</summary>
            PbipProject = 8,
            /// <summary>definition.pbism directly in it: the marker of a Power BI semantic model.</summary>
            PbismMarker = 16,
            /// <summary>database.json at its root: a Tabular Editor split folder (the loader's fall-through for any folder).</summary>
            TabularEditorFolder = 32,
            /// <summary>A folder named like a .bim file: it is not a file, but it is named as one.</summary>
            BimNamedFolder = 64,
            /// <summary>Exactly one <c>*.SemanticModel</c> in it with a TMDL <c>definition/</c>: <see cref="Resolve"/> opens the folder as that model (a project folder with no .pbip).</summary>
            SemanticModelHolder = 128,
        }

        /// <summary>Shapes that name a place that HOLDS a model without owning what is beside it: a project folder has its
        /// <c>*.SemanticModel</c> and reports in named children, and a Tabular Editor folder reads only its named folders
        /// (<see cref="TabularEditorFolderParts"/>). A new child of either is inert to the model; the folder itself, and anything
        /// inside a folder the model reads, is not. Every other shape owns its whole subtree.</summary>
        public const ModelShape HostShapes = ModelShape.PbipProject | ModelShape.TabularEditorFolder | ModelShape.SemanticModelHolder;

        /// <summary>Every file or folder name an opener looks for to decide a folder is a model. One list, read by
        /// <see cref="ShapeOf"/> and by the test that fails when the opener starts looking for a name that is not here
        /// (<c>ModelShapeDriftTests</c>). Entries starting with a dot are an extension or a folder-name ending.</summary>
        public static readonly string[] OpenerNames =
        {
            "database.tmdl", "database.tmd", "model.tmdl", "model.tmd", "definition", ".SemanticModel",
            ".pbip", "definition.pbism", "database.json", ".bim",
        };

        /// <summary>Names the opener reads that are not a way for a FOLDER to be a model, each with why. Reached only through a
        /// shape above (a project file's own contents, a single file, or an extension test on a file).</summary>
        public static readonly (string Name, string Why)[] OpenerNamesThatAreNotFolderShapes =
        {
            ("model.bim", "a .bim file the .pbip points at; reached only from a folder with a .pbip in it (PbipProject)"),
            ("definition.pbir", "the report's pointer inside a .pbip project; reached only through a .pbip (PbipProject)"),
            (".pbit", "a Power BI template FILE; a folder is never one"),
            (".tmd", "an extension test on a FILE the person opened; the folder walk uses the TMDL root names"),
            (".tmdl", "an extension test on a FILE the person opened; the folder walk uses the TMDL root names"),
            (".json", "the files inside a split folder's own folders; reached only through database.json (TabularEditorFolder)"),
        };

        /// <summary>The folders (and their members) a Tabular Editor split folder's loader reads by name from its root
        /// (<c>SplitModelSerializer.CombineFolderJson</c>): every folder directly inside <c>tables/</c> is read as a table, so
        /// a stranger there stops the model opening. Anything else directly in the root is inert.</summary>
        public static readonly string[] TabularEditorFolderParts =
            { "tables", "dataSources", "expressions", "relationships", "cultures", "perspectives", "roles" };

        /// <summary>True when a folder named <paramref name="leaf"/> inside <paramref name="parent"/> is read BY NAME by the opener
        /// of <paramref name="parent"/>: <see cref="Resolve"/> looks for a <c>definition</c> folder in any folder (the bare folder
        /// names in <see cref="OpenerNames"/>), and a Tabular Editor split folder reads each of
        /// <see cref="TabularEditorFolderParts"/> from its root, case-insensitively. A new model folder with such a name makes the
        /// folder around it open as that model, or stop opening, so it is never a free name.</summary>
        public static bool IsNameReadInside(string parent, string leaf)
        {
            if (string.IsNullOrEmpty(leaf)) return false;
            if (OpenerNames.Any(n => !n.Contains('.') && string.Equals(n, leaf, StringComparison.OrdinalIgnoreCase))) return true;
            return TabularEditorFolderParts.Contains(leaf, StringComparer.OrdinalIgnoreCase)
                && ShapeOf(parent).HasFlag(ModelShape.TabularEditorFolder);
        }

        /// <summary>What kinds of model <paramref name="dir"/> is, by the same marks the openers use. <see cref="ModelShape.None"/>
        /// for a folder that is not one or does not exist. Read-only probing of that one folder, never its parents.</summary>
        public static ModelShape ShapeOf(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return ModelShape.None;
            var shape = ModelShape.None;
            var leaf = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (IsTmdlRoot(dir)) shape |= ModelShape.TmdlRoot;
            if (IsTmdlRoot(Path.Combine(dir, "definition"))) shape |= ModelShape.DefinitionFolder;
            if (leaf.EndsWith(".SemanticModel", StringComparison.OrdinalIgnoreCase)) shape |= ModelShape.SemanticModelFolder;
            if (leaf.EndsWith(".bim", StringComparison.OrdinalIgnoreCase)) shape |= ModelShape.BimNamedFolder;
            try { if (Directory.EnumerateFiles(dir, "*.pbip", SearchOption.TopDirectoryOnly).Any()) shape |= ModelShape.PbipProject; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            if (File.Exists(Path.Combine(dir, "definition.pbism"))) shape |= ModelShape.PbismMarker;
            if (File.Exists(Path.Combine(dir, "database.json"))) shape |= ModelShape.TabularEditorFolder;
            if (DefinitionUnderProjectFolder(dir) != null) shape |= ModelShape.SemanticModelHolder;
            return shape;
        }

        /// <summary>The outermost folders INSIDE a TMDL root that are TMDL roots themselves (a copy someone saved into the model's own folder).
        /// The TMDL loader reads every folder under a root, so each one merges with the real model and the model stops opening.
        /// Empty when there are none. A folder the engine is still writing (<c>.semanticus-save-*</c>) is not one.</summary>
        public static string[] NestedModelRoots(string root)
        {
            var found = new System.Collections.Generic.List<string>();
            if (!IsTmdlRoot(root)) return found.ToArray();
            void Walk(string dir)
            {
                IEnumerable<string> kids;
                try { kids = Directory.EnumerateDirectories(dir).ToList(); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return; }
                foreach (var kid in kids.OrderBy(k => k, StringComparer.Ordinal))
                {
                    if (Path.GetFileName(kid).StartsWith(".semanticus-save-", StringComparison.Ordinal)) continue;
                    if (IsTmdlRoot(kid)) found.Add(kid); else Walk(kid);
                }
            }
            Walk(root);
            return found.ToArray();
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
