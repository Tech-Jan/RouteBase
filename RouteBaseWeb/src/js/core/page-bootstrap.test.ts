import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { readIsland } from './page-bootstrap';

function seedIsland(id: string, content: string): void {
	document.body.innerHTML = `<script type="application/json" id="${id}">${content}</script>`;
}

const FALLBACK = { rows: [] as number[] };

describe('readIsland', () => {
	let errSpy: ReturnType<typeof vi.spyOn>;

	beforeEach(() => {
		errSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
	});
	afterEach(() => {
		document.body.innerHTML = '';
		errSpy.mockRestore();
	});

	it('returns the fallback when the island is absent — no throw, no log', () => {
		expect(readIsland('missingIsland', FALLBACK)).toBe(FALLBACK);
		expect(errSpy).not.toHaveBeenCalled();
	});

	it('returns the fallback on an empty island — no throw, no log (absent ≠ broken)', () => {
		seedIsland('cfg', '');
		expect(readIsland('cfg', FALLBACK)).toBe(FALLBACK);
		expect(errSpy).not.toHaveBeenCalled();
	});

	it('returns the fallback on a whitespace-only island — no throw, no log', () => {
		seedIsland('cfg', '   \n\t ');
		expect(readIsland('cfg', FALLBACK)).toBe(FALLBACK);
		expect(errSpy).not.toHaveBeenCalled();
	});

	it('returns the fallback AND logs once on malformed JSON (a broken server bug must not stay silent)', () => {
		seedIsland('cfg', '{ not valid json ]');
		expect(readIsland('cfg', FALLBACK)).toBe(FALLBACK);
		expect(errSpy).toHaveBeenCalledTimes(1);
	});

	it('parses a valid JSON object', () => {
		seedIsland('cfg', JSON.stringify({ rows: [1, 2, 3] }));
		expect(readIsland('cfg', FALLBACK)).toEqual({ rows: [1, 2, 3] });
		expect(errSpy).not.toHaveBeenCalled();
	});

	it('parses a valid JSON array', () => {
		seedIsland('cfg', JSON.stringify([{ a: 1 }, { a: 2 }]));
		expect(readIsland<Array<{ a: number }>>('cfg', [])).toEqual([{ a: 1 }, { a: 2 }]);
	});

	// The exact bug this helper retires: activity-details-page.ts:47 did `JSON.parse(el.textContent ?? '')`,
	// which throws on an empty island and disabled the whole Details page. An empty #activityDetailsConfig
	// must now return the null fallback, not throw.
	it('pins the activity-details fallback: an empty #activityDetailsConfig returns null, never throws', () => {
		seedIsland('activityDetailsConfig', '');
		expect(readIsland<{ activityId: number } | null>('activityDetailsConfig', null)).toBeNull();
		expect(errSpy).not.toHaveBeenCalled();
	});
});
