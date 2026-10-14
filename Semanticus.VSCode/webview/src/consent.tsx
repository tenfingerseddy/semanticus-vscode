import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import { claimConsentAsk, onDidChange, onReconnect, rpc } from './bridge';
import { useTier } from './pro';
import { whenFirstRunClosed } from './firstrun-gate';
import { useLearn } from './studio/learn-state';

// ===================================================================================================
// Permission to send this model's details for advice: the card, and the one line under Connections.
//
// WHY THIS FILE EXISTS. Astra found (F-176, 2026-09-21) that all seven typed-decision operations shipped
// on the RPC target and on MCP, and that a search of the whole extension found no caller, no handler and
// no view for any of them. So a person using VS Code could not give permission at all, and every Pro
// suggestion in the product was quietly skipped with a reason nobody could act on. This is the human
// door for three of those seven; decisions-settings.tsx is the door for the other three that a person
// drives (the seventh, the measure proposal, has no Studio surface in this slice and says so in
// test/rpc-name-parity.test.mjs rather than being quietly forgotten again).
//
// WHERE IT APPEARS, from the approved mockup (hierarchy-mockup.html, "Where Pro advice gets its home"):
//   the CARD, over whatever page first needs permission, once per session per model;
//   one LINE under Connections > Current setup, which is where it lives afterwards;
//   the switches, which are decisions-settings.tsx.
//
// WHAT RAISES THE CARD, and what does not. A page calls useConsentCard when its Pro answer did not
// arrive. That is only a hint: the answer can be missing because nothing was sure enough, because the
// service is down, or because the point is switched off. So the hook asks the ENGINE (get_consent) and
// raises the card only when the engine says permission is what is missing. A page never decides that by
// reading a sentence, because a sentence is prose and prose drifts.
//
// THREE THINGS IT WILL NOT DO. It never appears on a free session: the Pro pill already stands in those
// places and a permission card there would be asking for something the person cannot use. It never
// appears twice for the same model in the same session, whatever the person answered. And it never
// appears for a model that has never been saved, because there is nothing to keep the permission under;
// the Connections line says that in words instead.
// ===================================================================================================

/** get_consent, camelCased on the wire. Semanticus.Engine/Decisions/DecisionProtocol.cs. */
export interface ConsentInfoW {
  granted?: boolean;
  schemaVersion?: number;
  schemaVersionInForce?: number;
  /** True only when permission is granted, at the version in force, covering every field this build sends. */
  current?: boolean;
  fields?: string[];
  fieldsInForce?: string[];
  grantedUtc?: string | null;
  /** Who turned it on: 'agent' when their assistant did, on their word in chat; 'human' or absent when they did. */
  grantedBy?: string | null;
  withdrawnUtc?: string | null;
  /** Which model this permission belongs to. Empty means the open model has no identity to keep one under. */
  appliesTo?: string | null;
  /** Why permission is not current, in the engine's own plain words. */
  reason?: string | null;
}

/** get_decision_points, camelCased on the wire. */
export interface DecisionPointW {
  name?: string;
  caller?: string;
  /** The point's own plain description of what it does and what it sends. The label comes from here. */
  purpose?: string;
  enabled?: boolean;
  /** False while this build does not let the point act at all (its replay bar is unmet): it is never asked,
   *  whatever `enabled` says. Absent from an older engine, which reads as live. */
  live?: boolean;
  /** Said beside the switch when something other than this model's own switch decided it, when an on cannot last, or to
   *  say which Power BI Desktop models an on reaches (Semanticus knows a Desktop model only by its window title or by
   *  nothing, so an on reaches every model it cannot tell apart). The engine's own words. Absent when there is nothing to say. */
  note?: string | null;
  proOnly?: boolean;
  /** The declared fields as "point:field". This is the exact list a person agrees to. */
  fields?: string[];
}

// The engine names are written as LITERALS at every call site, never as a constant passed in. A constant
// reads more tidily and it is invisible to test/rpc-name-parity.test.mjs, which extracts the literal first
// argument of every rpc() call: behind a constant, these are the very calls that gate cannot check, and an
// unchecked decision call is how F-176 shipped in the first place.

