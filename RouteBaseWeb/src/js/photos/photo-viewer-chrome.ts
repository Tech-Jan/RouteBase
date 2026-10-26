// photo-viewer-chrome.ts — the controls layer shared by both full-size photo viewers: the map/details
// lightbox (photos/photo-lightbox.ts) and the /Photos library viewer (photos/photos.ts). Close, prev/next,
// counter, caption. It knows nothing about photos — the host owns the collection and just reports
// position; this owns where the controls sit and how they survive a pinch.
//
// The pinch part is the reason this is a module and not markup. The controls used to be
// `position: absolute` inside a `position: fixed` overlay, which anchors them to the LAYOUT viewport.
// Native pinch-zoom moves the VISUAL viewport across it, so on mobile the close button slid off screen
// and — with body scroll locked and the dialog in the top layer — the only way back was to zoom out
// first. Pinning to visualViewport keeps every control in the visible corners, at constant apparent
// size, whatever the zoom.

export interface ViewerChromeOptions {
	/** The viewer overlay to mount into: a position:fixed element with no transform of its own. */
	host: HTMLElement;
	onPrev(): void;
	onNext(): void;
	onClose(): void;
}

export interface ViewerChrome {
	/** Update the counter and show/hide the arrows (hidden for a single-photo collection). */
	setPosition(index: number, count: number): void;
	setCaption(text: string | null): void;
	/** Act on Escape / ArrowLeft / ArrowRight. Returns whether the key was consumed. */
	handleKey(e: KeyboardEvent): boolean;
	/** Move focus into the overlay (the close button) — the host's modal-focus step. */
	focusClose(): void;
	destroy(): void;
}

function button(className: string, label: string, glyph: string): HTMLButtonElement {
	const b = document.createElement("button");
	b.type = "button";
	b.className = className;
	b.setAttribute("aria-label", label);
	b.textContent = glyph;
	return b;
}

export function mountViewerChrome(opts: ViewerChromeOptions): ViewerChrome {
	const layer = document.createElement("div");
	layer.className = "photo-viewer-chrome";

	const close = button("photo-viewer-close", "Close", "×");
	const prev = button("photo-viewer-nav photo-viewer-prev", "Previous photo", "‹");
	const next = button("photo-viewer-nav photo-viewer-next", "Next photo", "›");

	// Both start hidden: a host that never calls setPosition/setCaption gets no empty pills.
	// role=status/aria-live=polite: paging with ‹ › or the arrow keys changes these silently otherwise,
	// so a screen-reader user never hears the new position or the new caption.
	const counter = document.createElement("div");
	counter.className = "photo-viewer-counter";
	counter.setAttribute("role", "status");
	counter.setAttribute("aria-live", "polite");
	counter.hidden = true;
	const caption = document.createElement("div");
	caption.className = "photo-viewer-caption";
	caption.setAttribute("role", "status");
	caption.setAttribute("aria-live", "polite");
	caption.hidden = true;

	layer.append(close, prev, next, caption, counter);
	opts.host.appendChild(layer);

	let canPage = false;

	// Clicks are stopped here: the host treats a click on itself as a backdrop dismiss, and the layer
	// is pointer-events:none, so without this a nav press would also close the viewer.
	close.addEventListener("click", (e) => { e.stopPropagation(); opts.onClose(); });
	prev.addEventListener("click", (e) => { e.stopPropagation(); opts.onPrev(); });
	next.addEventListener("click", (e) => { e.stopPropagation(); opts.onNext(); });

	// Position the layer over the VISUAL viewport, scaled back down by the pinch factor so its controls
	// keep their physical size. Width/height are pre-multiplied by the scale because the transform then
	// divides them again — the rendered box lands exactly on the visible region.
	const vv = window.visualViewport ?? null;
	function syncToVisualViewport(): void {
		if (!vv) return;
		const s = vv.scale || 1;
		layer.style.width = `${vv.width * s}px`;
		layer.style.height = `${vv.height * s}px`;
		layer.style.transform = `translate(${vv.offsetLeft}px, ${vv.offsetTop}px) scale(${1 / s})`;
	}
	// No visualViewport (older engines, jsdom) → the layer keeps its CSS 100vw/100vh fallback, which is
	// exactly today's behaviour. Nothing here exists only for tests.
	vv?.addEventListener("resize", syncToVisualViewport);
	vv?.addEventListener("scroll", syncToVisualViewport);
	syncToVisualViewport();

	return {
		setPosition(index: number, count: number): void {
			canPage = count > 1;
			prev.hidden = !canPage;
			next.hidden = !canPage;
			counter.hidden = !canPage;
			counter.textContent = canPage ? `${index + 1} / ${count}` : "";
		},
		setCaption(text: string | null): void {
			caption.hidden = !text;
			caption.textContent = text ?? "";
		},
		handleKey(e: KeyboardEvent): boolean {
			if (e.key === "Escape") { opts.onClose(); return true; }
			// Only claim the arrows when there is somewhere to go — a key this viewer does not act on
			// must still reach the page.
			if (!canPage) return false;
			if (e.key === "ArrowLeft") { opts.onPrev(); return true; }
			if (e.key === "ArrowRight") { opts.onNext(); return true; }
			return false;
		},
		focusClose(): void { close.focus(); },
		destroy(): void {
			vv?.removeEventListener("resize", syncToVisualViewport);
			vv?.removeEventListener("scroll", syncToVisualViewport);
			layer.remove();
		},
	};
}
