// The duplicate-task-id check had no test, which made it the guard most likely to rot: it reports by finding
// nothing, and a parser that has stopped recognising the register also finds nothing. Its only blindness
// guard was "zero definition rows", so PARTIAL blindness passed silently.
//
// Every row shape below is asserted in both directions: a duplicate written that way must FAIL, and a single
// definition written that way must PASS. A shape the parser cannot see is a shape a duplicate can hide in.

import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const checkUrl = `file://${resolve(repoRoot, 'tools/tasks-register-check.mjs').replace(/\\/g, '/')}`;
const { analyse, main } = await import(checkUrl);

// A definition row in each shape the register actually uses or could plausibly use. The id is what matters;
// the decoration around it must not decide whether the check can see it.
const shapes = {
    plain: id => `- [${id}] Do the thing.`,
    bold: id => `- **[${id}]** Do the thing.`,
    boldNoStars: id => `- __[${id}]__ Do the thing.`,
    checkbox: id => `- [ ] [${id}] Do the thing.`,
    checkboxDone: id => `- [x] [${id}] Do the thing.`,
    backticked: id => `- \`[${id}]\` Do the thing.`,
    indented: id => `    - [${id}] Do the thing.`,
    tabIndented: id => `\t- [${id}] Do the thing.`,

    // All of these were silently invisible: both readings required `-` followed by a space, so a duplicate
    // written with any other unordered marker, without the space, or with italics walked straight through.
    // Only strikethrough failed, which was the single case the previous test covered - I widened to the shapes
    // I thought of and then tested exactly those.
    asterisk: id => `* [${id}] Do the thing.`,
    plus: id => `+ [${id}] Do the thing.`,
    noSpace: id => `-[${id}] Do the thing.`,
    dashAsteriskMixed: id => `- *[${id}]* Do the thing.`,
    strikethrough: id => `- ~~[${id}]~~ Withdrawn.`,
};

// CITATION CONTEXTS, asserted NOT to be definition rows. Review listed these among the shapes a duplicate could
// hide in, and they are - but in THIS register numbered items and table cells are where tasks are cited, not
// where ids are allocated, and that is measured: on the register at 14269af9 there are 21 such rows (the
// `## [T165]` programme heading and the twenty numbered "what needs Kane" items citing T164, T160, T109 and
// the rest). Treating every one as a definition row yields 59 rows, 40 ids and 18 FALSE duplicates, and fails
// the gate on prose that is doing nothing wrong.
//
// Headings are the split: they still do not count as definition rows, so canonical parsing is preserved, but
// they do join the duplicate set. Numbered items and table cells stay citations. The residual that remains is
// the ordered-list form: an id allocated as `1. [T200] new task` and also cited in an unordered row is still
// unseen as an allocation.
const citationContexts = {
    numbered: id => `1. **[${id}]** a decision citing an existing task.`,
    numberedLong: id => `12. **[${id}]** another one.`,
    numberedParen: id => `1) **[${id}]** a parenthesized marker citation.`,
    numberedNineDigits: id => `123456789. **[${id}]** a nine-digit marker citation.`,
    tableRow: id => `| [${id}] | a cell citing a task |`,
};
const headingContexts = {
    heading: id => `## [${id}] PROGRAMME HEADING`,
    headingDeep: id => `#### [${id}] sub-heading`,
    headingH1: id => `# [${id}] PROGRAMME HEADING`,
    headingH6: id => `###### [${id}] sub-heading`,
    headingBold: id => `## **[${id}]** PROGRAMME HEADING`,
    headingStrong: id => `## __[${id}]__ PROGRAMME HEADING`,
    headingEm: id => `## *[${id}]* PROGRAMME HEADING`,
    headingStrike: id => `## ~~[${id}]~~ PROGRAMME HEADING`,
    headingCode: id => `## \`[${id}]\` PROGRAMME HEADING`,
    headingLink: id => `## [[${id}]](https://example.test/${id}) PROGRAMME HEADING`,
};
const setextHeading = id => `[${id}] PROGRAMME HEADING\n=======================`;
const bs = String.fromCharCode(92);
const tab = String.fromCharCode(9);

// ---- 1. every shape is RECOGNISED as a definition ------------------------------------------------
for (const [name, row] of Object.entries(shapes)) {
    const { rows, definitions } = analyse([row('T140'), '', '## Done', '- 2026-07-22 [T99] shipped.'].join('\n'));
    assert.equal(rows, 1, `the ${name} row shape was not recognised as a definition, so a duplicate could hide in it`);
    assert.deepEqual([...definitions.keys()], [140], `the ${name} row shape yielded the wrong id`);
}

// ---- 2. every shape FAILS when it duplicates an id ----------------------------------------------
for (const [name, row] of Object.entries(shapes)) {
    const { duplicates } = analyse([shapes.plain('T140'), row('T140')].join('\n'));
    assert.equal(duplicates.length, 1, `a duplicate written in the ${name} shape was not caught`);
    assert.equal(duplicates[0][0], 140);
    assert.deepEqual(duplicates[0][1], [1, 2], `the ${name} duplicate reported the wrong line numbers`);
}

// ---- 2b. a citation context is NOT a definition row, and is not reported as blindness either -----
for (const [name, row] of Object.entries({ ...citationContexts, ...headingContexts })) {
    const one = analyse([shapes.plain('T140'), row('T141')].join('\n'));
    assert.equal(one.rows, 1, `the ${name} citation context was counted as a definition row`);
    assert.equal(one.unrecognised.length, 0,
        `the ${name} citation context was reported as an unreadable definition, which would fail the gate on prose`);
}

for (const [name, row] of Object.entries(citationContexts)) {
    // Two numbered or table citations of the SAME id are still not a duplicate definition.
    const two = analyse([row('T141'), row('T141')].join('\n'));
    assert.equal(two.duplicates.length, 0, `two ${name} citations of one id were reported as a duplicate definition`);
}

for (const [name, row] of Object.entries(headingContexts)) {
    // Two heading-form allocations of the SAME id are a duplicate. They still do not count as definition rows.
    const two = analyse([row('T141'), row('T141')].join('\n'));
    assert.equal(two.rows, 0, `two ${name} allocations were counted as definition rows`);
    assert.equal(two.duplicates.length, 1, `two ${name} allocations of one id were not reported as a duplicate`);
    assert.equal(two.duplicates[0][0], 141);
    assert.deepEqual(two.duplicates[0][1], [1, 2], `the ${name} duplicate reported the wrong line numbers`);
}

// ---- 2c. an id allocated ONLY in a citation context is reported as unprotected -------------------
//
// This is the general form of the T165 bug, and it is what would have caught it without anyone measuring 21
// rows by hand. `## [T165] DAX KERNEL PROGRAMME` was the SOLE allocation site of a live id: no `- [T165]`
// anywhere, `definitions.has(165)` false, and 165 sitting in the orphan list looking like a dangling
// reference. So it was the one id the duplicate check could not protect.
//
// For numbered items and table cells the fix is still the DATA: add the canonical bullet. For headings, adding
// that bullet without taking the id out of heading-leading position is the F-018 residual, and 2d refuses it.
for (const [name, row] of Object.entries({ ...citationContexts, ...headingContexts })) {
    const alone = analyse([shapes.plain('T140'), row('T141')].join('\n'));
    assert.equal(alone.unprotected.length, 1,
        `an id allocated only in a ${name} context was not reported: the duplicate rule cannot protect it`);
    assert.equal(alone.unprotected[0].id, 141);
    assert.equal(alone.duplicates.length, 0,
        `a single ${name} allocation was reported as a duplicate against nothing`);
}

