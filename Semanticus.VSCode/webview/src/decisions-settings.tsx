import { useEffect, useRef, useState } from 'react';
import { onDidChange, rpc } from './bridge';
import { ConsentCard, ProAdviceLine, settingsPanelShown, type DecisionPointW } from './consent';

// ===================================================================================================
// The Pro page > What Pro decides (a section, since r1 min4; it was a window behind the Your assistant menu).
//
// The other half of F-176. Three of the seven typed-decision operations are settings a person drives,
// and until this file none of them had a control anywhere in VS Code: the switches existed on the wire
// and nowhere a person could reach. This is one switch per REGISTERED point, the month's allowance, and
// a fold that says what each point sends.
//
// EVERY WORD ON THIS PAGE COMES FROM THE ENGINE, and that is the point rather than a preference. The
// registry is the only place that knows which points exist, what each one sends and which version of
// the list is in force (Semanticus.Core/Decisions/DecisionRegistry.cs). A list written down here would
// be a second copy of that contract, and the day a point is added or a field is declared, the page
// would be describing a product that no longer exists while looking perfectly correct. So the label is
// the point's own plain description, the fold is its own account of what it sends, and the only thing
// this file decides is the layout.
//
// The one exception is the id. A point's name is measure.folder_and_format, and that is a key, not a
// word: the label is the first sentence of the point's purpose, which is written for a person.
//
// FREE SESSIONS see nothing: the Pro page mounts this section in its Pro body only. Every registered point is
// Pro only, so switches here would be switches over nothing.
// ===================================================================================================

/** get_decision_budget, camelCased on the wire. Semanticus.Engine/Decisions/DecisionProtocol.cs. */
interface DecisionBudgetW {
  /** The whole licence's month, settled by the relay across every session. Absent when it cannot be read. */
  monthlyUsedInputTokens?: number | null;
  monthlyCapInputTokens?: number | null;
  /** Why the month could not be read, in the engine's own plain words. */
  monthlyReason?: string | null;
}

const TITLE = 'What Pro decides';
const LEAD = 'Turn one off and Semanticus stops asking about it.';
const SENT = 'What is sent';
const NO_POINTS = 'This build has nothing it asks the advice service about.';
/** Said when a click has no model to be for. The switches are per model, so a click that cannot name its model changes nothing. */
export const NO_MODEL_ID = 'Semanticus could not tell which model these switches are for, so nothing was changed. Close this window and open it again.';
/** A point this build never asks (its replay bar is unmet). Said plainly, beside a switch that cannot be pressed. */
export const NOT_LIVE = 'Not on yet in this version.';
/** The same fact inside What is sent, where the rest of the row is written in the present tense ("It sends the
 *  measure's name..."). A point that is not on sends NOTHING, so the fold read as a claim about today that was not
 *  true of three of its own rows (round 3, DT5). The words that follow stay, because a person deciding whether to
 *  keep Pro on is owed what it would send if it were switched on later. */
export const NOT_LIVE_SENT = 'Not on yet in this version, so nothing is sent for this. This is what it would send if it were switched on later.';

/** Whether a point's switch shows on: only a point this build can ask, and that is switched on. */
export function switchOn(point: DecisionPointW): boolean {
  return point.live !== false && point.enabled === true;
}

/** Whatever the engine answers, the rows are a list. A reply that is not one (an error object, a string) used to reach
 *  `.map` and blank all of Studio. */
export function pointsOf(reply: unknown): DecisionPointW[] {
  return Array.isArray(reply) ? (reply as DecisionPointW[]) : [];
}

/**
 * The label for a switch: the first sentence of the point's own plain description.
 *
 * Not the id, which is a key. Not a phrase of ours, which would go stale the day the point changed what
 * it does. The rest of the description is still shown, in the fold, where there is room for it.
 */
function labelOf(point: DecisionPointW): string {
  const purpose = (point.purpose ?? '').trim();
  if (!purpose) return point.caller ?? '';
  const stop = purpose.indexOf('. ');
  return stop < 0 ? purpose : purpose.slice(0, stop + 1);
}

/** The rest of the description, after the sentence the label already used. Printing the whole purpose
 *  underneath its own first sentence made the fold say the same thing twice, which a shot showed. */
function restOf(point: DecisionPointW): string {
  const purpose = (point.purpose ?? '').trim();
  const stop = purpose.indexOf('. ');
  return stop < 0 ? '' : purpose.slice(stop + 2).trim();
}

/**
 * A declared field, in plain words: "measure.folder_and_format:measure_expression" reads as "measure
 * expression". Derived rather than mapped, so a field declared tomorrow is shown tomorrow instead of
 * appearing as a key nobody translated.
 */
