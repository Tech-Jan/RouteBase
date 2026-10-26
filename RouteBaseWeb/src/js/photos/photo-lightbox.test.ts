import { describe, it, expect, vi, afterEach, beforeEach, type Mock } from 'vitest';
import { openPhotoFullscreen, closePhotoFullscreen, type LightboxPhoto } from './photo-lightbox';
import { registerPlaybackHold, type PlaybackHoldTarget } from '../core/playback-hold';

// PU-04: the shared full-size overlay must be an accessible modal and manage focus — focus into the
// close button on open, restore to the trigger on close.
//
// It is a native <dialog> opened with showModal(). That replaced a div at z-index 2000, which the
// replay chrome (10001-10002) buried: a photo clicked mid-replay opened behind the transport bar.
// The top layer is not observable from jsdom, so what is pinned here is the mechanism that puts it
// there — showModal() on a real <dialog> — plus the behaviours that mechanism has to preserve.

const URL_A = '/api/photos/1/image?tier=preview&v=x';
const URL_B = '/api/photos/2/image?tier=preview&v=y';

const one = (url = URL_A): LightboxPhoto[] => [{ url }];
const many = (n: number): LightboxPhoto[] =>
	Array.from({ length: n }, (_, i) => ({ url: `/api/photos/${i + 1}/image?tier=preview&v=x`, lngLat: [7 + i, 47] }));

const shownSrc = () => document.querySelector('.det-photo-fs img')!.getAttribute('src');
const press = (k: string) => document.querySelector('.photo-viewer-close')!
	.dispatchEvent(new KeyboardEvent('keydown', { key: k, bubbles: true }));
const click = (sel: string) => document.querySelector(sel)!
	.dispatchEvent(new MouseEvent('click', { bubbles: true }));

describe('photo-lightbox (PU-04)', () => {
	afterEach(() => {
		closePhotoFullscreen();
		registerPlaybackHold(null);
		document.body.innerHTML = '';
	});

	it('opens a modal <dialog>, so it lands in the top layer rather than a z-index race', () => {
		const showModal = vi.spyOn(HTMLDialogElement.prototype, 'showModal');
		openPhotoFullscreen(one());

		const ov = document.querySelector('.det-photo-fs');
		expect(ov).not.toBeNull();
		expect(ov!.tagName).toBe('DIALOG');
		expect((ov as HTMLDialogElement).open).toBe(true);
		expect(showModal).toHaveBeenCalledTimes(1); // show() would not be top-layer
		expect(ov!.getAttribute('aria-label')).toBe('Photo viewer');
		showModal.mockRestore();
	});

	it('moves focus to the close button on open and restores it to the trigger on close', () => {
		const trigger = document.createElement('button');
		document.body.appendChild(trigger);
		trigger.focus();
		expect(document.activeElement).toBe(trigger);

		openPhotoFullscreen(one());
		expect(document.activeElement).toBe(document.querySelector('.photo-viewer-close'));

		closePhotoFullscreen();
		expect(document.activeElement).toBe(trigger);
	});

	it('replaces the current photo without stacking dialogs', () => {
		openPhotoFullscreen(one());
		openPhotoFullscreen(one(URL_B));
		expect(document.querySelectorAll('.det-photo-fs')).toHaveLength(1);
	});

	it('removes the dialog from the DOM on close', () => {
		openPhotoFullscreen(one());
		closePhotoFullscreen();
		expect(document.querySelector('.det-photo-fs')).toBeNull();
	});

	it('ignores an empty collection rather than opening a blank viewer', () => {
		openPhotoFullscreen([]);
		expect(document.querySelector('.det-photo-fs')).toBeNull();
	});

	// One keypress must have one effect. The replay session listens for Escape on `document`
	// (core/replay-history-session.ts), so without stopping it at the dialog, dismissing a photo
	// mid-replay would also exit the replay behind it.
	it('does not let Escape reach document-level handlers while open', () => {
		const pageEsc = vi.fn();
		document.addEventListener('keydown', (e) => { if (e.key === 'Escape') pageEsc(); });

		openPhotoFullscreen(one());
		press('Escape');

		expect(pageEsc).not.toHaveBeenCalled();
	});

	// Contract change (was: "only Escape is consumed"). The viewer now owns ←/→ as well, so letting
	// them through would recreate exactly the double-effect bug the Escape guard exists to prevent.
	// The original intent is intact: keys the viewer does NOT act on still reach the page.
	it('consumes the arrow keys it acts on, and lets every other key through', () => {
		const pageArrow = vi.fn();
		const pageOther = vi.fn();
		document.addEventListener('keydown', (e) => {
			if (e.key === 'ArrowRight') pageArrow();
			if (e.key === 'ArrowUp') pageOther();
		});

		openPhotoFullscreen(many(3));
		press('ArrowRight');
		press('ArrowUp');

		expect(pageArrow).not.toHaveBeenCalled();
		expect(pageOther).toHaveBeenCalledTimes(1);
	});

	// A single-photo viewer has nowhere to page, so it must not swallow the arrows either.
	it('lets the arrows through for a single-photo collection', () => {
		const pageArrow = vi.fn();
		document.addEventListener('keydown', (e) => { if (e.key === 'ArrowRight') pageArrow(); });

		openPhotoFullscreen(one());
		press('ArrowRight');

		expect(pageArrow).toHaveBeenCalledTimes(1);
	});
});

