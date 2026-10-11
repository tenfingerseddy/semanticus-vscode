// Round 5 (29 Sep): the known label slips of fresh-r2 and fresh-r3, corrected once, before any round-5 run of fresh-r3.
// The labels were written against "the data ends 11 October 2025"; the UAT fixture ends 31 December 2025, and the engine reads
// "last month / quarter / year" as the period the data ends in when the data ends on that period's last day (QueryPeriods
// EndsPeriod, decided in an earlier round), and "last week" as the Monday to Sunday week ending on or before the last day.
// Each correction ADDS an accepted reading with the reason; nothing is removed, and no other item is touched.
//   node correct-labels-r5.mjs
import { readFileSync, writeFileSync } from 'node:fs';
const dateFilter = (col, op, v) => ({ column: [col], op, values: [v] });
const fix = {
    'fresh-r2-labels.json': {
        'nn2-007': { reason: 'date end 31 Dec 2025: this month is December 2025', add: [
            { measures: [['Gross Profit']], groupBy: [['Store[Channel]']], filters: [dateFilter('Date[YearMonth]', 'is', '2025-12')], top: null },
            { measures: [['Gross Profit']], groupBy: [['Store[Channel]']], filters: [dateFilter('Date[Year]', 'is', '2025'), dateFilter('Date[MonthName]', 'is', 'December')], top: null }] },
        'nn2-016': { reason: 'date end 31 Dec 2025 (a Wednesday): last week is Monday 22 to Sunday 28 December', add: [
            { measures: [['Net Sales']], groupBy: [], filters: [dateFilter('Date[Date]', 'from', '2025-12-22'), dateFilter('Date[Date]', 'before', '2025-12-29')], top: null }] },
        'nn2-017': { reason: 'date end 31 Dec 2025: yesterday is read as the day the data ends on, 31 December 2025', add: [
            { measures: [['Order Count']], groupBy: [], filters: [dateFilter('Date[Date]', 'from', '2025-12-31'), dateFilter('Date[Date]', 'before', '2026-01-01')], top: null },
            { measures: [['Order Count']], groupBy: [], filters: [dateFilter('Date[Date]', 'is', '2025-12-31')], top: null }] },
        'nn2-018': { reason: 'date end 31 Dec 2025: last quarter is Q4 2025', add: [
            { measures: [['Units']], groupBy: [['Product[Category]']], filters: [dateFilter('Date[Year]', 'is', '2025'), dateFilter('Date[Quarter]', 'is', 'Q4')], top: null }] },
    },
    'fresh-r3-labels.json': {
        'nn3-001': { reason: 'date end 31 Dec 2025: last month is December 2025', add: [
            { measures: [['Net Sales']], groupBy: [], filters: [dateFilter('Date[YearMonth]', 'is', '2025-12')], top: null },
            { measures: [['Net Sales']], groupBy: [], filters: [dateFilter('Date[Year]', 'is', '2025'), dateFilter('Date[MonthName]', 'is', 'December')], top: null },
            { measures: [['Net Sales']], groupBy: [], filters: [dateFilter('Date[Date]', 'from', '2025-12-01'), dateFilter('Date[Date]', 'before', '2026-01-01')], top: null }] },
        'nn3-013': { reason: 'date end 31 Dec 2025: last year is 2025, the year the data ends in', add: [
            { measures: [['Return Rate %']], groupBy: [], filters: [dateFilter('Date[Year]', 'is', '2025')], top: null }] },
        'nn3-022': { reason: 'the fixture has a Store channel value, so "In-Store" is a slip of the word for it: Online and Store side by side is the right reading', add: [
            { measures: [['Net Sales']], groupBy: [['Store[Channel]']], filters: [{ column: ['Store[Channel]'], op: 'is', values: ['Online', 'Store'] }], top: null }] },
        'nn3-024': { reason: 'date end 31 Dec 2025: last quarter is Q4 2025', add: ['Store[Region]', 'Customer[Region]'].flatMap((g) => [
            { measures: [['Average Order Value']], groupBy: [[g]], filters: [dateFilter('Date[Year]', 'is', '2025'), dateFilter('Date[Quarter]', 'is', 'Q4')], top: null },
            { measures: [['Average Order Value']], groupBy: [[g]], filters: [dateFilter('Date[Date]', 'from', '2025-10-01'), dateFilter('Date[Date]', 'before', '2026-01-01')], top: null }]) },
    },
};
for (const [file, items] of Object.entries(fix)) {
    const labels = JSON.parse(readFileSync(file, 'utf8'));
    for (const [id, { reason, add }] of Object.entries(items)) {
        const item = labels.items.find((i) => i.id === id);
        if (!item) throw new Error(id + ' is not in ' + file);
        item.accept.push(...add);
        item.correction = 'round 5, 29 Sep: ' + reason;
    }
    labels.note = (labels.note ? labels.note + ' ' : '') + 'Corrected in round 5 (29 Sep) for the items carrying a "correction" field: an accepted reading was added; nothing was removed.';
    writeFileSync(file.replace('-labels.json', '-labels-corrected.json'), JSON.stringify(labels, null, 1) + '\n');
}
