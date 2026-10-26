/**
 * The distance↔pixel maths for a profile strip: one linear axis, drawn left to right.
 *
 * Pure and shared, because two renderers already need it (the race elevation profile and the
 * field-speed strip) and the segment replay strip is the third. The axis value is whatever the strip
 * is plotted against — metres of chainage for a race course, percent-of-segment or seconds for a
 * segment effort — so nothing here knows what it is measuring.
 */

/**
 * Where an x on the canvas lands, in axis units.
 *
 * Clicks outside the plot are clamped, never wrapped — dragging off the left edge must mean "the
 * start", not "the end".
 */
export function axisValueAtX(
	x: number, plotLeft: number, plotWidth: number, axisMax: number,
): number {
	if (plotWidth <= 0) return 0;
	const fraction = (x - plotLeft) / plotWidth;
	return Math.min(axisMax, Math.max(0, fraction * axisMax));
}

/** The inverse — where an axis value sits on the canvas. */
export function xAtAxisValue(
	value: number, plotLeft: number, plotWidth: number, axisMax: number,
): number {
	if (axisMax <= 0) return plotLeft;
	return plotLeft + (value / axisMax) * plotWidth;
}
