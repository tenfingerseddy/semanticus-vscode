import { useEffect, useRef, useState } from 'react';
import { loadState, saveState, onWorkflowLibraryChange, rpc } from './bridge';
import { Button, Panel, Banner } from './workflows';
import {
  acceptDocumentSave, applyTextareaChange, canApplyUpgrade, canPreviewUpgrade, createDocumentLoader, receiveDocument, rebaseDocument,
  type DocumentBuffer, type WorkflowDocument, type WorkflowDocumentEditResult, type WorkflowUpgradeResult,
} from './workflowdocument.mjs';

const draftKey = (path: string) => `workflow-document-draft:${path}`;
const message = (error: unknown) => error instanceof Error ? error.message : String(error);

export function WorkflowDocumentEditor({ name, onSaved, onDeleted }: { name: string; onSaved: (name: string) => void; onDeleted?: () => void }) {
  const [buffer, setBuffer] = useState<DocumentBuffer | null>(null);
  const current = useRef<DocumentBuffer | null>(null);
  const alive = useRef(true);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [confirmReload, setConfirmReload] = useState(false);
  const [upgrade, setUpgrade] = useState<WorkflowUpgradeResult | null>(null);
  const [upgradeBusy, setUpgradeBusy] = useState<'preview' | 'apply' | null>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const upgradeRequest = useRef(0);

  const clearUpgrade = () => { upgradeRequest.current++; setUpgrade(null); };

  const install = (next: DocumentBuffer) => {
    const previous = current.current;
    if (!previous || previous.text !== next.text || previous.base.path !== next.base.path
      || previous.base.byteHash !== next.base.byteHash || previous.latest.path !== next.latest.path
      || previous.latest.byteHash !== next.latest.byteHash) clearUpgrade();
    current.current = next;
    setBuffer(next);
    saveState(draftKey(next.base.path), next.text === next.base.exactText ? null : next);
  };
  const loader = useRef<ReturnType<typeof createDocumentLoader> | null>(null);
  if (!loader.current) loader.current = createDocumentLoader({
    read: (workflow) => rpc<WorkflowDocument>('getWorkflowDocument', workflow),
    onDocument: (document) => {
      const held = current.current?.base.path === document.path ? current.current
        : loadState<DocumentBuffer | null>(draftKey(document.path), null);
      const valid = held?.base?.path === document.path && typeof held.text === 'string' ? held : null;
      install(receiveDocument(valid, document));
      setError(null);
    },
    onError: (cause) => setError(message(cause)),
  });
  const refresh = () => loader.current!.load(name);

  useEffect(() => {
    alive.current = true;
    void refresh();
    const off = onWorkflowLibraryChange(() => { void refresh(); });
    return () => { alive.current = false; upgradeRequest.current++; loader.current!.invalidate(); off(); };
  }, [name]);

  const dirty = !!buffer && buffer.text !== buffer.base.exactText;
  const stale = !!buffer && (buffer.base.path !== buffer.latest.path || buffer.base.byteHash !== buffer.latest.byteHash
    || buffer.base.exactText !== buffer.latest.exactText || buffer.base.library !== buffer.latest.library);
  const stock = buffer?.base.library === 'stock';

  const save = async () => {
    const sent = current.current;
    if (!sent || sent.base.library === 'stock') return;
    clearUpgrade();
    setBusy(true); setError(null); setNotice(null);
    loader.current!.invalidate();
    try {
      const result = await rpc<WorkflowDocumentEditResult>('editWorkflowDocument', name, sent.base.byteHash, sent.text, sent.base.path, 'human');
      const refused = !result.changed && sent.text !== result.document.exactText;
      if (!alive.current) {
        const kept = loadState<DocumentBuffer | null>(draftKey(sent.base.path), null);
        if (kept?.text === sent.text && kept.base.byteHash === sent.base.byteHash) {
          const next = acceptDocumentSave(kept, result.document, sent.text, result.changed);
          saveState(draftKey(sent.base.path), next.text === next.base.exactText ? null : next);
        }
        return;
      }
      loader.current!.invalidate();
      install(acceptDocumentSave(current.current ?? sent, result.document, sent.text, result.changed));
      if (refused) {
        setError(result.reason || 'The file could not be saved. Your edits are kept.');
        return;
      }
      setNotice(result.changed ? 'File saved.' : 'No changes to save.');
      onSaved(name);
      void refresh();
    } catch (cause) {
      if (!alive.current) return;
      if (message(cause).includes('changed on disk')) {
        await refresh();
        if (alive.current) setError('The saved file changed. Your edits are kept. Review the saved version or reload it before saving.');
      } else setError(message(cause));
    } finally { if (alive.current) setBusy(false); }
  };

  const reload = async () => {
    clearUpgrade();
    setConfirmReload(false); setBusy(true);
    const document = await refresh();
    if (document && alive.current) { install(receiveDocument(null, document)); setNotice('Loaded the saved file.'); }
    if (alive.current) setBusy(false);
  };

  const keepReviewedEdits = (reviewed: WorkflowDocument) => {
    if (busy) return;
    try {
      install(rebaseDocument(current.current, reviewed));
      setError(null); setConfirmReload(false);
      setNotice('Your edits are kept. Save file will replace the reviewed saved version.');
    } catch (cause) { setError(message(cause)); }
  };

  const del = async () => {
    setBusy(true); setError(null); setNotice(null);
    try {
      await rpc('deleteWorkflow', name, 'human');
      if (!alive.current) return;
      if (onDeleted) onDeleted();
      else onSaved(name);
    } catch (cause) { if (alive.current) setError(message(cause)); }
    finally { if (alive.current) { setBusy(false); setConfirmDelete(false); } }
  };

  const createCopy = async () => {
    if (!current.current) return;
    clearUpgrade();
    setBusy(true); setError(null); setNotice(null);
    try {
      const document = await rpc<WorkflowDocument>('getWorkflowDocument', name);
      if (document.library !== 'stock') throw new Error('A project copy already exists. Reload to open it.');
      await rpc('saveWorkflow', name, document.exactText, 'human', true);
      if (!alive.current) return;
      await refresh();
      onSaved(name);
      setNotice('Project copy created. You can edit it now.');
    } catch (cause) { if (alive.current) setError(message(cause)); }
    finally { if (alive.current) setBusy(false); }
  };

  const previewUpgrade = async () => {
    const sent = current.current;
    if (busy || !sent || !canPreviewUpgrade(sent)) return;
    clearUpgrade();
    const ticket = upgradeRequest.current;
    setBusy(true); setUpgradeBusy('preview'); setError(null); setNotice(null);
    try {
      const result = await rpc<WorkflowUpgradeResult>('upgradeWorkflow', name, true, sent.base.byteHash, sent.base.path, 'human');
      if (!alive.current || ticket !== upgradeRequest.current) return;
      setUpgrade(result);
    } catch (cause) {
      if (alive.current && ticket === upgradeRequest.current) setError(message(cause));
    } finally { if (alive.current) { setBusy(false); setUpgradeBusy(null); } }
  };

  const applyUpgrade = async () => {
    if (busy || !upgrade || !canApplyUpgrade(current.current, upgrade)) return;
    const reviewed = upgrade.document;
    setBusy(true); setUpgradeBusy('apply'); setError(null); setNotice(null);
    try {
      const result = await rpc<WorkflowUpgradeResult>('upgradeWorkflow', name, false, reviewed.byteHash, reviewed.path, 'human');
      if (!alive.current) return;
      clearUpgrade();
      loader.current!.invalidate();
      install(receiveDocument(current.current, result.document));
      if (!result.changed) {
        setError(result.reason || result.parseError || 'The upgrade was not applied. Preview the saved file again.');
        return;
      }
      setNotice('File upgraded to format v2.');
      onSaved(name);
      void refresh();
    } catch (cause) { if (alive.current) { clearUpgrade(); setError(message(cause)); } }
    finally { if (alive.current) { setBusy(false); setUpgradeBusy(null); } }
  };

  return <Panel>
    <div data-workflow-document="true" className="flex flex-col gap-3 min-w-0">
      <div className="flex items-center gap-2 flex-wrap">
        <b className="text-[13px]">{name}.md</b>
        {dirty && <span className="text-[11px]" style={{ color: 'var(--sem-warn)' }}>Unsaved changes kept in this editor</span>}
        <div className="flex-1" />
        {buffer?.base.metadata.parses && <Button disabled={busy || !canPreviewUpgrade(buffer)} onClick={previewUpgrade}
          title={dirty ? 'Save or reload your draft before previewing a format upgrade.' : 'Preview format changes before applying them.'}>
          {upgradeBusy === 'preview' ? 'Previewing…' : 'Upgrade format'}
        </Button>}
        <Button disabled={busy} onClick={() => dirty ? setConfirmReload(true) : void reload()}>Reload saved file</Button>
        {buffer && (stock
          ? <Button primary disabled={busy} onClick={createCopy}>{busy && !upgradeBusy ? 'Creating…' : 'Create project copy'}</Button>
          : <>
              {confirmDelete
                ? <Button disabled={busy} onClick={() => void del()} title="Really delete. Deleting your copy of a stock workflow reverts to the built-in one"><span style={{ color: 'var(--sem-bad)' }}>Confirm delete</span></Button>
                : <Button disabled={busy} onClick={() => setConfirmDelete(true)}>Delete…</Button>}
              <Button primary disabled={busy || stale} onClick={save}>{busy && !upgradeBusy ? 'Saving…' : 'Save file'}</Button>
            </>)}
      </div>
      <div className="text-[11.5px]" style={{ color: 'var(--sem-muted)' }}>
        {stock ? 'This is the built-in file. Create a project copy to change it.'
          : 'Edit the saved Markdown directly. Unsaved text is kept when you leave this view.'}
      </div>
      {buffer && <div className="text-[10.5px] break-all" style={{ color: 'var(--sem-muted)' }}>{buffer.base.path}</div>}
      {confirmReload && <Banner color="var(--sem-warn)">
        <div>Reloading will discard your unsaved text and use the saved file.</div>
        <div className="flex gap-2 mt-2">
          <Button onClick={() => { void reload(); }}>Discard edits and reload</Button>
          <Button onClick={() => setConfirmReload(false)}>Keep my edits</Button>
        </div>
      </Banner>}
      {stale && <Banner color="var(--sem-warn)">The saved file changed while you were editing. Your text has been kept.</Banner>}
      {error && <Banner color="var(--sem-bad)">{error}</Banner>}
      {notice && <div role="status" className="text-[12px]" style={{ color: 'var(--sem-good)' }}>{notice}</div>}
      {upgrade && <section aria-label="Format upgrade preview" className="rounded-lg border p-3 flex flex-col gap-2" style={{ borderColor: 'var(--sem-border)' }}>
        <div className="flex items-center gap-2 flex-wrap">
          <b className="text-[13px]">Format upgrade preview</b>
          <div className="flex-1" />
          {upgrade.canApply && <Button primary disabled={busy || !canApplyUpgrade(buffer, upgrade)} onClick={applyUpgrade}>
            {upgradeBusy === 'apply' ? 'Applying…' : 'Apply upgrade'}
          </Button>}
          <Button disabled={busy} onClick={clearUpgrade}>Close preview</Button>
        </div>
        <div className="text-[12px]">{upgrade.canApply ? 'Review the source changes before applying the upgrade.' : upgrade.reason}</div>
        {upgrade.parseError && upgrade.parseError !== upgrade.reason && <Banner color="var(--sem-bad)">{upgrade.parseError}</Banner>}
        {upgrade.canApply && !canApplyUpgrade(buffer, upgrade) && <Banner color="var(--sem-warn)">
          This preview no longer matches the editor. Reload the saved file and preview again.
        </Banner>}
        {upgrade.diff && <pre aria-label="Proposed source diff" className="overflow-auto max-h-[320px] rounded-md p-3 text-[12px] whitespace-pre"
          style={{ background: 'var(--sem-surface-2)', fontFamily: 'var(--vscode-editor-font-family, monospace)' }}>
          {upgrade.diff.split(String.fromCharCode(10)).map((line, index) => <span key={index} style={{
            color: line.startsWith('+') && !line.startsWith('+++') ? 'var(--sem-good)'
              : line.startsWith('-') && !line.startsWith('---') ? 'var(--sem-bad)' : undefined,
          }}>{index > 0 ? String.fromCharCode(10) : ''}{line}</span>)}
        </pre>}
      </section>}
      {!buffer && !error && <div className="text-[12px]">Loading saved file…</div>}
      {buffer && <>
        {!buffer.base.metadata.parses && <Banner color="var(--sem-warn)">
          This file has a parse error. Correct it below and save: {buffer.base.metadata.parseError}
        </Banner>}
        <label className="text-[11px] font-semibold" htmlFor="workflow-source">Saved file source</label>
        <textarea id="workflow-source" aria-label="Saved file source" value={buffer.text.charCodeAt(0) === 0xfeff ? buffer.text.slice(1) : buffer.text} readOnly={stock || busy} spellCheck={false}
          onChange={(event) => {
            if (!current.current) return;
            install({ ...current.current, text: applyTextareaChange(current.current.text, event.target.value) });
            setNotice(null); setConfirmReload(false);
          }}
          className="w-full min-h-[420px] rounded-lg border p-3 outline-none resize-y text-[12px]"
          style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', borderColor: 'var(--sem-border)', fontFamily: 'var(--vscode-editor-font-family, monospace)', tabSize: 2 }} />
        {stale && <details className="text-[11.5px]">
          <summary className="cursor-pointer">Compare with the saved file</summary>
          <pre className="mt-2 whitespace-pre-wrap break-words rounded-md p-3" style={{ background: 'var(--sem-surface-2)' }}>{buffer.latest.exactText}</pre>
          <p className="my-2">After reviewing the saved file above, keep your current text and allow it to replace this saved version when you click Save file.</p>
          <Button disabled={busy || stock || buffer.latest.library !== 'user' || buffer.base.path !== buffer.latest.path}
            onClick={() => keepReviewedEdits(buffer.latest)}>Keep my edits and enable Save</Button>
        </details>}
      </>}
    </div>
  </Panel>;
}
