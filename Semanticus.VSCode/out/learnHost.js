"use strict";
var __createBinding = (this && this.__createBinding) || (Object.create ? (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    var desc = Object.getOwnPropertyDescriptor(m, k);
    if (!desc || ("get" in desc ? !m.__esModule : desc.writable || desc.configurable)) {
      desc = { enumerable: true, get: function() { return m[k]; } };
    }
    Object.defineProperty(o, k2, desc);
}) : (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    o[k2] = m[k];
}));
var __setModuleDefault = (this && this.__setModuleDefault) || (Object.create ? (function(o, v) {
    Object.defineProperty(o, "default", { enumerable: true, value: v });
}) : function(o, v) {
    o["default"] = v;
});
var __importStar = (this && this.__importStar) || (function () {
    var ownKeys = function(o) {
        ownKeys = Object.getOwnPropertyNames || function (o) {
            var ar = [];
            for (var k in o) if (Object.prototype.hasOwnProperty.call(o, k)) ar[ar.length] = k;
            return ar;
        };
        return ownKeys(o);
    };
    return function (mod) {
        if (mod && mod.__esModule) return mod;
        var result = {};
        if (mod != null) for (var k = ownKeys(mod), i = 0; i < k.length; i++) if (k[i] !== "default") __createBinding(result, mod, k[i]);
        __setModuleDefault(result, mod);
        return result;
    };
})();
Object.defineProperty(exports, "__esModule", { value: true });
exports.createLearn = createLearn;
// Learn Semanticus, the host's two actions (1.4, contract section 9). start copies the demo model to a scratch
// folder, opens it, and tells the Studio it is ready; end gives the person their earlier model back. extension.ts registers them as the commands
// semanticus.learn.start and semanticus.learn.end, which the Studio reaches through the host-command relay (runHostCommand, chapter id as the ref). Everything that needs extension.ts
// (opening a model, what was open, the Studio panel) arrives as `deps`, so this file holds the order of events and nothing else.
const vscode = __importStar(require("vscode"));
const learn_1 = require("./learn");
function createLearn(deps) {
    // The model that was open before the FIRST chapter. A second chapter keeps it, so Leave always lands on the person's own model.
    // `own`: the tour is about the person's own open model. Nothing was swapped, so Leave has nothing to put back.
    let session;
    // True while a chapter opens its demo copy: that open is the tour's own, so it must not end the tour.
    let starting = false;
    const start = async (chapter) => {
        if (!(0, learn_1.isLearnChapter)(chapter))
            return;
        starting = true;
        try {
            // A model is open: the tour is about that model. The demo is only for a person with nothing open, and it never
            // closes anyone's work (a tour that swapped the model away lost the person's place and had no data to show).
            const openNow = session?.own ? session.demoSessionId : (session ? undefined : deps.currentSessionId());
            if (openNow && deps.currentSessionId() === openNow) {
                session = { previous: undefined, hadPrevious: false, copy: '', demoSessionId: openNow, own: true };
                await deps.postToStudio({ type: 'learnReady', chapter, modelName: deps.currentModelName?.() ?? '', sessionId: openNow, own: true });
                return;
            }
            const hadPrevious = session ? session.hadPrevious : deps.remembered() !== undefined;
            const previous = session ? session.previous : deps.remembered();
            const copy = (0, learn_1.makeScratchCopy)(deps.assetsDir, deps.storageDir, chapter);
            const opened = await deps.open(copy);
            if (!opened) {
                await deps.postToStudio({ type: 'learnCancelled', chapter });
                return;
            }
            session = { previous, hadPrevious, copy, demoSessionId: opened.sessionId };
            await deps.postToStudio({ type: 'learnReady', chapter, modelName: opened.modelName, sessionId: opened.sessionId });
        }
        catch (e) {
            deps.log('Learn: the demo model did not open: ' + (e?.message ?? e));
            await deps.postToStudio({ type: 'learnFailed', chapter, message: 'The demo model could not open. Your own model is unchanged.' });
        }
        finally {
            starting = false;
        }
    };
    const end = async () => {
        const left = session;
        session = undefined;
        if (!left)
            return;
        try {
            // Reopening discards what is open (the demo copy's edits are scratch). That is only safe while the demo copy is what is open:
            // after the person opened another model, that model has their work in it, so the tour is just forgotten.
            if (!left.own && deps.currentSessionId() === left.demoSessionId) {
                if (left.hadPrevious)
                    await deps.restore(left.previous);
                else
                    deps.forget();
            }
        }
        catch (e) {
            deps.log('Learn: the earlier model did not reopen: ' + (e?.message ?? e));
            void vscode.window.showWarningMessage('Your earlier model could not be reopened. Use Open Model to bring it back.');
        }
        // A copy is only ever deleted by the age rule at the next start, never here: the person may still be looking at it.
        await deps.postToStudio({ type: 'learnEnded' });
        if (deps.currentSessionId() !== left.demoSessionId)
            await deps.postToStudio({ type: 'navigate', tab: 'plan', target: 'work', sessionId: deps.currentSessionId() });
    };
    // Any other model opening ends the tour (the Studio closes the bar on learnEnded), and the person's earlier model stays where it is.
    const modelChanged = (sessionId) => {
        if (!session || starting || sessionId === session.demoSessionId)
            return;
        session = undefined;
        void deps.postToStudio({ type: 'learnEnded' });
    };
    return { start, end, modelChanged };
}
//# sourceMappingURL=learnHost.js.map