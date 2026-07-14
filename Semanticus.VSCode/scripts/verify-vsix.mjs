#!/usr/bin/env node
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import yauzl from 'yauzl';
import {
  assertReleaseContentSafe,
  credentialPathFinding,
  isEngineOwnedPath,
  isPackagedEngineBinaryPath,
  isPackagedEngineEndpointPath,
} from './release-security.mjs';

const TARGETS = {
  'win32-x64': { platform: 'win32', arch: 'x64', executable: 'Semanticus.Engine.exe', minimumExecutableBytes: 100_000 },
  'win32-arm64': { platform: 'win32', arch: 'arm64', executable: 'Semanticus.Engine.exe', minimumExecutableBytes: 100_000 },
  'linux-x64': { platform: 'linux', arch: 'x64', executable: 'Semanticus.Engine', minimumExecutableBytes: 70_000 },
  'darwin-x64': { platform: 'darwin', arch: 'x64', executable: 'Semanticus.Engine', minimumExecutableBytes: 80_000 },
  'darwin-arm64': { platform: 'darwin', arch: 'arm64', executable: 'Semanticus.Engine', minimumExecutableBytes: 100_000 },
};

const EXACT_FILES = new Set([
  '[Content_Types].xml',
  'extension.vsixmanifest',
  'extension/LICENSE.txt',
  'extension/NOTICE',
  'extension/package.json',
  'extension/readme.md',
]);

const ALLOWED_PREFIXES = [
  'extension/engine/',
  'extension/language/',
  'extension/media/',
  'extension/node_modules/vscode-jsonrpc/',
  'extension/out/',
];

const FORBIDDEN_SEGMENTS = new Set([
  '.git', '.github', '.semanticus', '__pycache__', 'bin', 'dist', 'docs', 'obj',
  'scripts', 'shots', 'src', 'test', 'tests', 'tools', 'uitest', 'webview',
]);
const FORBIDDEN_RUNTIME_BASENAMES = new Set(['createdump', 'createdump.exe']);

const FORBIDDEN_SUFFIX = /(?:^|\/)(?:\.env(?:\..*)?|[^/]+\.(?:cs|env|jsonl|key|log|map|pdb|pem|pfx|snk|ts|tsx))$/i;
const MAX_ENTRIES = 2_000;
const MAX_ENTRY_BYTES = 150 * 1024 * 1024;
const MAX_TOTAL_BYTES = 250 * 1024 * 1024;

function targetInfo(target) {
  const info = TARGETS[target];
  if (!info) throw new Error(`Unknown VSIX target "${target}". Known: ${Object.keys(TARGETS).join(', ')}`);
  return info;
}

function assertSafeName(name) {
  if (!name || name.includes('\\') || name.startsWith('/') || /^[a-z]:/i.test(name)) {
    throw new Error(`Unsafe VSIX entry path: ${JSON.stringify(name)}`);
  }
  const segments = name.split('/');
  if (name.endsWith('/')) segments.pop();
  if (segments.some((segment) => segment.length === 0)) {
    throw new Error(`Unsafe VSIX entry has an empty path segment: ${name}`);
  }
  if (segments.some((segment) => segment === '.' || segment === '..')) {
    throw new Error(`VSIX entry attempts path traversal: ${name}`);
  }
}

function isAllowed(name) {
  return EXACT_FILES.has(name) || ALLOWED_PREFIXES.some((prefix) => name.startsWith(prefix));
}

