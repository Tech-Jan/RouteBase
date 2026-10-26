import { readData } from '../core/data-attrs';

/**
 * The map toolbar's <details> dropdown shell — Style, Size, and any view picker above a map.
 *
 * Extracted from segment-stats-map so the segment replay and the race replay share ONE toolbar rather
 * than two that drift apart. It manages only the SHELL: syncing each summary's value with the active
 * option, closing a picker after a choice, and closing an open dropdown on an outside click.
 *
 * What it deliberately does NOT do is own the option buttons. Those keep their ids so MapShell can wire
 * the basemap switcher and the size controls straight to them; a page's own items (segment's map-view
 * picker) stay the page's business.
 *
 * Closing is DELEGATED to the <details> container, not bound per button: MapShell renders the
 * style-switcher buttons into the menu *after* this runs, so binding each button would silently miss
 * them all.
 */

export interface MapToolbarOptions {
	/** Where to look. Defaults to the whole document. */
	readonly root?: ParentNode;
	/** The <details> elements. Default `.map-dd`. */
	readonly dropdownSelector?: string;
	/** The element showing the current choice in the summary. Default `.map-dd-value`. */
	readonly valueSelector?: string;
	/** The menu holding the option buttons. Default `.map-dd-menu`. */
	readonly menuSelector?: string;
	/** The option buttons inside a menu. Default `.map-dd-item`. */
	readonly itemSelector?: string;
}

/** @returns a teardown for the document-level outside-click listener. */
export function setupMapToolbar(options: MapToolbarOptions = {}): () => void {
	const root = options.root ?? document;
	const dropdownSelector = options.dropdownSelector ?? '.map-dd';
	const valueSelector = options.valueSelector ?? '.map-dd-value';
	const menuSelector = options.menuSelector ?? '.map-dd-menu';
	const itemSelector = options.itemSelector ?? '.map-dd-item';

	const dropdowns = Array.from(root.querySelectorAll<HTMLDetailsElement>(dropdownSelector));
	const observers: MutationObserver[] = [];

	for (const dropdown of dropdowns) {
		// Only a "picker" reports a current value; a Size menu has no single active choice.
		if (readData(dropdown, 'picker') !== 'true') continue;

		const valueEl = dropdown.querySelector<HTMLElement>(valueSelector);
		const menu = dropdown.querySelector<HTMLElement>(menuSelector);
		const syncValue = (): void => {
			if (!valueEl) return;
			const active = menu?.querySelector<HTMLElement>(`${itemSelector}.active`);
			const label = active?.textContent?.trim();
			// Idempotent: textContent replaces the text node even when the string is unchanged.
			if (label && valueEl.textContent !== label) valueEl.textContent = label;
		};

		syncValue();
		if (!menu) continue;

		// Observe the MENU, never the whole <details>: valueEl lives in the summary, and a textContent
		// write is a childList mutation, so observing an ancestor of it would make this observer feed
		// itself — an infinite microtask loop that hard-freezes the tab (it did).
		//
		// Watch both childList and class, and the order this module is called in stops mattering:
		//   · childList — MapShell renders the basemap buttons by replacing the menu's innerHTML, so if
		//     setup ran FIRST there is nothing to read yet. Attributes alone would never fire for that,
		//     and the summary would sit on its placeholder label for ever.
		//   · attributes — a later basemap switch only TOGGLES .active on buttons that already exist.
		// Segment-stats happens to set up after rendering and the race page before it; neither should
		// have to know.
		const observer = new MutationObserver(syncValue);
		observer.observe(menu, {
			subtree: true,
			childList: true,
			attributes: true,
			attributeFilter: ['class'],
		});
		observers.push(observer);

		dropdown.addEventListener('click', (event: Event) => {
			if ((event.target as HTMLElement).closest(itemSelector)) {
				// Next frame: let the button's own handler run before the menu closes under it.
				requestAnimationFrame(() => { dropdown.open = false; });
			}
		});
	}

	const onDocumentClick = (event: Event): void => {
		for (const dropdown of dropdowns) {
			if (dropdown.open && !dropdown.contains(event.target as Node)) dropdown.open = false;
		}
	};
	document.addEventListener('click', onDocumentClick);

	return () => {
		document.removeEventListener('click', onDocumentClick);
		for (const observer of observers) observer.disconnect();
	};
}
