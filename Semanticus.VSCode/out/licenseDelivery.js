"use strict";
/// How a Pro license token reaches the engine without sitting on the process command line.
/// /proc/<pid>/cmdline is world-readable on Linux; stdin and the owner pipe are not.
Object.defineProperty(exports, "__esModule", { value: true });
exports.canUseOsKeychain = canUseOsKeychain;
exports.ownerServeArgs = ownerServeArgs;
exports.ownerSignInArgs = ownerSignInArgs;
exports.ownerStdinPayload = ownerStdinPayload;
exports.mcpLaunchArgs = mcpLaunchArgs;
exports.argsContainLicenseToken = argsContainLicenseToken;
function canUseOsKeychain(platform = process.platform, env = process.env) {
    if (platform === 'win32' || platform === 'darwin')
        return true;
    if (platform !== 'linux')
        return false;
    if (env.GNOME_KEYRING_CONTROL || env.GNOME_KEYRING_PID)
        return true;
    if (env.KDE_FULL_SESSION)
        return true;
    const store = (env.PASSWORD_STORE || '').toLowerCase();
    return store === 'gnome-libsecret' || store.startsWith('kwallet');
}
function ownerServeArgs(workspace) {
    return ['serve', '--workspace', workspace, '--ui-challenge-stdin', '--license-stdin'];
}
/// The engine opens the system browser for a person's sign-in only when its command line says so (the engine's
/// InteractiveSignIn). A real window says so; the extension's own test runs (ExtensionMode.Test) never do, so no test
/// can raise a sign-in page on the desktop of whoever runs it.
function ownerSignInArgs(isTestRun) {
    return isTestRun ? [] : ['--interactive-signin'];
}
function ownerStdinPayload(uiChallenge, licenseToken) {
    return `${uiChallenge}\n${licenseToken || ''}\n`;
}
function mcpLaunchArgs(workspace) {
    return ['mcp', '--workspace', workspace];
}
function argsContainLicenseToken(args) {
    for (let i = 0; i < args.length - 1; i++) {
        if (args[i] === '--license' && args[i + 1] && !args[i + 1].startsWith('-'))
            return true;
    }
    return false;
}
//# sourceMappingURL=licenseDelivery.js.map