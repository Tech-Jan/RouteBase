// Dragnet inventory config. The entry list mirrors vite.config.esm.ts
// (Razor-loaded chunks; vite-entry-manifest.test.ts pins that set) — knip cannot evaluate the
// TS vite config, so a NEW page entry must be added here too or it reports as a false
// "unused file". Deletions still need the dead-code job's proof bar (grep + build + tests,
// including `await import()` — static sweeps miss those).
import type { KnipConfig } from 'knip';

const config: KnipConfig = {
	entry: [
 "src/js/activity/activity-actions.ts",
 "src/js/activity/activity-details-page.ts",
 "src/js/activity/activity-edit.ts",
 "src/js/activity/version-history.ts",
 "src/js/core/core.ts",
 "src/js/core/op-toast.ts",
 "src/js/dashboard/dashboard-nav.ts",
 "src/js/dashboard/dashboard.ts",
 "src/js/import/import.ts",
 "src/js/live-recording/live-recording.ts",
 "src/js/live-recording/record-review.ts",
 "src/js/live-recording/share-live-view.ts",
 "src/js/overview/activities-grid.ts",
 "src/js/overview/activities-page-init.ts",
 "src/js/overview/segments-grid.ts",
 "src/js/overview/shape-filter/shape-filter.ts",
 "src/js/pages/friends.ts",
 "src/js/pages/multiday.ts",
 "src/js/pages/profile-settings.ts",
 "src/js/pages/segments-index.ts",
 "src/js/pages/sharing-inventory.ts",
 "src/js/pages/statistics.ts",
 "src/js/pages/keys-and-data.ts",
 "src/js/photos/photos.ts",
 "src/js/race-sim/race-replay.ts",
 "src/js/race-sim/races-grid.ts",
 "src/js/race-sim/races.ts",
 "src/js/race-sim/share-race-view.ts",
 "src/js/segment-create/segment-create.ts",
 "src/js/segment-stats/segment-stats-efforts-table.ts",
 "src/js/segment-stats/segment-stats.ts",
 "src/js/track-edit/entry-picker.ts",
 "src/js/trash/trash.ts",
 "vite.config.esm.ts",
 "eslint.config.mjs",
 "eslint.dragnet.config.mjs"
],
	project: ['src/js/**/*.ts'],
	ignore: ['src/js/generated/**'],
};

export default config;
