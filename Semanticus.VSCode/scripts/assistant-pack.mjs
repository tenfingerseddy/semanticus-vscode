import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { pipeline } from 'node:stream/promises';
import yazl from 'yazl';

const extRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const repoRoot = path.dirname(extRoot);
export const SKILLS = ['semanticus', 'optimize-dax', 'ai-ready', 'interview-model', 'model-pr', 'distill-workflow', 'curate-knowledge'];
export const publicName = name => name === 'semanticus' ? name : `semanticus-${name}`;
export const digest = body => createHash('sha256').update(body).digest('hex');

export function prepareAssistantPack({ check = false } = {}) {
  const version = JSON.parse(fs.readFileSync(path.join(extRoot, 'package.json'), 'utf8')).version;
  const packRoot = path.join(extRoot, 'assistant-pack');
  const files = new Map();
  const putJson = (name, value) => files.set(name, JSON.stringify(value, null, 2) + '\n');
  const skills = [];
  // Public snapshots carry these generated files. Only the private authoring tree has the source skills.
  const sourceRoot = path.join(repoRoot, '.claude', 'skills');
  const hasSources = fs.existsSync(path.join(sourceRoot, 'semanticus', 'SKILL.md'));
  for (const source of SKILLS) {
    const name = publicName(source);
    const relative = `plugins/semanticus/skills/${name}/SKILL.md`;
    let body = fs.readFileSync(hasSources ? path.join(sourceRoot, source, 'SKILL.md') : path.join(packRoot, relative), 'utf8').replace(/\r\n/g, '\n');
    if (hasSources) body = body.replace(`name: ${source}\n`, `name: ${name}\n`);
    if (!body.startsWith(`---\nname: ${name}\n`) || !body.includes('\ndescription: ') || /[\x00-\x08\x0b\x0c\x0e-\x1f]/.test(body)) {
      throw new Error(`Invalid customer skill: ${name}`);
    }
    if (body.includes('docs/learning-loop-plan') || body.includes('TASKS.md')) throw new Error(`Internal reference in ${name}`);
    files.set(relative, body);
    skills.push({ name, path: relative, sha256: digest(body) });
  }
  const metadata = {
    name: 'semanticus', version,
    description: 'Use Semanticus to understand, edit, test and publish Power BI and Fabric semantic models.',
    author: { name: 'Semanticus', url: 'https://semanticus.com.au' },
    homepage: 'https://semanticus.com.au/docs/assistant-skills',
    repository: 'https://github.com/tenfingerseddy/semanticus-vscode', license: 'Elastic-2.0',
  };
  putJson('plugins/semanticus/.claude-plugin/plugin.json', metadata);
  putJson('plugins/semanticus/.codex-plugin/plugin.json', {
    ...metadata, skills: './skills/',
    interface: {
      displayName: 'Semanticus', shortDescription: 'Work with Power BI and Fabric semantic models.',
      longDescription: metadata.description, developerName: 'Semanticus', category: 'Productivity',
      capabilities: [], defaultPrompt: 'Use Semanticus to inspect the open model and help with my task.',
      websiteURL: 'https://semanticus.com.au',
    },
  });
  // Keep the scaffolded Codex marketplace descriptor; package paths are relative to this pack's root.
  const codexCatalog = JSON.parse(fs.readFileSync(path.join(packRoot, '.agents/plugins/marketplace.json'), 'utf8'));
  if (codexCatalog.plugins?.length !== 1 || codexCatalog.plugins[0].source?.path !== './plugins/semanticus') {
    throw new Error('The Codex catalog must resolve the Semanticus plugin inside this pack.');
  }
  putJson('.agents/plugins/marketplace.json', codexCatalog);
  putJson('.claude-plugin/marketplace.json', {
    name: 'semanticus', owner: { name: 'Semanticus' },
    plugins: [{ name: 'semanticus', source: './plugins/semanticus', description: metadata.description, version }],
  });
  putJson('manifest.json', { version, skills });
  files.set('LICENSE', fs.readFileSync(path.join(repoRoot, 'LICENSE'), 'utf8').replace(/\r\n/g, '\n'));
  files.set('README.md', `# Semanticus assistant skills ${version}\n\n` +
    'Seven guides for the Semanticus MCP connection: getting started, DAX optimisation, AI readiness, model interviews, Change Plans, reusable workflows and model knowledge.\n\n' +
    '## Install from Semanticus\n\n' +
    'In VS Code, run **Semanticus: Install or Update Assistant Skills** and choose your assistant. Claude Code and Codex receive user skills for all projects. Copilot skills are contributed by the extension itself in VS Code versions that support extension skills. Existing customised skill files are preserved. Run the command again after a Semanticus update.\n\n' +
    '## Install this download as a plugin\n\n' +
    'Extract this ZIP into a permanent folder. Use the folder containing this README as PACK_PATH below. Install either the plugin or the user skills, so your assistant does not discover duplicate copies.\n\n' +
    'Claude Code:\n\n```text\nclaude plugin marketplace add "PACK_PATH"\nclaude plugin install semanticus@semanticus\n```\n\n' +
    `Codex:\n\n\`\`\`text\ncodex plugin marketplace add "PACK_PATH"\ncodex plugin install semanticus@${codexCatalog.name}\n\`\`\`\n\n` +
    'Refresh or restart your assistant if the skills do not appear. To update a plugin, extract the new release into that folder, refresh the marketplace and update Semanticus in the assistant plugin manager.\n\n' +
    '## Other skills-compatible assistants\n\n' +
    'Copy the folders under plugins/semanticus/skills into the assistant\'s documented skills directory. For project-only use, Claude Code reads .claude/skills and Codex reads .agents/skills. Copilot also reads .github/skills.\n\n' +
    '## Connect the model\n\n' +
    'These guides use your existing Semanticus MCP server. Keep Semanticus open in VS Code. Connect AI Assistant writes .mcp.json for clients that use that format. For another client, use the same command and arguments in its MCP configuration. The skills do not create another engine, supply a licence, or add AI credentials.\n\n' +
    'Ask: "Use Semanticus to explain this model", "Optimise this measure across customer and product contexts", or "Save this process as a workflow". Skills load when relevant. Essential orientation also comes from MCP, so skills are optional.\n\n' +
    '[Full setup guide](https://semanticus.com.au/docs/assistant-skills)\n');
  for (const [relative, body] of files) {
    const file = path.join(packRoot, relative);
    if (check) {
      if (!fs.existsSync(file) || fs.readFileSync(file, 'utf8') !== body) throw new Error(`Regenerate assistant pack: ${relative}`);
    } else {
      fs.mkdirSync(path.dirname(file), { recursive: true });
      fs.writeFileSync(file, body);
    }
  }
  return { version, packRoot, files, skills };
}

export async function packageAssistantPack() {
  const pack = prepareAssistantPack({ check: true });
  const out = path.join(extRoot, 'dist', `semanticus-assistant-skills-${pack.version}.zip`);
  fs.mkdirSync(path.dirname(out), { recursive: true });
  const zip = new yazl.ZipFile();
  const writing = pipeline(zip.outputStream, fs.createWriteStream(out));
  for (const [name, body] of [...pack.files].sort(([a], [b]) => a.localeCompare(b))) {
    zip.addBuffer(Buffer.from(body), `semanticus-assistant-skills/${name}`, { mtime: new Date('2026-01-01T00:00:00Z'), mode: 0o100644 });
  }
  zip.end();
  await writing;
  console.log(`Packaged ${pack.skills.length} skills: ${out}`);
  return out;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  if (process.argv.includes('--zip')) await packageAssistantPack();
  else { const pack = prepareAssistantPack({ check: process.argv.includes('--check') }); console.log(`Assistant pack ${pack.version}: ${pack.skills.length} skills`); }
}
