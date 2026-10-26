/**
 * An animated profile strip: a terrain backdrop, optional coloured bands and markers over it — and a
 * field of dots, RIDING it.
 *
 * Extracted from the race elevation profile so the segment replay can mount the same strip. Nothing
 * here knows about races: the x-axis is an anonymous linear scale (`axisMax` units, drawn left to
 * right) and every race-shaped decision arrives through one of three seams:
 *
 *   1. `ProfileBackdrop` — either ONE shared terrain everyone rides, or N independent per-participant
 *      lines. That choice is a claim about the data, so the caller makes it; see the two types below.
 *   2. Decorations as DATA — `setDecorations`. Leg tints and checkpoints are bands and markers here;
 *      what they mean stays with the caller. No decorations = the cheap single-fill path, which is
 *      what the segment strip wants.
 *   3. `ProfileInteraction` — the whole place/drag path. Omit it and the strip is read-only, which is
 *      what a share fork and a segment viewer both are.
 *
 * The canvas mechanics — the dual-canvas static/moving split, DPR sizing, the hover, the playhead and
 * lead lines, the ResizeObserver and the hidden-container deferral — all live in `core/canvas-strip.ts`,
 * shared with the race field-speed strip. This file is the terrain/decorations/field half over that shell.
 */

import { cssVar } from './css-vars';
import { createCanvasStrip, type StripInteraction, type StripSurface } from './canvas-strip';
import { groupByStyle, type StyleGroup } from './profile-style-groups';

/**
 * ONE shared terrain everyone rides: an elevation array sampled every `step` axis units across
 * `axisMax`. The dots' height is READ OFF it, so every dot at the same x sits at the same y — which is
 * only honest when there really is one shared course underneath them.
 */
/**
 * The x-axis here is whatever `step`/`axisMax` say it is — metres along a race course, percent of a
 * track's distance — and EVERY value handed to setPlayhead/setHover/decorations has to be in that
 * same space. Mixing spaces does not fail loudly; it draws a plausible chart with the marker in the
 * wrong place (an elapsed-time playhead on a distance axis reads as "the summit came later than it
 * did"). An adapter whose engine counts in something else owes the conversion — see
 * `core/profile-terrain.axisValueAtIndex`.
 */
export interface ProfileTerrainBackdrop {
	readonly kind: 'terrain';
	readonly elevation: ArrayLike<number>;
	/** Axis distance between consecutive samples — metres for a race course, percent for a segment. */
	readonly step: number;
	readonly axisMax: number;
}

/** One line of the series backdrop: its own x/y pairs, in axis units, in its own colour. */
export interface ProfileSeries {
	readonly x: ArrayLike<number>;
	readonly y: ArrayLike<number>;
	readonly color: string;
}

/**
 * N INDEPENDENT lines, one per participant — for the case where there is no shared terrain to draw.
 *
 * A shapes segment is the reason this exists: it has zero reference GPS by construction, so "50%" means
 * 50% of THIS effort's own distance and every effort's 50% is somewhere else on the ground. A single
 * backdrop there would be a claim about shared position that is not true; per-effort lines are the
 * honest rendering. Dots must carry their own y here (`ProfileField.heights`) — there is nothing to
 * derive it from.
 */
export interface ProfileSeriesBackdrop {
	readonly kind: 'series';
	readonly series: readonly ProfileSeries[];
	readonly axisMax: number;
}

/** Either way the x-axis is `axisMax` units wide — seconds, percent, metres; the chart does not care. */
export type ProfileBackdrop = ProfileTerrainBackdrop | ProfileSeriesBackdrop;

/**
 * The trace colour every strip uses. Blue, and deliberately NOT the cyan the rider dots wear: the two
 * share the strip, so identity has to survive them overlapping. One constant because the trace means the
 * same thing on all of them — colour follows the entity, not the chart it happens to be drawn on.
 */
export const PROFILE_TRACE_COLOR = '#3b82f6';

/** The spread around a trace — index-aligned with its `x`. Drawn as one translucent area. */
export interface ProfileTraceBand {
	readonly low: ArrayLike<number>;
	readonly high: ArrayLike<number>;
}

