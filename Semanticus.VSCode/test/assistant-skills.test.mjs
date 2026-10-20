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

// Hero 24 D11: Connect Claude Code put the 7 skills in place but not the student of the Copilot test.
test('Claude Code gets the copilot-student agent with the skills, updated on update, never over a file the person changed', () => {
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'semanticus-agent-install-'));
  try {
    const skills = path.join(temp, 'skills'); const agents = path.join(temp, 'agents');
    const result = installAssistantSkills(pack.packRoot, skills, agents);
    assert.equal(result.installed.length, 7);
    const target = path.join(agents, 'copilot-student.md');
    const shipped = fs.readFileSync(path.join(pack.packRoot, pack.agents[0].path), 'utf8');
    assert.equal(fs.readFileSync(target, 'utf8'), shipped);
    assert.deepEqual(result.agentsInstalled, ['copilot-student']);
    // an earlier Semanticus copy is replaced on update
    fs.writeFileSync(target, 'Older student');
    const receiptPath = path.join(agents, '.semanticus-agents.json');
    const receipt = JSON.parse(fs.readFileSync(receiptPath, 'utf8'));
    receipt.files['copilot-student'] = createHash('sha256').update('Older student').digest('hex');
    fs.writeFileSync(receiptPath, JSON.stringify(receipt));
    assert.deepEqual(installAssistantSkills(pack.packRoot, skills, agents).agentsInstalled, ['copilot-student']);
    assert.equal(fs.readFileSync(target, 'utf8'), shipped);
    // the person's own change is kept
    fs.writeFileSync(target, 'My student');
    assert.deepEqual(installAssistantSkills(pack.packRoot, skills, agents).agentsPreserved, ['copilot-student']);
    assert.equal(fs.readFileSync(target, 'utf8'), 'My student');
    // Codex has no agents folder: with none given nothing is written beside the skills
    const codex = path.join(temp, 'codex');
    assert.equal(installAssistantSkills(pack.packRoot, codex).agentsInstalled.length, 0);
    assert.equal(fs.existsSync(path.join(temp, 'agents2')), false);
  } finally { fs.rmSync(temp, { recursive: true, force: true }); }
});

test('the skills and the server words never say a publish cannot be undone, and never leave "is it in git" to a guess (UAT 17 Oct)', () => {
  const repo = path.resolve(root, '..');
  const texts = {
    skill: fs.readFileSync(path.join(pack.packRoot, 'plugins/semanticus/skills/semanticus/SKILL.md'), 'utf8'),
    interview: fs.readFileSync(path.join(pack.packRoot, 'plugins/semanticus/skills/semanticus-interview-model/SKILL.md'), 'utf8'),
    server: fs.readFileSync(path.join(repo, 'Semanticus.Engine/Program.cs'), 'utf8'),
    defaults: fs.readFileSync(path.join(repo, 'Semanticus.Engine/Plan/AssistantDefaults.cs'), 'utf8'),
  };
  for (const [name, text] of Object.entries(texts)) {
    assert.ok(!/publish[^.]*(?:cannot|can't|never) (?:be )?(?:undone|take[n]? back)/i.test(text.replace(/never (?:tell the person|say) a publish cannot be undone/gi, '')), `${name} says a publish cannot be undone`);
    assert.ok(!/only if it is in git|when it is in git or you made a backup/i.test(text), `${name} leaves "in git" to a guess`);
  }
  assert.match(texts.skill, /Undo publish/);
  assert.match(texts.server, /Undo publish/);
  // fix2-publish-role replaced "read inGit, then choose where to save" with one rule: save_model and no path writes the model's own file.
  for (const name of ['skill', 'server', 'defaults']) assert.match(texts[name], /save_model and no path/i, `${name} tells the assistant to save with no path`);
  assert.match(texts.interview, /Publish, then test/);
});
