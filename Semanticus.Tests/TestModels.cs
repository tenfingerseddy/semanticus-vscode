using System;
using System.IO;
using Xunit;

// The engine is single-model-per-process: building a TabularModelHandler sets the process-wide
// TabularModelHandler.Singleton + shared FormulaFixup static state (hole #4, docs/strategy/04). So two test
// classes that each open a model CANNOT run concurrently — xUnit's default cross-class parallelism corrupts the
// shared static ("concurrent update ... corrupted its state"). Serialize the whole assembly until the Singleton
// dies (the IModelSession seam, Phase 1b, is the path to de-static-ing it). This is a real consequence of hole #4.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Semanticus.Tests
{
    /// <summary>Locates test models declared as build inputs and copied beside the test assembly. Runtime tests
    /// never walk toward the checkout, so an external artifacts path, hostile CWD, or ambient sibling clone cannot
    /// select a different fixture.</summary>
    internal static class TestModels
    {
        public static string FindBim(string fileName = "AdventureWorks.bim")
        {
            var path = Path.Combine(AppContext.BaseDirectory, "TestData", fileName);
            return File.Exists(path) ? path : throw new FileNotFoundException(fileName + " was not copied beside the test assembly", path);
        }
    }
}
