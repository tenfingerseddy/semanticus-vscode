// The close question (round 1, B1: "VS Code quit with 22 unsaved plan changes on a live model and gave no warning").
//
// An extension cannot veto a window close. VS Code asks "Do you want to save?" (Save, Don't Save, Cancel) for a document that
// is modified, so while the open model has unsaved edits the host keeps one small tab modified. Closing the window, or the tab,
// then asks the person, and Save runs the model's own save (a model open live from a workspace writes its changes back to it).
//
// The tab exists ONLY while the person's own edits are unsaved (SessionInfo.hasUnsavedEdits: Jev's permission marker is not an
// edit). It holds no model data and backs nothing up: a backup would let VS Code skip the question and restore a tab with nothing in it.
import * as vscode from 'vscode';
import { guardWanted } from './statusBarText';

export const GUARD_SCHEME = 'semanticus-unsaved';
export const GUARD_VIEW_TYPE = 'semanticus.unsavedModel';

/** What the guard needs from the host. Kept as a seam so the decision logic runs in a plain test. */
export interface GuardHost {
    /** Save the open model as the person would (Ctrl+S; a live model saves to its workspace). True when it saved. */
    save(): Promise<boolean>;
    /** The model's name, for the tab and the question. */
    modelName(): string | undefined;
    /** The tab opened beside the person's work and became the visible one; put the person's own tab back in front. */
    afterOpen(): void;
}


interface GuardDocument extends vscode.CustomDocument { readonly name: string }

export class UnsavedGuard implements vscode.CustomEditorProvider<GuardDocument>, vscode.FileSystemProvider {
    private readonly changed = new vscode.EventEmitter<vscode.CustomDocumentEditEvent<GuardDocument>>();
    readonly onDidChangeCustomDocument = this.changed.event;
    private readonly fsChanged = new vscode.EventEmitter<vscode.FileChangeEvent[]>();
    readonly onDidChangeFile = this.fsChanged.event;
    private document: GuardDocument | undefined;
    private busy = false;
    private wanted = false;
    private closedByPerson = false;

    constructor(private readonly host: GuardHost) {}

    // ---- the host tells the guard what the engine said --------------------------------------------------------------------

    /** Called with every read of the session. Opens the tab when edits become unsaved and closes it when they are not. */
    async update(info: Parameters<typeof guardWanted>[0]): Promise<void> {
        const want = guardWanted(info);
        if (!want) this.closedByPerson = false;
        // The person closed the tab (Don't Save, or Cancel then close): do not bring it back until the model has been clean once.
        if (want && this.closedByPerson) return;
        // A tab VS Code brought back from the last session is closed when nothing is unsaved (want false, document present).
        if (want === this.wanted && (want ? !!this.document : !this.document)) return;
        this.wanted = want;
        if (this.busy) return;
        this.busy = true;
        try {
            if (want) await this.open();
            else await this.close();
        } catch { /* a tab that cannot open must never break the model; the next read tries again */ }
        finally {
            this.busy = false;
            // The model changed state while this ran: say it again once.
            if (this.wanted !== (!!this.document)) { const again = this.wanted; this.wanted = !again; void this.update(again ? info : undefined); }
        }
    }

    private uri(): vscode.Uri {
        const name = (this.host.modelName() || 'Model').replace(/[^\w .-]+/g, '_').trim() || 'Model';
        return vscode.Uri.from({ scheme: GUARD_SCHEME, path: '/Unsaved changes in ' + name + '.semanticus-unsaved' });
    }

    private async open(): Promise<void> {
        const uri = this.uri();
        await vscode.commands.executeCommand('vscode.openWith', uri, GUARD_VIEW_TYPE, { preview: false, preserveFocus: true, viewColumn: vscode.ViewColumn.Active });
        // One edit makes the tab modified; VS Code then asks before it closes.
        const doc = this.document;
        if (doc) this.changed.fire({ document: doc, undo: () => undefined, redo: () => undefined, label: 'Unsaved model changes' });
        this.host.afterOpen();
    }

    private async close(): Promise<void> {
        const doc = this.document;
        if (!doc) return;
        // The tab is modified; reverting it first makes the close silent. The tab must be the active one for the two commands.
        const keep = vscode.window.activeTextEditor;
        await vscode.commands.executeCommand('vscode.openWith', doc.uri, GUARD_VIEW_TYPE, { preview: false, preserveFocus: false });
        await vscode.commands.executeCommand('workbench.action.files.revert');
        await vscode.commands.executeCommand('workbench.action.closeActiveEditor');
        if (keep) await vscode.window.showTextDocument(keep.document, { viewColumn: keep.viewColumn, preserveFocus: false });
        this.document = undefined;
    }

    // ---- CustomEditorProvider --------------------------------------------------------------------------------------------

    async openCustomDocument(uri: vscode.Uri): Promise<GuardDocument> {
        const doc: GuardDocument = { uri, name: decodeURIComponent(uri.path.replace(/^\//, '').replace(/^Unsaved changes in /, '').replace(/\.semanticus-unsaved$/, '')), dispose: () => { if (this.document?.uri.toString() === uri.toString()) { this.document = undefined; if (this.wanted) this.closedByPerson = true; } } };
        this.document = doc;
        return doc;
    }

    async resolveCustomEditor(doc: GuardDocument, panel: vscode.WebviewPanel): Promise<void> {
        panel.webview.options = { enableScripts: false };
        const name = doc.name.replace(/[<>&]/g, '');
        panel.webview.html = `<!doctype html><html><body style="font-family:var(--vscode-font-family);color:var(--vscode-foreground);padding:24px;line-height:1.5">`
            + `<h3 style="font-weight:600">Unsaved changes in ${name}</h3>`
            + `<p>This tab is here so VS Code asks you before it closes. Choose Save to keep your changes, or Don't Save to leave them.</p>`
            + `<p>It goes away by itself when the model is saved.</p></body></html>`;
    }

    async saveCustomDocument(_doc: GuardDocument, _cancel: vscode.CancellationToken): Promise<void> {
        const saved = await this.host.save();
        if (!saved) throw new Error('The model was not saved.');
    }

    async saveCustomDocumentAs(): Promise<void> { throw new Error('Use Save. This tab cannot save to another place.'); }

    async revertCustomDocument(): Promise<void> { /* Don't Save: nothing to put back here; the model is the engine's */ }

    async backupCustomDocument(): Promise<vscode.CustomDocumentBackup> {
        // NO BACKUP, ON PURPOSE. A backup lets VS Code skip the question when the person quits and bring back a tab that holds nothing: the
        // changes themselves live in the engine, so a restored tab would only hide that they are gone. Refusing makes VS Code stop the quit
        // and name this tab as holding changes that are not saved (the person then saves, or quits anyway).
        throw new Error('The model has changes that are not saved.');
    }

    // ---- FileSystemProvider: just enough for the tab's address -----------------------------------------------------------

    watch(): vscode.Disposable { return new vscode.Disposable(() => undefined); }
    stat(): vscode.FileStat { const now = Date.now(); return { type: vscode.FileType.File, ctime: now, mtime: now, size: 0 }; }
    readDirectory(): [string, vscode.FileType][] { return []; }
    createDirectory(): void { throw vscode.FileSystemError.NoPermissions(); }
    readFile(): Uint8Array { return new Uint8Array(0); }
    writeFile(): void { /* the save goes through saveCustomDocument */ }
    delete(): void { /* closing the tab */ }
    rename(): void { throw vscode.FileSystemError.NoPermissions(); }
}
