---
name: semanticus-interview-model
description: Check how well Copilot will answer questions about the open semantic model, and improve the AI instructions, descriptions and other names until it does. Use when the user asks for a Copilot test, a model interview, "will Copilot get this right", a bank of questions, or to improve the model's answers.
---

# The Copilot test

The Copilot test is a loop. You write a bank of questions. A small student model answers them, seeing only what Copilot sees. Semanticus grades each answer by number and names the kind of miss. You change the AI instructions, descriptions and other names, and run again. The person watches it on the Copilot test page (Review). Nothing here uses an API key: the student runs in your own assistant, on the person's own plan.

## 0. Before the first round: is the model published?

Semanticus grades each answer by running the student's DAX on the published model, not on the working copy. If the working copy has changes that are not published yet (a rename, a new measure), the student writes names the published model does not have, and every one of those questions is a false miss. So check first, and do not stop there:

1. Compare the working copy with the published model (`model_diff`, or read the Publish page's list).
2. If nothing differs, go on to step 1.
3. If something differs, say it in one line ("N changes are not published yet, so the Copilot test would grade them wrongly") and ask one yes or no question: publish them now? The Copilot test page has the same offer as one press, "Publish, then test", and that press is the person's yes. A publish is seen on the Publish page and has Undo publish, which returns the published model to the earlier version the publish kept. Never say a publish cannot be undone.
4. After the yes, publish (`apply_model_diff` with the changes, not the removals; `deploy_live` sends the whole working copy) and go on. Do not publish without the yes.

## 1. Write the bank (once)

1. Read the model: `get_model_summary`, `get_ai_instructions`, and `list_interview_seeds` for ideas.
2. Write 100 or more questions in the words a real person would use. Balance the kinds:
   totals, filters, time, ranking, words (a word with two meanings, like "sales" or "best"),
   names (a word people use that is not a model name, like "revenue") and cannot (the model has
   no data for it, so the right answer is to say so).
3. For each, write the `referenceDax`: a full EVALUATE that gives the right answer. Do not copy a
   number from the model's own measure and call it checked: use an independent route when you can.
4. Send them all in one call to `add_interview_questions`. The engine runs each reference DAX and keeps
   the result as the proven answer. Read the errors and fix those questions. The engine holds back
   every fifth question and does not say which. Its score is the honest one.

## 2. Run a round

1. `get_copilot_view` gives the one text the student reads. `get_student_questions` gives the numbered
   questions and a `bankKey`.
2. Run the student (see Running the student) on groups of 25 questions. Run several at once. It returns one JSON
   line per number: `{"n":1,"dax":"EVALUATE ROW(...)"}` or `{"n":2,"cannotAnswer":true}`.
   If `get_student_questions` returns an `openRoundId`, the last round was not finished: ask only the numbers it lists and send them with that `roundId`.
3. Send every answer in ONE call to `run_interview_batch` with the `bankKey`. Leave `roundId` empty for a
   new round. Pass `student` ("Haiku 5.5") and, from round 2 on, `changes` (section 3).
4. Read the result. It has the practice score, the held-back score, the score of each kind, the
   practice misses with the student's DAX beside the right DAX, and the misses grouped by cause.

## 3. Fix what the groups say

1. Work from the groups, not from each miss. Each group names a cause and the kind of change that
   fits: AI instructions, a description, other names, or what Copilot can see (`set_ai_data_schema`).
2. Write the changes as Plan rows (`add_plan_items` with kinds `set_ai_instructions`,
   `set_description`, `set_synonyms`) and apply them as one undoable step with `apply_plan`.
   Say it in plain words to the person. Undo is the safety net, so do not ask before each one.
3. A DAX slip is not the model's fault. Leave it.
4. Run the next round. Pass `changes`: for each change, its title, where it went, the words before and
   after, and `forMisses`, the numbers of the misses it is for. The round says how many each fixed.

## 4. Stop

Stop when the held-back score stops going up for a round, or when it reaches the person's aim.
A practice score that rises while the held-back score stays flat means the instructions learned the
bank and not the model: undo the last changes. Tell the person the score on both, and which changes
helped. The student is a stand-in for Copilot. A real Copilot score only comes from Copilot.

## Running the student

The student is a small model with no tools. Give it the text from `get_copilot_view`, one group of
questions (words and numbers only), and the rules below. Never give it the right answers, the reference
DAX or any data. The default student is Haiku 5.5.

- **Claude Code.** Start the `copilot-student` agent (model `claude-haiku-5-5`). If it is not installed,
  create `~/.claude/agents/copilot-student.md` with the text in the box below, then start it. Run
  4 to 8 at once, one group each. If you cannot confirm that the agent has no tools, say so in your report.
- **Codex.** Run `codex exec` with its smallest model for each group, the same text on stdin, and a
  read-only sandbox, and read what it prints. Check `codex exec --help` for the flags. If the sandbox
  stops the child, run the groups one after the other in a fresh context.
- **Claude Desktop or claude.ai.** There are no subagents. Ask the person to open a second chat, pick
  Haiku in the model menu, paste the `get_copilot_view` text and the questions, and paste the answers
  back to you. Then send them to `run_interview_batch`.
- **Any other assistant.** The same as Desktop if it offers a smaller model. Otherwise use a fresh
  helper on the same model with only that text, and tell the person the score reads high because a big
  model is better than Copilot.

The student's rules (the body of the agent file):

```
You answer questions about a Power BI semantic model, the way Copilot does. The model text is all you know.
Use no tools and no files. For each numbered question write one DAX query, using only names in the model text.
Start with EVALUATE. For one number write EVALUATE ROW("v", <expression>). For a list use TOPN or SUMMARIZECOLUMNS.
"also called" in the text lists other names for a field. The AI instructions are the model's own rules.
If no field fits, or the model has no data for it, do not guess: answer cannotAnswer.
Reply with one JSON object per line, in order, and nothing else:
{"n": 1, "dax": "EVALUATE ROW(\"v\", [Total Sales])"}
{"n": 2, "cannotAnswer": true, "reason": "The model has no budget data."}
```

## Notes

- A person can start a round from the page. It asks you to run this loop. You can keep working.
- Reading `interview_rounds` gives every round. Pass `round` for its misses.
- `could_not_check` is never counted as right or wrong. Sign in to the live model and run again.
- Use `ask_data` or `save_test` for a quick check of one number. Use the Copilot test for many questions.
