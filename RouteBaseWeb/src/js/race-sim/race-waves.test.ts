// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { formatOffset, parseOffset, renderWaveRows, wavesOf } from './race-waves';
import type { RaceParticipantDto } from './race-api';

function rider(over: Partial<RaceParticipantDto> = {}): RaceParticipantDto {
	return {
		participantId: 1, kind: 'simulated', name: 'A', bib: null,
		category: null, gender: null, club: null, color: null, emphasised: false, calloutOff: false,
		officialFinishSeconds: null, finishStatus: null, finishStatusRaw: null,
		startOffsetSeconds: null, wave: null, discipline: null, seed: 0, splits: [], track: null,
		...over,
	};
}

describe('wavesOf', () => {
	it('finds the distinct blocks and counts them', () => {
		const waves = wavesOf([
			rider({ wave: 'Startblock 1' }),
			rider({ wave: 'Startblock 2' }),
			rider({ wave: 'Startblock 1' }),
		]);

		expect(waves.map((w) => w.wave)).toEqual(['Startblock 1', 'Startblock 2']);
		expect(waves.map((w) => w.count)).toEqual([2, 1]);
	});

	/**
	 * FIRST-SEEN order, not alphabetical. Results files list blocks in start order, and sorting would put
	 * "Startblock 10" before "Startblock 2" — an editor whose rows are in an order the user has to fight.
	 */
	it('keeps the file\'s own order', () => {
		const waves = wavesOf([
			rider({ wave: 'Startblock 9' }),
			rider({ wave: 'Startblock 10' }),
			rider({ wave: 'Startblock 2' }),
		]);

		expect(waves.map((w) => w.wave)).toEqual(['Startblock 9', 'Startblock 10', 'Startblock 2']);
	});

	it('reports the offset the block already carries', () => {
		const waves = wavesOf([
			rider({ wave: 'A', startOffsetSeconds: 3600 }),
			rider({ wave: 'A', startOffsetSeconds: 3600 }),
		]);

		expect(waves[0]?.offsetSeconds).toBe(3600);
	});

	it('reports NULL when a block\'s riders disagree, rather than one of their numbers', () => {
		// Only a partial write produces this. Showing one rider's offset as the block's would invite the
		// user to "confirm" a value that is not true of the others.
		const waves = wavesOf([
			rider({ wave: 'A', startOffsetSeconds: 3600 }),
			rider({ wave: 'A', startOffsetSeconds: 7200 }),
		]);

		expect(waves[0]?.offsetSeconds).toBeNull();
	});

	it('ignores riders with no wave at all — a mass start has nothing to edit', () => {
		expect(wavesOf([rider(), rider({ wave: '   ' })])).toEqual([]);
	});
});

describe('the offset field round-trips', () => {
	it('renders seconds as h:mm:ss', () => {
		expect(formatOffset(0)).toBe('0:00:00');
		expect(formatOffset(3600)).toBe('1:00:00');
		expect(formatOffset(4 * 3600 + 30 * 60 + 5)).toBe('4:30:05');
	});

	it('renders nothing for a block with no offset', () => {
		expect(formatOffset(null)).toBe('');
		expect(formatOffset(Number.NaN)).toBe('');
		expect(formatOffset(-1)).toBe('');
	});

	it('reads back exactly what it wrote', () => {
		for (const seconds of [0, 60, 3600, 12_345]) {
			expect(parseOffset(formatOffset(seconds))).toBe(seconds);
		}
	});

	it('accepts the shorter forms a person actually types', () => {
		expect(parseOffset('90')).toBe(90);
		expect(parseOffset('1:30')).toBe(90);
		expect(parseOffset(' 2:00:00 ')).toBe(7200);
	});

	it('reads blank as "clear this block back to the gun"', () => {
		expect(parseOffset('')).toBeNull();
		expect(parseOffset('   ')).toBeNull();
	});

	/**
	 * `undefined` is the third answer, and it matters: the editor leaves an unreadable field OUT of the
	 * request rather than sending 0, which would move a whole block to the gun on a typo.
	 */
	it('reads nonsense as "leave this block alone"', () => {
		expect(parseOffset('soon')).toBeUndefined();
		expect(parseOffset('-5')).toBeUndefined();
		expect(parseOffset('1:2:3:4')).toBeUndefined();
	});
});

describe('renderWaveRows', () => {
	it('escapes the block label — it is text the results file wrote', () => {
		const html = renderWaveRows([
			{ wave: '<img src=x onerror=alert(1)>', count: 3, offsetSeconds: null },
		]);

		expect(html).not.toContain('<img');
		expect(html).toContain('&lt;img');
	});

	it('carries the label on the input, so the save knows which block it edited', () => {
		const html = renderWaveRows([{ wave: 'Startblock 1', count: 240, offsetSeconds: 3600 }]);

		expect(html).toContain('data-wave="Startblock 1"');
		expect(html).toContain('value="1:00:00"');
		expect(html).toContain('240 rider(s)');
	});
});
