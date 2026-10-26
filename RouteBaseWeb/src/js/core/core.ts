/**
 * core/core.ts — Core module barrel entry point + core chart-kernel index.
 *
 * Loaded as <script type="module"> by all three layouts, BEFORE their RenderSectionAsync("Scripts") —
 * module scripts execute in document order, so this file runs before every page entry, including
 * segment-statistics.ts's import-time ctx.
 *
 * It has TWO side effects, and that position is why the first one is here: initPageSettings() fills the
 * core/account-settings seam from the #appSettings island, before any page entry runs. Nothing imports
 * this module any more (the last consumer, activity/activity-details.ts, was repointed at
 * chart-panel-manager), so it is a boot entry rather than a barrel — which is what makes a side effect
 * here legitimate under the no-barrel-side-effects rule.
 *
 * The core chart kernel every page's charts share — import these directly, don't re-derive them
 * (the chart-kernel pile single-sourced the cross-cutting concerns here so a fix lands once):
 *   chart-factory         — the GPS-distance line chart (Details); the ONLY createGpsLineChart()
 *   chart-zoom-pan        — xZoomConfig + finalizeChart (X-zoom/pan/pinch + Y-freeze) for ALL charts
 *   chart-gesture-preview — bitmap gesture preview for x-synced chart groups
 *   chart-overlay         — overlay-canvas crosshair/highlight + THE strokeCrosshairLine kernel
 *   chart-panel-manager   — tab show/hide + afterLayout resize
 *   after-layout          — the double-rAF "wait for layout" primitive (replaces magic setTimeouts)
 *   chart-empty-state     — the one "no data" contract (a visible message, never a blank canvas)
 *   chart-error-log       — the one logged failure path (throttled; replaces silent catch {})
 *   chart-robust-scale    — outlier-tolerant P1..P99 axis bounds (generic; was in segment-stats/)
 */

import { initPageSettings } from './account-settings';
import { sweepLegacyDevicePrefs } from './device-prefs';

initPageSettings();

// Second side effect: take out the pre-registry device-pref keys, once per device. Self-limiting — it
// records a sentinel, so every load after the first is one read. Not a migration: decision 4 is a reset,
// and re-importing an old collapse state is not worth a line of code.
sweepLegacyDevicePrefs();
