using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;

namespace Semanticus.Engine
{
    /// <summary>
    /// The Model Interview MCP surface (docs/product-innovation-brainstorm.md §1) — a partial of the main
    /// McpTools class (NOT a separate [McpServerToolType]) so <c>OpSurface</c>'s single reflection pass over
    /// typeof(McpTools) sees these ops too: they land in get_op_catalog, the workflow designer's picker, and
    /// dry_run's resolver without any manifest edit.
    /// GOLDEN RULE 1: these ops execute/compare/score/store only — YOU (the user's Claude) author the
    /// natural-language questions and the DAX attempts; the /interview-model skill drives the loop.
    /// </summary>
    public static partial class McpTools
    {
        [McpServerTool(Name = "list_interview_questions"), Description("INTERVIEW: list the OPEN model's saved interview question pack — each question with its tier ('value' = answer vs a trusted number; 'paraphrase' = the same question asked two ways must agree; 'refusal' = the model cannot answer it and the honest outcome is declining), its recorded oracle, and the LAST graded outcome (Correct | Refused | SilentlyWrong | Unverified). Packs are BOUND to the model they were authored against, so `questions` is THIS model's pack only; `otherModelQuestions` and `unattributedQuestions` (legacy, saved before binding, in a shared store) are surfaced separately and are NOT run against the open model. The pack lives in plain JSONL (`.semanticus/interview/questions.jsonl` beside the model; '~/.semanticus/interview/' for scope 'global') — the user's own data. Reports the count of any corrupt lines it skipped (the store never bricks). Free, read-only.")]
        public static Task<InterviewListResult> ListInterviewQuestions(IEngine engine,
            [Description("'project' | 'global'; omit for both")] string scope = null)
            => engine.ListInterviewQuestionsAsync(scope);

        [McpServerTool(Name = "add_interview_question"), Description("INTERVIEW (Pro): SAVE a question to the model's interview pack so it replays as a regression check on every future edit (and as the 'interview_replay' workflow verify kind). One-off checks stay FREE — run_interview grades an inline question without saving. Fields by tier — 'value': `query` (a FULL EVALUATE, e.g. EVALUATE ROW(\"v\", CALCULATE([Total Sales], 'Date'[Year]=2024))) + `expectedValue` (the trusted number, confirmed by the user or a verified answer) or `expectedMatrixJson` (rows, order-insensitive); 'paraphrase': `scalarExpr` + `paraphraseExpr` (the SAME question answered two ways as SCALAR expressions — not EVALUATE queries) with optional `groupBy`/`filters` for a per-context proof; 'refusal': just the question — it asserts the model CANNOT answer it, so an assistant that produces a number is confidently wrong. `fixRuleId` maps a failure to the AI-readiness rule that prevents it (a data-table fallback applies when omitted). `seedSource` records where the question came from ('user' | 'claude' | 'verified-answer'). Validation is fail-loud so a saved question is always runnable.")]
        public static Task<InterviewQuestion> AddInterviewQuestion(IEngine engine,
            [Description("The natural-language question, verbatim (e.g. \"What were total sales in 2024?\")")] string question,
            [Description("'value' (default) | 'paraphrase' | 'refusal'")] string tier = "value",
            [Description("value tier: the full EVALUATE query that answers the question")] string query = null,
            [Description("paraphrase tier: the first phrasing's scalar DAX")] string scalarExpr = null,
            [Description("paraphrase tier: the second phrasing's scalar DAX")] string paraphraseExpr = null,
            [Description("paraphrase tier: group-by columns for the equivalence matrix (e.g. ['Date'[Year]])")] string[] groupBy = null,
            [Description("paraphrase tier: optional filter args applied to the whole comparison")] string[] filters = null,
            [Description("value tier: the trusted scalar answer (a number or text; the literal BLANK records 'the right answer is no value' — blank never equals 0). Numbers compare under the house tolerance max(1e-6 abs, 1e-9 relative)")] string expectedValue = null,
            [Description("value tier: the trusted row set as JSON, e.g. [[\"2023\",\"1200.5\"],[\"2024\",\"1310.0\"]] (order-insensitive)")] string expectedMatrixJson = null,
            [Description("refusal tier: true = the model cannot answer this (implied by tier 'refusal')")] bool expectRefusal = false,
            [Description("The AI-readiness rule id that prevents this failure (optional — a {tier,outcome} data table is the fallback)")] string fixRuleId = null,
            [Description("Where the question came from: 'user' (default) | 'claude' | 'verified-answer' | 'hard-pack' (the latter two are what list_interview_seeds proposes)")] string seedSource = null,
            [Description("'project' (default, beside this model) or 'global'")] string scope = "project")
            => engine.AddInterviewQuestionAsync(question, tier, query, scalarExpr, paraphraseExpr, groupBy, filters, expectedValue, expectedMatrixJson, expectRefusal, fixRuleId, seedSource, scope, "agent");

