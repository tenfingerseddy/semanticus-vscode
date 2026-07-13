using System;
using System.IO;
using System.Linq;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Serialization;

namespace Semanticus.Smoke
{
    /// <summary>
    /// M0 smoke test for Semanticus.Core. Proves the headless .NET 8 TOMWrapper port can:
    ///   (1) load a model,
    ///   (2) fire the real ObjectChanged event from the SetValue choke point,
    ///   (3) track dirty state (HasUnsavedChanges),
    ///   (4) serialize to TMDL (SerializeDatabaseToFolder),
    ///   (5) re-load from TMDL (DeserializeDatabaseFromFolder),
    ///   (6) round-trip TMDL idempotently (save -> reload -> save produces identical files).
    /// Exit code 0 = all checks passed.
    /// </summary>
    internal static class Program
    {
        private static int _failures;

        private static int Main(string[] args)
        {
            try
            {
                var bim = args.Length > 0 ? args[0] : FindTestBim();
                Console.WriteLine($"[i] Source model: {bim}");

                var outRoot = Path.Combine(Path.GetTempPath(), "semanticus-m0");
                if (Directory.Exists(outRoot)) Directory.Delete(outRoot, true);
                var tmdl1 = Path.Combine(outRoot, "tmdl1");
                var tmdl2 = Path.Combine(outRoot, "tmdl2");

                // ---- (1) Load ------------------------------------------------------------
                var handler = new TabularModelHandler(bim);
                var model = handler.Model;
                Check("load: model is non-null", model != null);
                Console.WriteLine($"[i] Loaded '{model.Database?.Name ?? model.Name}' | tables={model.Tables.Count} measures={model.AllMeasures.Count()} cols={model.AllColumns.Count()}");
                Check("load: at checkpoint (clean) immediately after open", handler.HasUnsavedChanges == false);

                // ---- (2)(3) Edit a measure, observe event + dirty flag -------------------
                var changed = 0;
                ObjectChangedEventArgs lastChange = null;
                handler.ObjectChanged += (s, e) => { changed++; lastChange = e; };

                var measure = model.AllMeasures.FirstOrDefault();
                Check("edit: model has at least one measure to edit", measure != null);
                if (measure != null)
                {
                    var before = measure.Expression;
                    var marker = " /* semanticus M0 */";
                    measure.Expression = (before ?? string.Empty) + marker;

                    Check("event: ObjectChanged fired on measure edit", changed >= 1);
                    Check("event: args carry the edited object", lastChange?.TabularObject == measure);
                    Check("event: args report Expression property", lastChange?.PropertyName == "Expression");
                    Check("dirty: HasUnsavedChanges is true after edit", handler.HasUnsavedChanges);
                    Console.WriteLine($"[i] Edited measure [{measure.Name}] on table '{measure.Table?.Name}'  (events fired: {changed})");
                }

                // ---- (4) Serialize to TMDL ----------------------------------------------
                handler.Save(tmdl1, SaveFormat.TMDL, SerializeOptions.Default, resetCheckpoint: false);
                Check("tmdl: SerializeDatabaseToFolder produced files", Directory.Exists(tmdl1) && Directory.EnumerateFiles(tmdl1, "*.tmdl", SearchOption.AllDirectories).Any());
                Console.WriteLine($"[i] Saved TMDL #1 -> {tmdl1}  ({Directory.EnumerateFiles(tmdl1, "*.tmdl", SearchOption.AllDirectories).Count()} .tmdl files)");

                // ---- (5) Re-load from TMDL ----------------------------------------------
                var handler2 = new TabularModelHandler(tmdl1);
                Check("reload: TMDL folder deserialized", handler2.Model != null);
                Check("reload: table count preserved", handler2.Model.Tables.Count == model.Tables.Count);
                Check("reload: measure count preserved", handler2.Model.AllMeasures.Count() == model.AllMeasures.Count());
                Console.WriteLine($"[i] Reloaded from TMDL | tables={handler2.Model.Tables.Count} measures={handler2.Model.AllMeasures.Count()}");

                // confirm the edit survived the round-trip
                var reloadedMeasure = measure == null ? null : handler2.Model.AllMeasures.FirstOrDefault(m => m.Name == measure.Name && m.Table?.Name == measure.Table?.Name);
                if (measure != null)
                    Check("reload: edited expression survived round-trip", reloadedMeasure != null && (reloadedMeasure.Expression ?? "").Contains("semanticus M0"));

                // ---- (6) Idempotent round-trip: save reloaded model, compare folders -----
                handler2.Save(tmdl2, SaveFormat.TMDL, SerializeOptions.Default, resetCheckpoint: false);
                var diff = CompareFolders(tmdl1, tmdl2);
                if (diff.Count == 0)
                {
                    Check("round-trip: TMDL save->reload->save is byte-identical", true);
                }
                else
                {
                    Check("round-trip: TMDL save->reload->save is byte-identical", false);
                    Console.WriteLine($"    [!] {diff.Count} file difference(s) (first few):");
                    foreach (var d in diff.Take(8)) Console.WriteLine("        " + d);
                    Console.WriteLine("    (note: minor churn here is expected to surface here in M0 — this is exactly the diff-fidelity spike.)");
                }

                // ---- (7) In-place TMDL apply, FULLY INTEGRATED (reflect + structural add + dirty + undo + redo).
                //          A fresh CLEAN handler isolates the dirty/undo assertions from the earlier edits above.
                {
                    var h = new TabularModelHandler(bim);
                    var tName = h.Model.Tables.First(x => x.Measures.Count > 0).Name;
                    var mName = h.Model.Tables.First(x => x.Name == tName).Measures.First().Name;
                    var newName = "Semanticus TMDL New Measure";
                    TabularEditor.TOMWrapper.Table T() => h.Model.Tables.First(x => x.Name == tName);
                    string DescOf(string n) => T().Measures.First(x => x.Name == n).Description;
                    bool HasNew() => T().Measures.Any(x => x.Name == newName);

                    var tl = TabularEditor.TOMWrapper.Utils.TmdlScripter.ScriptTmdl(T()).Replace("\r\n", "\n").Split('\n').ToList();
                    int ml = tl.FindIndex(l => l.TrimStart().StartsWith("measure ") && l.Contains(mName));
                    Check("tmdl-apply: located the measure line in the table TMDL", ml >= 0);
                    if (ml >= 0)
                    {
                        var ind = tl[ml].Substring(0, tl[ml].Length - tl[ml].TrimStart().Length);
                        tl.Insert(ml, ind + "/// applied by smoke");                 // (a) edit existing measure's description
                        tl.Add(ind + "measure '" + newName + "' = 123");             // (b) ADD a new measure (structural)

                        Check("tmdl-apply: model is CLEAN before apply", h.HasUnsavedChanges == false);
                        TabularEditor.TOMWrapper.Utils.TmdlApplier.Apply(h.Model, "./tables/" + tName, string.Join("\n", tl));

                        Check("tmdl-apply: existing measure description reflected", DescOf(mName) == "applied by smoke");
                        Check("tmdl-apply: NEW measure surfaces in the wrapper (structural reinit)", HasNew());
                        Check("tmdl-apply: model marked DIRTY from a clean open", h.HasUnsavedChanges);

                        h.UndoManager.Undo();
                        Check("tmdl-apply: undo removes the new measure", !HasNew());
                        Check("tmdl-apply: undo reverts the description", DescOf(mName) != "applied by smoke");
                        Check("tmdl-apply: undo returns the model to clean (at checkpoint)", h.HasUnsavedChanges == false);

                        h.UndoManager.Redo();
                        Check("tmdl-apply: redo re-adds the new measure", HasNew());
                        Check("tmdl-apply: redo re-applies the description", DescOf(mName) == "applied by smoke");
                        Check("tmdl-apply: redo marks dirty again", h.HasUnsavedChanges);
                    }
                }

                Console.WriteLine();
                if (_failures == 0)
                {
                    Console.WriteLine("==== M0 SMOKE TEST: PASS ====");
                    return 0;
                }
                Console.WriteLine($"==== M0 SMOKE TEST: {_failures} CHECK(S) FAILED ====");
                return 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine("==== M0 SMOKE TEST: EXCEPTION ====");
                Console.WriteLine(ex);
                return 2;
            }
        }

