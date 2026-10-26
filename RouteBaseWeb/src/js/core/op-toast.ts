import { asyncCallback } from './async-listener';
import { must } from './invariant';
import { registerOpPolling } from './op-polling-bridge';
import { OP_TOAST_HIDDEN_EVENT } from './dom-events';
import type { ProgressStatus } from './queue-progress-contract';

(function () {
    'use strict';

    // ─────────────────────────────────────────────────────────────
    // Unified background-operation progress toast
    //
    // To add a new operation type in the future:
    //   1. Add a server-side progress singleton + background worker
    //   2. Add a GET progress + POST acknowledge endpoint
    //   3. Register the type in OP_TYPES below — that's it.
    //
    // Uses CSS custom-property colours with hard-coded fallbacks so
    // it works both on cockpit pages (tokens defined) and on Bootstrap
    // pages that lack the cockpit :root block.
    // ─────────────────────────────────────────────────────────────

    // ProgressStatus (the GET /api/queue/progress union) is imported from ./queue-progress-contract.

    /** Shape of POST /api/queue/cancel — mirrors the C# CancelOperationsResponse DTO (QueueContracts.cs).
     *  Parsed + typed to pin the contract; not rendered (the toast shows its own text). */
    interface CancelOperationsResponse { message: string }

    interface OpTypeConfig {
        title:         string;
        icon:          string;          // emoji / character shown in the icon slot
        accentColor:   string;          // CSS value for the progress bar
        defaultPhase:  string;
        completedText: string;
        failedText:    string;
    }

    // ── Per-type display config ──────────────────────────────────
    const OP_TYPES: Record<string, OpTypeConfig> = {
        analysis: {
            title:         'Segment Analysis',
            icon:          '⚡',
            accentColor:   'var(--acc-amber, #f5a623)',
            defaultPhase:  'Analyzing...',
            completedText: '✅ Analysis complete!',
            failedText:    '❌ Analysis failed',
        },
        import: {
            title:         'Strava Import',
            icon:          '↓',
            accentColor:   'var(--acc-cyan, #3b9eff)',
            defaultPhase:  'Importing...',
            completedText: '✅ Import complete!',
            failedText:    '❌ Import failed',
        },
        trackImport: {
            title:         'File Import',
            icon:          '📁',
            accentColor:   'var(--acc-cyan, #3b9eff)',
            defaultPhase:  'Importing...',
            completedText: '✅ File import complete!',
            failedText:    '❌ File import failed',
        },
        heatmap: {
            title:         'Heatmap',
            icon:          '🔥',
            accentColor:   'var(--acc-orange, #ff7a1a)',
            defaultPhase:  'Generating...',
            completedText: '✅ Heatmap ready!',
            failedText:    '❌ Heatmap failed',
        },
        export: {
            title:         'GPX Export',
            icon:          '🗜',
            accentColor:   'var(--acc-lime, #52c41a)',
            defaultPhase:  'Building ZIP...',
            completedText: '✅ Export ready!',
            failedText:    '❌ Export failed',
        },
    };

    const PROGRESS_URL = '/api/queue/progress';
    const ACK_URL      = '/api/queue/acknowledge';
    const CANCEL_URL   = '/api/queue/cancel';

    // ── DOM refs ─────────────────────────────────────────────────
    const toast       = document.getElementById('opToast');
    const phaseEl     = document.getElementById('opPhase');
    const progressBar = document.getElementById('opProgressBar');
    const detailEl    = document.getElementById('opDetail');
    const iconEl      = document.getElementById('opToastIcon');
    const titleEl     = document.getElementById('opToastTitle');
    const badgeEl     = document.getElementById('opQueueBadge');
    const elapsedEl   = document.getElementById('opElapsed');
    const cancelBtn   = document.getElementById('opCancelBtn');
    const downloadBtn = document.getElementById('opDownloadBtn');

    // ── State ────────────────────────────────────────────────────
    let pollTimer:             number | null = null;
    let startTime:             Date   | null = null;
    let autoHideTimer:         number | null = null;
    let dismissedWhileRunning                = false;
    let lastOpType:            string | null = null;
    let ignoreIdleUntil                      = 0;

    // ── Helpers ──────────────────────────────────────────────────
    function formatElapsed(sec: number): string {
        const m = Math.floor(sec / 60), s = Math.floor(sec % 60);
        return m > 0 ? `${m}m ${s}s` : `${s}s`;
    }

    function show(): void {
        if (toast) toast.style.display = 'block';
    }

    function hide(): void {
        if (!toast) return;
        toast.style.display = 'none';
        document.dispatchEvent(new CustomEvent(OP_TOAST_HIDDEN_EVENT));
    }

    function clearAutoHide(): void {
        if (autoHideTimer) { clearTimeout(autoHideTimer); autoHideTimer = null; }
    }

    function acknowledge(): void {
        fetch(ACK_URL, { method: 'POST' }).catch(() => {});
    }

    function stopPolling(): void {
        if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
    }

    // ── Apply per-type icon + title when type changes ────────────
    function applyType(opType: string): void {
        const cfg = OP_TYPES[opType];
        if (!cfg || opType === lastOpType) return;
        lastOpType = opType;
        if (iconEl)  iconEl.textContent  = cfg.icon;
        if (titleEl) titleEl.textContent = cfg.title;
    }

    // ── Set progress bar colour and width ────────────────────────
    function setBar(width: string, color: string): void {
        if (!progressBar) return;
        progressBar.style.width      = width;
        progressBar.style.background = color;
        progressBar.setAttribute('aria-valuenow', String(parseInt(width) || 0));
    }

    // ── Finished-with-a-file (export) ────────────────────────────
    // Returns true when a download was offered, which is also the signal to skip the auto-hide.
    function showDownload(status: ProgressStatus): boolean {
        if (!downloadBtn) return false;
        const url = status.$type === 'export' ? status.downloadUrl : null;
        if (!url) {
            downloadBtn.style.display = 'none';
            return false;
        }
        downloadBtn.setAttribute('href', url);
        downloadBtn.style.display = '';
        return true;
    }

    // ── Main UI update ───────────────────────────────────────────
    function updateUI(status: ProgressStatus): void {
        // Narrow the generated union on the `$type` shape discriminator (D30): the idle branch carries none
        // of the progress/summary fields, so peel it off first — the rest is basic|import with them all present.
        if (status.$type === 'idle') {
            // Idle / acknowledged — suppress if inside the grace window after a fresh enqueue.
            if (Date.now() > ignoreIdleUntil) { hide(); stopPolling(); }
            return;
        }

        const cfg = OP_TYPES[status.operationType] || must(OP_TYPES['analysis'], "'analysis' is declared in OP_TYPES");

        applyType(status.operationType);

        if (status.isRunning) {
            if (!dismissedWhileRunning) show();

            const pct = status.total > 0
                ? Math.round((status.current / status.total) * 100) : 0;
            setBar(`${pct}%`, cfg.accentColor);

            if (phaseEl)  phaseEl.textContent  = status.phase || cfg.defaultPhase;
            if (detailEl) detailEl.textContent  = status.detail || '';

            if (!startTime && status.startedAt) startTime = new Date(status.startedAt);
            if (startTime && elapsedEl)
                elapsedEl.textContent = formatElapsed((Date.now() - startTime.getTime()) / 1000);

            if (badgeEl) {
                if (status.queueTotal > 1) {
                    badgeEl.textContent   = `Op ${status.queuePosition} / ${status.queueTotal}`;
                    badgeEl.style.display = '';
                } else {
                    badgeEl.style.display = 'none';
                }
            }
            if (cancelBtn)   cancelBtn.style.display   = '';
            if (downloadBtn) downloadBtn.style.display = 'none'; // last run's file is not this run's
            return;
        }

        // ── Not running (status is basic|import|export; idle already handled above) ──
        dismissedWhileRunning = false;
        if (badgeEl)     badgeEl.style.display     = 'none';
        if (cancelBtn)   cancelBtn.style.display   = 'none';
        if (downloadBtn) downloadBtn.style.display = 'none'; // re-shown below only by a completed export

        if (status.phase === 'Completed') {
            show();
            setBar('100%', 'var(--acc-lime, #52c41a)');
            if (phaseEl)  phaseEl.textContent  = cfg.completedText;
            if (detailEl) detailEl.textContent  = status.completedSummary || '';
            if (status.startedAt && status.completedAt && elapsedEl) {
                const dur = (new Date(status.completedAt).getTime() - new Date(status.startedAt).getTime()) / 1000;
                elapsedEl.textContent = formatElapsed(dur);
            }
            stopPolling();
            clearAutoHide();
            // An operation that finished WITH A FILE must not auto-hide: the toast is the only place the
            // download is offered, so dismissing it after 1.5s would throw the result away in front of the
            // user. Those wait for an explicit dismiss instead.
            if (showDownload(status)) return;
            autoHideTimer = window.setTimeout(() => { hide(); acknowledge(); }, 1500);

        } else if (status.phase === 'Failed') {
            show();
            setBar('100%', 'var(--acc-red, #f5222d)');
            if (phaseEl)  phaseEl.textContent  = cfg.failedText;
            if (detailEl) detailEl.textContent  = status.error || 'Unknown error';
            stopPolling();

        } else {
            // A finished-but-not-Completed/Failed op (rare) — suppress inside the fresh-enqueue grace window.
            if (Date.now() > ignoreIdleUntil) {
                hide();
                stopPolling();
            }
        }
    }

    // ── Polling ──────────────────────────────────────────────────
    // In-flight guard: setInterval over an async fetch lets a slow (>1 s) response overlap the next
    // tick and resolve out of order, briefly painting stale progress — the class core/poll-until.ts
    // exists to prevent. Skip the tick instead.
    let pollInFlight = false;
    function poll(): Promise<ProgressStatus | null> {
        if (pollInFlight) return Promise.resolve(null);
        pollInFlight = true;
        return fetch(PROGRESS_URL)
            .then(r => r.ok ? r.json() : null)
            .then((s: ProgressStatus | null) => { if (s) updateUI(s); return s; })
            .catch(() => null)
            .finally(() => { pollInFlight = false; });
    }

    function startPolling(opType: string): void {
        dismissedWhileRunning = false;
        startTime  = null;
        lastOpType = null;
        clearAutoHide();

        // Suppress idle responses for 3 s — covers the race where an in-flight
        // poll() resolves as idle just after enqueue but before the worker starts.
        ignoreIdleUntil = Date.now() + 3000;
        stopPolling();

        const cfg = OP_TYPES[opType];
        if (cfg) {
            applyType(opType);
            if (phaseEl)  phaseEl.textContent  = cfg.defaultPhase;
            if (detailEl) detailEl.textContent  = '';
            setBar('0%', cfg.accentColor);
            if (elapsedEl) elapsedEl.textContent = '';
            if (badgeEl)   badgeEl.style.display  = 'none';
            if (cancelBtn) cancelBtn.style.display = '';
            show();
        }

        pollTimer = window.setInterval(asyncCallback('op-toast poll', poll), 1000);
        void poll();
    }

    // ── Dismiss (✕ button) ───────────────────────────────────────
    function dismissOpToast(): void {
        clearAutoHide();
        hide();
        fetch(PROGRESS_URL)
            .then(r => r.ok ? r.json() : null)
            .then((s: ProgressStatus | null) => {
                if (!s) return;
                if (s.isRunning) {
                    dismissedWhileRunning = true;
                } else {
                    stopPolling();
                    acknowledge();
                }
            })
            .catch(() => {});
    }
    document.getElementById('opDismissBtn')?.addEventListener('click', dismissOpToast);

    // ── Cancel all operations ────────────────────────────────────
    function cancelAllOperations(): void {
        // Response typed to the server contract (C#↔TS); the body is a plain ack — the toast reflects
        // the cancel through the next progress poll, so it's parsed but not rendered.
        fetch(CANCEL_URL, { method: 'POST' })
            .then((r): Promise<CancelOperationsResponse | null> => (r.ok ? r.json() : Promise.resolve(null)))
            .catch(() => {});
    }
    cancelBtn?.addEventListener('click', cancelAllOperations);

    // ── Public start hook (called from page bundles via op-polling-bridge) ────────
    registerOpPolling((kind) => startPolling(kind));

    // ── Page-load check ──────────────────────────────────────────
    void poll().then((status: ProgressStatus | null) => {
        if (!status) return;
        if (status.isRunning) {
            startPolling(status.operationType);
        } else if (status.$type !== 'idle' && (status.phase === 'Completed' || status.phase === 'Failed')) {
            if (status.completedAt) {
                const age = (Date.now() - new Date(status.completedAt).getTime()) / 1000;
                if (age < 5) {
                    updateUI(status);
                } else {
                    acknowledge();
                }
            }
        }
    });

}());

export {};
