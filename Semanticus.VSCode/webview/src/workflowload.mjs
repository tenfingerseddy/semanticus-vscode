// The one door that turns "which workflow is current" into "which definition is displayed".
// Both doors that can start a definition fetch , a selection change and a library (agent-save)
// notification , load through here, so a single validity rule decides which response is allowed to
// install. Two doors with their own rules is what let a slow response for a workflow the person had
// already left overwrite the one on screen.

export function createDefinitionLoader({ rpc, setDef, currentSelection }) {
  // Monotonic ticket. Every load takes the next one; only the holder of the newest ticket may install,
  // and only while its workflow is still the selected one. The two conditions cover the two ways a
  // response goes stale: a newer request for the same workflow (overlapping saves), and a change of
  // selection (the response is for a file nobody is looking at).
  let issued = 0;

  const load = (name) => {
    const ticket = ++issued;
    const valid = () => ticket === issued && currentSelection() === name;
    rpc('getWorkflow', name)
      .then((d) => { if (valid()) setDef(d); })
      .catch(() => { if (valid()) setDef(null); });
  };

  // Refetch one named file. Every door that is not a selection change comes through here.
  const reload = (name) => { if (name) load(name); };

  return {
    // The selection effect. Returns the effect cleanup: leaving this selection retires the ticket, so a
    // response that arrives after the person moved on can never install.
    loadForSelection(name) {
      if (!name) { issued++; setDef(null); return () => { issued++; }; }
      load(name);
      return () => { issued++; };
    },
    // A library notification means someone (usually an agent) saved a file. Refetch whatever is open now.
    // Same ticket rule, so this can refresh Boxes for the open workflow but cannot install anything else.
    loadAfterNotification() { reload(currentSelection()); },
    // A human save from the Author pane. Naming the file explicitly matters because the save can also be
    // the moment a brand-new playbook becomes the selection.
    reload,
  };
}

// A saved definition arrived for the open workflow. Boxes always follows the file; a dirty Outline draft
// is the author's unsaved work and is never re-seeded over by a refresh.
export function onDefinitionArrived(state, def) {
  return { liveDef: def, reseedDraft: !state.creating && !state.dirty };
}
