// The Copilot test ops (Kane, 15 Oct 2026): what Copilot sees, the bank, the numbered questions of a round, the grade of a whole round in one call,
// and the rounds read back. Golden rule 1 holds hard: the engine runs no inference. The person's own assistant writes the questions and the
// student's DAX; the engine only runs DAX, compares by number (Interview.cs: InterviewScoring), names the kind of miss and stores. Both doors reach
// the same methods (RPC names in EngineRpcTarget, tools in McpTools.CopilotTest.cs).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine.Query;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        /// <summary>Every fifth question of the bank is held back: graded each round, its misses never returned. Fixed.</summary>
        internal const int HeldBackEvery = 5;

        /// <summary>A test's own DAX runner in place of the live connection. Null on every real path. The model of a unit test has no data, so a fake
        /// student's queries are answered by a table the test holds.</summary>
        internal Func<string, Task<ResultSet>> CopilotDaxForTest;

        private string CopilotRoundsFile()
        {
            var dir = ProjectInterviewDir();
            return dir == null ? null : Path.Combine(dir, CopilotRoundStore.FileName);
        }

        private (string File, List<InterviewQuestion> Bank) CopilotBank()
        {
            var file = InterviewScopeFile("project");
            var (live, _) = InterviewStore.Materialize(file, "project");
            var (mine, _, _) = PartitionByAttribution(live);
            return (file, mine.Where(q => !string.IsNullOrEmpty(q.Kind)).ToList());
        }

        internal static string CopilotBankKey(IEnumerable<InterviewQuestion> bank) => GateHash(bank.Select(q => q.Id).ToArray()).Substring(0, 12);

        /// <summary>Whether a round of this id is already on disk for the open model. The gate asks about the call that makes a round; a call that adds answers to one that exists is not asked again.</summary>
        internal bool CopilotRoundExists(string roundId)
        {
            if (string.IsNullOrWhiteSpace(roundId)) return false;
            try { return CopilotRoundStore.Read(CopilotRoundsFile()).Any(r => string.Equals(r.Id, roundId.Trim(), StringComparison.Ordinal)); }
            catch { return false; }
        }

        private Task<ResultSet> CopilotRunAsync(string query, string origin, LiveConnection live)
            => CopilotDaxForTest != null ? CopilotDaxForTest(query)
             : live == null ? Task.FromResult<ResultSet>(null)
             : RunDaxAsync(query, 10000, origin);

        // ---- what Copilot sees ------------------------------------------------------------------------------------------------------------

        public async Task<CopilotViewResult> GetCopilotViewAsync()
        {
            var s = _sessions.Require();
            return await s.ReadAsync(m => BuildCopilotView(m)).ConfigureAwait(false);
        }

        internal static CopilotViewResult BuildCopilotView(Model m)
        {
            PrepForAiReader.Invalidate(m);
            PrepForAiConfig prep = null;
            try { prep = PrepForAiReader.Read(m); } catch { /* best effort, like every other reader of it */ }
            var excluded = new HashSet<string>(prep?.AiSchemaExcludedNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var cat = QueryCatalog.Read(m);
            var sb = new StringBuilder();
            int tables = 0, columns = 0, measures = 0, described = 0, otherNames = 0, left = 0;

            sb.AppendLine("THE MODEL");
            foreach (var t in m.Tables)
            {
                if (t is CalculationGroupTable) { continue; }
                if (t.IsHidden || excluded.Contains(t.Name)) { left++; continue; }
                tables++;
                var tableSyn = cat.TableSynonyms.TryGetValue(t.Name, out var ts) ? ts : Array.Empty<string>();
                otherNames += tableSyn.Count(x => !string.Equals(x, t.Name, StringComparison.OrdinalIgnoreCase));
                sb.AppendLine();
                sb.AppendLine("Table " + t.Name + Describe(t.Description, tableSyn, t.Name));
                foreach (var c in cat.Columns.Where(c => c.Table == t.Name))
                {
                    if (c.Hidden || excluded.Contains(c.Name) || excluded.Contains(t.Name + "." + c.Name)) { left++; continue; }
                    columns++; otherNames += c.Synonyms.Count(x => !string.Equals(x, c.Name, StringComparison.OrdinalIgnoreCase));
                    sb.AppendLine("  Column " + c.Id + " (" + c.Kind + ")" + Describe(c.Description, c.Synonyms, c.Name));
                }
                foreach (var me in cat.Measures.Where(x => x.Table == t.Name))
                {
                    if (me.Hidden || excluded.Contains(me.Name) || excluded.Contains(t.Name + "." + me.Name)) { left++; continue; }
                    measures++; otherNames += me.Synonyms.Count(x => !string.Equals(x, me.Name, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(me.Description)) described++;
                    var format = string.IsNullOrWhiteSpace(me.FormatString) ? "" : " (format " + me.FormatString + ")";
                    sb.AppendLine("  Measure [" + me.Name + "]" + format + Describe(me.Description, me.Synonyms, me.Name));
                }
            }
            var relationships = m.Relationships.OfType<SingleColumnRelationship>()
                .Where(r => r.FromColumn != null && r.ToColumn != null && r.IsActive
                    && !r.FromColumn.Table.IsHidden && !r.ToColumn.Table.IsHidden)
                .Select(r => "  " + r.FromColumn.Table.Name + "[" + r.FromColumn.Name + "] filters by " + r.ToColumn.Table.Name + "[" + r.ToColumn.Name + "]").ToList();
            if (relationships.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("RELATIONSHIPS");
                foreach (var r in relationships) sb.AppendLine(r);
            }
            var instructions = (prep?.AiInstructions ?? "").Trim();
            var words = instructions.Length == 0 ? 0 : instructions.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length;
            sb.AppendLine();
            sb.AppendLine("AI INSTRUCTIONS");
            sb.AppendLine(instructions.Length == 0 ? "(none)" : instructions);

            var sees = new List<CopilotSee>
            {
                new CopilotSee { What = "Tables, columns and measures", Count = $"{tables} tables, {columns} columns, {measures} measures", On = true },
                new CopilotSee { What = "AI instructions", Count = words + " words", On = true },
                new CopilotSee { What = "Descriptions", Count = $"{described} of {measures} measures", On = true },
                new CopilotSee { What = "Other names", Count = otherNames + " names", On = true },
                new CopilotSee { What = "Hidden tables and columns", Count = left + " left out", On = false },
                new CopilotSee { What = "Your data and the right answers", Count = "never shown", On = false },
            };
            return new CopilotViewResult
            {
                Text = sb.ToString(), Sees = sees.ToArray(), Tables = tables, Columns = columns, Measures = measures, MeasuresDescribed = described,
                OtherNames = otherNames, InstructionWords = words, LeftOut = left,
                Note = "This is what Copilot is given. Hidden objects and objects left out of the AI data schema are not in it. It holds no data, no formulas and no right answers.",
            };

            static string Describe(string description, string[] synonyms, string own)
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(description)) parts.Add(description.Trim().TrimEnd('.', ' ').Replace("\r", " ").Replace("\n", " "));
                var others = (synonyms ?? Array.Empty<string>()).Where(x => !string.Equals(x, own, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (others.Length > 0) parts.Add("also called: " + string.Join(", ", others));
                return parts.Count == 0 ? "" : ": " + string.Join(". ", parts);
            }
        }

        // ---- the bank --------------------------------------------------------------------------------------------------------------------

        /// <summary>Add many questions in one call. The engine runs each reference DAX and keeps what it returns as the right answer; a question whose
        /// reference DAX does not run does not enter the bank. Every fifth question of the bank is held back (not said which).</summary>
        public async Task<CopilotAddResult> AddInterviewQuestionsAsync(CopilotQuestionIn[] items, string origin)
        {
            GuardOutsideWrite(origin);
            if (items == null || items.Length == 0)
                throw new InvalidOperationException("Pass the questions: a list of {question, kind, referenceDax}. The kind is one of " + string.Join(", ", CopilotKinds.All) + ". A cannot question needs no referenceDax.");
            if (items.Length > 600) throw new InvalidOperationException("Add up to 600 questions in one call.");

            var context = _sessions.CurrentContext;
            var sess = context.Session;
            var liveConn = context.Live;
            var file = InterviewScopeFile("project");
            if (file == null)
                throw new InvalidOperationException("No project interview store: open or save a model, or run the engine with a workspace.");
            var identity = PaneIdentity(sess, liveConn);
            if (string.IsNullOrEmpty(identity))
                throw new InvalidOperationException("This model has no durable identity to bind the questions to (it is unsaved and not connected to a live source), so nothing was saved. Save the model (save_model) or connect it (open_live / connect_xmla) first.");
            if (liveConn == null && CopilotDaxForTest == null)
                throw new InvalidOperationException("The engine proves each right answer by running its reference DAX, so it needs a live connection (open_live, connect_xmla or connect_local). Connect, then add the questions again.");
            var modelName = sess == null ? null : await ReadModelNameAsync(sess).ConfigureAwait(false);
            var label = ModelPaneLabel(sess, modelName);

            var (_, existing) = CopilotBank();
            var seen = new HashSet<string>(existing.Select(q => q.Question.Trim().ToLowerInvariant()), StringComparer.Ordinal);
            var outItems = new CopilotAddItem[items.Length];
            var built = new InterviewQuestion[items.Length];

            // Validate first (cheap, in order), then prove the reference answers two at a time so Power BI Desktop stays responsive.
            var toRun = new List<int>();
            for (var i = 0; i < items.Length; i++)
            {
                var it = items[i] ?? new CopilotQuestionIn();
                var text = (it.Question ?? "").Trim();
                var kind = CopilotKinds.Normalize(it.Kind);
                string error = null;
                if (text.Length == 0) error = "The question has no words.";
                else if (kind == null) error = $"'{it.Kind}' is not a kind of question. Use one of: {string.Join(", ", CopilotKinds.All)}.";
                else if (!seen.Add(text.ToLowerInvariant())) error = "This question is already in the bank.";
                else if (kind != CopilotKinds.Cannot && string.IsNullOrWhiteSpace(it.ReferenceDax)) error = "Give the referenceDax: the DAX that answers it. The engine runs it and keeps the result as the right answer.";
                outItems[i] = new CopilotAddItem { Index = i, Ok = error == null, Error = error };
                if (error != null) continue;
                built[i] = new InterviewQuestion
                {
                    Question = text, Kind = kind, Tier = kind == CopilotKinds.Cannot ? "refusal" : "value", ExpectRefusal = kind == CopilotKinds.Cannot,
                    Query = kind == CopilotKinds.Cannot ? null : CopilotGrader.AsQuery(it.ReferenceDax), SeedSource = "copilot-test",
                };
                if (kind != CopilotKinds.Cannot) toRun.Add(i);
            }
            using (var gate = new SemaphoreSlim(2))
            {
                await Task.WhenAll(toRun.Select(async i =>
                {
                    await gate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        var rs = await CopilotRunAsync(built[i].Query, origin, liveConn).ConfigureAwait(false);
                        var err = ProveReference(built[i], rs);
                        if (err != null) { outItems[i].Ok = false; outItems[i].Error = err; built[i] = null; }
                    }
                    catch (Exception ex) { outItems[i].Ok = false; outItems[i].Error = "The reference DAX could not be run: " + ex.Message; built[i] = null; }
                    finally { gate.Release(); }
                })).ConfigureAwait(false);
            }

            // Held back: every fifth question of the whole bank, counted in the order they were added.
            var position = existing.Count;
            var execIdentity = LiveIdentityFor(liveConn);
            var execTarget = LiveTargetLabel(liveConn);
            var added = 0;
            await _interviewGate.WaitAsync().ConfigureAwait(false);
            try
            {
                EnsureContextCurrent(context, "Copilot test question save");
                for (var i = 0; i < items.Length; i++)
                {
                    var q = built[i];
                    if (q == null || !outItems[i].Ok) continue;
                    q.HeldBack = position % HeldBackEvery == HeldBackEvery - 1;
                    position++;
                    q.Id = InterviewStore.NewId();
                    if (!InterviewStore.TryAppend(file, new InterviewStore.Delta
                    {
                        Op = "add", Id = q.Id, When = DateTime.UtcNow.ToString("o"), Origin = origin ?? "agent",
                        Question = q.Question, Tier = q.Tier, Query = q.Query, ExpectedValue = q.ExpectedValue, ExpectedMatrix = q.ExpectedMatrix,
                        ExpectRefusal = q.ExpectRefusal, SeedSource = q.SeedSource, Kind = q.Kind, HeldBack = q.HeldBack,
                        ModelIdentity = identity, ModelLabel = label, ExecIdentity = execIdentity, ExecTarget = execTarget, ModelWitness = modelName,
                    }, out var why))
                    {
                        outItems[i].Ok = false; outItems[i].Error = "Not saved: " + why;
                        position--;
                        continue;
                    }
                    outItems[i].Id = q.Id;
                    outItems[i].Answer = CopilotGrader.ReferenceText(q);
                    added++;
                }
            }
            finally { _interviewGate.Release(); }

            await EmitInterviewActivity("add_interview_questions", added > 0, $"Added {added} questions to the Copilot test bank", null, origin).ConfigureAwait(false);
            var bankAfter = CopilotBank().Bank.Count;
            return new CopilotAddResult
            {
                Added = added, Rejected = items.Length - added, BankSize = bankAfter, Items = outItems,
                Note = added == 0 ? "Nothing was added. Read each item's error." : "Added. The engine keeps part of the bank held back, so the held-back score cannot be tuned to the bank. It is not said which.",
            };
        }

        /// <summary>Turn what a reference query returned into the stored right answer. Returns the reason it cannot, or null.</summary>
        private static string ProveReference(InterviewQuestion q, ResultSet rs)
        {
            if (rs == null) return "The reference DAX was not run (no live connection), so there is no proven answer.";
            if (rs.PolicyRefused) return "The agent policy stopped the reference DAX from running: " + rs.Error;
            if (!string.IsNullOrEmpty(rs.Error)) return "The reference DAX did not run: " + CopilotGrader.Shorten(rs.Error, 240);
            if (rs.Truncated) return "The reference DAX returned too many rows to keep as the right answer. Narrow it.";
            if (rs.Rows == null || rs.Rows.Length == 0) return "The reference DAX returned no rows. Write it as EVALUATE ROW(\"v\", ...) so it always returns one row.";
            if (rs.Rows.Length == 1)
            {
                var row = rs.Rows[0];
                if (row.Length == 0) return "The reference DAX returned a row with no columns.";
                var cell = row[row.Length - 1];
                q.ExpectedValue = cell == null ? "BLANK" : DaxBench.Fmt(cell);
                return null;
            }
            q.ExpectedMatrix = rs.Rows.Select(r => r.Select(c => c == null ? "BLANK" : DaxBench.Fmt(c)).ToArray()).ToArray();
            var size = System.Text.Json.JsonSerializer.Serialize(q.ExpectedMatrix).Length;
            return size > 40000 ? "The right answer has too many rows to keep (" + rs.Rows.Length + "). Rank the top few instead." : null;
        }

        // ---- the questions of the next round --------------------------------------------------------------------------------------------

        public Task<CopilotStudentQuestions> GetStudentQuestionsAsync()
        {
            _sessions.Require();
            var (_, bank) = CopilotBank();
            var rounds = CopilotRoundStore.Read(CopilotRoundsFile());
            var last = rounds.Count == 0 ? 0 : rounds.Max(r => r.Number);
            var key = CopilotBankKey(bank);
            // A round that was started and not finished is continued, not left behind: only the numbers it has no answer for are asked again.
            var open = rounds.Count == 0 ? null : rounds.OrderBy(r => r.Number).Last();
            if (open != null && open.Answers.Count < open.Total && string.Equals(open.BankKey, key, StringComparison.Ordinal))
            {
                var answered = new HashSet<int>(open.Answers.Select(a => a.N));
                return Task.FromResult(new CopilotStudentQuestions
                {
                    Round = open.Number, Total = bank.Count, BankKey = key, OpenRoundId = open.Id,
                    Questions = bank.Select((q, i) => new CopilotAsk { N = i + 1, Question = q.Question }).Where(a => !answered.Contains(a.N)).ToArray(),
                    Note = $"Round {open.Number} is not finished: {open.Answers.Count} of {open.Total} are graded. These are the numbers still to answer. Send the answers with roundId {open.Id}.",
                });
            }
            return Task.FromResult(new CopilotStudentQuestions
            {
                Round = last + 1, Total = bank.Count, BankKey = key,
                Questions = bank.Select((q, i) => new CopilotAsk { N = i + 1, Question = q.Question }).ToArray(),
                Note = bank.Count == 0
                    ? "The bank is empty. Write questions and add them with add_interview_questions first."
                    : "Give each student the Copilot view (get_copilot_view) and a batch of these questions, and ask for one DAX query per number. Then send every answer in one call to run_interview_batch with this bankKey.",
            });
        }

        // ---- grade a round ----------------------------------------------------------------------------------------------------------------

        /// <summary>Grade the student's answers by number, in one call. A new round is made when <paramref name="roundId"/> is empty. The call that makes
        /// the round is the one the person is asked about; a call that adds answers to a round already made is not asked again.</summary>
        public async Task<CopilotBatchResult> RunInterviewBatchAsync(string roundId, string bankKey, string student, CopilotAnswerIn[] answers, CopilotRoundChange[] changes, string origin)
        {
            var creating = string.IsNullOrWhiteSpace(roundId);
            // A call for a round that exists adds to the person's own round and is not asked again. Anything else (no id, or an id with no round) is asked about.
            if (!CopilotRoundExists(roundId)) GuardOutsideWrite(origin);
            if (answers == null || answers.Length == 0)
                throw new InvalidOperationException("Pass the student's answers: a list of {n, dax} or {n, cannotAnswer: true}, by the numbers get_student_questions gave.");

            var context = _sessions.CurrentContext;
            var sess = context.Session;
            var liveConn = context.Live;
            var currentName = sess == null ? null : await ReadModelNameAsync(sess).ConfigureAwait(false);
            var (_, bank) = CopilotBank();
            if (bank.Count == 0) throw new InvalidOperationException("The bank is empty. Add questions with add_interview_questions first.");
            var key = CopilotBankKey(bank);
            if (!string.IsNullOrWhiteSpace(bankKey) && !string.Equals(bankKey.Trim(), key, StringComparison.Ordinal))
                throw new InvalidOperationException("The bank changed since the numbers were given, so the answers might be for other questions. Call get_student_questions again and ask the student again.");
            var file = CopilotRoundsFile();
            if (file == null) throw new InvalidOperationException("No project interview store: open or save a model first.");

            var rounds = CopilotRoundStore.Read(file);
            CopilotRoundRecord round;
            if (creating)
            {
                round = new CopilotRoundRecord
                {
                    Id = CopilotRoundStore.NewId(), Number = (rounds.Count == 0 ? 0 : rounds.Max(r => r.Number)) + 1, When = DateTime.UtcNow.ToString("o"),
                    Student = string.IsNullOrWhiteSpace(student) ? "Haiku 5.5" : student.Trim(), BankKey = key, Total = bank.Count,
                    Changes = (changes ?? Array.Empty<CopilotRoundChange>()).ToList(),
                };
                if (!CopilotRoundStore.AddRound(file, round, out var whyRound)) throw new InvalidOperationException("The round was not saved: " + whyRound);
            }
            else
            {
                round = rounds.FirstOrDefault(r => string.Equals(r.Id, roundId.Trim(), StringComparison.Ordinal))
                    ?? throw new InvalidOperationException($"No round has the id '{roundId}'. Leave roundId empty to start a new round, or read the ids with interview_rounds.");
                if (!string.Equals(round.BankKey, key, StringComparison.Ordinal))
                    throw new InvalidOperationException("The bank changed during this round, so more answers cannot be added to it. Start a new round.");
            }

            // Grade, two at a time. A number outside the bank, or asked twice in this call, is listed and not graded.
            var skipped = new List<string>();
            var work = new List<(int N, CopilotAnswerIn A)>();
            var seenN = new HashSet<int>();
            foreach (var a in answers)
            {
                if (a == null) continue;
                if (a.N < 1 || a.N > bank.Count) { skipped.Add($"{a.N}: there is no question with this number (1 to {bank.Count})"); continue; }
                if (!seenN.Add(a.N)) { skipped.Add($"{a.N}: answered twice in this call, the first one was used"); continue; }
                // A number graded once in a round stays graded. Grading it again would let a held-back question be tried with other DAX and its grade read off the score.
                if (round.Answers.Any(x => x.N == a.N)) { skipped.Add($"{a.N}: already graded in this round, so it was not graded again"); continue; }
                work.Add((a.N, a));
            }
            var graded = new CopilotAnswerRecord[work.Count];
            var done = 0;
            using (var gate = new SemaphoreSlim(2))
            {
                await Task.WhenAll(work.Select(async (w, idx) =>
                {
                    await gate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        var q = bank[w.N - 1];
                        var rec = await GradeCopilotAnswerAsync(q, w.N, w.A, sess, liveConn, currentName, origin).ConfigureAwait(false);
                        graded[idx] = rec;
                        var d = Interlocked.Increment(ref done);
                        if (d % 5 == 0 || d == work.Count)
                            _sessions.Bus.PublishProgress(new OperationProgress { OpKey = "copilot_round", RunId = round.Id, Done = d, Total = work.Count, Note = "Round " + round.Number });
                    }
                    finally { gate.Release(); }
                })).ConfigureAwait(false);
            }
            var when = DateTime.UtcNow.ToString("o");
            foreach (var rec in graded)
                if (rec != null && !CopilotRoundStore.AddAnswer(file, round.Id, rec, when, out var why))
                    skipped.Add($"{rec.N}: not saved ({why})");

            // The round is complete when every question has an answer. The misses are then grouped, once.
            var after = CopilotRoundStore.Read(file).First(r => r.Id == round.Id);
            var complete = after.Answers.Count >= after.Total;
            if (complete && after.GroupedBy == null)
            {
                await GroupCopilotMissesAsync(sess, after, bank, file).ConfigureAwait(false);
                after = CopilotRoundStore.Read(file).First(r => r.Id == round.Id);
            }

            await EmitInterviewActivity("run_interview_batch", true, $"Graded {work.Count} answers in round {after.Number}", round.Id, origin).ConfigureAwait(false);
            var all = CopilotRoundStore.Read(file);
            var summary = Summarize(after, all, withMisses: true);
            return new CopilotBatchResult
            {
                RoundId = after.Id, Round = after.Number, Graded = work.Count, Total = after.Total, Answered = after.Answers.Count, Complete = complete,
                Summary = summary, Skipped = skipped.ToArray(),
                Note = complete
                    ? "The round is complete. Read the groups and the practice misses, propose changes, apply them as one undoable step, then run the next round. The held-back score is the one that tells you whether the model got better, not only the bank."
                    : $"{after.Answers.Count} of {after.Total} answered. Send the rest with this roundId.",
            };
        }

        private async Task<CopilotAnswerRecord> GradeCopilotAnswerAsync(InterviewQuestion q, int n, CopilotAnswerIn a, Session capturedSession, LiveConnection capturedLive, string currentName, string origin)
        {
            var saidNo = a.CannotAnswer || string.IsNullOrWhiteSpace(a.Dax);
            var dax = saidNo ? null : CopilotGrader.AsQuery(a.Dax);
            ResultSet rs = null;
            CopilotGrade grade;
            var refusal = PaneReplacementRefusal(q, currentName) ?? ExecutionTargetRefusal(q, capturedLive, currentName);
            if (refusal != null && q.Tier != "refusal")
                grade = new CopilotGrade { Miss = CopilotMiss.CouldNotCheck, Detail = refusal };
            else
            {
                string unstable = null;
                if (!saidNo && q.Tier != "refusal")
                {
                    unstable = CopilotDaxForTest == null ? StabilityRefusal(capturedSession, capturedLive) : null;
                    if (unstable == null) rs = await CopilotRunAsync(dax, origin, capturedLive).ConfigureAwait(false);
                }
                grade = unstable != null ? new CopilotGrade { Miss = CopilotMiss.CouldNotCheck, Detail = unstable } : CopilotGrader.Grade(q, dax, saidNo, rs);
            }
            return new CopilotAnswerRecord
            {
                N = n, Qid = q.Id, Question = q.Question, Kind = q.Kind, Miss = grade.Miss, Held = q.HeldBack,
                StudentDax = saidNo ? null : CopilotGrader.Shorten(dax, 6000), StudentAnswer = saidNo ? "said it cannot answer" : grade.StudentAnswer,
                ReferenceDax = q.Query == null ? null : CopilotGrader.Shorten(q.Query, 6000), ReferenceAnswer = CopilotGrader.ReferenceText(q), Detail = grade.Detail,
            };
        }

        // ---- the rounds read back ---------------------------------------------------------------------------------------------------------

        /// <summary>The rounds with their scores. With <paramref name="round"/> (an id or a number) the practice misses of that round come with it.</summary>
        public Task<CopilotRoundsView> InterviewRoundsAsync(string round)
        {
            _sessions.Require();
            var (_, bank) = CopilotBank();
            var all = CopilotRoundStore.Read(CopilotRoundsFile());
            var detail = string.IsNullOrWhiteSpace(round) ? null
                : all.FirstOrDefault(r => string.Equals(r.Id, round.Trim(), StringComparison.Ordinal) || r.Number.ToString() == round.Trim());
            if (!string.IsNullOrWhiteSpace(round) && detail == null)
                throw new InvalidOperationException($"No round has the id or number '{round}'. Call interview_rounds with no argument to list them.");
            return Task.FromResult(new CopilotRoundsView
            {
                Rounds = all.Select(r => Summarize(r, all, withMisses: detail != null && ReferenceEquals(r, detail))).ToArray(),
                BankSize = bank.Count,
                BankKey = CopilotBankKey(bank),
                HeldBack = bank.Count(q => q.HeldBack),
                BankByKind = CopilotKinds.All.Select(k => new CopilotKindCount { Kind = k, Count = bank.Count(q => q.Kind == k) }).ToArray(),
                Note = all.Count == 0 ? "No round has run yet." : "Scores are right out of what could be checked. The held-back score is the honest one: its questions are never shown, so it cannot be tuned to.",
            });
        }

        private static CopilotScore ScoreOf(IEnumerable<CopilotAnswerRecord> answers)
        {
            var list = answers.ToList();
            var right = list.Count(a => a.Miss == CopilotMiss.Right);
            var unchecked_ = list.Count(a => a.Miss == CopilotMiss.CouldNotCheck);
            var total = list.Count - unchecked_;
            return new CopilotScore { Right = right, Total = total, CouldNotCheck = unchecked_, Share = total == 0 ? (double?)null : Math.Round((double)right / total, 4) };
        }

        internal static CopilotRoundSummary Summarize(CopilotRoundRecord r, IReadOnlyList<CopilotRoundRecord> all, bool withMisses)
        {
            var practice = r.Answers.Where(a => !a.Held).ToList();
            // Until the round is complete, held-back answers stay out of every score: a round sent in parts would otherwise show one held-back grade at a time.
            var complete = r.Answers.Count >= r.Total;
            var held = complete ? r.Answers.Where(a => a.Held).ToList() : new List<CopilotAnswerRecord>();
            var counted = complete ? r.Answers : practice;
            var prev = all.Where(x => x.Number < r.Number).OrderByDescending(x => x.Number).FirstOrDefault();
            var prevByN = prev == null ? new Dictionary<int, CopilotAnswerRecord>() : prev.Answers.ToDictionary(a => a.N);
            var thisByN = r.Answers.ToDictionary(a => a.N);

            int newMisses = 0;
            foreach (var a in practice)
                if (a.Miss != CopilotMiss.Right && a.Miss != CopilotMiss.CouldNotCheck && prevByN.TryGetValue(a.N, out var p) && p.Miss == CopilotMiss.Right) newMisses++;

            var changes = r.Changes.Select(c => new CopilotChangeOut
            {
                Title = c.Title, Where = c.Where, On = c.On, Before = c.Before, After = c.After, ForMisses = c.ForMisses ?? Array.Empty<int>(),
                Fixed = (c.ForMisses ?? Array.Empty<int>()).Count(n => thisByN.TryGetValue(n, out var now) && !now.Held && now.Miss == CopilotMiss.Right),
            }).ToArray();

            var groups = r.Groups.Select(g => new CopilotGroup
            {
                Cause = g.Cause, Fix = g.Fix, Label = g.Label, FixWords = CopilotCause.FixWords(g.Fix), Count = g.Numbers.Length, Numbers = g.Numbers, Objects = g.Objects, By = g.By,
            }).ToArray();
            var causeOf = new Dictionary<int, string>();
            foreach (var g in r.Groups) foreach (var n in g.Numbers) causeOf[n] = g.Label;

            return new CopilotRoundSummary
            {
                Id = r.Id, Number = r.Number, When = r.When, Student = r.Student, Total = r.Total, Answered = r.Answers.Count, Complete = complete,
                Practice = ScoreOf(practice), HeldBack = ScoreOf(held), All = ScoreOf(counted),
                // Hero 26 N4: a kind's score is the practice questions only. A held-back question inside it would let the assistant work out which held-back one was missed.
                ByKind = CopilotKinds.All.Select(k => { var of = practice.Where(a => a.Kind == k && a.Miss != CopilotMiss.CouldNotCheck).ToList(); return new CopilotKindScore { Kind = k, Right = of.Count(a => a.Miss == CopilotMiss.Right), Total = of.Count }; })
                    .Where(k => k.Total > 0).ToArray(),
                MissCounts = CopilotMiss.Misses.ToDictionary(k => k, k => practice.Count(a => a.Miss == k)),
                NewMisses = newMisses, Changes = changes, Groups = groups,
                Misses = !withMisses ? Array.Empty<CopilotMissOut>() : practice.Where(a => CopilotMiss.Misses.Contains(a.Miss)).Select(a => new CopilotMissOut
                {
                    N = a.N, Question = a.Question, Kind = a.Kind, Miss = a.Miss, MissWords = CopilotMiss.Words(a.Miss), StudentDax = a.StudentDax, StudentAnswer = a.StudentAnswer,
                    ReferenceDax = a.ReferenceDax, ReferenceAnswer = a.ReferenceAnswer, Detail = a.Detail, Cause = causeOf.TryGetValue(a.N, out var c) ? c : null,
                }).ToArray(),
            };
        }
    }
}
