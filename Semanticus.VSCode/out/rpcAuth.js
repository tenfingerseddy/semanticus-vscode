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
exports.RpcHandshakeRejectedError = void 0;
exports.rpcChallengeKey = rpcChallengeKey;
exports.getUiChallenge = getUiChallenge;
exports.rpcRolePreamble = rpcRolePreamble;
exports.waitForRpcHandshake = waitForRpcHandshake;
const crypto = __importStar(require("crypto"));
const path = __importStar(require("path"));
const MIN_CHALLENGE_LENGTH = 32;
const MAX_CHALLENGE_LENGTH = 128;
const CHALLENGE_PATTERN = /^[A-Za-z0-9_-]+$/;
const HANDSHAKE_ACCEPTED = 'SEMANTICUS-RPC/1 accepted';
const HANDSHAKE_REJECTED = 'SEMANTICUS-RPC/1 rejected';
const MAX_HANDSHAKE_RESPONSE_BYTES = 256;
class RpcHandshakeRejectedError extends Error {
    constructor() {
        super('The running engine rejected the VS Code authentication proof.');
        this.name = 'RpcHandshakeRejectedError';
    }
}
exports.RpcHandshakeRejectedError = RpcHandshakeRejectedError;
function rpcChallengeKey(ws, platform = process.platform) {
    const resolved = path.resolve(ws);
    const canonical = platform === 'win32' ? resolved.toLowerCase() : resolved;
    return `semanticus.rpcUiChallenge.${crypto.createHash('sha256').update(canonical, 'utf8').digest('hex')}`;
}
function validChallenge(value) {
    return !!value
        && value.length >= MIN_CHALLENGE_LENGTH
        && value.length <= MAX_CHALLENGE_LENGTH
        && CHALLENGE_PATTERN.test(value);
}
/// One random proof per workspace, held in VS Code's encrypted SecretStorage. A second VS Code window for the same
/// workspace retrieves the same proof; a shell process that merely knows the public pipe name does not.
async function getUiChallenge(secrets, ws) {
    const key = rpcChallengeKey(ws);
    const existing = await secrets.get(key);
    if (validChallenge(existing))
        return existing;
    const value = crypto.randomBytes(32).toString('base64url');
    await secrets.store(key, value);
    return value;
}
function rpcRolePreamble(role, uiChallenge) {
    if (role === 'agent')
        return 'SEMANTICUS-RPC/1 agent\n';
    if (!validChallenge(uiChallenge))
        throw new Error('The RPC UI challenge is invalid.');
    return `SEMANTICUS-RPC/1 human ${uiChallenge}\n`;
}
/// Wait for the server to bind the connection role before JSON-RPC owns the stream. Any bytes after the one-line
/// response are put back for the JSON-RPC reader, so a concurrent notification cannot be swallowed at the handoff.
function waitForRpcHandshake(stream, timeoutMs = 5000) {
    return new Promise((resolve, reject) => {
        let buffered = Buffer.alloc(0);
        let settled = false;
        const finish = (error, remainder) => {
            if (settled)
                return;
            settled = true;
            clearTimeout(timer);
            stream.removeListener('data', onData);
            stream.removeListener('end', onEnd);
            stream.removeListener('error', onError);
            if (!error) {
                stream.pause();
                if (remainder?.length)
                    stream.unshift(remainder);
                resolve();
            }
            else {
                reject(error);
            }
        };
        const onData = (chunk) => {
            const bytes = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
            buffered = Buffer.concat([buffered, bytes]);
            const newline = buffered.indexOf(0x0a);
            if (newline < 0) {
                if (buffered.length >= MAX_HANDSHAKE_RESPONSE_BYTES)
                    finish(new Error('The RPC role handshake response is too long.'));
                return;
            }
            if (newline >= MAX_HANDSHAKE_RESPONSE_BYTES)
                return finish(new Error('The RPC role handshake response is too long.'));
            const lineBytes = buffered.subarray(0, newline);
            if (lineBytes.includes(0x0d))
                return finish(new Error('The RPC role handshake response is invalid.'));
            const line = lineBytes.toString('utf8');
            const remainder = buffered.subarray(newline + 1);
            if (line === HANDSHAKE_ACCEPTED)
                return finish(undefined, remainder);
            if (line === HANDSHAKE_REJECTED)
                return finish(new RpcHandshakeRejectedError());
            return finish(new Error('The RPC role handshake response is invalid.'));
        };
        const onEnd = () => finish(new Error('The engine disconnected before confirming the RPC role handshake.'));
        const onError = (error) => finish(error);
        const timer = setTimeout(() => finish(new Error(`The engine did not confirm the RPC role handshake within ${timeoutMs}ms.`)), timeoutMs);
        stream.on('data', onData);
        stream.once('end', onEnd);
        stream.once('error', onError);
    });
}
//# sourceMappingURL=rpcAuth.js.map