/**
 * shape-filter/shape-filter-service.ts — talks to the live-preview endpoint.
 *
 * Debounced, abortable network concern, isolated from UI and rendering. Subscribes
 * to the store and emits the matching-activity count + ids as the user paints.
 */

import { LatestRun } from '../../core/latest-run';
import { buildRuleDocument } from './rule-document';
import type { GridFiltersMap } from './rule-document';
import { getActivitiesOwnerFilter } from '../overview-grid-bridge';
import type { ShapeStore } from './shape-store';
import type { MatchMode } from './shape-model';
import type { ShapePreviewExactResponse, ShapePreviewResponse } from '../../generated/contracts';

export interface PreviewResult extends ShapePreviewResponse {
	/** false = fast bbox estimate (may over-count); true = precise point-inside count. Client-computed —
	 * not on the wire. effortCount/truncated (SB-01: capped candidate set → count is a lower bound) are
	 * only set on the exact result (its wire always carries them since the D9 cleanup). */
	exact: boolean;
	effortCount?: ShapePreviewExactResponse['effortCount'];
	truncated?: ShapePreviewExactResponse['truncated'];
}

const DEBOUNCE_MS = 300;
const PREVIEW_URL = '/api/SegmentsApi/shapes/preview';            // fast, approximate (bbox overlap)
const PREVIEW_EXACT_URL = '/api/SegmentsApi/shapes/preview/exact'; // slower, precise (point-inside)

export class ShapeFilterService {
	private timer: number | undefined;
	private readonly run = new LatestRun();
	private unsubscribe: (() => void) | null = null;

	constructor(
		private readonly store: ShapeStore,
		// Extent affects membership now (e.g. stays-within on a clipped span), so previews need it.
		private readonly getMatchMode: () => MatchMode,
		private readonly getGridFilters: () => GridFiltersMap | null,
		// Decides which gate crossings count, so the previewed effort count moves with it.
		private readonly getGateDebounceSeconds: () => number,
		private readonly onResult: (result: PreviewResult) => void,
	) {}

	/** Re-run the preview after a non-store input changed (e.g. the match mode). */
	refresh(): void {
		this.schedule();
	}

	start(): void {
		this.unsubscribe = this.store.subscribe(() => this.schedule());
		this.schedule();
	}

	stop(): void {
		this.unsubscribe?.();
		this.unsubscribe = null;
		if (this.timer !== undefined) clearTimeout(this.timer);
		this.run.cancel();
	}

	private schedule(): void {
		if (this.timer !== undefined) clearTimeout(this.timer);
		this.timer = window.setTimeout(() => void this.fetchPreview(), DEBOUNCE_MS);
	}

	private async fetchPreview(): Promise<void> {
		const shapes = this.store.getAll();
		if (shapes.length === 0) {
			this.run.cancel(); // abort any in-flight preview so its stale result can't overwrite the zero state
			this.onResult({ count: 0, trackIds: [], exact: true });
			return;
		}

		const signal = this.run.begin();
		// Include the current owner filter so the live count matches the owners shown on the map (and what
		// saving will produce); null = own-only.
		const body = JSON.stringify({
			rules: buildRuleDocument(shapes, this.getMatchMode(), {
				gridFilters: this.getGridFilters(),
				gateDebounceSeconds: this.getGateDebounceSeconds(),
			}),
			owner: getActivitiesOwnerFilter(),
		});

		// Phase 1 — instant bbox estimate, so the number appears immediately.
		const fast = await this.post<ShapePreviewResponse>(PREVIEW_URL, body, signal);
		if (signal.aborted) return;
		if (fast) this.onResult({ ...fast, exact: false });

		// Phase 2 — precise point-inside count in the background; refines the number
		// (typically downward) once the GPS scan finishes. A newer paint aborts it.
		const exact = await this.post<ShapePreviewExactResponse>(PREVIEW_EXACT_URL, body, signal);
		if (signal.aborted) return;
		if (exact) this.onResult({ ...exact, exact: true });
	}

	private async post<T extends ShapePreviewResponse>(url: string, body: string, signal: AbortSignal): Promise<T | null> {
		try {
			const response = await fetch(url, {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				body,
				signal,
			});
			if (!response.ok) {
				console.warn('shape preview rejected (HTTP ' + response.status + ')', await response.text().catch(() => ''));
				return null;
			}
			const parsed: T = await response.json();
			return parsed;
		} catch (err) {
			if (!(err instanceof DOMException && err.name === 'AbortError')) {
				console.error('shape preview failed', err);
			}
			return null;
		}
	}
}
