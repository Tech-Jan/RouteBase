/**
 * Click-to-hold for a replay's follow panel — the race sidebar, the segment effort list, and whatever
 * comes next.
 *
 * Every replay's list is compact by default and grows for a few seconds when you touch it, then settles
 * back. That is right when you brushed past it and wrong when you are READING it, and no timer can tell
 * those two apart — so a click says which: click the panel and it stays open until you click it again.
 *
 * Three rules, and each replaced something that was wrong in the hand:
 *
 *   - Only the PANEL toggles it. A click anywhere else — the map, the transport, the toolbar — leaves the
 *     latch alone. Releasing on an outside click was the first cut: you brush the map while reading, and
 *     the thing you were reading closes.
 *   - Rows are EXEMPT, via `ignoreSelector`. A tap on a rider row already means "follow him"; if it also
 *     toggled, picking a second rider would un-latch the panel you opened to compare the two.
 *   - A DRAG IS NOT A CLICK, and neither is a press on a scroll bar. Dragging the thumb, or holding a
 *     scrollbar arrow, used to toggle the latch on release — so scrolling the list turned the highlight
 *     off. Same tap discipline the race sidebar's row-follow already uses, plus a gutter check.
 *
 * `keepOpen` is the other half, and the reason this module knows about presses at all: a scrollbar drag
 * emits NEITHER `wheel` NOR `touchmove`, only `scroll`. Callers wake their grow on the first two, so a
 * timer armed before the drag expired part-way through it and the list collapsed under the pointer that
 * was scrolling it. Holding the press open is what fixes that, and it belongs here because both replays
 * had the same hole.
 *
 * The module owns the latch and the class, never the caller's timers. Each replay's settle-back is its own
 * (race-replay-overlay's beginFreeScroll, segment-stats' effort-list-expand) and stays that way — they
 * gate it on `keepOpen`, which is the whole contract between them.
 */

/** The class stamped on the panel while latched. Each page styles it for its own panel. */
export const HELD_CLASS = 'is-held';

/** Movement past this many px makes a press a DRAG. Matches the row-tap slop in race-replay-overlay. */
const TAP_SLOP_PX = 8;

export interface HoldOpenPanelOptions {
	/** The panel the user clicks — the whole sidebar, not just its scrolling body. */
	readonly panel: HTMLElement;
	/**
	 * Selector for descendants whose clicks mean something else and must never toggle the latch. A row
	 * selector belongs here; see the rules above.
	 */
	readonly ignoreSelector?: string;
	/**
	 * `keepOpen` changed — re-run your grow. On the way up it means "no timer"; on the way down it means
	 * "arm one now". Latch changes that do not move `keepOpen` (latching with the finger still down) do
	 * not fire: nothing about the caller's timer has changed yet.
	 */
	readonly onChange: (keepOpen: boolean) => void;
}

export interface HoldOpenPanel {
	/** Latched by a click. This is what the highlight reflects — nothing else. */
	readonly held: boolean;
	/**
	 * Latched, OR the pointer is down on the panel right now. Gate the settle-back timer on THIS, never on
	 * `held` alone — see the note above about scrollbar drags emitting only `scroll`.
	 */
	readonly keepOpen: boolean;
	/** Release the latch from outside (the header collapsing the panel). A no-op when not held. */
	release(): void;
	destroy(): void;
}

/**
 * Did this press land in a scroll bar's gutter rather than on the content?
 *
 * `offsetX`/`offsetY` are measured inside the target's padding box, which EXCLUDES the scroll bars — so a
 * press on one reads as at-or-past `clientWidth`/`clientHeight`. The segment sidebar is the case that
 * forced this: the panel ITSELF is the scroller, so its bar is inside the element the latch listens on.
 */
function isScrollbarPress(event: PointerEvent, target: Element): boolean {
	if (!(target instanceof HTMLElement)) return false;
	const onVerticalBar = target.scrollHeight > target.clientHeight && event.offsetX >= target.clientWidth;
	const onHorizontalBar = target.scrollWidth > target.clientWidth && event.offsetY >= target.clientHeight;
	return onVerticalBar || onHorizontalBar;
}

export function createHoldOpenPanel(options: HoldOpenPanelOptions): HoldOpenPanel {
	const { panel, ignoreSelector } = options;
	let held = false;
	let pressed = false;
	/** The press may still become a toggle: inside the panel, off a row, off a scroll bar, and not a drag. */
	let armed = false;
	let downX = 0;
	let downY = 0;

	const keepOpen = (): boolean => held || pressed;

	function set(nextHeld: boolean, nextPressed: boolean): void {
		const before = keepOpen();
		held = nextHeld;
		pressed = nextPressed;
		panel.classList.toggle(HELD_CLASS, held);
		if (keepOpen() !== before) options.onChange(keepOpen());
	}

	// pointerdown, not click: the lists rebuild every row node on each board tick, so a `click` whose
	// mousedown target is destroyed before mouseup is silently dropped — the same trap the race sidebar's
	// tap-to-follow already documents.
	const onPointerDown = (e: PointerEvent): void => {
		const target = e.target;
		if (!(target instanceof Element)) return;
		set(held, true);   // the press holds the panel open whatever it turns out to mean
		downX = e.clientX;
		downY = e.clientY;
		armed = !(ignoreSelector && target.closest(ignoreSelector)) && !isScrollbarPress(e, target);
	};

	// On the WINDOW: a drag that starts on the thumb routinely ends with the pointer off the panel, and a
	// release we never saw would leave `pressed` stuck true — a panel that never settles back again.
	const onPointerUp = (e: PointerEvent): void => {
		if (!pressed) return;
		const dragged = Math.abs(e.clientX - downX) > TAP_SLOP_PX || Math.abs(e.clientY - downY) > TAP_SLOP_PX;
		const toggles = armed && !dragged;
		armed = false;
		set(toggles ? !held : held, false);
	};
	const onPointerCancel = (): void => {
		if (!pressed) return;
		armed = false;
		set(held, false);
	};

	panel.addEventListener('pointerdown', onPointerDown);
	window.addEventListener('pointerup', onPointerUp);
	window.addEventListener('pointercancel', onPointerCancel);

	return {
		get held(): boolean { return held; },
		get keepOpen(): boolean { return keepOpen(); },
		release(): void { set(false, pressed); },
		destroy(): void {
			panel.removeEventListener('pointerdown', onPointerDown);
			window.removeEventListener('pointerup', onPointerUp);
			window.removeEventListener('pointercancel', onPointerCancel);
			panel.classList.remove(HELD_CLASS);
			// The state goes with the class. Leaving `held` true behind a removed class is a getter that
			// contradicts the thing it describes — and a caller polling keepOpen during teardown would then
			// never arm its timer again. No onChange: the caller is on its way out.
			held = false;
			pressed = false;
			armed = false;
		},
	};
}
