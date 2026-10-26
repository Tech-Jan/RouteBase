/**
 * colors.ts — Color-gradient helpers shared by MapLibre tracks
 * and the segment-statistics effort palette.
 *
 * Extracted from the old utils god-file (CB-01 dissolution). Pure functions.
 */

/**
 * Map a value in [min, max] to an RGB color string.
 */
export function getColorForValue(
	value: number,
	min: number,
	max: number,
	colorScheme = "speed",
): string {
	const range = max - min;
	const normalized = range > 0 ? Math.max(0, Math.min(1, (value - min) / range)) : 0;

	let r: number, g: number, b: number;

	switch (colorScheme) {
		case "speed":
		case "cadence":
			// Green → Yellow → Red
			if (normalized < 0.5) {
				r = Math.floor(normalized * 2 * 255);
				g = 255;
				b = 0;
			} else {
				r = 255;
				g = Math.floor((1 - (normalized - 0.5) * 2) * 255);
				b = 0;
			}
			break;

		case "heartrate":
			// Blue → Green → Yellow → Red
			if (normalized < 0.33) {
				r = 0;
				g = Math.floor(normalized * 3 * 255);
				b = Math.floor((1 - normalized * 3) * 255);
			} else if (normalized < 0.66) {
				r = Math.floor((normalized - 0.33) * 3 * 255);
				g = 255;
				b = 0;
			} else {
				r = 255;
				g = Math.floor((1 - (normalized - 0.66) * 3) * 255);
				b = 0;
			}
			break;

		case "elevation":
			// Dark green → Light green → Yellow → Brown
			if (normalized < 0.5) {
				r = Math.floor(normalized * 2 * 200);
				g = Math.floor(100 + normalized * 2 * 155);
				b = 0;
			} else {
				r = Math.floor(200 + (normalized - 0.5) * 2 * 55);
				g = Math.floor(255 - (normalized - 0.5) * 2 * 155);
				b = Math.floor((normalized - 0.5) * 2 * 50);
			}
			break;

		default:
			return "#dc3545";
	}

	return `rgb(${r}, ${g}, ${b})`;
}

/**
 * Ranked effort color palette used by segment statistics and replays.
 * Index 0 = best effort (gold), 1 = silver, 2 = bronze, then accent colours.
 */
export const EFFORT_COLORS: string[] = [
	"#FFD700", // rank 1 — gold
	"#C0C0C0", // rank 2 — silver
	"#CD7F32", // rank 3 — bronze
	"#FF5722",
	"#2196F3",
	"#FF9800",
	"#9C27B0",
	"#00BCD4",
	"#E91E63",
	"#3F51B5",
];
