// The Plan page's engine, played in the browser and in the node test (lane U1).
//
// SHAPE is docs/redesign/contracts/plan-item.json, E1's frozen shape file, copied in whole: twelve rows, one per row of the
// mockup's sample, with the strip and the switch. test/plan-heart.test.mjs fails if the two ever differ, so this fixture
// cannot drift from the wire. QUEUE is tools/uishot/fixtures/plan-heart-queue.json, the assistant's track of mockup
// section 5, which Semanticus.Tests/PlanHeartFoldParityTests.cs runs through the engine's own fold line.
//
// The functions below are a MOCK of the engine's person controls, pure and small: they move a row's state word the way
// the engine's compose would for the few cases the driver presses, and recount the strip from the rows. They are not
// the engine and do not pretend to be; they exist so a click in the driver lands on a plan the engine could have sent.
// This file is inlined into the browser seed as text (cp-plan-drive.mjs), so it may not import anything.

export const HEART_SHAPE = {
 "assistantQueue": "on",
 "strip": {
  "gradeNow": "B",
  "gradeAfter": "A",
  "bestPractice": "C",
  "bestPracticeWhy": "Best practice C: 4 open errors and 9 warnings across 190 objects checked, 0.20 weighted per object (C is over 0.10 up to 0.25). A needs no open errors or warnings.",
  "tracks": [
   {
    "doer": "semanticus",
    "done": 5,
    "onIt": 1,
    "ready": 3,
    "needsYou": 0,
    "waiting": 1
   },
   {
    "doer": "you",
    "done": 2,
    "onIt": 0,
    "ready": 0,
    "needsYou": 2,
    "waiting": 0
   },
   {
    "doer": "assistant",
    "done": 4,
    "onIt": 1,
    "ready": 2,
    "needsYou": 0,
    "waiting": 2
   }
  ],
  "latest": {
   "entryId": "le-2c9d10f4",
   "when": "2026-09-24T10:46:00+00:00",
   "by": "semanticus",
   "sentence": "Semanticus hid 3 Promotion key columns"
  },
  "updated": {
   "when": "2026-09-24T10:46:00+00:00",
   "by": "jev"
  }
 },
 "rows": [
  {
   "id": "jev.measures.table:Sales",
   "section": "Numbers",
   "title": "Pick the default summary for 6 number columns",
   "sub": "Sales: Quantity, Unit Price and 4 more",
   "value": "",
   "who": "jev",
   "status": "proposed",
   "optional": false,
   "confidence": 0,
   "why": [],
   "diff": [],
   "touches": "6 columns in Sales",
   "tables": [],
   "jump": {
    "tool": "advmodels",
    "label": "Advanced modelling"
   },
   "skipReason": null,
   "skippedUtc": null,
   "state": "gap",
   "changeSet": null,
   "source": "jev",
   "goal": "ai",
   "goals": ["ai"],
   "rules": [],
   "sourcePage": null,
   "doer": "you",
   "stateWord": "needs_you",
   "stateNote": "A choice only you can make.",
   "doneWhen": "model_check",
   "doneText": "Semanticus checks the model: every one has a summary.",
   "objects": [
    "column:Sales/Quantity",
    "column:Sales/Unit Price"
   ],
   "reads": [],
   "writes": [],
   "footprintKnown": false,
   "waitsFor": [],
   "waitsForCount": null,
   "held": false,
   "order": null,
   "note": null,
   "reply": null,
   "fullText": null,
   "claim": null,
   "originLink": null,
   "offerNumber": null,
   "events": []
  },
  {
   "id": "pt-3f9a2c11",
   "section": "Structure",
   "title": "Choose when the fiscal year starts",
   "sub": "Date: your assistant asked, one word needed",
   "value": "",
   "who": "you",
   "status": "ready",
   "optional": false,
   "confidence": 0,
   "why": [],
   "diff": [],
   "touches": "",
   "tables": [],
   "jump": {
    "tool": "advmodels",
    "label": "Advanced modelling"
   },
   "skipReason": null,
   "skippedUtc": null,
   "state": "gap",
   "changeSet": null,
   "source": "words",
   "goal": "aims",
   "goals": ["aims"],
   "rules": [],
   "sourcePage": null,
   "doer": "you",
   "stateWord": "needs_you",
   "stateNote": "Your assistant could not: it needs the month the fiscal year starts.",
   "doneWhen": "you_mark",
   "doneText": "You mark it done.",
   "objects": [
    "table:Date"
   ],
   "reads": [],
   "writes": [],
   "footprintKnown": false,
   "waitsFor": [],
   "waitsForCount": null,
   "held": false,
   "order": null,
   "note": null,
   "reply": null,
   "fullText": null,
   "claim": null,
   "originLink": {
    "kind": "words",
    "ref": null
   },
   "offerNumber": null,
   "events": []
  },
  {
   "id": "pt-7c01d4e9",
   "section": "Ship",
   "title": "Monthly tidy",
   "sub": "Workflow run: step 3 of 7",
   "value": "",
   "who": "auto",
   "status": "ready",
   "optional": false,
   "confidence": 0,
   "why": [],
   "diff": [],
   "touches": "",
   "tables": [],
   "jump": {
    "tool": "workflows",
    "label": "Workflows"
   },
   "skipReason": null,
   "skippedUtc": null,
   "state": "gap",
   "changeSet": null,
   "source": "repeat",
   "goal": "aims",
   "goals": ["aims"],
   "rules": [],
   "sourcePage": null,
   "doer": "semanticus",
   "stateWord": "on_it",
   "stateNote": "step 3 of 7",
   "doneWhen": "model_check",
   "doneText": "Semanticus checks the model when the run finishes.",
   "objects": [],
   "reads": [],
   "writes": [],
   "footprintKnown": false,
   "waitsFor": [],
   "waitsForCount": null,
   "held": false,
   "order": null,
   "note": null,
   "reply": null,
   "fullText": null,
   "claim": null,
   "originLink": {
    "kind": "run",
    "ref": "wfr-5b2e90aa"
   },
   "offerNumber": null,
   "events": []
  },
  {
   "id": "pt-a41b6f02",
   "section": "Structure",
   "title": "Add a Store geography hierarchy",
   "sub": "Store: Country, State, City",
   "value": "",
   "who": "asst",
   "status": "ready",
   "optional": false,
   "confidence": 0,
   "why": [],
   "diff": [],
   "touches": "",
   "tables": [],
   "jump": {
    "tool": "diagram",
    "label": "Diagram"
   },
   "skipReason": null,
   "skippedUtc": null,
   "state": "gap",
   "changeSet": null,
   "source": "you",
   "goal": "aims",
   "goals": ["aims"],
   "rules": [],
   "sourcePage": "diagram",
   "doer": "assistant",
   "stateWord": "on_it",
   "stateNote": "since its last call",
   "doneWhen": "model_check",
   "doneText": "Semanticus checks the model: the hierarchy exists.",
   "objects": [
    "column:Store/Country",
    "column:Store/State",
    "column:Store/City"
   ],
   "reads": [],
   "writes": [],
   "footprintKnown": false,
   "waitsFor": [],
   "waitsForCount": null,
   "held": false,
   "order": 1,
   "note": null,
   "reply": null,
   "fullText": null,
   "claim": {
    "claimedBy": "Claude Code",
    "since": "2026-09-24T10:41:00+00:00"
   },
   "originLink": {
    "kind": "page",
    "ref": "diagram"
   },
   "offerNumber": null,
   "events": []
  },
  {
   "id": "jev.describe.table:Product",
   "section": "Meaning",
   "title": "Write descriptions for 12 Product columns",
   "sub": "Product: Brand, Colour and 10 more",
   "value": "",
   "who": "jev",
   "status": "proposed",
   "optional": false,
   "confidence": 0,
   "why": [],
   "diff": [],
   "touches": "",
   "tables": [],
   "jump": {
    "tool": "readiness",
    "label": "AI readiness"
   },
   "skipReason": null,
   "skippedUtc": null,
   "state": "gap",
   "changeSet": null,
   "source": "jev",
   "goal": "ai",
   "goals": ["ai"],
   "rules": [],
   "sourcePage": null,
   "doer": "assistant",
   "stateWord": "ready",
   "stateNote": null,
   "doneWhen": "model_check",
   "doneText": "Semanticus checks the model: all 12 have words.",
   "objects": [
    "column:Product/Brand",
    "column:Product/Colour"
   ],
   "reads": [],
   "writes": [],
   "footprintKnown": false,
   "waitsFor": [],
   "waitsForCount": null,
   "held": false,
   "order": null,
   "note": null,
   "reply": null,
   "fullText": null,
   "claim": null,
   "originLink": null,
   "offerNumber": null,
   "events": []
  },
  {
   "id": "safefix/Visibility",
   "section": "Structure",
   "title": "Hide 9 key columns",
   "sub": "Sales: CustomerKey, ProductKey and 7 more",
   "value": "",
   "who": "auto",
   "status": "ready",
   "optional": false,
   "confidence": 0,
   "why": [],
   "diff": [
    "~ Sales[CustomerKey]: hide",
    "~ Sales[ProductKey]: hide"
   ],
   "touches": "9 columns in Sales",
   "tables": [],
   "jump": {
    "tool": "bpa",
    "label": "Model quality (BPA)"
   },
   "skipReason": null,
   "skippedUtc": null,
   "state": "gap",
   "changeSet": null,
   "source": "found",
   "goal": "ai",
   "goals": ["ai"],
   "rules": [],
   "sourcePage": null,
   "doer": "semanticus",
   "stateWord": "ready",
   "stateNote": null,
   "doneWhen": "model_check",
   "doneText": "Semanticus checks the model.",
   "objects": [
    "column:Sales/CustomerKey",
    "column:Sales/ProductKey"
   ],
   "reads": [],
   "writes": [],
   "footprintKnown": false,
   "waitsFor": [],
   "waitsForCount": null,
   "held": false,
   "order": null,
   "note": null,
   "reply": null,
   "fullText": null,
   "claim": null,
   "originLink": null,
   "offerNumber": null,
   "events": []
  },
  {
   "id": "datetable/table:Date",
   "section": "Structure",
   "title": "Mark Date as the date table",
   "sub": "Date: key column Date[Date]",
   "value": "",
   "who": "auto",
   "status": "ready",
   "optional": false,
   "confidence": 0,
   "why": [],
   "diff": [],
   "touches": "",
   "tables": [],
   "jump": {
    "tool": "advmodels",
    "label": "Advanced modelling"
   },
   "skipReason": null,
   "skippedUtc": null,
   "state": "gap",
   "changeSet": null,
   "source": "found",
   "goal": "ai",
   "goals": ["ai"],
   "rules": [],
   "sourcePage": null,
   "doer": "semanticus",
   "stateWord": "ready",
   "stateNote": null,
   "doneWhen": "model_check",
   "doneText": "Semanticus checks the model.",
   "objects": [
    "table:Date"
   ],
   "reads": [],
   "writes": [],
   "footprintKnown": false,
   "waitsFor": [],
   "waitsForCount": null,
   "held": false,
   "order": null,
   "note": null,
   "reply": null,
   "fullText": null,
   "claim": null,
   "originLink": null,
   "offerNumber": null,
   "events": []
  },
  {
   "id": "timeintel/measure:Sales/Sales Amount",
   "section": "Numbers",
   "title": "Add year to date and last year measures",
   "sub": "Waits for: Mark Date as the date table",
   "value": "",
   "who": "auto",
   "status": "ready",
   "optional": false,
   "confidence": 0,
   "why": [],
   "diff": [],
   "touches": "",
   "tables": [],
   "jump": {
    "tool": "advmodels",
    "label": "Advanced modelling"
   },
   "skipReason": null,
   "skippedUtc": null,
   "state": "gap",
   "changeSet": null,
   "source": "found",
   "goal": "ai",
   "goals": ["ai"],
   "rules": [],
   "sourcePage": null,
   "doer": "semanticus",
   "stateWord": "waiting",
   "stateNote": null,
   "doneWhen": "model_check",
   "doneText": "Semanticus checks the model.",
   "objects": [
    "measure:Sales/Sales Amount"
   ],
   "reads": [],
   "writes": [],
   "footprintKnown": false,
   "waitsFor": [
    {
     "id": "datetable/table:Date",
     "title": "Mark Date as the date table"
    }
   ],
   "waitsForCount": null,
   "held": false,
   "order": null,
   "note": null,
   "reply": null,
   "fullText": null,
   "claim": null,
   "originLink": null,
   "offerNumber": null,
   "events": []
  },
  {
   "id": "pt-0d6e33b8",
   "section": "Meaning",
   "title": "Add synonyms for Customer",
   "sub": "Customer: from your aim: easy to ask about customers",
   "value": "",
   "who": "asst",
   "status": "ready",
   "optional": false,
   "confidence": 0,
   "why": [],
   "diff": [],
   "touches": "",
   "tables": [],
   "jump": {
    "tool": "instructions",
    "label": "AI instructions"
   },
   "skipReason": null,
   "skippedUtc": null,
   "state": "gap",
   "changeSet": null,
   "source": "words",
   "goal": "aims",
   "goals": ["aims"],
   "rules": [],
   "sourcePage": null,
   "doer": "assistant",
   "stateWord": "paused",
   "stateNote": null,
   "doneWhen": "assistant_says",
   "doneText": "Your assistant says it is done.",
   "objects": [
    "table:Customer"
   ],
   "reads": [],
   "writes": [],
   "footprintKnown": false,
   "waitsFor": [],
   "waitsForCount": null,
   "held": true,
   "order": 2,
   "note": null,
   "reply": null,
   "fullText": null,
   "claim": null,
   "originLink": {
    "kind": "words",
    "ref": null
   },
   "offerNumber": null,
   "events": []
  },
  {
   "id": "cleanup/unused-measures",
   "section": "Numbers",
   "title": "Remove 2 unused measures",
   "sub": "Measures: Old Margin, Test Total",
   "value": "",
   "who": "auto",
   "status": "ready",
   "optional": false,
   "confidence": 0,
   "why": [],
   "diff": [
    "- Old Margin",
    "- Test Total"
   ],
   "touches": "",
   "tables": [],
   "jump": {
    "tool": "bpa",
    "label": "Model quality (BPA)"
   },
   "skipReason": null,
   "skippedUtc": null,
   "state": "gap",
   "changeSet": null,
   "source": "found",
   "goal": "quality",
   "goals": ["quality"],
   "rules": [],
   "sourcePage": null,
   "doer": "semanticus",
   "stateWord": "ready",
   "stateNote": null,
   "doneWhen": "model_check",
   "doneText": "Semanticus checks the model.",
   "objects": [
    "measure:Measures/Old Margin",
    "measure:Measures/Test Total"
   ],
   "reads": [],
   "writes": [],
   "footprintKnown": false,
   "waitsFor": [],
   "waitsForCount": null,
   "held": false,
   "order": null,
   "note": null,
   "reply": null,
   "fullText": null,
   "claim": null,
   "originLink": null,
   "offerNumber": null,
   "events": []
  },
  {
   "id": "pt-9e2f7a10",
   "section": "Meaning",
   "title": "Describe 8 Product measures",
   "sub": "Product: Units Sold and 7 more",
   "value": "",
   "who": "asst",
   "status": "ready",
   "optional": false,
   "confidence": 0,
   "why": [],
   "diff": [],
   "touches": "",
   "tables": [],
   "jump": {
    "tool": "readiness",
    "label": "AI readiness"
   },
   "skipReason": null,
   "skippedUtc": null,
   "state": "gap",
   "changeSet": null,
   "source": "you",
   "goal": "aims",
   "goals": ["aims"],
   "rules": [],
   "sourcePage": "readiness",
   "doer": "assistant",
   "stateWord": "ready",
   "stateNote": "on its next call",
   "doneWhen": "has_words",
   "doneText": "Semanticus checks the model: all 8 have words.",
   "objects": [
    "measure:Product/Units Sold"
   ],
   "reads": [],
   "writes": [],
   "footprintKnown": false,
   "waitsFor": [],
   "waitsForCount": null,
   "held": false,
   "order": 3,
   "note": "Keep each one to a sentence.",
   "reply": null,
   "fullText": null,
   "claim": null,
   "originLink": {
    "kind": "finding",
    "ref": "DESCRIBE_MEASURES|measure:Product/Units Sold"
   },
   "offerNumber": 1,
   "events": []
  },
  {
   "id": "jev.describe.table:Measures",
   "section": "Meaning",
   "title": "Describe the 2 new time measures",
   "sub": "Waits for: Add year to date and last year measures",
   "value": "",
   "who": "jev",
   "status": "proposed",
   "optional": false,
   "confidence": 0,
   "why": [],
   "diff": [],
   "touches": "",
   "tables": [],
   "jump": {
    "tool": "readiness",
    "label": "AI readiness"
   },
   "skipReason": null,
   "skippedUtc": null,
   "state": "gap",
   "changeSet": null,
   "source": "jev",
   "goal": "ai",
   "goals": ["ai"],
   "rules": [],
   "sourcePage": null,
   "doer": "assistant",
   "stateWord": "waiting",
   "stateNote": null,
   "doneWhen": "model_check",
   "doneText": "Semanticus checks the model: both have words.",
   "objects": [
    "measure:Measures/YTD Sales",
    "measure:Measures/Sales LY"
   ],
   "reads": [],
   "writes": [],
   "footprintKnown": false,
   "waitsFor": [
    {
     "id": "timeintel/measure:Sales/Sales Amount",
     "title": "Add year to date and last year measures"
    }
   ],
   "waitsForCount": null,
   "held": false,
   "order": null,
   "note": null,
   "reply": null,
   "fullText": null,
   "claim": null,
   "originLink": null,
   "offerNumber": null,
   "events": []
  }
 ]
};

