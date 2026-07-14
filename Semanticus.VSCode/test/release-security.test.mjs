import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  assertReleaseContentSafe,
  credentialPathFinding,
  isEngineOwnedPath,
  isPackagedEngineBinaryPath,
  isPackagedEngineEndpointPath,
  releaseContentFinding,
} from '../scripts/release-security.mjs';

const privateKey = ['-----BEGIN', 'PRIVATE KEY-----'].join(' ');
const syntheticFindings = [
  ['private-key', privateKey],
  ['github-token', `ghp_${'A'.repeat(36)}`],
  ['npm-token', `npm_${'B'.repeat(36)}`],
  ['slack-token', `xoxb-${'C'.repeat(24)}`],
  ['aws-access-key', `AKIA${'C'.repeat(16)}`],
  ['google-api-key', `AIza${'D'.repeat(35)}`],
  ['azure-devops-pat', `${'J'.repeat(75)}AZDO${'K'.repeat(5)}`],
  ['jwt', `eyJ${'L'.repeat(12)}.${'M'.repeat(16)}.${'N'.repeat(48)}`],
  ['model-provider-token', `sk-proj-${'E'.repeat(24)}`],
  ['stripe-secret', `sk_live_${'F'.repeat(24)}`],
  ['azure-storage-key', `AccountKey=${'G'.repeat(44)}`],
  ['shared-access-signature', `SharedAccessSignature=sv=1&sig=${'H'.repeat(24)}`],
  ['hardcoded-aws-secret', `AWS_SECRET_ACCESS_KEY=${'W'.repeat(40)}`],
  ['hardcoded-aws-secret', `aws_secret_access_key: "${'Y'.repeat(40)}"`],
  ['hardcoded-aws-secret', `AWS_SESSION_TOKEN='${'Z'.repeat(48)}'`],
  ['hardcoded-aws-secret', `AWS_SECRET_KEY=${'D'.repeat(40)}`],
  ['hardcoded-aws-secret', `AMAZON_SECRET_ACCESS_KEY=${'E'.repeat(40)}`],
  ['hardcoded-aws-secret', `AWS_BEARER_TOKEN_BEDROCK=${'F'.repeat(40)}`],
  ['hardcoded-aws-secret', `SecretAccessKey=${'K'.repeat(40)}`],
  ['hardcoded-aws-secret-xml', `<add key="AWS_SECRET_ACCESS_KEY" value="${'M'.repeat(40)}" />`],
  ['hardcoded-provider-secret', `FABRIC_SECRET=${'I'.repeat(24)}`],
  ['hardcoded-provider-secret', `XAI_API_KEY=${'X'.repeat(24)}`],
  ['hardcoded-provider-secret', `PERPLEXITY_API_KEY="${'P'.repeat(24)}"`],
  ['hardcoded-provider-secret', `HF_TOKEN='${'H'.repeat(24)}'`],
  ['hardcoded-provider-secret', `HUGGING_FACE_HUB_TOKEN=${'J'.repeat(24)}`],
  ['hardcoded-provider-secret', `HUGGINGFACEHUB_API_TOKEN="${'L'.repeat(24)}"`],
  ['hardcoded-provider-secret-xml', `<add key="HF_TOKEN" value="${'V'.repeat(24)}" />`],
  ['hardcoded-generic-secret', `client_secret=${'O'.repeat(24)}`],
  ['hardcoded-provider-secret', `{"OPENAI_API_KEY":"${'Q'.repeat(24)}"}`],
  ['hardcoded-generic-secret', `{"client_secret":"${'R'.repeat(24)}"}`],
  ['hardcoded-provider-secret', `OPENAI_API_KEY="${'!@#$%^&*()-+'.repeat(2)}"`],
  ['hardcoded-generic-secret', `client_secret="${'!@#$%^&*()-+'.repeat(2)}"`],
  ['hardcoded-provider-secret-xml', `<add key="OPENAI_API_KEY" value="${'S'.repeat(24)}" />`],
  ['hardcoded-generic-secret-xml', `<add value="${'T'.repeat(24)}" key="client_secret" />`],
  ['hardcoded-provider-secret-xml', `<add key="FABRIC_SECRET"\n value="${'X'.repeat(24)}" />`],
  ['hardcoded-provider-secret', `OPENAI_API_KEY="${'abcdefghijkl'.split('').join(',')}"`],
  ['hardcoded-generic-secret', `client_secret: "${Array(6).fill('[a]').join('')}"`],
  ['hardcoded-generic-secret', `Password=${'abcdefgh'.split('').join('|')}`],
];
for (const [expected, content] of syntheticFindings) {
  assert.equal(releaseContentFinding(content), expected, expected);
}
const embeddedSecret = `client_secret="${'U'.repeat(24)}"`;
assert.equal(
  releaseContentFinding(Buffer.concat([Buffer.from([0, 255, 1]), Buffer.from(embeddedSecret), Buffer.from([0, 2])])),
  'hardcoded-generic-secret',
);
assert.equal(
  releaseContentFinding(Buffer.concat([Buffer.from([0, 255, 1]), Buffer.from(embeddedSecret, 'utf16le'), Buffer.from([0, 2])])),
  'hardcoded-generic-secret',
);
assert.equal(
  releaseContentFinding(Buffer.concat([Buffer.from([255]), Buffer.from(`AKIA${'V'.repeat(16)}`, 'utf16le')])),
  'aws-access-key',
);
assert.equal(
  releaseContentFinding(Buffer.concat([Buffer.alloc(10 * 1024, 65), Buffer.from(embeddedSecret, 'utf16le')])),
  'hardcoded-generic-secret',
);