for (const [name, row] of Object.entries(citationContexts)) {
    const fixed_ = analyse([shapes.plain('T140'), row('T141'), shapes.plain('T141')].join('\n'));
    assert.equal(fixed_.unprotected.length, 0,
        `a ${name} citation still reported as unprotected after a canonical definition row was added`);
    assert.equal(fixed_.duplicates.length, 0,
        `a ${name} citation plus its canonical row was reported as a duplicate definition`);
}

// ---- 2d. FAIL-RED CONTROL: a heading plus a unique bullet of the same id is a duplicate ----------
//
// Canonical parsing is preserved: the heading is still not a definition row. The residual F-018 named is that
// the heading was invisible to the duplicate rule, so a unique bullet made the pair look clean. This is the
// control that must go red, in both directions of the pair, including the T165 shape of heading then bullet.
for (const [name, row] of Object.entries(headingContexts)) {
    const headingThenBullet = analyse([row('T140'), shapes.plain('T140')].join('\n'));
    assert.equal(headingThenBullet.rows, 1, `the ${name} plus bullet pair counted the heading as a definition row`);
    assert.equal(headingThenBullet.unprotected.length, 0,
        `the ${name} plus bullet pair was reported unprotected, which hides the duplicate`);
    assert.equal(headingThenBullet.duplicates.length, 1, `the ${name} plus bullet pair was not reported as a duplicate`);
    assert.equal(headingThenBullet.duplicates[0][0], 140);
    assert.deepEqual(headingThenBullet.duplicates[0][1], [1, 2],
        `the ${name} then bullet duplicate reported the wrong line numbers`);

    const bulletThenHeading = analyse([shapes.plain('T140'), row('T140')].join('\n'));
    assert.equal(bulletThenHeading.rows, 1, `the bullet plus ${name} pair counted the heading as a definition row`);
    assert.equal(bulletThenHeading.duplicates.length, 1, `the bullet plus ${name} pair was not reported as a duplicate`);
    assert.deepEqual(bulletThenHeading.duplicates[0][1], [1, 2],
        `the bullet then ${name} duplicate reported the wrong line numbers`);
}

// ---- 2e. watched-red heading allocations: Setext, wrappers already in headingContexts, and the exclusions
//
// Canonical counts stay bullet rows. Duplicate allocations add a real top-level heading whose first rendered
// item is the id. Setext reports the content line, not the underline. Fenced, indented, blockquoted and
// list-nested headings are not allocations. Date-first headings and ids after visible prose are citations.
// HTML or entity decoration in leading position must fail loudly, not pass silent, but only when an id is
// hidden at allocation position.
{
    const threeSpaceThenBullet = analyse(['   ## [T140] PROGRAMME HEADING', '- [T140] a'].join('\n'));
    assert.equal(threeSpaceThenBullet.rows, 1, 'a 3-space ATX heading was counted as a definition row');
    assert.equal(threeSpaceThenBullet.duplicates.length, 1,
        'a 3-space ATX heading plus unique bullet was not a duplicate');
    assert.deepEqual(threeSpaceThenBullet.duplicates[0][1], [1, 2]);

    const threeSpaceAlone = analyse(['   ## [T141] PROGRAMME HEADING', '- [T140] a'].join('\n'));
    assert.equal(threeSpaceAlone.unprotected.length, 1,
        'a 3-space ATX heading as the only leading site of T141 was not reported unprotected');
    assert.equal(threeSpaceAlone.unprotected[0].id, 141);

    const setextThenBullet = analyse([setextHeading('T140'), shapes.plain('T140')].join('\n'));
    assert.equal(setextThenBullet.rows, 1, 'a Setext heading was counted as a definition row');
    assert.equal(setextThenBullet.duplicates.length, 1, 'a Setext heading plus unique bullet was not a duplicate');
    assert.equal(setextThenBullet.duplicates[0][0], 140);
    assert.deepEqual(setextThenBullet.duplicates[0][1], [1, 3],
        'the Setext duplicate must name the content line, not the underline');

    const bulletThenSetext = analyse([shapes.plain('T140'), setextHeading('T140')].join('\n'));
    assert.equal(bulletThenSetext.duplicates.length, 1, 'a unique bullet plus Setext heading was not a duplicate');
    assert.deepEqual(bulletThenSetext.duplicates[0][1], [1, 2],
        'the bullet then Setext duplicate must name the content line');

    const twoSetext = analyse([setextHeading('T141'), setextHeading('T141')].join('\n'));
    assert.equal(twoSetext.rows, 0, 'two Setext headings were counted as definition rows');
    assert.equal(twoSetext.duplicates.length, 1, 'two Setext allocations of one id were not a duplicate');
    assert.deepEqual(twoSetext.duplicates[0][1], [1, 3],
        'two Setext headings must report both content lines');

    const fenced = analyse([
        '```',
        '## [T140] PROGRAMME HEADING',
        '```',
        '- [T140] a',
    ].join('\n'));
    assert.equal(fenced.rows, 1, 'a fenced heading stole the definition-row count');
    assert.equal(fenced.duplicates.length, 0,
        'a heading inside a fence was counted as an allocation: fenced false positive');
    assert.equal(fenced.unrecognised.length, 0, 'a fenced heading was reported as unreadable');

    const indented = analyse(['    ## [T140] PROGRAMME HEADING', '- [T140] a'].join('\n'));
    assert.equal(indented.duplicates.length, 0,
        'a four-space indented heading was counted as an allocation: indented false positive');

    const tabbed = analyse(['\t## [T140] PROGRAMME HEADING', '- [T140] a'].join('\n'));
    assert.equal(tabbed.duplicates.length, 0,
        'a tab-indented heading was counted as an allocation: indented false positive');

    const quoted = analyse(['> ## [T140] PROGRAMME HEADING', '- [T140] a'].join('\n'));
    assert.equal(quoted.duplicates.length, 0,
        'a blockquoted heading was counted as an allocation');

    const nested = analyse(['- parent item', '  ## [T140] PROGRAMME HEADING', '- [T140] a'].join('\n'));
    assert.equal(nested.duplicates.length, 0,
        'a list-nested heading was counted as an allocation');

    const dateFirst = analyse(['## 2026-07-22 [T140] shipped in a heading', '- [T140] a'].join('\n'));
    assert.equal(dateFirst.rows, 1, 'a date-first heading was counted as a definition row');
    assert.equal(dateFirst.duplicates.length, 0,
        'a date-first heading was counted as an allocation: citation false positive');
    assert.equal(dateFirst.unrecognised.length, 0, 'a date-first heading was reported as unreadable');

    const proseFirst = analyse([
        '## DAX KERNEL PROGRAMME [T165]: Phase A, lanes A0 to A9',
        '- [T165] programme row.',
    ].join('\n'));
    assert.equal(proseFirst.rows, 1, 'the T165 prose-first heading was counted as a definition row');
    assert.equal(proseFirst.duplicates.length, 0,
        'the current T165 heading plus its bullet was treated as a duplicate allocation');
    assert.equal(proseFirst.unprotected.length, 0, 'the current T165 heading was reported unprotected');

    const html = analyse(['## <em>[T140]</em> PROGRAMME HEADING', '- [T140] a'].join('\n'));
    assert.equal(html.unrecognised.length, 1,
        'leading HTML on a heading was silent: unsupported decoration must fail loudly');
    assert.equal(html.unrecognised[0].line, 1);

    const entity = analyse(['## &nbsp;[T140] PROGRAMME HEADING', '- [T140] a'].join('\n'));
    assert.equal(entity.unrecognised.length, 1,
        'leading entity decoration on a heading was silent: unsupported decoration must fail loudly');
    assert.equal(entity.unrecognised[0].line, 1);
}