/**
 * A SECOND measured series over the backdrop, on its own y-scale — the replay strips' speed line.
 *
 * The strip carries two units at once and draws NO axis for either. The terrain is a silhouette read as
 * "where the climbs are"; the trace is a shape read as "where he was quick". The number for either comes
 * from the hover readout, which names its units — one place to read a value, and no y-axis furniture in a
 * strip 190px tall. (It briefly had gridlines and left-edge labels for the trace; they were the only
 * numbers on the strip and they still cost a fifth of its height.)
 *
 * The scale is nonetheless anchored at 0 and topped at a round number above the data (`niceTop`) rather
 * than fitted to the data's own min/max. That is not about the missing axis — it is what makes a line
 * near the top mean genuinely fast, instead of merely "the fastest this ride happened to go".
 */
export interface ProfileTrace {
	/** Axis position per sample, ASCENDING, in the backdrop's axis units. */
	readonly x: ArrayLike<number>;
	/** The measured value per sample, index-aligned with `x`. */
	readonly y: ArrayLike<number>;
	/** Optional spread around the line (a field's p10–p90). Omit for a single track's own trace. */
	readonly band?: ProfileTraceBand | undefined;
	readonly color: string;
}

/** A tinted stretch of the backdrop, in axis units. The race draws its legs with these. */
export interface ProfileBand {
	readonly fromValue: number;
	readonly toValue: number;
	/** Null = the untinted default, so "no bands yet" looks deliberate rather than broken. */
	readonly color: string | null;
}

/** A vertical rule with a dot on top. The race draws its checkpoints with these. */
export interface ProfileMarker {
	readonly id: number;
	readonly atValue: number;
	/** Drawn brighter and fatter — the race's selected checkpoint. */
	readonly emphasised: boolean;
}

/** The dots. Index-aligned with whatever the caller's field is; read live every frame, never copied. */
export interface ProfileField {
	/** Axis position per dot. Pass the live array — it is read, not stored. */
	readonly values: ArrayLike<number>;
	/**
	 * Height per dot, in backdrop units. Omit under a `terrain` backdrop and it is read off the terrain;
	 * REQUIRED under a `series` backdrop, where each dot rides its own line.
	 */
	readonly heights?: ArrayLike<number> | null;
	/** 1 when that dot is done. Finished dots fade rather than pile up on the line. */
	readonly finished: ArrayLike<number>;
	readonly colors: readonly string[];
}

/**
 * The write path, in the caller's hands. The chart owns the pixel maths (which marker is under the
 * pointer, what axis value an x is); the caller owns what may be moved and where it may land.
 */
export interface ProfileInteraction {
	/**
	 * A press landed at `value`, over marker `markerId` (null = empty stretch). Return a marker id to
	 * begin dragging it, or null for "nothing to drag" — which is how a caller refuses to move an
	 * endpoint, or handles a placement click instead.
	 */
	onPointerDownAt(value: number, markerId: number | null): number | null;
	/** The drag moved. The caller snaps it and calls setMarkers/setBands back — the chart does not guess. */
	onDragTo?(markerId: number, value: number): void;
	/** Released after actually moving. One write per drag, not one per pixel. */
	onDragEnd?(markerId: number): void;
}

export interface ProfileChartOptions {
	readonly canvas: HTMLCanvasElement;
	readonly backdrop: ProfileBackdrop;
	/** Omit for a read-only strip. */
	readonly interaction?: ProfileInteraction | undefined;
	/** Pointer moved over the strip (axis units) — drives a shared hover. Null on leave. */
	readonly onHover?: ((value: number | null) => void) | undefined;
	/**
	 * The hover readout. Default reads the axis as metres and the backdrop as elevation. `y` is null
	 * under a series backdrop, where the pointer is over no one line in particular; `trace` is null
	 * whenever no trace is set, or the pointer is past its last sample.
	 */
	readonly formatHover?:
		| ((value: number, y: number | null, trace: number | null) => string)
		| undefined;
	readonly now?: (() => number) | undefined;
}

/**
 * The static decorations, set together. One call because they change together — a race checkpoint
 * moving is both a marker and the boundary of two leg bands, and a drag would otherwise repaint the
 * whole terrain path twice per pointermove.
 */
export interface ProfileDecorations {
	/** Tinted stretches of the backdrop. Empty = one flat untinted fill. */
	readonly bands: readonly ProfileBand[];
	readonly markers: readonly ProfileMarker[];
}

