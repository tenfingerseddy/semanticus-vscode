"use strict";
var __createBinding = (this && this.__createBinding) || (Object.create ? (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    var desc = Object.getOwnPropertyDescriptor(m, k);
    if (!desc || ("get" in desc ? !m.__esModule : desc.writable || desc.configurable)) {
      desc = { enumerable: true, get: function() { return m[k]; } };
    }
    Object.defineProperty(o, k2, desc);
}) : (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    o[k2] = m[k];
}));
var __setModuleDefault = (this && this.__setModuleDefault) || (Object.create ? (function(o, v) {
    Object.defineProperty(o, "default", { enumerable: true, value: v });
}) : function(o, v) {
    o["default"] = v;
});
var __importStar = (this && this.__importStar) || (function () {
    var ownKeys = function(o) {
        ownKeys = Object.getOwnPropertyNames || function (o) {
            var ar = [];
            for (var k in o) if (Object.prototype.hasOwnProperty.call(o, k)) ar[ar.length] = k;
            return ar;
        };
        return ownKeys(o);
    };
    return function (mod) {
        if (mod && mod.__esModule) return mod;
        var result = {};
        if (mod != null) for (var k = ownKeys(mod), i = 0; i < k.length; i++) if (k[i] !== "default") __createBinding(result, mod, k[i]);
        __setModuleDefault(result, mod);
        return result;
    };
})();
Object.defineProperty(exports, "__esModule", { value: true });
exports.EXTENSION_FOLDER_PREFIX = exports.LAUNCHER_NAME = void 0;
exports.launcherPath = launcherPath;
exports.launcherEntry = launcherEntry;
exports.posixLauncherScript = posixLauncherScript;
exports.writePosixLauncher = writePosixLauncher;
exports.isLauncherCommand = isLauncherCommand;
const path = __importStar(require("path"));
const licenseDelivery_1 = require("./licenseDelivery");
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
exports.LAUNCHER_NAME = 'semanticus-mcp';
exports.EXTENSION_FOLDER_PREFIX = 'semanticus-vscode.semanticus-vscode-';
function launcherPath(home) {
    return path.posix.join(home, '.semanticus', 'bin', exports.LAUNCHER_NAME);
}
function launcherEntry(launcher, ws) {
    return { command: launcher, args: (0, licenseDelivery_1.mcpLaunchArgs)(ws) };
}
/// A value inside single quotes in POSIX sh: close, escaped quote, reopen. Nothing else is special there, so
/// spaces, parentheses, dollars and backslashes in a path all survive literally.
function shQuote(value) {
    return `'${value.replace(/'/g, `'\\''`)}'`;
}
function posixLauncherScript(input) {
    return `#!/bin/sh
# Semanticus connection launcher for your assistant. Semanticus rewrites this file each time it starts in
# VS Code, so edits here do not last. It starts the installed Semanticus engine that fits this folder, so the
# connection keeps working after an extension update removes an older version's folder.
EXT_DIR=${shQuote(input.extensionsDir)}
FALLBACK=${shQuote(input.fallbackEngine)}
PREFIX=${shQuote(exports.EXTENSION_FOLDER_PREFIX)}

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
/// Write the launcher atomically (a half-written script started by the assistant would fail in confusing ways)
/// and return its path. Throws on failure; the caller keeps the absolute engine entry instead.
function writePosixLauncher(fs, home, input) {
    const target = launcherPath(home);
    fs.mkdirSync(path.posix.dirname(target), { recursive: true });
    const tmp = `${target}.tmp-${process.pid}-${Date.now()}`;
    fs.writeFileSync(tmp, posixLauncherScript(input), { mode: 0o755 });
    fs.chmodSync(tmp, 0o755);
    fs.renameSync(tmp, target);
    return target;
}
/// Is this entry the launcher shape Semanticus writes? Its command is <home>/.semanticus/bin/semanticus-mcp.
function isLauncherCommand(command) {
    if (typeof command !== 'string')
        return false;
    const p = path.posix;
    return p.basename(command) === exports.LAUNCHER_NAME
        && p.basename(p.dirname(command)) === 'bin'
        && p.basename(p.dirname(p.dirname(command))) === '.semanticus';
}
//# sourceMappingURL=mcpLauncher.js.map