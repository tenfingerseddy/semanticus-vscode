import { isIP } from 'node:net';
import { createHash } from 'node:crypto';
import { parse as parseDomain } from 'tldts';

const CREDENTIAL_DIRECTORIES = new Set([
  '.aws', '.azure', '.docker', '.gnupg', '.kube', '.ssh',
]);

const CREDENTIAL_BASENAMES = new Set([
  '.netrc', '.npmrc', '.pypirc', '.yarnrc', '.yarnrc.yml', '_netrc',
  'accesstokens.json', 'application_default_credentials.json', 'auth.json',
  'credentials', 'credentials.json', 'id_dsa', 'id_ecdsa', 'id_ed25519', 'id_rsa',
  'nuget.config', 'service-account.json', 'secrets.json', 'tokens.json',
]);

const CREDENTIAL_SUFFIX = /(?:^|\/)(?:\.env(?:\..*)?|[^/]+\.env(?:\..*)?|[^/]+\.(?:key|p12|pem|pfx|snk))$/i;

const SECRET_TOKEN_RULES = [
  ['private-key', /-----BEGIN (?:ENCRYPTED |RSA |EC |DSA |OPENSSH |PGP )?PRIVATE KEY(?: BLOCK)?-----/i],
  ['github-token', /\b(?:gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{50,})\b/],
  ['npm-token', /\bnpm_[A-Za-z0-9]{36,}\b/],
  ['slack-token', /\bxox[baprs]-[A-Za-z0-9-]{20,}\b/],
  ['aws-access-key', /\b(?:AKIA|ASIA)[A-Z0-9]{16}\b/],
  ['google-api-key', /\bAIza[0-9A-Za-z_-]{35}\b/],
  ['azure-devops-pat', /\b[A-Za-z0-9]{75}AZDO[A-Za-z0-9]{5}\b/],
  ['jwt', /\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{40,}\b/],
  ['model-provider-token', /\b(?:sk-ant-(?:api\d{2}-)?|sk-(?:proj-|svcacct-)?)[A-Za-z0-9_-]{20,}\b/],
  ['stripe-secret', /\bsk_(?:live|test)_[0-9A-Za-z]{20,}\b/],
  ['azure-storage-key', /\bAccountKey\s*=\s*[A-Za-z0-9+/]{40,}={0,2}/i],
  ['shared-access-signature', /\bSharedAccessSignature\s*=\s*[^;\r\n]*\bsig=[A-Za-z0-9%+/=_-]{20,}/i],
];

