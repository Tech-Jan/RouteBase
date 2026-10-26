/**
 * Vite config — ES Module output
 *
 * Builds the ES module files — those that use `export function` and are loaded
 * via `<script type="module">` in Details.cshtml and Statistics.cshtml.
 *
 * These files can import types from src/js/types/ (type-only imports are stripped
 * at build time). They must NOT import runtime values from IIFE files — cross-module
 * communication with IIFE files is via window.* globals only.
 *
 * Phase 4: activity ES module files.
 * (Extend this list if Statistics.cshtml gains ES module files in future phases.)
 */

import { defineConfig, type Plugin } from 'vite';
import { resolve, join } from 'path';
import { existsSync, rmSync } from 'fs';

const root = __dirname;

function entry(name: string, srcRel: string): Record<string, string> {
    const abs = join(root, srcRel);
    return existsSync(abs) ? { [name]: abs } : {};
}

/**
 * Wipe wwwroot/js/chunks before every build. Needed because chunk names now carry a content hash: without
 * this, every edit leaves its old hashed chunk behind forever. Safe to wipe wholesale — unlike wwwroot/js
 * (which holds hand-placed static assets, hence emptyOutDir:false), chunks/ is 100% generated.
 */
function cleanChunksDir(): Plugin {
    return {
        name: 'clean-chunks-dir',
        buildStart() {
            rmSync(resolve(root, 'wwwroot/js/chunks'), { recursive: true, force: true });
        },
    };
}

// Exported so vite-entry-manifest.test.ts can pin the exact entry set. A wrong `srcRel` makes
// entry() drop that entry silently (existsSync guard, above) — the page then 404s its script with
// every build+lint gate still green. The manifest test is the ONLY automated guard on that trap.
export const esmEntries: Record<string, string> = {
    // Phase 8 Wave 7 — core modules (loaded globally via _Layout.cshtml)
    ...entry('core/core',      'src/js/core/core.ts'),
    // Shared background-operation progress toast. A core module (its importer set is app-wide, not
    // page-specific; moved out of pages/). Loaded on every dashboard page via
    // _DashboardLayout.cshtml — output name and source path both live in core/.
    ...entry('core/op-toast',  'src/js/core/op-toast.ts'),

    // Phase 8 Wave 1 — leaf pages (no cross-page dependencies)
    ...entry('pages/profile-settings', 'src/js/pages/profile-settings.ts'),
    // Phase 8 Wave 2 — Live Recording / LiveView pages (sub-modules are imported chunks)
    ...entry('live-recording/live-recording',   'src/js/live-recording/live-recording.ts'),
    // Public (anonymous) live-recording map for /s/{token} share links — polls, no SignalR.
    ...entry('live-recording/share-live-view',  'src/js/live-recording/share-live-view.ts'),
    ...entry('live-recording/record-review',    'src/js/live-recording/record-review.ts'),
    ...entry('dashboard/dashboard',    'src/js/dashboard/dashboard.ts'), // imports dashboard-grid.ts
    ...entry('pages/statistics',       'src/js/pages/statistics.ts'),
    ...entry('dashboard/dashboard-nav', 'src/js/dashboard/dashboard-nav.ts'),
    ...entry('pages/segments-index',   'src/js/pages/segments-index.ts'),
    // /Sharing — every live share link the owner has, in one list.
    ...entry('pages/sharing-inventory', 'src/js/pages/sharing-inventory.ts'),
    ...entry('pages/keys-and-data',      'src/js/pages/keys-and-data.ts'),
    ...entry('segment-create/segment-create', 'src/js/segment-create/segment-create.ts'),
    ...entry('pages/friends',          'src/js/pages/friends.ts'),
    ...entry('pages/multiday',         'src/js/pages/multiday.ts'),
    ...entry('import/import',          'src/js/import/import.ts'),
    ...entry('photos/photos',          'src/js/photos/photos.ts'),
    ...entry('trash/trash',            'src/js/trash/trash.ts'),
    ...entry('race-sim/races',         'src/js/race-sim/races.ts'),
    ...entry('race-sim/races-grid',    'src/js/race-sim/races-grid.ts'),
    ...entry('race-sim/race-replay',   'src/js/race-sim/race-replay.ts'),
    // Public (anonymous) race replay for /s/{token} share links — the owner page's view modules at
    // editable:false, over the redacted payload.
    ...entry('race-sim/share-race-view', 'src/js/race-sim/share-race-view.ts'),

    // Phase 8 Wave 4 — Replay shared modules (ESM).
    // camera-controls + touch-gesture-handler are now chunk dependencies (ESM-imported by the
    // replay chains: segment-stats-replay + activity-replay), no longer separate entries/script tags.
    ...entry('activity/activity-actions',       'src/js/activity/activity-actions.ts'),

    // Phase 8 Wave 6 — Segment Statistics page (single ESM entry, replaces 16 IIFE scripts)
    ...entry('segment-stats/segment-stats',                        'src/js/segment-stats/segment-stats.ts'),
    ...entry('segment-stats/segment-stats-efforts-table',          'src/js/segment-stats/segment-stats-efforts-table.ts'),

    // Phase 8 Wave 5 — Activity Details page shared module.
    // maplibre-terrain/-helpers/-track/-state are imported chunks now (ID-08): the Details,
    // Segment-Stats and Overview entries import them directly, sharing one instance per page.
    ...entry('activity/activity-edit',                           'src/js/activity/activity-edit.ts'),

    // Activity Details page single entry — bundles init/map-controls/splits/segments/replay-ui as chunks.
    ...entry('activity/activity-details-page',  'src/js/activity/activity-details-page.ts'),
    ...entry('activity/version-history',        'src/js/activity/version-history.ts'),

    // Phase 8 Wave 3 — Activities overview page
    // (activities-overview + -progressive + -maplibre are imported chunks now; page-init/grid/
    // shape-filter are the entries that pull activities-overview, sharing one instance per page)
    ...entry('overview/activities-page-init',   'src/js/overview/activities-page-init.ts'),
    ...entry('overview/activities-grid',        'src/js/overview/activities-grid.ts'),
    ...entry('overview/segments-grid',          'src/js/overview/segments-grid.ts'),

    // Shape-filter overlay (paint rectangles → shape-defined segment). Sibling modules
    // (shape-model/store/adapter/layer/interactions/service/panel) are bundled as chunks.
    ...entry('overview/shape-filter',           'src/js/overview/shape-filter/shape-filter.ts'),

    // TrackEditor entry picker (A5) — Activities-page "Open in editor" + ?pickFor picker overlay.
    // Own entry (shape-filter precedent); imports activities-overview as the shared chunk (one instance).
    ...entry('track-edit/entry-picker',         'src/js/track-edit/entry-picker.ts'),
};

