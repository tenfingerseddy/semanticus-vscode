// Type surface for the framework-free workflow wire-argument builder (workflowsubmit.mjs). Keeping it
// in a plain ES module lets the node test execute the real builder and clone its output the way the
// bridge does (test/workflow-submit-clone.test.mjs), while the webview stays fully type-checked.
export function gateArg(value: unknown): string | undefined;
export function submitStepArgs(
  runId: string,
  stepId: string,
  answersJson: string,
  origin: string,
  callGate?: string | null,
): (string | null)[];
