// photo-lightbox.ts — shared full-size photo viewer, used by the Details photos panel
// (activity/activity-photos.ts) and the map overlay (photos/photo-map-overlay.ts) on both the Details
// and Activities overview maps. One singleton overlay: opening a new one replaces the current.
// Dismiss via the close button, a backdrop click, or Esc. Locks body scroll.
//
// It takes a COLLECTION plus a starting index, not one URL: the ‹ › controls page through it in place,
// and `onShow` lets the host follow along — panning its map to each photo. That is also what makes
// overlapping map photos reachable. Past the thumbnail zoom threshold, photos taken seconds apart draw
// as stacked icons and a click can only ever hit the topmost one; paging reaches the rest without moving
// a single pin. Controls come from photos/photo-viewer-chrome (close, arrows, counter, caption).
//
// It is a native <dialog> opened with showModal(), which renders in the browser's TOP LAYER — above
// every z-index stacking context. It used to be a plain div at z-index 2000, which the replay chrome
// (10001-10002, set inline by activity-replay-ui.ts) buried: clicking a photo mid-replay appeared to
// do nothing. The top layer takes that class of bug off the table permanently rather than winning one
// more round of the z-index ladder.

import { acquirePlaybackHold, releasePlaybackHold, seekPlaybackHoldTo } from "../core/playback-hold";
import { must } from '../core/invariant';
import { mountViewerChrome, type ViewerChrome } from "./photo-viewer-chrome";
import { mountPhotoZoom, type PhotoZoom } from "./photo-zoom";

export interface LightboxPhoto {
	/** Preview-tier image URL. */
	url: string;
	/** Where the photo sits on the map, when it is placed — hosts pan to it from `onShow`. */
	lngLat?: [number, number] | null;
	caption?: string | null;
}

export interface LightboxOptions {
	/** The shown photo changed (fires on open too) — both hosts use it to pan their map to the photo. */
	onShow?(photo: LightboxPhoto, index: number): void;
}

let dialog: HTMLDialogElement | null = null;
let chrome: ViewerChrome | null = null;
let zoom: PhotoZoom | null = null;
let image: HTMLImageElement | null = null;
let photos: LightboxPhoto[] = [];
let idx = 0;
let options: LightboxOptions = {};
let lastFocused: HTMLElement | null = null; // PU-04: element to restore focus to on close

/** Remove the overlay without restoring focus or releasing the playback hold (used by open-as-replace). */
function teardown(): void {
	if (!dialog) return;
	const d = dialog;
	dialog = null; // cleared first so a close-triggered handler can't re-enter
	chrome?.destroy();
	chrome = null;
	zoom?.destroy();
	zoom = null;
	image = null;
	photos = [];
	idx = 0;
	options = {};
	if (d.open) d.close();
	d.remove();
	document.body.style.overflow = "";
}

/**
 * Page to another photo in place — no rebuild, so the playback hold and focus survive.
 *
 * `viaPaging` separates "the user walked here with ‹ ›" from "the viewer just opened on this photo".
 * Only the walk is allowed to move a held playback (see below).
 */
function show(to: number, viaPaging = false): void {
	if (!image || !chrome || photos.length === 0) return;
	idx = (to + photos.length) % photos.length;
	const p = must(photos[idx], 'modulo index within photos (guarded non-empty)');
	zoom?.reset(); // a pinch belongs to the photo it was made on, not to the next one
	image.src = p.url;
	chrome.setPosition(idx, photos.length);
	chrome.setCaption(p.caption ?? null);
	// Only a deliberate ‹ › step carries the held playback (a running replay) to the photo, so closing
	// resumes from where the user walked to. Opening one photo and closing it again must leave the
	// replay exactly where it was. Before onShow: the host pans the map from there, and this must not
	// move the camera after it. Placed photos only.
	if (viaPaging && p.lngLat) seekPlaybackHoldTo(p.lngLat);
	options.onShow?.(p, idx);
}

/** Open (or replace) the full-size overlay on `items`, starting at `index`. */
export function openPhotoFullscreen(items: LightboxPhoto[], index = 0, opts: LightboxOptions = {}): void {
	if (items.length === 0) return;
	// PU-04: save the trigger only on a fresh open (not an open→open replace) so the
	// eventual close restores focus to the original trigger, not the previous overlay's button.
	if (!dialog) lastFocused = document.activeElement as HTMLElement | null;
	teardown();

	const d = document.createElement("dialog");
	d.className = "det-photo-fs";
	d.setAttribute("aria-label", "Photo viewer");

	const img = document.createElement("img");
	img.alt = "";
	d.appendChild(img);

	const c = mountViewerChrome({
		host: d,
		onPrev: () => show(idx - 1, true),
		onNext: () => show(idx + 1, true),
		onClose: () => closePhotoFullscreen(),
	});
	// The viewer, not the browser, owns a pinch in here — a page zoom would outlive the close and
	// leave the map behind it magnified. It also swallows the click that ends a pan, which the
	// backdrop handler below would otherwise read as a dismiss.
	const z = mountPhotoZoom({ image: img, surface: d });

	d.addEventListener("click", (e) => {
		// A modal dialog's backdrop reports the dialog itself as the target. The chrome layer is
		// pointer-events:none, so a click on empty chrome resolves to the dialog and lands here too.
		if (e.target === d) closePhotoFullscreen();
	});
	// Escape and the arrows are acted on here, and stopped here. The replay session also listens for
	// Escape on `document` (core/replay-history-session.ts), which would otherwise fire a second time
	// behind the photo and exit the replay on one keypress. Keys the viewer does not act on still reach
	// the page. showModal() puts focus inside the dialog, so the event passes through this listener
	// before it can reach document.
	d.addEventListener("keydown", (e) => {
		if (!chrome?.handleKey(e)) return;
		e.stopPropagation();
		e.preventDefault(); // Escape: don't let the browser also emit `cancel` on the now-closed dialog
	});
	d.addEventListener("cancel", (e) => { e.preventDefault(); closePhotoFullscreen(); });

	document.body.appendChild(d);
	dialog = d;
	chrome = c;
	zoom = z;
	image = img;
	photos = items;
	options = opts;
	d.showModal();
	document.body.style.overflow = "hidden";
	c.focusClose(); // PU-04: move focus into the dialog

	// Pause a running replay while the photo is up (no-op when nothing is playing). Idempotent, so an
	// open→open replace doesn't double-hold. Before the first show(), so a host that pans its map from
	// `onShow` isn't fighting a still-running replay camera for it.
	acquirePlaybackHold();
	show(index);
}

export function closePhotoFullscreen(): void {
	const wasOpen = dialog !== null;
	teardown();
	// PU-04: restore focus to the original trigger (if still attached), then clear it.
	if (wasOpen && lastFocused?.isConnected) lastFocused.focus();
	lastFocused = null;
	// Resume the replay we paused on open — only if we were the one that paused it.
	if (wasOpen) releasePlaybackHold();
}
