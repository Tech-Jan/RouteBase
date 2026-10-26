// @vitest-environment node
import type { OverviewTrack } from './overview-types';
import { must } from '../core/invariant';
import { describe, it, expect, vi, beforeEach, afterEach, type Mock } from 'vitest';
import * as prog from './activities-overview-progressive';
import { loadActivitiesChunked } from './activities-gps-loader';

// MC-03: an empty viewport at the full-res tier must upgrade nothing (never fall back to the
// whole set). Partial-mock the loader so the real OVERVIEW_TIERS / overviewTierForZoom stay,
// but loadActivitiesChunked is a spy that resolves empty.
vi.mock('./activities-gps-loader', async (orig) => ({
	...(await (orig() as Promise<Record<string, unknown>>)),
	loadActivitiesChunked: vi.fn().mockResolvedValue([]),
}));

const loadMock = loadActivitiesChunked as unknown as Mock<(...args: unknown[]) => Promise<unknown>>;

/** Track id 1 with a single usable point at (lat, lng). */
function track1At(lat: number, lng: number): OverviewTrack {
	return {
		id: 1, name: 'Run 1', date: '2026-01-01', type: 'Run', distance: 1, ascent: 10,
		points: [[lat, lng]],
	} as OverviewTrack;
}

beforeEach(() => {
	vi.useFakeTimers();
	loadMock.mockClear();
});

afterEach(() => {
	prog.reset();
	vi.useRealTimers();
});

describe('activities overview progressive — viewport upgrade scope (MC-03)', () => {
	it('pan to an empty viewport upgrades nothing', () => {
		prog.init([1], {
			getZoom: () => 12,
			getBounds: () => ({ south: 50, north: 51, west: 50, east: 51 }),
			getCachedTracks: () => [track1At(1, 10)],
			onTracksUpgraded: vi.fn(),
		});
		prog.onViewChanged();
		vi.advanceTimersByTime(600);
		expect(loadMock).not.toHaveBeenCalled();
	});

	it('visible track at full-res zoom is upgraded with maxPoints 0', () => {
		prog.init([1], {
			getZoom: () => 12,
			getBounds: () => ({ south: 0, north: 3, west: 9, east: 12 }),
			getCachedTracks: () => [track1At(1, 10)],
			onTracksUpgraded: vi.fn(),
		});
		prog.onViewChanged();
		vi.advanceTimersByTime(600);
		expect(loadMock).toHaveBeenCalledTimes(1);
		expect(loadMock).toHaveBeenCalledWith(
			expect.objectContaining({ ids: [1], maxPointsPerTrack: 0 }),
		);
	});
});

// The newest-viewport-wins guard: begin() aborts the prior run's signal, and each run's onChunk bails on
// `signal.aborted`. A superseded run's late chunk must therefore apply nothing — otherwise a slow earlier
// fetch would repaint stale detail over the newer viewport's tracks.
describe('activities overview progressive — supersede drops the older run (inFlightOwner/_upgradeSeq)', () => {
	type LoadArgs = { ids: number[]; onChunk: (t: OverviewTrack[]) => void; signal: AbortSignal };

	it('a superseded run\'s late chunk is dropped while the newer run\'s chunk applies', () => {
		const t1 = track1At(1, 10);
		const t2: OverviewTrack = { ...track1At(2, 40), id: 2, name: 'Run 2' };
		const onTracksUpgraded = vi.fn();
		let bounds = { south: 0, north: 3, west: 9, east: 12 }; // covers id 1 only
		// Never-settling loader promise so onChunk is driven by hand and finally() doesn't clear marks mid-test.
		loadMock.mockImplementation(() => new Promise<void>(() => {}));

		prog.init([1, 2], {
			getZoom: () => 12, // full tier for both
			getBounds: () => bounds,
			getCachedTracks: () => [t1, t2],
			onTracksUpgraded,
		});

		// Run A upgrades the then-visible id 1.
		prog.onViewChanged();
		vi.advanceTimersByTime(600);
		expect(loadMock).toHaveBeenCalledTimes(1);
		const runA = must(loadMock.mock.calls[0], 'run A call')[0] as LoadArgs;
		expect(runA.ids).toEqual([1]);

		// Pan so only id 2 is visible — run B begins and supersedes run A (aborts its signal).
		bounds = { south: 0, north: 3, west: 39, east: 42 };
		prog.onViewChanged();
		vi.advanceTimersByTime(600);
		expect(loadMock).toHaveBeenCalledTimes(2);
		const runB = must(loadMock.mock.calls[1], 'run B call')[0] as LoadArgs;
		expect(runB.ids).toEqual([2]);
		expect(runA.signal.aborted).toBe(true); // A was superseded by B

		// A's chunk lands late — dropped because its signal is aborted.
		runA.onChunk([{ ...t1, points: [[1, 10], [1.001, 10]] }]);
		expect(onTracksUpgraded).not.toHaveBeenCalled();

		// B's chunk applies — the surviving run repaints its track, and only its track.
		runB.onChunk([{ ...t2, points: [[2, 40], [2.001, 40]] }]);
		expect(onTracksUpgraded).toHaveBeenCalledTimes(1);
		expect(must(onTracksUpgraded.mock.calls[0], 'one upgrade call')[0].map((t: OverviewTrack) => t.id)).toEqual([2]);
	});
});