// Paging is what makes an occluded map photo reachable: past the thumbnail zoom threshold, photos taken
// seconds apart draw as overlapping icons and a click can only ever hit the topmost one.
describe('photo-lightbox paging', () => {
	afterEach(() => {
		closePhotoFullscreen();
		registerPlaybackHold(null);
		document.body.innerHTML = '';
	});

	it('opens on the requested index, not the first photo', () => {
		openPhotoFullscreen(many(4), 2);
		expect(shownSrc()).toBe('/api/photos/3/image?tier=preview&v=x');
		expect(document.querySelector('.photo-viewer-counter')!.textContent).toBe('3 / 4');
	});

	it('pages forward and back, wrapping at both ends', () => {
		openPhotoFullscreen(many(3));
		click('.photo-viewer-next');
		expect(shownSrc()).toBe('/api/photos/2/image?tier=preview&v=x');

		click('.photo-viewer-prev');
		click('.photo-viewer-prev'); // wraps past the start
		expect(shownSrc()).toBe('/api/photos/3/image?tier=preview&v=x');
	});

	it('reports every step to the host, so it can pan its map along', () => {
		const onShow = vi.fn();
		openPhotoFullscreen(many(3), 0, { onShow });

		expect(onShow).toHaveBeenNthCalledWith(1, expect.objectContaining({ lngLat: [7, 47] }), 0);
		click('.photo-viewer-next');
		expect(onShow).toHaveBeenNthCalledWith(2, expect.objectContaining({ lngLat: [8, 47] }), 1);
	});

	// The `onClose` option is gone with the cluster gallery that needed it: it existed to clear the
	// highlight ring that gallery painted on the map, and no host puts anything on screen from `onShow`
	// any more — both only pan a map, which nothing needs to undo. Test deleted with the contract, not
	// weakened around it.

	it('shows the caption when the host supplies one', () => {
		openPhotoFullscreen([{ url: URL_A, caption: 'Col du Galibier' }]);
		expect(document.querySelector('.photo-viewer-caption')!.textContent).toBe('Col du Galibier');
	});
});

