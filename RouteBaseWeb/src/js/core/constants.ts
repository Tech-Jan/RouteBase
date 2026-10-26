/**
 * Shared numeric constants used across multiple modules.
 *
 * Rules:
 *   - Only add a constant here if it is used in TWO OR MORE files with the
 *     same semantic meaning. File-local magic numbers stay local.
 *   - All constants are read-only once set.
 *
 * Loaded via core/core.ts (ES module entry in _Layout.cshtml).
 *
 * Consumed by:
 *   - geo.ts                       (EARTH_RADIUS_M)
 *   - map-utils-maplibre.ts (MAP_THROTTLE_MS)
 *   - segment-stats-map-hover.ts   (MAP_THROTTLE_MS)
 *   - chart-factory.ts             (CHART_HOVER_THROTTLE_MS)
 *   - segment-stats-charts.ts      (CHART_HOVER_THROTTLE_MS)
 */

/** Earth radius in metres — used for Haversine distance calculations. */
export const EARTH_RADIUS_M = 6371000;

/**
 * Maximum framerate for map mousemove handlers (~30 fps).
 * Prevents excessive nearest-point lookups on every mouse pixel.
 */
export const MAP_THROTTLE_MS = 32;

/**
 * Maximum framerate for chart hover handlers (~30 fps).
 * 30 fps is visually smooth for crosshair tracking while halving the number
 * of chart.draw() calls compared to 60 fps — each draw() repaints the full
 * canvas including the verticalLine plugin and tooltip overlay.
 */
export const CHART_HOVER_THROTTLE_MS = 32;
