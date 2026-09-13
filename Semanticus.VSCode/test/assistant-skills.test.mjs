import assert from 'node:assert/strict';
import { test } from 'node:test';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import { installAssistantSkills } from '../out/assistantSkills.js';
import { prepareAssistantPack } from '../scripts/assistant-pack.mjs';

const root = fileURLToPath(new URL('..', import.meta.url));
const pack = prepareAssistantPack({ check: true });
const manifest = JSON.parse(fs.readFileSync(path.join(root, 'package.json'), 'utf8'));

test('seven customer skills match the Copilot registration and plugin versions', () => {
  assert.equal(pack.skills.length, 7);
  assert.deepEqual(manifest.contributes.chatSkills.map(item => item.path).sort(),
    pack.skills.map(item => `./assistant-pack/${item.path}`).sort());
  assert.equal(pack.version, manifest.version);
  for (const client of ['claude', 'codex']) {
    const plugin = JSON.parse(fs.readFileSync(path.join(pack.packRoot, `plugins/semanticus/.${client}-plugin/plugin.json`), 'utf8'));
    assert.equal(plugin.version, pack.version);
    assert.equal(plugin.name, 'semanticus');
    assert.equal(plugin.mcpServers, undefined);
  }
});

test('installation updates owned skills and preserves user changes and unrelated files', () => {
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'semanticus-skill-install-'));
  try {
    assert.equal(installAssistantSkills(pack.packRoot, temp).installed.length, 7);
    const userFile = path.join(temp, 'semanticus', 'SKILL.md');
    const custom = fs.readFileSync(userFile, 'utf8') + '\nMy project preferences.\n';
    fs.writeFileSync(userFile, custom);
    fs.mkdirSync(path.join(temp, 'unrelated'));
    fs.writeFileSync(path.join(temp, 'unrelated', 'SKILL.md'), 'User skill');
    const oldOwnedFile = path.join(temp, pack.skills[1].name, 'SKILL.md');
    fs.writeFileSync(oldOwnedFile, 'Previously installed Semanticus guide');
    const receiptPath = path.join(temp, '.semanticus-skills.json');
    const receipt = JSON.parse(fs.readFileSync(receiptPath, 'utf8'));
    receipt.version = '1.1.2';
    receipt.files[pack.skills[1].name] = createHash('sha256').update(fs.readFileSync(oldOwnedFile)).digest('hex');
    fs.writeFileSync(receiptPath, JSON.stringify(receipt));
    const updated = installAssistantSkills(pack.packRoot, temp);
    assert.equal(updated.installed.length, 6);
    assert.deepEqual(updated.preserved, ['semanticus']);
    assert.equal(fs.readFileSync(userFile, 'utf8'), custom);
    assert.equal(fs.readFileSync(path.join(temp, 'unrelated', 'SKILL.md'), 'utf8'), 'User skill');
    assert.equal(fs.readFileSync(oldOwnedFile, 'utf8'), fs.readFileSync(path.join(pack.packRoot, pack.skills[1].path), 'utf8'));
  } finally { fs.rmSync(temp, { recursive: true, force: true }); }
});

test('unowned skills survive and corrupt bundled bytes fail before installation', () => {
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'semanticus-skill-conflict-'));
  try {
    const dest = path.join(temp, 'installed');
    fs.mkdirSync(path.join(dest, 'semanticus'), { recursive: true });
    fs.writeFileSync(path.join(dest, 'semanticus/SKILL.md'), 'Custom guide');
    assert.deepEqual(installAssistantSkills(pack.packRoot, dest).preserved, ['semanticus']);
    const copy = path.join(temp, 'pack');
    fs.cpSync(pack.packRoot, copy, { recursive: true });
    fs.appendFileSync(path.join(copy, pack.skills[0].path), 'corrupt');
    const untouched = path.join(temp, 'untouched');
    assert.throws(() => installAssistantSkills(copy, untouched), /incomplete/);
    assert.equal(fs.existsSync(untouched), false);
  } finally { fs.rmSync(temp, { recursive: true, force: true }); }
});