export const HEART_QUEUE = {
 "assistantQueue": "on",
 "items": [
  {
   "section": "Structure",
   "value": "",
   "who": "asst",
   "status": "ready",
   "optional": false,
   "confidence": 0,
   "why": [],
   "diff": [],
   "touches": "",
   "tables": [],
   "skipReason": null,
   "skippedUtc": null,
   "state": "gap",
   "changeSet": null,
   "source": "you",
   "sourcePage": "diagram",
   "doer": "assistant",
   "stateNote": "since its last call",
   "doneWhen": "model_check",
   "doneText": null,
   "objects": [
    "table:Store"
   ],
   "reads": [],
   "writes": [
    "table:Store"
   ],
   "footprintKnown": true,
   "waitsFor": [],
   "held": false,
   "order": 1,
   "note": null,
   "reply": null,
   "fullText": null,
   "claim": {
    "claimedBy": "Claude Code",
    "since": "2026-09-24T10:41:00+00:00"
   },
   "originLink": null,
   "events": [],
   "id": "pt-a41b6f02",
   "title": "Add a Store geography hierarchy",
   "sub": "Store: Country, State, City",
   "stateWord": "on_it",
   "jump": {
    "tool": "diagram",
    "label": "Diagram"
   }
  },
  {
   "section": "Meaning",
   "value": "",
   "who": "asst",
   "status": "ready",
   "optional": false,
   "confidence": 0,
   "why": [],
   "diff": [],
   "touches": "",
   "tables": [],
   "skipReason": null,
   "skippedUtc": null,
   "state": "gap",
   "changeSet": null,
   "source": "jev",
   "sourcePage": null,
   "doer": "assistant",
   "stateNote": "on its next call",
   "doneWhen": "has_words",
   "doneText": "Semanticus checks the model: all 12 have words.",
   "objects": [
    "column:Product/Brand",
    "column:Product/Colour"
   ],
   "reads": [
    "column:Product/Brand",
    "column:Product/Colour"
   ],
   "writes": [
    "column:Product/Brand",
    "column:Product/Colour"
   ],
   "footprintKnown": true,
   "waitsFor": [],
   "held": false,
   "order": 2,
   "note": "Keep each under 20 words. Use the words the sales team uses.",
   "reply": null,
   "fullText": null,
   "claim": null,
   "originLink": null,
   "events": [],
   "id": "pt-3b7d0c21",
   "title": "Write descriptions for 12 Product columns",
   "sub": "Product: Brand, Colour and 10 more",
   "stateWord": "ready",
   "jump": {
    "tool": "readiness",
    "label": "AI readiness"
   }
  },
  {
   "section": "Meaning",
   "value": "",
   "who": "asst",
   "status": "ready",
   "optional": false,
   "confidence": 0,
   "why": [],
   "diff": [],
   "touches": "",
   "tables": [],
   "skipReason": null,
   "skippedUtc": null,
   "state": "gap",
   "changeSet": null,
   "source": "you",
   "sourcePage": "readiness",
   "doer": "assistant",
   "stateNote": "on its next call",
   "doneWhen": "has_words",
   "doneText": null,
   "objects": [
    "measure:Product/Units Sold"
   ],
   "reads": [
    "measure:Product/Units Sold"
   ],
   "writes": [
    "measure:Product/Units Sold"
   ],
   "footprintKnown": true,
   "waitsFor": [],
   "held": false,
   "order": 3,
   "note": "Keep each one to a sentence.",
   "reply": null,
   "fullText": null,
   "claim": null,
   "originLink": null,
   "events": [],
   "id": "pt-9e2f7a10",
   "title": "Describe 8 Product measures",
   "sub": "Product: Units Sold and 7 more",
   "stateWord": "ready",
   "jump": {
    "tool": "readiness",
    "label": "AI readiness"
   }
  },
  {
   "section": "Meaning",
   "value": "",
   "who": "asst",
   "status": "ready",
   "optional": false,
   "confidence": 0,
   "why": [],
   "diff": [],
   "touches": "",
   "tables": [],
   "skipReason": null,
   "skippedUtc": null,
   "state": "gap",
   "changeSet": null,
   "source": "words",
   "sourcePage": null,
   "doer": "assistant",
   "stateNote": null,
   "doneWhen": "has_words",
   "doneText": null,
   "objects": [
    "table:Customer"
   ],
   "reads": [],
   "writes": [
    "table:Customer"
   ],
   "footprintKnown": false,
   "waitsFor": [],
   "held": true,
   "order": 4,
   "note": null,
   "reply": null,
   "fullText": null,
   "claim": null,
   "originLink": null,
   "events": [],
   "id": "pt-0d6e33b8",
   "title": "Add synonyms for Customer",
   "sub": "Customer: from your aim: easy to ask about customers",
   "stateWord": "paused",
   "jump": {
    "tool": "instructions",
    "label": "AI instructions"
   }
  },
  {
   "section": "Meaning",
   "value": "",
   "who": "asst",
   "status": "ready",
   "optional": false,
   "confidence": 0,
   "why": [],
   "diff": [],
   "touches": "",
   "tables": [],
   "skipReason": null,
   "skippedUtc": null,
   "state": "gap",
   "changeSet": null,
   "source": "words",
   "sourcePage": null,
   "doer": "assistant",
   "stateNote": null,
   "doneWhen": "has_words",
   "doneText": null,
   "objects": [
    "measure:Sales/YTD Sales",
    "measure:Sales/Sales LY"
   ],
   "reads": [
    "measure:Sales/YTD Sales",
    "measure:Sales/Sales LY"
   ],
   "writes": [
    "measure:Sales/YTD Sales",
    "measure:Sales/Sales LY"
   ],
   "footprintKnown": true,
   "waitsFor": [
    {
     "id": "timeintel/measure:Sales/Sales Amount",
     "title": "Add year to date and last year measures"
    }
   ],
   "held": false,
   "order": 5,
   "note": null,
   "reply": null,
   "fullText": null,
   "claim": null,
   "originLink": null,
   "events": [],
   "id": "pt-c4e1a9f3",
   "title": "Describe the 2 new time measures",
   "sub": "Waits for: Add year to date and last year measures",
   "stateWord": "waiting",
   "jump": {
    "tool": "readiness",
    "label": "AI readiness"
   }
  }
 ]
};

