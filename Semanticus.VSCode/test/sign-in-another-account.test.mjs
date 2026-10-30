// si1 (Kane, 29 Oct, on 1.7.0-rc23 and the store 1.6.0): "I'm not able to sign in another account".
// With one saved browser account, the Connections page sends every "add an account" press to the workspace picker,
// and the picker can only read its list as the saved account: listWorkspaces(authMode, tenant) carries no account
// choice, so the engine pins the saved record (EntraToken.AcquireFabricAsync, BuildCredential with skipSavedRecord
// false). A UAT lane showed the picker header "as <saved account>" right after "Sign in another account", and the
// browser sign-in it starts is locked to the saved account's tenant. These checks pin the way out on the screen.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (...p) => readFileSync(resolve(root, ...p), 'utf8');
const hub = read('webview', 'src', 'connectionshub.tsx');
const pickers = read('webview', 'src', 'connectpickers.tsx');

// The picker offers its own "Use another account", next to the account it lists as.
assert.match(pickers, /data-testid="pick-another-account"/,
  'the workspace picker must offer "Use another account": with a saved account it otherwise lists only that account');
// That press asks the engine for a NEW sign-in, not a list as the saved account.
assert.match(pickers, /'listWorkspaces', authMode, tenantArg\(\), [^)]*anotherAccount/,
  'the picker must pass the another-account choice to listWorkspaces');
// The account menu's "Sign in another account" with no published model in play must open the picker in that mode,
// never the plain picker that silently reuses the saved account.
assert.doesNotMatch(hub, /data-testid="hub-sign-in-another-top"[\s\S]{0,140}else openPicker\(\)/,
  '"Sign in another account" must not open the plain picker, which lists as the saved account');

console.log('sign-in-another-account: ok');
