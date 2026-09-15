# Keyboard shortcuts

Every common Semanticus operation has a keyboard shortcut. Press **`?`** inside Studio for the
in-product cheat sheet, or run **“Semanticus: Keyboard Shortcuts”** from the command palette.

Two rules govern every default here:

1. **Semanticus never grabs a key outside its own surfaces.** Every binding carries a `when` clause
   scoping it to a Semanticus surface — the Studio panel, the Model tree, or a DAX/TMDL
   editor. Ctrl+F in your Python file is still VS Code find; Ctrl+Z in a text editor is still text undo.
2. **Everything is remappable.** Every gesture is backed by a command, so you can change any of them in
   **File → Preferences → Keyboard Shortcuts** (search “Semanticus”). The handful of in-Studio single-key
   gestures (`?`, `Esc`, and the `Ctrl+Alt+letter` tab jumps) are built into the Studio page itself — but
   their actions are still commands (`semanticus.studioGoTab`, `semanticus.undo`, …), so you can bind
   your own keys to the same actions if you prefer different ones.

## Studio (the `activeWebviewPanelId == 'semanticusStudio'` scope)

These work while the Studio panel is the active editor — including when your cursor is inside it.

| Gesture | Action | Notes |
|---|---|---|
| `Ctrl+F` (`⌘F`) | Search & Replace across the model | Opens the Search tab and focuses the find box — names, descriptions, formats, and (opt-in) DAX & M |
| `Ctrl+S` (`⌘S`) | Save the model to disk | The same save as the Model view’s save icon (TMDL beside the model). Studio and Properties forward this chord to the host, because a webview otherwise swallows it. |
| `Ctrl+Z` (`⌘Z`) | Undo the last model change | When you are not typing in a box. Typing still undoes text. `Ctrl+Alt+Z` always undoes a model change. |
| `Ctrl+Shift+P` (`⌘⇧P`) | Command palette | Forwarded out of Studio and Properties so the palette still opens with focus inside the webview. |
| `Ctrl+Shift+1…5` | Jump to an intent: Understand · Change · Improve · Prove · Ship | Returns to the tab you last used in that intent |
| `Ctrl+Alt+←` / `Ctrl+Alt+→` | Previous / next Studio tab | Cycles all tabs, wrapping at the ends |
| `Ctrl+Alt+Z` / `Ctrl+Alt+Shift+Z` | Undo / redo the last **model change** | The shared you-and-AI timeline (Edit History) — not text undo |
| `Ctrl+Alt+D` | Diagram | |
| `Ctrl+Alt+M` | M Code | |
| `Ctrl+Alt+L` | DAX Lab | |
| `Ctrl+Alt+R` | AI Readiness | Also re-runs the scan |
| `Ctrl+Alt+B` | BPA | Scans on open |
| `Ctrl+Alt+H` | Edit History | |
| `Ctrl+Alt+W` | Workflows | |
| `Ctrl+Alt+K` | Knowledge | |
| `Ctrl+Alt+T` | Focus the Model tree (side bar) | The reverse of `Ctrl+Alt+S` below |
| `Ctrl+Enter` (`⌘↩`) | Run the query (DAX Lab) | Anywhere in the lab — editor, wells, workbench |
| `?` | Show / hide the shortcuts cheat sheet | Never fires while you’re typing in a box |
| `Esc` | Close the cheat sheet / the help slide-over | |

**Why the split between `Ctrl+Shift`/arrow bindings and `Ctrl+Alt+letter` jumps?** Webviews swallow
keystrokes, so the suite is two layers. The chords that can never be produced by typing text
(Ctrl+F, Ctrl+S, Ctrl+Shift+digit, Ctrl+Alt+arrows) are real VS Code keybindings, and Studio plus
Properties also forward Save, model undo (when you are not typing), and the command palette to the
host so a focused webview cannot swallow them. They are remappable. The letter jumps are handled
inside the Studio page instead, because on Windows **AltGr is reported as Ctrl+Alt**: a package-level
`Ctrl+Alt+Z` binding would fire while a Polish-layout user types `ż` into a description field. The
in-page handler checks the key target and stands down in any input, textarea, or editor — so typing
(on any layout) can never trigger a jump or an undo.

## Model tree (the `focusedView == semanticusModel` scope)

| Gesture | Action | Notes |
|---|---|---|
| `Ctrl+F` (`⌘F`) | Find in Model | Quick-pick over every object — names, descriptions, DAX. Hands off to Search & Replace |
| `Ctrl+S` (`⌘S`) | Save the model to disk | |
| `Ctrl+Z` (`⌘Z`) | Undo the last model change | |
| `Ctrl+Shift+Z` / `Ctrl+Y` | Redo a model change | |
| `F2` | Rename the selected object | DAX references are rewritten automatically. On a display folder it renames the folder — every member (nested subfolders included) moves in one undoable change |
| `Delete` (`⌘⌫`) | Delete the selected object(s) | Always confirms first; references are NOT rewritten |
| `Ctrl+Alt+N` | New measure | On the selected table (any selected child resolves its table); a selected display folder prefills it. Otherwise offers a table picker |
| `Ctrl+Alt+S` | Open Semanticus Studio | |
| `Ctrl+C` (`⌘C`) | Copy the selected object | Measures, columns, hierarchies, tables, calculation groups, calculation items |
| `Ctrl+V` (`⌘V`) | Paste: duplicate the copied object | Same container = a copy beside the original (collision-safe name); a measure pasted onto another table lands there, and onto a display folder lands on that folder's table filed into it. Undoable, visible in Edit History |

