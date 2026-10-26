// Kept as the Activity Details page's entry point. The implementation moved to core/share-links.ts when
// segment sharing landed and the dialog stopped being activity-specific; this shim means
// activity-details-page.ts (and anything else importing it) did not have to change.

import { setupShareLinks } from "../core/share-links";

export function setupActivityShare(activityId: number): void {
	setupShareLinks({ kind: "activity", resourceId: activityId });
}