const clone = (v) => JSON.parse(JSON.stringify(v));

// Two Semanticus rows carry a worked-out change set, so the list shows Approve on them as the mockup does. The shape
// file sends none (E1 fixed the shape, not the sample's change sets); these are the only fields added to its rows.
const CHANGE_SETS = {
  'safefix/Visibility': { id: 'cs-visibility', state: 'proposed', digest: 'dg-visibility-1', lines: [] },
  'datetable/table:Date': { id: 'cs-datetable', state: 'proposed', digest: 'dg-datetable-1', lines: [] },
};

/** The plan the page reads first. mode: 'plain' (the mockup's sample), 'queue' (the assistant's track swapped for
 *  QUEUE's rows), 'empty' (nothing on the plan). free drops the Jev rows and the Jev line, as a free engine would. */
// The two best-practice build rows exactly as the engine sent them on the raw-CSV journey (lane m11/plan-rows, review-fix
// round 2 engine, the first plan read: a format Semanticus cannot prove is a choice in the words row), shown by mode 'bparows'.
const BPA_ROWS = [{"id": "bpafix/model", "section": "Structure", "title": "Fix 29 best-practice issues", "sub": "Waits for: Fix 5 safe things in formatting", "value": "fewer best-practice issues", "who": "auto", "status": "ready", "confidence": 0, "why": ["29 best-practice issues on Model quality have one exact fix each, so Semanticus can make them for you.", "Each fix is the rule's own. Undo takes them all back in one step.", "7 other fixes were left out, because they would choose a format Semanticus cannot prove, rename or change a type. Model quality lists them."], "diff": ["~ dim_date[FY_NO]: [Formatting] Do not summarize numeric columns", "~ dim_date[FY_QTR]: [Formatting] Do not summarize numeric columns", "~ dim_date[MTH_NO]: [Formatting] Do not summarize numeric columns", "~ dim_date[DOW_NO]: [Formatting] Do not summarize numeric columns", "~ dim_client[ONBRD_DT_KEY]: [Formatting] Do not summarize numeric columns", "~ dim_staff[STD_RATE]: [Formatting] Do not summarize numeric columns", "~ dim_staff[HIRE_DT_KEY]: [Formatting] Do not summarize numeric columns", "~ dim_project[BUDGET_HRS]: [Formatting] Do not summarize numeric columns, [Formatting] Hide fact table columns", "  and 14 more"], "touches": "22 objects", "tables": [{"name": "dim_client", "count": 3, "what": "2 columns, 1 measure", "diff": ["~ dim_client[ONBRD_DT_KEY]: [Formatting] Do not summarize numeric columns", "~ dim_client[CLNT_ACTV_CNT]: [Formatting] Whole numbers should be formatted with thousands separators and no decimals", "~ dim_client[CLNT_ID]: [Formatting] Hide foreign keys, [Formatting] Mark primary keys"]}, {"name": "dim_date", "count": 4, "what": "4 columns", "diff": ["~ dim_date[FY_NO]: [Formatting] Do not summarize numeric columns", "~ dim_date[FY_QTR]: [Formatting] Do not summarize numeric columns", "~ dim_date[MTH_NO]: [Formatting] Do not summarize numeric columns", "~ dim_date[DOW_NO]: [Formatting] Do not summarize numeric columns"]}, {"name": "dim_project", "count": 7, "what": "5 columns, 2 measures", "diff": ["~ dim_project[BUDGET_HRS]: [Formatting] Do not summarize numeric columns, [Formatting] Hide fact table columns", "~ dim_project[BUDGET_AMT]: [Formatting] Do not summarize numeric columns", "~ dim_project[START_DT_KEY]: [Formatting] Do not summarize numeric columns", "~ dim_project[END_DT_KEY]: [Formatting] Do not summarize numeric columns", "~ dim_project[PRJ_CNT]: [Formatting] Whole numbers should be formatted with thousands separators and no decimals", "~ dim_project[BUDGET_HRS_TOT]: [Formatting] Whole numbers should be formatted with thousands separators and no decimals", "~ dim_project[PRJ_ID]: [Formatting] Hide foreign keys, [Formatting] Mark primary keys"]}, {"name": "dim_staff", "count": 3, "what": "3 columns", "diff": ["~ dim_staff[STD_RATE]: [Formatting] Do not summarize numeric columns", "~ dim_staff[HIRE_DT_KEY]: [Formatting] Do not summarize numeric columns", "~ dim_staff[EMP_NO]: [Formatting] Hide foreign keys, [Formatting] Mark primary keys"]}, {"name": "fact_invoice", "count": 3, "what": "2 columns, 1 measure", "diff": ["~ fact_invoice[INV_AMT]: [Formatting] Do not summarize numeric columns, [Formatting] Hide fact table columns", "~ fact_invoice[TAX_AMT]: [Formatting] Do not summarize numeric columns, [Formatting] Hide fact table columns", "~ fact_invoice[INV_CNT]: [Formatting] Whole numbers should be formatted with thousands separators and no decimals"]}, {"name": "fact_timesheet", "count": 2, "what": "2 columns", "diff": ["~ fact_timesheet[HRS]: [Formatting] Do not summarize numeric columns, [Formatting] Hide fact table columns", "~ fact_timesheet[BILL_RATE]: [Formatting] Do not summarize numeric columns"]}], "jump": {"tool": "bpa", "label": "Model quality"}, "state": "gap", "source": "found", "doer": "semanticus", "stateWord": "waiting", "doneWhen": "model_check", "doneText": "Semanticus checks the model.", "objects": [], "reads": ["column:dim_client/CLNT_ID", "column:dim_client/ONBRD_DT_KEY", "column:dim_date/DOW_NO", "column:dim_date/FY_NO", "column:dim_date/FY_QTR", "column:dim_date/MTH_NO", "column:dim_project/BUDGET_AMT", "column:dim_project/BUDGET_HRS", "column:dim_project/END_DT_KEY", "column:dim_project/PRJ_ID", "column:dim_project/START_DT_KEY", "column:dim_staff/EMP_NO", "column:dim_staff/HIRE_DT_KEY", "column:dim_staff/STD_RATE", "column:fact_invoice/INV_AMT", "column:fact_invoice/TAX_AMT", "column:fact_timesheet/BILL_RATE", "column:fact_timesheet/HRS", "measure:dim_client/CLNT_ACTV_CNT", "measure:dim_project/BUDGET_HRS_TOT", "measure:dim_project/PRJ_CNT", "measure:fact_invoice/INV_CNT", "model:objects"], "writes": ["column:dim_client/CLNT_ID", "column:dim_project/PRJ_ID", "column:dim_staff/EMP_NO", "model:bpa/column:dim_client/CLNT_ID", "model:bpa/column:dim_client/ONBRD_DT_KEY", "model:bpa/column:dim_date/DOW_NO", "model:bpa/column:dim_date/FY_NO", "model:bpa/column:dim_date/FY_QTR", "model:bpa/column:dim_date/MTH_NO", "model:bpa/column:dim_project/BUDGET_AMT", "model:bpa/column:dim_project/BUDGET_HRS", "model:bpa/column:dim_project/END_DT_KEY", "model:bpa/column:dim_project/PRJ_ID", "model:bpa/column:dim_project/START_DT_KEY", "model:bpa/column:dim_staff/EMP_NO", "model:bpa/column:dim_staff/HIRE_DT_KEY", "model:bpa/column:dim_staff/STD_RATE", "model:bpa/column:fact_invoice/INV_AMT", "model:bpa/column:fact_invoice/TAX_AMT", "model:bpa/column:fact_timesheet/BILL_RATE", "model:bpa/column:fact_timesheet/HRS", "model:bpa/measure:dim_client/CLNT_ACTV_CNT", "model:bpa/measure:dim_project/BUDGET_HRS_TOT", "model:bpa/measure:dim_project/PRJ_CNT", "model:bpa/measure:fact_invoice/INV_CNT", "model:keys", "model:revision"], "footprintKnown": true, "waitsFor": [{"id": "safefix/Formatting", "title": "Fix 5 safe things in formatting"}], "held": false, "events": []}, {"id": "words/model", "section": "Meaning", "title": "Give the words to your assistant", "sub": "Waits for: Sort out 5 relationships and 3 more", "value": "assistants stop guessing", "who": "asst", "status": "ready", "confidence": 0, "why": ["Your assistant writes the words each one needs (a description, a business name, synonyms or the AI instructions) and chooses the formats Semanticus cannot prove, starting with the ones that raise the grade most. Semanticus checks every word lands and you can undo any of it.", "Words are the one thing Semanticus never writes itself. Your assistant reads the whole list from one call, a page at a time."], "diff": ["+ dim_client[CLNT_ACTV_CNT]: a description, a business name, a format to choose", "+ dim_project[PRJ_CNT]: a description, a business name, a format to choose", "+ dim_project[BUDGET_HRS_TOT]: a description, a business name, a format to choose", "+ fact_timesheet[HRS_TOT]: a description, a business name, a format to choose", "+ fact_timesheet[HRS_BILL]: a description, a business name, a format to choose", "+ fact_timesheet[HRS_NBILL]: a description, a business name, a format to choose", "+ fact_timesheet[UTIL_PCT]: a description, a business name, a format to choose", "+ fact_timesheet[TS_VAL]: a description, a business name, a format to choose", "  and 64 more"], "touches": "72 objects", "tables": [{"name": "dim_client", "count": 8, "what": "6 columns, 1 measure, 1 table", "diff": ["+ dim_client[CLNT_ACTV_CNT]: a description, a business name, a format to choose", "+ dim_client: a description, a business name", "+ dim_client[CLNT_ID]: a description, a business name", "+ dim_client[CLNT_NM]: a description, a business name", "+ dim_client[IND_CD]: a description, a business name", "+ dim_client[RGN_CD]: a description, a business name", "+ dim_client[ACTV_FLG]: a description, a business name", "+ dim_client[ONBRD_DT_KEY]: a description, a business name"]}, {"name": "dim_date", "count": 9, "what": "8 columns, 1 table", "diff": ["+ dim_date: a description, a business name", "+ dim_date[DATE_KEY]: a description, a business name", "+ dim_date[CAL_DT]: a description, a business name", "+ dim_date[FY_NO]: a description, a business name", "+ dim_date[FY_QTR]: a description, a business name", "+ dim_date[MTH_NO]: a description, a business name", "+ dim_date[MTH_ABBR]: a description, a business name", "+ dim_date[DOW_NO]: a description, a business name", "+ dim_date[WKDAY_FLG]: a description, a business name"]}, {"name": "dim_project", "count": 13, "what": "10 columns, 2 measures, 1 table", "diff": ["+ dim_project[PRJ_CNT]: a description, a business name, a format to choose", "+ dim_project[BUDGET_HRS_TOT]: a description, a business name, a format to choose", "+ dim_project: a description, a business name", "+ dim_project[PRJ_ID]: a description, a business name", "+ dim_project[CLNT_ID]: a description, a business name", "+ dim_project[PRJ_NM]: a description, a business name", "+ dim_project[PRAC_CD]: a description, a business name", "+ dim_project[ENG_TYPE_CD]: a description, a business name", "+ dim_project[BUDGET_HRS]: a description, a business name", "+ dim_project[BUDGET_AMT]: a description, a business name", "+ dim_project[START_DT_KEY]: a description, a business name", "+ dim_project[END_DT_KEY]: a description, a business name", "+ dim_project[STAT_CD]: a description, a business name"]}, {"name": "dim_staff", "count": 8, "what": "7 columns, 1 table", "diff": ["+ dim_staff: a description, a business name", "+ dim_staff[EMP_NO]: a description, a business name", "+ dim_staff[EMP_NM]: a description, a business name", "+ dim_staff[GRADE_CD]: a description, a business name", "+ dim_staff[PRAC_CD]: a description, a business name", "+ dim_staff[STD_RATE]: a description, a business name", "+ dim_staff[HIRE_DT_KEY]: a description, a business name", "+ dim_staff[TERM_FLG]: a description, a business name"]}, {"name": "fact_invoice", "count": 14, "what": "10 columns, 3 measures, 1 table", "diff": ["+ fact_invoice[INV_AMT_NET]: a description, a business name, a format to choose", "+ fact_invoice[INV_CNT]: a description, a business name, a format to choose", "+ fact_invoice[AR_OPEN]: a description, a business name, a format to choose", "+ fact_invoice: a description, a business name", "+ fact_invoice[INV_NO]: a description, a business name", "+ fact_invoice[PRJ_ID]: a description, a business name", "+ fact_invoice[CLNT_ID]: a description, a business name", "+ fact_invoice[INV_DT_KEY]: a description, a business name", "+ fact_invoice[DUE_DT_KEY]: a description, a business name", "+ fact_invoice[PAID_DT_KEY]: a description, a business name", "+ fact_invoice[INV_AMT]: a description, a business name", "+ fact_invoice[TAX_AMT]: a description, a business name", "+ fact_invoice[CURR_CD]: a description, a business name", "+ fact_invoice[VOID_FLG]: a description, a business name"]}, {"name": "fact_timesheet", "count": 15, "what": "9 columns, 5 measures, 1 table", "diff": ["+ fact_timesheet[HRS_TOT]: a description, a business name, a format to choose", "+ fact_timesheet[HRS_BILL]: a description, a business name, a format to choose", "+ fact_timesheet[HRS_NBILL]: a description, a business name, a format to choose", "+ fact_timesheet[UTIL_PCT]: a description, a business name, a format to choose", "+ fact_timesheet[TS_VAL]: a description, a business name, a format to choose", "+ fact_timesheet: a description, a business name", "+ fact_timesheet[TS_LINE_ID]: a description, a business name", "+ fact_timesheet[EMP_NO]: a description, a business name", "+ fact_timesheet[PRJ_ID]: a description, a business name", "+ fact_timesheet[WORK_DT_KEY]: a description, a business name", "+ fact_timesheet[HRS]: a description, a business name", "+ fact_timesheet[BILL_FLG]: a description, a business name", "+ fact_timesheet[BILL_RATE]: a description, a business name", "+ fact_timesheet[APPR_FLG]: a description, a business name", "+ fact_timesheet[SRC_ROW_TXT]: a description, a business name"]}, {"name": "ref_codes", "count": 4, "what": "3 columns, 1 table", "diff": ["+ ref_codes: a description, a business name", "+ ref_codes[CODE_TYPE]: a description, a business name", "+ ref_codes[CODE]: a description, a business name", "+ ref_codes[CODE_DESC]: a description, a business name"]}], "jump": {"tool": "readiness", "label": "AI readiness"}, "state": "gap", "source": "found", "doer": "assistant", "stateWord": "waiting", "stateNote": "Semanticus ordered these by the plan's order.", "doneWhen": "model_check", "doneText": "Semanticus checks the model.", "objects": [], "reads": ["column:dim_client/ACTV_FLG", "column:dim_client/CLNT_ID", "column:dim_client/CLNT_NM", "column:dim_client/IND_CD", "column:dim_client/ONBRD_DT_KEY", "column:dim_client/RGN_CD", "column:dim_date/CAL_DT", "column:dim_date/DATE_KEY", "column:dim_date/DOW_NO", "column:dim_date/FY_NO", "column:dim_date/FY_QTR", "column:dim_date/MTH_ABBR", "column:dim_date/MTH_NO", "column:dim_date/WKDAY_FLG", "column:dim_project/BUDGET_AMT", "column:dim_project/BUDGET_HRS", "column:dim_project/CLNT_ID", "column:dim_project/END_DT_KEY", "column:dim_project/ENG_TYPE_CD", "column:dim_project/PRAC_CD", "column:dim_project/PRJ_ID", "column:dim_project/PRJ_NM", "column:dim_project/START_DT_KEY", "column:dim_project/STAT_CD", "column:dim_staff/EMP_NM", "column:dim_staff/EMP_NO", "column:dim_staff/GRADE_CD", "column:dim_staff/HIRE_DT_KEY", "column:dim_staff/PRAC_CD", "column:dim_staff/STD_RATE", "column:dim_staff/TERM_FLG", "column:fact_invoice/CLNT_ID", "column:fact_invoice/CURR_CD", "column:fact_invoice/DUE_DT_KEY", "column:fact_invoice/INV_AMT", "column:fact_invoice/INV_DT_KEY", "column:fact_invoice/INV_NO", "column:fact_invoice/PAID_DT_KEY", "column:fact_invoice/PRJ_ID", "column:fact_invoice/TAX_AMT", "column:fact_invoice/VOID_FLG", "column:fact_timesheet/APPR_FLG", "column:fact_timesheet/BILL_FLG", "column:fact_timesheet/BILL_RATE", "column:fact_timesheet/EMP_NO", "column:fact_timesheet/HRS", "column:fact_timesheet/PRJ_ID", "column:fact_timesheet/SRC_ROW_TXT", "column:fact_timesheet/TS_LINE_ID", "column:fact_timesheet/WORK_DT_KEY", "column:ref_codes/CODE", "column:ref_codes/CODE_DESC", "column:ref_codes/CODE_TYPE", "measure:dim_client/CLNT_ACTV_CNT", "measure:dim_project/BUDGET_HRS_TOT", "measure:dim_project/PRJ_CNT", "measure:fact_invoice/AR_OPEN", "measure:fact_invoice/INV_AMT_NET", "measure:fact_invoice/INV_CNT", "measure:fact_timesheet/HRS_BILL", "measure:fact_timesheet/HRS_NBILL", "measure:fact_timesheet/HRS_TOT", "measure:fact_timesheet/TS_VAL", "measure:fact_timesheet/UTIL_PCT", "model:objects", "obj:Model/Model", "table:dim_client", "table:dim_date", "table:dim_project", "table:dim_staff", "table:fact_invoice", "table:fact_timesheet", "table:ref_codes"], "writes": ["column:dim_client/ACTV_FLG", "column:dim_client/CLNT_ID", "column:dim_client/CLNT_NM", "column:dim_client/IND_CD", "column:dim_client/ONBRD_DT_KEY", "column:dim_client/RGN_CD", "column:dim_date/CAL_DT", "column:dim_date/DATE_KEY", "column:dim_date/DOW_NO", "column:dim_date/FY_NO", "column:dim_date/FY_QTR", "column:dim_date/MTH_ABBR", "column:dim_date/MTH_NO", "column:dim_date/WKDAY_FLG", "column:dim_project/BUDGET_AMT", "column:dim_project/BUDGET_HRS", "column:dim_project/CLNT_ID", "column:dim_project/END_DT_KEY", "column:dim_project/ENG_TYPE_CD", "column:dim_project/PRAC_CD", "column:dim_project/PRJ_ID", "column:dim_project/PRJ_NM", "column:dim_project/START_DT_KEY", "column:dim_project/STAT_CD", "column:dim_staff/EMP_NM", "column:dim_staff/EMP_NO", "column:dim_staff/GRADE_CD", "column:dim_staff/HIRE_DT_KEY", "column:dim_staff/PRAC_CD", "column:dim_staff/STD_RATE", "column:dim_staff/TERM_FLG", "column:fact_invoice/CLNT_ID", "column:fact_invoice/CURR_CD", "column:fact_invoice/DUE_DT_KEY", "column:fact_invoice/INV_AMT", "column:fact_invoice/INV_DT_KEY", "column:fact_invoice/INV_NO", "column:fact_invoice/PAID_DT_KEY", "column:fact_invoice/PRJ_ID", "column:fact_invoice/TAX_AMT", "column:fact_invoice/VOID_FLG", "column:fact_timesheet/APPR_FLG", "column:fact_timesheet/BILL_FLG", "column:fact_timesheet/BILL_RATE", "column:fact_timesheet/EMP_NO", "column:fact_timesheet/HRS", "column:fact_timesheet/PRJ_ID", "column:fact_timesheet/SRC_ROW_TXT", "column:fact_timesheet/TS_LINE_ID", "column:fact_timesheet/WORK_DT_KEY", "column:ref_codes/CODE", "column:ref_codes/CODE_DESC", "column:ref_codes/CODE_TYPE", "measure:dim_client/CLNT_ACTV_CNT", "measure:dim_project/BUDGET_HRS_TOT", "measure:dim_project/PRJ_CNT", "measure:fact_invoice/AR_OPEN", "measure:fact_invoice/INV_AMT_NET", "measure:fact_invoice/INV_CNT", "measure:fact_timesheet/HRS_BILL", "measure:fact_timesheet/HRS_NBILL", "measure:fact_timesheet/HRS_TOT", "measure:fact_timesheet/TS_VAL", "measure:fact_timesheet/UTIL_PCT", "model:revision", "obj:Model/Model", "table:dim_client", "table:dim_date", "table:dim_project", "table:dim_staff", "table:fact_invoice", "table:fact_timesheet", "table:ref_codes"], "footprintKnown": true, "waitsFor": [{"id": "proposal/relationships", "title": "Sort out 5 relationships"}, {"id": "safefix/Visibility", "title": "Fix 9 safe things in visibility"}, {"id": "proposal/collisions", "title": "Fix 10 names that Copilot reads as the same word"}, {"id": "safefix/Formatting", "title": "Fix 5 safe things in formatting"}], "held": false, "events": []}];
// An empty model's two starts, as the engine sends them (ModelPlanView.Starts), shown by mode 'starts'.
const STARTS = [
  { id: 'sql', label: 'From a SQL source', line: 'Read the tables, columns and keys from a Fabric SQL endpoint with a Microsoft sign-in. No data rows are copied and no password is kept. Check the draft, then build it in one step.', jump: { tool: 'spec', label: 'Model Spec' }, op: 'autogenerate_spec_from_fabric' },
  { id: 'blank', label: 'New blank model', line: 'Start with nothing and add tables yourself.', jump: { tool: 'connections', label: 'Connections' }, op: 'create_model' },
];