// ---- 2f. repair cases. Watch these go red on the T-1932 scanner, then land recognition.
//
// Multiline Setext whose first rendered item is the id. List-container state surviving nested fences
// and quotes, plus CommonMark marker indentation. HTML/entity fail only when an id is hidden at
// allocation position. Raw HTML blocks and comments excluded. Escaped leading task ids detected.
{
    const multiSetext = analyse([
        '[T140] PROGRAMME',
        'HEADING CONTINUED',
        '=======================',
        '- [T140] a',
    ].join('\n'));
    assert.equal(multiSetext.rows, 1, 'a multiline Setext heading was counted as a definition row');
    assert.equal(multiSetext.duplicates.length, 1,
        'a multiline Setext heading whose first rendered item is the id plus a unique bullet was not a duplicate');
    assert.equal(multiSetext.duplicates[0][0], 140);
    assert.deepEqual(multiSetext.duplicates[0][1], [1, 4],
        'the multiline Setext duplicate must name the first content line, not the underline');

    const multiSetextAlone = analyse([
        '[T141] PROGRAMME',
        'HEADING CONTINUED',
        '=======================',
        '- [T140] a',
    ].join('\n'));
    assert.equal(multiSetextAlone.unprotected.length, 1,
        'a multiline Setext heading as the only leading site of T141 was not reported unprotected');
    assert.equal(multiSetextAlone.unprotected[0].id, 141);
    assert.equal(multiSetextAlone.duplicates.length, 0);

    const multiProseFirst = analyse([
        'PROGRAMME',
        '[T140] later',
        '=======================',
        '- [T140] a',
    ].join('\n'));
    assert.equal(multiProseFirst.duplicates.length, 0,
        'a multiline Setext heading with the id after prose was counted as an allocation');
    assert.equal(multiProseFirst.unrecognised.length, 0);

    const listFence = analyse([
        '- parent item',
        '  ```',
        '  ## [T199] inside fence in list',
        '  ```',
        '  ## [T140] still nested after fence',
        '- [T140] a',
    ].join('\n'));
    assert.equal(listFence.rows, 1, 'a nested fence stole or invented a definition-row count');
    assert.equal(listFence.duplicates.length, 0,
        'a list-nested heading after a nested fence was counted as an allocation: list state died');
    assert.equal(listFence.unrecognised.length, 0);

    const listQuote = analyse([
        '- parent item',
        '  > quoted',
        '  ## [T140] still nested after quote',
        '- [T140] a',
    ].join('\n'));
    assert.equal(listQuote.rows, 1, 'a nested quote stole or invented a definition-row count');
    assert.equal(listQuote.duplicates.length, 0,
        'a list-nested heading after a nested quote was counted as an allocation: list state died');

    const markerIndent = analyse([
        '-     parent with five spaces after the marker',
        '  ## [T140] PROGRAMME HEADING',
        '- [T140] a',
    ].join('\n'));
    assert.equal(markerIndent.rows, 1, 'CommonMark marker padding changed the definition-row count');
    assert.equal(markerIndent.duplicates.length, 0,
        'a heading at the CommonMark content column after wide marker padding was counted as top-level');

    const htmlNoId = analyse(['## <em>Hello</em> world', '- [T140] a'].join('\n'));
    assert.equal(htmlNoId.unrecognised.length, 0,
        'a no-id HTML heading failed loudly: only a hidden id at allocation position is unsupported');
    assert.equal(htmlNoId.duplicates.length, 0);
    assert.equal(htmlNoId.rows, 1);

    const htmlProseFirst = analyse(['## <em>Hello [T140]</em>', '- [T140] a'].join('\n'));
    assert.equal(htmlProseFirst.unrecognised.length, 0,
        'a prose-first HTML heading failed loudly: the id is not at allocation position');
    assert.equal(htmlProseFirst.duplicates.length, 0);

    const entityNoId = analyse(['## &copy; 2026', '- [T140] a'].join('\n'));
    assert.equal(entityNoId.unrecognised.length, 0,
        'a no-id entity heading failed loudly: only a hidden id at allocation position is unsupported');
    assert.equal(entityNoId.duplicates.length, 0);

    const entityProseFirst = analyse(['## &nbsp;Hello [T140]', '- [T140] a'].join('\n'));
    assert.equal(entityProseFirst.unrecognised.length, 0,
        'a prose-first entity heading failed loudly: the id is not at allocation position');
    assert.equal(entityProseFirst.duplicates.length, 0);

    const htmlBlock = analyse([
        '<div>',
        '## [T140] PROGRAMME HEADING',
        '</div>',
        '',
        '- [T140] a',
    ].join('\n'));
    assert.equal(htmlBlock.rows, 1, 'an HTML block stole the definition-row count');
    assert.equal(htmlBlock.duplicates.length, 0,
        'a heading inside a raw HTML block was counted as an allocation');
    assert.equal(htmlBlock.unrecognised.length, 0, 'a heading inside a raw HTML block was reported as unreadable');

    const htmlComment = analyse([
        '<!--',
        '## [T140] PROGRAMME HEADING',
        '-->',
        '- [T140] a',
    ].join('\n'));
    assert.equal(htmlComment.rows, 1, 'an HTML comment stole the definition-row count');
    assert.equal(htmlComment.duplicates.length, 0,
        'a heading inside an HTML comment was counted as an allocation');
    assert.equal(htmlComment.unrecognised.length, 0, 'a heading inside an HTML comment was reported as unreadable');

    const escapedThenBullet = analyse([
        `## ${bs}[T140] PROGRAMME HEADING`,
        '- [T140] a',
    ].join('\n'));
    assert.equal(escapedThenBullet.rows, 1, 'an escaped leading id was counted as a definition row');
    assert.equal(escapedThenBullet.unrecognised.length, 0,
        'an escaped leading task id was reported unreadable instead of detected as an allocation');
    assert.equal(escapedThenBullet.duplicates.length, 1,
        'an escaped leading task id plus unique bullet was not detected as a duplicate');
    assert.deepEqual(escapedThenBullet.duplicates[0][1], [1, 2]);

    const escapedAlone = analyse([
        `## ${bs}[T141] PROGRAMME HEADING`,
        '- [T140] a',
    ].join('\n'));
    assert.equal(escapedAlone.unprotected.length, 1,
        'an escaped leading task id as the only leading site of T141 was not reported unprotected');
    assert.equal(escapedAlone.unprotected[0].id, 141);
}

