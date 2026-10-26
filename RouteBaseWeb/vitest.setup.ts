// vitest.setup.ts — test-environment shims for browser APIs jsdom doesn't implement.
//
// Only put things here that are missing from the ENVIRONMENT. Production code must never carry a
// branch that exists solely to make a test pass — if a shim would have to live in src/, the design
// is wrong, not the test.

// jsdom (29.x) ships <dialog> but not showModal()/close(): calling it throws TypeError. Several
// modules open real modal dialogs (photo-lightbox, core/share-links, pages/keys-and-data), so without
// this every test that opens one fails on the environment rather than on the code. The top-layer
// behaviour can't be emulated and isn't asserted — only open/close state is.
const proto = globalThis.HTMLDialogElement?.prototype as HTMLDialogElement | undefined;
if (proto && typeof proto.showModal !== "function") {
	proto.showModal = function showModal(this: HTMLDialogElement): void {
		this.open = true;
	};
	proto.show = function show(this: HTMLDialogElement): void {
		this.open = true;
	};
	proto.close = function close(this: HTMLDialogElement, returnValue?: string): void {
		if (!this.open) return;
		this.open = false;
		if (returnValue !== undefined) this.returnValue = returnValue;
		this.dispatchEvent(new Event("close"));
	};
}

// jsdom has no layout, so Element.scrollIntoView simply isn't there. Anything that scrolls a panel into
// view before acting (activity-photos' placement arms the map card that way) would fail on the missing
// method rather than on its own logic. A no-op is honest here: there is nothing to scroll.
const elProto = globalThis.Element?.prototype as Element | undefined;
if (elProto && typeof elProto.scrollIntoView !== "function") {
	elProto.scrollIntoView = function scrollIntoView(): void {};
}
