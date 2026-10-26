import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// LR-04: closing the Record tab mid-recording must keepalive-flush the buffered tail
// (frozen batch + next seq) rather than dropping it silently.

const REC_IDS = [
	'rec-start-error', 'rec-gps-text', 'rec-elapsed', 'rec-distance', 'rec-speed', 'rec-points',
];

function buildRecordDom(): void {
	document.body.innerHTML = `
		<div id="rec-start"></div>
		<div id="rec-active" data-session-id="sess-1" data-track-id="9" data-started-at="2026-07-10T12:00:00Z" data-last-sequence="7" data-point-count="0"></div>
		<button id="rec-start-btn"></button>
		<button id="rec-stop-btn"></button>
		${REC_IDS.map((id) => `<span id="${id}"></span>`).join('')}
		<div id="rec-share-link"></div>
		<a id="rec-share-url"></a>
		<div id="rec-stopped-banner"></div>
		<a id="rec-activity-link"></a>
	`;
}

describe('Record page pagehide keepalive flush (LR-04)', () => {
	let fetchMock: ReturnType<typeof vi.fn>;

	beforeEach(() => {
		buildRecordDom();
		fetchMock = vi.fn(() => Promise.resolve({ ok: false, json: () => Promise.resolve({}) } as unknown as Response));
		vi.stubGlobal('fetch', fetchMock);
		// Feed exactly one point synchronously via a stubbed geolocation watch.
		vi.stubGlobal('navigator', {
			geolocation: {
				watchPosition: (success: (p: GeolocationPosition) => void) => {
					success({ coords: { latitude: 40, longitude: -70, altitude: 100, speed: 5 }, timestamp: Date.UTC(2026, 6, 10, 12, 0, 1) } as GeolocationPosition);
					return 1;
				},
				clearWatch: () => {},
			},
		});
	});

	afterEach(() => {
		vi.unstubAllGlobals();
		vi.resetModules();
	});

	it('pagehide sends a keepalive POST with the frozen seq and buffered point', async () => {
		await import('./live-recording');
		document.dispatchEvent(new Event('DOMContentLoaded'));

		window.dispatchEvent(new Event('pagehide'));

		const beacon = fetchMock.mock.calls.find(
			(c) => typeof c[0] === 'string' && c[0].includes('/points') && (c[1] as RequestInit | undefined)?.keepalive === true,
		);
		expect(beacon).toBeTruthy();
		const body = JSON.parse((beacon![1] as RequestInit).body as string);
		expect(body.clientSequence).toBe(8); // last-sequence 7 + 1
		expect(body.points).toHaveLength(1);
		expect(body.points[0].lat).toBe(40);
	});
});