export function validatePayloadInventory(entries, target, manifestText, packageText, contentScan) {
  const info = targetInfo(target);
  const names = new Set();
  const byName = new Map();
  const folded = new Set();
  let engineFiles = 0;
  let totalBytes = 0;

  if (entries.length > MAX_ENTRIES) throw new Error(`VSIX has too many entries (${entries.length}, limit ${MAX_ENTRIES})`);

  for (const entry of entries) {
    const name = entry.fileName;
    if (!Number.isSafeInteger(entry.uncompressedSize) || entry.uncompressedSize < 0 || entry.uncompressedSize > MAX_ENTRY_BYTES) {
      throw new Error(`VSIX entry has an invalid or excessive uncompressed size: ${name} (${entry.uncompressedSize})`);
    }
    totalBytes += entry.uncompressedSize;
    if (totalBytes > MAX_TOTAL_BYTES) throw new Error(`VSIX uncompressed payload exceeds ${MAX_TOTAL_BYTES} bytes`);
    assertSafeName(name);
    if (name.endsWith('/') && (entry.uncompressedSize !== 0 || entry.compressedSize !== 0)) {
      throw new Error(`VSIX directory entry has content: ${name}`);
    }
    const lower = name.toLowerCase();
    if (folded.has(lower)) throw new Error(`Duplicate or case-colliding VSIX entry: ${name}`);
    folded.add(lower);
    names.add(name);
    byName.set(name, entry);

    if (!isAllowed(name)) throw new Error(`VSIX entry is outside the production allow-list: ${name}`);
    const segments = name.split('/').filter(Boolean).map((segment) => segment.toLowerCase());
    if (segments.some((segment) => FORBIDDEN_SEGMENTS.has(segment))) {
      throw new Error(`Forbidden development or internal path in VSIX: ${name}`);
    }
    if (credentialPathFinding(name)) {
      throw new Error(`Forbidden credential file or directory in VSIX: ${name}`);
    }
    if (name.startsWith('extension/engine/') && FORBIDDEN_RUNTIME_BASENAMES.has(segments.at(-1))) {
      throw new Error(`Forbidden non-product diagnostic executable in VSIX: ${name}`);
    }
    if (FORBIDDEN_SUFFIX.test(name)) throw new Error(`Forbidden source, debug, log, or credential file in VSIX: ${name}`);
    const unixMode = (entry.externalFileAttributes >>> 16) & 0xffff;
    if ((unixMode & 0xf000) === 0xa000) throw new Error(`Symbolic links are not allowed in the VSIX: ${name}`);
    if (name.startsWith('extension/engine/') && !name.endsWith('/')) engineFiles++;
  }

  if (!contentScan || contentScan.scannedEntries !== entries.length) {
    throw new Error(`VSIX content scan was incomplete (${contentScan?.scannedEntries ?? 0} of ${entries.length} entries)`);
  }

  const required = [
    ...EXACT_FILES,
    'extension/out/extension.js',
    'extension/media/studio/studio.js',
    'extension/media/studio/studio.css',
    'extension/node_modules/vscode-jsonrpc/node.js',
    `extension/engine/${info.executable}`,
    'extension/engine/Semanticus.Engine.dll',
    'extension/engine/Semanticus.Core.dll',
    'extension/engine/Semanticus.Analysis.dll',
    'extension/engine/Semanticus.Engine.deps.json',
    'extension/engine/Semanticus.Engine.runtimeconfig.json',
    'extension/engine/System.Private.CoreLib.dll',
    'extension/engine/workflows/verified-measure.md',
  ];
  const missing = required.filter((name) => !names.has(name));
  if (missing.length) throw new Error(`VSIX is missing required production files:\n  ${missing.join('\n  ')}`);
  if (engineFiles < 100) throw new Error(`VSIX engine payload is implausibly small (${engineFiles} files)`);
  const minimumSizes = new Map([
    [`extension/engine/${info.executable}`, info.minimumExecutableBytes],
    ['extension/engine/Semanticus.Engine.dll', 500_000],
    ['extension/engine/System.Private.CoreLib.dll', 1_000_000],
  ]);
  for (const [name, minimum] of minimumSizes) {
    if (byName.get(name).uncompressedSize < minimum) {
      throw new Error(`Required runtime file is implausibly small: ${name} (${byName.get(name).uncompressedSize} bytes)`);
    }
  }

  if (!manifestText.includes(`TargetPlatform="${target}"`)) {
    throw new Error(`VSIX manifest does not declare TargetPlatform="${target}"`);
  }
  let packageJson;
  try { packageJson = JSON.parse(packageText); }
  catch (error) { throw new Error(`VSIX extension/package.json is invalid JSON: ${error.message}`); }
  if (packageJson.name !== 'semanticus-vscode' || packageJson.main !== './out/extension.js') {
    throw new Error('VSIX package manifest does not point at the Semanticus production extension entry point');
  }

  return { entries: entries.length, engineFiles, executable: info.executable, scannedEntries: contentScan.scannedEntries };
}

function openZip(file) {
  return new Promise((resolve, reject) => {
    yauzl.open(file, { lazyEntries: true, validateEntrySizes: true }, (error, zip) => error ? reject(error) : resolve(zip));
  });
}

function openEntry(zip, entry) {
  return new Promise((resolve, reject) => zip.openReadStream(entry, (error, stream) => error ? reject(error) : resolve(stream)));
}

async function readEntryBuffer(zip, entry) {
  const stream = await openEntry(zip, entry);
  const chunks = [];
  for await (const chunk of stream) chunks.push(chunk);
  return Buffer.concat(chunks);
}

function safeExtractPath(root, entryName) {
  const relative = entryName.slice('extension/engine/'.length);
  const output = path.resolve(root, relative);
  const prefix = path.resolve(root) + path.sep;
  if (output !== path.resolve(root) && !output.startsWith(prefix)) throw new Error(`Unsafe engine extraction path: ${entryName}`);
  return output;
}