// ---- 2g. CommonMark boundary matrix added after the first state machine landed -------------------
//
// These cases are separate from 2f so the stronger T-1967 protections stay visible. Each allocation case
// pairs the heading with one canonical bullet; each exclusion must leave that bullet unique.
{
    for (let digits = 1; digits <= 9; digits++) {
        for (const delimiter of ['.', ')']) {
            const marker = `${'1'.repeat(digits)}${delimiter}`;
            const citation = analyse([`${marker} **[T140]** citation`, '- [T140] canonical row'].join('\n'));
            assert.equal(citation.duplicates.length, 0,
                `${digits}-digit ${delimiter} ordered marker stopped being a citation site`);

            const padding = ' '.repeat(marker.length + 1);
            const nested = analyse([`${marker} parent`, `${padding}## [T140] nested heading`, '- [T140] canonical row'].join('\n'));
            assert.equal(nested.duplicates.length, 0,
                `${digits}-digit ${delimiter} ordered marker lost its nested heading`);
        }
    }

    const nestedOrdered = analyse([
        '1) outer',
        '   2) inner',
        '      ## [T140] nested heading',
        '- [T140] canonical row',
    ].join('\n'));
    assert.equal(nestedOrdered.duplicates.length, 0, 'a nested ordered-list heading was counted as top-level');

    const tabIndentedCode = analyse([` ${tab}## [T140] code`, '- [T140] canonical row'].join('\n'));
    assert.equal(tabIndentedCode.duplicates.length, 0,
        'spaces plus a tab did not advance to the CommonMark four-column code indent');

    const tabAfterMarker = analyse([`1)${tab}parent`, '   ## [T140] top-level heading', '- [T140] canonical row'].join('\n'));
    assert.equal(tabAfterMarker.duplicates.length, 1,
        'a tab after an ordered marker swallowed a heading left of the item content column');

    const spacesTabAfterMarker = analyse([`- ${tab}parent`, '   ## [T140] top-level heading', '- [T140] canonical row'].join('\n'));
    assert.equal(spacesTabAfterMarker.duplicates.length, 1,
        'spaces plus a tab after a marker used character count instead of tab stops');

    const entityCases = {
        tab: analyse(['## &Tab;[T140] hidden allocation', '- [T140] canonical row'].join('\n')),
        newline: analyse(['## &NewLine;[T140] hidden allocation', '- [T140] canonical row'].join('\n')),
        wrongCase: analyse(['## &NBSP;[T140] citation', '- [T140] canonical row'].join('\n')),
        upperHexTab: analyse(['## &#X9;[T140] hidden allocation', '- [T140] canonical row'].join('\n')),
    };
    assert.equal(entityCases.tab.unrecognised.length, 1, '&Tab; was not decoded as leading whitespace');
    assert.equal(entityCases.newline.unrecognised.length, 1, '&NewLine; was not decoded from the named-entity root');
    assert.equal(entityCases.wrongCase.unrecognised.length, 0, '&NBSP; was treated as the valid &nbsp; entity');
    assert.equal(entityCases.wrongCase.duplicates.length, 0, '&NBSP; exposed a task id as an allocation');
    assert.equal(entityCases.upperHexTab.unrecognised.length, 1, 'uppercase X numeric hex was not decoded');

    for (const [name, encoded] of Object.entries({
        upperHex: '&#X5B;T140&#X5D;',
        decimal: '&#91;T140&#93;',
        named: '&lbrack;T140&rbrack;',
    })) {
        const result = analyse([`## ${encoded} encoded brackets`, '- [T140] canonical row'].join('\n'));
        assert.equal(result.duplicates.length, 1, `${name}: encoded brackets hid a heading allocation`);
        assert.deepEqual(result.duplicates[0][1], [1, 2], `${name}: encoded brackets reported wrong lines`);
    }

    const quotedGreaterThan = analyse([
        '## <span title="a > b">[T140]</span> hidden allocation',
        '- [T140] canonical row',
    ].join('\n'));
    assert.equal(quotedGreaterThan.unrecognised.length, 1,
        'a greater-than sign inside a quoted HTML attribute ended the tag early');

    const linkDefinition = analyse(['[T140]: /url', '===', '- [T140] canonical row'].join('\n'));
    assert.equal(linkDefinition.duplicates.length, 0,
        '[T140]: /url before a Setext underline is a link definition, not a heading allocation');

    const referenceLinkHeading = analyse(['[[T140]][ref]', '===', '- [T140] canonical row'].join('\n'));
    assert.equal(referenceLinkHeading.duplicates.length, 1,
        'a reference-link wrapped task id before a Setext underline was not allocated');

    const type7Html = analyse([
        '<semanticus-card data-kind="custom">',
        '## [T140] inside custom HTML block',
        '</semanticus-card>',
        '',
        '- [T140] canonical row',
    ].join('\n'));
    assert.equal(type7Html.duplicates.length, 0, 'a heading inside a custom type-7 HTML block was allocated');
    assert.equal(type7Html.unrecognised.length, 0, 'a heading inside a custom type-7 HTML block failed loudly');

    const type7CannotInterruptParagraph = analyse([
        'paragraph text',
        '<span>',
        '## [T140] heading after paragraph inline HTML',
        '- [T140] canonical row',
    ].join('\n'));
    assert.equal(type7CannotInterruptParagraph.duplicates.length, 1,
        'a complete type-7 tag interrupted a paragraph and swallowed the following ATX allocation');
    assert.deepEqual(type7CannotInterruptParagraph.duplicates[0][1], [3, 4],
        'the heading after paragraph inline HTML reported wrong lines');

    const orderedTwoCannotInterruptParagraph = analyse([
        'paragraph text',
        '2. item cannot interrupt this paragraph',
        '<span>',
        '## [T140] heading after paragraph inline HTML',
        '',
        '- [T140] canonical row after the HTML boundary',
    ].join('\n'));
    assert.equal(orderedTwoCannotInterruptParagraph.duplicates.length, 1,
        'an ordered marker starting at 2 interrupted a paragraph and let type-7 HTML swallow the ATX allocation');
    assert.deepEqual(orderedTwoCannotInterruptParagraph.duplicates[0][1], [4, 6],
        'the start-at-2 paragraph control reported the wrong allocation lines');

    const orderedOneInterruptsParagraph = analyse([
        'paragraph text',
        '1. item starts a list and interrupts the paragraph',
        '<span>',
        '## [T140] inside type-7 HTML block',
        '',
        '- [T140] canonical row after the HTML boundary',
    ].join('\n'));
    assert.equal(orderedOneInterruptsParagraph.duplicates.length, 0,
        'an ordered marker starting at 1 failed to reset paragraph state before type-7 HTML');

    const thematicBreakEndsParagraphBeforeType7 = analyse([
        'paragraph text',
        '***',
        '<span>',
        '## [T140] inside type-7 HTML block',
        '',
        '- [T140] canonical row after the HTML boundary',
    ].join('\n'));
    assert.equal(thematicBreakEndsParagraphBeforeType7.duplicates.length, 0,
        'a thematic break failed to end paragraph state before a type-7 HTML block');

    const doubleBacktickCode = analyse([
        '## ``[T140]`` code span',
        '- [T140] canonical row',
    ].join('\n'));
    assert.equal(doubleBacktickCode.duplicates.length, 1,
        'a matching double-backtick code span hid a heading allocation');
    assert.deepEqual(doubleBacktickCode.duplicates[0][1], [1, 2],
        'the double-backtick code-span allocation reported wrong lines');

    const tripleBacktickCode = analyse([
        '## ```[T140]``` code span',
        '- [T140] canonical row',
    ].join('\n'));
    assert.equal(tripleBacktickCode.duplicates.length, 1,
        'a matching triple-backtick code span hid a heading allocation');

    const escapedInCode = analyse([
        `## \`${bs}[T140]\` code span`,
        '- [T140] canonical row',
    ].join('\n'));
    assert.equal(escapedInCode.duplicates.length, 0,
        'a backslash escape operated inside a code span and exposed a task id');

    const escapedInDoubleBackticks = analyse([
        `## \`\`${bs}[T140]\`\` code span`,
        '- [T140] canonical row',
    ].join('\n'));
    assert.equal(escapedInDoubleBackticks.duplicates.length, 0,
        'a backslash inside a double-backtick code span stopped being literal');

    for (const [name, block] of Object.entries({
        type1: ['<pre>', '- [T140] inert raw HTML bullet', '</pre>'],
        type6: ['<div>', '- [T140] inert raw HTML bullet', '</div>', ''],
        type7: ['<semanticus-card>', '- [T140] inert raw HTML bullet', '</semanticus-card>', ''],
        comment: ['<!--', '- [T140] inert comment bullet', '-->'],
        fence: ['```text', '- [T140] inert fenced bullet', '```'],
    })) {
        const result = analyse([...block, '- [T140] canonical row'].join('\n'));
        assert.equal(result.rows, 1, `${name}: an inert bullet was counted as a canonical definition row`);
        assert.equal(result.duplicates.length, 0, `${name}: an inert bullet duplicated the canonical definition`);
    }

    const thematicStopsSetext = analyse(['[T140] paragraph', '***', 'continued', '===', '- [T140] canonical row'].join('\n'));
    assert.equal(thematicStopsSetext.duplicates.length, 0, 'Setext content crossed a thematic break');

    const lazyListStopsSetext = analyse(['[T140] paragraph', '- lazy list line', '===', '- [T140] canonical row'].join('\n'));
    assert.equal(lazyListStopsSetext.duplicates.length, 0, 'Setext content crossed a lazy list-item line');
    const lazyQuoteStopsSetext = analyse(['[T140] paragraph', '> lazy quote line', '===', '- [T140] canonical row'].join('\n'));
    assert.equal(lazyQuoteStopsSetext.duplicates.length, 0, 'Setext content crossed a lazy blockquote line');
}

