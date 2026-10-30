// UAT 1.7 Free, D3 and D11: opening a published model again offers the saved copy that holds more, and switching models with unsaved work offers
// "Save and open" as the main button. The engine half is Semanticus.Tests/SavedCopyOfferTests.cs; this pins the two screens that show it.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const extension = readFileSync(resolve(root, 'src', 'extension.ts'), 'utf8');
const hub = readFileSync(resolve(root, 'webview', 'src', 'connectionshub.tsx'), 'utf8');

const offer = extension.slice(extension.indexOf('async function offerSavedCopy'), extension.indexOf('async function finishOpen'));
assert.match(offer, /Open the saved copy/, 'the offer names the saved copy');
assert.match(offer, /Keep the service copy/, 'the person can keep the service copy');
assert.match(offer, /confirmAndSendOpen<OpenResult>\('open'/, 'taking the offer opens the file the usual guarded way');
assert.doesNotMatch(offer, /—/, 'no em dash in the offer');
assert.match(extension, /void offerSavedCopy\(r\.savedCopy\)/, 'an open from a command offers the saved copy');
assert.match(extension, /msg\.method === 'openLive'\) void offerSavedCopy\(result\?\.savedCopy\)/, 'an open from the Connections page offers the saved copy');
assert.match(extension, /function noteSavedCopyLeftAlone[\s\S]{0,200}was left as it was[\s\S]{0,80}showInformationMessage/, 'a save that left a saved copy alone says so where it is read');
assert.match(extension, /noteSavedCopyLeftAlone\(r\.note\)/, 'the save command says it');

const confirmCard = hub.slice(hub.indexOf('data-testid="hub-open-unsaved"'), hub.indexOf('{createOpen && ('));
assert.match(confirmCard, /data-testid="hub-open-save"[\s\S]*?>Save and open</, 'the unsaved-work card offers Save and open');
assert.match(confirmCard, /className=\{BTN_PRIMARY\} data-testid="hub-open-save"/, 'Save and open is the main button');
assert.match(confirmCard, /className=\{openSaveFormat \? BTN : BTN_PRIMARY\} data-testid="hub-open-discard"/, 'Discard is the main button only when there is no file to save to');
assert.match(hub, /rpc\('save', null, openSaveFormat, false\)/, 'Save and open saves the model the person has open');

console.log('saved copy offer wiring tests passed');
