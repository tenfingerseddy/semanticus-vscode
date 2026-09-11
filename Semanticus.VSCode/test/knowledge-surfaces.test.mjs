import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const vscodeRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repoRoot = resolve(vscodeRoot, '..');
const read = (fromRoot, file) => readFileSync(resolve(fromRoot, file), 'utf8');

const knowledge = read(vscodeRoot, 'webview/src/knowledge.tsx');
const rpcTarget = read(repoRoot, 'Semanticus.Engine/EngineRpcTarget.cs');
const mcpTools = read(repoRoot, 'Semanticus.Engine/McpTools.cs');

// D-107: the Knowledge tab shipped as Primer-only. The in-app guide still describes Insights,
// Recall and Purge. This test pins those surfaces on the same view as the Primer document.

assert.match(knowledge, /rpc<PrimerDocument>\('getPrimer'\)/, 'Primer document must remain on the Knowledge tab');
assert.match(knowledge, /rpc<PrimerDocument>\('setPrimer'/, 'Primer save must remain on the Knowledge tab');

assert.match(knowledge, /'listInsights'/, 'Insights must load through listInsights');
assert.match(knowledge, /'upvoteInsight'/, 'Insights must expose upvote');
assert.match(knowledge, /'downvoteInsight'/, 'Insights must expose downvote');
assert.match(knowledge, /'editInsight'/, 'Insights must expose edit');
assert.match(knowledge, /'deleteInsight'/, 'Insights must expose delete');
assert.match(knowledge, /'approveInsight'/, 'Pending insights must be approvable');
assert.match(knowledge, /Pending approval/, 'the guide\'s Pending approval section must exist');
assert.match(knowledge, /Upvote/, 'the guide\'s Upvote control must exist');
assert.match(knowledge, /Downvote/, 'the guide\'s Downvote control must exist');

assert.match(knowledge, /'recallExperience'/, 'Recall must call recallExperience');
assert.match(knowledge, /'getModelFingerprint'/, 'Recall must show the model fingerprint');
assert.match(knowledge, /busy \? 'Recalling…' : 'Recall'/, 'the Recall button must exist');

assert.match(knowledge, /'purgeKnowledge'/, 'Purge must call purgeKnowledge');
assert.match(knowledge, /purgeKnowledge',\s*scope,\s*confirm/, 'Purge must pass an explicit confirm flag');
assert.match(knowledge, /confirm\s*\?\s*'done'\s*:\s*'review'/, 'Purge must preview before it confirms');

assert.match(knowledge, /Learned workflows/, 'the guide\'s Learned workflows section must exist');
assert.match(knowledge, /'checkWorkflow'/, 'Learned workflows must offer Check');
assert.match(knowledge, /'replayCheckWorkflow'/, 'Learned workflows must offer Replay check');

assert.match(rpcTarget, /listInsights\(/, 'the UI door must share listInsights with the engine RPC');
assert.match(rpcTarget, /recallExperience\(/, 'the UI door must share recallExperience with the engine RPC');
assert.match(rpcTarget, /purgeKnowledge\(/, 'the UI door must share purgeKnowledge with the engine RPC');
assert.match(mcpTools, /Name = "list_insights"/, 'the agent door must keep list_insights');
assert.match(mcpTools, /Name = "recall_experience"/, 'the agent door must keep recall_experience');
assert.match(mcpTools, /Name = "purge_knowledge"/, 'the agent door must keep purge_knowledge');

console.log('Knowledge tab Insights, Recall and Purge surface tests passed');
