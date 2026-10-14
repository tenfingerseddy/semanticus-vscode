// Headless fixture. It imports the actual Studio components and token sheet.
import { useState } from 'react';
import { createRoot } from 'react-dom/client';
import { MotionProvider, GradeFlip, BulkCount, RelationshipSnap, PublishDraw, UndoRewind, JevSettle, BigWin, NextMotionStub, SmallWin } from './index';
import '../../tokens.css';
import '../../styles.css';
import './fixture.css';

declare global { interface Window { motionFixture: { play: () => void; quiet: (value: boolean) => void; hide: (value: boolean) => void; sound: (value: boolean) => void; production: (value: boolean) => void; error: (value: boolean) => void; sounds: string[] } } }

const changes = ['Renamed Amount to Sales amount', 'Hid DateKey in Sales', 'Set format on Margin %', 'Described Total Sales', 'Made Sales to Date'].map((text, i) => ({ id: String(i), text }));
const choices = [{ id: 'due', label: 'Sales[DueDate]', confidence: 0.33 }, { id: 'order', label: 'Sales[OrderDate]', confidence: 0.92 }, { id: 'modified', label: 'Sales[ModifiedAt]', confidence: 0.08 }, { id: 'ship', label: 'Sales[ShipDate]', confidence: 0.41 }];
const sounds: string[] = [];
function Fixture() {
  const [event, setEvent] = useState(0), [quiet, setQuiet] = useState(false), [hidden, setHidden] = useState(false), [sound, setSound] = useState(false);
  const [production, setProduction] = useState(false), [error, setError] = useState(false);
  window.motionFixture = { play: () => setEvent(e => e + 1), quiet: setQuiet, hide: setHidden, sound: setSound, production: setProduction, error: setError, sounds };
  return <MotionProvider options={{ personality: quiet ? 'quiet' : 'on', visible: !hidden, sound }} sounds={{ play: name => sounds.push(name) }}>
    <main className="motion-fixture sem-page"><header className="sem-card"><h1 className="sem-page-title">The small animations</h1><p className="sem-text-meta">Completed model changes. Personality: {quiet ? 'Quiet' : 'On'}. Sound: {sound ? 'On' : 'Off'}.</p><button className="sem-btn" onClick={() => setEvent(e => e + 1)}>Play</button></header>
      <div className="motion-fixture-grid" key={event}>
        <section className="sem-card"><h2 className="sem-card-title">AI readiness</h2><div className="motion-fixture-grade"><GradeFlip grade="A" eventKey={event} /><span className="sem-num">91<br /><small>Readiness score</small></span></div></section>
        <section className="sem-card"><h2 className="sem-card-title">Bulk work</h2><BulkCount count={885} elapsedMs={1200} noun="synonyms written" eventKey={event} /></section>
        <section className="sem-card"><h2 className="sem-card-title">A relationship</h2><svg className="motion-fixture-rel" viewBox="0 0 340 120"><g className="motion-fixture-table"><rect x="0" y="16" width="105" height="88" rx="3" /><text x="12" y="38">Sales</text><text x="12" y="78">DateKey</text><rect x="235" y="16" width="105" height="88" rx="3" /><text x="247" y="38">Date</text><text x="247" y="78">DateKey</text></g><RelationshipSnap eventKey={event} labels={<><text x="116" y="62">*</text><text x="215" y="62">1</text></>}><path d="M105 74H235" fill="none" stroke="var(--sem-accent)" strokeWidth="2" /></RelationshipSnap></svg></section>
        <section className="sem-card"><h2 className="sem-card-title">Publish</h2><PublishDraw first production={production} state={error ? 'error' : 'success'} eventKey={event}><span>{error ? 'The destination could not be reached.' : 'Published 14 tables to Sales (Test).'}</span></PublishDraw></section>
        <section className="sem-card"><h2 className="sem-card-title">Undo</h2><UndoRewind changes={changes} fromRevision={42} toRevision={37} eventKey={event} /></section>
        <section className="sem-card"><h2 className="sem-card-title">Jev chooses the date key</h2><JevSettle choices={choices} chosenId="order" eventKey={event} /></section>
        <section className="sem-card"><h2 className="sem-card-title">Big wins only</h2><div className="motion-fixture-win"><span className="motion-fixture-win-mark">A<BigWin kind="zero-findings" eventKey={event} /></span><span>No findings left.</span><SmallWin eventKey={event}>✓ Measure saved</SmallWin></div></section>
        <section className="sem-card"><h2 className="sem-card-title">The next step</h2><NextMotionStub text="hide 9 key columns. About a second." eventKey={event} /></section>
      </div>
    </main>
  </MotionProvider>;
}
createRoot(document.getElementById('root')!).render(<Fixture />);