// ---- the exact copy from the approved mockup ------------------------------------------------------
// Fixed, not derived. Kane approved these words on 21 September and the two lists are a promise about
// the product, not a rendering of a field list: "table, column and measure names" is what a person can
// check, and "measure.folder_and_format:measure_formula_facts" is not. The field ids are still shown, in
// plain words, in the Settings fold, which is the audit surface.
//
// 23 September (F-231): "What goes" gained the typed questions, because the ask box sends the words a
// person types and a typed question can carry a value. Kane chose to name them on the card. The engine's
// consent version went to 3 in the same change (DecisionRegistry.ConsentSchemaVersion), so a person who
// agreed to the three-line card sees this one once more. The formula text is NOT on the card because it
// is no longer sent (F-229): the measure point sends facts worked out from it instead.
//
// 25 September (composer lane L5, F-231's rule): Jev drafting sends the words a person types to describe a task (the
// Plan field, Ctrl J) or a workflow (Describe it in words), so the card names them too. New copy, for Kane's sweep.
//
// 26 September (Ask your data): Jev reads a data question typed on Pro, so the words of the question go (the
// query.summarize point, question_words). The card names them. The consent version is bumped once for the release.
// New copy, for Kane's sweep.
//
// 28 September (Kane's decision 2, the natural-set tuning): when a word of a data question is one of the model's own
// other names for a measure (a synonym), Jev is told which measure that word names. The synonym is model metadata the
// card did not name, so it gets its own line. It goes only when the person typed that word. New copy, for Kane's sweep.
//
// 29 September (Jev data questions v2): before a number is shown, Jev is also told how Semanticus read the question, in the
// question's own words ("narrowed to rows where Region is 'coast'"), so it can say what the reading leaves out. No value, no
// date. It gets its own line. New copy, for Kane's sweep.
const CARD = {
  title: 'Let Pro decide for this model?',
  lead: "To rank the plan and answer questions, Semanticus sends this model's shape to the advice service. It never sends your data.",
  goesTitle: 'What goes',
  goes: ['table, column and measure names', 'data types, formats, folders', 'counts and relationships',
    'the questions you type into the ask box', 'the words you type to describe a task or a workflow',
    'the words of your data questions', 'measure and column descriptions, when you ask about your data',
    'other names the model gives a measure, when your data question uses one',
    'how a data question was read, in the words of the question, with no values or dates'],
  neverTitle: 'What never goes',
  never: ['rows or values from any table', 'your connection or credentials', 'anything from other models'],
  notNow: 'Not now',
  ok: 'OK for this model',
};

// ---- the start plan's variant of the card (spec-first start, finding S2, lane sf-consent) -----------------------------
// A new model started from a SQL source is planned from a hidden SOURCE, and the permission above was given for an
// empty model: "this model's shape" does not tell the person that Pro will read the source's tables. So before the
// first ask the card names the source, the tables, and whether a sample of rows is used, and the person agrees to THAT.
// It asks again when a table is added or the sample is switched on ("Also let Pro see Payroll?"). The engine checks the
// same scope on every ask (Semanticus.Engine/StartPlan/StartPlanConsent.cs), so this card is the person's side of a
// rule the engine enforces, not a courtesy. New copy for Kane's sweep (1 October); none of it changes CARD above, so the
// ratified card and its consent version are untouched.
export const START_CARD = {
  title: 'Let Pro see this source?',
  alsoTitle: (names: string[]) => `Also let Pro see ${joinNames(names)}?`,
  lead: (source: string) => `To plan this model, Pro reads the shape of ${source}, and only the tables listed here.`,
  tablesTitle: 'Tables Pro will see',
  sampleOn: 'It also sees counts and patterns worked out from a sample of rows. It never sees the rows.',
  sampleOff: 'No sample of rows is used.',
  sampleReading: 'A sample of rows is being read on your computer. Pro would see only counts and patterns from it, never the rows.',
  sampleSome: (names: string[]) => `It also sees counts and patterns worked out from a sample of the rows of ${joinNames(names)}. It never sees the rows.`,
  sampleNone: 'No sample of rows goes to Pro for these tables, because you refused each one.',
  tableOut: 'Leave this table out', tablePutBack: 'Put it back',
  tableSample: 'Send counts and patterns from its sample', tableSampleHint: 'Turn off to let Pro see this table\'s names and settings and nothing worked out from its rows.',
  tableLeftOut: 'Left out. Pro does not see it.',
  neverTitle: 'What never goes',
  never: ['rows or values from any table', 'tables you left out', 'your connection or credentials'],
  alsoForModel: 'Because you have not yet said yes for this model, this also lets Pro decide for it. This is everything it may be sent:',
  notNow: 'Not now',
  ok: 'OK, let Pro see these',
  alsoOk: 'OK, add it',
};

function joinNames(names: string[]): string {
  if (names.length <= 1) return names[0] ?? '';
  return `${names.slice(0, -1).join(', ')} and ${names[names.length - 1]}`;
}

/** What the start plan's card is about. The page that raises it supplies `grant`, which calls the engine's grant with the
 *  digest of the plan the person was looking at; it rejects with a plain sentence when the plan moved. */
