// after-layout.ts — run a callback once the browser has committed a layout change.
//
// A chart mounted inside a display:none container measures 0 and renders into a 1 px box;
// its real pixel size is only readable AFTER the browser has laid the revealed element out.
// A single rAF still fires before layout settles in some engines, so the reliable primitive
// is the double-rAF: frame 1 commits the DOM/visibility change, frame 2 has final dimensions.
//
// THE single home for that dance (chart-kernel item 5). Both the panel manager (tab reveal) and
// the collapsible card (expand) route through here, so no chart falls back to a magic setTimeout
// racing layout — the failure mode that left a chart stuck at 1 px until the next interaction.
export function afterLayout(cb: () => void): void {
	requestAnimationFrame(() => {
		requestAnimationFrame(() => {
			cb();
		});
	});
}
