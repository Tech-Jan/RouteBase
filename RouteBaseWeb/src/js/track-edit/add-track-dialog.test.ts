import { describe, expect, it, beforeEach, vi } from 'vitest';
import { openAddTrackDialog, buildQueryPoint } from './add-track-dialog';
import { EditApiError } from './edit-api';
import { trackEditErrors } from './track-edit-errors';
import type { SuggestedActivityDto, TrackSuggestionsResponse } from './edit-session-api';
import type { SlotTrack } from './apply-script';

const f64 = (v: number[]): Float64Array => Float64Array.from(v);
const tick = (): Promise<void> => new Promise((r) => setTimeout(r, 0));

function dto(over: Partial<SuggestedActivityDto>): SuggestedActivityDto {
	// Explicit nulls, like the wire (the generated type requires them — G1).
	return { trackId: 1, name: 'Ride', activityType: 'Ride', startDateLocal: null, totalDistanceMeters: null, startGapMeters: null, sameDay: false, nearEnd: false, alreadyLoaded: false, ...over };
}
const resp = (suggestions: SuggestedActivityDto[]): TrackSuggestionsResponse => ({ suggestions });

beforeEach(() => { document.body.innerHTML = ''; });

describe('openAddTrackDialog', () => {
	it('renders suggestion rows with reason chips from the DTO verbatim (V18)', async () => {
		openAddTrackDialog({
			fetchSuggestions: () => Promise.resolve(resp([
				dto({ trackId: 10, name: 'Afternoon Ride', nearEnd: true, startGapMeters: 320 }),
				dto({ trackId: 11, name: 'Morning Run', sameDay: true }),
			])),
			search: () => Promise.resolve(resp([])),
			addTrack: () => Promise.resolve(),
		});
		await tick();

		const rows = document.querySelectorAll('.track-edit-add-row');
		expect(rows).toHaveLength(2);
		expect(document.body.textContent).toContain('starts 320 m from the end');
		expect(document.body.textContent).toContain('same day');
	});

	it('wire-null distance and gap render nothing — not "0.0 km" (FE-02)', async () => {
		// The wire writes explicit nulls (no WhenWritingNull server-side); a `!== undefined` guard
		// let them through and (null / 1000).toFixed(1) presented "0.0 km" as a fact.
		openAddTrackDialog({
			fetchSuggestions: () => Promise.resolve(resp([
				dto({ trackId: 40, name: 'Null Ride', activityType: null, startDateLocal: null, totalDistanceMeters: null, nearEnd: true, startGapMeters: null }),
			])),
			search: () => Promise.resolve(resp([])),
			addTrack: () => Promise.resolve(),
		});
		await tick();

		expect(document.querySelectorAll('.track-edit-add-row')).toHaveLength(1);
		expect(document.body.textContent).toContain('Null Ride');
		expect(document.body.textContent).not.toContain('0.0 km');
		expect(document.body.textContent).not.toContain('m from the end');
	});

	it('alreadyLoaded rows render disabled and do not invoke addTrack (V19)', async () => {
		const addTrack = vi.fn(() => Promise.resolve());
		openAddTrackDialog({
			fetchSuggestions: () => Promise.resolve(resp([dto({ trackId: 12, name: 'Old Commute', alreadyLoaded: true })])),
			search: () => Promise.resolve(resp([])),
			addTrack,
		});
		await tick();

		const row = document.querySelector('.track-edit-add-row') as HTMLButtonElement;
		expect(row.disabled).toBe(true);
		expect(row.classList.contains('is-loaded')).toBe(true);
		row.click();
		await tick();
		expect(addTrack).not.toHaveBeenCalled();
		expect(document.body.textContent).toContain('in editor');
	});

	it('typing debounces into track-search and replaces the list (V20)', async () => {
		const search = vi.fn(() => Promise.resolve(resp([dto({ trackId: 20, name: 'Searched Ride' })])));
		openAddTrackDialog({
			fetchSuggestions: () => Promise.resolve(resp([dto({ trackId: 10, name: 'Suggested' })])),
			search,
			addTrack: () => Promise.resolve(),
			debounceMs: 0,
		});
		await tick();

		const input = document.querySelector('.track-edit-add-search') as HTMLInputElement;
		input.value = 'ride';
		input.dispatchEvent(new Event('input'));
		await tick();

		expect(search).toHaveBeenCalledWith('ride', undefined);
		expect(document.body.textContent).toContain('Searched Ride');
		expect(document.body.textContent).not.toContain('Suggested');
	});

	it('query point is built from the preview channels; NaN tail skipped (V21)', () => {
		const slot: SlotTrack = { count: 3, channels: { lat: f64([47, 47.1, NaN]), lng: f64([8, 8.1, NaN]), time: f64([1000, 1010, NaN]) } };
		const qp = buildQueryPoint(slot, 2);
		expect(qp.endLat).toBe(47.1); // last present pair (NaN tail skipped)
		expect(qp.endLng).toBe(8.1);
		expect(qp.localDate).toBe('1970-01-01'); // first present time + tz
	});

	it('pick invokes addTrack once and closes on success; maps duplicateLoad on failure (V22)', async () => {
		const ok = vi.fn(() => Promise.resolve());
		const handle = openAddTrackDialog({
			fetchSuggestions: () => Promise.resolve(resp([dto({ trackId: 30, name: 'Pick Me' })])),
			search: () => Promise.resolve(resp([])),
			addTrack: ok,
		});
		await tick();
		(document.querySelector('.track-edit-add-row') as HTMLButtonElement).click();
		await tick();
		expect(ok).toHaveBeenCalledTimes(1);
		expect(document.body.contains(handle.el)).toBe(false); // closed

		document.body.innerHTML = '';
		const dup = vi.fn(() => Promise.reject(new EditApiError(422, trackEditErrors.duplicateLoad)));
		const handle2 = openAddTrackDialog({
			fetchSuggestions: () => Promise.resolve(resp([dto({ trackId: 31, name: 'Dup' })])),
			search: () => Promise.resolve(resp([])),
			addTrack: dup,
		});
		await tick();
		(document.querySelector('.track-edit-add-row') as HTMLButtonElement).click();
		await tick();
		expect(document.body.contains(handle2.el)).toBe(true); // stays open
		const row = document.querySelector('.track-edit-add-row') as HTMLButtonElement;
		expect(row.classList.contains('is-loaded')).toBe(true);
	});
});