assert.equal(
  releaseContentFinding('process.env.OPENAI_API_KEY', { engineBoundary: true }),
  'model-provider-credential',
);
for (const awsCredentialName of [
  'AWS_ACCESS_KEY_ID',
  'AWS_SECRET_ACCESS_KEY',
  'AWS_SESSION_TOKEN',
  'AWS_BEARER_TOKEN_BEDROCK',
  'AMAZON_SECRET_ACCESS_KEY',
  'AWS_PROFILE',
  'AWS_WEB_IDENTITY_TOKEN_FILE',
  'AWS_CONTAINER_AUTHORIZATION_TOKEN_FILE',
  'HUGGING_FACE_HUB_TOKEN',
  'HUGGINGFACEHUB_API_TOKEN',
]) {
  assert.equal(
    releaseContentFinding(`Environment.GetEnvironmentVariable("${awsCredentialName}")`, { engineBoundary: true }),
    'model-provider-credential',
    awsCredentialName,
  );
}
for (const awsEndpointConfig of [
  'AWS_ENDPOINT_URL',
  'AWS_ENDPOINT_URL_BEDROCK_RUNTIME',
  'AWS_ENDPOINT_URL_BEDROCK_AGENT_RUNTIME',
  'AWS_ENDPOINT_URL_SAGEMAKER_RUNTIME',
  'AWS_BEDROCK_RUNTIME_ENDPOINT',
]) {
  assert.equal(
    releaseContentFinding(`Environment.GetEnvironmentVariable("${awsEndpointConfig}")`, { engineBoundary: true }),
    'model-provider-endpoint',
    awsEndpointConfig,
  );
}
assert.equal(releaseContentFinding('AWS_SECRET_ACCESS_KEY=${AWS_SECRET_ACCESS_KEY}'), null);
assert.equal(
  releaseContentFinding('https://api.anthropic.com/v1/messages', { engineBoundary: true }),
  'model-provider-endpoint',
);
for (const endpoint of [
  'https://api.mistral.ai/v1/chat/completions',
  'https://api.groq.com/openai/v1/chat/completions',
  'https://api.deepseek.com/chat/completions',
  'https://api.together.xyz/v1/chat/completions',
  'https://api.cohere.ai/v1/generate',
  'https://api.cohere.com/v2/chat',
  'https://api.x.ai/v1/chat/completions',
  'https://api.perplexity.ai/chat/completions',
  'https://models.github.ai/inference',
  'https://api-inference.huggingface.co/models/example',
  'https://router.huggingface.co/v1/chat/completions',
  'https://example.us-east-1.aws.endpoints.huggingface.cloud',
  'https://us-central1-aiplatform.googleapis.com',
  'https://semanticus.services.ai.azure.com/models',
]) {
  assert.equal(releaseContentFinding(endpoint, { engineBoundary: true }), 'model-provider-endpoint', endpoint);
}
for (const credential of ['XAI_API_KEY', 'PERPLEXITY_API_KEY']) {
  assert.equal(releaseContentFinding(`process.env.${credential}`, { engineBoundary: true }), 'model-provider-credential');
}
assert.equal(
  releaseContentFinding('https://new-provider.example/v1/infer', { engineBoundary: true, engineSource: true }),
  'unapproved-engine-endpoint',
);
assert.equal(
  releaseContentFinding('https://[::1]/v1/infer', { engineBoundary: true, engineSource: true }),
  'unapproved-engine-endpoint',
);
for (const localEndpoint of [
  'http://localhost:1234/v1/chat/completions',
  'http://localhost:5000/health',
  'http://127.0.0.1:1234/v1/models',
  'http://[::1]:1234/v1/responses',
]) {
  assert.equal(
    releaseContentFinding(localEndpoint, { engineBoundary: true, engineSource: true }),
    'unapproved-engine-endpoint',
    localEndpoint,
  );
  assert.equal(
    releaseContentFinding(Buffer.from(localEndpoint), {
      engineBoundary: true,
      engineSource: true,
      engineBinary: true,
    }),
    'unapproved-engine-endpoint',
    `${localEndpoint} in a packaged binary`,
  );
}
assert.equal(
  releaseContentFinding('https://token:secret@api.fabric.microsoft.com/v1', { engineBoundary: true, engineSource: true }),
  'credentialed-engine-endpoint',
);
assert.equal(
  releaseContentFinding(Buffer.from('https://new-provider.example/v1/infer', 'utf16le'), { engineBoundary: true, engineSource: true }),
  'unapproved-engine-endpoint',
);
assert.equal(
  releaseContentFinding(Buffer.concat([
    Buffer.from([255]),
    Buffer.from('https://token:secret@api.fabric.microsoft.com/v1', 'utf16le'),
  ]), { engineBoundary: true, engineSource: true }),
  'credentialed-engine-endpoint',
);
const utf16beEndpoint = Buffer.from('https://model-provider.corp/v1/infer', 'utf16le');
utf16beEndpoint.swap16();
assert.equal(
  releaseContentFinding(Buffer.concat([Buffer.from([255]), utf16beEndpoint, Buffer.alloc(2)]), {
    engineBoundary: true,
    engineSource: true,
    engineBinary: true,
  }),
  'unapproved-engine-endpoint',
  'a bounded big-endian UTF-16 endpoint at an odd byte offset is scanned',
);
assert.equal(
  releaseContentFinding(Buffer.concat([
    Buffer.from('https://', 'utf16le'),
    Buffer.from('BSJB'),
  ]), { engineBoundary: true, engineSource: true, engineBinary: true }),
  null,
  'adjacent ASCII metadata is not misread as a big-endian UTF-16 hostname',
);
assert.equal(
  releaseContentFinding('https://api.fabric.microsoft.com:443/v1/workspaces', { engineBoundary: true, engineSource: true }),
  null,
);
assert.equal(
  releaseContentFinding('https://semanticus.com.au/pro', { engineBoundary: true, engineSource: true }),
  null,
  'the live product-owned Pro page is an approved engine endpoint',
);
assert.equal(
  releaseContentFinding('https://semanticus.dev/renew', { engineBoundary: true, engineSource: true }),
  'unapproved-engine-endpoint',
  'the dead placeholder host is not approved for release',
);
for (const packagedSource of [
  'extension/engine/Semanticus.Engine.exe',
  'extension/engine/createdump.exe',
  'extension/engine/Semanticus.Engine',
  'extension/engine/Semanticus.Engine.dll',
  'extension/engine/Semanticus.Analysis.dll',
  'extension/engine/Semanticus.Engine.deps.json',
  'extension/engine/workflows/verified-measure.md',
]) {
  assert.equal(isPackagedEngineEndpointPath(packagedSource), true, packagedSource);
  assert.equal(
    releaseContentFinding('https://new-provider.example/v1/infer', {
      engineBoundary: true,
      engineSource: isPackagedEngineEndpointPath(packagedSource),
    }),
    'unapproved-engine-endpoint',
  );
}
const unixExecutableAttributes = (0o100755 << 16) >>> 0;
assert.equal(isPackagedEngineEndpointPath('extension/engine/createdump', unixExecutableAttributes), true);
assert.equal(
  releaseContentFinding('https://new-provider.example/v1/infer', {
    engineBoundary: true,
    engineSource: isPackagedEngineEndpointPath('extension/engine/createdump', unixExecutableAttributes),
  }),
  'unapproved-engine-endpoint',
);
assert.equal(isPackagedEngineEndpointPath('extension/engine/WindowsBase.dll'), true);
assert.equal(isPackagedEngineBinaryPath('extension/engine/WindowsBase.dll'), true);
assert.equal(isPackagedEngineBinaryPath('extension/engine/libhostfxr.so'), true);
assert.equal(isPackagedEngineBinaryPath('extension/engine/libhostfxr.dylib'), true);
assert.equal(isPackagedEngineBinaryPath('extension/engine/icudt.dat'), true);
assert.equal(isPackagedEngineBinaryPath('extension/engine/workflows/verified-measure.md'), false);
assert.equal(
  releaseContentFinding(Buffer.from('https://new-provider.example/v1/infer'), {
    engineBoundary: true,
    engineSource: true,
    engineBinary: true,
  }),
  'unapproved-engine-endpoint',
);
assert.equal(
  releaseContentFinding(Buffer.from('https://model-provider.biz/v1/infer'), {
    engineBoundary: true,
    engineSource: true,
    engineBinary: true,
  }),
  'unapproved-engine-endpoint',
);
for (const endpoint of [
  'https://model-provider.corp/v1/infer',
  'https://model-provider.newdelegation/v1/infer',
  'http://link',
]) {
  assert.equal(
    releaseContentFinding(Buffer.from(endpoint), {
      engineBoundary: true,
      engineSource: true,
      engineBinary: true,
    }),
    'unapproved-engine-endpoint',
    endpoint,
  );
}
assert.equal(
  releaseContentFinding(Buffer.from('http://[runtime'), { engineBoundary: true, engineSource: true, engineBinary: true }),
  null,
);
assert.equal(
  releaseContentFinding(Buffer.from('https://socks4:/'), { engineBoundary: true, engineSource: true, engineBinary: true }),
  null,
  'a URL parser repair of an empty port is not a usable binary endpoint',
);
assert.equal(
  releaseContentFinding('http://link', { engineBoundary: true, engineSource: true }),
  'malformed-engine-endpoint',
);