export function makeHeartPlan({ mode = 'plain', free = false } = {}) {
  const shape = clone(HEART_SHAPE);
  let items = shape.rows.map((row) => (CHANGE_SETS[row.id] ? { ...row, changeSet: clone(CHANGE_SETS[row.id]) } : row));
  if (mode === 'bparows') items = [...clone(BPA_ROWS), ...items];
  // A model with no tables and one open row (a task the person typed): the starts still show (verify, 28 Sep, H3).
  if (mode === 'starts-open') items = items.filter((r) => r.id.startsWith('pt-')).slice(0, 1);
  if (mode === 'queue') {
    const queue = clone(HEART_QUEUE.items);
    const ids = new Set(queue.map((r) => r.id));
    items = [...items.filter((r) => !(r.id.startsWith('pt-') && r.doer === 'assistant') && !ids.has(r.id)
      && !r.id.startsWith('jev.describe.')), ...queue];
  }
  // 'lines': the Jev row carries a long change set, as a column format row does on a real model. Its diff lines
  // each hold a screen-reader label, which is what once pushed the whole Studio into a second scroll (m11 M4).
  if (mode === 'lines') {
    const cols = ['Amount', 'Quantity', 'Discount Rate', 'Unit Price', 'Cost', 'Freight', 'Tax', 'Margin', 'Returns', 'Weight', 'Units', 'Net'];
    items = items.map((r) => (r.id !== 'jev.describe.table:Product' ? r : {
      ...r,
      objects: [],
      changeSet: {
        id: 'cs-jev-lines', state: 'proposed', digest: 'dg-jev-lines-1', revision: 41,
        lines: cols.map((c, i) => ({
          id: `jev/column:Product/${c}`, planItemId: `ci-${String(i + 1).padStart(4, '0')}`,
          line: `~ ${c}: format as #,##0`, op: 'set_format_string', target: `column:Product/${c}`, property: 'formatString',
          before: '(none)', after: '#,##0', kept: true,
          provenance: { kind: 'decision', point: 'column.format', rule: null, confidence: 0.95, confidenceWord: 'sure' },
          reason: `${c} counts whole things, so it shows no decimal places.`,
          check: { exists: true, text: '#,##0 is the standard whole numbers format in the format library.' },
        })),
      },
    }));
  }
  // 'suggest': the Jev row carries one line the rule proved (kept, "sure", a check mark) and two it could not: a ratio and a
  // count over blank spaces. Those arrive as SUGGESTIONS, the way the engine sends them (THE FORMAT FILL RULE, addendum 2
  // item 11): unkept, no confidence word, no check, the plain note as a field, and the reason put as Jev's reading.
  if (mode === 'suggest') {
    const NOTE = 'Jev suggests this format; check it fits before you run it.';
    const decision = (word) => ({ kind: 'decision', point: 'measure.folder_and_format', rule: null, confidence: 0.97, confidenceWord: word });
    items = items.map((r) => (r.id !== 'jev.describe.table:Product' ? r : {
      ...r,
      objects: [],
      changeSet: {
        id: 'cs-jev-suggest', state: 'proposed', digest: 'dg-jev-suggest-1', revision: 41,
        lines: [
          { id: 'jev/measure:Sales/Orders', planItemId: 'ci-0001', line: '+ Orders: format as #,##0', op: 'set_measure_format',
            target: 'measure:Sales/Orders', property: 'formatString', before: '(none)', after: '#,##0', kept: true,
            provenance: decision('sure'), reason: 'It counts whole things, and its formula counts rows, so it shows no decimal places.',
            check: { exists: true, text: '#,##0 is the standard whole numbers format in the format library, and the model accepts it.' },
            suggestion: false, note: null },
          { id: 'jev/measure:Sales/Order Share', planItemId: 'ci-0002', line: '+ Order Share: format as #,##0', op: 'set_measure_format',
            target: 'measure:Sales/Order Share', property: 'formatString', before: '(none)', after: '#,##0', kept: false,
            provenance: decision('sure'), reason: "Jev's reading, not proved: it counts whole things, so it shows no decimal places.",
            check: null, suggestion: true, note: NOTE },
          { id: 'jev/measure:Sales/Spaced Orders', planItemId: 'ci-0003', line: '~ Spaced Orders: format as #,##0', op: 'set_measure_format',
            target: 'measure:Sales/Spaced Orders', property: 'formatString', before: '   ', after: '#,##0', kept: false,
            provenance: decision('sure'), reason: "Jev's reading, not proved: it counts whole things, so it shows no decimal places.",
            check: null, suggestion: true, note: NOTE },
        ],
      },
    }));
  }
  // 'held': the Jev row carries one line somebody WROTE over, held (m11 join part 5): a new plan or a Work it out again came along, so the words
  // stay on the plan with the engine's own reason, unticked and with no confidence word, and wait for the person's tick.
  if (mode === 'held') {
    items = items.map((r) => (r.id !== 'jev.describe.table:Product' ? r : {
      ...r,
      objects: [],
      changeSet: {
        id: 'cs-jev-held', state: 'proposed', digest: 'dg-jev-held-1', revision: 41,
        lines: [
          { id: 'jev/measure:Sales/Total Amount#description', planItemId: 'ci-0001', line: '~ Total Amount: description', op: 'set_description',
            target: 'measure:Sales/Total Amount', property: 'description', before: '(none)', after: 'Money in, as finance counts it.', kept: false,
            provenance: null, reason: 'Your words are kept here from the plan before. They wait for your tick on this one, and they are checked against the object as it is when you apply them.',
            check: null, suggestion: false, note: null },
        ],
      },
    }));
  }
  // 'cleanup': the cleanup worked out on the row, as the engine sends it since round 5 of m11/plan-harm (Kane, 30 Sep 2026): a suggestion
  // list for a lean model. EVERY unused column is a line. The ones the rule suggests arrive ticked and marked suggested, the rest arrive
  // unticked, and each carries its reason (LocalEngine.PlanCleanup.cs, CleanupGuard.Judge). One of the unticked lines is Jev's: on Pro it
  // may leave a suggestion out, with the engine's own sentence. The row's sentences are the engine's own too (LocalEngine.Plan.cs:
  // CleanupOptionalSentence, CleanupPurposeSentence, CleanupCountSentence).
  if (mode === 'cleanup') {
    const TICKED = 'Nothing uses it, and nothing about it looks like a key, a date, a name or a number.';
    const lines = [
      ['Customer', 'Segment', true, TICKED],
      ['Customer', 'Region', true, TICKED],
      ['Store', 'Channel', true, TICKED],
      ['Product', 'Barcode', false, 'Not ticked: Jev would leave it out of the suggestion (sure). Tick it if you want it removed.', 'jev'],
      ['Sales', 'BilledOn', false, 'Not ticked: it looks like a date.'],
      ['Product', 'ProductRef', false, 'Not ticked: it looks like a key.'],
    ];
    const gone = lines.filter(([, , ticked]) => ticked);
    items = [{
      ...items.find((r) => r.id === 'cleanup/unused-measures'),
      id: 'cleanup/model', title: `Remove ${gone.length} unused columns`, doer: 'you', who: 'you', stateWord: 'needs_you',
      optional: true,
      sub: `Optional: ${gone.map(([, c]) => c).join(', ')}`,
      why: [
        'Optional. No report\'s use of this model has been read yet, so Semanticus cannot tell whether a report shows these. Nothing else on the plan waits for this.',
        'This list keeps the model lean. It is only a suggestion, so you look it over before anything is removed: untick any line you want to keep, or set the whole row aside. A column that nothing uses today may still be wanted for a report you build later, and only you know that.',
        '6 unused columns are listed. 3 start ticked. 3 start unticked, each with its reason on the line (for example a key, a date, a name or a number). Press Work it out to see them all.',
        'Nothing in the model uses them.',
        'A version is saved first, so undo puts them back in one step.',
        '9 unused measures are not on this list. A measure is removed on its own, when you are sure no report needs it.',
      ],
      diff: gone.map(([t, c]) => `- column ${t}[${c}]`),
      objects: gone.map(([t, c]) => `column:${t}/${c}`),
      touches: `${gone.length} objects`,
      jump: { tool: 'lineage', label: 'Lineage' },
      changeSet: {
        id: 'cs-cleanup', state: 'proposed', digest: 'dg-cleanup-1', revision: 41,
        lines: lines.map(([t, c, ticked, reason, by], i) => ({
          id: `CLEANUP-UNUSED/column:${t}/${c}#object`, planItemId: `ci-${String(i + 1).padStart(4, '0')}`,
          line: `- ${t}[${c}]: remove unused column`, op: 'delete_if_unused', target: `column:${t}/${c}`,
          property: 'object', before: `Column: ${c}`, after: '', kept: ticked, suggested: ticked, reason,
          provenance: by === 'jev'
            ? { kind: 'decision', point: 'cleanup.suggest', rule: null, confidence: 0.92, confidenceWord: 'sure' }
            : { kind: 'rule', point: null, rule: 'CLEANUP-UNUSED', confidence: null },
        })),
      },
    }, ...items.filter((r) => r.id !== 'cleanup/unused-measures')];
  }
  if (free) items = items.filter((r) => r.source !== 'jev');
  const plan = {
    modelName: 'Contoso Sales', revision: 41, refreshedAt: '2026-09-24T10:46:00+00:00', ranked: !free,
    orderedBy: free ? 'In rule order.' : 'In value order, highest first.',
    items: mode === 'empty' || mode === 'starts' ? [] : items, starts: mode === 'starts' || mode === 'starts-open' ? clone(STARTS) : [], history: [], tables: [], targets: [],
    strip: shape.strip, assistantQueue: shape.assistantQueue, readyDigest: null,
    __doneBase: { semanticus: 5, you: 2, assistant: 4 },
  };
  if (free) plan.strip.updated = { when: plan.strip.updated.when, by: 'you' };
  if (mode === 'empty' || mode === 'starts' || mode === 'starts-open') plan.strip = { gradeNow: 'B', gradeAfter: null, tracks: [], latest: null, updated: null };
  plan.readyDigest = digestOf(plan);
  if (mode !== 'plain') recount(plan);
  return stampFold(plan);
}

