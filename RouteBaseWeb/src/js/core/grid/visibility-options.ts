/**
 * The activity/segment/race visibility values as multiselect filter options. Shared by every grid
 * that filters on visibility (activities table, dashboard feed, statistics header). Values come
 * from the generated vocabulary (D4) — a server-side rename or addition changes this list via the
 * VocabulariesTsGateTests regen, never by hand.
 */

import type { FilterOption } from './grid-types';
import { VISIBILITY_VALUES, type Visibility } from '../../generated/vocabularies';

const LABELS: Record<Visibility, string> = {
	private: 'Private',
	friends: 'Friends',
	public: 'Public',
};

export const VISIBILITY_FILTER_OPTIONS: FilterOption[] = VISIBILITY_VALUES.map((v) => ({
	value: v,
	label: LABELS[v],
}));