// ---- 2h. a sibling list item ends an unclosed nested fenced or HTML block (F-110) ----------------
//
// A fenced or raw HTML block opened inside a list item belongs to that item, and CommonMark gives a fenced
// block no lazy continuation, so the first non-blank line left of the item's content column ends the item
// and the block with it. The scanner kept the block inert to end of file instead. Every case here puts one
// canonical row AHEAD of the list, because that is the register's real shape and it is what makes this
// silent: with rows > 0 the blindness guard never fires, so main() prints "Every task id is allocated
// exactly once" over a genuine duplicate it simply stopped reading.
{
    const earlier = '- [T100] an earlier canonical row';

    const unclosedFence = analyse([
        earlier,
        '- parent item',
        '  ```',
        '  ## [T199] inside an unclosed nested fence',
        '- [T140] canonical row',
        '- [T140] duplicate row',
    ].join('\n'));
    assert.equal(unclosedFence.rows, 3,
        'a sibling list item did not end an unclosed nested fence: the rows after it went unread');
    assert.equal(unclosedFence.duplicates.length, 1,
        'a duplicate after an unclosed nested fence was reported as a clean register');
    assert.equal(unclosedFence.duplicates[0][0], 140);
    assert.deepEqual(unclosedFence.duplicates[0][1], [5, 6],
        'the duplicate after an unclosed nested fence reported the wrong lines');
    assert.equal(unclosedFence.unrecognised.length, 0,
        'ending a nested fence at the sibling made its own content unreadable');

    // The heading half of the same defect: allocation checks must resume at the list-item boundary.
    const headingAfterBoundary = analyse([
        earlier,
        '- parent item',
        '  ```',
        '  ## [T199] inside an unclosed nested fence',
        '## [T140] top-level heading after the list',
        '- [T140] canonical row',
    ].join('\n'));
    assert.equal(headingAfterBoundary.rows, 2,
        'a top-level heading after an unclosed nested fence changed the canonical row count');
    assert.equal(headingAfterBoundary.duplicates.length, 1,
        'a heading allocation after an unclosed nested fence stayed invisible');
    assert.deepEqual(headingAfterBoundary.duplicates[0][1], [5, 6],
        'the heading after the list-item boundary reported the wrong lines');

    // Every raw HTML block kind that can stay open across a sibling item. Type 6 and type 7 end at a blank
    // line, so they only reach end of file when the sibling follows immediately, which is this shape.
    for (const [name, opener] of Object.entries({
        type1: '  <pre>',
        type6: '  <div>',
        type7: '  <semanticus-card>',
        comment: '  <!--',
        cdata: '  <![CDATA[',
        declaration: '  <!DOCTYPE html',
        instruction: '  <?php',
    })) {
        const result = analyse([earlier, '- parent item', opener, '- [T140] canonical row', '- [T140] duplicate row'].join('\n'));
        assert.equal(result.rows, 3, `${name}: an unclosed nested HTML block swallowed the rows after the sibling`);
        assert.equal(result.duplicates.length, 1,
            `${name}: a duplicate after an unclosed nested HTML block was reported as a clean register`);
        assert.deepEqual(result.duplicates[0][1], [4, 5], `${name}: reported the wrong duplicate lines`);
    }

    // PRESERVED, and each of these is the direction that would invent an allocation out of inert content.
    const blankInsideNestedFence = analyse([
        earlier,
        '- parent item',
        '  ```',
        '',
        '  ## [T140] still inside the nested fence',
        '  ```',
        '- [T140] canonical row',
    ].join('\n'));
    assert.equal(blankInsideNestedFence.rows, 2, 'a blank line inside a nested fence changed the row count');
    assert.equal(blankInsideNestedFence.duplicates.length, 0,
        'a blank line ended a nested fence and exposed its content as an allocation');

    const deeperLineStaysInert = analyse([
        earlier,
        '- parent item',
        '  ```',
        '     ## [T140] fence content indented past the marker',
        '- [T140] canonical row',
    ].join('\n'));
    assert.equal(deeperLineStaysInert.duplicates.length, 0,
        'fence content at or beyond the item content column stopped being inert');

    // A fence marker indented PAST the content column still owns lines back at the content column: they are
    // item content, not a dedent out of the item, so the block must not close there.
    const contentColumnIsNotADedent = analyse([
        earlier,
        '- parent item',
        '     ```',
        '  ## [T140] fence content at the item content column',
        '- [T140] canonical row',
    ].join('\n'));
    assert.equal(contentColumnIsNotADedent.duplicates.length, 0,
        'a line at the item content column closed a deeper fence marker and invented an allocation');

    const topLevelUnclosedFence = analyse([
        '```',
        '- [T140] inert fenced bullet',
        '- [T140] inert fenced bullet again',
    ].join('\n'));
    assert.equal(topLevelUnclosedFence.rows, 0,
        'an unclosed top-level fence stopped being inert: nothing dedents out of a top-level block');
    assert.equal(topLevelUnclosedFence.duplicates.length, 0,
        'an unclosed top-level fence produced duplicate allocations from its own content');

    const topLevelUnclosedHtml = analyse([
        '<pre>',
        '- [T140] inert raw HTML bullet',
        '- [T140] inert raw HTML bullet again',
    ].join('\n'));
    assert.equal(topLevelUnclosedHtml.rows, 0, 'an unclosed top-level HTML block stopped being inert');
    assert.equal(topLevelUnclosedHtml.duplicates.length, 0,
        'an unclosed top-level HTML block produced duplicate allocations from its own content');
}

