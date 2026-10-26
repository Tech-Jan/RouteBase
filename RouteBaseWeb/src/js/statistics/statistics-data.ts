/**
 * Statistics data layer: the single fetch point shared by every widget. On each filter-state change it
 * serializes the filters once (the `f.<key>.*` wire format), fetches `summary` + `cumulative` in parallel,
 * and fans the results out to subscribers. Results are cached per query string and stale responses are
 * dropped, so rapid filter edits never render an out-of-order result.
 *
 * Extension point: a new widget subscribes via {@link StatsDataLayer.onSummary}/`onCumulative` — it never
 * fetches on its own.
 */

import type { GridColumn } from '../core/grid/grid-types';
import { serializeFilters } from '../core/grid/grid-url';
import type { GridFilterState } from '../core/grid/grid-filter-state';

// Generated from the C# statistics contracts, re-exported under the established local names.
import type { ActivityStatistics, CumulativeTypePoint, StatisticsSummary, TypeStatistics } from '../generated/contracts';

/** Overall/by-type totals (server `ActivityStatistics`). */
export type ActivityStatsDto = ActivityStatistics;
export type TypeStatsDto = TypeStatistics;
export type StatsSummary = StatisticsSummary;

/** One day's totals for one raw activity type. */
export type { CumulativeTypePoint } from '../generated/contracts';

export interface StatsDataLayer {
	onSummary(fn: (s: StatsSummary) => void): void;
	onCumulative(fn: (points: CumulativeTypePoint[]) => void): void;
	/**
	 * Fires when a fetch fails and there is no last-good render to fall back on (the first load). A
	 * later failure keeps the last-good render silently (ST-04), so this never fires once anything has
	 * rendered — the page shows stale-but-valid data instead of an alarm.
	 */
	onError(fn: () => void): void;
	/** Fetch for the current filter state and dispatch to subscribers. */
	refresh(): void;
}

export function createStatsData(state: GridFilterState, columns: GridColumn<unknown>[]): StatsDataLayer {
	const summarySubs: ((s: StatsSummary) => void)[] = [];
	const cumulativeSubs: ((p: CumulativeTypePoint[]) => void)[] = [];
	const errorSubs: (() => void)[] = [];
	const cache = new Map<string, Promise<[StatsSummary, CumulativeTypePoint[]]>>();
	let latest = '';
	// Gates the first-load error surface: once any query has rendered, a failure keeps the last-good
	// render (ST-04) rather than raising the banner over valid data.
	let hasRendered = false;

	function queryString(): string {
		const p = new URLSearchParams();
		serializeFilters(columns, state.snapshot().filters, p);
		return p.toString();
	}

	async function load(qs: string): Promise<[StatsSummary, CumulativeTypePoint[]]> {
		const suffix = qs ? `?${qs}` : '';
		const [summary, cumulative] = await Promise.all([
			fetchJson<StatsSummary>(`/api/statistics/summary${suffix}`),
			fetchJson<CumulativeTypePoint[]>(`/api/statistics/cumulative${suffix}`),
		]);
		return [summary, cumulative];
	}

	async function refresh(): Promise<void> {
		const qs = queryString();
		latest = qs;
		let pending = cache.get(qs);
		if (!pending) {
			pending = load(qs);
			cache.set(qs, pending);
			// ST-04: a failed fetch must not poison the cache forever — evict on rejection so the
			// next visit to this filter state retries instead of replaying a stale/empty result.
			pending.catch(() => cache.delete(qs));
		}
		let result: [StatsSummary, CumulativeTypePoint[]];
		try {
			result = await pending;
		} catch {
			// FUX-06: on the FIRST load there is no last-good render, so a swallowed failure leaves the
			// KPI/chart skeletons at "—" forever. Surface it (once) so the page can say so; a later
			// failure with a good render on screen still stays quiet (ST-04).
			if (!hasRendered && qs === latest) errorSubs.forEach((fn) => fn());
			return; // the cache entry was evicted, next refresh retries
		}
		if (qs !== latest) return; // a newer filter change superseded this fetch
		hasRendered = true;
		const [summary, cumulative] = result;
		summarySubs.forEach((fn) => fn(summary));
		cumulativeSubs.forEach((fn) => fn(cumulative));
	}

	state.subscribe(() => void refresh());

	return {
		onSummary: (fn) => summarySubs.push(fn),
		onCumulative: (fn) => cumulativeSubs.push(fn),
		onError: (fn) => errorSubs.push(fn),
		refresh: () => void refresh(),
	};
}

// Throws on network/HTTP failure so refresh() can evict the cache entry (ST-04) instead of
// caching an empty fallback forever. The caller keeps its last-good render on rejection.
async function fetchJson<T>(url: string): Promise<T> {
	const resp = await fetch(url);
	if (!resp.ok) throw new Error(`Statistics fetch ${url} failed: HTTP ${resp.status}`);
	const body: T = await resp.json();
	return body;
}