export interface StartPlanCardW {
  /** The source in plain words, for example "Sales on wh.fabric.example". */
  source: string;
  /** The tables included, as the person reads them. */
  tables: string[];
  /** Tables added since the person last agreed. When there are any, the card asks "Also let Pro see ...?". */
  newTables?: string[];
  /** Whether facts worked out from a sample of rows go too. */
  sample: boolean;
  /** Where the sample read stands (none, reading, read, refused). */
  sampleState?: string;
  /** EVERY table of the plan with the person's two choices on it: leave it out, and read it without sending its sample to Pro. The same two choices the
   *  assistant has (edit_start_plan include_table and set_table_sample). Absent on an older page, which lists `tables` only. */
  choices?: StartCardTable[];
  /** Make one of the choices. Answers with the card as the plan reads after it, bound to the plan's new digest. */
  edit?: (e: { op: string; target: string; value?: string }) => Promise<StartPlanCardW>;
  grant: () => Promise<unknown>;
}

export interface StartCardTable { name: string; sourceName?: string | null; included: boolean; sampleOff: boolean }

// ---- the one card, raised from anywhere -----------------------------------------------------------
// A module-level store rather than context, because the two pages that raise the card do not contain it:
// it is mounted once, above the page switch, so it can sit over whichever page needed permission.

/// `scope` is WHICH MODEL this card is a question about, as get_consent reported it when the card was
/// raised. It travels with the answer, and the engine refuses a grant whose named model is not the one
/// open by the time it arrives (LocalEngine.Decisions.cs, SetConsentAsync). Astra found the hole on
/// 2026-09-22: this store is module level and survives App's session-keyed remount, so a card opened on
/// one model could be answered after the other door opened another, and the permission landed on the
/// wrong one. Clearing the card below is the tidy half; the scope travelling with the answer is the
/// half that cannot be got round.
interface CardState { open: boolean; reask?: () => void; scope?: string | null; startPlan?: StartPlanCardW }

let cardState: CardState = { open: false };
const cardListeners = new Set<(next: CardState) => void>();

function publishCard(next: CardState): void {
  cardState = next;
  for (const listener of cardListeners) listener(next);
}

/** Raise the card by hand, for the model `scope` names. The Connections line's "Turn on" uses this;
 *  the pages use the hook below. */
export function openConsentCard(reask?: () => void, scope?: string | null): void {
  watchModelChanges();
  heldCard = null;          // asked for by hand, so a card that was waiting has been answered by this one
  publishCard({ open: true, reask, scope });
}

/** Raise the start plan's variant of the card. The start plan page calls this when its Needs-you grant is pressed. */
export function openStartPlanConsentCard(plan: StartPlanCardW, reask?: () => void, scope?: string | null): void {
  watchModelChanges();
  heldCard = null;
  publishCard({ open: true, reask, scope, startPlan: plan });
}

/// ONE DIALOG AT A TIME (verify, 28 Sep, L6). A new model's first save raises the card, and the person may have What
/// Pro decides open when it happens: the card then opened on top of the panel, two dialogs at once. The card WAITS
/// rather than closing the panel, for two reasons. The panel is where the person chose to be, and closing it under
/// them loses their place. And the panel already asks the same question on its permission line (Turn on), so nothing
/// is lost while the card waits. Dropping the card instead would bring back the bug the first-save fix closed: a new
/// model whose card is never offered. So the card comes once the panel closes, and only if permission is still not
/// given for that model by then (the person may have pressed Turn on in the panel).
///
/// A person's own Turn on still raises the card over the panel: that is a card they asked for, not one that arrived.
let settingsOpen = false;
let heldCard: { reask?: () => void; scope?: string | null } | null = null;

/** decisions-settings.tsx calls this whenever What Pro decides opens or closes. */
export function settingsPanelShown(open: boolean): void {
  settingsOpen = open;
  if (open || !heldCard) return;
  const held = heldCard;
  heldCard = null;
  void rpc<ConsentInfoW>('getConsent').then((consent) => {
    if (!consent || consent.current || settingsOpen || cardState.open) return;
    if (!consent.appliesTo || consent.appliesTo !== held.scope) return;   // another model now: its own check asks
    openConsentCard(held.reask, held.scope);
  }).catch(() => { /* a failed read is not a reason to interrupt somebody */ });
}

/** The pages' way to raise the card: at once, or after What Pro decides closes. */
function raiseCard(reask: () => void, scope: string): void {
  if (settingsOpen) { heldCard = { reask, scope }; return; }
  openConsentCard(reask, scope);
}

