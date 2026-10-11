---
name: semanticus-interview-model
description: Check business questions against a Semanticus model using known answers, paraphrases and questions it should decline. Use when the user asks for a model interview, a Copilot rehearsal, or a deeper check of answer reliability.
---

# Interview a model

This is your own check. Studio does not show interviews, so report the results in the chat. For a
quick check of one number, `ask_data` or `save_test` is shorter. Use an interview when the user wants
many questions asked in several ways.

Orient with `get_model_summary` and `get_model_primer`. `list_interview_questions` has the open
model's saved questions and `list_interview_seeds` has candidates that fit its schema. Seeds are not
trusted answers, and questions from another model prove nothing about this one.

Choose questions that reflect the user's business and likely calculation mistakes: distinct counts,
totals, shares, closing balances, inactive relationships and date boundaries. Take the expected answer
from a known report, an approved figure or an independent source. A number from the query being
checked does not validate that same query.

- Value questions compare against an independently known value.
- Paraphrase questions check that two ways of asking agree.
- Unanswerable questions check that the model declines honestly.

Run `run_interview` with a saved `questionId` or an `inlineJson` question. Value and paraphrase checks
need the intended live connection. An inline question with no expected answer stays Unverified. Save a
reusable question with `add_interview_question` and keep where its answer came from. For an
unanswerable question, set `abstained` when declining or give `attemptDax` for the attempt.

Report Correct, Refused, SilentlyWrong and Unverified as returned. Errors, truncation and missing
expected answers are not passes. Follow the readiness fix hints, then rerun the affected questions.
