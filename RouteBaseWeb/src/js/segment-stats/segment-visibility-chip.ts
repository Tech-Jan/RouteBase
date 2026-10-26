// The segment header's visibility control: a det-badge chip that swaps to a select while you edit it.
//
// Mirrors the activity header's two states with ONE deliberate difference: there is no Edit/Save/Cancel
// cycle here, because this page never had one. The select PATCHes immediately and swaps straight back,
// which is the behaviour the owner panel already had — this change is about how it LOOKS, not about
// when it saves.
//
// Reverts the select when the server refuses, so the UI never claims a change that did not happen.
// `data-previous` is seeded from the rendered value BEFORE the first change: inside the handler
// `sel.value` is already the new value, so without the seed the first failed PATCH had nothing to
// revert to.

import { renderVisibilityBadge } from '../core/visibility-badge';

export function setupSegmentVisibilityChip(): void {
	const badge = document.getElementById('segVisibilityBadge');
	const sel = document.getElementById('segVisibilitySelect') as HTMLSelectElement | null;
	// Non-owner has the badge but no select; a share view has neither. Both are no-ops.
	if (!badge || !sel) return;

	sel.dataset['previous'] ??= sel.value;

	// Const rebinds — the guard's narrowing survives into the hoisted handlers below.
	const badgeEl = badge, selEl = sel;

	function showSelect(): void {
		badgeEl.style.display = 'none';
		selEl.style.setProperty('display', 'inline-block', 'important');
		selEl.focus();
	}

	function showBadge(): void {
		selEl.style.setProperty('display', 'none', 'important');
		badgeEl.style.display = '';
	}

	badge.addEventListener('click', showSelect);
	badge.addEventListener('keydown', (e) => {
		if (e.key === 'Enter' || e.key === ' ') {
			e.preventDefault();
			showSelect();
		}
	});

	// Dismiss without choosing. Guarded on the select still being visible so the change handler's own
	// swap-back does not race this one.
	sel.addEventListener('blur', () => {
		if (sel.style.display !== 'none') showBadge();
	});

	sel.addEventListener('change', () => {
		const previous = sel.dataset['previous'] ?? sel.value;
		const next = sel.value;
		sel.disabled = true;
		void fetch(`/api/SegmentsApi/${sel.dataset['segmentId']}/visibility`, {
			method: 'PATCH',
			headers: { 'Content-Type': 'application/json' },
			body: JSON.stringify({ visibility: next }),
		})
			.then((resp) => {
				if (resp.ok || resp.status === 204) {
					sel.dataset['previous'] = next;
					renderVisibilityBadge(badge, next);
				} else {
					sel.value = previous;
				}
			})
			.catch(() => {
				sel.value = previous;
			})
			.finally(() => {
				sel.disabled = false;
				showBadge();
			});
	});
}
