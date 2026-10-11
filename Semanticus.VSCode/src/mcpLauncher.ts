import * as path from 'path';
import { mcpLaunchArgs } from './licenseDelivery';
import type { McpServerEntry } from './engineResolution';

/// A STABLE launch path for the assistant's connection. The bundled engine lives inside the VERSIONED extension
/// folder, and VS Code deletes that folder after an update, so an .mcp.json entry naming it breaks on the first
/// update (measured on a test machine: the 1.2.0 entry survived into 1.2.1 and could not start anything).
/// This launcher lives outside every version folder, is rewritten on each activation, and at RUN time starts the
/// engine the running Semanticus for that folder uses when it is an installed one, else the newest installed
/// engine. So it keeps working even when the assistant starts before the new version has activated.
///
/// POSIX only. On Windows the entry stays the absolute engine path (Node starts an .exe with a path holding
/// spaces and parentheses correctly, and a script wrapper cannot be started as reliably there), healed on
/// activation by shouldAutoHealMcpEntry.

export const LAUNCHER_NAME = 'semanticus-mcp';
export const EXTENSION_FOLDER_PREFIX = 'semanticus-vscode.semanticus-vscode-';

export function launcherPath(home: string): string {
    return path.posix.join(home, '.semanticus', 'bin', LAUNCHER_NAME);
}

export function launcherEntry(launcher: string, ws: string): McpServerEntry {
    return { command: launcher, args: mcpLaunchArgs(ws) };
}

/// A value inside single quotes in POSIX sh: close, escaped quote, reopen. Nothing else is special there, so
/// spaces, parentheses, dollars and backslashes in a path all survive literally.
function shQuote(value: string): string {
    return `'${value.replace(/'/g, `'\\''`)}'`;
}

export interface LauncherInput {
    /// The folder holding every installed version, e.g. ~/.vscode/extensions.
    extensionsDir: string;
    /// The engine of the version that wrote the launcher: used only when no installed engine is found by scan.
    fallbackEngine: string;
}

export function posixLauncherScript(input: LauncherInput): string {
    return `#!/bin/sh
# Semanticus connection launcher for your assistant. Semanticus rewrites this file each time it starts in
# VS Code, so edits here do not last. It starts the installed Semanticus engine that fits this folder, so the
# connection keeps working after an extension update removes an older version's folder.
EXT_DIR=${shQuote(input.extensionsDir)}
FALLBACK=${shQuote(input.fallbackEngine)}
PREFIX=${shQuote(EXTENSION_FOLDER_PREFIX)}

ws=''
prev=''
for a in "$@"; do
  if [ "$prev" = '--workspace' ]; then ws=$a; fi
  prev=$a
done

is_engine() { [ -f "$1" ] && [ -x "$1" ]; }

# True when version $1 is newer than version $2 (numeric, dot separated).
newer() {
  awk -v a="$1" -v b="$2" 'BEGIN { n = split(a, x, "."); m = split(b, y, "."); k = (n > m) ? n : m;
    for (i = 1; i <= k; i++) { p = x[i] + 0; q = y[i] + 0; if (p > q) exit 0; if (p < q) exit 1 } exit 1 }'
}

pick=''
# 1. The engine Semanticus is already running for this folder, when it is an installed one and still alive.
#    A different version is refused when it joins, so while an older window is open, match that window.
rec_file="$ws/.semanticus/engine.json"
if [ -n "$ws" ] && [ -f "$rec_file" ]; then
  rec=$(sed -n 's/^[[:space:]]*"ExePath"[[:space:]]*:[[:space:]]*"\\([^"\\\\]*\\)".*$/\\1/p' "$rec_file" | head -n 1)
  pid=$(sed -n 's/^[[:space:]]*"Pid"[[:space:]]*:[[:space:]]*\\([0-9][0-9]*\\).*$/\\1/p' "$rec_file" | head -n 1)
  case "$rec" in
    *'/../'*|*'/./'*) rec='' ;;
  esac
  case "$rec" in
    "$EXT_DIR"/"$PREFIX"*/engine/Semanticus.Engine)
      if is_engine "$rec" && [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null; then pick=$rec; fi ;;
  esac
fi

# 2. Otherwise the newest installed engine.
if [ -z "$pick" ]; then
  bestv=''
  for exe in "$EXT_DIR"/"$PREFIX"*/engine/Semanticus.Engine; do
    is_engine "$exe" || continue
    d=\${exe%/engine/Semanticus.Engine}
    v=\${d##*/"$PREFIX"}
    v=\${v%%-*}
    if [ -z "$pick" ] || newer "$v" "$bestv"; then pick=$exe; bestv=$v; fi
  done
fi

# 3. The version that wrote this file.
if [ -z "$pick" ] && is_engine "$FALLBACK"; then pick=$FALLBACK; fi

if [ -z "$pick" ]; then
  echo "Semanticus: no installed Semanticus engine was found. Run Semanticus: Connect AI Assistant in VS Code, then reconnect with /mcp." >&2
  exit 1
fi
exec "$pick" "$@"
`;
}

export interface LauncherFs {
    mkdirSync(p: string, o: { recursive: true }): unknown;
    writeFileSync(p: string, data: string, o: { mode: number }): void;
    renameSync(from: string, to: string): void;
    chmodSync(p: string, mode: number): void;
}

/// Write the launcher atomically (a half-written script started by the assistant would fail in confusing ways)
/// and return its path. Throws on failure; the caller keeps the absolute engine entry instead.
export function writePosixLauncher(fs: LauncherFs, home: string, input: LauncherInput): string {
    const target = launcherPath(home);
    fs.mkdirSync(path.posix.dirname(target), { recursive: true });
    const tmp = `${target}.tmp-${process.pid}-${Date.now()}`;
    fs.writeFileSync(tmp, posixLauncherScript(input), { mode: 0o755 });
    fs.chmodSync(tmp, 0o755);
    fs.renameSync(tmp, target);
    return target;
}

/// Is this entry the launcher shape Semanticus writes? Its command is <home>/.semanticus/bin/semanticus-mcp.
export function isLauncherCommand(command: unknown): boolean {
    if (typeof command !== 'string') return false;
    const p = path.posix;
    return p.basename(command) === LAUNCHER_NAME
        && p.basename(p.dirname(command)) === 'bin'
        && p.basename(p.dirname(p.dirname(command))) === '.semanticus';
}
