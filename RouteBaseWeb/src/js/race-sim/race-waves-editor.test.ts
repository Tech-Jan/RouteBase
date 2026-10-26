import { beforeEach, describe, expect, it, vi } from 'vitest';
import { initRaceWaveEditor } from './race-waves';
import type { RaceParticipantDto } from './race-api';

/**
 * The wave editor's WIRING (the pure helpers are covered in race-waves.test.ts, which runs without a DOM).
 *
 * This exists because the editor is re-initialised on every payload read, and its save button lives
 * OUTSIDE the rows it re-renders — so it survives, listeners and all. That was harmless while the payload
 * was read exactly once per page; giving the replay view a reload() made a second call reachable, and a
 * stacked listener means one click PUTs the offsets twice.
 */
function rider(wave: string | null): RaceParticipantDto {
	return {
		participantId: 1, kind: 'simulated', name: 'A', bib: null,
		category: null, gender: null, club: null, color: null, emphasised: false, calloutOff: false,
		officialFinishSeconds: null, finishStatus: null, finishStatusRaw: null,
		startOffsetSeconds: null, wave, discipline: null, seed: 0, splits: [], track: null,
	};
}

function mount(): void {
	document.body.innerHTML = `
		<section id="raceWaves">
			<div id="raceWaveRows"></div>
			<button id="raceWaveSave">Save start times</button>
			<p id="raceWaveStatus"></p>
		</section>`;
}

function clickSave(): void {
	document.getElementById('raceWaveSave')!.dispatchEvent(new Event('click', { bubbles: true }));
}

describe('the wave editor', () => {
	beforeEach(() => {
		vi.stubGlobal('fetch', vi.fn(async () => new Response(
			JSON.stringify({ updated: 3 }), { status: 200, headers: { 'Content-Type': 'application/json' } })));
		mount();
	});

	it('saves the offsets once per click', async () => {
		initRaceWaveEditor(1, [rider('Startblock 1')], () => {});

		clickSave();

		await vi.waitFor(() => expect(fetch).toHaveBeenCalledTimes(1));
		expect(fetch).toHaveBeenCalledWith('/api/RacesApi/1/waves', expect.objectContaining({ method: 'PUT' }));
	});

	it('re-initialising does NOT stack a second listener on the save button', async () => {
		// One user click must be one PUT, however many times the payload has been re-read. Stacked
		// listeners would also fire onSaved twice, i.e. two replay refreshes for one save.
		const onSaved = vi.fn();
		initRaceWaveEditor(1, [rider('Startblock 1')], onSaved);
		initRaceWaveEditor(1, [rider('Startblock 1')], onSaved);
		initRaceWaveEditor(1, [rider('Startblock 1')], onSaved);

		clickSave();

		await vi.waitFor(() => expect(onSaved).toHaveBeenCalledTimes(1));
		expect(fetch).toHaveBeenCalledTimes(1);
	});

	it('a re-init re-renders the rows from the participants it was given', () => {
		initRaceWaveEditor(1, [rider('Startblock 1')], () => {});
		initRaceWaveEditor(1, [rider('Startblock 1'), rider('Startblock 2')], () => {});

		expect(document.querySelectorAll('.race-wave-offset')).toHaveLength(2);
	});

	it('stays hidden and binds nothing when the field has no waves', () => {
		initRaceWaveEditor(1, [rider(null)], () => {});

		clickSave();

		expect((document.getElementById('raceWaves') as HTMLElement).hidden).toBe(true);
		expect(fetch).not.toHaveBeenCalled();
	});
});
