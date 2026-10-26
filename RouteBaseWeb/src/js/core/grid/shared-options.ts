/**
 * "Is this published?" as multiselect filter options. Shared by every grid that filters on it
 * (dashboard feed, segments list, races list), the same way visibility-options.ts is shared.
 *
 * Three states, because a share link ending does not un-publish the past: 'shared' is a live link,
 * 'wasshared' is one that was revoked or expired (its view count survives on the card, dimmed), and
 * 'notshared' was never published at all.
 *
 * The wire values live ONLY here, and both sides of the match go through this file: the row's flags
 * become a value via {@link sharedFilterValue}, and the filter offers the same strings. A filter that
 * silently matches nothing is the failure mode if those two ever drift apart.
 */

import type { FilterOption } from './grid-types';

export const SHARED_FILTER_OPTIONS: FilterOption[] = [
	{ value: 'shared', label: 'Shared' },
	{ value: 'wasshared', label: 'Previously shared' },
	{ value: 'notshared', label: 'Not shared' },
];

/** Row flags → the filter's wire value. `isShared` wins: the two are mutually exclusive server-side, and
 * a row that is live now is "Shared" whatever its history. */
export const sharedFilterValue = (isShared: boolean, wasShared: boolean): string =>
	isShared ? 'shared' : wasShared ? 'wasshared' : 'notshared';

/** The Shared column's cell text — blank for never-shared, so the column stays quiet on most rows. */
export const sharedCellText = (isShared: boolean, wasShared: boolean): string =>
	isShared ? 'Shared' : wasShared ? 'Previously shared' : '';
