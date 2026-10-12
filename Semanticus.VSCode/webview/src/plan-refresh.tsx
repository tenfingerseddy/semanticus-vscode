// Refresh plan (Kane, 11 Oct 2026): one button in the Plan's toolbar. The engine scans AI readiness and Model quality again,
// adds rows for anything new and takes the finished rows off the list (they stay in History). Its one sentence ("2 new items,
// 3 done items cleared." or "Nothing new.") is the answer, shown in the page's status line. refresh_plan is the same call.

/** The button. `busy` is the Plan's one running control: Refresh waits while another runs, and says it is working. */
export function RefreshPlanButton({ busy, onRefresh }: { busy: string | null; onRefresh: () => void }) {
  return <button type="button" className="sem-btn sem-btn-sm" data-testid="plan-refresh" disabled={!!busy}
    title="Check AI readiness and Model quality again, add anything new, and clear finished rows into History."
    onClick={onRefresh}>{busy === 'refresh' ? 'Refreshing...' : 'Refresh plan'}</button>;
}
