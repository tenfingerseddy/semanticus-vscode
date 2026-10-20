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
exports.readPersonalitySettings = readPersonalitySettings;
exports.writePersonalitySetting = writePersonalitySetting;
exports.personalityReport = personalityReport;
exports.applyHostPersonality = applyHostPersonality;
exports.watchPersonalityForEngine = watchPersonalityForEngine;
exports.attachPersonalitySettings = attachPersonalitySettings;
// Personality settings for the Studio (1.5, The Link lane). The values are the person's VS Code user settings, so they follow the
// person across models and folders. The webview reads them through webview/src/personality/settings.ts.
const vscode = __importStar(require("vscode"));
const KEYS = ['personality', 'sounds', 'soundVolume'];
function readPersonalitySettings() {
    const c = vscode.workspace.getConfiguration('semanticus');
    const volume = Number(c.get('soundVolume', 40));
    const motion = vscode.workspace.getConfiguration('workbench').get('reduceMotion', 'auto');
    return {
        type: 'personalitySettings',
        personality: c.get('personality', 'on') === 'quiet' ? 'quiet' : 'on',
        sounds: c.get('sounds', false) === true,
        soundVolume: Number.isFinite(volume) ? Math.min(100, Math.max(0, volume)) : 40,
        reduceMotion: motion === 'on' || motion === 'off' ? motion : 'auto',
    };
}
/** The page changed a switch: write it to the user's settings. The change event posts it back to every Studio panel. */
async function writePersonalitySetting(msg) {
    const key = msg.key;
    if (!KEYS.includes(key))
        return;
    let value;
    if (key === 'personality')
        value = msg.value === 'quiet' ? 'quiet' : 'on';
    else if (key === 'sounds')
        value = msg.value === true;
    else {
        const n = Number(msg.value);
        if (!Number.isFinite(n))
            return;
        value = Math.round(Math.min(100, Math.max(0, n)));
    }
    await vscode.workspace.getConfiguration('semanticus').update(key, value, vscode.ConfigurationTarget.Global);
}
/** What the engine is told about the settings (F-292): personality, sounds and volume, in the order reportHostSettings takes them. */
function personalityReport() {
    const s = readPersonalitySettings();
    return [s.personality, s.sounds, s.soundVolume];
}
/** The assistant set some of them (set_personality_settings, host/settings): write each through the same path as the page's switch. */
async function applyHostPersonality(v) {
    if (!v)
        return;
    const now = readPersonalitySettings();
    if (v.personality !== undefined && v.personality !== now.personality)
        await writePersonalitySetting({ key: 'personality', value: v.personality });
    if (v.sounds !== undefined && v.sounds !== now.sounds)
        await writePersonalitySetting({ key: 'sounds', value: v.sounds });
    if (v.soundVolume !== undefined && v.soundVolume !== now.soundVolume)
        await writePersonalitySetting({ key: 'soundVolume', value: v.soundVolume });
}
/** Call `report` now and whenever one of the three settings changes, so the engine can answer get_personality_settings. */
function watchPersonalityForEngine(report) {
    report();
    return vscode.workspace.onDidChangeConfiguration((e) => {
        if (KEYS.some((k) => e.affectsConfiguration(`semanticus.${k}`)))
            report();
    });
}
/** Post the settings to this panel now, and again whenever one of them changes, until the panel closes. Once per panel. */
const attached = new WeakSet();
function attachPersonalitySettings(panel) {
    const post = () => { try {
        void panel.webview.postMessage(readPersonalitySettings());
    }
    catch { /* disposing */ } };
    post();
    if (attached.has(panel))
        return;
    attached.add(panel);
    const sub = vscode.workspace.onDidChangeConfiguration((e) => {
        if (KEYS.some((k) => e.affectsConfiguration(`semanticus.${k}`)) || e.affectsConfiguration('workbench.reduceMotion'))
            post();
    });
    panel.onDidDispose(() => sub.dispose());
}
//# sourceMappingURL=personalitySettings.js.map