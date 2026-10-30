// UAT 1.7 Free, D7: Claude called save_model on a live model, which gave it a file. The DAX editor tab opened before that carries the model's session id as
// its key; the live key is now the file. Ctrl+S said "This editor belongs to a different model session" and said it again after a reopen. The same open
// model must still save; an editor from another model must still be refused.
import assert from 'node:assert/strict';
import { decideDaxSave, guardModelMatch, identityToken } from '../out/daxHeader.js';

let passed = 0;
const test = (name, fn) => { fn(); passed++; console.log('  [PASS] ' + name); };

const session = identityToken('s123abc');
const file = identityToken('/home/u/Semanticus/Model/Sales.bim');
const other = identityToken('/home/u/Semanticus/Model/Other.bim');

test('an editor opened before the first save still saves once the model has a file', () => {
    const d = decideDaxSave('measure:Sales/Total', '', '1+1', { isHeaderUri: false, uriModelKey: session, modelKey: file, earlierModelKey: session });
    assert.equal(d.kind, 'body');
});
test('a create-then-edit header editor from before the save saves too', () => {
    const d = decideDaxSave('measure:Sales/New Measure', "MEASURE 'Sales'[New Measure] =", '1', { isHeaderUri: true, uriModelKey: session, modelKey: file, earlierModelKey: session });
    assert.equal(d.kind, 'header');
});
test('an editor minted after the save (the file key) saves', () => {
    assert.equal(guardModelMatch(file, file, session).ok, true);
});
test('an editor from another model is still refused', () => {
    const d = decideDaxSave('measure:Sales/Total', '', '1', { isHeaderUri: false, uriModelKey: other, modelKey: file, earlierModelKey: session });
    assert.equal(d.kind, 'reject');
    assert.equal(guardModelMatch(identityToken('sOTHER'), file, session).ok, false);
});
test('with no earlier key the old rule holds', () => {
    assert.equal(guardModelMatch(session, file).ok, false);
    assert.equal(guardModelMatch(undefined, file, session).ok, false);
});
console.log(`${passed} passed`);