const OPEN = (w) => w !== 'done' && w !== 'set_aside' && w !== 'checking';

// What the engine puts on the view for the assistant's track (ModelPlanView.AssistantSees and ModelPlanItem.OfferNumber,
// contract 6.1). The page shows these fields and works out nothing of its own; this mock engine plays them for the rows
// it can move (order, hold, the switch). The engine's own text over the same rows is pinned byte for byte by
// Semanticus.Tests/PlanHeartFoldParityTests.cs against plan-heart-fold-on.txt and plan-heart-fold-paused.txt, and
// test/plan-heart.test.mjs holds this mock to the same two files.
function stampFold(plan) {
  const mine = plan.items.filter((r) => r.id.startsWith('pt-') && r.doer === 'assistant');
  for (const r of plan.items) r.offerNumber = null;
  if (plan.assistantQueue === 'paused') {
    const held = mine.filter((r) => r.stateWord === 'on_it' && r.claim && r.claim.claimedBy !== 'you');
    plan.assistantSees = ['Plan: tasks are paused by the person. Carry on with what they ask you directly.',
      ...held.slice(0, 5).map((t) => `${t.title}: you may finish it.`)].join('\n');
    return plan;
  }
  const ready = mine.filter((r) => r.stateWord === 'ready').sort((a, b) => (a.order ?? 2147483647) - (b.order ?? 2147483647));
  ready.forEach((r, k) => { r.offerNumber = k + 1; });
  if (ready.length === 0) { plan.assistantSees = null; return plan; }
  const head = `Plan: ${ready.length}${ready.length === 1 ? ' task is ready for you.' : ' tasks are ready for you.'}`
    + (ready.length > 1 && ready.every((r) => r.footprintKnown) ? ' They do not depend on each other, so take more than one if you can.' : '');
  plan.assistantSees = [head,
    ...ready.slice(0, 5).map((r, k) => `${k + 1}. ${r.title} (${r.id})`),
    ...mine.filter((r) => r.stateWord === 'waiting').map((w) => `Not offered yet: ${w.title}. It waits for ${w.waitsFor?.[0]?.title ?? 'another row'}.`),
    'Call list_assistant_requests, then claim_request.'].join('\n');
  return plan;
}

