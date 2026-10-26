/**
 * split-state.ts — locked-split state (core leaf).
 *
 * A split/segment the user pinned by clicking. While locked, hover-driven highlight
 * clears are suppressed. Formerly window.lockedSplit (the `false` init value is
 * normalized to null here — all readers were truthiness-based). Read cross-bundle by
 * map-utils-maplibre as the fallback under its _opts.getLockedSplit injection.
 */

export interface LockedSplit { start: number; end: number }

let _lockedSplit: LockedSplit | null = null;

export function getLockedSplit(): LockedSplit | null { return _lockedSplit; }
export function setLockedSplit(range: LockedSplit | null): void { _lockedSplit = range; }