// ---- 2i. Unicode whitespace entities expose a heading allocation (F-111) -------------------------
//
// Character references decode before inline parsing, so `## &ensp;[T140]` renders with the id as its first
// visible item exactly as `## &nbsp;[T140]` does. The leading-markup loop stripped only ASCII whitespace and
// NBSP, so the wider spaces fell through and the heading was filed as a citation: silent, and free to
// duplicate a canonical row. The decision this register already made is that such decoration fails LOUDLY
// rather than being read as an allocation, and these must reach the same verdict as `&nbsp;` does.
{
    const named = ['ensp', 'emsp', 'thinsp'];
    for (const name of named) {
        const result = analyse([`## &${name};[T140] hidden allocation`, '- [T140] canonical row'].join('\n'));
        assert.equal(result.unrecognised.length, 1, `&${name}; hid a heading allocation without a word`);
        assert.equal(result.unrecognised[0].line, 1);
        assert.equal(result.rows, 1, `&${name}; changed the canonical row count`);
    }

    // Numeric references for the same characters, and the rest of the Unicode space separators a register
    // could carry. Decimal, lower-case hex and upper-case hex all decode from the same root.
    for (const [name, reference] of Object.entries({
        enSpaceDecimal: '&#8194;',
        emSpaceHex: '&#x2003;',
        thinSpaceUpperHex: '&#X2009;',
        hairSpace: '&#8202;',
        oghamSpaceMark: '&#5760;',
        narrowNoBreakSpace: '&#8239;',
        mediumMathematicalSpace: '&#x205F;',
        ideographicSpace: '&#12288;',
    })) {
        const result = analyse([`## ${reference}[T140] hidden allocation`, '- [T140] canonical row'].join('\n'));
        assert.equal(result.unrecognised.length, 1, `${name}: a numeric whitespace reference hid a heading allocation`);
        assert.equal(result.unrecognised[0].line, 1);
    }

    // Not whitespace, but invisible in exactly the same way and already in the entity root, so they hide an
    // id the same way and get the same loud verdict.
    for (const [name, reference] of Object.entries({
        zwnj: '&zwnj;',
        zwj: '&zwj;',
        lrm: '&lrm;',
        rlm: '&rlm;',
        zeroWidthSpace: '&#8203;',
    })) {
        const result = analyse([`## ${reference}[T140] hidden allocation`, '- [T140] canonical row'].join('\n'));
        assert.equal(result.unrecognised.length, 1, `${name}: an invisible leading reference hid a heading allocation`);
    }

    // PRESERVED: a reference that decodes to a VISIBLE character is prose, so the id is not at allocation
    // position and the heading stays an ordinary citation.
    for (const [name, reference] of Object.entries({
        copyright: '&copy;',
        visibleDecimal: '&#65;',
        notAnEntity: '&NBSP;',
    })) {
        const result = analyse([`## ${reference}[T140] citation`, '- [T140] canonical row'].join('\n'));
        assert.equal(result.unrecognised.length, 0, `${name}: a visible leading reference failed loudly`);
        assert.equal(result.duplicates.length, 0, `${name}: a visible leading reference exposed an allocation`);
    }

    // PRESERVED: whitespace decoration in front of PROSE is not a hidden allocation, so it stays quiet.
    const whitespaceThenProse = analyse(['## &emsp;Hello [T140]', '- [T140] canonical row'].join('\n'));
    assert.equal(whitespaceThenProse.unrecognised.length, 0,
        'a wide-space entity in front of prose failed loudly: the id is not at allocation position');
    assert.equal(whitespaceThenProse.duplicates.length, 0);
}

// ---- 2j. entity references stay literal inside code spans (F-112) -------------------------------
//
// Backslash escapes are already literal inside a code span, and character references are literal for the
// same reason: a code span has no inline parsing inside it. The code-span path still decoded bracket
// references, so `` `&#91;T140&#93;` `` was read as an allocation and collided with the canonical row. That
// is a FALSE failure on a citation that is doing nothing wrong, which is the outcome this file guards hardest.
{
    for (const [name, encoded] of Object.entries({
        decimal: '&#91;T140&#93;',
        lowerHex: '&#x5B;T140&#x5D;',
        upperHex: '&#X5B;T140&#X5D;',
        named: '&lbrack;T140&rbrack;',
        openOnly: '&#91;T140]',
        closeOnly: '[T140&#93;',
    })) {
        const single = analyse([`## \`${encoded}\` code span citation`, '- [T140] canonical row'].join('\n'));
        assert.equal(single.duplicates.length, 0,
            `${name}: an encoded bracket citation inside a code span was counted as an allocation`);
        assert.equal(single.rows, 1, `${name}: a code-span citation changed the canonical row count`);
        assert.equal(single.unrecognised.length, 0, `${name}: a code-span citation was reported as unreadable`);

        const double = analyse([`## \`\`${encoded}\`\` code span citation`, '- [T140] canonical row'].join('\n'));
        assert.equal(double.duplicates.length, 0,
            `${name}: an encoded bracket citation inside a double-backtick code span was counted as an allocation`);
    }

    // Whitespace references are literal inside a code span too, so nothing is stripped off the front and the
    // id is not the first character of the content.
    const spacedInCode = analyse(['## `&nbsp;[T140]` code span citation', '- [T140] canonical row'].join('\n'));
    assert.equal(spacedInCode.duplicates.length, 0,
        'a whitespace reference was stripped inside a code span and exposed a task id');
    assert.equal(spacedInCode.unrecognised.length, 0,
        'a whitespace reference inside a code span was reported as unsupported decoration');

    // PRESERVED, both directions of the distinction: a LITERAL bracket inside a code span is still an
    // allocation, and an encoded bracket OUTSIDE a code span is still an allocation.
    const literalInCode = analyse(['## `[T140]` code span', '- [T140] canonical row'].join('\n'));
    assert.equal(literalInCode.duplicates.length, 1,
        'a literal task id inside a code span stopped being an allocation');
    assert.deepEqual(literalInCode.duplicates[0][1], [1, 2]);

    const encodedOutsideCode = analyse(['## &#91;T140&#93; encoded brackets', '- [T140] canonical row'].join('\n'));
    assert.equal(encodedOutsideCode.duplicates.length, 1,
        'encoded brackets outside a code span stopped being an allocation');

    const escapeStillLiteralInCode = analyse([
        `## \`${bs}[T140]\` code span`,
        '- [T140] canonical row',
    ].join('\n'));
    assert.equal(escapeStillLiteralInCode.duplicates.length, 0,
        'a backslash escape stopped being literal inside a code span');
}