const signedPe = Buffer.alloc(1024);
signedPe.write('MZ');
signedPe.writeUInt32LE(0x80, 0x3c);
signedPe.write('PE\0\0', 0x80, 'latin1');
signedPe.writeUInt16LE(0xe0, 0x80 + 20);
signedPe.writeUInt16LE(0x10b, 0x80 + 24);
signedPe.writeUInt32LE(768, 0x80 + 24 + 96 + (4 * 8));
signedPe.writeUInt32LE(256, 0x80 + 24 + 96 + (4 * 8) + 4);
signedPe.write('http://www.microsoft.com0', 768, 'ascii');
assert.equal(
  releaseContentFinding(signedPe, { engineBoundary: true, engineSource: true, engineBinary: true }),
  null,
  'Authenticode certificate metadata is not a runtime endpoint',
);
signedPe.write('https://model-provider.corp/v1/infer', 384, 'ascii');
assert.equal(
  releaseContentFinding(signedPe, { engineBoundary: true, engineSource: true, engineBinary: true }),
  'unapproved-engine-endpoint',
  'runtime image data remains fail closed when certificate metadata is excluded',
);
assert.doesNotThrow(() => assertReleaseContentSafe(
  'extension/engine/System.IO.Packaging.dll',
  Buffer.from('http://defaultcontainer/'),
  { engineBoundary: true, engineSource: true, engineBinary: true },
));
assert.throws(
  () => assertReleaseContentSafe(
    'extension/engine/Other.dll',
    Buffer.from('http://defaultcontainer/'),
    { engineBoundary: true, engineSource: true, engineBinary: true },
  ),
  /unapproved-engine-endpoint/,
  'the OPC placeholder exception is scoped to its owning runtime assembly',
);
for (const msalRuntime of [
  'extension/engine/msalruntime.dll',
  'extension/engine/msalruntime_arm64.dll',
  'extension/engine/libmsalruntime.so',
  'extension/engine/msalruntime.dylib',
  'extension/engine/msalruntime_arm64.dylib',
]) {
  assert.doesNotThrow(() => assertReleaseContentSafe(
    msalRuntime,
    Buffer.from('https://login.'),
    { engineBoundary: true, engineSource: true, engineBinary: true },
  ), msalRuntime);
}
const macMsalRuntime = Buffer.alloc(7_009_171);
const passwordPersistenceSelector = ['password', 'persistence', ''].join(':');
const passwordTestSinkSelector = ['password', 'jsTestData', 'eventSink', ''].join(':');
macMsalRuntime.write(passwordPersistenceSelector, 3_497_291, 'ascii');
macMsalRuntime.write(passwordTestSinkSelector, 3_567_414, 'ascii');
macMsalRuntime.write(passwordTestSinkSelector, 7_009_141, 'ascii');
assert.doesNotThrow(() => assertReleaseContentSafe(
  'extension/engine/msalruntime.dylib',
  macMsalRuntime,
  { engineBoundary: true, engineSource: true, engineBinary: true },
));
macMsalRuntime.write(`client_secret=${'S'.repeat(24)}`, 128, 'ascii');
assert.throws(
  () => assertReleaseContentSafe(
    'extension/engine/msalruntime.dylib',
    macMsalRuntime,
    { engineBoundary: true, engineSource: true, engineBinary: true },
  ),
  /hardcoded-generic-secret/,
  'the exact native-symbol exception must not hide a real secret elsewhere in the binary',
);
const splitNativeStrings = Buffer.concat([
  Buffer.from('password:', 'ascii'),
  Buffer.alloc(16),
  Buffer.from('someLongNativeSymbol', 'ascii'),
]);
assert.equal(
  releaseContentFinding(splitNativeStrings, { engineBoundary: true, engineSource: true, engineBinary: true }),
  null,
  'separate native string-table entries must not combine into a synthetic credential assignment',
);
assert.equal(
  releaseContentFinding(['password:', 'someLongSecretValue'].join('\n')),
  'hardcoded-generic-secret',
  'a real multiline source assignment remains detected',
);
assert.throws(
  () => assertReleaseContentSafe(
    'extension/engine/Other.dll',
    Buffer.from('https://login.'),
    { engineBoundary: true, engineSource: true, engineBinary: true },
  ),
  /unapproved-engine-endpoint/,
  'the MSAL authority prefix exception is scoped to the signed runtime assembly',
);
for (const template of [
  'https://replyUrlNotSet',
  'https://enterpriseregistration.',
  'https://{0}/{1}/',
  'https://{0}/dstsv2/{1}/',
]) {
  assert.doesNotThrow(() => assertReleaseContentSafe(
    'extension/engine/Microsoft.Identity.Client.dll',
    Buffer.from(template),
    { engineBoundary: true, engineSource: true, engineBinary: true },
  ), template);
  assert.throws(
    () => assertReleaseContentSafe(
      'extension/engine/Other.dll',
      Buffer.from(template),
      { engineBoundary: true, engineSource: true, engineBinary: true },
    ),
    /(unapproved|malformed)-engine-endpoint/,
    `${template} is scoped to the MSAL managed assembly`,
  );
}
for (const localMsalValue of [
  'http://localhost',
  'http://localhost:port',
  'http://localhost:',
  'http://127.0.0.1:40342/metadata/identity/oauth2/token',
]) {
  assert.doesNotThrow(() => assertReleaseContentSafe(
    'extension/engine/Microsoft.Identity.Client.dll',
    Buffer.from(localMsalValue),
    { engineBoundary: true, engineSource: true, engineBinary: true },
  ), localMsalValue);
}
assert.equal(
  releaseContentFinding('http://127.0.0.1:40342/metadata/identity/oauth2/token', {
    engineBoundary: true,
    engineSource: true,
  }),
  'unapproved-engine-endpoint',
  'the Azure Arc identity URL is not globally allowed outside its owning runtime assembly',
);
assert.doesNotThrow(() => assertReleaseContentSafe(
  'extension/engine/Azure.Identity.dll',
  Buffer.from('http://localhost'),
  { engineBoundary: true, engineSource: true, engineBinary: true },
));
for (const identityMetadata of [
  ['extension/engine/Azure.Identity.dll', 'http://169.254.169.254/metadata/identity/oauth2/token'],
  ['extension/engine/Microsoft.Identity.Client.dll', 'http://169.254.169.254/metadata/instance/compute/location'],
]) {
  assert.doesNotThrow(() => assertReleaseContentSafe(
    identityMetadata[0],
    Buffer.from(identityMetadata[1]),
    { engineBoundary: true, engineSource: true, engineBinary: true },
  ), identityMetadata[1]);
}
assert.throws(
  () => assertReleaseContentSafe(
    'extension/engine/Microsoft.Identity.Client.dll',
    Buffer.from('http://169.254.169.254'),
    { engineBoundary: true, engineSource: true, engineBinary: true },
  ),
  /unapproved-engine-endpoint/,
  'the bare metadata host requires the exact pinned Microsoft Identity Client binary',
);
const awsMetadataCredentialPath = 'http://169.254.169.254/latest/meta-data/iam/security-credentials/example';
assert.equal(
  releaseContentFinding(awsMetadataCredentialPath, { engineBoundary: true, engineSource: true }),
  'unapproved-engine-endpoint',
);
assert.throws(
  () => assertReleaseContentSafe(
    'extension/engine/Azure.Identity.dll',
    Buffer.from(awsMetadataCredentialPath),
    { engineBoundary: true, engineSource: true, engineBinary: true },
  ),
  /unapproved-engine-endpoint/,
  'identity assembly exceptions do not allow AWS credential-metadata routes',
);
assert.doesNotThrow(() => assertReleaseContentSafe(
  'extension/engine/Microsoft.AnalysisServices.Core.dll',
  Buffer.from('https://{0}/AASRedirect/mappings/lookup'),
  { engineBoundary: true, engineSource: true, engineBinary: true },
));
assert.throws(
  () => assertReleaseContentSafe(
    'extension/engine/Microsoft.AnalysisServices.Core.dll',
    Buffer.from('https://{0}/v1/infer'),
    { engineBoundary: true, engineSource: true, engineBinary: true },
  ),
  /unapproved-engine-endpoint/,
  'only the inventoried TOM service templates are exempted',
);
const inertDictionary = Buffer.from('https://model-provider.corp/v1/infer');
const inertContainer = Buffer.concat([Buffer.from('prefix'), inertDictionary, Buffer.from('suffix')]);
const inertRange = {
  offset: 6,
  length: inertDictionary.length,
  sha256: createHash('sha256').update(inertDictionary).digest('hex'),
};
assert.equal(
  releaseContentFinding(inertContainer, {
    engineBoundary: true,
    engineSource: true,
    engineBinary: true,
    binaryIgnoredRanges: [inertRange],
  }),
  null,
  'an exact hash-pinned inert binary range is excluded',
);
const mismatchedInertContainer = Buffer.concat([
  Buffer.from('prefix\0'),
  inertDictionary,
  Buffer.from('\0suffix'),
]);
assert.equal(
  releaseContentFinding(mismatchedInertContainer, {
    engineBoundary: true,
    engineSource: true,
    engineBinary: true,
    binaryIgnoredRanges: [inertRange],
  }),
  'unapproved-engine-endpoint',
  'an inert range with different bytes is not excluded',
);
assert.equal(
  releaseContentFinding(Buffer.concat([inertContainer, Buffer.from('\0https://other-provider.corp/v1')]), {
    engineBoundary: true,
    engineSource: true,
    engineBinary: true,
    binaryIgnoredRanges: [inertRange],
  }),
  'unapproved-engine-endpoint',
  'endpoint data outside a hash-pinned inert range still fails closed',
);
assert.equal(
  releaseContentFinding('<PackageReference Include="Azure.AI.OpenAI" />', { engineBoundary: true }),
  'model-provider-package',
);
assert.equal(
  releaseContentFinding('<PackageReference Include="OpenAI" />', { engineBoundary: true }),
  'model-provider-package',
);
assert.equal(
  releaseContentFinding('using OpenAI.Chat;', { engineBoundary: true }),
  'model-provider-sdk',
);
assert.equal(
  releaseContentFinding('using Azure.AI.Inference;', { engineBoundary: true }),
  'model-provider-sdk',
);
assert.equal(
  releaseContentFinding('<PackageReference Include="Microsoft.Extensions.AI.AzureAIInference" />', { engineBoundary: true }),
  'model-provider-package',
);
assert.equal(
  releaseContentFinding('<PackageReference Include="Microsoft.Extensions.AI.Ollama" />', { engineBoundary: true }),
  'model-provider-package',
);
assert.equal(
  releaseContentFinding('new OllamaChatClient()', { engineBoundary: true }),
  'model-provider-sdk',
);
assert.equal(
  releaseContentFinding('AzureAIInferenceExtensions.AddChatClient()', { engineBoundary: true }),
  'model-provider-sdk',
);
assert.equal(
  releaseContentFinding('Microsoft.Extensions.AI.Abstractions', { engineBoundary: true }),
  null,
);
assert.equal(
  releaseContentFinding('using Microsoft.Extensions.AI; IChatClient client;', { engineBoundary: true }),
  null,
);
for (const sharedImport of ['Directory.Build.props', 'build/Directory.Build.targets', 'Directory.Packages.props']) {
  assert.equal(isEngineOwnedPath(sharedImport), true, `${sharedImport} affects the engine MSBuild boundary`);
  assert.equal(
    releaseContentFinding('<PackageReference Include="Microsoft.Extensions.AI.OpenAI" />', {
      engineBoundary: isEngineOwnedPath(sharedImport),
    }),
    'model-provider-package',
  );
}
assert.equal(releaseContentFinding('ANTHROPIC_API_KEY must be unset.'), null);
assert.equal(credentialPathFinding('docs/.env.production'), 'credential-suffix');
assert.equal(credentialPathFinding('docs/prod.env'), 'credential-suffix');
assert.equal(credentialPathFinding('docs/settings.env.local'), 'credential-suffix');
assert.equal(credentialPathFinding('extension/media/.aws/credentials'), 'credential-directory');
assert.equal(credentialPathFinding('extension/engine/NuGet.Config'), 'credential-filename');
assert.equal(credentialPathFinding('extension/media/.env/notes.txt'), 'credential-suffix');
assert.equal(credentialPathFinding('extension/media/id_rsa/readme.txt'), 'credential-filename');
assert.equal(credentialPathFinding('extension/media/private.pem/notes.txt'), 'credential-suffix');
assert.equal(credentialPathFinding('docs/release-notes.md'), null);
assert.equal(releaseContentFinding('password=/pwd=/access_token= in a message is masked'), null);
assert.equal(releaseContentFinding('cs += $";Password={bearerToken}";'), null);
assert.equal(releaseContentFinding('password:!0,range:!0,search:!0,terminal:!0'), null);
assert.equal(releaseContentFinding('FABRIC_SECRET: ${{ secrets.FABRIC_SECRET }}'), null);

