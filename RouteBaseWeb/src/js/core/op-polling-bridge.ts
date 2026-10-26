/**
 * Operation-toast polling bridge (leaf slot module).
 *
 * Lives in core because segment-analysis-wait.ts (a core module) consumes it.
 * op-toast.ts — a per-page layout script in its own bundle — registers the single
 * start(kind) hook; page bundles call the named call-throughs. Each call-through
 * no-ops until op-toast registers, identical to the old `window.startXPolling?.()`
 * fire-and-forget guards. Names kept verbatim from the former window globals for
 * grep continuity.
 */

export type OpKind = 'analysis' | 'import' | 'trackImport' | 'heatmap' | 'export';

let _start: ((kind: OpKind) => void) | null = null;

/** Registered by op-toast.ts at load; the sole provider of the poll loop. */
export function registerOpPolling(start: (kind: OpKind) => void): void {
	_start = start;
}

export function startAnalysisPolling(): void { _start?.('analysis'); }
export function startImportPolling(): void { _start?.('import'); }
export function startTrackImportPolling(): void { _start?.('trackImport'); }
export function startHeatmapPolling(): void { _start?.('heatmap'); }
export function startExportPolling(): void { _start?.('export'); }
