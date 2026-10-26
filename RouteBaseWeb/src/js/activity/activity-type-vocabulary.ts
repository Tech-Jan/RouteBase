/**
 * Mirrors RouteBaseWeb/Helpers/ActivityTypeVocabulary.cs `Recordable` (cross-repo contract
 * with GpsRecorder's res/values/strings.xml `activity_types`). Keep all three in lockstep.
 */
export const RECORDABLE_ACTIVITY_TYPES = ['Run', 'Ride', 'Walk', 'Hike', 'AlpineSki', 'NordicSki', 'Other'] as const;
export type RecordableActivityType = (typeof RECORDABLE_ACTIVITY_TYPES)[number];
