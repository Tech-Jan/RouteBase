import { describe, it, expect, vi, afterEach } from 'vitest';
import { mountViewerChrome } from './photo-viewer-chrome';

// The full-size viewers' controls used to be position:absolute inside a position:fixed overlay — i.e.
// anchored to the LAYOUT viewport. Pinch-zoom slides the VISUAL viewport across that, so on a phone the
// close button walked off screen and, with body scroll locked and the dialog in the top layer, the only
// way back was to zoom out first. What these pin is the pinning: the layer tracks visualViewport and
// counter-scales, so the buttons stay in the visible corners at their physical size.

interface FakeVV {
	width: number; height: number; scale: number; offsetLeft: number; offsetTop: number;
	addEventListener(ev: string, cb: () => void): void;
	removeEventListener(ev: string, cb: () => void): void;
}

function stubVisualViewport(init: Partial<FakeVV> = {}): {
	vv: FakeVV; fire(ev: string): void; listenerCount(ev: string): number;
} {
	const listeners: Record<string, (() => void)[]> = {};
	const vv: FakeVV = {
		width: 400, height: 800, scale: 1, offsetLeft: 0, offsetTop: 0, ...init,
		addEventListener: (ev, cb) => { (listeners[ev] ??= []).push(cb); },
		removeEventListener: (ev, cb) => { listeners[ev] = (listeners[ev] ?? []).filter((f) => f !== cb); },
	};
	Object.defineProperty(window, 'visualViewport', { value: vv, configurable: true, writable: true });
	return {
		vv,
		fire: (ev) => (listeners[ev] ?? []).slice().forEach((cb) => cb()),
		listenerCount: (ev) => (listeners[ev] ?? []).length,
	};
}

function mount(overrides: Partial<Parameters<typeof mountViewerChrome>[0]> = {}) {
	const host = document.createElement('div');
	document.body.appendChild(host);
	const calls = { prev: vi.fn(), next: vi.fn(), close: vi.fn() };
	const chrome = mountViewerChrome({
		host, onPrev: calls.prev, onNext: calls.next, onClose: calls.close, ...overrides,
	});
	return { host, chrome, calls };
}

const key = (k: string) => new KeyboardEvent('keydown', { key: k });

afterEach(() => {
	document.body.innerHTML = '';
	Reflect.deleteProperty(window, 'visualViewport');
});

describe('viewer chrome controls', () => {
	it('always mounts a close button, whatever the collection size', () => {
		const { host, chrome } = mount();
		chrome.setPosition(0, 1);
		expect(host.querySelector('.photo-viewer-close')).not.toBeNull();
	});

	it('hides the arrows and counter for a single photo, shows them for more', () => {
		const { host, chrome } = mount();

		chrome.setPosition(0, 1);
		expect((host.querySelector('.photo-viewer-prev') as HTMLElement).hidden).toBe(true);
		expect((host.querySelector('.photo-viewer-counter') as HTMLElement).hidden).toBe(true);

		chrome.setPosition(2, 7);
		expect((host.querySelector('.photo-viewer-next') as HTMLElement).hidden).toBe(false);
		expect(host.querySelector('.photo-viewer-counter')!.textContent).toBe('3 / 7');
	});

	it('announces the counter and caption as polite live regions (paging changes them silently otherwise)', () => {
		const { host } = mount();
		const counter = host.querySelector('.photo-viewer-counter') as HTMLElement;
		const caption = host.querySelector('.photo-viewer-caption') as HTMLElement;
		expect(counter.getAttribute('role')).toBe('status');
		expect(counter.getAttribute('aria-live')).toBe('polite');
		expect(caption.getAttribute('role')).toBe('status');
		expect(caption.getAttribute('aria-live')).toBe('polite');
	});

	it('shows a caption only when there is one', () => {
		const { host, chrome } = mount();
		const caption = host.querySelector('.photo-viewer-caption') as HTMLElement;

		chrome.setCaption('Summit');
		expect(caption.hidden).toBe(false);
		expect(caption.textContent).toBe('Summit');

		chrome.setCaption(null);
		expect(caption.hidden).toBe(true);
	});

	it('routes clicks and stops them reaching the host, which reads a click as dismiss', () => {
		const { host, chrome, calls } = mount();
		chrome.setPosition(0, 3);
		const hostClick = vi.fn();
		host.addEventListener('click', hostClick);

		for (const sel of ['.photo-viewer-prev', '.photo-viewer-next', '.photo-viewer-close']) {
			host.querySelector<HTMLElement>(sel)!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
		}

		expect(calls.prev).toHaveBeenCalledTimes(1);
		expect(calls.next).toHaveBeenCalledTimes(1);
		expect(calls.close).toHaveBeenCalledTimes(1);
		expect(hostClick).not.toHaveBeenCalled();
	});
});