const SECRET_ASSIGNMENT_RULES = [
  ['hardcoded-aws-secret-xml', /<(?=[^>]{0,1024}\b(?:key|name)\s*=\s*["'](?:(?:aws|amazon)[_-]?secret(?:[_-]?access)?[_-]?key|(?:aws|amazon)[_-]?(?:session|security)[_-]?token|aws[_-]?bearer[_-]?token[_-]?bedrock|secretAccessKey)["'])(?=[^>]{0,1024}\bvalue\s*=\s*(?:"(?![^"\r\n]*\$\{)[^"\r\n]{16,}"|'(?![^'\r\n]*\$\{)[^'\r\n]{16,}'))[^>]*>/i],
  ['hardcoded-provider-secret-xml', /<(?=[^>]{0,1024}\b(?:key|name)\s*=\s*["'](?:ANTHROPIC_API_KEY|OPENAI_API_KEY|AZURE_OPENAI_API_KEY|GOOGLE_API_KEY|GEMINI_API_KEY|COHERE_API_KEY|MISTRAL_API_KEY|DEEPSEEK_API_KEY|GROQ_API_KEY|TOGETHER_API_KEY|XAI_API_KEY|PERPLEXITY_API_KEY|HF_TOKEN|HUGGING_FACE_HUB_TOKEN|HUGGINGFACEHUB_API_TOKEN|FABRIC_SECRET|AZURE_CLIENT_SECRET|POWERBI_CLIENT_SECRET|VSCE_PAT)["'])(?=[^>]{0,1024}\bvalue\s*=\s*(?:"(?![^"\r\n]*\$\{)[^"\r\n]{16,}"|'(?![^'\r\n]*\$\{)[^'\r\n]{16,}'))[^>]*>/i],
  ['hardcoded-generic-secret-xml', /<(?=[^>]{0,1024}\b(?:key|name)\s*=\s*["'](?:password|pwd|client[_-]?secret|clientSecret|api[_-]?key|apiKey)["'])(?=[^>]{0,1024}\bvalue\s*=\s*(?:"(?![^"\r\n]*\$\{)[^"\r\n]{12,}"|'(?![^'\r\n]*\$\{)[^'\r\n]{12,}'))[^>]*>/i],
  ['hardcoded-aws-secret', /\b(?:(?:aws|amazon)[_-]?secret(?:[_-]?access)?[_-]?key|(?:aws|amazon)[_-]?(?:session|security)[_-]?token|aws[_-]?bearer[_-]?token[_-]?bedrock|secretAccessKey)\b["']?\s*[:=]\s*(?:"(?![^"\r\n]*\$\{)[^"\r\n]{16,}"|'(?![^'\r\n]*\$\{)[^'\r\n]{16,}'|(?!\$?\{[A-Za-z_][A-Za-z0-9_.]*\}(?=$|[\s"'<>;,]))[^\s"'<>;,=]{16,}={0,2})/i],
  ['hardcoded-provider-secret', /\b(?:ANTHROPIC_API_KEY|OPENAI_API_KEY|AZURE_OPENAI_API_KEY|GOOGLE_API_KEY|GEMINI_API_KEY|COHERE_API_KEY|MISTRAL_API_KEY|DEEPSEEK_API_KEY|GROQ_API_KEY|TOGETHER_API_KEY|XAI_API_KEY|PERPLEXITY_API_KEY|HF_TOKEN|HUGGING_FACE_HUB_TOKEN|HUGGINGFACEHUB_API_TOKEN|FABRIC_SECRET|AZURE_CLIENT_SECRET|POWERBI_CLIENT_SECRET|VSCE_PAT)\b["']?\s*[:=]\s*(?:"(?![^"\r\n]*\$\{)[^"\r\n]{16,}"|'(?![^'\r\n]*\$\{)[^'\r\n]{16,}'|(?!\$?\{[A-Za-z_][A-Za-z0-9_.]*\}(?=$|[\s"'<>;,]))[^\s"'<>;,=]{16,}={0,2})/i],
  ['hardcoded-generic-secret', /\b(?:password|pwd|client[_-]?secret|clientSecret|api[_-]?key|apiKey)\b["']?\s*[:=]\s*(?:"(?![^"\r\n]*\$\{)[^"\r\n]{12,}"|'(?![^'\r\n]*\$\{)[^'\r\n]{12,}'|(?!(?:\/)?(?:password|pwd|access[_-]?token)\s*=)(?!\$?\{[A-Za-z_][A-Za-z0-9_.]*\}(?=$|[\s"'<>;,]))[^\s"'<>;,=]{12,}={0,2})/i],
];

const ENGINE_INFERENCE_RULES = [
  ['model-provider-credential', /\b(?:ANTHROPIC_API_KEY|OPENAI_API_KEY|AZURE_OPENAI_API_KEY|GOOGLE_API_KEY|GEMINI_API_KEY|COHERE_API_KEY|MISTRAL_API_KEY|DEEPSEEK_API_KEY|GROQ_API_KEY|TOGETHER_API_KEY|XAI_API_KEY|PERPLEXITY_API_KEY|HF_TOKEN|HUGGING_FACE_HUB_TOKEN|HUGGINGFACEHUB_API_TOKEN|AWS_ACCESS_KEY_ID|AWS_ACCESS_KEY|AWS_SECRET_ACCESS_KEY|AWS_SECRET_KEY|AWS_SESSION_TOKEN|AWS_SECURITY_TOKEN|AWS_BEARER_TOKEN_BEDROCK|AMAZON_ACCESS_KEY_ID|AMAZON_SECRET_ACCESS_KEY|AMAZON_SECURITY_TOKEN|AWS_PROFILE|AWS_DEFAULT_PROFILE|AWS_SHARED_CREDENTIALS_FILE|AWS_CONFIG_FILE|AWS_WEB_IDENTITY_TOKEN_FILE|AWS_ROLE_ARN|AWS_ROLE_SESSION_NAME|AWS_CONTAINER_CREDENTIALS_RELATIVE_URI|AWS_CONTAINER_CREDENTIALS_FULL_URI|AWS_CONTAINER_AUTHORIZATION_TOKEN|AWS_CONTAINER_AUTHORIZATION_TOKEN_FILE)\b/i],
  ['model-provider-endpoint', /\b(?:api\.anthropic\.com|api\.openai\.com|[a-z0-9.-]+\.openai\.azure\.com|[a-z0-9.-]+\.services\.ai\.azure\.com|[a-z0-9.-]+\.inference\.ai\.azure\.com|generativelanguage\.googleapis\.com|[a-z0-9-]+-aiplatform\.googleapis\.com|bedrock-runtime\.[a-z0-9-]+\.amazonaws\.com|AWS_ENDPOINT_URL(?:_[A-Z0-9_]+)?|AWS_BEDROCK_RUNTIME_ENDPOINT|api\.mistral\.ai|api\.groq\.com|api\.deepseek\.com|api\.together\.(?:xyz|ai)|api\.cohere\.(?:ai|com)|api\.x\.ai|api\.perplexity\.ai|models\.github\.ai|(?:api-inference|router)\.huggingface\.co|[a-z0-9.-]+\.endpoints\.huggingface\.cloud|localhost:11434)\b/i],
  ['model-provider-package', /<(?:PackageReference|PackageVersion)\b[^>]*\bInclude\s*=\s*["'](?:Anthropic(?:\.SDK)?|OpenAI|Azure\.AI\.(?:Inference|OpenAI)|Microsoft\.Extensions\.AI\.(?!Abstractions["'])[^"']+|Microsoft\.SemanticKernel|AWSSDK\.BedrockRuntime|Google\.GenerativeAI|OllamaSharp|LangChain[^"']*)["']/i],
  ['model-provider-sdk', /\b(?:AnthropicClient|Anthropic\.SDK|OpenAIClient|OpenAI\.(?:Assistants|Audio|Chat|Embeddings|Files|FineTuning|Images|Models|Realtime|Responses|VectorStores)|Azure\.AI\.(?:Inference|OpenAI)|Microsoft\.Extensions\.AI\.(?:AzureAIInference|Ollama|OpenAI)|(?:AzureAIInference|Ollama|OpenAI)ChatClient|AzureAIInferenceExtensions|Microsoft\.SemanticKernel|AmazonBedrockRuntimeClient|Amazon\.BedrockRuntime|Google\.GenerativeAI|OllamaSharp|LangChain)\b/i],
];

const ENGINE_ALLOWED_HTTP_HOSTS = new Set([
  '0.0.0.2',
  'aarnott.github.io',
  'aka.ms',
  'analysis.chinacloudapi.cn',
  'analysis.cloudapi.de',
  'analysis.eaglex.ic.gov',
  'analysis.microsoft.scloud',
  'analysis.sovcloud-api.fr',
  'analysis.usgovcloudapi.net',
  'analysis.windows-int.net',
  'analysis.windows.net',
  'api.high.powerbigov.us',
  'api.daxlib.org',
  'api.fabric.microsoft.com',
  'api.mil.powerbigov.us',
  'api.powerbi.cn',
  'api.powerbi.com',
  'api.powerbi.eaglex.ic.gov',
  'api.powerbi.microsoft.scloud',
  'api.powerbi.sovcloud-api.fr',
  'api.powerbigov.us',
  'aswl-onebox-pbi-mock.pbidedicated.windows-int.net',
  'bi-azure-int3-redirect.analysis-df.windows.net',
  'biazure-int-edog-redirect.analysis-df.windows.net',
  'canaryhigh.analysis.usgovcloudapi.net',
  'canaryhighapi.high.powerbigov.us',
  'collations.microsoft.com',
  'crl3.digicert.com',
  'crl4.digicert.com',
  'dailyapi.powerbi.com',
  'database.windows.net',
  'developer.microsoft.com',
  'dev.azure.com',
  'df-msit-scus-redirect.analysis.windows.net',
  'docs.oasis-open.org',
  'dxtapi.powerbi.com',
  'enterpriseregistration.windows.net',
  'exslt.org',
  'global.asazure.windows.net',
  'github.com',
  'go.microsoft.com',
  'high.analysis.usgovcloudapi.net',
  'james.newtonking.com',
  'json-schema.org',
  'learn.microsoft.com',
  'login.chinacloudapi.cn',
  'login.microsoft.com',
  'login.microsoftonline.com',
  'login.microsoftonline.de',
  'login.microsoftonline.eaglex.ic.gov',
  'login.microsoftonline.microsoft.scloud',
  'login.microsoftonline.us',
  'login.sovcloud-api.fr',
  'login.windows-ppe.net',
  'login.windows.local',
  'login.windows.net',
  'management.azure.com',
  'management.chinacloudapi.cn',
  'management.microsoftazure.de',
  'management.usgovcloudapi.net',
  'manifests.microsoft.com',
  'microsoft.github.io',
  'microsoft.com',
  'mil.analysis.usgovcloudapi.net',
  'msdn.microsoft.com',
  'my.netscape.com',
  'onebox-redirect.analysis.windows-int.net',
  'openid.net',
  'opentelemetry.io',
  'powerbiapi.analysis-df.windows.net',
  'powerbistagingapi.analysis.windows.net',
  'purl.org',
  'raw.githubusercontent.com',
  'schemas.microsoft.com',
  'schemas.datacontract.org',
  'schemas.openxmlformats.org',
  'schemas.xmlsoap.org',
  'semanticus.com.au',
  'tipedog-redirect.analysis-df.windows.net',
  'wabi-daily-us-east2-redirect.analysis.windows.net',
  'wabi-staging-us-east-redirect.analysis.windows.net',
  'www.bing.com',
  'www.digicert.com',
  'www.microsoft.com',
  'www.newtonsoft.com',
  'www.nuget.org',
  'www.w3.org',
]);

const TOM_SERVICE_ENDPOINT_TEMPLATES = new Set([
  'https://{0}',
  'https://{0}/aasredirect/public/mappings/exists',
  'https://{0}/api/data/v9.0/getpowerbidatasetdetails(datasetname=',
  'https://{0}/api/data/v9.0/getpowerbidatasetembedtoken(datasetname=',
  'https://{0}/aasredirect/mappings/lookup',
  'https://{0}/powerbi/databases/v201606/workspaces',
  'https://{0}/powerbi/databases/v201606/workspaces?includemyworkspace=true',
  'https://{0}/powerbi/databases/v201606/workspaces/{1}',
  'https://{0}/powerbi/databases/v201606/workspaces/{1}/getdatabasename',
  'https://{0}/powerbi/databases/v201606/datasets/{1}/getdatasetdetailsforanalyzeinexcel',
  'https://{0}/powerbi/databases/v201606/datasets/{1}/getdatasetdetailsforinternalserviceconnections',
  'https://{0}/metadata/v201606/generateastoken',
  'https://{0}/metadata/v201606/generateastokenforinternalserviceconnections',
]);

const PACKAGED_BINARY_ENDPOINT_EXCEPTIONS = new Map([
  // System.IO.Packaging uses this fixed URI solely as an OPC container base, never as a network endpoint.
  ['extension/engine/system.io.packaging.dll', new Set(['http://defaultcontainer/'])],
  // MSAL combines this incomplete cloud-authority prefix with a separately validated environment suffix.
  ['extension/engine/msalruntime.dll', new Set(['https://login.'])],
  // MSAL managed-code sentinels/templates are completed or rejected before any network request.
  ['extension/engine/microsoft.identity.client.dll', new Set([
    'https://replyurlnotset',
    'https://enterpriseregistration.',
    'https://{0}/{1}/',
    'https://{0}/dstsv2/{1}/',
    'http://localhost',
    'http://localhost:port',
    'http://localhost:',
    'http://127.0.0.1:40342/metadata/identity/oauth2/token',
    'http://169.254.169.254/metadata/instance/compute/location',
  ])],
  // Azure.Identity uses these exact browser-loopback and managed-identity routes.
  ['extension/engine/azure.identity.dll', new Set([
    'http://localhost',
    'http://169.254.169.254/metadata/identity/oauth2/token',
  ])],
  // TOM's service-discovery routes substitute a separately resolved Analysis Services host.
  ['extension/engine/microsoft.analysisservices.core.dll', TOM_SERVICE_ENDPOINT_TEMPLATES],
  ['extension/engine/microsoft.analysisservices.adomdclient.dll', TOM_SERVICE_ENDPOINT_TEMPLATES],
]);

const PACKAGED_HASHED_BINARY_ENDPOINT_EXCEPTIONS = new Map([
  ['extension/engine/microsoft.identity.client.dll', new Map([
    ['http://169.254.169.254', 'd224cf0d1ad2692bf04f500de673440c03fdd44cdc6280ea51e04d6c924602fd'],
  ])],
]);

const PACKAGED_BINARY_IGNORED_RANGES = new Map([
  ['extension/engine/system.io.compression.native.dll', [{
    offset: 345488,
    length: 122784,
    sha256: '20e42eb1b511c21806d4d227d07e5dd06877d8ce7b3a817f378f313653f35c70',
    purpose: '.NET 8.0.28 Brotli static dictionary',
  }]],
]);

function contentViews(content) {
  const buffer = Buffer.isBuffer(content) ? content : Buffer.from(String(content), 'utf8');
  const views = [buffer.toString('latin1')];
  if (buffer.length >= 4) {
    views.push(buffer.toString('utf16le'));
    views.push(buffer.subarray(1).toString('utf16le'));
  }
  return views;
}

function withoutPeCertificateTable(content) {
  if (!Buffer.isBuffer(content) || content.length < 0x40 || content.toString('ascii', 0, 2) !== 'MZ') return content;
  const peOffset = content.readUInt32LE(0x3c);
  if (peOffset > content.length - 24 || content.toString('latin1', peOffset, peOffset + 4) !== 'PE\0\0') return content;

  const optionalHeader = peOffset + 24;
  const optionalHeaderSize = content.readUInt16LE(peOffset + 20);
  const optionalHeaderEnd = optionalHeader + optionalHeaderSize;
  if (optionalHeaderEnd > content.length || optionalHeaderSize < 40) return content;

  const magic = content.readUInt16LE(optionalHeader);
  const dataDirectories = optionalHeader + (magic === 0x10b ? 96 : magic === 0x20b ? 112 : optionalHeaderSize);
  const certificateDirectory = dataDirectories + (4 * 8);
  if (certificateDirectory + 8 > optionalHeaderEnd) return content;

  // The PE security directory is an inert file-offset certificate table, not executable image data.
  // Excluding it avoids treating Authenticode DER boundaries as runtime URL suffixes.
  const certificateOffset = content.readUInt32LE(certificateDirectory);
  const certificateSize = content.readUInt32LE(certificateDirectory + 4);
  if (certificateOffset === 0 || certificateSize === 0 || certificateOffset > content.length - certificateSize) return content;
  const sanitized = Buffer.from(content);
  sanitized.fill(0, certificateOffset, certificateOffset + certificateSize);
  return sanitized;
}

function withoutKnownInertBinaryRanges(content, ranges) {
  if (!Buffer.isBuffer(content) || ranges.length === 0) return content;
  let sanitized;
  for (const range of ranges) {
    if (range.offset > content.length - range.length) continue;
    const candidate = content.subarray(range.offset, range.offset + range.length);
    const actual = createHash('sha256').update(candidate).digest('hex');
    if (actual !== range.sha256) continue;
    sanitized ??= Buffer.from(content);
    sanitized.fill(0, range.offset, range.offset + range.length);
  }
  return sanitized ?? content;
}

function assignmentViews(content) {
  if (!Buffer.isBuffer(content)) return [String(content)];
  const buffer = content;
  let controls = 0;
  for (const byte of buffer) {
    if (byte === 0 || byte < 9 || (byte > 13 && byte < 32)) controls++;
  }
  const latin = buffer.toString('latin1');
  const ascii = (latin.match(/[\x20-\x7e]{8,}/g) ?? []).join('\n');
  const boundedUtf16Runs = (pattern) => [...latin.matchAll(pattern)]
    .filter((match) => {
      const next = buffer[match.index + match[0].length];
      return next === undefined || next < 0x20 || next > 0x7e;
    })
    .map((match) => match[0].replaceAll('\0', ''))
    .join('\n');
  const utf16le = boundedUtf16Runs(/(?:[\x20-\x7e]\x00){8,}/g);
  const utf16be = boundedUtf16Runs(/(?:\x00[\x20-\x7e]){8,}/g);
  const extracted = [ascii, utf16le, utf16be];
  return controls <= Math.max(1, Math.floor(buffer.length / 200))
    ? [buffer.toString('utf8'), latin, ...extracted]
    : extracted;
}

function findRuleInViews(views, rules) {
  for (const view of views) {
    for (const [id, pattern] of rules) {
      if (pattern.test(view)) return id;
    }
  }
  return null;
}

function findRule(content, rules) {
  return findRuleInViews(contentViews(content), rules);
}

function isPlausibleEndpointHost(host) {
  const unbracketed = host.startsWith('[') && host.endsWith(']') ? host.slice(1, -1) : host;
  if (isIP(unbracketed) || host === 'localhost') return true;
  const parsed = parseDomain(host, { allowPrivateDomains: true, validateHostname: true });
  return Boolean(parsed.domain)
    && (parsed.isIcann === true || parsed.isPrivate === true || host.endsWith('.example'));
}

function isIncompleteBinaryEndpoint(candidate) {
  // WHATWG URL repairs an empty port such as `https://socks4:/`; it is still not a usable endpoint.
  return /^https?:\/\/[^./:\s]+:\/$/i.test(candidate);
}

function engineSourceEndpointFinding(content, {
  tolerateBinaryFragments = false,
  binaryEndpointExceptions = new Set(),
  binaryIgnoredRanges = [],
} = {}) {
  const endpointContent = tolerateBinaryFragments
    ? withoutKnownInertBinaryRanges(withoutPeCertificateTable(content), binaryIgnoredRanges)
    : content;
  const views = assignmentViews(endpointContent);
  for (const text of views) {
    for (const match of text.matchAll(/\bhttps?:\/\/[^\s"'<>`)]+/gi)) {
      if (tolerateBinaryFragments && binaryEndpointExceptions.has(match[0].toLowerCase())) continue;
      let endpoint;
      let host;
      try {
        endpoint = new URL(match[0]);
        host = endpoint.hostname.toLowerCase();
      } catch {
        if (tolerateBinaryFragments) continue;
        return 'malformed-engine-endpoint';
      }
      if (endpoint.username || endpoint.password) return 'credentialed-engine-endpoint';
      if (ENGINE_ALLOWED_HTTP_HOSTS.has(host)) continue;
      if (!isPlausibleEndpointHost(host)) {
        if (tolerateBinaryFragments && isIncompleteBinaryEndpoint(match[0])) continue;
        if (tolerateBinaryFragments) return 'unapproved-engine-endpoint';
        return 'malformed-engine-endpoint';
      }
      return 'unapproved-engine-endpoint';
    }
  }
  return null;
}

export function credentialPathFinding(fileName) {
  const normalized = fileName.replaceAll('\\', '/');
  const segments = normalized.split('/').filter(Boolean);
  if (segments.some((segment) => CREDENTIAL_DIRECTORIES.has(segment.toLowerCase()))) return 'credential-directory';
  if (segments.some((segment) => CREDENTIAL_BASENAMES.has(segment.toLowerCase()))) return 'credential-filename';
  if (segments.some((segment) => CREDENTIAL_SUFFIX.test(segment))) return 'credential-suffix';
  return null;
}

export function releaseContentFinding(content, {
  engineBoundary = false,
  engineSource = false,
  engineBinary = false,
  binaryEndpointExceptions = new Set(),
  binaryIgnoredRanges = [],
} = {}) {
  const printable = assignmentViews(content);
  return findRule(content, SECRET_TOKEN_RULES)
    || findRuleInViews(printable, SECRET_TOKEN_RULES)
    || findRuleInViews(printable, SECRET_ASSIGNMENT_RULES)
    || (engineBoundary
      ? findRule(content, ENGINE_INFERENCE_RULES) || findRuleInViews(printable, ENGINE_INFERENCE_RULES)
      : null)
    || (engineSource ? engineSourceEndpointFinding(content, {
      tolerateBinaryFragments: engineBinary,
      binaryEndpointExceptions,
      binaryIgnoredRanges,
    }) : null);
}

export function assertReleaseContentSafe(fileName, content, options) {
  const normalized = fileName.replaceAll('\\', '/').toLowerCase();
  const binaryEndpointExceptions = new Set(PACKAGED_BINARY_ENDPOINT_EXCEPTIONS.get(normalized) ?? []);
  const hashPinned = PACKAGED_HASHED_BINARY_ENDPOINT_EXCEPTIONS.get(normalized);
  if (hashPinned) {
    const actual = createHash('sha256').update(content).digest('hex');
    for (const [endpoint, expected] of hashPinned) if (actual === expected) binaryEndpointExceptions.add(endpoint);
  }
  const finding = releaseContentFinding(content, {
    ...options,
    binaryEndpointExceptions,
    binaryIgnoredRanges: PACKAGED_BINARY_IGNORED_RANGES.get(normalized) ?? [],
  });
  if (finding) throw new Error(`Release security gate rejected ${fileName}: ${finding}`);
}

export function isEngineOwnedPath(fileName) {
  const normalized = fileName.replaceAll('\\', '/');
  return /^(?:Semanticus\.(?:Analysis|Core|Engine))(?:\/|$)/i.test(normalized)
    || /(?:^|\/)(?:Directory\.Build\.(?:props|targets)|Directory\.Packages\.props)$/i.test(normalized)
    || normalized.startsWith('extension/engine/');
}

export function isPackagedEngineEndpointPath(fileName, externalFileAttributes = 0) {
  const normalized = fileName.replaceAll('\\', '/');
  return normalized.startsWith('extension/engine/') && !normalized.endsWith('/');
}

export function isPackagedEngineBinaryPath(fileName, externalFileAttributes = 0) {
  const normalized = fileName.replaceAll('\\', '/');
  return isPackagedEngineEndpointPath(normalized, externalFileAttributes)
    && !/\.(?:config|ini|json|md|props|targets|toml|txt|xml|ya?ml)$/i.test(normalized);
}