/// A model change closes any open card, and drops one that was waiting for the panel to close.
///
/// The card is a question about ONE model, so it cannot survive the model changing underneath it: the
/// answer would be about something that is no longer on screen.
///
/// Which models have been asked about, and whether the last swap was the assistant's, is not kept here: this page
/// is rebuilt each time Studio is closed and reopened, and a memory kept in it forgot "Not now" with it (verifier r2,
/// O2). The extension host keeps both for the window's run (src/consentAsks.ts); see raiseConsentCardFor.
///
/// Installed lazily, once, by whichever surface first needs it, so this module costs nothing until the
/// permission surfaces are actually used.
let watchingModel = false;
let lastSessionId: string | null = null;
function watchModelChanges(): void {
  if (watchingModel) return;
  watchingModel = true;
  onDidChange((n) => {
    const id = n?.sessionId ?? null;
    if (!id || id === lastSessionId) return;
    // The FIRST notification is not a change of model, it is the first time this watch has seen which
    // model is open. Treating it as a change closed the card the moment anything at all happened, which
    // the driver caught: Not now had nothing left to press. Record it and wait for a real swap.
    const first = lastSessionId === null;
    lastSessionId = id;
    if (first) return;
    heldCard = null;
    if (cardState.open) publishCard({ open: false });
  });
}

function useCardState(): CardState {
  const [value, setValue] = useState(cardState);
  useEffect(() => {
    cardListeners.add(setValue);
    setValue(cardState);
    return () => { cardListeners.delete(setValue); };
  }, []);
  return value;
}

/**
 * The engine's word on permission, and whether it raises the card now. True when it did. Exported so the
 * once-per-model rules run in test/assistant-swap-quiet.test.mjs without a page around them.
 *
 * The host decides whether this model may be asked about (src/consentAsks.ts): once per model in the window's run. A model the assistant
 * opened is asked about like any other (hero 20 D10); the card only asks, and permission is given by the person's press on it.
 */
export async function raiseConsentCardFor(consent: ConsentInfoW | null | undefined, reask?: () => void): Promise<boolean> {
  if (!consent) return false;
  if (consent.current) return false;                   // permission is fine; something else was missing
  if (!consent.appliesTo) return false;                // never saved: the Connections line says so
  if (!await claimConsentAsk(consent.appliesTo)) return false;
  raiseCard(reask ?? (() => undefined), consent.appliesTo);   // waits for What Pro decides when that panel is open (L6)
  return true;
}

/**
 * A model is open and nobody has decided about Jev for it: raise the card at once. True when it did. The pages ask only when
 * their own Pro answer is missing; a model the assistant opened has no page waiting, so without this Jev stayed silent until the
 * person found Pro > Turn on (hero 20 D10). Someone who turned permission off for the model has decided, so it never asks them.
 * It only asks: permission is given by the person's press on the card.
 */
export async function raiseConsentCardForOpenModel(): Promise<boolean> {
  const consent = await rpc<ConsentInfoW>('getConsent');
  if (!consent || consent.current || consent.withdrawnUtc || !consent.appliesTo) return false;
  return raiseConsentCardFor(consent);
}

/** Mounted once in Studio: asks for the model open now, and for each model the assistant opens afterwards. */
export function useAskForOpenModel(): void {
  const tier = useTier();
  const { runtime: learn } = useLearn();
  useEffect(() => {
    if (tier !== 'pro' || learn.phase !== 'idle') return;
    let live = true;
    watchModelChanges();
    const ask = () => { void whenFirstRunClosed().then(() => live ? raiseConsentCardForOpenModel() : false).catch(() => { /* a failed read is not a reason to interrupt somebody */ }); };
    ask();
    const off = onReconnect((by) => { if (by === 'otherDoor') ask(); });
    return () => { live = false; off(); };
  }, [tier, learn.phase]);
}

/**
 * The one line a page adds. Pass whether the Pro answer is missing, and what to run again once permission
 * is given.
 *
 * `missing` is a HINT and never the verdict. The engine is asked whether permission is the thing that is
 * missing, and only the engine's answer raises the card.
 */
export function useConsentCard(missing: boolean | undefined, reask?: () => void): void {
  const tier = useTier();
  const { runtime: learn } = useLearn();
  // The re-ask is a fresh closure on every render of the calling page. Holding it in a ref keeps it out
  // of the effect's dependencies, so a page that re-renders while its answer is still missing does not
  // ask the engine again on every keystroke.
  const reaskRef = useRef(reask);
  reaskRef.current = reask;
  // A model that has never been saved has nothing to keep permission under, so the check stops there. Its FIRST SAVE
  // is what gives it one, and nothing else about the page changes then: `missing` and the tier stay as they were, so
  // the check never ran again and the card was never offered for a new model (UAT plan statement check, 28 Sep).
  // So while the last check stopped at "never saved", the next change on the bus (a save rides it) asks again.
  const [again, setAgain] = useState(0);
  const unsaved = useRef(false);
  useEffect(() => onDidChange(() => { if (unsaved.current) { unsaved.current = false; setAgain((n) => n + 1); } }), []);

  useEffect(() => {
    // 'unknown' is a licence still being read. Waiting costs one moment; guessing shows a paying customer
    // no card, or shows a free customer a card for something they cannot use.
    if (!missing || tier !== 'pro' || learn.phase !== 'idle') return;
    let live = true;
    void whenFirstRunClosed().then(() => live ? rpc<ConsentInfoW>('getConsent') : null).then((consent) => {
      if (!live || !consent) return;
      if (!consent.current && !consent.appliesTo) { unsaved.current = true; return; }   // never saved: asked again after a save
      void raiseConsentCardFor(consent, () => reaskRef.current?.());
    }).catch(() => {
      // A failed read is not a reason to interrupt somebody. The page keeps its own quiet line.
    });
    return () => { live = false; };
  }, [missing, tier, again, learn.phase]);
}

