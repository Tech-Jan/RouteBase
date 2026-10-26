/**
 * Segment Statistics — chart backdrop plugins (extracted from segment-stats-charts.ts, SC2-05).
 *
 * The density backdrop and the elevation-profile backdrop, as plugin factories that
 * take the one charts.ts-local helper each needs (effortPointX / the aggregate system)
 * plus the live ctx, RECEIVED instead of reached for via the module singleton.
 * The plugin bodies still read ctx live per draw (via the captured reference); no top-level side effects.
 */
import type { EffortDataPoint } from './segment-stats-effort-types';
import type { SegmentStatisticsContext } from './segment-stats-page-types';
import type { ChartKey } from './segment-stats-delta';
import type { AggregateSystem } from './segment-stats-aggregate';
import { must } from '../core/invariant';

// Density backdrop: blit the chart's precomputed effort-density bitmap (built alongside the
// aggregate datasets) behind the band/median. Pixel rect comes from the live scales, so pan/zoom
// just stretches the bitmap — one drawImage per frame, never a re-bin.
export function makeDensityPlugin(key: ChartKey, deps: { aggregate: AggregateSystem; ctx: SegmentStatisticsContext }) {
	const { ctx } = deps;
	return {
		id: "segDensity_" + key,
		beforeDatasetsDraw: (chart: InstanceType<typeof Chart>) => {
			if (!ctx.aggregateMode) return;
			const imgs = deps.aggregate.getDensityImages(key);
			if (!imgs.length) return;
			const xScale = chart.scales.x, yScale = chart.scales.y;
			const area = chart.chartArea;
			if (!xScale || !yScale || !area) return;
			const c = chart.ctx;
			c.save();
			// Clip to the plot area so the y-overshoot (yMax above the frozen axis) can't paint over the axes.
			c.beginPath();
			c.rect(area.left, area.top, area.right - area.left, area.bottom - area.top);
			c.clip();
			c.imageSmoothingEnabled = true;
			// One bitmap per bin region (high-res main window + coarse outlier tail). For each,
			// draw only the slice inside the visible x-window: mapping the whole image would hand
			// the canvas a destination rect hundreds of thousands of px wide on a deep zoom-in,
			// which browsers may clamp/distort (reads as x-compression).
			for (const img of imgs) {
				const viewMin = Math.max(xScale.min, img.xMin);
				const viewMax = Math.min(xScale.max, img.xMax);
				if (!(viewMax > viewMin)) continue;
				const sxPerX = img.canvas.width / (img.xMax - img.xMin);
				const sx0 = (viewMin - img.xMin) * sxPerX;
				const sx1 = (viewMax - img.xMin) * sxPerX;
				const dx0 = xScale.getPixelForValue(viewMin);
				const dx1 = xScale.getPixelForValue(viewMax);
				const pyTop = yScale.getPixelForValue(img.yMax);
				const pyBot = yScale.getPixelForValue(img.yMin);
				if (!isFinite(dx0) || !isFinite(dx1) || !isFinite(pyTop) || !isFinite(pyBot)) continue;
				c.drawImage(img.canvas, sx0, 0, sx1 - sx0, img.canvas.height, dx0, pyTop, dx1 - dx0, pyBot - pyTop);
			}
			c.restore();
		},
	};
}

// Light-grey elevation profile of the currently highlighted effort, drawn BEHIND the
// speed lines on the performance chart (mirrors the elevation backdrop on the
// activity-details page). Nothing renders when no effort is highlighted.
const ELEV_BG_FILL = "rgba(140, 145, 160, 0.22)";
const ELEV_BG_STROKE = "rgba(140, 145, 160, 0.45)";

export function makeElevBackgroundPlugin(deps: { effortPointX: (p: EffortDataPoint, totalDistance: number) => number; ctx: SegmentStatisticsContext }) {
	const { effortPointX, ctx } = deps;
	return {
		id: "segElevBackground",
		beforeDatasetsDraw: (chart: InstanceType<typeof Chart>) => {
			const sel = ctx.selectedEffortIndex;
			if (sel == null) return;
			const pts = ctx.performanceData?.[sel]?.points;
			if (!pts || pts.length === 0) return;
			const xScale = chart.scales.x;
			const area = chart.chartArea;
			if (!xScale || !area) return;

			// Elevation extent (+8% grace top & bottom, matching the activity chart's yBg axis).
			let minA = Infinity, maxA = -Infinity;
			for (const pt of pts) {
				const a = pt.altitude ?? 0;
				if (a < minA) minA = a;
				if (a > maxA) maxA = a;
			}
			if (!isFinite(minA) || !isFinite(maxA)) return;
			const grace = (maxA - minA || 1) * 0.08;
			const lo = minA - grace;
			const span = (maxA + grace) - lo || 1;

			const totalDist = must(pts.at(-1), 'non-empty (caller-guarded)').distance;
			const plotH = area.bottom - area.top;
			const altY = (p: EffortDataPoint): number =>
				area.bottom - (((p.altitude ?? 0) - lo) / span) * plotH;

			// Sample to ~1000 points (full resolution is wasteful on every pan/zoom redraw),
			// always keeping the last point so the area spans the effort's full width.
			const stride = Math.max(1, Math.floor(pts.length / 1000));
			const coords: Array<[number, number]> = [];
			for (let i = 0; i < pts.length; i += stride) {
				const pt = must(pts[i], 'i < length (loop-bounded)');
				coords.push([xScale.getPixelForValue(effortPointX(pt, totalDist)), altY(pt)]);
			}
			const last = must(pts.at(-1), 'non-empty (caller-guarded)');
			coords.push([xScale.getPixelForValue(effortPointX(last, totalDist)), altY(last)]);

			const c = chart.ctx;
			c.save();
			// Clip to the plot area so zoomed-out-of-range geometry can't paint over the axes.
			c.beginPath();
			c.rect(area.left, area.top, area.right - area.left, plotH);
			c.clip();

			// Filled area down to the baseline.
			const firstC = must(coords[0], 'coords non-empty (last always pushed)');
			const lastC = must(coords.at(-1), 'coords non-empty (last always pushed)');
			c.beginPath();
			c.moveTo(firstC[0], area.bottom);
			for (const [px, py] of coords) c.lineTo(px, py);
			c.lineTo(lastC[0], area.bottom);
			c.closePath();
			c.fillStyle = ELEV_BG_FILL;
			c.fill();

			// Faint top edge to define the profile.
			c.beginPath();
			c.moveTo(firstC[0], firstC[1]);
			for (const [px, py] of coords) c.lineTo(px, py);
			c.lineWidth = 1;
			c.strokeStyle = ELEV_BG_STROKE;
			c.stroke();
			c.restore();
		},
	};
}