export interface ProfileChart {
	/** Swap what is drawn underneath — a segment replay re-opened over a different set of efforts. */
	setBackdrop(backdrop: ProfileBackdrop): void;
	setDecorations(decorations: ProfileDecorations): void;
	/** The measured overlay (speed) over the backdrop; null removes it and its gridlines. */
	setTrace(trace: ProfileTrace | null): void;
	/** The field to animate. Call once; the arrays are read live thereafter. */
	setField(field: ProfileField | null): void;
	/** Repaint dots (a table painted a group). Index → colour. */
	setColors(colors: ReadonlyMap<number, string>): void;
	/**
	 * Which dots a filter MATCHED. Both sets name the ones that are KEPT — never the ones acted on —
	 * because that is the contract `RaceMarkers.setDimmed(exceptIndices)` already has, and a page hands
	 * the SAME array to both views.
	 *
	 *   bright  — everyone else is drawn small and faint (Highlight mode).
	 *   visible — everyone else is not drawn at all (Hide mode).
	 *
	 * Null on both = the whole field, full strength. `bright` used to mean the opposite of this — the
	 * set to dim — so the map and the profile faded exactly the wrong halves of one field.
	 */
	setFilter(filter: { bright?: ReadonlySet<number> | null; visible?: ReadonlySet<number> | null }): void;
	/**
	 * Which dots are EMPHASISED — drawn bigger and on top of the rest. Null clears.
	 *
	 * A third set rather than a flag on `setFilter`, because it is not a filter: emphasis is a persisted
	 * property of the rider, and a filter is what the viewer is doing right now. Dimming still wins over
	 * it, so an emphasised rider the filter dropped recedes with everyone else.
	 */
	setEmphasis(indices: ReadonlySet<number> | null): void;
	/**
	 * Where the WATCHED participant is, in axis units — the leader by default, whoever is being followed
	 * while a follow is on. Null hides the playhead.
	 */
	setPlayhead(value: number | null): void;
	/**
	 * The leader, as a second golden rule. Set it only while the playhead is someone ELSE: the distance
	 * between the two lines is then the gap the viewer is watching. Null clears it.
	 */
	setLeadLine(value: number | null): void;
	/** Draw the hover line here — called when another chart or the map is hovered. Null clears. */
	setHover(value: number | null): void;
	/** The canvas cursor: `crosshair` while placement is armed, `default` otherwise. */
	setCursor(cursor: string): void;
	/** Redraw the moving half. `force` bypasses the 30 Hz cap — a seek must not lag the playhead. */
	tick(force?: boolean): void;
	destroy(): void;
}

/** How close (in px) a pointer must be to a marker to grab it rather than hit empty backdrop. */
const GRAB_RADIUS_PX = 6;

/**
 * The BACKDROP colours (the rule/hover colours live in the shell). Resolved to concrete strings — a
 * canvas cannot take `var(--x)` — and re-read only when the static half is redrawn.
 */
interface ProfileStaticTheme {
	untinted: string;
	marker: string;
	markerStrong: string;
}

function readStaticTheme(): ProfileStaticTheme {
	return {
		untinted: cssVar('--profile-untinted', 'rgba(148, 163, 184, 0.30)'),
		marker: cssVar('--profile-marker', 'rgba(226, 232, 240, 0.5)'),
		markerStrong: cssVar('--profile-marker-strong', '#ffffff'),
	};
}

/** A round top for the trace's scale, so the line is normalised against 10s or 20s rather than its own max. */
function niceTop(max: number): number {
	if (!(max > 0)) return 10;
	const step = max > 60 ? 20 : 10;
	return Math.ceil((max * 1.1) / step) * step;
}