## DAX / TMDL editors

| Gesture | Action | Scope (`when`) |
|---|---|---|
| `Ctrl+S` (`⌘S`) | Save the DAX back to the model | Built in — measure/column/calc-item editors are virtual documents |
| `Ctrl+Enter` (`⌘↩`) | Apply a DAX / TMDL script | `editorTextFocus && editorLangId == dax\|tmdl && resourceScheme == untitled` — the editable Script ▸ documents only |
| `Shift+Alt+F` | Format DAX (offline) | Built in — VS Code’s standard Format Document |
| `Ctrl+Alt+F` | Format DAX with DAX Formatter (online) | `editorTextFocus && editorLangId == dax` |

## Every action is also a command

Prefer different keys? These commands back the gestures — bind anything to them:

| Command | What it does |
|---|---|
| `semanticus.studioSearch` | Open Search & Replace and focus the find box |
| `semanticus.studioNextTab` / `semanticus.studioPrevTab` | Cycle Studio tabs |
| `semanticus.studioGoGroup` (arg: `build`/`inspect`/`improve`/`ship`) | Jump to a group |
| `semanticus.studioGoTab` (arg: any tab id, e.g. `daxlab`, `lineage`, `deploy`) | Jump to any tab — including ones without a default key |
| `semanticus.scanReadiness` / `semanticus.scanBpa` | Open the tab and run the scan |
| `semanticus.keyboardShortcuts` | Open the `?` cheat sheet |
| `semanticus.undo` / `semanticus.redo` | Step the shared model timeline |
| `semanticus.save`, `semanticus.openStudio`, `semanticus.findInModel`, `semanticus.newMeasure`, … | As named |

Example — bind `Ctrl+K L` to the Lineage tab (in your `keybindings.json`):

```json
{ "key": "ctrl+k l", "command": "semanticus.studioGoTab", "args": "lineage",
  "when": "activeWebviewPanelId == 'semanticusStudio'" }
```

## Design notes: collisions we accept (and why)

Every default was checked against the VS Code defaults it could shadow. Because of the `when` scoping,
each shadow exists **only inside the named Semanticus surface**:

| Our binding | VS Code default it shadows | Why it’s the right trade |
|---|---|---|
| `Ctrl+F` in the Model tree | `list.find` (tree filter) | The native tree filter only matches nodes already loaded/expanded; Find in Model searches the whole model, descriptions and DAX included |
| `Ctrl+F` when Studio is active | `actions.find` | With a webview active, VS Code find would target a background text editor — surprising; model search is what you meant |
| `Ctrl+S` in the tree / Studio | `workbench.action.files.save` | No text file has focus in these scopes; “save” means the model. (With Studio active, the default is a no-op anyway.) |
| `Ctrl+Z` etc. in the Model tree | global `undo`/`redo` | Trees don’t handle text undo; the model timeline is the only undo that exists here |
| `Ctrl+Alt+←`/`→` when Studio is active | `workbench.action.moveEditorTo(Previous\|Next)Group` | A rare gesture (still available via mouse / while any other editor is active); tab cycling is a constant one |
| `Ctrl+Enter` in *untitled* DAX/TMDL docs | `editor.action.insertLineAfter` | Scoped to the editable Script ▸ scratch documents, where “apply the script” is the dominant intent; a real newline is `Enter` |
| `F2` in the Model tree | `editor.action.rename` (editor-only scope) | No conflict in practice — the editor binding requires `editorTextFocus` |
| `Delete` in the Model tree | — (unbound for custom trees) | Confirmation dialog guards it |
| `Ctrl+C` / `Ctrl+V` in the Model tree | — (unbound for custom trees) | Copy/paste of model objects is the dominant intent in the tree; both are no-ops until something copyable is selected/copied |
| `Ctrl+Shift+1…5`, `Ctrl+Alt+N/S/F` | — (unbound in core VS Code) | Third-party extensions may use them globally; our scoped bindings win inside Semanticus surfaces only, and everything is remappable |

Deliberately **not** bound: `Ctrl+PageUp/PageDown` (editor-tab cycling must keep working so you can
leave Studio by keyboard), `Ctrl+Tab` (editor MRU switcher), `Ctrl+K Ctrl+S` (VS Code’s own Keyboard
Shortcuts UI), `Ctrl+Alt+digit` (AltGr+digit types characters on AZERTY and other layouts), and any
unscoped/global gesture.

Keep this file, `Semanticus.VSCode/package.json` (`contributes.keybindings`) and
`Semanticus.VSCode/webview/src/shortcuts.tsx` in sync — they change together, in the same PR.