export default defineConfig({
    // outDir is wwwroot/js, which the app serves at /js/ — so THAT is this bundle's base, not the site root.
    // Static chunk imports are relative and never cared, but Vite resolves a dynamic import's modulepreload
    // list against `base`: with the default '/', every lazy chunk got preloaded from /chunks/… and 404'd
    // (harmless — the real import used the relative path — but it filled the console with red).
    base: '/js/',
    plugins: [cleanChunksDir()],
    build: {
        outDir:      resolve(root, 'wwwroot/js'),
        emptyOutDir: false,   // Never wipe wwwroot/js — IIFE output + static assets also live there
        sourcemap:   true,
        // F5 (FrontendBaseline): minify on. sourcemap stays true so file:line still resolves in
        // devtools / stack traces; entry names stay stable (asp-append-version cache-busts them).
        minify:      true,
        rollupOptions: {
            input: esmEntries,
            // Preserve exported functions — consumers are external HTML <script type="module">
            // tags, not bundled imports. Without this, Rollup tree-shakes away all exports.
            preserveEntrySignatures: 'exports-only',
            output: {
                format:         'es',
                // Entries: no hash. The .cshtml <script> tags name them literally and cache-bust with
                // asp-append-version="true" (a ?v= query over the file's content).
                entryFileNames: '[name].js',
                // Chunks: hash REQUIRED. Nothing in a .cshtml names a chunk — the entry imports it by path, so
                // asp-append-version can never reach it. With a stable name a browser keeps serving the OLD
                // chunk against a NEW entry, and because minified exports are single letters (`E`, `v`), the
                // entry's `import { E }` silently binds to whatever `E` meant in the stale file. That is not a
                // cache miss, it is a CROSS-WIRE: it cost us a live bug where listVersions() ran moveCursor()
                // and POSTed to /api/edit-sessions/<trackId>/cursor. Content in the name = impossible by
                // construction.
                chunkFileNames: 'chunks/[name]-[hash].js',
                globals: {
                    'maplibre-gl': 'maplibregl',
                    'chart.js':   'Chart',
                },
            },
            external: ['maplibre-gl', 'chart.js'],
        },
    },
});
