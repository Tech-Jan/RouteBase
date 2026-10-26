/**
 * The pull-up chart drawer: a strip of charts under a replay map that slides down out of the way and
 * back up again, and that you can drag to whatever height you want.
 *
 * A Y-axis port of the follow sidebar's drag-to-resize (`race-sim/race-replay-overlay.ts`), which was
 * already the proven one: pointer events, so mouse and touch are ONE path; `setPointerCapture`, so the
 * drag survives leaving the 10 px handle; a CSS custom property for the size, so the transition and the
 * layout stay in the stylesheet where they belong.
 *
 * Open/closed and the chosen height are DEVICE preferences, remembered per drawer through
 * core/device-prefs.ts (which owns the key registry and the try/catch). **A missing key means OPEN**:
 * the charts are the reason the drawer exists, so a first-time viewer sees them; only someone who has
 * actually closed it gets it closed.
 *
 * The handle is both the grab strip and the toggle — tap it to collapse, drag it to resize. They are
 * told apart by the same slop model the sidebar's row taps use, because a resize that ends within a few
 * pixels of where it started was a click all along. Dragging it all the way DOWN dismisses the drawer
 * outright rather than bottoming out at the minimum height: the gesture that opens it has to be able to
 * close it, or the panel is one you can only shrink.
 *
 * A drag is a live PREVIEW; the release is what commits. While the finger is down the strip follows it
 * continuously — below the minimum height too, all the way to nothing — so the charts compress under the
 * pull instead of holding at the minimum and then vanishing in one step. Letting go settles the drawer
 * with the stylesheet's transition: past the dismissal line it eases shut, short of it it eases back open
 * at the minimum. Nothing snaps, and nothing is decided until you let go.
 */

import { devicePrefKeys, getBool, getNumber, setBool, setNumber } from './device-prefs';

/** Movement (px) below which a press-and-release is a tap, not a drag. Matches the sidebar's row taps. */
const TAP_SLOP_PX = 8;

/**
 * How far below the minimum height a downward drag turns into a dismissal, as a fraction of that
 * minimum. Half: high enough that a clumsy resize does not close the drawer, low enough that "pull it
 * all the way down" always does.
 */
const DISMISS_FRACTION = 0.5;

/** How long the caller's CSS transition may take before `onResize` is fired regardless. */
const TRANSITION_FALLBACK_MS = 320;

export interface ReplayChartDrawerOptions {
	/** Carries the `is-open` / `is-resizing` classes. The stylesheet hangs the layout off these. */
	readonly hostEl: HTMLElement;
	/** The collapsing box. Its natural height is the drawer's height until the user drags one. */
	readonly contentEl: HTMLElement;
	/** The grab strip: tap to toggle, drag to resize. */
	readonly handleEl: HTMLElement;
	/** Names this drawer in the device-pref registry (devicePrefKeys.drawerOpen/drawerHeight). */
	readonly storageKey: string;
	/** Default when nothing is stored. True — see the note above. */
	readonly defaultOpen?: boolean;
	/** Used until the content can be measured (a canvas strip has no natural height of its own). */
	readonly defaultHeightPx?: number;
	readonly minHeightPx?: number;
	/** Read live: it depends on the viewport, and a phone rotates. */
	readonly maxHeightPx?: () => number;
	/** The custom property the stylesheet reads. */
	readonly heightVar?: string;
	/** Where that property is set. Defaults to the host; a fixed-position sibling needs it higher up. */
	readonly varTarget?: HTMLElement;
	/** Fired after the open/close transition and after a resize — for maps and charts that must re-fit. */
	readonly onResize?: () => void;
}

export interface ReplayChartDrawer {
	open(): void;
	close(): void;
	toggle(): void;
	isOpen(): boolean;
	/** The drawer's current height in px — 0 while closed. */
	height(): number;
	destroy(): void;
}