/**
 * The one Allow button a note carries where Jev was not asked for want of permission (K19, Kane 8 Oct). One press gives
 * permission for the open model, as the person, for exactly the list the engine says is in force, and then asks the
 * question again. It never presses itself: permission guards sending this model's details off the machine.
 */
export const ALLOW_JEV = 'Allow';
export function AllowButton({ onAllowed }: { onAllowed: () => void }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const press = () => {
    if (busy) return;
    setBusy(true); setError(null);
    void rpc<ConsentInfoW>('getConsent').then((info) => {
      if (!info?.fieldsInForce?.length || info.schemaVersionInForce == null) throw new Error('Permission was not saved.');
      return rpc<ConsentInfoW>('setConsent', info.fieldsInForce, info.schemaVersionInForce, 'human', info.appliesTo ?? null);
    }).then((next) => {
      if (!next?.current) throw new Error(next?.reason ?? 'Permission was not saved.');
      setBusy(false);
      onAllowed();
    }).catch((reason: unknown) => {
      setError(reason instanceof Error ? reason.message : String(reason));
      setBusy(false);
    });
  };
  return (
    <span className="sem-inline" data-testid="askdata-allow">
      <button type="button" className="sem-btn sem-btn-sm sem-btn-primary self-start" disabled={busy} onClick={press}>{ALLOW_JEV}</button>
      {error && <span role="alert" style={{ color: 'var(--sem-fg)' }}>{error}</span>}
    </span>
  );
}

/** The dialog both variants of the card sit in: the backdrop that closes it, and the one panel. One place, so the two cannot drift. */
function CardShell({ label, testid, onClose, children }: { label: string; testid: string; onClose: () => void; children: ReactNode }) {
  return (
    <div className="fixed inset-0 z-[90] flex items-center justify-center p-4" role="presentation"
      style={{ background: 'color-mix(in srgb, var(--sem-bg) 72%, transparent)' }}
      onMouseDown={(event) => { if (event.target === event.currentTarget) onClose(); }}>
      <section role="dialog" aria-modal="true" aria-label={label} data-testid={testid}
        className="sem-card w-[min(560px,94vw)]"
        style={{ background: 'var(--sem-bg)', boxShadow: 'var(--sem-shadow-pop)' }}>
        {children}
      </section>
    </div>
  );
}

/**
 * The card itself, mounted once. It reads the declared fields from the engine at the moment it opens and
 * agrees to exactly those: a list written down here would be a second copy of the contract, and the day
 * a point declared a new field the person would be agreeing to a list that no longer describes what goes.
 */
