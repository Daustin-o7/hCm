export function WorkspaceSkeleton() {
  return (
    <div className="flex h-screen w-screen bg-[var(--color-bg,#F5F6F2)] overflow-hidden font-sans">
      {/* ── Left Sidebar Silhouette (Desktop) ── */}
      <aside className="hidden lg:flex w-64 bg-[#0B131E] border-r border-[#1E293B] flex-col p-4 shrink-0">
        <div className="flex items-center gap-3 pb-6 border-b border-slate-800">
          <div className="w-9 h-9 rounded-md bg-teal-800/40 animate-pulse" />
          <div className="space-y-1.5 flex-1">
            <div className="w-24 h-3 rounded bg-slate-700 animate-pulse" />
            <div className="w-16 h-2 rounded bg-slate-800 animate-pulse" />
          </div>
        </div>

        <nav className="mt-6 space-y-2 flex-1">
          {[...Array(6)].map((_, i) => (
            <div key={i} className="flex items-center gap-3 px-3 py-2.5 rounded-md">
              <div className="w-4 h-4 rounded bg-slate-800 animate-pulse" />
              <div
                className="h-3 rounded bg-slate-800/80 animate-pulse"
                style={{ width: `${60 + (i % 3) * 20}%` }}
              />
            </div>
          ))}
        </nav>

        <div className="pt-4 border-t border-slate-800 flex items-center gap-3">
          <div className="w-8 h-8 rounded-full bg-slate-800 animate-pulse" />
          <div className="space-y-1 flex-1">
            <div className="w-20 h-2.5 rounded bg-slate-700 animate-pulse" />
            <div className="w-12 h-2 rounded bg-slate-800 animate-pulse" />
          </div>
        </div>
      </aside>

      {/* ── Main Content Area ── */}
      <div className="flex-1 flex flex-col min-w-0 overflow-hidden">
        {/* Topbar Silhouette */}
        <header className="h-14 border-b border-[var(--color-border)] bg-[var(--color-surface,#FFFFFF)] px-6 flex items-center justify-between shrink-0">
          <div className="flex items-center gap-3">
            <div className="w-28 h-4 rounded bg-slate-200 dark:bg-slate-700 animate-pulse" />
            <div className="w-2 h-2 rounded-full bg-slate-300 dark:bg-slate-600" />
            <div className="w-20 h-3 rounded bg-slate-100 dark:bg-slate-800 animate-pulse" />
          </div>
          <div className="flex items-center gap-3">
            <div className="w-40 h-8 rounded-md bg-slate-100 dark:bg-slate-800 border border-slate-200/60 dark:border-slate-700 animate-pulse hidden sm:block" />
            <div className="w-8 h-8 rounded-full bg-slate-200 dark:bg-slate-700 animate-pulse" />
          </div>
        </header>

        {/* Workspace Body */}
        <main className="flex-1 p-6 overflow-y-auto space-y-6">
          {/* Header Row */}
          <div className="flex items-center justify-between">
            <div className="space-y-1.5">
              <div className="w-48 h-6 rounded bg-slate-200 dark:bg-slate-700 animate-pulse" />
              <div className="w-72 h-3.5 rounded bg-slate-100 dark:bg-slate-800 animate-pulse" />
            </div>
            <div className="w-32 h-9 rounded-md bg-slate-200 dark:bg-slate-700 animate-pulse" />
          </div>

          {/* 3 Telemetry Cards */}
          <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
            {[...Array(3)].map((_, i) => (
              <div
                key={i}
                className="p-4 rounded-lg border border-[var(--color-border)] bg-[var(--color-surface,#FFFFFF)] space-y-3"
              >
                <div className="w-24 h-3 rounded bg-slate-100 dark:bg-slate-800 animate-pulse" />
                <div className="w-36 h-7 rounded bg-slate-200 dark:bg-slate-700 animate-pulse" />
                <div className="w-20 h-2.5 rounded bg-slate-100 dark:bg-slate-800 animate-pulse" />
              </div>
            ))}
          </div>

          {/* Table Outline */}
          <div className="rounded-lg border border-[var(--color-border)] bg-[var(--color-surface,#FFFFFF)] p-4 space-y-3">
            <div className="flex items-center justify-between pb-3 border-b border-slate-100 dark:border-slate-800">
              <div className="w-36 h-4 rounded bg-slate-200 dark:bg-slate-700 animate-pulse" />
              <div className="w-24 h-7 rounded-md bg-slate-100 dark:bg-slate-800 animate-pulse" />
            </div>
            {[...Array(5)].map((_, i) => (
              <div key={i} className="flex items-center justify-between py-2.5 border-b border-slate-50 dark:border-slate-800/40">
                <div className="w-24 h-3.5 rounded bg-slate-200/80 dark:bg-slate-700 animate-pulse" />
                <div className="w-40 h-3.5 rounded bg-slate-100 dark:bg-slate-800 animate-pulse" />
                <div className="w-20 h-3.5 rounded bg-slate-100 dark:bg-slate-800 animate-pulse" />
                <div className="w-16 h-5 rounded-full bg-slate-200/60 dark:bg-slate-700 animate-pulse" />
              </div>
            ))}
          </div>
        </main>
      </div>
    </div>
  )
}

export default WorkspaceSkeleton
