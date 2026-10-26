/**
 * Glue for driving an external card / minimap grid from a controls-only {@link GridView}
 * (`externalResults: true`).
 *
 * The grid's filter/sort produce a result set; this binding hides the cards that fell out of the filter and
 * reorders the survivors to match the sort — via the CSS `order` property, so the DOM nodes never move
 * (keeps embedded canvas thumbnails / iframes intact). The card container must be `display: flex|grid` for
 * `order` to take effect. Generic over the row type; reused by Segments and the dashboard minimap preview.
 */

import type { GridDataResponse, GridQuery } from './grid-types';

export interface CardBindingOptions<TRow> {
	/** Container holding the card elements (must be `display: flex` or `grid` so CSS `order` applies). */
	container: HTMLElement;
	/** Stable per-row id, matched against each card's `idAttribute`. */
	getId: (row: TRow) => number;
	/** Data attribute on each card holding its id. Default `'data-segment-id'`. */
	idAttribute?: string;
	/** Class toggled on cards that fall outside the current filter. Default `'is-hidden'`. */
	hiddenClass?: string;
	/** CSS selector for the card elements. Default `.${idAttribute-derived}` → built from `idAttribute`. */
	cardSelector?: string;
	/** Optional element shown (un-`hidden`) when zero cards match. */
	emptyState?: HTMLElement | null;
}

/**
 * Returns an `onData` handler for {@link GridViewOptions.onData} that reorders + filters the cards to match
 * each query result. The container is read fresh on every call, so late-arriving / re-rendered cards are
 * still handled.
 */
export function bindCardsToGrid<TRow>(opts: CardBindingOptions<TRow>): (res: GridDataResponse<TRow>, q: GridQuery) => void {
	const idAttr = opts.idAttribute ?? 'data-segment-id';
	const hiddenClass = opts.hiddenClass ?? 'is-hidden';
	const selector = opts.cardSelector ?? `[${idAttr}]`;

	return (res) => {
		const visibleIds = new Set(res.filteredIds);
		// `res.rows` is already filtered + sorted; its index is the desired visual order.
		const order = new Map(res.rows.map((row, i) => [opts.getId(row), i]));
		const cards = opts.container.querySelectorAll<HTMLElement>(selector);
		cards.forEach((card) => {
			const id = Number(card.getAttribute(idAttr));
			card.classList.toggle(hiddenClass, !visibleIds.has(id));
			card.style.order = String(order.get(id) ?? 0);
		});
		if (opts.emptyState) opts.emptyState.hidden = visibleIds.size !== 0;
	};
}