function plainField(field: string): string {
  const name = field.includes(':') ? field.slice(field.indexOf(':') + 1) : field;
  return name.replace(/_/g, ' ');
}

/**
 * A token count in the words the relay uses. It is an allowance, not a currency, and printing 41,203 of
 * 30,000,000 asks a person to count digits to learn something they could read in three words.
 */
function plainCount(n: number): string {
  if (n >= 1_000_000) return `${Math.round(n / 100_000) / 10} million`;
  if (n >= 1_000) return `${Math.round(n / 1_000)} thousand`;
  return String(n);
}

/** A reason the engine writes as a clause ("this session is set up for development ...") read as a sentence on
 *  the page: a capital at the front and a full stop at the end, like every other line here. */
function asSentence(reason: string): string {
  const text = reason.trim();
  if (text.length === 0) return text;
  const capital = `${text.charAt(0).toUpperCase()}${text.slice(1)}`;
  return /[.!?]$/.test(capital) ? capital : `${capital}.`;
}

export function monthLine(budget: DecisionBudgetW | null): string | null {
  if (!budget) return null;
  if (budget.monthlyUsedInputTokens == null || budget.monthlyCapInputTokens == null) {
    // Absent with a reason, never rounded into a zero that reads as measured.
    return budget.monthlyReason ? asSentence(budget.monthlyReason) : null;
  }
  return `This month: ${plainCount(budget.monthlyUsedInputTokens)} of ${plainCount(budget.monthlyCapInputTokens)}`;
}

/** What Pro decides, as a section of the Pro page (Pro only: the page mounts it in its Pro body). The 14 switches are
 *  folded behind "Choose which": Semanticus deciding is the default, so switching one off is the person's quiet task. */