describe('photo-lightbox playback hold', () => {
	let playing: boolean;
	let target: PlaybackHoldTarget & {
		pause: Mock<() => void>;
		resume: Mock<() => void>;
		seekToPosition: Mock<(ll: [number, number]) => void>;
	};

	beforeEach(() => {
		playing = true;
		target = {
			isPlaying: () => playing,
			pause: vi.fn<() => void>(() => { playing = false; }),
			resume: vi.fn<() => void>(() => { playing = true; }),
			seekToPosition: vi.fn<(ll: [number, number]) => void>(),
		};
		registerPlaybackHold(target);
	});

	afterEach(() => {
		closePhotoFullscreen();
		registerPlaybackHold(null);
		document.body.innerHTML = '';
	});

	it('pauses a running replay on open and resumes it on close', () => {
		openPhotoFullscreen(one());
		expect(target.pause).toHaveBeenCalledTimes(1);

		closePhotoFullscreen();
		expect(target.resume).toHaveBeenCalledTimes(1);
	});

	it('holds once across an open→open replace', () => {
		openPhotoFullscreen(one());
		openPhotoFullscreen(one(URL_B));
		expect(target.pause).toHaveBeenCalledTimes(1);

		closePhotoFullscreen();
		expect(target.resume).toHaveBeenCalledTimes(1);
	});

	// Paging swaps the image in place rather than reopening — otherwise every arrow press would
	// release and re-take the hold, and the replay would stutter forward one frame at a time.
	it('holds once across paging, and resumes only at the end', () => {
		openPhotoFullscreen(many(4));
		click('.photo-viewer-next');
		click('.photo-viewer-next');
		click('.photo-viewer-prev');

		expect(target.pause).toHaveBeenCalledTimes(1);
		expect(target.resume).not.toHaveBeenCalled();

		closePhotoFullscreen();
		expect(target.resume).toHaveBeenCalledTimes(1);
	});

	it('does not resume a replay the user had already paused', () => {
		playing = false;
		openPhotoFullscreen(one());
		closePhotoFullscreen();
		expect(target.pause).not.toHaveBeenCalled();
		expect(target.resume).not.toHaveBeenCalled();
	});

	// Contract change (was: "every photo the viewer shows moves the playhead"). Opening a photo is a
	// look, not a move — dragging the replay to it meant a glance at a photo and a close relocated the
	// ride. Only the ‹ › walk is a deliberate "take me here", so only the walk moves the playhead.
	it('leaves the playhead alone when a photo is merely opened and closed', () => {
		openPhotoFullscreen(many(3), 1);
		closePhotoFullscreen();

		expect(target.seekToPosition).not.toHaveBeenCalled();
	});

	it('carries the playhead on each ‹ › step', () => {
		openPhotoFullscreen(many(3));

		click('.photo-viewer-next');
		expect(target.seekToPosition).toHaveBeenNthCalledWith(1, [8, 47]);

		click('.photo-viewer-next');
		expect(target.seekToPosition).toHaveBeenNthCalledWith(2, [9, 47]);

		click('.photo-viewer-prev');
		expect(target.seekToPosition).toHaveBeenNthCalledWith(3, [8, 47]);
	});

	// The arrow keys page through the same chrome callbacks — they must carry the playhead too.
	it('carries the playhead when paging by keyboard', () => {
		openPhotoFullscreen(many(3));
		press('ArrowRight');

		expect(target.seekToPosition).toHaveBeenCalledWith([8, 47]);
	});

	// Order matters: the seek re-frames the playback, the host's pan is the last word on the camera.
	it('seeks before it tells the host to pan', () => {
		const calls: string[] = [];
		target.seekToPosition.mockImplementation(() => { calls.push('seek'); });
		openPhotoFullscreen(many(2), 0, { onShow: () => { calls.push('onShow'); } });
		calls.length = 0; // drop the open's onShow — this is about the step that follows

		click('.photo-viewer-next');

		expect(calls).toEqual(['seek', 'onShow']);
	});

	it('does not seek when the step lands on an unplaced photo', () => {
		openPhotoFullscreen([{ url: URL_A, lngLat: [7, 47] }, { url: URL_B }]);
		click('.photo-viewer-next'); // no lngLat — nowhere to seek to

		expect(target.seekToPosition).not.toHaveBeenCalled();
	});
});
