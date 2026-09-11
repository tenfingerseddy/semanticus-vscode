// One selected workflow owns this controller. Disposal fences late replies after selection changes.
export function workflowLayoutSession({ name, rpc, subscribe, apply, error }) {
  let disposed = false;
  let request = 0;
  let edits = 0;
  let revision;
  let dirty = false;
  const pendingEchoes = new Set();
  const seenWhileDirty = [];
  const read = async (edit) => {
    if (disposed) return;
    const ticket = ++request;
    try {
      const layout = await rpc('getWorkflowLayout', name);
      if (disposed || ticket !== request) return;
      revision = layout.revision;
      if (edit === edits) {
        dirty = false;
        seenWhileDirty.length = 0;
        apply(layout.positions);
        error('');
      }
    } catch (e) {
      if (!disposed && ticket === request) error(e.message || String(e));
    }
  };
  const unsubscribe = subscribe((layout) => {
    if (disposed || layout.name !== name || pendingEchoes.delete(layout.revision)) return;
    if (dirty) { seenWhileDirty.push(layout); return; }
    ++request;
    revision = layout.revision;
    apply(layout.positions);
    error('');
  });
  const ready = read(edits);
  let chain = ready;
  return {
    ready,
    refresh() {
      const edit = ++edits;
      // An explicit reload runs after writes already requested by this panel.
      chain = chain.then(() => read(edit));
      return chain;
    },
    save(positions) {
      dirty = true;
      const edit = ++edits;
      chain = chain.then(async () => {
        if (disposed) return;
        try {
          // Only an explicit Reset may recover a layout that could not be read.
          if (!revision && Object.keys(positions).length) throw new Error('Load the shared layout before saving. Your positions are still here.');
          const saved = await rpc('saveWorkflowLayout', name, positions, revision);
          if (disposed) return;
          // Consume our one echo, whether it arrived before or after the reply. A later external
          // reset may legitimately produce the same content hash and must still be applied.
          const echo = seenWhileDirty.findIndex(layout => layout.revision === saved.revision);
          if (echo < 0) pendingEchoes.add(saved.revision);
          else seenWhileDirty.splice(0, echo + 1); // this save supersedes notifications preceding its echo
          revision = saved.revision;
          if (edit === edits) {
            dirty = false;
            // Another client can publish after our write but before its reply. Keep the latest
            // complete layout; a revision set loses both positions and repeated-hash restores.
            const latest = seenWhileDirty.at(-1);
            seenWhileDirty.length = 0;
            if (latest) { revision = latest.revision; apply(latest.positions); }
            error('');
          }
        } catch (e) {
          if (!disposed) error(e.message || String(e));
        }
      });
      return chain;
    },
    dispose() { disposed = true; ++request; unsubscribe(); },
  };
}
