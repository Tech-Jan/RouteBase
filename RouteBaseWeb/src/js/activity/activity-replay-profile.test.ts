import { beforeEach, describe, expect, it, vi } from 'vitest';
import { must } from '../core/invariant';
import { createActivityReplayProfile } from './activity-replay-profile';
import type { GpsPoint } from '../core/core-types';

/**
 * Two things are worth pinning here. The MOUNT decision: a strip that appears for a ride with no profile
 * is a panel the user has to close by hand, and a grip that pulls up nothing is worse than no grip at
 * all. And the AXIS conversion: this adapter is the seam between an engine that counts in samples and a
 * chart whose x-axis is distance, so it is the one place that can put the marker off the summit.
 *
 * core/profile-chart is stubbed — the drawing has its own tests, and what matters here is the value this
 * module hands it. The terrain build is NOT stubbed: the axis it produces is half of the contract.
 */
interface StubTrace { x: ArrayLike<number>; y: ArrayLike<number>; color: string; unit: string }
interface StubOptions {
	backdrop: { elevation: ArrayLike<number> };
	formatHover?: (value: number, y: number | null, trace: number | null) => string;
}

const { setPlayhead, setTrace, built } = vi.hoisted(() => ({
	setPlayhead: vi.fn<(v: number | null) => void>(),
	/** The speed overlay handed to the chart, or null when the ride carries no usable speed. */
	setTrace: vi.fn<(t: unknown) => void>(),
	/** Every option set handed to the chart — the backdrop it draws and the hover it formats. */
	built: [] as StubOptions[],
}));

vi.mock('../core/profile-chart', () => ({
	PROFILE_TRACE_COLOR: '#3b82f6',
	createProfileChart: (o: StubOptions) => {
		built.push(o);
		return {
			setDecorations: vi.fn(),
			setTrace,
			setField: vi.fn(),
			setPlayhead,
			tick: vi.fn(),
			destroy: vi.fn(),
		};
	},
}));

class FakeResizeObserver {
	observe(): void {}
	unobserve(): void {}
	disconnect(): void {}
}

function stubCanvas(): void {
	vi.stubGlobal('ResizeObserver', FakeResizeObserver);
	const ctx = {
		fillStyle: '', strokeStyle: '', globalAlpha: 1, lineWidth: 1, lineJoin: '', font: '',
		textBaseline: 'top',
		beginPath() {}, moveTo() {}, lineTo() {}, closePath() {}, arc() {}, fill() {}, stroke() {},
		fillRect() {}, fillText() {}, clearRect() {}, drawImage() {}, save() {}, restore() {},
		rect() {}, clip() {}, setTransform() {}, scale() {}, setLineDash() {},
		measureText: () => ({ width: 10 }),
	};
	HTMLCanvasElement.prototype.getContext = vi.fn(() => ctx) as never;
	HTMLCanvasElement.prototype.getBoundingClientRect = vi.fn(
		() => ({ left: 0, top: 0, width: 800, height: 120, right: 800, bottom: 120 })) as never;
}

function mountMarkup(): HTMLElement {
	document.body.innerHTML = `
		<div id="activityReplayDock" class="replay-dock replay-drawer">
			<div class="replay-dock-bar">
				<button id="activityReplayChartsHandle" hidden></button>
			</div>
			<div id="activityReplayCharts"><canvas id="activityReplayProfile"></canvas></div>
		</div>`;
	const handle = document.getElementById('activityReplayChartsHandle')!;
	handle.setPointerCapture = vi.fn();
	handle.hasPointerCapture = vi.fn(() => true);
	handle.releasePointerCapture = vi.fn();
	return handle;
}

/** A climbing ride: ~100 m between points, 10 m up each time. */
function hillyRide(n = 6): GpsPoint[] {
	return Array.from({ length: n }, (_, i) => ({
		lat: 47.9 + i * 0.0009, lng: 8.1, altitude: 500 + i * 10, speed: 20, distance: i * 100,
	}));
}

