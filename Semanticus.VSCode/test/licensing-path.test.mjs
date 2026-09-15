import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const compiledPath = resolve(root, 'out', 'extension.js');
assert.ok(existsSync(compiledPath), 'out/extension.js is missing. Run npm run compile first.');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const extension = read('src/extension.ts');
const compiledExtension = read('out/extension.js');
const bridge = read('webview/src/bridge.ts');
const pro = read('webview/src/pro.tsx');
const app = read('webview/src/App.tsx');
const pkg = JSON.parse(read('package.json'));
const entitlement = read('../Semanticus.Engine/Entitlement/IEntitlement.cs');
const license = read('../Semanticus.Engine/Entitlement/LicenseEntitlement.cs');

assert.ok(pkg.contributes.commands.some((x) => x.command === 'semanticus.manageLicense' && x.title === 'Pro Plans and Support'),
  'the account pathway must remain available from the command palette');
assert.match(extension, /registerCommand\('semanticus\.manageLicense',[\s\S]*manageLicenseCmd\(\)/,
  'the native command must drive the shared management function');
assert.match(extension, /async function manageLicenseCmd[\s\S]*getEntitlement[\s\S]*manageUrl[\s\S]*\^https:[\s\S]*openExternal/,
  'account management must prefer the engine URL, require HTTPS and open outside the extension');
assert.match(extension, /Semanticus Pro is active/,
  'Show License must report an active Pro subscription');
assert.doesNotMatch(extension, /'Pro options'[\s\S]{0,80}manageLicenseCmd/,
  'Show License must not send an active Pro subscriber to the marketing page');
assert.match(extension, /Semanticus is on the free tier[\s\S]*'Upgrade to Pro'[\s\S]*manageLicenseCmd\(info\)/,
  'Show License must route free users to upgrade');
assert.match(extension, /msg\?\.type === 'manageLicense'[\s\S]*semanticus\.manageLicense/,
  'the webview must reach the same native command, not own a billing URL');
assert.match(bridge, /function manageLicense\(\)[\s\S]*type: 'manageLicense'/,
  'the bridge must expose a URL-free account action');
assert.match(pro, /function LicenseButton\(\)[\s\S]*Pro options[\s\S]*Upgrade to Pro/,
  'Studio must show tier-appropriate Pro-page copy');
// Still a refresh on reconnect, but it INVALIDATES first. Dropping the cached promise alone left the old
// grant published, so between a licence lapsing and the new answer landing every Pro page stayed mounted and
// kept calling paid operations (Astra, 2026-09-15). The refresh is now three steps and all three matter.
assert.match(pro, /onReconnect\(\(\) => \{[\s\S]{0,200}?generation\+\+;[\s\S]{0,120}?tierPromise = null;[\s\S]{0,160}?publish\(\{ tier: 'unknown', features: \[\] \}\);[\s\S]{0,80}?void fetchTier\(\);/,
  'Studio must invalidate the grant, then refresh the tier, after activation restarts the engine');
assert.match(pro, /function UpsellNotice[\s\S]*onClick=\{manageLicense\}[\s\S]*Upgrade to Pro/,
  'every contextual Pro invitation must contain a direct upgrade action');
assert.doesNotMatch(app, /<LicenseButton \/>/, 'the shell keeps licensing inside Help instead of competing with the five-area navigation');
assert.match(entitlement, /ManageUrl[^\n]*LicenseEntitlement\.ManageUrl/,
  'the entitlement DTO must carry the engine-owned management URL through both doors');
assert.match(license, /public const string ManageUrl = "https:\/\/[^"]+";[\s\S]*public const string RenewUrl = ManageUrl/,
  'renewal and account management must remain one web destination');

assert.match(extension, /DEFAULT_LICENSE_MANAGE_URL = 'https:\/\/semanticus\.com\.au\/pro'/,
  'the older-engine fallback must open the live Semanticus Pro page');
assert.match(compiledExtension, /DEFAULT_LICENSE_MANAGE_URL = 'https:\/\/semanticus\.com\.au\/pro'/,
  'the packaged extension fallback must open the live Semanticus Pro page');
assert.match(license, /public const string ManageUrl = "https:\/\/semanticus\.com\.au\/pro";/,
  'the engine-owned account door must open the live Semanticus Pro page');
assert.doesNotMatch(extension + compiledExtension + license, /semanticus\.dev/,
  'the account pathway must not regress to the unregistered semanticus.dev host');

console.log('licensing pathway tests passed');
