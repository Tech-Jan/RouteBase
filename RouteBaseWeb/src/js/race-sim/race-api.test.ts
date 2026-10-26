// @vitest-environment node
import { describe, expect, it, vi } from 'vitest';
import { fetchRaceReplay } from './race-api';

/**
 * The failure mode this slice keeps having, seen from the browser.
 *
 * When a non-finite double reaches the payload, System.Text.Json throws while WRITING the body — the
 * status line (200) and the first chunks are already gone. So the client gets a 200 whose body stops in
 * the middle of a number, `response.ok` is true, and `.json()` rejects with a bare SyntaxError. Chrome
 * shows ERR_INCOMPLETE_CHUNKED_ENCODING; `fetch` says "Failed to fetch"; the UI says nothing at all.
 *
 * Twice now that has cost a hand-debug in the browser to find. The client cannot prevent the server bug,
 * but it MUST be able to name it — a truncated body is a server fault, and it is distinguishable from a
 * network drop and from a 4xx.
 */
describe('fetchRaceReplay', () => {
	function respondWith(body: string, init: ResponseInit = { status: 200 }): void {
		vi.stubGlobal(
			'fetch',
			vi.fn(async () =>
				new Response(body, { headers: { 'Content-Type': 'application/json' }, ...init }),
			),
		);
	}

	it('names a truncated body as a server fault, rather than throwing a bare SyntaxError', async () => {
		// Exactly what the server emits when it throws mid-serialization: valid JSON, cut off at the
		// number it refused to write.
		respondWith('{"raceId":1,"course":{"lat":[1.0,2.0,');

		await expect(fetchRaceReplay(1)).rejects.toThrow(/incomplete|truncated|server/i);
	});

	it('does not report a truncated body as if the race were merely missing', async () => {
		respondWith('{"raceId":1,"participants":[{"elapsedSeconds":');

		// The distinction that matters: a 404 is "no such race" (the user's problem), a truncated 200 is
		// "this race has data the server cannot send" (ours). Collapsing them hides a data bug.
		await expect(fetchRaceReplay(1)).rejects.not.toThrow(/not found|404/i);
	});

	it('still reports a real HTTP error by status', async () => {
		respondWith('', { status: 404 });

		await expect(fetchRaceReplay(1)).rejects.toThrow(/404/);
	});

	it('returns the payload when the body is whole', async () => {
		respondWith(JSON.stringify({ raceId: 7, raceName: 'Ski to Sea', participants: [] }));

		const payload = await fetchRaceReplay(7);

		expect(payload.raceId).toBe(7);
		expect(payload.raceName).toBe('Ski to Sea');
	});
});
