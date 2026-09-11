// ===================================================================================================
// The arguments the Studio posts for workflow actions that move a run along (submit / skip / abort).
//
// Why this is its own module: the bridge hands every rpc argument to postMessage, which
// structured-clones them, and a DOM event is a platform object that cannot be cloned. One argument
// that is really a click event kills the whole call before it leaves the webview. Round 4 (D-171)
// lost the Submit step to exactly that: `onClick={doSubmit}` handed the React click event to the
// gate override parameter, and every workflow, calendar and journey case stopped dead on the UI door.
//
// Pure and framework-free on purpose: the node test runs this real builder and clones its output the
// same way the bridge does, so the guard is the shipped code path and not a copy of it.
// ===================================================================================================

/**
 * The gate override a workflow action handler may carry. ONLY a non-empty string is a gate.
 *
 * A handler bound straight to onClick is called with a DOM event, and that object is not a gate. The
 * event is dropped here rather than thrown on, so a future bare binding costs the override and not
 * the whole submit. The wire arguments are the real guarantee; this is the edge where they are built.
 */
export function gateArg(value) {
  return typeof value === 'string' && value.length > 0 ? value : undefined;
}

/**
 * The wire arguments for submitWorkflowStep: runId, stepId, the answers JSON, the origin, and the
 * call gate (or null). Every element is a string or null, so the array survives the structured clone
 * postMessage performs on its way out of the webview.
 */
export function submitStepArgs(runId, stepId, answersJson, origin, callGate) {
  return [runId, stepId, answersJson, origin, gateArg(callGate) ?? null];
}