describe('createActivityReplayProfile', () => {
	beforeEach(() => {
		localStorage.clear();
		document.body.innerHTML = '';
		built.length = 0;
		setTrace.mockClear();
		stubCanvas();
	});

	it('traces the ride speed on the SAME axis the terrain was resampled from', () => {
		mountMarkup();
		const points = hillyRide(8);
		const profile = createActivityReplayProfile({ points })!;

		const trace = setTrace.mock.calls[0]?.[0] as StubTrace | null;
		expect(trace).not.toBeNull();
		// x is the backdrop's own axisAt — one value per SOURCE point, 0..100 — so the line cannot drift
		// from the terrain the way a second, independently-resampled x would.
		expect(trace!.x.length).toBe(points.length);
		expect(trace!.x[0]).toBe(0);
		expect(trace!.x[points.length - 1]).toBeCloseTo(100, 6);
		expect(trace!.y.length).toBe(points.length);

		profile.destroy();
	});

	it('draws no speed line at all when the track carries none — a flat zero would read as standing still', () => {
		mountMarkup();
		const points = hillyRide(8).map((p) => ({ ...p, speed: 0 }));

		const profile = createActivityReplayProfile({ points })!;

		expect(setTrace).toHaveBeenCalledWith(null);
		profile.destroy();
	});

	it('the hover readout drops a term rather than faking it — speed only when there is a trace', () => {
		mountMarkup();
		const profile = createActivityReplayProfile({ points: hillyRide(8) })!;
		const formatHover = must(built[0], 'the chart was built').formatHover!;

		expect(formatHover(50, 550, 24.6)).toBe('50% · 550 m · 25 km/h');
		expect(formatHover(50, 550, null)).toBe('50% · 550 m');

		profile.destroy();
	});

	it('draws the altitude the details chart draws — smoothed, not the raw signal', () => {
		mountMarkup();
		// A steady climb under a ±1 m sawtooth: barometric-looking noise that the details-page chart
		// irons out with the same Gaussian window. At source resolution the strip shows every tooth
		// unless it smooths too — which is what made the two disagree.
		const noisy: GpsPoint[] = Array.from({ length: 400 }, (_, i) => ({
			lat: 47.9 + i * 0.0009, lng: 8.1,
			altitude: 500 + i * 0.1 + (i % 2 === 0 ? 1 : -1),
			speed: 20, distance: i * 100,
		}));

		const profile = createActivityReplayProfile({ points: noisy })!;
		const ele = must(built[0], 'the chart was built').backdrop.elevation;

		// Path roughness: a monotone climb spends exactly its own height, every tooth adds ~2 m on top.
		// Raw, that is ~800 m over 400 points; smoothed it is barely above the climb itself.
		let roughness = 0;
		for (let i = 1; i < ele.length; i++) {
			roughness += Math.abs(must(ele[i], 'i < length (loop-bounded)') - must(ele[i - 1], 'i-1 >= 0'));
		}
		const climb = must(ele[ele.length - 1], 'non-empty') - must(ele[0], 'non-empty');
		expect(roughness).toBeLessThan(climb * 1.5);

		profile.destroy();
	});

	it('still declines a ride with a HOLE in its altitude — smoothing must not fill one in', () => {
		mountMarkup();
		// gaussianSmooth tolerates a missing sample as `|| 0`, which would hand the terrain build a
		// plausible number where there is no measurement, and it would draw the line it exists to refuse.
		const holed = hillyRide(300).map((p, i) => (
			i === 150 ? { ...p, altitude: undefined as unknown as number } : p));

		expect(createActivityReplayProfile({ points: holed })).toBeNull();
	});

	it('mounts for a ride with relief, and only then does the grip appear', () => {
		const handle = mountMarkup();
		expect(handle.hidden).toBe(true);

		const profile = createActivityReplayProfile({ points: hillyRide() });

		expect(profile).not.toBeNull();
		expect(handle.hidden).toBe(false);

		profile!.destroy();
		expect(handle.hidden).toBe(true);
	});

	it('declines a ride with no relief — a flat line in a panel is not worth closing by hand', () => {
		const handle = mountMarkup();
		const flat = hillyRide().map((p) => ({ ...p, altitude: 0 }));

		expect(createActivityReplayProfile({ points: flat })).toBeNull();
		expect(handle.hidden).toBe(true);
	});

	it('declines a ride whose points never went anywhere', () => {
		mountMarkup();
		const parked = Array.from({ length: 5 }, () => (
			{ lat: 47.9, lng: 8.1, altitude: 500, speed: 0, distance: 0 }));

		expect(createActivityReplayProfile({ points: parked })).toBeNull();
	});

	it('does nothing at all when the markup is absent', () => {
		document.body.innerHTML = '<div id="map"></div>';
		expect(createActivityReplayProfile({ points: hillyRide() })).toBeNull();
	});

	it('takes a track INDEX and clamps it — an index is not a percentage of anything', () => {
		mountMarkup();
		const profile = createActivityReplayProfile({ points: hillyRide() })!;

		expect(() => {
			profile.tick(-5);
			profile.tick(2.5);
			profile.tick(140, true);   // far past the end: valid as an index, must not run off the strip
		}).not.toThrow();

		profile.destroy();
	});

	it('puts the dot where the rider IS on the ground, not where they are in the sample count', () => {
		mountMarkup();
		// Four 100 m hops up to a summit, then one 2 km run home. The summit is point 4 of 5 — 80 % of
		// the way through the samples, but only 1/6 of the way along the ground. Handing the engine's
		// percentage straight to the chart drew it at 80 and made the summit look late; the axis this
		// chart is drawn on says ~16.7.
		const bunched: GpsPoint[] = [0, 1, 2, 3, 4, 24].map((hops, i) => ({
			lat: 47.9 + hops * 0.0009, lng: 8.1,
			altitude: i <= 4 ? 500 + i * 10 : 500,
			speed: 20, distance: hops * 100,
		}));
		const profile = createActivityReplayProfile({ points: bunched })!;

		setPlayhead.mockClear();
		profile.tick(4);

		expect(setPlayhead).toHaveBeenCalledTimes(1);
		expect(must(setPlayhead.mock.calls[0], 'one setPlayhead call')[0]).toBeCloseTo((4 / 24) * 100, 1);

		profile.destroy();
	});

	it('reports finished by INDEX — a stationary finish pins the axis at 100 while the ride runs on', () => {
		mountMarkup();
		// The last two points share a spot, so the distance axis reaches 100 an index early.
		const stops: GpsPoint[] = [0, 1, 2, 3, 3].map((hops, i) => ({
			lat: 47.9 + hops * 0.0009, lng: 8.1, altitude: 500 + i * 10, speed: 0, distance: hops * 100,
		}));
		const profile = createActivityReplayProfile({ points: stops })!;

		setPlayhead.mockClear();
		profile.tick(3);
		// Axis says "at the end" — but there is still a point to go, so the replay is not finished.
		expect(must(setPlayhead.mock.calls[0], 'one setPlayhead call')[0]).toBeCloseTo(100, 6);

		profile.destroy();
	});
});
