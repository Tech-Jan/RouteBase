// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { InMemoryGridDataSource } from '../core/grid/grid-data-source';
import type { GridColumn } from '../core/grid/grid-types';
import {
	RACE_FINISH_STATUS,
	finishStatusCell,
	finishStatusOptions,
	finishStatusShort,
	specOf,
	statusOf,
	type RaceFinishStatus,
} from './race-finish-status';
import type { RaceParticipantDto } from './race-api';

interface Row { readonly id: number; readonly status: RaceFinishStatus }

const ROWS: Row[] = [
	{ id: 1, status: 'finished' },
	{ id: 2, status: 'dnf' },
	{ id: 3, status: 'finished' },
	{ id: 4, status: 'dsq' },
];

const COLUMNS: GridColumn<Row>[] = [{
	key: 'status', header: 'Status',
	filter: { type: 'multiselect', options: finishStatusOptions(ROWS.map((r) => r.status)) },
	getValue: (r) => r.status,
	formatter: (r) => finishStatusCell(r.status),
}];

const matching = async (selected: string[]): Promise<number[]> => {
	const source = new InMemoryGridDataSource<Row>(ROWS, { getId: (r) => r.id, columns: COLUMNS });
	const sort = { column: 'status', direction: 'asc' } as const;
	const res = await source.fetch({ sort, sorts: [sort], filters: { status: { selected } }, page: 1, pageSize: 100 });
	return res.filteredIds;
};

function participant(over: Partial<RaceParticipantDto> = {}): RaceParticipantDto {
	return {
		participantId: 1, kind: 'simulated', name: 'KURZ Felix', bib: '4496',
		category: null, gender: null, club: null, color: null, emphasised: false, calloutOff: false,
		officialFinishSeconds: null, finishStatus: null, finishStatusRaw: null, startOffsetSeconds: null, wave: null, discipline: null,
		seed: 1, splits: [], track: null,
		...over,
	};
}

describe('race finish status', () => {
	// The failure mode this file exists to prevent: a value the rows emit that the filter never offers
	// (or vice versa) makes a control that silently matches nothing. Both sides come from here.
	it('offers exactly the values the rows can emit — no more, no fewer', () => {
		const offered = finishStatusOptions(ROWS.map((r) => r.status)).map((o) => o.value);

		expect([...offered].sort()).toEqual([...new Set(ROWS.map((r) => r.status))].sort());
	});

	it('does not offer a status this race has never seen', () => {
		// Ten values in the vocabulary; a road race publishes three of them. A "Missing punch" option on
		// that race is the same dead control, seen from the other side.
		const offered = finishStatusOptions(['finished', 'dnf']).map((o) => o.value);

		expect(offered).toEqual(['finished', 'dnf']);
		expect(offered).not.toContain('missingPunch');
	});

	it('offers them in vocabulary order, not in the order the rows happened to arrive', () => {
		expect(finishStatusOptions(['dns', 'finished', 'dnf']).map((o) => o.value))
			.toEqual(['finished', 'dnf', 'dns']);
	});

	it('narrows through the real grid data source', async () => {
		expect(await matching(['dnf'])).toEqual([2]);
		expect(await matching(['finished'])).toEqual([1, 3]);
		expect(await matching(['finished', 'dsq'])).toEqual([1, 3, 4]);
		expect(await matching([])).toEqual([1, 2, 3, 4]);
	});
});

describe('statusOf', () => {
	/**
	 * THE inversion the boolean could not express. Black Forest bibs 2423, 2843, 2713 and 2569 are
	 * published DNF and carry a `cp_ziel` finish-mat time. Deriving from the splits called all four
	 * finishers; the file is the authority.
	 */
	it('lets a published status beat a finish split', () => {
		const p = participant({ finishStatus: 'dnf', splits: [{ checkpointId: 4, elapsedSeconds: 9798 }] });

		expect(statusOf(p, true)).toBe('dnf');
	});

	it('falls back to the derived rule when the file published nothing', () => {
		expect(statusOf(participant(), true)).toBe('finished');
		expect(statusOf(participant(), false)).toBe('dnf');
	});

	it('keeps a published FINISH on a rider whose splits have a hole', () => {
		// The other direction: the mat missed him, the results did not.
		expect(statusOf(participant({ finishStatus: 'finished' }), false)).toBe('finished');
	});
});

describe('the status table', () => {
	const every = Object.keys(RACE_FINISH_STATUS) as RaceFinishStatus[];

	it('covers the whole generated vocabulary', () => {
		// The Record<RaceFinishStatus, …> makes this a compile-time guarantee; the count is here so that
		// widening the C# vocabulary shows up as a failing NUMBER rather than only as a tsc error.
		expect(every).toHaveLength(10);
	});

	it('puts exactly one status off the course — a DNS is on the start list, not on the road', () => {
		expect(every.filter((s) => !specOf(s).onCourse)).toEqual(['dns']);
	});

	it('separates "did not reach the line" from "reached it but does not count"', () => {
		// The distinction every serious standard makes, and the reason a boolean was never enough.
		// A DSQ has a full set of splits and no place; a DNF has neither.
		expect(specOf('dsq').onCourse).toBe(true);
		expect(specOf('dsq').ranks).toBe(false);
		expect(specOf('notCompeting').onCourse).toBe(true);
		expect(specOf('notCompeting').ranks).toBe(false);
		expect(specOf('dnf').ranks).toBe(false);
	});

	it('ranks the three statuses that count, and unknown on the derived fallback', () => {
		expect(every.filter((s) => specOf(s).ranks).sort())
			.toEqual(['finished', 'moved', 'provisional', 'unknown']);
	});

	it('keeps every sidebar label inside the three characters that slot allows', () => {
		for (const s of every) expect(finishStatusShort(s).length).toBeLessThanOrEqual(3);
	});
});

describe('finishStatusCell', () => {
	it('stays quiet for a finisher — 864 badges read as noise, 41 read as data', () => {
		expect(finishStatusCell('finished')).toBe('');
	});

	it('badges the statuses that need explaining', () => {
		expect(finishStatusCell('dnf')).toContain('is-dnf');
		expect(finishStatusCell('dnf')).toContain('DNF');
		expect(finishStatusCell('dns')).toContain('is-dns');
		expect(finishStatusCell('dsq')).toContain('is-dsq');
	});

	/**
	 * Schauinsland publishes the merged token `DNS/DNF`, which normalizes to `unknown`. Showing "Unknown"
	 * would be discarding the one piece of information the file actually gave us — which is the whole
	 * reason finish_status_raw is a column.
	 */
	it('shows the file\'s OWN token for an unrecognised status, not a label we invented', () => {
		const html = finishStatusCell('unknown', 'DNS/DNF');

		expect(html).toContain('DNS/DNF');
		expect(html).not.toContain('>Unknown<');
	});

	it('escapes the raw token — it comes straight out of the user\'s file', () => {
		const html = finishStatusCell('unknown', '<img src=x onerror=alert(1)>');

		expect(html).not.toContain('<img');
		expect(html).toContain('&lt;img');
	});

	it('falls back to the label when unknown carries no raw token', () => {
		expect(finishStatusCell('unknown', null)).toContain('Unknown');
	});
});
