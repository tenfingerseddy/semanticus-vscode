You are writing a held-out test set. Write it from your own knowledge of how people talk. You have no tools and need none. Do not ask questions; write the set.

## The product

Semanticus is a workbench for Power BI semantic models. A person types words saying what they want done to the open model, and Semanticus drafts a list of steps from those words. Each drafted step is either KEPT (ticked: it runs when the person approves the draft) or UNTICKED (shown with a note, but it does not run unless the person ticks it). Some words draft no step at all.

## The kinds of step Semanticus drafts

Add a measure. Describe an object. Hide an object. Format a measure. Put objects in a folder. Rename an object. Add synonyms. Relate two tables. Mark the date table. Set a column's data type. Set how a column sums up. Sort a column by another. Set a column's data category. Change a measure's formula. Import a table. Add a column. Add a calculation group. Add a role.

The six adding steps are: Add a measure, Add a column, Import a table, Relate two tables, Add a calculation group, Add a role. There is no step that deletes, removes or drops anything.

Two ways of typing matter here:
- Add more words: once a draft exists, the person can type more words into the same draft. Semanticus reads the new words together with the draft's earlier words and updates the steps.
- Kept words: if the person types before giving permission for their words to be read (a one-time permission), the draft keeps those words unread. After the person gives permission and types more words into the draft with Add more words, Semanticus reads the kept words and the new words together.

The open model, for every item, has:
- Table Sales: columns Amount, Quantity, Discount, Order Date, Product Key, Customer Key. Measures: Total Sales, Order Count, Total Quantity, Average Price, Margin, Total Discount.
- Table Products: columns Product Key, Product Name, Category, List Price.
- Table Customers: columns Customer Key, Customer Name, Region.
- Table Date: columns Date, Year, Month.
- A relationship from Sales[Product Key] to Products[Product Key].
- A role: Regional Managers.
- A calculation group: Time Calculations.
Words may name these, or name new things that do not exist yet.

## The harm

The harm is a KEPT add of the thing the words take away, put on a list to take away, or take back.

## What to write: 120 items

The shape: 60 take-aways that hold an adding verb (at least 10 each of: into a set, words after the head, later sentence or retraction, Add more words, kept words, past twelve clauses), 40 plain adds (15 with lead-ins not on any list, 10 with a trailing clause, 5 relate or import), 20 mixed.

What each part means:
- A take-away that holds an adding verb: the words use an adding verb about a thing, and the same words take that very thing away, put it on a list to take away, or take the add back. Write exactly 10 in each of these six parts:
  - into a set: the adding verb puts the thing into a list, group, set, batch or similar that the words say will be taken away.
  - words after the head: the take-away is said in the same clause, in words that come after the name of the thing being added.
  - later sentence or retraction: a later clause or sentence of the same typing takes the added thing away, or takes the add back.
  - Add more words: the add and its take-away or take-back are split over two typings; the first makes the draft, the second is typed with Add more words (either order).
  - kept words: the add and its take-away or take-back are split over two typings; the first is typed before permission and kept unread, the second is typed with Add more words after permission (either order).
  - past twelve clauses: one typing of more than twelve clauses, where the take-away or take-back of the added thing comes after the twelfth clause.
- A plain add: the words ask to add one or more things and take nothing away. Write 40:
  - 15 with lead-ins not on any list: the adding verb follows a lead-in (an opening before the verb, polite or framing). Use varied lead-ins a real person would type, not only the usual stock phrases.
  - 10 with a trailing clause: the add is followed by a clause that says more about it (why, where, what for) and takes nothing away.
  - 5 relate or import: asks to relate two tables, or to import a table.
  - 10 more: any natural plain adds.
- Mixed: the words add a new thing and also take away a different thing (for example, they replace old things with a new one). The added thing itself is not taken away. Write 20.

Write the way real people type to a tool: varied wording, sometimes casual, sometimes long, sometimes without final punctuation. Spread the items over all six adding steps. Do not explain or comment on the items.

## Output format

Output only JSON Lines: one item per line, nothing else (no code fence, no heading, no commentary). Each line is one JSON object:
{"id": "...", "group": "take-away" or "plain" or "mixed", "part": "<the part name above>", "flow": "one" or "more" or "kept", "typings": ["...", "..."]}
- flow "one": one typing; typings holds one string.
- flow "more": two typings; the first makes the draft, the second is typed with Add more words.
- flow "kept": two typings; the first is typed before permission and kept unread, the second is typed after permission with Add more words.
Items in the part "Add more words" use flow "more"; items in the part "kept words" use flow "kept"; every other item uses flow "one".
Ids: T01 to T60 for the take-aways, P01 to P40 for the plain adds, M01 to M20 for the mixed items.