// ---- T229 hosted finding: every named alias for the supported space characters ----------------
// The aliases are facts from https://html.spec.whatwg.org/entities.json, which CommonMark 0.31.2
// uses for character references. A known space code point is insufficient if its name is rejected.
// ThickSpace is two spaces, so this also proves the whole decoded sequence stays leading decoration.
{
    const names = ["MediumSpace", "NegativeMediumSpace", "NegativeThickSpace", "NegativeThinSpace", "NegativeVeryThinSpace", "NewLine", "NonBreakingSpace", "Tab", "ThickSpace", "ThinSpace", "VeryThinSpace", "ZeroWidthSpace", "emsp", "emsp13", "emsp14", "ensp", "hairsp", "lrm", "nbsp", "numsp", "puncsp", "rlm", "thinsp", "zwj", "zwnj"];
    for (const name of names) {
        for (const heading of [[`## &${name};[T140] allocation`], [`&${name};[T140] allocation`, '===']]) {
            const result = analyse([...heading, '- [T140] canonical row'].join('\n'));
            assert.equal(result.rows, 1, `${name}: canonical count changed`);
            assert.equal(result.unrecognised.length, 1, `${name}: named space hid the allocation`);
        }
        const literal = analyse([`## \`&${name};[T140]\` literal citation`, '- [T140] canonical row'].join('\n'));
        assert.equal(literal.duplicates.length, 0, `${name}: entity decoded inside a code span`);
        assert.equal(literal.unrecognised.length, 0, `${name}: literal citation became an unreadable allocation`);
    }
    for (const name of ['HAIRSP', 'Thinspace', 'MEDIUMSPACE', 'notARealSpace']) {
        const result = analyse([`## &${name};[T140] prose citation`, '- [T140] canonical row'].join('\n'));
        assert.equal(result.duplicates.length, 0, `${name}: case-sensitive or unknown entity became an allocation`);
        assert.equal(result.unrecognised.length, 0, `${name}: unknown entity became decoration`);
    }
}

// ---- T229 hosted finding: block indentation is relative to the list container ----------------
{
    for (const [marker, column] of [['- ', 2], [' - ', 3], ['1. ', 3], ['10. ', 4]]) {
        for (const extra of [0, 1, 2, 3]) {
            for (const [open, close] of [['~~~', '~~~'], ['```', '```'], ['<pre>', '</pre>'], ['<!--', '-->'], ['<div>', '']]) {
                const pad = ' '.repeat(column);
                const lines = [marker + 'parent', ' '.repeat(column + extra) + open,
                    pad + '- [T140] literal inside block', pad + close,
                    '- [T140] canonical row', '## [T141] real heading', '- [T141] real duplicate'];
                const result = analyse(lines.join('\n'));
                assert.equal(result.rows, 2, `${marker}/${extra}/${open}: nested literal became a row`);
                assert.deepEqual(result.duplicates.map(([id]) => id), [141], `${marker}/${extra}/${open}: false duplicate or hidden real heading`);
            }
        }
    }
}

// ---- 3. a DATED log citation is never a definition ----------------------------------------------
// This is the distinction the whole check rests on. Get it wrong the other way and the append-only DONE log
// reads as hundreds of duplicates.
const log = analyse([
    '- [T140] The live row.',
    '## Done',
    '- 2026-07-22 [T140] shipped it.',
    '- 2026-07-21 [T140] and mentioned it again.',
    '    - 2026-07-20 [T140] indented citation.',
].join('\n'));
assert.equal(log.rows, 1, 'a dated DONE-log citation was counted as a definition');
assert.equal(log.duplicates.length, 0, 'dated citations were reported as duplicate definitions');

// ---- 4. partial blindness FAILS, rather than passing over the part it understands ---------------
// A row that puts an id ahead of every letter is in definition position whatever decorates it. If the
// definition rule cannot read it, the check is only partly reading the file, and it must say so rather than
// report a clean run over the part it happens to understand.
//
// `==highlight==` is the case here: it is markdown decoration, it is non-alphabetic so the crude reading sees
// it, and it is deliberately NOT in the definition rule's set. The remedy when this fires is to add the shape
// on purpose, which is the whole point: an unanticipated shape becomes a decision instead of a silent gap.
const blind = analyse(['- [T140] Fine.', '- ==[T141]== Highlighted, a decoration the rule does not know.'].join('\n'));
assert.equal(blind.unrecognised.length, 1,
    'a definition-position id in an unknown row shape was silently ignored: partial blindness passes');
assert.equal(blind.unrecognised[0].line, 2);
assert.equal(blind.rows, 1, 'the recognised row must still be counted while the unknown one is reported');

// ---- 5. the reported reference count is cross-checked, not decorative ---------------------------
// The comment claimed the count was evidence the row-versus-reference rule still splits the file, and nothing
// compared it to anything. Every DEFINED id must also be seen by the reference scanner, or the two patterns
// have drifted apart.
const both = analyse(['- [T140] a', '- [T141] b', '- 2026-07-22 [T99] shipped'].join('\n'));
assert.deepEqual(both.orphans, [99], 'the reference scanner and the definition scanner disagree');

// ---- 6. total blindness still fails --------------------------------------------------------------
assert.equal(analyse('nothing here at all').rows, 0, 'an empty register must report zero definition rows');

// ---- 7. ids are matched exactly, not by prefix ---------------------------------------------------
const distinct = analyse(['- [T14] a', '- [T140] b', '- [T1400] c'].join('\n'));
assert.equal(distinct.duplicates.length, 0, 'T14, T140 and T1400 are three different ids');
assert.deepEqual([...distinct.definitions.keys()].sort((a, b) => a - b), [14, 140, 1400]);

