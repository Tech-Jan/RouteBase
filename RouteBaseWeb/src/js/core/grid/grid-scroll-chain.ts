/**
 * Page-first scroll lock for a fixed-height grid table.
 *
 * A grid table lives in a constant-size scroll window (see .act-table-wrapper / .table-responsive).
 * Native browser overscroll chaining hands scrolling to the inner table before the page has fully
 * reached its bottom, which feels wrong. This locks the table's vertical scroll (overflow-y: hidden)
 * until the page scroller (.cockpit-scroll) is at its exact bottom, then unlocks it (overflow-y: auto).
 *
 * Effect: scrolling down scrolls the page to its bottom first, then flows into the table; scrolling up
 * empties the table to its top, then leaving the bottom re-locks it and the page scrolls up. It only
 * toggles native scrollability — no wheel/touch interception — so touch momentum is preserved and the
 * behavior is identical on desktop and mobile.
 */

/** Toggle-and-forget: pass the fixed-height table scroll wrapper; it finds its .cockpit-scroll page scroller. */
export function attachScrollChain(wrapper: HTMLElement): void {
	const page = wrapper.closest<HTMLElement>('.cockpit-scroll');
	if (!page) return;

	let locked: boolean | null = null; // null = not yet applied, so the first update always writes
	const update = (): void => {
		// 1px epsilon absorbs fractional device-pixel rounding at the bottom.
		const atBottom = page.scrollTop + page.clientHeight >= page.scrollHeight - 1;
		const next = !atBottom;
		if (next === locked) return; // only touch the DOM on a real state change
		locked = next;
		wrapper.style.overflowY = next ? 'hidden' : 'auto';
	};

	page.addEventListener('scroll', update, { passive: true });
	window.addEventListener('resize', update);
	// Content-height changes (filters shrinking the grid, panels collapsing) move page.scrollHeight
	// without any scroll/resize event — re-evaluate when the wrapper or the page's sections resize.
	const ro = new ResizeObserver(update);
	ro.observe(wrapper);
	for (const child of Array.from(page.children)) ro.observe(child);
	update();
}