const repositoryRoot = fileURLToPath(new URL('../../', import.meta.url));
const listed = spawnSync('git', ['ls-files', '-z'], {
  cwd: repositoryRoot,
  encoding: 'utf8',
  maxBuffer: 10 * 1024 * 1024,
});
if (listed.error) throw listed.error;
assert.equal(listed.status, 0, `git ls-files failed with exit code ${listed.status}`);
const tracked = listed.stdout.split('\0').filter(Boolean);
assert.ok(tracked.length > 500, `release security scan found only ${tracked.length} tracked paths`);

let scannedFiles = 0;
let engineFiles = 0;
for (const relative of tracked) {
  const normalized = relative.replaceAll('\\', '/');
  const pathFinding = credentialPathFinding(normalized);
  assert.equal(pathFinding, null, `Release security gate rejected ${normalized}: ${pathFinding}`);

  const absolute = path.join(repositoryRoot, ...normalized.split('/'));
  const stat = lstatSync(absolute);
  assert.equal(stat.isSymbolicLink(), false, `Release security gate rejects tracked symbolic link: ${normalized}`);
  if (!stat.isFile()) continue; // The vendored TOM donor is a pinned gitlink, not release-tree content.

  const engineBoundary = isEngineOwnedPath(normalized);
  const contentFinding = releaseContentFinding(readFileSync(absolute), { engineBoundary, engineSource: engineBoundary });
  assert.equal(contentFinding, null, `Release security gate rejected ${normalized}: ${contentFinding}`);
  scannedFiles++;
  if (engineBoundary) engineFiles++;
}

assert.ok(scannedFiles > 500, `release security scan opened only ${scannedFiles} tracked files`);
assert.ok(engineFiles > 100, `release security scan opened only ${engineFiles} engine-owned files`);
console.log(`release security boundary tests passed: ${scannedFiles} tracked files, ${engineFiles} engine-owned files`);
