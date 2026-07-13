import { Component, type ErrorInfo, type ReactNode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import { ConnectionProvider } from './connection';
import './styles.css';

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
        <div className="max-w-md rounded-xl border p-5" style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>
          <div className="text-[15px] font-semibold">Studio could not finish loading</div>
          <div className="text-[12px] mt-1" style={{ color: 'var(--sem-muted)' }}>Reload Studio to restore the open model. Your model session is unchanged.</div>
          <button className="text-[12px] font-semibold px-3 py-1.5 rounded-lg mt-4" style={{ background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }} onClick={() => location.reload()}>Reload Studio</button>
        </div>
      </div>
    );
  }
}

const el = document.getElementById('root');
if (el) createRoot(el).render(
  <StudioErrorBoundary>
    <ConnectionProvider>
      <App />
    </ConnectionProvider>
  </StudioErrorBoundary>,
);
