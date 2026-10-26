/**
 * mark-card-deleted.ts — Grey out a just-deleted minimap card in place, instead of reloading.
 *
 * Both the dashboard feed (.activity-card) and the segments list (.seg-card) delete via a trash
 * button overlaid on the minimap. Reloading the whole page after each delete threw away the scroll
 * position (jump to top). Instead we dim + disable the card here; it lingers, greyed, until the next
 * real page load (server no longer returns it).
 *
 * The greyed-out look is applied as INLINE styles, not via a CSS class alone: `cockpit-components.css`
 * is served with `asp-append-version` + long `immutable` caching, and in this dev setup an edited
 * stylesheet can stay cached in the browser while the (hash-named) JS chunk updates — so a class-only
 * approach silently no-ops (class present, rule missing). Editing this file changes the chunk hash, which
 * guarantees the browser refetches it, so inline styles here always take effect. The `.is-card-deleted`
 * class is kept as the canonical hook (styled in cockpit-components.css once it refreshes, and greppable).
 */
export function markCardDeleted(card: HTMLElement | null): void {
	if (!card) return;
	card.classList.add("is-card-deleted");
	card.setAttribute("aria-disabled", "true");
	// Drop href so keyboard Enter can't open the disabled card either (pointer-events blocks the mouse).
	card.removeAttribute("href");

	card.style.opacity = "0.5";
	card.style.filter = "grayscale(0.7)";
	card.style.pointerEvents = "none"; // kills the card link AND the overlaid delete button
	card.style.cursor = "default";

	// The trash button is meaningless now — hide it (card is already gone server-side).
	const btn = card.querySelector<HTMLElement>(".activity-delete-btn, .seg-delete-btn");
	if (btn) btn.style.display = "none";
}