/** The Ready Semanticus set as the engine fences it: ids, change set digest, held, and the revision. */
export function digestOf(plan) {
  const ready = plan.items.filter((r) => r.doer === 'semanticus' && r.stateWord === 'ready' && !r.held)
    .map((r) => `${r.id}|${r.changeSet?.digest ?? 'none'}|${r.held ? 1 : 0}`).sort();
  return `rd-${plan.revision}-${ready.join(',').length}-${ready.length}`;
}

/** The strip counted again from the rows, with the done counts the sample started with. */
export function recount(plan) {
  const base = plan.__doneBase ?? { semanticus: 0, you: 0, assistant: 0 };
  const tracks = ['semanticus', 'you', 'assistant'].map((doer) => {
    const mine = plan.items.filter((r) => r.doer === doer);
    const n = (w) => mine.filter((r) => r.stateWord === w).length;
    return { doer, done: base[doer] + mine.filter((r) => r.__doneHere).length, onIt: n('on_it'), ready: n('ready'),
      needsYou: n('needs_you'), waiting: n('waiting') + n('paused') };
  });
  plan.strip = { ...plan.strip, tracks };
  plan.readyDigest = digestOf(plan);
  return plan;
}

/** One compose pass over the rows the mock can move: held, waits, the switch's note. */
function compose(plan) {
  const byId = new Map(plan.items.map((r) => [r.id, r]));
  for (const r of plan.items) {
    if (!OPEN(r.stateWord) || r.stateWord === 'on_it') continue;
    const blockers = (r.waitsFor ?? []).filter((w) => byId.has(w.id) && OPEN(byId.get(w.id).stateWord));
    if (r.__sub === undefined) r.__sub = r.sub;
    if ((r.waitsFor ?? []).length && blockers.length === 0) {
      r.waitsFor = [];
      r.sub = r.__subBefore ?? r.sub.replace(/^Waits for: .*/u, 'Now ready');
    }
    if (r.held) r.stateWord = 'paused';
    else if ((r.waitsFor ?? []).length) r.stateWord = 'waiting';
    else if (r.doer === 'you') r.stateWord = 'needs_you';
    else r.stateWord = 'ready';
    if (r.doer === 'assistant' && r.stateWord === 'ready' && r.id.startsWith('pt-')) {
      r.stateNote = plan.assistantQueue === 'paused' ? 'Paused by you. Nothing is offered.' : 'on its next call';
    } else if (r.stateWord === 'paused') r.stateNote = null;
  }
  plan.revision += 0;
  return stampFold(recount(plan));
}

