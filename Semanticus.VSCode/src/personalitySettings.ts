// Personality settings for the Studio (1.5, The Link lane). The values are the person's VS Code user settings, so they follow the
// person across models and folders. The webview reads them through webview/src/personality/settings.ts.
import * as vscode from 'vscode';

export type PersonalitySettingsMessage = {
    type: 'personalitySettings'; personality: 'on' | 'quiet'; sounds: boolean; soundVolume: number; reduceMotion: 'on' | 'off' | 'auto';
};

const KEYS = ['personality', 'sounds', 'soundVolume'] as const;
type Key = (typeof KEYS)[number];

export function readPersonalitySettings(): PersonalitySettingsMessage {
    const c = vscode.workspace.getConfiguration('semanticus');
    const volume = Number(c.get<number>('soundVolume', 40));
    const motion = vscode.workspace.getConfiguration('workbench').get<string>('reduceMotion', 'auto');
    return {
        type: 'personalitySettings',
        personality: c.get<string>('personality', 'on') === 'quiet' ? 'quiet' : 'on',
        sounds: c.get<boolean>('sounds', false) === true,
        soundVolume: Number.isFinite(volume) ? Math.min(100, Math.max(0, volume)) : 40,
        reduceMotion: motion === 'on' || motion === 'off' ? motion : 'auto',
    };
}

/** The page changed a switch: write it to the user's settings. The change event posts it back to every Studio panel. */
export async function writePersonalitySetting(msg: { key?: unknown; value?: unknown }): Promise<void> {
    const key = msg.key as Key;
    if (!KEYS.includes(key)) return;
    let value: unknown;
    if (key === 'personality') value = msg.value === 'quiet' ? 'quiet' : 'on';
    else if (key === 'sounds') value = msg.value === true;
    else { const n = Number(msg.value); if (!Number.isFinite(n)) return; value = Math.round(Math.min(100, Math.max(0, n))); }
    await vscode.workspace.getConfiguration('semanticus').update(key, value, vscode.ConfigurationTarget.Global);
}

/** Post the settings to this panel now, and again whenever one of them changes, until the panel closes. Once per panel. */
const attached = new WeakSet<vscode.WebviewPanel>();
export function attachPersonalitySettings(panel: vscode.WebviewPanel): void {
    const post = () => { try { void panel.webview.postMessage(readPersonalitySettings()); } catch { /* disposing */ } };
    post();
    if (attached.has(panel)) return;
    attached.add(panel);
    const sub = vscode.workspace.onDidChangeConfiguration((e) => {
        if (KEYS.some((k) => e.affectsConfiguration(`semanticus.${k}`)) || e.affectsConfiguration('workbench.reduceMotion')) post();
    });
    panel.onDidDispose(() => sub.dispose());
}
