// Pure unit tests for the display-folder math (src/folders.ts -> out/folders.js). Run: `npm test` (node,
// no VS Code host). Pins the two review-fix invariants that the native tree can't screenshot:
//   1) folder sorting is case-insensitive (matches the case-insensitive grouping + docs), and
//   2) reveal-by-ref reconstructs a parent chain whose every ancestor ref is a folder node the tree ACTUALLY
//      renders — the regression Codex flagged (cold-tree reveal fell back to the table because getParent
//      couldn't resolve a foldered member's ancestry). We simulate getParent's PURE ancestry walk (the async
//      engine lookup only yields the member's displayFolder, which we supply) and assert each ancestor ref
//      matches what groupFolderLevel emits at its parent level. The live F5 reveal is Kane's.
import assert from 'node:assert/strict';
import {
    normFolder, folderParts, folderRef, parentFolderPath, leafFolderName, groupFolderLevel,
} from '../out/folders.js';

let passed = 0;
const test = (name, fn) => { fn(); passed++; console.log('  [PASS] ' + name); };

// A stand-in for the tree's flat engine fan.
const member = (ref, displayFolder) => ({ ref, name: ref.split('/').pop(), kind: 'measure', hasChildren: false, displayFolder });

test('normFolder strips whitespace and stray separators', () => {
    assert.equal(normFolder('  \\Fin\\Sub\\ '), 'Fin\\Sub');
    assert.equal(normFolder(undefined), '');
    assert.equal(normFolder('KPIs'), 'KPIs');
});

test('folderParts round-trips with folderRef (path may nest)', () => {
    assert.deepEqual(folderParts('dfolder:Sales/KPIs\\Core'), { table: 'Sales', path: 'KPIs\\Core' });
    assert.equal(folderRef('Sales', 'KPIs\\Core'), 'dfolder:Sales/KPIs\\Core');
    const { table, path } = folderParts(folderRef('Sales', 'A\\B'));
    assert.equal(table, 'Sales'); assert.equal(path, 'A\\B');
});

test('parentFolderPath / leafFolderName walk the folder chain', () => {
    assert.equal(parentFolderPath('KPIs\\Growth'), 'KPIs');
    assert.equal(parentFolderPath('KPIs'), '');
    assert.equal(leafFolderName('KPIs\\Growth'), 'Growth');
    assert.equal(leafFolderName('KPIs'), 'KPIs');
});

test('groupFolderLevel sorts subfolders case-insensitively (alpha before Zebra)', () => {
    const kids = [member('measure:T/a', 'Zebra'), member('measure:T/b', 'alpha')];
    const level = groupFolderLevel(kids, 'T', '');
    const folderNames = level.filter((n) => n.kind === 'dfolder').map((n) => n.name);
    assert.deepEqual(folderNames, ['alpha', 'Zebra']);   // NOT ['Zebra','alpha'] (default case-sensitive order)
});

test('groupFolderLevel: subfolders first, then direct members in engine order', () => {
    const kids = [
        member('measure:T/root1', ''),          // at the table root
        member('measure:T/kpi', 'KPIs'),         // one level down
        member('measure:T/deep', 'KPIs\\Core'),  // two levels down
    ];
    const root = groupFolderLevel(kids, 'T', '');
    assert.deepEqual(root.map((n) => n.ref), ['dfolder:T/KPIs', 'measure:T/root1']);   // folder, then direct member
    const inKpis = groupFolderLevel(kids, 'T', 'KPIs');
    assert.deepEqual(inKpis.map((n) => n.ref), ['dfolder:T/KPIs\\Core', 'measure:T/kpi']);
});

// Mirror getParent's PURE ancestry (the async lookup only supplies the member's displayFolder).
function parentChain(table, memberRef, displayFolder) {
    const chain = [];
    const df = normFolder(displayFolder);
    if (df) {
        let path = df;
        chain.push(folderRef(table, path));            // deepest folder the member sits in
        while (parentFolderPath(path)) { path = parentFolderPath(path); chain.push(folderRef(table, path)); }
    }
    chain.push('table:' + table);
    return chain;   // member -> deepest folder -> ... -> shallowest folder -> table
}

test('reveal chain: every ancestor of a nested foldered member is a node the tree renders', () => {
    const memberRef = 'measure:T/deep';
    const df = 'KPIs\\Core';
    const kids = [member(memberRef, df), member('measure:T/other', 'KPIs')];
    const chain = parentChain('T', memberRef, df);
    assert.deepEqual(chain, ['dfolder:T/KPIs\\Core', 'dfolder:T/KPIs', 'table:T']);

    // Each folder ancestor must be a real rendered node at its PARENT level (so treeView.reveal can match it).
    // dfolder:T/KPIs is rendered at the table root; dfolder:T/KPIs\Core is rendered inside KPIs; the member
    // itself is a direct child of KPIs\Core. Cold-tree reveal walked to the table before the fix — now it lands.
    const atRoot = groupFolderLevel(kids, 'T', '').map((n) => n.ref);
    assert.ok(atRoot.includes('dfolder:T/KPIs'), 'KPIs folder rendered at root');
    const inKpis = groupFolderLevel(kids, 'T', 'KPIs').map((n) => n.ref);
    assert.ok(inKpis.includes('dfolder:T/KPIs\\Core'), 'KPIs\\Core folder rendered inside KPIs');
    const inCore = groupFolderLevel(kids, 'T', 'KPIs\\Core').map((n) => n.ref);
    assert.ok(inCore.includes(memberRef), 'the member is a direct child of KPIs\\Core');
});

test('reveal chain: an unfoldered member parents straight to its table', () => {
    assert.deepEqual(parentChain('T', 'measure:T/x', ''), ['table:T']);
    assert.deepEqual(parentChain('T', 'measure:T/x', '  \\  '), ['table:T']);   // stray-only path = root
});

console.log(`\nfolders.test.mjs: ${passed} passed`);