async function inspectArchive(vsixPath, target, extractRoot) {
  const zip = await openZip(vsixPath);
  const entries = [];
  let manifestText = '';
  let packageText = '';
  let totalBytes = 0;
  let scannedEntries = 0;

  try {
    await new Promise((resolve, reject) => {
      let settled = false;
      const fail = (error) => { if (!settled) { settled = true; reject(error); } };
      zip.once('error', fail);
      zip.once('end', () => { if (!settled) { settled = true; resolve(); } });
      zip.on('entry', (entry) => {
        (async () => {
          assertSafeName(entry.fileName);
          totalBytes += entry.uncompressedSize;
          if (entry.uncompressedSize > MAX_ENTRY_BYTES || totalBytes > MAX_TOTAL_BYTES || entries.length >= MAX_ENTRIES) {
            throw new Error(`VSIX archive size or entry-count limit exceeded at ${entry.fileName}`);
          }
          entries.push({
            fileName: entry.fileName,
            compressedSize: entry.compressedSize,
            uncompressedSize: entry.uncompressedSize,
            externalFileAttributes: entry.externalFileAttributes,
          });
          const isDirectory = entry.fileName.endsWith('/');
          const content = isDirectory ? Buffer.alloc(0) : await readEntryBuffer(zip, entry);
          if (content.length !== entry.uncompressedSize) {
            throw new Error(`VSIX entry size changed while reading: ${entry.fileName}`);
          }
          const engineBoundary = isEngineOwnedPath(entry.fileName);
          assertReleaseContentSafe(entry.fileName, content, {
            engineBoundary,
            engineSource: isPackagedEngineEndpointPath(entry.fileName, entry.externalFileAttributes),
            engineBinary: isPackagedEngineBinaryPath(entry.fileName, entry.externalFileAttributes),
          });
          scannedEntries++;
          if (entry.fileName === 'extension.vsixmanifest') manifestText = content.toString('utf8');
          if (entry.fileName === 'extension/package.json') packageText = content.toString('utf8');
          if (extractRoot && entry.fileName.startsWith('extension/engine/')) {
            const output = safeExtractPath(extractRoot, entry.fileName);
            if (entry.fileName.endsWith('/')) fs.mkdirSync(output, { recursive: true });
            else {
              fs.mkdirSync(path.dirname(output), { recursive: true });
              fs.writeFileSync(output, content);
            }
          }
          zip.readEntry();
        })().catch(fail);
      });
      zip.readEntry();
    });
  } finally {
    try { zip.close(); } catch { /* already closed */ }
  }
  return { entries, manifestText, packageText, contentScan: { scannedEntries } };
}

function runExtractedEngine(engineRoot, executable) {
  const exe = path.join(engineRoot, executable);
  if (process.platform !== 'win32') fs.chmodSync(exe, 0o755);
  const isolatedEnv = { ...process.env };
  for (const key of Object.keys(isolatedEnv)) {
    if (key.toLowerCase() === 'path' || key.toUpperCase().startsWith('DOTNET_ROOT') || key.toUpperCase() === 'DOTNET_HOST_PATH') {
      delete isolatedEnv[key];
    }
  }
  isolatedEnv.PATH = engineRoot;
  isolatedEnv.DOTNET_ROOT = path.join(engineRoot, '__no_global_dotnet__');
  isolatedEnv.DOTNET_MULTILEVEL_LOOKUP = '0';
  const result = spawnSync(exe, [], {
    cwd: engineRoot,
    encoding: 'utf8',
    timeout: 30_000,
    windowsHide: true,
    env: isolatedEnv,
  });
  if (result.error) throw new Error(`Extracted engine could not start: ${result.error.message}`);
  if (result.status !== 0) {
    throw new Error(`Extracted engine exited ${result.status}. stderr: ${(result.stderr || '').trim()}`);
  }
  const output = `${result.stdout || ''}\n${result.stderr || ''}`;
  if (!output.includes('Semanticus.Engine') || !output.includes('Usage:')) {
    throw new Error('Extracted engine started but did not return the expected help contract');
  }
}

export async function verifyVsix(vsixPath, target) {
  const absolute = path.resolve(vsixPath);
  if (!fs.existsSync(absolute)) throw new Error(`VSIX not found: ${absolute}`);
  const info = targetInfo(target);
  const hostMatches = info.platform === process.platform && info.arch === process.arch;
  const tempRoot = hostMatches ? fs.mkdtempSync(path.join(os.tmpdir(), 'semanticus-vsix-')) : undefined;
  const engineRoot = tempRoot && path.join(tempRoot, 'engine');

  try {
    const inspected = await inspectArchive(absolute, target, engineRoot);
    const summary = validatePayloadInventory(
      inspected.entries,
      target,
      inspected.manifestText,
      inspected.packageText,
      inspected.contentScan,
    );
    if (hostMatches) runExtractedEngine(engineRoot, info.executable);
    return { ...summary, hostExecuted: hostMatches, sizeBytes: fs.statSync(absolute).size };
  } finally {
    if (tempRoot) fs.rmSync(tempRoot, { recursive: true, force: true });
  }
}

const invokedDirectly = process.argv[1] && pathToFileURL(path.resolve(process.argv[1])).href === import.meta.url;
if (invokedDirectly) {
  const [, , vsixPath, target] = process.argv;
  if (!vsixPath || !target) {
    console.error('Usage: node scripts/verify-vsix.mjs <path.vsix> <target>');
    process.exit(2);
  }
  try {
    const result = await verifyVsix(vsixPath, target);
    console.log(`VSIX verified: ${result.entries} entries, ${result.engineFiles} engine files, extracted engine executed: ${result.hostExecuted}`);
  } catch (error) {
    console.error(`VSIX verification failed: ${error.message}`);
    process.exit(1);
  }
}
