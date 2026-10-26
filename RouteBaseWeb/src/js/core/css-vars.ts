// css-vars.ts — read a CSS custom property off :root.
//
// Charts that draw to a raw canvas (bars, the year-row plugin, …) need theme colours as concrete
// strings, not `var(--x)`. This reads the resolved value off document.documentElement, falling back
// when the variable is unset/empty. Shared so the bar chart and its year-row plugin agree.

export function cssVar(name: string, fallback: string): string {
	return getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback;
}
