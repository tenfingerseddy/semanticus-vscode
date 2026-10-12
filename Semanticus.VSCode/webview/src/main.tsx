// tokens.css first, ahead of every module that brings its own stylesheet: every other style reads the
// visual-language tokens it defines, and the bundle keeps CSS in import order.
import './tokens.css';
import { Component, type ErrorInfo, type ReactNode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import { ConnectionProvider } from './connection';
import { ConnectionsStandalone } from './connectionshub';
import './styles.css';
import './studio/tour';   // Learn Semanticus (1.4): registers the chapters and the player before any page draws

// The SAME bundle serves two hosts. When the host page sets window.__semanticusInitialView = 'connections' (the
// dedicated "Manage connections" panel opened while Studio is closed), the bundle mounts ONLY the Connections hub —
// one source of truth, no second bundle. Any other value mounts the full Studio.
const flags = window as unknown as { __semanticusInitialView?: string; __semanticusInitialSection?: string };
const initialView = flags.__semanticusInitialView;
// The section the standalone hub should land on (Manage Connections -> Current setup). Only the known hub views apply.
const sectionFlag = flags.__semanticusInitialSection;
const initialSection = (['open', 'setup', 'accounts', 'history', 'add', 'sqlsources', 'createsql', 'create'] as const).find((v) => v === sectionFlag);

class StudioErrorBoundary extends Component<{ children: ReactNode }, { failed: boolean }> {
  state = { failed: false };

  static getDerivedStateFromError() { return { failed: true }; }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error('[Studio] render failed', error, info.componentStack);
  }

  render() {
    if (!this.state.failed) return this.props.children;
    return (
      <div className="h-full flex items-center justify-center p-8" style={{ background: 'var(--sem-bg)', color: 'var(--sem-fg)' }}>
        <div className="max-w-md rounded-(--sem-radius-card) border p-4" style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>
          <div className="text-sm font-semibold">Studio could not finish loading</div>
          <div className="text-xs mt-1" style={{ color: 'var(--sem-muted)' }}>Reload Studio to restore the open model. Your model session is unchanged.</div>
          <button className="sem-btn sem-btn-primary mt-4" onClick={() => location.reload()}>Reload Studio</button>
        </div>
      </div>
    );
  }
}

const el = document.getElementById('root');
if (el) createRoot(el).render(
  <StudioErrorBoundary>
    <ConnectionProvider>
      {initialView === 'connections' ? <ConnectionsStandalone initialSection={initialSection} /> : <App />}
    </ConnectionProvider>
  </StudioErrorBoundary>,
);
