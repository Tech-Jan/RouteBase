/**
 * Activity Details page entry — single ESM bundle for the Details view.
 * Reads its config from the #activityDetailsConfig JSON tag (asp-append-version handles cache-busting),
 * replacing the old inline module + hand-rolled SHA256 cache-buster.
 */
import { initializeActivityDetails, ActivityDetailsConfig } from './activity-details-init';
import { setupMapControls } from './activity-map-controls';
import { setupSplitsInteraction } from './activity-splits';
import { setupCustomSegments } from './activity-segments';
import { setupReplayControls } from './activity-replay-ui';
import { setupActivityPhotos } from './activity-photos';
import { setupActivityShare } from './activity-share';
import { wireTrackEditEntry } from '../track-edit/edit-mode-entry';
import { setupReviewBanner } from './review-banner';
import { expandGpsTrack } from '../core/gps-data';
import { readIsland } from '../core/page-bootstrap';
import type { GpsTrackWire } from '../core/core-types';

/** Raw shape of the #activityDetailsConfig JSON island (Details.cshtml.cs) — gpsTrack is
 * columnar, expanded to ActivityDetailsConfig.gpsData (GpsPoint[]) before use. */
type ActivityDetailsConfigWire = Omit<ActivityDetailsConfig, 'gpsData'> & { gpsTrack: GpsTrackWire };

// DC-09: guard the config island — a missing/malformed #activityDetailsConfig would
// otherwise throw at import and silently disable every page interaction. Server-rendered
// static content stays; only the interactive layer is skipped.
function bootDetails(config: ActivityDetailsConfig): void {
	initializeActivityDetails(config);
	setupMapControls();
	setupSplitsInteraction(config.gpsData);
	// Explicit, not left to setupCustomSegments' own markup guard: a share view must make no
	// /api/SegmentsApi call at all, and resting that on a defensive bail would let a future markup
	// change silently re-enable it (and log a warning on every share page load).
	if (config.canCreateSegments) setupCustomSegments(config.activityId, config.maxDistance);
	setupReplayControls(config);
	setupActivityPhotos(config.activityId, config.isOwner, config.photosApiBase);
	// Self-gating: the dialog markup renders only for the owner, so this is a no-op elsewhere.
	setupActivityShare(config.activityId);
	wireTrackEditEntry(config); // dynamic-imports the editor chunk on demand (D-U8)
}

// Outside bootDetails on purpose: approving or restoring a ride must stay possible even if the GPS config
// island is broken — the banner reads nothing from it, and self-gates on its own markup.
setupReviewBanner();

// readIsland guards the parse itself (an empty island used to throw here — DC-09); the try/catch that
// remains guards only the columnar gpsTrack → GpsPoint[] expansion, which can still throw on
// well-formed-JSON-but-malformed track data.
const wire = readIsland<ActivityDetailsConfigWire | null>('activityDetailsConfig', null);
let config: ActivityDetailsConfig | null = null;
try {
	config = wire ? { ...wire, gpsData: expandGpsTrack(wire.gpsTrack, wire.tzOffsetHours) } : null;
} catch (err) {
	console.error('[details] #activityDetailsConfig gpsTrack expansion failed — page interactions disabled', err);
}

if (config) bootDetails(config);
else console.error('[details] missing/invalid #activityDetailsConfig — page interactions disabled');
