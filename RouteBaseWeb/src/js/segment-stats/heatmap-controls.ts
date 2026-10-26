/**
 * Segment Statistics — Heatmap controls (data lifecycle)
 *
 * A heatmap is an optional raster overlay on a shapes segment. This module owns the heatmap's data
 * lifecycle — render status, direct-link polling, and the owner "Generate / Regenerate" button — and
 * attaches/detaches the raster on the MapLibre map.
 *
 * The heatmap is presented as one of the mutually-exclusive "Show" views; segment-stats-map.ts owns
 * that menu + the selection. These controls don't decide when the heatmap is shown — the orchestrator
 * calls `applyOverlay()` with the current view's intent. Status changes are reported via
 * `onStatusChange` so the orchestrator can enable/label the Heatmap menu item, and a finished Generate
 * reports `onGenerated` so it can switch to the Heatmap view.
 */
import { ctx } from './segment-statistics';
import { heatmapDisplay } from './heatmap-layer';
import { startHeatmapPolling } from '../core/op-polling-bridge';
import { escapeHtml } from '../core/html';

interface HeatmapControlsOpts {
	/** Re-run the map's _applyLayers so the raster attaches/detaches for the current view. */
	redraw: () => void;
	/** Report the live render status so the orchestrator can sync the Heatmap "Show" menu item. */
	onStatusChange: (status: string) => void;
	/** A render just finished (viewable tiles) → the orchestrator switches to the Heatmap view. */
	onGenerated: () => void;
}

export interface HeatmapControls {
	/** True once tiles are viewable (done/stale). */
	tilesReady(): boolean;
	/** Live render status, mirrors the server, or 'none' before the first render exists. */
	status(): string;
	/** Attach the raster when `show` (and tiles ready), else detach it. */
	applyOverlay(map: maplibregl.Map, show: boolean, fit: boolean): void;
}

/** Inert controls: no DOM, no fetch, no timers. */
const NO_HEATMAP: HeatmapControls = {
	tilesReady: () => false,
	status: () => 'none',
	applyOverlay: () => { /* nothing to attach or detach */ },
};

export function createHeatmapControls(opts: HeatmapControlsOpts): HeatmapControls {
	const cfg = ctx.config;

	// Structural suppression for routes that offer no heatmap (a share view). The server already omits
	// hasHeatmap/heatmapStatus there, which alone makes `status` compute 'none' and skips the poll — but
	// keying on the SURFACE means a contributor who later serializes heatmapStatus onto a share island
	// still cannot start the 2s x 300 poll loop. Absent key = permissive, so the signed-in page is
	// unaffected.
	if (cfg.showHeatmap === false) {
		opts.onStatusChange('none');
		return NO_HEATMAP;
	}

	const segmentId = cfg.segmentId ?? 0;
	const genBtn = document.getElementById('segHeatmapGenBtn') as HTMLButtonElement | null;

	// Live render status: pending|running|done|stale|failed, or 'none' before the first render exists.
	let status = cfg.heatmapStatus ?? (cfg.hasHeatmap ? 'done' : 'none');

	function tilesReady(): boolean { return status === 'done' || status === 'stale'; }

	function syncGenBtn(): void {
		if (!genBtn) return;
		const verb = status === 'none' ? 'Generate' : 'Regenerate';
		genBtn.innerHTML = `<i class="bi bi-fire"></i> ${escapeHtml(verb)} heatmap`;
	}

	/** Fetch metadata (resolves at once for terminal states, polls through pending/running), then
	 *  reflect the settled status and redraw so the raster attaches if the Heatmap view is active. */
	async function poll(): Promise<void> {
		const meta = await heatmapDisplay.load(segmentId, (m) => {
			status = m.status;
			opts.onStatusChange(status);
		});
		if (!meta) return;
		status = meta.status;
		opts.onStatusChange(status);
		syncGenBtn();
		opts.redraw();
	}

	/** Attach/regenerate the heatmap. The statistics-page button has no type UI → all activity types. */
	async function generate(): Promise<void> {
		if (!segmentId || !genBtn) return;
		genBtn.disabled = true;
		try {
			const resp = await fetch(`/api/heatmaps/${segmentId}`, {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify({ activityTypes: [] }),
			});
			// 409 = a render is already pending/running for this segment. That is the state this click
			// was asking for, so adopt it — same polling, same button state, same operation toast. It
			// used to alert() instead, which blocked the page to announce a job it was happy about.
			if (!resp.ok && resp.status !== 409) {
				window.alert(`Failed to start heatmap generation (HTTP ${resp.status}).`);
				return;
			}
			startHeatmapPolling(); // global operation toast
			status = 'running';
			opts.onStatusChange(status);
			await poll();
			if (tilesReady()) opts.onGenerated(); // show the fresh render
		} catch (err) {
			console.error('generate heatmap failed', err);
			window.alert('Failed to start heatmap generation.');
		} finally {
			genBtn.disabled = false;
		}
	}

	genBtn?.addEventListener('click', () => void generate());

	syncGenBtn();
	opts.onStatusChange(status);
	// Populate _meta (and resume polling if a render is mid-flight) whenever a heatmap row exists.
	if (segmentId && status !== 'none') void poll();

	return {
		tilesReady,
		status(): string { return status; },
		applyOverlay(map: maplibregl.Map, show: boolean, fit: boolean): void {
			if (show && tilesReady()) heatmapDisplay.attachMapLibre(map, fit);
			else heatmapDisplay.detachMapLibre(map);
		},
	};
}
