// Verifier proofs for the Queries redesign (m11/queries-redesign 55792505, verify round 1, 27 Sep 2026). Each assertion
// FAILED on 55792505 when it was written; it is left failing on purpose and passes once the defect is fixed. The
// behaviour defects on the page itself are driven by tools/uishot/verify-queries-drive.mjs (scenes v1 to v14).
import assert from 'node:assert/strict';
import { columnFormatter, friendlyHeader } from '../webview/src/resultformat.mjs';

const failures = [];
const check = (ok, what) => { if (!ok) failures.push(what); };

// V-F1. An amount keeps its thousands separators whatever its measure is called. columnFormatter treats any column
// whose NAME matches /year|key|id$|code|number/ as a label, so common amounts lose their separators: "Sales Last
// Year", "Prior Year Sales" and "Amount Paid" (which ends in "id") show 5057989 where 5,057,989 is meant.
for (const name of ['Sales Last Year', 'Prior Year Sales', 'Amount Paid', 'Order Number Count']) {
  const shown = columnFormatter([[5057989], [4412070]], 0, name)(5057989);
  check(/5.?057.?989/.test(shown) && shown !== '5057989', `an amount named "${name}" shows "${shown}", without its separators`);
}
// The rule's own intent still holds: a year or a key reads as a label.
check(columnFormatter([[2023], [2024]], 0, "'Date'[Year]")(2024) === '2024', 'a year still reads 2024');

// V-F2. A header the DAX view shows reads the same as the answer's. Running the question's DAX by hand names its columns
// "[@Net Sales]" (the query's own "@Net Sales"), and friendlyHeader strips the @ only when there are no brackets, so the DAX view's
// header reads "@Net sales" next to the answer's "Net sales" (seen live, FrameworkTesting, 27 Sep, q44-hand-edit2).
check(friendlyHeader('[@Net Sales]') === 'Net sales', `friendlyHeader('[@Net Sales]') is "${friendlyHeader('[@Net Sales]')}"`);

if (failures.length) {
  console.error('verify-queries-redesign: ' + failures.length + ' defect(s)\n  - ' + failures.join('\n  - '));
  process.exit(1);
}
console.log('verify-queries-redesign: ok');
