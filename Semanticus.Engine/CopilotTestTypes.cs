// The shapes the Copilot test ops send over both doors (Kane, 15 Oct 2026). Plain data: camelCase on the wire, the same on the pipe and on MCP.

using System;

namespace Semanticus.Engine
{
    /// <summary>What Copilot sees of the open model, as the one text the student reads, and the counts the page shows beside it.</summary>
    public sealed class CopilotViewResult
    {
        /// <summary>The model as Copilot sees it: visible tables, columns and measures with descriptions, formats and other names, the relationships, and the AI instructions. Hidden objects and objects left out of the AI data schema are not in it. No data, no formulas.</summary>
        public string Text { get; set; }
        public CopilotSee[] Sees { get; set; } = Array.Empty<CopilotSee>();
        public int Tables { get; set; }
        public int Columns { get; set; }
        public int Measures { get; set; }
        public int MeasuresDescribed { get; set; }
        public int OtherNames { get; set; }
        public int InstructionWords { get; set; }
        public int LeftOut { get; set; }
        public string Note { get; set; }
    }

    public sealed class CopilotSee
    {
        public string What { get; set; }
        public string Count { get; set; }
        /// <summary>True when the student is given it, false when it is kept from the student.</summary>
        public bool On { get; set; }
    }

    /// <summary>One question for the bank. The kind is totals, filters, time, ranking, words, names or cannot. A cannot question has no reference DAX: the right answer is to say so.</summary>
    public sealed class CopilotQuestionIn
    {
        public string Question { get; set; }
        public string Kind { get; set; }
        /// <summary>The DAX that gives the right answer, a full EVALUATE (EVALUATE ROW("v", [Total Sales]) for one number). The engine runs it and keeps what it returns as the right answer.</summary>
        public string ReferenceDax { get; set; }
    }

    public sealed class CopilotAddItem
    {
        public int Index { get; set; }
        public bool Ok { get; set; }
        public string Id { get; set; }
        /// <summary>What the reference DAX returned, as the right answer: one number or word, "N rows", or "Say no".</summary>
        public string Answer { get; set; }
        public string Error { get; set; }
    }

    public sealed class CopilotAddResult
    {
        public int Added { get; set; }
        public int Rejected { get; set; }
        public int BankSize { get; set; }
        public CopilotAddItem[] Items { get; set; } = Array.Empty<CopilotAddItem>();
        public string Note { get; set; }
    }

    public sealed class CopilotAsk
    {
        public int N { get; set; }
        public string Question { get; set; }
    }

    /// <summary>The questions of the next round, numbered. A student gets the numbers and the words and nothing else.</summary>
    public sealed class CopilotStudentQuestions
    {
        public int Round { get; set; }
        public int Total { get; set; }
        /// <summary>Pass this back to run_interview_batch. It says which bank the numbers are for.</summary>
        public string BankKey { get; set; }
        public int BatchSize { get; set; } = 25;
        /// <summary>Set when the last round is not finished: send the answers to these numbers with this roundId, and `questions` holds only the numbers still to answer.</summary>
        public string OpenRoundId { get; set; }
        public CopilotAsk[] Questions { get; set; } = Array.Empty<CopilotAsk>();
        public string Note { get; set; }
    }

    /// <summary>The student's answer to one numbered question: DAX, or cannotAnswer.</summary>
    public sealed class CopilotAnswerIn
    {
        public int N { get; set; }
        public string Dax { get; set; }
        public bool CannotAnswer { get; set; }
    }

    public sealed class CopilotScore
    {
        public int Right { get; set; }
        /// <summary>Answers that could be checked (right or a miss). A could-not-check answer is in neither.</summary>
        public int Total { get; set; }
        public int CouldNotCheck { get; set; }
        /// <summary>Right out of Total, 0 to 1; null when nothing could be checked.</summary>
        public double? Share { get; set; }
    }

