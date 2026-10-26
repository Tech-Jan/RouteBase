import { describe, it, expect } from 'vitest';
import { buildColumns, type EffortTableCaps } from './segment-stats-efforts-table';
import type { EffortRow } from './segment-stats-effort-types';
import { must } from '../core/invariant';

/** Everything a signed-in owner gets. */
const OWNER: EffortTableCaps = {
	canExcludeEfforts: true,
	showRiderIdentity: true,
	canLinkToActivities: true,
};

/** Everything an anonymous share viewer gets: nothing. */
const SHARE: EffortTableCaps = {
	canExcludeEfforts: false,
	showRiderIdentity: false,
	canLinkToActivities: false,
};

const ROW = {
	effortIndex: 0,
	activityId: 4242,
	activityName: 'Morning ride',
	activityType: 'Ride',
	userDisplayName: 'Someone Else',
} as unknown as EffortRow;

function cell(key: string, caps: EffortTableCaps, hasForeignRiders = false): string {
	const col = buildColumns(hasForeignRiders, [], caps).find((c) => c.key === key);
	expect(col, `column "${key}" should exist`).toBeDefined();
	return col!.formatter!(ROW) as string;
}

// SS-01: the efforts-grid removal column is owner-only (server DELETE gates on OwnedSegments, so a
// non-owner marking rows only yields a failed batch + pointless reload).
describe('buildColumns — SS-01 exclusion gate', () => {
	it('omits the actions column when the route cannot exclude efforts', () => {
		const cols = buildColumns(false, [], SHARE);
		expect(cols.some((c) => c.key === 'actions')).toBe(false);
		expect(cols.some((c) => c.key === 'excluded')).toBe(false);
	});

	it('appends the actions column last when it can', () => {
		const cols = buildColumns(false, [], OWNER);
		expect(cols.some((c) => c.key === 'actions')).toBe(true);
		expect(must(cols.at(-1), 'non-empty columns').key).toBe('actions');
	});
});

// These controls are built CLIENT-side, so no server-side render guard can see them. This describe block
// is the ONLY thing pinning that a share view does not leak rider identity or app links through the grid.
describe('buildColumns — share-view capabilities', () => {
	it('omits the Rider column when identity is off, EVEN WITH foreign riders present', () => {
		// The dangerous case: hasForeignRiders is derived from the data, so a data-only gate would put
		// other riders' display names on an anonymous page.
		const cols = buildColumns(true, [], SHARE);
		expect(cols.some((c) => c.key === 'rider')).toBe(false);
	});

	it('keeps the Rider column when identity is on and foreign riders exist', () => {
		const cols = buildColumns(true, [], OWNER);
		expect(cols.some((c) => c.key === 'rider')).toBe(true);
	});

	it('renders the activity cell as plain text — no href — when linking is off', () => {
		// A share view must render zero hrefs: a link to /Activities/Details is a dead drop into login.
		const html = cell('activity', SHARE);
		expect(html).not.toContain('href');
		expect(html).not.toContain('<a ');
		expect(html).toContain('Morning ride');
	});

	it('renders the activity cell as a link when linking is on', () => {
		const html = cell('activity', OWNER);
		expect(html).toContain('href="/Activities/Details/4242"');
	});
});