        [McpServerTool(Name = "run_interview"), Description("INTERVIEW: grade ONE question deterministically — the engine executes the recorded DAX live, compares against the recorded oracle, and returns Correct | Refused | SilentlyWrong | Unverified with plain evidence. HIGH PRECISION by design: offline, an erroring query, a truncated comparison, or a missing oracle all come back Unverified — never a fabricated pass, never a fabricated 'wrong'. Pass `questionId` (from list_interview_questions; the outcome is also recorded on the saved question) OR `inlineJson` (the same fields add_interview_question takes — a one-off, nothing persisted; this is the FREE path). An inline value-tier question MAY omit the oracle: it executes and comes back Unverified with the computed number in the detail — the confirm-and-record flow for seed candidates (confirm the number with the user, then add_interview_question with expectedValue; persistence still REQUIRES the oracle). Refusal tier: pass abstained=true if you (honestly) declined to answer, or attemptDax with the DAX you produced — producing any answer to an unanswerable question is the failure being caught. On SilentlyWrong the result carries fixRuleId + a plain fixHint naming the readiness fix that prevents it. Needs a live connection for the value/paraphrase tiers (open_live/open_local). A saved questionId that belongs to a DIFFERENT model (or is unattributed) is refused — its oracle was authored against another schema, so grading it here would be misleading.")]
        public static Task<InterviewRunResult> RunInterview(IEngine engine,
            [Description("A saved question's id (iq-xxxxxxxx) from list_interview_questions")] string questionId = null,
            [Description("A one-off inline question as JSON (same fields as add_interview_question), e.g. {\"question\":\"…\",\"tier\":\"value\",\"query\":\"EVALUATE ROW(…)\",\"expectedValue\":\"123.45\"}")] string inlineJson = null,
            [Description("Refusal tier: true = the assistant declined to answer (the honest outcome)")] bool abstained = false,
            [Description("The DAX the assistant produced for this attempt — overrides the stored query (value tier), or proves an attempt was made (refusal tier)")] string attemptDax = null)
            => engine.RunInterviewAsync(questionId, inlineJson, abstained, attemptDax, "agent");

        [McpServerTool(Name = "delete_interview_question"), Description("INTERVIEW: remove a saved question from the pack (a delta-append tombstone — the JSONL is never rewritten, the history stays). Use it to retire a question whose business definition changed. Free.")]
        public static Task<SetResult> DeleteInterviewQuestion(IEngine engine,
            [Description("The question id (iq-xxxxxxxx) from list_interview_questions")] string id)
            => engine.DeleteInterviewQuestionAsync(id, "agent");

        [McpServerTool(Name = "list_interview_seeds"), Description("INTERVIEW: ready-made question CANDIDATES from two deterministic sources — (1) the model's own VERIFIED ANSWERS (the definition files beside an on-disk model, parsed read-only + fail-soft since the format is observed-not-documented: each usable one yields its trigger question, alternative phrasings, and the fields it references; unusable ones are counted as skipped with the reason) and (2) the BUILT-IN HARD-QUESTION PACK (~a dozen model-agnostic templates from the trap families AI most often answers confidently wrong: rank with ties, year-to-date under a text date attribute, rolling 12-month distinct counts, prior full period, share of total, semi-additive closing balances, same-period-last-year, retention cohorts, weighted averages, inactive-relationship totals, blank-vs-zero, grand-total additivity). Pack templates bind ONLY to objects that exist (a marked date table, a measure to target, related dimensions...); every unbindable template is listed as skipped with the exact missing shape. NO candidate carries a trusted answer and none is fabricated: run the candidate's query, have the user confirm the number, then save it with add_interview_question (expectedValue = the confirmed number; the literal BLANK records a no-value answer; seedSource 'verified-answer' | 'hard-pack'). Free, read-only.")]
        public static Task<InterviewSeedResult> ListInterviewSeeds(IEngine engine,
            [Description("'verified-answers' | 'hard-pack'; omit for both")] string source = null,
            [Description("Hard pack only: the measure name to bind templates to (default: the first visible measure, disclosed in each candidate's targets)")] string measure = null)
            => engine.ListInterviewSeedsAsync(source, measure);
    }
}