    public sealed class CopilotMissOut
    {
        public int N { get; set; }
        public string Question { get; set; }
        public string Kind { get; set; }
        public string Miss { get; set; }
        public string MissWords { get; set; }
        public string StudentDax { get; set; }
        public string StudentAnswer { get; set; }
        public string ReferenceDax { get; set; }
        public string ReferenceAnswer { get; set; }
        public string Detail { get; set; }
        /// <summary>The cause this miss was grouped under, in the engine's words; null before the round is complete.</summary>
        public string Cause { get; set; }
    }

    public sealed class CopilotGroup
    {
        public string Cause { get; set; }
        public string Fix { get; set; }
        public string Label { get; set; }
        public string FixWords { get; set; }
        public int Count { get; set; }
        public int[] Numbers { get; set; } = Array.Empty<int>();
        public string[] Objects { get; set; } = Array.Empty<string>();
        /// <summary>jev | rules</summary>
        public string By { get; set; }
    }

    public sealed class CopilotKindScore
    {
        public string Kind { get; set; }
        public int Right { get; set; }
        public int Total { get; set; }
    }

    public sealed class CopilotChangeOut
    {
        public string Title { get; set; }
        public string Where { get; set; }
        public string On { get; set; }
        public string Before { get; set; }
        public string After { get; set; }
        public int[] ForMisses { get; set; } = Array.Empty<int>();
        /// <summary>How many of the misses it was for the round now got right.</summary>
        public int Fixed { get; set; }
    }

    public sealed class CopilotRoundSummary
    {
        public string Id { get; set; }
        public int Number { get; set; }
        public string When { get; set; }
        public string Student { get; set; }
        public int Total { get; set; }
        public int Answered { get; set; }
        public bool Complete { get; set; }
        /// <summary>The questions the assistant has seen the results of.</summary>
        public CopilotScore Practice { get; set; }
        /// <summary>The held-back fifth: its score only. Its questions, answers and misses are never returned.</summary>
        public CopilotScore HeldBack { get; set; }
        public CopilotScore All { get; set; }
        public CopilotKindScore[] ByKind { get; set; } = Array.Empty<CopilotKindScore>();
        /// <summary>How many practice misses there are of each kind of miss.</summary>
        public System.Collections.Generic.Dictionary<string, int> MissCounts { get; set; } = new System.Collections.Generic.Dictionary<string, int>();
        /// <summary>Practice questions that were right in the round before and are a miss now.</summary>
        public int NewMisses { get; set; }
        public CopilotChangeOut[] Changes { get; set; } = Array.Empty<CopilotChangeOut>();
        public CopilotGroup[] Groups { get; set; } = Array.Empty<CopilotGroup>();
        /// <summary>Practice misses with the student's DAX beside the right DAX. Only in the detail of one round.</summary>
        public CopilotMissOut[] Misses { get; set; } = Array.Empty<CopilotMissOut>();
    }

    public sealed class CopilotKindCount
    {
        public string Kind { get; set; }
        public int Count { get; set; }
    }

    public sealed class CopilotRoundsView
    {
        public CopilotRoundSummary[] Rounds { get; set; } = Array.Empty<CopilotRoundSummary>();
        public int BankSize { get; set; }
        public CopilotKindCount[] BankByKind { get; set; } = Array.Empty<CopilotKindCount>();
        public string BankKey { get; set; }
        /// <summary>Questions held back from the assistant, as a count. Which ones they are is not said.</summary>
        public int HeldBack { get; set; }
        public string Note { get; set; }
    }

    public sealed class CopilotBatchResult
    {
        public string RoundId { get; set; }
        public int Round { get; set; }
        public int Graded { get; set; }
        public int Total { get; set; }
        public int Answered { get; set; }
        public bool Complete { get; set; }
        public CopilotRoundSummary Summary { get; set; }
        /// <summary>Numbers that could not be graded, each with the reason (out of range, asked twice).</summary>
        public string[] Skipped { get; set; } = Array.Empty<string>();
        public string Note { get; set; }
    }
}
