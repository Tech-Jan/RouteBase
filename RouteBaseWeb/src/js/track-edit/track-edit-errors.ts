/**
 * track-edit-errors.ts — error-code + limit mirror of C# TrackEditErrors / TrackEditLimits
 * (Services/TrackEdit/TrackEditContracts.cs). Codes are grep-able string constants shared by the
 * replay twin (apply-script.ts); the C# engine returns the identical strings (invariant 4/8).
 */

export const trackEditErrors = {
	badSchemaVersion: 'badSchemaVersion',
	unknownOp: 'unknownOp',
	scriptTooLong: 'scriptTooLong',
	sourceMissing: 'sourceMissing',
	duplicateLoad: 'duplicateLoad',
	slotNotFound: 'slotNotFound',
	slotConsumed: 'slotConsumed',
	slotCapExceeded: 'slotCapExceeded',
	rangeOutOfBounds: 'rangeOutOfBounds',
	rangeInverted: 'rangeInverted',
	resultTooSmall: 'resultTooSmall',
	splitIndexOutOfBounds: 'splitIndexOutOfBounds',
	selfAppend: 'selfAppend',
	timeModeInvalid: 'timeModeInvalid',
	gapSecondsMissing: 'gapSecondsMissing',
	gapSecondsOutOfRange: 'gapSecondsOutOfRange',
	timeAnchorMissing: 'timeAnchorMissing',
	valueOutOfRange: 'valueOutOfRange',
} as const;

export type TrackEditErrorCode = (typeof trackEditErrors)[keyof typeof trackEditErrors];

export const TRACK_EDIT_LIMITS = {
	maxSlots: 20,
	maxOps: 500,
	maxGapSeconds: 2_592_000,
	minSlotPoints: 2,
} as const;
