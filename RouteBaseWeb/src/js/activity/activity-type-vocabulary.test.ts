// @vitest-environment node
import { describe, it, expect } from 'vitest';
import { RECORDABLE_ACTIVITY_TYPES } from './activity-type-vocabulary';

// Frozen-literal mirror pin: vitest can't read the C# source, so this asserts the TS array
// matches the expected values verbatim. Must be updated in lockstep with
// RouteBaseWeb.Tests/Helpers/ActivityTypeVocabularyTests.cs's Recordable_MatchesAndroidStringsXml
// if the vocabulary ever changes.

describe('RECORDABLE_ACTIVITY_TYPES', () => {
	it('matches the ActivityTypeVocabulary.cs Recordable list, in order', () => {
		expect(RECORDABLE_ACTIVITY_TYPES).toEqual(['Run', 'Ride', 'Walk', 'Hike', 'AlpineSki', 'NordicSki', 'Other']);
	});
});
