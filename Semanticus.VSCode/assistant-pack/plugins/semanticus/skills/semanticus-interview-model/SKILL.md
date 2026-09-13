---
name: semanticus-interview-model
description: Test business questions against a Semanticus model using known answers, paraphrases and questions it should decline. Use for model interviews, Copilot rehearsals or checks of answer reliability.
---

# Interview a model

Orient with `get_model_summary` and `get_model_primer`. Use `list_interview_questions` for the
open model's saved questions and `list_interview_seeds` for candidates that fit its schema.
Seeds are not trusted answers. Questions from another model are not evidence for this one.

Choose questions that reflect the user's business and likely calculation mistakes: distinct
counts, totals, shares, closing balances, inactive relationships and date boundaries.
Use a known report, approved figure or independently checked source as the expected answer.
A number from the query under test does not independently validate that same query.

- Value questions compare against an independently known value.
- Paraphrase questions check agreement between two ways of asking the same question.
- Refusal questions check whether an unanswerable question is honestly declined.

Run `run_interview` with a saved `questionId` or an `inlineJson` question. Value and paraphrase
checks need the intended live connection. An inline question without an oracle can explore an
answer, but remains Unverified until an independent answer is supplied. Save reusable questions
with `add_interview_question` when requested, retaining the source of the expected answer.

For refusal questions, set `abstained` when declining or provide `attemptDax` for the attempt.
Report Correct, Refused, SilentlyWrong and Unverified as returned. Errors, truncation and missing
oracles are not passes. Investigate returned readiness fix hints and rerun affected questions
after a requested repair.