export function ConsentCard() {
  const { open, reask, scope, startPlan } = useCardState();
  const { runtime: learn } = useLearn();
  const [modelCurrent, setModelCurrent] = useState<boolean | null>(null);
  const [fields, setFields] = useState<string[] | null>(null);
  const [version, setVersion] = useState<number | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // The card as the plan reads NOW: each table choice is an edit of the plan, which answers with the card again, bound to the new digest.
  const [edited, setEdited] = useState<StartPlanCardW | null>(null);
  useEffect(() => { setEdited(null); }, [startPlan]);
  const plan = edited ?? startPlan;
  const choose = (e: { op: string; target: string; value?: string }) => {
    if (busy || !startPlan?.edit) return;
    setBusy(true); setError(null);
    void (plan?.edit ?? startPlan.edit)(e).then((next) => { setEdited(next); setBusy(false); })
      .catch((reason: unknown) => { setError(reason instanceof Error ? reason.message : String(reason)); setBusy(false); });
  };

  useEffect(() => {
    if (!open) return;
    let live = true;
    setError(null);
    setModelCurrent(null);
    void Promise.all([
      rpc<DecisionPointW[]>('getDecisionPoints'),
      rpc<ConsentInfoW>('getConsent'),
    ]).then(([points, consent]) => {
      if (!live) return;
      // The engine's own list of what is in force (get_consent's fieldsInForce): every field of every point the
      // card may cover. Agreeing to less leaves permission not current, so the card would close and the page would
      // still be without an answer. One list, the engine's, so the card grants exactly what get_consent reports and
      // the settings fold shows (verify-askdata defect 7 found the card granting query.summarize before it named it).
      // An older engine without the list falls back to every point's fields.
      const declared = consent?.fieldsInForce?.length ? [...consent.fieldsInForce].sort()
        : [...new Set((points ?? []).flatMap((p) => p.fields ?? []))].sort();
      setFields(declared);
      setVersion(consent?.schemaVersionInForce ?? null);
      setModelCurrent(consent?.current ?? false);
    }).catch((reason: unknown) => {
      if (live) setError(reason instanceof Error ? reason.message : String(reason));
    });
    return () => { live = false; };
  }, [open]);

  const close = useCallback(() => publishCard({ open: false }), []);

  // ANSWERED ON THE OTHER DOOR (verify, 28 Sep, L8). The assistant can give permission while this card is up. The card
  // then stood asking for something already given, and OK refused, because a first grant stamps the model and the
  // scope the card was raised about is no longer the scope in force. The stamp is a change on the bus, so on any change
  // while the card is open, ask the engine again: permission current for the model open now means the question is
  // answered, so the card closes and the page asks its own question again, exactly as after OK. A change of MODEL is
  // not this case: watchModelChanges closes the card on a swap without re-asking anything.
  useEffect(() => {
    // Not for the start plan's variant: the model's own permission being current does not answer ITS question, which is
    // which tables Pro may see. Only the grant button answers that.
    if (!open || startPlan) return;
    let live = true;
    const off = onDidChange(() => {
      void rpc<ConsentInfoW>('getConsent').then((consent) => {
        if (!live || !consent?.current || !cardState.open) return;
        close();
        reask?.();
      }).catch(() => { /* the card stays; OK still says plainly what happened */ });
    });
    return () => { live = false; off(); };
  }, [open, reask, close, startPlan]);

  useEffect(() => {
    if (!open) return;
    const key = (event: globalThis.KeyboardEvent) => { if (event.key === 'Escape') close(); };
    window.addEventListener('keydown', key);
    return () => window.removeEventListener('keydown', key);
  }, [open, close]);

  if (!open || learn.phase !== 'idle') return null;

  const agreeStartPlan = () => {
    if (busy || !startPlan || !fields || fields.length === 0 || version == null || modelCurrent == null) return;
    setBusy(true); setError(null);
    // The model's own permission first when it is missing (the engine refuses the grant without it), then the tables. Both
    // carry the person's own word: this button is the only way either is given from this card.
    const model = modelCurrent
      ? Promise.resolve<ConsentInfoW | null>({ current: true })
      : rpc<ConsentInfoW>('setConsent', fields, version, 'human', scope ?? null);
    void model.then((next) => {
      if (!next?.current) throw new Error(next?.reason ?? 'Permission was not saved.');
      return (plan ?? startPlan).grant();
    }).then(() => {
      setBusy(false);
      const stillOpen = cardState.open;
      close();
      if (stillOpen) reask?.();
    }).catch((reason: unknown) => {
      setError(reason instanceof Error ? reason.message : String(reason));
      setBusy(false);
    });
  };

  const agree = () => {
    if (startPlan) { agreeStartPlan(); return; }
    if (busy || !fields || fields.length === 0 || version == null) return;
    setBusy(true); setError(null);
    // The model this card is a question ABOUT travels with the answer. If the model changed while the
    // card was open, the engine refuses and says so here rather than granting permission for whatever
    // happens to be open now.
    void rpc<ConsentInfoW>('setConsent', fields, version, 'human', scope ?? null).then((next) => {
      if (!next?.current) { setError(next?.reason ?? 'Permission was not saved.'); setBusy(false); return; }
      setBusy(false);
      // The stamp this grant made is a change, and the listener above may have closed the card and asked again first.
      const stillOpen = cardState.open;
      close();
      if (stillOpen) reask?.();          // the page asks its question again, now that it may
    }).catch((reason: unknown) => {
      setError(reason instanceof Error ? reason.message : String(reason));
      setBusy(false);
    });
  };

  if (startPlan && plan) {
    const adding = (plan.newTables ?? []).length > 0;
    const title = adding ? START_CARD.alsoTitle(plan.newTables ?? []) : START_CARD.title;
    const choices = plan.choices;
    const withSample = (choices ?? []).filter((c) => c.included && !c.sampleOff).map((c) => c.name);
    const sampleWords = plan.sampleState === 'reading' ? START_CARD.sampleReading
      : !plan.sample ? START_CARD.sampleOff
      : !choices ? START_CARD.sampleOn
      : withSample.length === 0 ? START_CARD.sampleNone
      : START_CARD.sampleSome(withSample);
    const canChoose = !!choices && !!startPlan.edit;
    return (
      <CardShell label={title} testid="consent-card-start" onClose={close}>
          <h2 className="sem-card-title">{title}</h2>
          <p className="sem-text-meta m-0 mt-1">{START_CARD.lead(plan.source)}</p>
          <div className="mt-3 grid grid-cols-2 gap-3 max-[520px]:grid-cols-1">
            <div className="sem-inset" data-testid="consent-start-tables">
              <b className="text-xs">{START_CARD.tablesTitle}</b>
              {canChoose
                ? <ul className="m-0 mt-1 list-none p-0 text-xs" data-testid="consent-start-choices">
                  {choices!.map((c) => <li key={c.name} className="mb-2" data-testid={`consent-table-${c.name}`} data-included={c.included ? 'yes' : 'no'}>
                    <div className="flex items-center justify-between gap-2">
                      <span><b>{c.name}</b>{c.sourceName && c.sourceName !== c.name ? <span style={{ color: 'var(--sem-muted)' }}> {c.sourceName}</span> : null}</span>
                      <button type="button" className="sem-btn sem-btn-sm" disabled={busy} data-testid="consent-table-toggle"
                        onClick={() => choose({ op: 'include_table', target: c.name, value: c.included ? 'false' : 'true' })}>{c.included ? START_CARD.tableOut : START_CARD.tablePutBack}</button>
                    </div>
                    {c.included
                      ? (plan.sample || plan.sampleState === 'reading') && <label className="mt-1 flex items-start gap-1" style={{ color: 'var(--sem-muted)' }} title={START_CARD.tableSampleHint}>
                        <input type="checkbox" checked={!c.sampleOff} disabled={busy} data-testid="consent-table-sample"
                          onChange={() => choose({ op: 'set_table_sample', target: c.name, value: c.sampleOff ? 'on' : 'off' })} />
                        <span>{START_CARD.tableSample}</span></label>
                      : <p className="m-0 mt-1" style={{ color: 'var(--sem-muted)' }}>{START_CARD.tableLeftOut}</p>}
                  </li>)}
                </ul>
                : <ul className="m-0 mt-1 list-disc pl-4 text-xs" style={{ color: 'var(--sem-muted)' }}>
                  {plan.tables.map((name) => <li key={name}>{name}</li>)}
                </ul>}
              <p data-testid="consent-start-sample" className="m-0 mt-2 text-xs" style={{ color: 'var(--sem-muted)' }}>{sampleWords}</p>
            </div>
            <div className="sem-inset">
              <b className="text-xs">{START_CARD.neverTitle}</b>
              <ul className="m-0 mt-1 list-disc pl-4 text-xs" style={{ color: 'var(--sem-muted)' }}>
                {START_CARD.never.map((line) => <li key={line}>{line}</li>)}
              </ul>
            </div>
          </div>
          {modelCurrent === false && (
            <div className="sem-inset mt-3" data-testid="consent-start-model">
              <p className="m-0 text-xs">{START_CARD.alsoForModel}</p>
              <ul className="m-0 mt-1 list-disc pl-4 text-xs" style={{ color: 'var(--sem-muted)' }}>
                {CARD.goes.map((line) => <li key={line}>{line}</li>)}
              </ul>
            </div>
          )}
          {error && <p data-testid="consent-error" className="m-0 mt-2 text-xs" style={{ color: 'var(--sem-bad)' }}>{error}</p>}
          <div className="mt-3 flex items-center justify-end gap-2">
            <button type="button" data-testid="consent-not-now" onClick={close} className="sem-btn">{START_CARD.notNow}</button>
            <button type="button" autoFocus data-testid="consent-ok" onClick={agree}
              disabled={busy || !fields || version == null || modelCurrent == null || plan.tables.length === 0}
              className="sem-btn sem-btn-primary">
              {adding ? START_CARD.alsoOk : START_CARD.ok}
            </button>
          </div>
      </CardShell>
    );
  }

  return (
    <CardShell label={CARD.title} testid="consent-card" onClose={close}>
        <h2 className="sem-card-title">{CARD.title}</h2>
        <p className="sem-text-meta m-0 mt-1">{CARD.lead}</p>
        <div className="mt-3 grid grid-cols-2 gap-3 max-[520px]:grid-cols-1">
          {[[CARD.goesTitle, CARD.goes], [CARD.neverTitle, CARD.never]].map(([heading, lines]) => (
            <div key={heading as string} className="sem-inset">
              <b className="text-xs">{heading as string}</b>
              <ul className="m-0 mt-1 list-disc pl-4 text-xs" style={{ color: 'var(--sem-muted)' }}>
                {(lines as string[]).map((line) => <li key={line}>{line}</li>)}
              </ul>
            </div>
          ))}
        </div>
        {error && <p data-testid="consent-error" className="m-0 mt-2 text-xs" style={{ color: 'var(--sem-bad)' }}>{error}</p>}
        <div className="mt-3 flex items-center justify-end gap-2">
          <button type="button" data-testid="consent-not-now" onClick={close} className="sem-btn">{CARD.notNow}</button>
          <button type="button" autoFocus data-testid="consent-ok" onClick={agree} disabled={busy || !fields || version == null}
            className="sem-btn sem-btn-primary">
            {CARD.ok}
          </button>
        </div>
    </CardShell>
  );
}

