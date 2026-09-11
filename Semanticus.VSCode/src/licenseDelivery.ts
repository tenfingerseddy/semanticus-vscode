/// How a Pro license token reaches the engine without sitting on the process command line.
/// /proc/<pid>/cmdline is world-readable on Linux; stdin and the owner pipe are not.

export function canUseOsKeychain(
    platform: NodeJS.Platform = process.platform,
    env: NodeJS.ProcessEnv = process.env,
): boolean {
    if (platform === 'win32' || platform === 'darwin') return true;
    if (platform !== 'linux') return false;
    if (env.GNOME_KEYRING_CONTROL || env.GNOME_KEYRING_PID) return true;
    if (env.KDE_FULL_SESSION) return true;
    const store = (env.PASSWORD_STORE || '').toLowerCase();
    return store === 'gnome-libsecret' || store.startsWith('kwallet');
}

export function ownerServeArgs(workspace: string): string[] {
    return ['serve', '--workspace', workspace, '--ui-challenge-stdin', '--license-stdin'];
}

export function ownerStdinPayload(uiChallenge: string, licenseToken: string): string {
    return `${uiChallenge}\n${licenseToken || ''}\n`;
}

export function mcpLaunchArgs(workspace: string): string[] {
    return ['mcp', '--workspace', workspace];
}

export function argsContainLicenseToken(args: readonly string[]): boolean {
    for (let i = 0; i < args.length - 1; i++) {
        if (args[i] === '--license' && args[i + 1] && !args[i + 1].startsWith('-')) return true;
    }
    return false;
}
