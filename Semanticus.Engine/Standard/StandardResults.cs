using System.Collections.Generic;
using Semanticus.Analysis;

namespace Semanticus.Engine
{
    // The wire shapes of the four standard tools. Plain words in every sentence field: the page shows them as they are.

    /// <summary>One part of a standard, worked out on the open model. Nothing here is written by a preview.</summary>
    public sealed class StandardPartResult
    {
        /// <summary>checks | names | formats | folders | descriptions | synonyms | keys-and-totals | ai-instructions</summary>
        public string Part { get; set; }
        /// <summary>The choice in plain words, as the page shows it.</summary>
        public string Setting { get; set; }
        /// <summary>Changes Semanticus makes now, because they are sure (a format for a known kind of measure, a key hidden). One undo.</summary>
        public int ChangesNow { get; set; }
        /// <summary>How many objects go to the Plan, as one task for this part, because they need words or could break a report (a rename, a description).</summary>
        public int Tasks { get; set; }
        /// <summary>Up to three before and after lines for this part.</summary>
        public string[] Examples { get; set; } = new string[0];
        /// <summary>One plain sentence when the count needs a word of explanation. Null otherwise.</summary>
        public string Note { get; set; }
    }

    /// <summary>The standard in force on the open model and where it came from.</summary>
    public sealed class StandardState
    {
        public string Name { get; set; }
        /// <summary>model-file | your-default | built-in</summary>
        public string Source { get; set; }
        public string Path { get; set; }
        /// <summary>Where set_standard writes by default.</summary>
        public string WritePath { get; set; }
        /// <summary>Your default standard file on this computer, and whether it exists yet. The page points a model at it with use.</summary>
        public string UserPath { get; set; }
        public bool UserExists { get; set; }
        public string Use { get; set; }
        public string Hash { get; set; }
        /// <summary>Every part, as it is held now (the preset, the shared file and the model's own lines already merged).</summary>
        public StandardSpec Effective { get; set; }
        /// <summary>Only the lines written in the file that was read. Null when the standard is built in.</summary>
        public StandardSpec Written { get; set; }
        /// <summary>The file text that was read, so the page can show the file in place. Null when built in.</summary>
        public string FileText { get; set; }
        /// <summary>Plain sentences about anything that could not be read. The standard still works.</summary>
        public string[] Notes { get; set; } = new string[0];
        /// <summary>What the model was last checked against (name and short hash), or null when it never was.</summary>
        public string CheckedAgainst { get; set; }
    }

    public sealed class GetStandardResult
    {
        public StandardState Standard { get; set; }
        /// <summary>light | balanced | strict, each as a full standard.</summary>
        public StandardSpec[] Presets { get; set; }
    }

    public sealed class SetStandardResult
    {
        /// <summary>The file written.</summary>
        public string Path { get; set; }
        /// <summary>The parts the patch changed, in plain words.</summary>
        public string[] Changed { get; set; } = new string[0];
        public StandardState Standard { get; set; }
        public string Sentence { get; set; }
    }

    public sealed class PreviewStandardResult
    {
        public StandardState Standard { get; set; }
        public StandardPartResult[] Parts { get; set; } = new StandardPartResult[0];
        public int ChangesNow { get; set; }
        /// <summary>Plan rows apply would add: one for each part that has words or renames to hand over.</summary>
        public int Tasks { get; set; }
        /// <summary>The grade now and the grade after the changes now. Grades are absolute: a standard never moves them by itself.</summary>
        public string GradeNow { get; set; }
        public string GradeAfter { get; set; }
        public int ScoreNow { get; set; }
        public int ScoreAfter { get; set; }
        public string Sentence { get; set; }
    }

    public sealed class ApplyStandardResult
    {
        public long Revision { get; set; }
        public string[] Applied { get; set; } = new string[0];
        public StandardPartResult[] Parts { get; set; } = new StandardPartResult[0];
        public int ChangesNow { get; set; }
        /// <summary>Plan rows this apply added (renames, words).</summary>
        public int Tasks { get; set; }
        public string[] PlanRows { get; set; } = new string[0];
        public string GradeBefore { get; set; }
        public string GradeAfter { get; set; }
        public string Sentence { get; set; }
    }

    /// <summary>One Plan task the apply step hands over: the words, the engine-built brief and the objects.</summary>
    internal sealed class StandardPlanTask
    {
        public string Part { get; set; }
        public string Key { get; set; }
        public string Words { get; set; }
        public string Brief { get; set; }
        public int Count { get; set; }
        public string[] Objects { get; set; } = new string[0];
    }

    /// <summary>The raw outcome of one pass over the model: the numbers and examples for the page, what was written (when it wrote),
    /// and the Plan tasks to hand over.</summary>
    internal sealed class StandardWork
    {
        public List<StandardPartResult> Parts { get; } = new List<StandardPartResult>();
        public List<string> Applied { get; } = new List<string>();
        public List<StandardPlanTask> Tasks { get; } = new List<StandardPlanTask>();
    }
}