// ---- the permission line in Your assistant > What Pro decides --------------------------------------

const NEVER_SAVED_LINE = 'Save this model before you can let Pro decide for it. Permission is kept for one model at a time.';
const FOOTNOTE = 'Each model you open asks you once, whoever opens it.';

/** "21 Sep", in the reader's own zone. A date with no day in it reads as a different promise. */
function shortDate(when: string | null | undefined): string | null {
  if (!when) return null;
  const at = new Date(when);
  return Number.isNaN(at.getTime()) ? null
    : at.toLocaleDateString(undefined, { day: 'numeric', month: 'short' });
}

/**
 * One line at the top of Your assistant > What Pro decides: where permission lives once the card has been
 * answered. (Connections carried it until slice C; it moved here because consent is a setting, not a connection.)
 *
 * It reads get_consent and shows one of three states, because there really are three: on (with the date
 * and a way to take it back), off (with a way to raise the card again), and a model that has never been
 * saved, which cannot be granted at all. Free sessions see nothing here: permission is for a Pro feature
 * and offering it to somebody who cannot use it is a dead control.
 */
export function ProAdviceLine() {
  const tier = useTier();
  const [consent, setConsent] = useState<ConsentInfoW | null>(null);
  const [busy, setBusy] = useState(false);

  const read = useCallback(() => {
    void rpc<ConsentInfoW>('getConsent').then(setConsent).catch(() => setConsent(null));
  }, []);

  useEffect(() => {
    if (tier !== 'pro') return;
    watchModelChanges();
    read();
    // The card writes permission from somewhere else entirely, so this line follows the card rather than
    // its own button: answering the card and then opening Connections must not show yesterday's answer.
    const follow = () => read();
    cardListeners.add(follow);
    // A first save gives the model somewhere to keep permission, and rides the change bus: read again then, or the
    // line kept saying "Save this model before you can let Pro decide for it" after the save.
    const off = onDidChange(() => read());
    return () => { cardListeners.delete(follow); off(); };
  }, [tier, read]);

  if (tier !== 'pro' || !consent) return null;

  const withdraw = () => {
    if (busy) return;
    setBusy(true);
    void rpc<ConsentInfoW>('withdrawConsent').then((next) => { setConsent(next); setBusy(false); })
      .catch(() => { read(); setBusy(false); });
  };

  const since = shortDate(consent.grantedUtc);
  const noModel = !consent.appliesTo;

  return (
    <div className="mt-3 border-b pb-2" style={{ borderColor: 'var(--sem-rule)' }} data-testid="consent-line">
      <div className="flex items-center gap-2">
        {noModel ? (
          <span data-testid="consent-never-saved" className="text-xs" style={{ color: 'var(--sem-muted)' }}>{NEVER_SAVED_LINE}</span>
        ) : consent.current ? (
          <>
            <span data-testid="consent-state" className="text-xs">Pro advice for this model: <b>on</b>{since ? ` since ${since}` : ''}</span>
            <button type="button" data-testid="consent-withdraw" onClick={withdraw} disabled={busy}
              className="sem-btn sem-btn-sm ml-auto">Withdraw</button>
          </>
        ) : (
          <>
            <span data-testid="consent-state" className="text-xs">Pro advice for this model: <b>off</b></span>
            <button type="button" data-testid="consent-turn-on" onClick={() => openConsentCard(read, consent.appliesTo)}
              className="sem-btn sem-btn-sm ml-auto">Turn on</button>
          </>
        )}
      </div>
      {!noModel && consent.current && consent.grantedBy === 'agent' && (
        <p data-testid="consent-by-assistant" className="sem-text-meta m-0 mt-2">Turned on by your assistant. Withdraw turns it off.</p>
      )}
      {!noModel && <p className="sem-text-meta m-0 mt-2">{FOOTNOTE}</p>}
    </div>
  );
}
