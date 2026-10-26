// AUTO-GENERATED from the RouteBaseWeb C# const classes — do not make direct changes.
// Regenerate: change the const class, run `dotnet test` and paste what VocabulariesTsGateTests prints.
//
// Named unions + *_VALUES arrays for client code that BUILDS option lists or validates raw
// strings. Wire DTO properties carry the same unions via the [WireValues] enum stamps in
// openapi-types.ts; both derive from the same const classes and cannot drift apart.

// Visibilities.cs
export type Visibility = 'private' | 'friends' | 'public';
export const VISIBILITY_VALUES: readonly Visibility[] = ['private', 'friends', 'public'];

// RecordingSessionStatus.cs
export type RecordingSessionStatus = 'recording' | 'paused' | 'stopped' | 'finalized' | 'finalize_failed' | 'abandoned';
export const RECORDING_SESSION_STATUS_VALUES: readonly RecordingSessionStatus[] = ['recording', 'paused', 'stopped', 'finalized', 'finalize_failed', 'abandoned'];

// ApiErrorCodes.cs
export type ApiErrorCode = 'badRequest' | 'unauthorized' | 'forbidden' | 'notFound' | 'conflict' | 'rateLimited' | 'serverError' | 'serviceUnavailable' | 'validationFailed';
export const API_ERROR_CODE_VALUES: readonly ApiErrorCode[] = ['badRequest', 'unauthorized', 'forbidden', 'notFound', 'conflict', 'rateLimited', 'serverError', 'serviceUnavailable', 'validationFailed'];

// LifecycleStates.cs
export type LifecycleState = 'in-work' | 'released' | 'trashed';
export const LIFECYCLE_STATE_VALUES: readonly LifecycleState[] = ['in-work', 'released', 'trashed'];

// LifecycleReasons.cs
export type LifecycleReason = 'awaiting-review' | 'recording' | 'user' | 'edit-merge' | 'review-rejected' | 'abandoned' | 'auto-approved';
export const LIFECYCLE_REASON_VALUES: readonly LifecycleReason[] = ['awaiting-review', 'recording', 'user', 'edit-merge', 'review-rejected', 'abandoned', 'auto-approved'];

// ShareKindNames.cs
export type ShareKind = 'activity' | 'segment' | 'live' | 'race';
export const SHARE_KIND_VALUES: readonly ShareKind[] = ['activity', 'segment', 'live', 'race'];

// RaceParticipantKind.cs
export type RaceParticipantKind = 'real' | 'simulated';
export const RACE_PARTICIPANT_KIND_VALUES: readonly RaceParticipantKind[] = ['real', 'simulated'];

// ActivitySources.cs
export type ActivitySource = 'auto-recording' | 'live-recording';
export const ACTIVITY_SOURCE_VALUES: readonly ActivitySource[] = ['auto-recording', 'live-recording'];

// HeatmapStatuses.cs
export type HeatmapStatus = 'pending' | 'running' | 'done' | 'failed' | 'stale';
export const HEATMAP_STATUS_VALUES: readonly HeatmapStatus[] = ['pending', 'running', 'done', 'failed', 'stale'];

// ActivityEditSessionStatuses.cs
export type EditSessionStatus = 'active' | 'saved' | 'discarded';
export const EDIT_SESSION_STATUS_VALUES: readonly EditSessionStatus[] = ['active', 'saved', 'discarded'];

// SegmentKinds.cs
export type SegmentKind = 'polyline' | 'shapes';
export const SEGMENT_KIND_VALUES: readonly SegmentKind[] = ['polyline', 'shapes'];

// QueueOperationTypes.cs
export type QueueOperationType = 'idle' | 'analysis' | 'import' | 'trackImport' | 'heatmap' | 'export';
export const QUEUE_OPERATION_TYPE_VALUES: readonly QueueOperationType[] = ['idle', 'analysis', 'import', 'trackImport', 'heatmap', 'export'];

// PhotoLocationSources.cs
export type PhotoLocationSource = 'exif' | 'interpolated' | 'manual' | 'none';
export const PHOTO_LOCATION_SOURCE_VALUES: readonly PhotoLocationSource[] = ['exif', 'interpolated', 'manual', 'none'];

// CostKinds.cs
export type CostKind = 'measured' | 'estimated';
export const COST_KIND_VALUES: readonly CostKind[] = ['measured', 'estimated'];

// SortDirections.cs
export type SortDirection = 'asc' | 'desc';
export const SORT_DIRECTION_VALUES: readonly SortDirection[] = ['asc', 'desc'];

// ChartDetailLevels.cs
export type ChartDetailLevel = 'auto' | 'low' | 'normal' | 'high' | 'full';
export const CHART_DETAIL_LEVEL_VALUES: readonly ChartDetailLevel[] = ['auto', 'low', 'normal', 'high', 'full'];

