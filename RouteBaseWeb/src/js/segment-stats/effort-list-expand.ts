/**
 * The segments effort list: compact by default, grown briefly on interaction, and — because efforts on a
 * segment are FIXED-ORDER and never reshuffle — the followed effort is centred ONCE (on select), not every
 * frame. That is the whole point of doing it here rather than reusing the race sidebar's per-tick centring:
 * a segment can hold ~1000 efforts, so there must be no per-frame work. Selecting an effort or scrolling
 * grows the list (`.is-expanded`); it eases back after a few seconds. A wheel/touch scroll grows it WITHOUT
 * yanking the scroll; only a selection centres.
 */

/** The scrollTop that puts row `idx` in the middle of the viewport, clamped so the first row lands at the
 *  top and the last at the bottom. Pure — the one bit worth a unit test. */
export function centerScrollTop(idx: number, rowH: number, viewportH: number, scrollH: number): number {
	if (rowH <= 0) return 0;
	const target = idx * rowH - viewportH / 2 + rowH / 2;
	const max = Math.max(0, scrollH - viewportH);
	return Math.max(0, Math.min(max, target));
}

export interface EffortListExpand {
	/** Grow the list; when `center`, glide the followed effort to the middle once. */
	expand(center: boolean): void;
	destroy(): void;
}

export interface EffortListExpandOptions {
	/** The scroll container — `#replayEffortList` (= `.replay-sidebar-body`). */
	readonly container: HTMLElement;
	/** The (uniform) row height, read live — it is measured after layout. */
	readonly rowHeight: () => number;
	/** The engine's follow index, or -1. */
	readonly getFollowIndex: () => number;
	/** How long the list stays grown after the last interaction. */
	readonly holdMs?: number;
	/**
	 * Must the list stay open right now (core/panel-hold-open's `keepOpen`)? True while the panel is
	 * latched AND while a press is still down on it — a scrollbar drag is the second one, and it emits
	 * neither wheel nor touchmove, so before this the timer expired part-way through a drag and the list
	 * collapsed under the pointer. A predicate, not a flag, so the latch stays the single owner.
	 */
	readonly stayOpen?: () => boolean;
}

export function createEffortListExpand(options: EffortListExpandOptions): EffortListExpand {
	const { container } = options;
	const holdMs = options.holdMs ?? 3500;
	let timer: ReturnType<typeof setTimeout> | undefined;

	function centerFollowed(): void {
		const idx = options.getFollowIndex();
		const rowH = options.rowHeight();
		if (idx < 0 || rowH <= 0 || typeof container.scrollTo !== 'function') return;
		const top = centerScrollTop(idx, rowH, container.clientHeight, container.scrollHeight);
		container.scrollTo({ top, behavior: 'smooth' });
	}

	function expand(center: boolean): void {
		container.classList.add('is-expanded');
		if (timer) clearTimeout(timer);
		// Latched, or a press still down: grown, and no timer to take it away again.
		if (!options.stayOpen?.()) timer = setTimeout(() => container.classList.remove('is-expanded'), holdMs);
		if (center) centerFollowed();
	}

	// A wheel or touch-drag grows the list for browsing — it must NOT yank the scroll, so it never centres.
	const onScrollGesture = (): void => expand(false);
	container.addEventListener('wheel', onScrollGesture, { passive: true });
	container.addEventListener('touchmove', onScrollGesture, { passive: true });

	return {
		expand,
		destroy(): void {
			if (timer) clearTimeout(timer);
			container.classList.remove('is-expanded');
			container.removeEventListener('wheel', onScrollGesture);
			container.removeEventListener('touchmove', onScrollGesture);
		},
	};
}
