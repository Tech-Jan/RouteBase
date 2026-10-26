/**
 * Activity slice contracts — DTOs consumed by the activity details page.
 *
 * Formerly ambient globals (GodFileSplit).
 */

import type { SegmentSummaryDto } from '../generated/contracts';

// The efforts-detailed wire (SegmentEffortSummaryDto) and the created-segment wire
// (CreatedSegmentResponse) are consumed straight from ../generated/contracts — no hand mirrors.

// ---------------------------------------------------------------------------
// Segment summary — from GET /api/SegmentsApi?activityId= (activity page)
// ---------------------------------------------------------------------------

// Partial view of the full generated SegmentSummaryDto — activity-segments.ts reads only these three
// fields. Pick over the generated type so a rename/removal in the C# DTO breaks HERE (drift caught at
// tsc), instead of silently. This is the pattern for an intentional narrow view of a wider server DTO.
export type ActivitySegmentSummary = Pick<SegmentSummaryDto, 'segmentId' | 'segmentName' | 'segmentDistance'>;
