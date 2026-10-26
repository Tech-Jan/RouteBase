// The det-badge rendering of a visibility value, shared by every page that shows one.
//
// Extracted at the SECOND use rather than the third: the activity header and the segment header now
// promise (in their own markup comments) to look identical, and a shared renderer is what makes that
// true instead of aspirational. The maps are the whole contract — icon, label, colour variant.
//
// Note the icons differ from the card lists (`bi-lock-fill` there, `bi-lock` here). That is deliberate:
// a chip carries its own label text, a bare card icon does not and needs the heavier glyph to read.

import { escapeHtml } from './html';
import { must } from './invariant';

const ICONS: Record<string, string> = { private: 'bi-lock', friends: 'bi-people', public: 'bi-globe' };
const LABELS: Record<string, string> = { private: 'Private', friends: 'Friends', public: 'Public' };
const VARIANTS: Record<string, string> = {
	private: 'det-badge--danger',
	friends: 'det-badge--info',
	public: 'det-badge--success',
};

/** Unrecognised values render as "friends" — the app's own default, and the safest of the three. */
const FALLBACK = 'friends';

export function visibilityIcon(vis: string): string {
	return ICONS[vis] ?? must(ICONS[FALLBACK], "'friends' is a declared key");
}

export function visibilityLabel(vis: string): string {
	return LABELS[vis] ?? must(LABELS[FALLBACK], "'friends' is a declared key");
}

export function visibilityVariant(vis: string): string {
	return VARIANTS[vis] ?? must(VARIANTS[FALLBACK], "'friends' is a declared key");
}

/** Repaints an existing `.det-badge` element in place — class list AND contents, so no stale variant sticks. */
export function renderVisibilityBadge(badge: HTMLElement, vis: string): void {
	badge.className = `det-badge ${visibilityVariant(vis)}`;
	// Both interpolations run through escapeHtml (a declared escaper in eslint.config.mjs), so the
	// no-unsanitized rule passes without a disable — every value also comes from the closed maps above.
	badge.innerHTML = `<i class="bi ${escapeHtml(visibilityIcon(vis))}"></i> ${escapeHtml(visibilityLabel(vis))}`;
}
