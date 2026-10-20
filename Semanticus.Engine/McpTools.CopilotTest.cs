// The Copilot test's tools (Kane, 15 Oct 2026): the model interview as a test loop. The person's own assistant writes a bank of questions, runs a small student
// model on them, and these tools prove the answers and grade them by number. The same IEngine methods sit behind the Studio page (EngineRpcTarget), so the person
// and the assistant read one score. GOLDEN RULE 1: nothing here runs inference. The student runs in the assistant, on the person's own plan.

using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;

namespace Semanticus.Engine
{
    public static partial class McpTools
    {
        [McpServerTool(Name = "get_copilot_view"), Description("COPILOT TEST: exactly what Copilot is given to answer questions about the OPEN model: the visible tables, columns and measures with their descriptions, formats and other names, the relationships, and the AI instructions. Hidden objects and objects left out of the AI data schema are NOT in it. It holds no data, no formulas and no right answers. `text` is the one block to hand a student model; the rest are counts. Read-only, free. See the interview-model skill for the loop.")]
        public static Task<CopilotViewResult> GetCopilotView(IEngine engine) => engine.GetCopilotViewAsync();

        [McpServerTool(Name = "add_interview_questions"), Description("COPILOT TEST: add many questions to the model's question bank in ONE call. Each is {question, kind, referenceDax}. `kind` is one of totals, filters, time, ranking, words (a word that could mean two things, like 'sales' or 'best'), names (a word people use that is not a model name, like 'revenue') or cannot (the model has no data for it: the right answer is to say so, no referenceDax). `referenceDax` is a full EVALUATE that gives the right answer (EVALUATE ROW(\"v\", [Total Sales]) for one number). The engine RUNS it and keeps the result as the proven right answer, so it needs a live connection; a question whose reference DAX does not run is rejected with the reason and does not enter the bank. The engine holds back every fifth question of the bank, graded every round but never returned, so the held-back score cannot be tuned to the bank; it does not say which. Aim for 100 or more questions, balanced across the kinds, in the words a real person would use. Written beside the model (.semanticus/interview/). Free.")]
        public static async Task<CopilotAddResult> AddInterviewQuestions(IEngine engine,
            [Description("The questions: [{question, kind, referenceDax}, ...]")] CopilotQuestionIn[] questions)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var r = await engine.AddInterviewQuestionsAsync(questions, "agent");
            McpToolsTesting.Note(engine, "add_interview_questions", "Added " + (r?.Added ?? 0) + " questions to the Copilot test bank.", r != null && r.Added > 0, null, clock);
            return r;
        }

        [McpServerTool(Name = "get_student_questions"), Description("COPILOT TEST: the questions of the next round, numbered 1 to N, words only: no kind, no answer, no reference DAX. Hand them to the student in batches of `batchSize` (25) together with get_copilot_view's `text`, and ask for one DAX query per number, or 'cannot answer'. Also returns `bankKey`, which run_interview_batch takes so answers are never graded against a different bank. If the last round was started and not finished, `openRoundId` is set and `questions` holds only the numbers still to answer: send their answers with that roundId. Read-only, free.")]
        public static Task<CopilotStudentQuestions> GetStudentQuestions(IEngine engine) => engine.GetStudentQuestionsAsync();

        [McpServerTool(Name = "run_interview_batch"), Description("COPILOT TEST: grade a whole round of the student's answers BY NUMBER in one call. Pass every answer you have as {n, dax} (a full EVALUATE, or an expression) or {n, cannotAnswer: true}. The engine runs each DAX on the open model (two at a time), compares the result with the stored right answer (a number within max(1e-6, 1e-9 x value); a blank is never 0; a table without regard to row order) and names the kind of miss: wrong_field, wrong_filter, wrong_number, said_no (could answer), should_say_no, dax_failed, or could_not_check (never counted as right or wrong). Leave `roundId` empty to start a new round (the call a person may be asked about once); to add more answers to a round already started, pass its roundId. `changes` lists the changes you applied since the last round, each {title, where ('AI instructions' | 'Description' | 'Other names'), on, before, after, forMisses: [the numbers of the misses it is for]}, so each can say how many it fixed. When the last answer is graded, the practice misses are grouped into a few causes in one step (Jev picks the cause when it is on and sure; plain rules otherwise). The result has the practice score and the HELD-BACK score (a score only: the held-back questions' misses are never returned), each kind's score, the practice misses with the student's DAX beside the right DAX, and the groups. Needs a live connection. Free.")]
        public static async Task<CopilotBatchResult> RunInterviewBatch(IEngine engine,
            [Description("The student's answers: [{n, dax} | {n, cannotAnswer: true}, ...]")] CopilotAnswerIn[] answers,
            [Description("Empty to start a new round; a round's id to add answers to it")] string roundId = null,
            [Description("The bankKey from get_student_questions")] string bankKey = null,
            [Description("Which student answered, in words (default 'Haiku 5.5')")] string student = null,
            [Description("The changes tried since the last round")] CopilotRoundChange[] changes = null)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var r = await engine.RunInterviewBatchAsync(roundId, bankKey, student, answers, changes, "agent");
            McpToolsTesting.Note(engine, "run_interview_batch", "Graded " + (r?.Graded ?? 0) + " answers: round " + (r?.Round ?? 0) + ".", r != null, null, clock);
            return r;
        }

        [McpServerTool(Name = "interview_rounds"), Description("COPILOT TEST: every round so far with its scores (practice and held-back, right out of what could be checked), each kind's score, the count of each kind of miss, the changes tried in it and how many misses each fixed, and the groups of misses. Pass `round` (an id or a number) to also get that round's practice misses with the student's DAX beside the right DAX. The held-back questions' misses are never in it. Also the bank's size and its questions by kind. Read-only, free.")]
        public static Task<CopilotRoundsView> InterviewRounds(IEngine engine,
            [Description("A round's id or number, for its misses")] string round = null)
            => engine.InterviewRoundsAsync(round);
    }
}