describe('viewer chrome key handling', () => {
	it('acts on Escape and the arrows, and reports them consumed', () => {
		const { chrome, calls } = mount();
		chrome.setPosition(1, 4);

		expect(chrome.handleKey(key('Escape'))).toBe(true);
		expect(chrome.handleKey(key('ArrowLeft'))).toBe(true);
		expect(chrome.handleKey(key('ArrowRight'))).toBe(true);
		expect(calls.close).toHaveBeenCalledTimes(1);
		expect(calls.prev).toHaveBeenCalledTimes(1);
		expect(calls.next).toHaveBeenCalledTimes(1);
	});

	it('leaves the arrows alone when there is nowhere to page', () => {
		const { chrome, calls } = mount();
		chrome.setPosition(0, 1);

		expect(chrome.handleKey(key('ArrowRight'))).toBe(false);
		expect(calls.next).not.toHaveBeenCalled();
	});

	it('does not claim keys it has no use for — the page still gets them', () => {
		const { chrome } = mount();
		chrome.setPosition(0, 5);
		expect(chrome.handleKey(key('ArrowUp'))).toBe(false);
		expect(chrome.handleKey(key('a'))).toBe(false);
	});
});

describe('viewer chrome visual-viewport pinning', () => {
	it('covers the visible region and counter-scales it, so a pinch cannot hide the controls', () => {
		const h = stubVisualViewport({ width: 400, height: 800, scale: 1 });
		const { host } = mount();
		const layer = host.querySelector('.photo-viewer-chrome') as HTMLElement;

		// Unzoomed: the layer is the whole viewport at 1:1.
		expect(layer.style.transform).toBe('translate(0px, 0px) scale(1)');

		// Pinch to 2× and pan: the visible region is half as wide and offset inside the layout viewport.
		h.vv.scale = 2; h.vv.width = 200; h.vv.height = 400; h.vv.offsetLeft = 30; h.vv.offsetTop = 40;
		h.fire('resize');

		// Pre-multiplied by the scale that the transform then divides out — the rendered box lands
		// exactly on the visible region, and a 44px button still measures 44px on the glass.
		expect(layer.style.width).toBe('400px');
		expect(layer.style.height).toBe('800px');
		expect(layer.style.transform).toBe('translate(30px, 40px) scale(0.5)');
	});

	it('follows a pinch-pan, not just a pinch', () => {
		const h = stubVisualViewport({ width: 200, height: 400, scale: 2, offsetLeft: 0, offsetTop: 0 });
		const { host } = mount();
		const layer = host.querySelector('.photo-viewer-chrome') as HTMLElement;

		h.vv.offsetLeft = 120; h.vv.offsetTop = 60;
		h.fire('scroll');

		expect(layer.style.transform).toBe('translate(120px, 60px) scale(0.5)');
	});

	it('detaches its viewport listeners and removes the layer on destroy', () => {
		const h = stubVisualViewport();
		const { host, chrome } = mount();
		expect(h.listenerCount('resize')).toBe(1);
		expect(h.listenerCount('scroll')).toBe(1);

		chrome.destroy();

		expect(host.querySelector('.photo-viewer-chrome')).toBeNull();
		expect(h.listenerCount('resize')).toBe(0);
		expect(h.listenerCount('scroll')).toBe(0);
	});

	it('degrades to the static CSS layout where visualViewport does not exist', () => {
		// jsdom has none; neither do older engines. The layer must still mount, with no inline geometry
		// overriding the stylesheet's 100vw/100vh.
		const { host } = mount();
		const layer = host.querySelector('.photo-viewer-chrome') as HTMLElement;
		expect(layer).not.toBeNull();
		expect(layer.style.transform).toBe('');
	});
});