// ---- 8. THE ENTRY POINT, not just the parser ----------------------------------------------------
//
// analyse() was the only thing under test, so main() - the thing the workflow actually runs, and the only
// place the verdict becomes an exit code - was untested. Change `if (duplicates.length > 0)` to `if (false)`
// and a two-row T140 register exits 0 printing "Every task id is defined exactly once", with the parser test
// and the workflow both green. Verifying the reader and trusting the verdict, one last time.
{
    const dir = mkdtempSync(join(tmpdir(), 'register-'));
    try {
        const say = [];
        const run = (name, text) => {
            const path = join(dir, name);
            writeFileSync(path, text);
            say.length = 0;
            const code = main(path, line => say.push(String(line)), line => say.push(String(line)));
            return { code, output: say.join('\n') };
        };

        const clean = run('clean.md', ['- [T140] a', '- [T141] b', '', '- 2026-07-22 [T99] shipped'].join('\n'));
        assert.equal(clean.code, 0, 'a clean register must exit 0');
        assert.match(clean.output, /Every task id is allocated exactly once/u);

        const dup = run('dup.md', ['- [T140] a', '- [T140] b'].join('\n'));
        assert.equal(dup.code, 1, 'main returned 0 for a register with a duplicate id: the verdict never fails');
        assert.match(dup.output, /T140 is allocated 2 times, on lines 1, 2/u, 'the exit code failed but the report did not name it');
        assert.doesNotMatch(dup.output, /Every task id is allocated exactly once/u,
            'main reported success text alongside a failing exit code');

        const headingDup = run('heading-dup.md', ['## [T140] PROGRAMME HEADING', '- [T140] a'].join('\n'));
        assert.equal(headingDup.code, 1,
            'main returned 0 for a heading plus unique bullet of the same id: the F-018 residual never fails');
        assert.match(headingDup.output, /T140 is allocated 2 times, on lines 1, 2/u,
            'the heading plus bullet pair failed without naming both lines');
        assert.doesNotMatch(headingDup.output, /Every task id is allocated exactly once/u,
            'main reported success text alongside a heading-plus-bullet failure');

        const setextDup = run('setext-dup.md', ['[T140] PROGRAMME HEADING', '=======================', '- [T140] a'].join('\n'));
        assert.equal(setextDup.code, 1, 'main returned 0 for a Setext heading plus unique bullet');
        assert.match(setextDup.output, /T140 is allocated 2 times, on lines 1, 3/u,
            'the Setext plus bullet pair failed without naming the content line');

        const multiSetextDup = run('multi-setext.md', [
            '[T140] PROGRAMME',
            'HEADING CONTINUED',
            '=======================',
            '- [T140] a',
        ].join('\n'));
        assert.equal(multiSetextDup.code, 1, 'main returned 0 for a multiline Setext heading plus unique bullet');
        assert.match(multiSetextDup.output, /T140 is allocated 2 times, on lines 1, 4/u,
            'the multiline Setext plus bullet pair failed without naming the first content line');

        const fencedOk = run('fenced.md', ['```', '## [T140] PROGRAMME HEADING', '```', '- [T140] a'].join('\n'));
        assert.equal(fencedOk.code, 0, 'main failed a fenced heading as a duplicate allocation');

        const dateOk = run('date-heading.md', ['## 2026-07-22 [T140] shipped', '- [T140] a'].join('\n'));
        assert.equal(dateOk.code, 0, 'main failed a date-first heading as a duplicate allocation');

        const htmlLoud = run('html-heading.md', ['## <em>[T140]</em> PROGRAMME HEADING', '- [T140] a'].join('\n'));
        assert.equal(htmlLoud.code, 1, 'main returned 0 for HTML heading decoration: silence');
        assert.match(htmlLoud.output, /allocation position|HTML or entity/u);

        const entityLoud = run('entity-heading.md', ['## &nbsp;[T140] PROGRAMME HEADING', '- [T140] a'].join('\n'));
        assert.equal(entityLoud.code, 1, 'main returned 0 for entity heading decoration: silence');
        assert.match(entityLoud.output, /allocation position|HTML or entity/u);

        const htmlNoIdOk = run('html-noid.md', ['## <em>Hello</em> world', '- [T140] a'].join('\n'));
        assert.equal(htmlNoIdOk.code, 0, 'main failed a no-id HTML heading as unsupported');

        const htmlBlockOk = run('html-block.md', ['<div>', '## [T140] PROGRAMME HEADING', '</div>', '', '- [T140] a'].join('\n'));
        assert.equal(htmlBlockOk.code, 0, 'main failed a raw HTML block heading as a duplicate allocation');

        const escapedDup = run('escaped.md', [`## ${bs}[T140] PROGRAMME HEADING`, '- [T140] a'].join('\n'));
        assert.equal(escapedDup.code, 1, 'main returned 0 for an escaped leading task id plus unique bullet');
        assert.match(escapedDup.output, /T140 is allocated 2 times, on lines 1, 2/u);

        // T229 at the entry point, because the exit code is where these three actually cost something.
        // F-110's damage is not that the parser is wrong, it is that main() prints the success line and
        // exits 0 over rows it stopped reading, and F-112's is that main() fails a citation doing nothing
        // wrong. Neither is visible from analyse() alone.
        const nestedFenceDup = run('nested-fence.md', [
            '- [T100] an earlier canonical row',
            '- parent item',
            '  ```',
            '  ## [T199] inside an unclosed nested fence',
            '- [T140] canonical row',
            '- [T140] duplicate row',
        ].join('\n'));
        assert.equal(nestedFenceDup.code, 1,
            'main returned 0 for a duplicate below an unclosed nested fence: the silent pass never fails');
        assert.match(nestedFenceDup.output, /T140 is allocated 2 times, on lines 5, 6/u,
            'the duplicate below an unclosed nested fence failed without naming both lines');
        assert.doesNotMatch(nestedFenceDup.output, /Every task id is allocated exactly once/u,
            'main printed the success line over rows it had stopped reading');

        const nestedHtmlDup = run('nested-html.md', [
            '- [T100] an earlier canonical row',
            '- parent item',
            '  <pre>',
            '- [T140] canonical row',
            '- [T140] duplicate row',
        ].join('\n'));
        assert.equal(nestedHtmlDup.code, 1,
            'main returned 0 for a duplicate below an unclosed nested raw HTML block');
        assert.match(nestedHtmlDup.output, /T140 is allocated 2 times, on lines 4, 5/u);

        const wideSpaceLoud = run('wide-space.md', ['## &emsp;[T140] PROGRAMME HEADING', '- [T140] a'].join('\n'));
        assert.equal(wideSpaceLoud.code, 1,
            'main returned 0 for a heading allocation hidden behind a wide-space entity: silence');
        assert.match(wideSpaceLoud.output, /allocation position|HTML or entity/u);

        const codeSpanCitationOk = run('code-span-citation.md', [
            '## `&#91;T140&#93;` code span citation',
            '- [T140] canonical row',
        ].join('\n'));
        assert.equal(codeSpanCitationOk.code, 0,
            'main failed an encoded bracket citation inside a code span as a duplicate allocation');
        assert.match(codeSpanCitationOk.output, /Every task id is allocated exactly once/u);

        const blindMain = run('blind.md', ['- [T140] a', '- ==[T141]== b'].join('\n'));
        assert.equal(blindMain.code, 1, 'main returned 0 despite a row it could not read');
        assert.match(blindMain.output, /allocation position/u);

        const empty = run('empty.md', 'no rows at all');
        assert.equal(empty.code, 1, 'main returned 0 for a register it could not parse at all');
        assert.match(empty.output, /No definition rows found at all/u);
    } finally {
        rmSync(dir, { recursive: true, force: true });
    }
}

console.log('task register: every row shape is recognised, dated citations are not definitions, partial');
console.log('              blindness fails, and main() turns each verdict into the right exit code.');
