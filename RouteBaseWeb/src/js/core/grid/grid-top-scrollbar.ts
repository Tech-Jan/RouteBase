/**
 * Adds a second horizontal scrollbar *above* a scroll container, mirroring its horizontal scroll.
 *
 * Useful for wide grids (especially tall ones) where reaching the bottom scrollbar means scrolling the
 * page. Generic and reusable: pass any horizontally-scrolling container plus the element whose width it
 * scrolls (e.g. the table). The strip auto-hides when there's nothing to scroll horizontally, and stays
 * in sync as columns resize/toggle or the window resizes. Returns a disposer for callers that re-mount.
 */

/** Measure the platform's horizontal scrollbar thickness so the top strip is exactly tall enough. */
function scrollbarHeight(): number {
	const probe = document.createElement('div');
	probe.style.cssText = 'position:absolute;visibility:hidden;overflow-x:scroll;width:60px;height:60px;';
	const inner = document.createElement('div');
	inner.style.cssText = 'width:120px;height:120px;';
	probe.appendChild(inner);
	document.body.appendChild(probe);
	const h = probe.offsetHeight - probe.clientHeight;
	probe.remove();
	return h || 12; // overlay-scrollbar platforms report 0 → fall back to a visible height
}

export function attachTopScrollbar(scrollEl: HTMLElement, contentEl: HTMLElement): () => void {
	const bar = document.createElement('div');
	bar.className = 'grid-top-scrollbar';
	bar.style.height = `${scrollbarHeight()}px`;
	const spacer = document.createElement('div');
	spacer.className = 'grid-top-scrollbar-inner';
	bar.appendChild(spacer);
	scrollEl.parentElement?.insertBefore(bar, scrollEl);

	const sync = (): void => {
		spacer.style.width = `${scrollEl.scrollWidth}px`;
		// Match the container's viewport width (it reserves a vertical-scrollbar gutter we must mirror)
		// so both scrollbars share the same scroll range and the far right is reachable from the top.
		bar.style.marginRight = `${scrollEl.offsetWidth - scrollEl.clientWidth}px`;
		bar.style.display = scrollEl.scrollWidth > scrollEl.clientWidth + 1 ? '' : 'none';
	};

	// Two-way scroll sync, guarded so each programmatic update doesn't echo back into a loop.
	const ac = new AbortController();
	let locked = false;
	bar.addEventListener('scroll', () => {
		if (locked) return;
		locked = true;
		scrollEl.scrollLeft = bar.scrollLeft;
		locked = false;
	}, { signal: ac.signal });
	scrollEl.addEventListener('scroll', () => {
		if (locked) return;
		locked = true;
		bar.scrollLeft = scrollEl.scrollLeft;
		locked = false;
	}, { signal: ac.signal });

	// Keep the spacer width (→ scroll range) in step with column resizes/toggles and viewport changes.
	const ro = new ResizeObserver(sync);
	ro.observe(contentEl);
	ro.observe(scrollEl);
	sync();

	// The scroll container outlives a grid that is torn down and re-mounted (the race field rebuild), so
	// the caller has to be able to drop this strip — otherwise each remount stacks another one.
	return () => {
		ac.abort();
		ro.disconnect();
		bar.remove();
	};
}