export function createProfileChart(options: ProfileChartOptions): ProfileChart {
	const { canvas } = options;
	const interaction = options.interaction;
	let backdrop = options.backdrop;

	let bands: readonly ProfileBand[] = [];
	let markers: readonly ProfileMarker[] = [];
	let trace: ProfileTrace | null = null;
	/** Top of the trace's own 0-anchored y-scale. Only meaningful while `trace` is set. */
	let traceTop = 10;
	let field: ProfileField | null = null;
	let colors: string[] = [];
	let bright: ReadonlySet<number> | null = null;   // the MATCHED dots — everyone else dims
	let visible: ReadonlySet<number> | null = null;
	let emphasised: ReadonlySet<number> | null = null;   // drawn bigger, and last
	/**
	 * The memoised style buckets, rebuilt lazily on the next frame after a style change. Null = dirty.
	 * Invalidation is EXPLICIT rather than inferred: setColors mutates the `colors` array in place, so an
	 * identity check would silently keep painting the old colours.
	 */
	let groups: StyleGroup[] | null = null;
	const invalidateGroups = (): void => { groups = null; };
	/** Which marker a drag is moving, and whether the pointer moved since it began. */
	let dragging: { markerId: number; moved: boolean } | null = null;

	/**
	 * Elevation → y range, over the backdrop's own min/max so a flat course still fills the strip. In
	 * series mode the range spans EVERY line, so the lines stay comparable to each other.
	 */
	let rangeMin = 0;
	let rangeSpan = 1;
	function measureRange(): void {
		let min = Infinity;
		let max = -Infinity;
		const consider = (v: number): void => {
			if (v < min) min = v;
			if (v > max) max = v;
		};
		// ?? NaN: loop-bounded reads of dense arrays; NaN fails both comparisons in consider()
		// exactly as undefined did.
		if (backdrop.kind === 'terrain') {
			for (let i = 0; i < backdrop.elevation.length; i++) consider(backdrop.elevation[i] ?? NaN);
		} else {
			for (const s of backdrop.series) {
				for (let i = 0; i < s.y.length; i++) consider(s.y[i] ?? NaN);
			}
		}
		rangeMin = Number.isFinite(min) ? min : 0;
		rangeSpan = Number.isFinite(max) && max > min ? max - min : 1;
	}
	measureRange();

	/**
	 * The terrain height at an axis value — nearest node is plenty at one dot per 4 px. Null in series
	 * mode: there is no shared ground there, which is the whole reason that mode exists.
	 */
	function terrainAt(value: number): number | null {
		if (backdrop.kind !== 'terrain') return null;
		const i = Math.round(value / backdrop.step);
		const clamped = Math.min(backdrop.elevation.length - 1, Math.max(0, i));
		return backdrop.elevation[clamped] ?? 0;
	}

	/**
	 * The trace's y-scale, measured over the line AND its band so a p90 excursion is not clipped off the
	 * top of the strip. Anchored at 0 — see the ProfileTrace docs for why that is not negotiable.
	 */
	function measureTrace(): void {
		traceTop = 10;
		if (!trace) return;
		let max = 0;
		const consider = (v: number): void => { if (Number.isFinite(v) && v > max) max = v; };
		const source = trace.band ? trace.band.high : trace.y;
		for (let i = 0; i < source.length; i++) consider(source[i] ?? NaN);
		traceTop = niceTop(max);
	}

	/** Trace value → y pixel, over the trace's own 0..traceTop scale (NOT the backdrop's). */
	function traceYAt(surface: StripSurface, value: number): number {
		const p = surface.plot();
		return p.top + p.height - (value / traceTop) * p.height;
	}

	/**
	 * The trace value at an axis position — the nearest sample, by binary search over the ascending `x`.
	 * Null when there is no trace, so a hover label can drop the term rather than print a fake zero.
	 */
	function traceAt(value: number): number | null {
		if (!trace) return null;
		const { x, y } = trace;
		const n = Math.min(x.length, y.length);
		if (n === 0) return null;

		let lo = 0;
		let hi = n - 1;
		while (lo < hi) {
			const mid = (lo + hi) >> 1;
			if ((x[mid] ?? NaN) < value) lo = mid + 1;
			else hi = mid;
		}
		// lo is the first sample at or past `value`; its predecessor may be the closer of the two.
		const prev = lo > 0 ? lo - 1 : 0;
		const nearer = Math.abs((x[prev] ?? NaN) - value) <= Math.abs((x[lo] ?? NaN) - value) ? prev : lo;
		const at = y[nearer];
		return at !== undefined && Number.isFinite(at) ? at : null;
	}

	const formatHover = options.formatHover
		?? ((value: number, y: number | null) =>
			(y == null ? `${(value / 1000).toFixed(2)} km` : `${(value / 1000).toFixed(2)} km · ${Math.round(y)} m`));

	/** One filled curve, tinted per band. Bands are intervals of THE SAME curve, not separate curves — so
	 *  the one path is filled once per band, clipped to that band's span. No bands = one fill. */
	function paintTerrain(surface: StripSurface, t: ProfileTerrainBackdrop, untinted: string): void {
		const { g, plot, xAt, yAt } = surface;
		const p = plot();
		const nodes = t.elevation.length;
		if (nodes < 2) return;

		const areas: readonly ProfileBand[] = bands.length > 0
			? bands
			: [{ fromValue: 0, toValue: t.axisMax, color: null }];

		for (const band of areas) {
			g.save();
			g.beginPath();
			const x0 = xAt(band.fromValue);
			const x1 = xAt(band.toValue);
			g.rect(x0, p.top, Math.max(0, x1 - x0), p.height);
			g.clip();

			g.beginPath();
			g.moveTo(p.left, p.top + p.height);
			// ?? NaN (here and the draws below): loop-bounded reads of dense arrays; a NaN coordinate
			// degrades the path op exactly as the undefined-arithmetic NaN it replaces did.
			for (let i = 0; i < nodes; i++) g.lineTo(xAt(i * t.step), yAt(t.elevation[i] ?? NaN));
			g.lineTo(p.left + p.width, p.top + p.height);
			g.closePath();
			g.fillStyle = band.color ?? untinted;
			g.globalAlpha = band.color ? 0.5 : 1;
			g.fill();
			g.restore();
		}
	}

	/** N stroked lines, each in its own colour. Not filled: filled areas would occlude each other, and
	 *  the point of this mode is that the lines are separate claims. */
	function paintSeries(surface: StripSurface, b: ProfileSeriesBackdrop): void {
		const { g, xAt, yAt } = surface;
		g.globalAlpha = 0.85;
		g.lineWidth = 1.5;
		g.lineJoin = 'round';
		for (const s of b.series) {
			const n = Math.min(s.x.length, s.y.length);
			if (n < 2) continue;
			g.beginPath();
			g.moveTo(xAt(s.x[0] ?? NaN), yAt(s.y[0] ?? NaN));
			for (let i = 1; i < n; i++) g.lineTo(xAt(s.x[i] ?? NaN), yAt(s.y[i] ?? NaN));
			g.strokeStyle = s.color;
			g.stroke();
		}
		g.globalAlpha = 1;
	}

	/**
	 * The trace: its spread band, then its line — over the terrain and under the markers, so a checkpoint
	 * rule still reads as the topmost thing on the strip. No axis furniture; see ProfileTrace.
	 */
	function paintTrace(surface: StripSurface, t: ProfileTrace): void {
		const { g, xAt } = surface;
		const n = Math.min(t.x.length, t.y.length);
		if (n < 2) return;

		const yAt = (v: number): number => traceYAt(surface, v);

		// The spread band: up the highs, back down the lows, as one closed area.
		const band = t.band;
		if (band) {
			const bandN = Math.min(n, band.low.length, band.high.length);
			if (bandN >= 2) {
				g.beginPath();
				g.moveTo(xAt(t.x[0] ?? NaN), yAt(band.high[0] ?? NaN));
				for (let i = 1; i < bandN; i++) g.lineTo(xAt(t.x[i] ?? NaN), yAt(band.high[i] ?? NaN));
				for (let i = bandN - 1; i >= 0; i--) g.lineTo(xAt(t.x[i] ?? NaN), yAt(band.low[i] ?? NaN));
				g.closePath();
				g.fillStyle = t.color;
				g.globalAlpha = 0.18;
				g.fill();
				g.globalAlpha = 1;
			}
		}

		g.beginPath();
		g.moveTo(xAt(t.x[0] ?? NaN), yAt(t.y[0] ?? NaN));
		for (let i = 1; i < n; i++) g.lineTo(xAt(t.x[i] ?? NaN), yAt(t.y[i] ?? NaN));
		g.strokeStyle = t.color;
		g.lineWidth = 2;
		g.lineJoin = 'round';
		g.stroke();
	}

	/** The static half: terrain (or per-effort lines), the trace over it, then the markers on top. */
	function drawStatic(surface: StripSurface): void {
		const { g, plot, xAt } = surface;
		const theme = readStaticTheme();
		const p = plot();

		if (backdrop.kind === 'terrain') paintTerrain(surface, backdrop, theme.untinted);
		else paintSeries(surface, backdrop);

		if (trace) paintTrace(surface, trace);

		for (const m of markers) {
			const x = xAt(m.atValue);
			g.beginPath();
			g.moveTo(x, p.top);
			g.lineTo(x, p.top + p.height);
			g.lineWidth = m.emphasised ? 2 : 1;
			g.strokeStyle = m.emphasised ? theme.markerStrong : theme.marker;
			g.stroke();

			g.beginPath();
			g.arc(x, p.top, m.emphasised ? 5 : 3.5, 0, Math.PI * 2);
			g.fillStyle = m.emphasised ? theme.markerStrong : '#cbd5e1';
			g.fill();
		}
	}

	/** The moving half: the field of dots, grouped by colour so it is a handful of state changes a frame. */
	function drawForeground(surface: StripSurface): void {
		if (!field) return;
		const { g, xAt, yAt } = surface;
		const n = field.colors.length;

		// Grouped by colour: one fillStyle change and one path per colour, not per dot. With 508 riders and
		// a handful of painted groups that is ~5 state changes a frame instead of 508. The grouping itself
		// is memoised — the dots MOVE every frame but never change style, so only the inner loop is per-frame
		// work. Invalidated explicitly by the three setters that can alter a style; see invalidateGroups.
		groups ??= groupByStyle(colors, field.finished, bright, visible, emphasised, n);

		// In draw order — emphasised last, so it lands on top of the pile. See groupByStyle's sort.
		for (const { color, isDim, isFinished, isEmphasised, indices } of groups) {
			g.beginPath();
			for (const i of indices) {
				const x = xAt(field.values[i] ?? NaN);
				// Its own line's height where there is one, the shared ground where there is not.
				const y = yAt(field.heights ? (field.heights[i] ?? NaN) : (terrainAt(field.values[i] ?? NaN) ?? 0));
				const r = isDim ? 1.5 : isEmphasised ? 5 : 2.5;
				g.moveTo(x + r, y);
				g.arc(x, y, r, 0, Math.PI * 2);
			}
			g.fillStyle = color;
			g.globalAlpha = isDim ? 0.25 : isFinished ? 0.35 : 0.9;
			g.fill();
		}
		g.globalAlpha = 1;
	}

	// Translate the shell's generic press/drag seam onto the caller's ProfileInteraction. Marker hit-testing
	// is ours (only we know where the markers are); what a press means and where a drag may land is the
	// caller's, exactly as before.
	const stripInteraction: StripInteraction | undefined = interaction
		? {
			onPress({ value, x, xAt }) {
				const hit = markers.find((m) => Math.abs(xAt(m.atValue) - x) <= GRAB_RADIUS_PX);
				// A null answer covers both "nothing here" and "this one is fixed" — an endpoint the server
				// would refuse to move.
				const dragId = interaction.onPointerDownAt(value, hit ? hit.id : null);
				if (dragId == null) return false;
				dragging = { markerId: dragId, moved: false };
				return true;
			},
			onDrag(value) {
				if (!dragging) return;
				dragging.moved = true;
				// The caller snaps it and calls setDecorations back, which repaints — so the drag feels live.
				interaction.onDragTo?.(dragging.markerId, value);
			},
			onRelease() {
				if (!dragging) return;
				const { markerId, moved } = dragging;
				dragging = null;
				// One write per drag, not one per pixel of mouse movement.
				if (moved) interaction.onDragEnd?.(markerId);
			},
		}
		: undefined;

	const strip = createCanvasStrip({
		canvas,
		axisMax: backdrop.axisMax,
		yDomain: { min: rangeMin, span: rangeSpan },
		drawStatic,
		drawForeground,
		hoverLabel: (value) => formatHover(value, terrainAt(value), traceAt(value)),
		onHover: options.onHover,
		interaction: stripInteraction,
		now: options.now,
	});

	return {
		setBackdrop(next) {
			backdrop = next;
			measureRange();
			strip.setAxisMax(next.axisMax);
			strip.setYDomain(rangeMin, rangeSpan);
			strip.repaint();
		},
		setDecorations(next) {
			bands = next.bands;
			markers = next.markers;
			strip.repaint();
		},
		setTrace(next) {
			trace = next;
			measureTrace();
			strip.repaint();
		},
		setField(next) {
			field = next;
			colors = next ? [...next.colors] : [];
			invalidateGroups();
			strip.repaintFrame();
		},
		setColors(next) {
			for (const [i, color] of next) {
				if (i >= 0 && i < colors.length) colors[i] = color;
			}
			invalidateGroups();
			strip.repaintFrame();
		},
		setFilter({ bright: nextBright, visible: nextVisible }) {
			if (nextBright !== undefined) bright = nextBright;
			if (nextVisible !== undefined) visible = nextVisible;
			invalidateGroups();
			strip.repaintFrame();
		},
		setEmphasis(indices) {
			emphasised = indices;
			invalidateGroups();
			strip.repaintFrame();
		},
		setPlayhead: (value) => strip.setPlayhead(value),
		setLeadLine: (value) => strip.setLeadLine(value),
		setHover: (value) => strip.setHover(value),
		setCursor: (cursor) => strip.setCursor(cursor),
		tick: (force = false) => strip.tick(force),
		destroy: () => strip.destroy(),
	};
}
