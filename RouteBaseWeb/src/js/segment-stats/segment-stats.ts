/**
 * Segment Statistics — ESM Entry Point
 *
 * Imports all leaf modules which transitively pull in the full dependency graph:
 *   hover → segment-statistics
 *   map   → map-maplibre, map-data, map-hover, progressive → segment-statistics
 *   charts, charts-progress → segment-statistics
 *   replay-ui → replay → replay-efforts, replay-tracks, replay-markers, replay-perf
 *
 * A single <script type="module" src="segment-stats.js"> replaces the 16 classic
 * script tags that were previously required in Statistics.cshtml.
 */
import './segment-stats-hover';
import './segment-stats-map';
import './segment-stats-charts';
import './segment-stats-charts-progress';
import './segment-stats-charts-distribution';
import './segment-stats-replay-ui';

import { ctx } from './segment-statistics';
import { setupShareLinks } from '../core/share-links';

// Owner-only share dialog. The markup renders only under Surfaces.CanShare and setupShareLinks no-ops
// when it is absent, so this is safe to call unconditionally — but it is gated anyway, so a share view
// does not even load the wiring for a dialog it can never show.
if (ctx?.config?.canManageSegment && ctx.config.segmentId) {
	setupShareLinks({ kind: 'segment', resourceId: ctx.config.segmentId });
}