export function DecisionSettingsSection() {
  const [points, setPoints] = useState<DecisionPointW[] | null>(null);
  const [budget, setBudget] = useState<DecisionBudgetW | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [choosing, setChoosing] = useState(false);
  const [sentOpen, setSentOpen] = useState<string | null>(null);
  const [reads, setReads] = useState(0);
  // Which model the rows on screen were read for. A click sends it, and the engine switches nothing when another
  // model is open by then, so rows read for one model can never set another's switch.
  const rowsFor = useRef<string | null>(null);
  const [modelName, setModelName] = useState<string | null>(null);

  // A card that arrives while a person is on this section waits for them to leave (L6): one question at a time.
  useEffect(() => { settingsPanelShown(true); return () => settingsPanelShown(false); }, []);

  // The switches are the open model's. When the other door opens another model while this section is up, the engine
  // says so with a change under the new model's id, and the rows are read again. Nothing can be pressed until they
  // are back.
  useEffect(() => {
    return onDidChange((n) => {
      const id = n?.sessionId ?? null;
      if (!id) return;
      if (id === rowsFor.current) {
        // A live open publishes its session first and stamps which live model it is a moment later, under the same id.
        // Rows read in between are the placeholder's, so a notice from the engine itself reads them again. The rows stay
        // on screen meanwhile: a click in that moment is refused by the engine, and one after it is about the same model.
        if (n.origin === 'system') setReads((r) => r + 1);
        return;
      }
      setPoints(null);
      setError(null);
      setReads((r) => r + 1);
    });
  }, []);

  // A read after a refused click keeps the refusal on screen, so this read does not clear the error.
  useEffect(() => {
    let live = true;
    // Which model first, then its rows: a model opened in between makes the click refuse, never land on the wrong model.
    void rpc<{ sessionId?: string | null; modelName?: string | null }>('sessionInfo')
      .catch(() => null)
      .then((session) => {
        if (live) { rowsFor.current = session?.sessionId ?? null; setModelName(session?.modelName?.trim() || null); }
        return rpc<unknown>('getDecisionPoints');
      })
      .then((next) => { if (live) setPoints(pointsOf(next)); })
      .catch((reason: unknown) => { if (live) setError(reason instanceof Error ? reason.message : String(reason)); });
    // The month is the one read here that goes to a network. It never throws and it never blocks the
    // switches: a relay that is down costs the counter, not the page.
    void rpc<DecisionBudgetW>('getDecisionBudget')
      .then((next) => { if (live) setBudget(next ?? null); })
      .catch(() => { if (live) setBudget(null); });
    return () => { live = false; };
  }, [reads]);

  const toggle = (point: DecisionPointW) => {
    const name = point.name;
    if (!name || busy || points == null || point.live === false) return;
    // Fail closed: rows that cannot say which model they were read for cannot set that model's switch.
    if (!rowsFor.current) { setError(NO_MODEL_ID); return; }
    setBusy(name); setError(null);
    void rpc<unknown>('setDecisionPointEnabled', name, !point.enabled, 'human', rowsFor.current)
      .then((next) => { setPoints(pointsOf(next)); setBusy(null); })
      .catch((reason: unknown) => {
        setError(reason instanceof Error ? reason.message : String(reason));
        setBusy(null);
        // Refused because another model is open now, or failed: read the rows again so what is shown is true.
        setPoints(null);
        setReads((r) => r + 1);
      });
  };

  const month = monthLine(budget);
  const rows = points ?? [];

  return (
    <section className="pp-decides" data-testid="decisions-section" aria-label={TITLE}>
      <h2 className="pp-h3">{TITLE}{modelName ? <span data-testid="decisions-model"> for {modelName}</span> : null}</h2>
      <p className="pp-m m-0 mt-2">{LEAD}</p>
      {error && <p data-testid="decisions-error" className="m-0 mt-2 text-xs" style={{ color: 'var(--sem-bad)' }}>{error}</p>}
      {/* Permission for this model, and the one place a person can take it back (withdrawConsent). */}
      <ProAdviceLine />
      <div className="mt-3 flex items-center gap-3">
        <span data-testid="decisions-month" className="text-xs" style={{ color: 'var(--sem-muted)' }}>{month ?? ''}</span>
        <button type="button" data-testid="decisions-fold" onClick={() => setChoosing((c) => !c)} aria-expanded={choosing}
          className="sem-btn sem-btn-sm sem-btn-quiet ml-auto">
          {points ? `Choose which (${rows.length})` : 'Choose which'}
        </button>
      </div>
      {choosing && (
        <div className="mt-2 flex flex-col" data-testid="decisions-body">
          {rows.length === 0 && points != null && (
            <p className="m-0 text-xs" style={{ color: 'var(--sem-muted)' }}>{NO_POINTS}</p>
          )}
          {rows.map((point) => {
            const notLive = point.live === false;
            const on = switchOn(point);
            const showSent = sentOpen === point.name;
            return (
              <div key={point.name} className="border-b py-2" style={{ borderColor: 'var(--sem-rule)' }}>
                <div data-testid="decisions-row" className="flex items-center gap-3" style={{ minHeight: 'var(--sem-row)' }}>
                  <span className="min-w-0 flex-1 text-xs">
                    {labelOf(point)}
                    {point.note && <span data-testid="decisions-note" className="mt-1 block" style={{ color: 'var(--sem-muted)' }}>{point.note}</span>}
                  </span>
                  {notLive && <span data-testid="decisions-not-live" className="shrink-0 text-xs" style={{ color: 'var(--sem-muted)' }}>{NOT_LIVE}</span>}
                  <button type="button" data-testid="decisions-sent-toggle" aria-expanded={showSent}
                    onClick={() => setSentOpen(showSent ? null : point.name ?? null)}
                    className="sem-btn sem-btn-sm sem-btn-quiet shrink-0">{SENT}</button>
                  <button type="button" role="switch" data-testid="decisions-switch" aria-checked={on}
                    aria-label={labelOf(point)} disabled={busy != null || notLive} onClick={() => toggle(point)}
                    className="relative h-5 w-8 shrink-0 rounded-full transition-colors"
                    style={{
                      background: on ? 'var(--sem-fg)' : 'var(--sem-border)',
                      opacity: busy === point.name || notLive ? 0.5 : 1,
                    }}>
                    <span className="absolute top-1 h-3 w-3 rounded-full transition-all"
                      style={{ left: on ? 18 : 2, background: 'var(--sem-bg)' }} />
                  </button>
                </div>
                {showSent && (
                  <div data-testid="decisions-sent" className="mt-1">
                    {notLive && (
                      <p data-testid="decisions-sent-not-live" className="m-0 text-xs"
                        style={{ color: 'var(--sem-muted)' }}>{NOT_LIVE_SENT}</p>
                    )}
                    <p className="m-0 mt-1 text-xs" style={{ color: 'var(--sem-muted)' }}>{restOf(point)}</p>
                    <p className="sem-text-meta m-0 mt-1">
                      {(point.fields ?? []).map(plainField).join(', ')}
                    </p>
                  </div>
                )}
              </div>
            );
          })}
        </div>
      )}
    </section>
  );
}

/**
 * The single mount for the permission card. It lives here because the card is half of the same feature (F-176):
 * one line in App.tsx rather than two, so a sibling lane rewriting the header strip has nothing extra to merge.
 */
export function ProDecisionsHost() {
  return <ConsentCard />;
}
