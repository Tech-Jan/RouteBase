/**
 * Module augmentation for Chart.js.
 *
 * This file MUST be a module (the top-level `export {}` is required) so that
 * `declare module "chart.js"` augments the existing module rather than
 * replacing it with an ambient module declaration.
 *
 * Augmentations:
 *   - resetZoom / zoomScale  — added by chartjs-plugin-zoom (no official @types package)
 *   - getElementsAtEventForMode overload — Chart.js runtime accepts ChartEvent-shaped
 *     objects; the stock type signature only lists native Event.
 */
export {};

declare module "chart.js" {
	interface Chart {
		resetZoom(): void;
		isZoomedOrPanned(): boolean;
		zoomScale(id: string, range: { min: number; max: number }, transition: string): void;
		getElementsAtEventForMode(
			e: ChartEvent,
			mode: string,
			options: InteractionOptions,
			useFinalPosition: boolean,
		): InteractionItem[];
	}
}