const find = (plan, id) => {
  const row = plan.items.find((r) => r.id === id);
  if (!row) throw new Error(`Nothing on the plan has the id ${id}.`);
  return row;
};

export function heartHold(plan, id, held) { const p = clone(plan); find(p, id).held = !!held; return compose(p); }

export function heartQueue(plan, state) { const p = clone(plan); p.assistantQueue = state === 'paused' ? 'paused' : 'on'; return compose(p); }

export function heartUp(plan, id) {
  const p = clone(plan);
  const row = find(p, id);
  const mine = p.items.filter((r) => r.id.startsWith('pt-') && r.doer === 'assistant' && OPEN(r.stateWord))
    .sort((a, b) => (a.order ?? 1e9) - (b.order ?? 1e9));
  const at = mine.indexOf(row);
  if (at <= 0) return compose(p);
  const above = mine[at - 1];
  [above.order, row.order] = [row.order, above.order];
  return compose(p);
}

export function heartDoer(plan, id, doer) {
  const p = clone(plan);
  const row = find(p, id);
  if (doer === 'semanticus' && row.id.startsWith('pt-')) throw new Error('Semanticus cannot do a task by itself. Give it to your assistant, or do it yourself.');
  row.doer = doer;
  if (doer !== 'assistant') row.order = null;
  if (doer === 'assistant' && row.order == null) row.order = Math.max(0, ...p.items.map((r) => r.order ?? 0)) + 1;
  return compose(p);
}

