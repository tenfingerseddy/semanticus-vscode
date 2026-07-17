import * as crypto from 'crypto';
import * as path from 'path';
import { Readable } from 'stream';

export interface SecretStore {
    get(key: string): Thenable<string | undefined>;
    store(key: string, value: string): Thenable<void>;
}

const MIN_CHALLENGE_LENGTH = 32;
const MAX_CHALLENGE_LENGTH = 128;
const CHALLENGE_PATTERN = /^[A-Za-z0-9_-]+$/;
const HANDSHAKE_ACCEPTED = 'SEMANTICUS-RPC/1 accepted';
const HANDSHAKE_REJECTED = 'SEMANTICUS-RPC/1 rejected';
const MAX_HANDSHAKE_RESPONSE_BYTES = 256;

export class RpcHandshakeRejectedError extends Error {
    constructor() {
        super('The running engine rejected the VS Code authentication proof.');
        this.name = 'RpcHandshakeRejectedError';
    }
}

export function rpcChallengeKey(ws: string, platform = process.platform): string {
    const resolved = path.resolve(ws);
    const canonical = platform === 'win32' ? resolved.toLowerCase() : resolved;
    return `semanticus.rpcUiChallenge.${crypto.createHash('sha256').update(canonical, 'utf8').digest('hex')}`;
}

function validChallenge(value: string | undefined): value is string {
    return !!value
        && value.length >= MIN_CHALLENGE_LENGTH
        && value.length <= MAX_CHALLENGE_LENGTH
        && CHALLENGE_PATTERN.test(value);
}

/// One random proof per workspace, held in VS Code's encrypted SecretStorage. A second VS Code window for the same
/// workspace retrieves the same proof; a shell process that merely knows the public pipe name does not.
export async function getUiChallenge(secrets: SecretStore, ws: string): Promise<string> {
    const key = rpcChallengeKey(ws);
    const existing = await secrets.get(key);
    if (validChallenge(existing)) return existing;

    const value = crypto.randomBytes(32).toString('base64url');
    await secrets.store(key, value);
    return value;
}

export function rpcRolePreamble(role: 'agent' | 'human', uiChallenge?: string): string {
    if (role === 'agent') return 'SEMANTICUS-RPC/1 agent\n';
    if (!validChallenge(uiChallenge)) throw new Error('The RPC UI challenge is invalid.');
    return `SEMANTICUS-RPC/1 human ${uiChallenge}\n`;
}

/// Wait for the server to bind the connection role before JSON-RPC owns the stream. Any bytes after the one-line
/// response are put back for the JSON-RPC reader, so a concurrent notification cannot be swallowed at the handoff.
export function waitForRpcHandshake(stream: Readable, timeoutMs = 5000): Promise<void> {
    return new Promise((resolve, reject) => {
        let buffered = Buffer.alloc(0);
        let settled = false;
        const finish = (error?: Error, remainder?: Buffer) => {
            if (settled) return;
            settled = true;
            clearTimeout(timer);
            stream.removeListener('data', onData);
            stream.removeListener('end', onEnd);
            stream.removeListener('error', onError);
            if (!error) {
                stream.pause();
                if (remainder?.length) stream.unshift(remainder);
                resolve();
            } else {
                reject(error);
            }
        };
        const onData = (chunk: Buffer | string) => {
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
            if (line === HANDSHAKE_ACCEPTED) return finish(undefined, remainder);
            if (line === HANDSHAKE_REJECTED) return finish(new RpcHandshakeRejectedError());
            return finish(new Error('The RPC role handshake response is invalid.'));
        };
        const onEnd = () => finish(new Error('The engine disconnected before confirming the RPC role handshake.'));
        const onError = (error: Error) => finish(error);
        const timer = setTimeout(
            () => finish(new Error(`The engine did not confirm the RPC role handshake within ${timeoutMs}ms.`)),
            timeoutMs);
        stream.on('data', onData);
        stream.once('end', onEnd);
        stream.once('error', onError);
    });
}