export function createReplayChartDrawer(options: ReplayChartDrawerOptions): ReplayChartDrawer {
	const {
		hostEl, contentEl, handleEl, storageKey,
		defaultOpen = true,
		defaultHeightPx = 180,
		minHeightPx = 80,
		maxHeightPx = () => Math.max(120, window.innerHeight * 0.6),
		heightVar = '--replay-drawer-h',
		varTarget = hostEl,
		onResize,
	} = options;

	const openKey = devicePrefKeys.drawerOpen(storageKey);
	const heightKey = devicePrefKeys.drawerHeight(storageKey);

	// Missing key = open (see the header). getBool returns the fallback for anything it cannot read as a
	// boolean, so a half-written key lands on the useful side the same way the old `!== 'false'` did.
	let isOpen = getBool(openKey, defaultOpen);

	const storedHeight = getNumber(heightKey, 0);
	// scrollHeight, not the bounding box: the box is the collapsing one, so until the custom property is
	// set its measured height is 0 and every drawer would open at the fallback size.
	let openHeight = storedHeight > 0 ? storedHeight : (contentEl.scrollHeight || defaultHeightPx);

	let resizeTimer = 0;

	/** The height the finger is currently asking for. Non-null ONLY during a drag, and it outranks the
	 *  committed state: that is what makes the drag a preview and the release the decision. */
	let liveHeight: number | null = null;

	function clamp(px: number): number {
		return Math.round(Math.max(minHeightPx, Math.min(maxHeightPx(), px)));
	}

	function applyHeight(): void {
		const px = liveHeight ?? (isOpen ? openHeight : 0);
		varTarget.style.setProperty(heightVar, `${px}px`);
	}

	/** Fire onResize once the CSS transition has settled — a map resized mid-slide re-fits to a wrong box. */
	function scheduleResize(): void {
		if (!onResize) return;
		window.clearTimeout(resizeTimer);
		resizeTimer = window.setTimeout(() => onResize(), TRANSITION_FALLBACK_MS);
	}

	function setOpen(next: boolean, persist = true): void {
		isOpen = next;
		hostEl.classList.toggle('is-open', isOpen);
		handleEl.setAttribute('aria-expanded', String(isOpen));
		applyHeight();
		if (persist) setBool(openKey, isOpen);
		scheduleResize();
	}

	// ── drag to resize (mouse or touch, one path) ──────────────────────────────────────────────────
	let dragStartY = 0;
	let dragStartH = 0;
	let dragged = false;

	const onDragMove = (e: PointerEvent): void => {
		// Up is bigger: the drawer grows out of the bottom of the page.
		const delta = dragStartY - e.clientY;
		if (Math.abs(delta) > TAP_SLOP_PX) dragged = true;
		if (!dragged) return;

		// The strip follows the finger the whole way — past the minimum, down to nothing. It compresses
		// under the pull rather than holding at the minimum and then disappearing in one step, and that
		// continuity is the only cue that says "keep pulling and this closes".
		liveHeight = Math.round(Math.max(0, Math.min(maxHeightPx(), dragStartH + delta)));

		// Only a height worth keeping is remembered: squeezing it to nothing must not overwrite the size
		// the user last chose, or re-opening would come back at a sliver.
		if (liveHeight >= minHeightPx) openHeight = liveHeight;

		// The CLASS is the picture, `isOpen` is the decision — mid-drag they are allowed to disagree.
		// Previewing it here is what widens the grip the moment the strip reaches nothing: the cue that
		// says "let go now and it stays shut" arrives while you can still change your mind.
		hostEl.classList.toggle('is-open', liveHeight > 0);

		applyHeight();
	};

	const onDragEnd = (e: PointerEvent): void => {
		if (handleEl.hasPointerCapture?.(e.pointerId)) handleEl.releasePointerCapture(e.pointerId);
		handleEl.removeEventListener('pointermove', onDragMove);
		handleEl.removeEventListener('pointerup', onDragEnd);
		handleEl.removeEventListener('pointercancel', onDragEnd);
		// Dropped BEFORE the final height is written, so the settle animates instead of jumping — while
		// the class is on, the stylesheet kills the transition so the drag can track the finger.
		hostEl.classList.remove('is-resizing');

		// A press that never really moved is the toggle, not a resize.
		if (!dragged) {
			liveHeight = null;
			setOpen(!isOpen);
			return;
		}

		const settled = liveHeight ?? (isOpen ? openHeight : 0);
		liveHeight = null;   // hand the height back to the committed state

		// Pulled all the way down, the drawer GOES — it does not bottom out at its minimum. A panel you
		// cannot get rid of with the same gesture that opened it is the thing every bottom sheet gets
		// wrong. Short of that line it eases back open at the minimum instead: a pull that did not go far
		// enough undoes itself, so the gesture is always recoverable.
		if (settled < minHeightPx * DISMISS_FRACTION) {
			setOpen(false);   // `openHeight` is untouched, so re-opening restores the chosen size
			return;
		}

		openHeight = clamp(settled);
		setOpen(true);
		setNumber(heightKey, openHeight);
	};

	const onDragStart = (e: PointerEvent): void => {
		e.preventDefault();   // a drag must not select text or scroll the page
		dragStartY = e.clientY;
		dragStartH = isOpen ? openHeight : 0;
		dragged = false;
		hostEl.classList.add('is-resizing');
		handleEl.setPointerCapture?.(e.pointerId);
		handleEl.addEventListener('pointermove', onDragMove);
		handleEl.addEventListener('pointerup', onDragEnd);
		handleEl.addEventListener('pointercancel', onDragEnd);
	};

	// Keyboard: the handle is a control, so it answers to Enter/Space like one.
	const onKeyDown = (e: KeyboardEvent): void => {
		if (e.key !== 'Enter' && e.key !== ' ') return;
		e.preventDefault();
		setOpen(!isOpen);
	};

	handleEl.addEventListener('pointerdown', onDragStart);
	handleEl.addEventListener('keydown', onKeyDown);

	// Paint the remembered state straight onto the DOM: no persist (nothing changed) and no onResize
	// (nothing has been laid out against it yet).
	hostEl.classList.toggle('is-open', isOpen);
	handleEl.setAttribute('aria-expanded', String(isOpen));
	applyHeight();

	return {
		open: () => { if (!isOpen) setOpen(true); },
		close: () => { if (isOpen) setOpen(false); },
		toggle: () => setOpen(!isOpen),
		isOpen: () => isOpen,
		height: () => (isOpen ? openHeight : 0),
		destroy() {
			window.clearTimeout(resizeTimer);
			handleEl.removeEventListener('pointerdown', onDragStart);
			handleEl.removeEventListener('keydown', onKeyDown);
			handleEl.removeEventListener('pointermove', onDragMove);
			handleEl.removeEventListener('pointerup', onDragEnd);
			handleEl.removeEventListener('pointercancel', onDragEnd);
		},
	};
}