export function heartNote(plan, id, text) { const p = clone(plan); find(p, id).note = String(text ?? '').trim() || null; return compose(p); }

export function heartAdd(plan, request) {
  const p = clone(plan);
  const n = p.items.filter((r) => r.id.startsWith('pt-')).length + 1;
  const id = `pt-${(0x5a000000 + n * 7919).toString(16).slice(-8)}`;
  const order = Math.max(0, ...p.items.map((r) => r.order ?? 0)) + 1;
  // As PlanTasks.TitleOf: words longer than a title keep their start as the title and every word as fullText.
  const words = String(request.words);
  const long = words.length > 280;
  const cut = words.lastIndexOf(' ', 277);
  const title = long ? (cut > 138 ? words.slice(0, cut) : words.slice(0, 277)).trimEnd() + '...' : words;
  p.items.push({ id, section: 'Structure', title, fullText: long ? words : null, sub: 'Your words', value: '', who: 'asst', status: 'ready',
    confidence: 0, why: [], diff: [], touches: '', tables: [], jump: null, skipReason: null, skippedUtc: null, state: 'gap',
    changeSet: null, source: request.sourcePage ? 'you' : 'words', sourcePage: request.sourcePage ?? null,
    doer: request.doer ?? 'assistant', stateWord: 'ready', stateNote: 'on its next call', doneWhen: 'assistant_says',
    doneText: 'Your assistant says it answered.', objects: request.objects ?? [], reads: [], writes: [], footprintKnown: false,
    waitsFor: [], held: false, order, note: null, reply: null, claim: null, originLink: { kind: 'words', ref: null }, events: [] });
  return { itemId: id, action: 'add', performed: false,
    sentence: 'Added to the Plan.' + (long ? ' Its title is the start of your text, and the whole text is kept with the task.' : ''), plan: compose(p) };
}

export function heartClaim(plan, id) {
  const p = clone(plan);
  const row = find(p, id);
  row.claim = { claimedBy: 'you', since: '2026-09-24T10:50:00+00:00' };
  row.doer = 'you'; row.order = null; row.stateWord = 'on_it'; row.stateNote = 'you took it';
  return compose(p);
}

export function heartComplete(plan, id, reply) {
  const p = clone(plan);
  const row = find(p, id);
  row.reply = reply ? String(reply) : row.reply; row.claim = null;
  row.stateWord = row.doneWhen === 'you_mark' || row.doneWhen === 'assistant_says' ? 'done' : 'checking';
  row.__doneHere = true;
  return compose(p);
}

export function heartDecline(plan, id, reason) {
  const p = clone(plan);
  const row = find(p, id);
  row.claim = null; row.doer = 'you'; row.stateWord = 'needs_you'; row.stateNote = `You could not: ${reason}`;
  return compose(p);
}

/** Remove (Kane's UAT, 25 Sep): a task or draft row leaves the plan; the engine keeps it for Undo. The row and its place
 *  are handed back beside the result so the page's Undo (restorePlanRow) can put it back. A found row is refused. */
export function heartRemove(plan, id) {
  const p = clone(plan);
  const index = p.items.findIndex((r) => r.id === id);
  if (index < 0) throw new Error('No task on the plan has that id. It may have been removed already.');
  const row = p.items[index];
  if (!id.startsWith('pt-') && !id.startsWith('draft/')) throw new Error('Only a task or a draft can be removed.');
  p.items.splice(index, 1);
  const told = !!row.claim && row.claim.claimedBy !== 'you' || (row.stateWord === 'on_it' && row.doer === 'assistant');
  return { row, index, result: { itemId: id, title: row.title, kind: id.startsWith('draft/') ? 'draft' : 'task', toldAssistant: told,
    sentence: 'Removed from the Plan.', plan: compose(p) } };
}

/** Undo a removal: the row goes back where it stood, unclaimed. */
export function heartRestore(plan, removed) {
  if (!removed) throw new Error('There is nothing to put back for that row.');
  const p = clone(plan);
  const row = clone(removed.row);
  if (row.claim && row.claim.claimedBy !== 'you') { row.claim = null; row.stateWord = 'ready'; }
  p.items.splice(Math.min(removed.index, p.items.length), 0, row);
  return compose(p);
}

export function heartSkip(plan, id, reason) {
  const p = clone(plan);
  const row = find(p, id);
  row.stateWord = 'set_aside'; row.skipReason = reason; row.status = 'skipped';
  return compose(p);
}

/** Run the plan: refused when the quoted digest is not the one the plan has now, else every Ready Semanticus row is
 *  applied as one step and the rows that waited for them turn Ready (mockup section 6, "Now ready"). */
export function heartRun(plan, items, readyDigest) {
  if ((items ?? []).length) throw new Error('Pass the items or the ready digest, not both.');
  if (readyDigest !== plan.readyDigest) {
    return { performed: false, applied: 0, sentence: 'The plan moved since you read it. Read it again.', plan };
  }
  const p = clone(plan);
  const ran = p.items.filter((r) => r.doer === 'semanticus' && r.stateWord === 'ready' && !r.held);
  for (const r of ran) { r.stateWord = 'done'; r.__doneHere = true; }
  p.revision += 1;
  compose(p);
  return { performed: ran.length > 0, applied: ran.length, itemIds: ran.map((r) => r.id),
    sentence: `Applied ${ran.length} as one step you can undo.`, revision: p.revision, plan: p };
}

export function heartApprove(plan, id, digest) {
  const p = clone(plan);
  const row = find(p, id);
  if (row.changeSet?.digest !== digest) return { itemId: id, action: 'approve', performed: false, sentence: 'These changes moved. Look again.', plan };
  row.stateWord = 'done'; row.__doneHere = true; row.changeSet = { ...row.changeSet, state: 'applied' };
  p.revision += 1;
  return { itemId: id, action: 'approve', performed: true, sentence: 'Applied as one step you can undo.', revision: p.revision, plan: compose(p) };
}

/** Keep or drop one line, by the handle the engine decides it by (setPlanItem(planItemId, after, approved)). */
export function heartDecideLine(plan, ref, kept) {
  const next = clone(plan);
  for (const item of next.items) for (const line of item.changeSet?.lines ?? []) if (line.planItemId === ref) line.kept = !!kept;
  return next;
}