        private static void Check(string label, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (!ok) _failures++;
        }

        private static string FindTestBim()
        {
            // Walk up from the executable until we find the sibling TabularEditor donor clone.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                foreach (var root in new[] { Path.Combine("external", "TabularEditor"), "TabularEditor" })
                {
                    var candidate = Path.Combine(dir.FullName, root, "TOMWrapperTest", "TestData", "AdventureWorks.bim");
                    if (File.Exists(candidate)) return candidate;
                }
                dir = dir.Parent;
            }
            throw new FileNotFoundException("Could not locate TabularEditor\\TOMWrapperTest\\TestData\\AdventureWorks.bim by walking up from " + AppContext.BaseDirectory + ". Pass a .bim/TMDL path as the first argument.");
        }

        private static System.Collections.Generic.List<string> CompareFolders(string a, string b)
        {
            var diffs = new System.Collections.Generic.List<string>();
            string Rel(string root, string f) => f.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
            var aFiles = Directory.EnumerateFiles(a, "*", SearchOption.AllDirectories).ToDictionary(f => Rel(a, f), f => f);
            var bFiles = Directory.EnumerateFiles(b, "*", SearchOption.AllDirectories).ToDictionary(f => Rel(b, f), f => f);

            foreach (var rel in aFiles.Keys.Union(bFiles.Keys).OrderBy(x => x))
            {
                if (!aFiles.ContainsKey(rel)) { diffs.Add("only in #2: " + rel); continue; }
                if (!bFiles.ContainsKey(rel)) { diffs.Add("only in #1: " + rel); continue; }
                if (!File.ReadAllText(aFiles[rel]).Equals(File.ReadAllText(bFiles[rel]), StringComparison.Ordinal))
                    diffs.Add("content differs: " + rel);
            }
            return diffs;
        }
    }
}
