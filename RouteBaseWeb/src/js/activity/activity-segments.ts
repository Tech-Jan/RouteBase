/**
 * Activity Details - Custom Segments Module
 * Handles segment creation UI and API calls with collapsible all-efforts display
 */

import { asyncCallback } from '../core/async-listener';
import { createInlineError } from '../core/inline-error';
import { must } from '../core/invariant';
import { readData, readDataNumber, setData } from '../core/data-attrs';
import type { GpsPoint } from '../core/core-types';
import type { ActivitySegmentSummary } from './activity-types';
import type { CreatedSegmentResponse, SegmentEffortSummaryDto } from '../generated/contracts';
import { openSegmentWhenAnalyzed } from "../core/segment-analysis-wait";
import { formatTime } from "../core/format";
import { escapeHtml, getRankingBadge } from "../core/html";
import { getGpsData } from "../core/gps-data";
import { getLockedSplit, setLockedSplit } from "../core/split-state";
import { highlightSplitRange, clearSplitHighlight, setOnChartClick, closeOtherActionPanels, registerActionPanel } from "./details-shared";

const _gpsIdMaps = new WeakMap<object, Map<number, GpsPoint>>();

// D: pull the server's real reason out of a failed CreateSegment response. Error bodies are uniformly
// ApiError { code, message } since D32 — read message, fall back to raw text for anything else.
async function readSegmentError(resp: Response): Promise<string> {
	const raw = await resp.text().catch(() => "");
	try {
		const j = JSON.parse(raw);
		if (j && typeof j === "object" && typeof j.message === "string") return j.message;
		if (typeof j === "string" && j) return j;
	} catch { /* not JSON — use the raw text below */ }
	return raw || "Failed to create segment. Please try again.";
}

export function setupCustomSegments(activityId: number, _maxDistance: number): void {
	const segmentTab0 = document.getElementById("detSegmentTab");
	const segmentCreator0 = document.getElementById("segmentCreator") as HTMLElement | null;
	const createSegmentBtn0 = document.getElementById("createSegmentBtn");
	const cancelSegmentBtn0 = document.getElementById("cancelSegmentBtn");
	const segmentStartKm0 = document.getElementById("segmentStartKm") as HTMLInputElement | null;
	const segmentEndKm0 = document.getElementById("segmentEndKm") as HTMLInputElement | null;
	const segmentName0 = document.getElementById("segmentName") as HTMLInputElement | null;
	const segmentsList0 = document.getElementById("segmentsList");
	const segmentPreview0 = document.getElementById("segmentPreview");

	// DC-07: every element the setup body dereferences must be present, or a TypeError inside the
	// try/catch (and again in the catch's segmentsList.innerHTML) escapes this fire-and-forget
	// call. One combined guard hardens against markup drift.
	if (!segmentTab0 || !segmentCreator0 || !segmentsList0 || !segmentPreview0 ||
		!createSegmentBtn0 || !cancelSegmentBtn0 || !segmentStartKm0 || !segmentEndKm0 || !segmentName0) {
		console.warn("Custom segments elements not found");
		return;
	}
	// Narrowed rebinds (G3): the guard's narrowing does not cross into the closures below, and the
	// alternative was a `!` on every later use. All non-null from here on.
	const segmentTab = segmentTab0, segmentCreator = segmentCreator0, segmentsList = segmentsList0;
	const segmentPreview = segmentPreview0, createSegmentBtn = createSegmentBtn0;
	const cancelSegmentBtn = cancelSegmentBtn0, segmentStartKm = segmentStartKm0;
	const segmentEndKm = segmentEndKm0, segmentName = segmentName0;

	// ── Chart-click segment picker ──────────────────────────────────
	let _pickerStep = 0; // 0 = waiting for start, 1 = waiting for end

	function _updatePickerHints(): void {
		// The prompt lives on its own line (#segPickerHint); the active input's cyan border shows which
		// endpoint the next chart/map click will set.
		const hint = document.getElementById("segPickerHint");
		if (hint)
			hint.innerHTML =
				'<span class="det-badge det-badge--primary">← Click chart or map to set ' +
				escapeHtml(_pickerStep === 0 ? "start" : "end") + "</span>";
		segmentStartKm.classList.toggle("det-seg-input--active", _pickerStep === 0);
		segmentEndKm.classList.toggle("det-seg-input--active", _pickerStep === 1);
	}

	function _onChartClick(distanceKm: number, _index: number): void {
		const val = distanceKm.toFixed(2);
		if (_pickerStep === 0) {
			segmentStartKm.value = val;
			segmentEndKm.value = "";
			_pickerStep = 1;
		} else {
			const start = parseFloat(segmentStartKm.value);
			const end = parseFloat(val);
			if (!isNaN(start) && end < start) {
				segmentEndKm.value = segmentStartKm.value;
				segmentStartKm.value = val;
			} else {
				segmentEndKm.value = val;
			}
			_pickerStep = 0;
		}
		_updatePickerHints();
		updateSegmentPreview();
		updateCreateButtonState();

		const s = parseFloat(segmentStartKm.value);
		const e = parseFloat(segmentEndKm.value);
		if (!isNaN(s) && !isNaN(e) && s < e) {
			highlightSplitRange(s, e);
		}
	}

	function _attachPicker(): void {
		_pickerStep = 0;
		setOnChartClick(_onChartClick);
		_updatePickerHints();
	}

	function _detachPicker(): void {
		setOnChartClick(null);
		const hint = document.getElementById("segPickerHint");
		if (hint) hint.innerHTML = "";
		segmentStartKm.classList.remove("det-seg-input--active");
		segmentEndKm.classList.remove("det-seg-input--active");
	}
	// ─────────────────────────────────────────────────────────────────

	void loadExistingSegments(activityId, segmentsList);

	// Enable the Save button only once a name is present and the range is valid.
	function updateCreateButtonState(): void {
		const startKm = parseFloat(segmentStartKm.value);
		const endKm   = parseFloat(segmentEndKm.value);
		const hasName = segmentName.value.trim().length > 0;
		const rangeOk = !isNaN(startKm) && !isNaN(endKm) && startKm < endKm;
		(createSegmentBtn as HTMLButtonElement).disabled = !(hasName && rangeOk);
	}

	// Create-Segment tab in the shared Details action bar (grouped with Photos, accordion).
	function setSegmentOpen(open: boolean): void {
		segmentCreator.hidden = !open;
		segmentTab.setAttribute("aria-expanded", String(open));
		segmentTab.classList.toggle("active", open);
		if (open) {
			_attachPicker();
			updateCreateButtonState();
		} else {
			segmentStartKm.value = "";
			segmentEndKm.value = "";
			segmentName.value = "";
			segmentPreview.style.display = "none";
			_detachPicker();
			clearSplitHighlight();
		}
	}

	registerActionPanel("segment", () => setSegmentOpen(false));
	segmentTab.addEventListener("click", () => {
		const opening = segmentCreator.hidden !== false;
		if (opening) closeOtherActionPanels("segment");
		setSegmentOpen(opening);
	});

	cancelSegmentBtn.addEventListener("click", () => setSegmentOpen(false));

	function updateSegmentPreview(): void {
		const startKm = parseFloat(segmentStartKm.value);
		const endKm = parseFloat(segmentEndKm.value);

		if (isNaN(startKm) || isNaN(endKm) || startKm >= endKm) {
			segmentPreview.style.display = "none";
			return;
		}

		const gpsDataSource = getGpsData();
		if (gpsDataSource.length === 0) {
			segmentPreview.style.display = "none";
			return;
		}

		const segmentPoints = gpsDataSource.filter(
			(p: GpsPoint) => p.distance >= startKm && p.distance <= endKm,
		);

		if (segmentPoints.length < 2) {
			segmentPreview.style.display = "none";
			return;
		}

		const firstPoint = must(segmentPoints[0], 'length >= 2 (guarded above)');
		const lastPoint  = must(segmentPoints[segmentPoints.length - 1], 'length >= 2 (guarded above)');
		const distance   = lastPoint.distance - firstPoint.distance;

		let ascent = 0;
		for (let i = 1; i < segmentPoints.length; i++) {
			const cur = must(segmentPoints[i], 'i < length (loop-bounded)');
			const prev = must(segmentPoints[i - 1], 'i-1 >= 0 (loop-bounded)');
			const diff = cur.altitude - prev.altitude;
			if (diff > 0) ascent += diff;
		}

		let timeStr = "-";
		let seconds = 0;
		if (firstPoint.time && lastPoint.time) {
			const startTime = new Date("1970-01-01T" + firstPoint.time + "Z");
			const endTime   = new Date("1970-01-01T" + lastPoint.time + "Z");
			seconds = (endTime.getTime() - startTime.getTime()) / 1000;
			if (seconds < 0) seconds += 86400; // DC-08: range crossing midnight (local HH:mm:ss)
			const minutes = Math.floor(seconds / 60);
			const secs    = Math.floor(seconds % 60);
			timeStr = `${minutes}:${secs.toString().padStart(2, "0")}`;
		}

		segmentPreview.style.display = "block";
		const setPreview = (id: string, text: string): void => {
			const el = document.getElementById(id);
			if (el) el.textContent = text;
		};
		setPreview("previewDistance", distance.toFixed(2));
		setPreview("previewAscent", ascent.toFixed(0));
		setPreview("previewTime", timeStr);
		setPreview("previewSpeed", seconds > 0 ? (distance / (seconds / 3600)).toFixed(1) : "-");
	}

	// Validation message for the create form — lives under the inputs, clears when one is edited.
	const formError = createInlineError(segmentCreator.querySelector('.det-seg-form') ?? segmentCreator, {
		fields: [segmentStartKm, segmentEndKm, segmentName],
	});

	segmentStartKm.addEventListener("input", () => { updateSegmentPreview(); updateCreateButtonState(); });
	segmentEndKm.addEventListener("input", () => { updateSegmentPreview(); updateCreateButtonState(); });
	segmentName.addEventListener("input", updateCreateButtonState);

	createSegmentBtn.addEventListener("click", asyncCallback('create segment', async () => {
		const startKm = parseFloat(segmentStartKm.value);
		const endKm   = parseFloat(segmentEndKm.value);
		const name    = segmentName.value.trim();

		// Range + name are already enforced by updateCreateButtonState (the button is disabled until both
		// hold), so these two are unreachable guards, not user-facing messages — they used to alert().
		if (isNaN(startKm) || isNaN(endKm) || startKm >= endKm || !name) {
			console.error("[Segments] Save fired with an invalid range/name", { startKm, endKm, name });
			return;
		}

		const gpsDataSource = getGpsData();
		const segmentPoints = gpsDataSource.filter(
			(p: GpsPoint) => p.distance >= startKm && p.distance <= endKm,
		);

		// Reachable: a well-formed range can still be empty (a gap in the track). Inline, next to the
		// inputs that need changing, and it clears itself as soon as one of them is edited.
		if (segmentPoints.length < 2) {
			formError.show("No GPS points in this range — pick a start and end that the ride passes through.");
			return;
		}
		formError.clear();

		const gpsPoints = segmentPoints.map((p: GpsPoint) => ({
			lat: p.lat,
			lng: p.lng,
			altitude: Math.round(p.altitude),
		}));

		// DC-02: Details has no [data-activity-type] hook — read the type from the header
		// edit-select (owner) or the type badge (non-owner). Both stay fresh after an
		// in-session type edit (activity-edit.ts updates dataset.current + badge text).
		const typeSelect = document.getElementById("activityTypeSelect") as HTMLSelectElement | null;
		const activityType =
			(typeSelect ? readData(typeSelect, "current") : null)
			?? document.getElementById("activityTypeBadge")?.textContent?.trim()
			?? "";

		(createSegmentBtn as HTMLButtonElement).disabled = true;
		createSegmentBtn.innerHTML = '<span class="btn-spinner"></span> Saving...';

		try {
			const response = await fetch("/api/SegmentsApi", {
				method: "POST",
				headers: { "Content-Type": "application/json" },
				body: JSON.stringify({ segmentName: name, activityType, gpsPoints }),
			});

			// The server's refusal is about this form — show it in the form, not in a modal the user
			// has to dismiss before they can act on it.
			if (!response.ok) { formError.show(await readSegmentError(response)); return; }

			const body: CreatedSegmentResponse = await response.json();
			const { segmentId } = body;

			// The backend queues match analysis in the background, so the segment has no
			// efforts yet. Wait for it to finish, then open the new segment's statistics
			// page — otherwise it loads empty.
			await openSegmentWhenAnalyzed(segmentId);
		} catch (error) {
			console.error("Error creating segment:", error);
			formError.show("Could not create the segment — check the connection and try again.");
		} finally {
			(createSegmentBtn as HTMLButtonElement).disabled = false;
			createSegmentBtn.innerHTML = '<i class="bi bi-save"></i> Save';
		}
	}));
}

async function loadExistingSegments(activityId: number, segmentsList: HTMLElement): Promise<void> {
	try {
		const response = await fetch(`/api/SegmentsApi?activityId=${activityId}`);
		if (!response.ok) {
			console.warn("Failed to fetch segments:", response.status);
			return;
		}

		const segments: ActivitySegmentSummary[] = await response.json();

		if (segments.length === 0) {
			segmentsList.innerHTML = `
                <div class="det-notice det-notice--info">
                    <i class="bi bi-info-circle" style="flex-shrink:0;"></i>
                    <span>Create custom segments to track specific sections of your route and compare performance across different activities. Click "Create Segment" to get started.</span>
                </div>`;
			return;
		}

		segmentsList.innerHTML = "";

		const results = await Promise.all(
			segments.map((segment: ActivitySegmentSummary) =>
				buildSegmentCard(segment, activityId),
			),
		);
		for (const card of results) {
			if (card) segmentsList.appendChild(card);
		}
	} catch (error) {
		console.error("Error loading segments:", error);
		segmentsList.innerHTML = `
            <div class="det-notice det-notice--warn">
                <i class="bi bi-exclamation-triangle" style="flex-shrink:0;"></i>
                <span>Unable to load segments. Please try refreshing the page.</span>
            </div>`;
	}
}

const _hoverThrottle = new WeakMap<HTMLElement, number>();
const _HOVER_THROTTLE_MS = 80;

async function buildSegmentCard(
	segment: ActivitySegmentSummary,
	currentActivityId: number,
): Promise<HTMLElement | null> {
	try {
		const effortsResponse = await fetch(
			`/api/SegmentsApi/${segment.segmentId}/efforts-detailed`,
		);
		const efforts: SegmentEffortSummaryDto[] = effortsResponse.ok ? await effortsResponse.json() : [];

		const currentEffort = efforts.find((e: SegmentEffortSummaryDto) => e.activityId === currentActivityId);
		if (!currentEffort) return null;

		const sortedEfforts = efforts.sort(
			(a: SegmentEffortSummaryDto, b: SegmentEffortSummaryDto) => a.elapsedTime - b.elapsedTime,
		);
		const ranking = sortedEfforts.findIndex(
			(e: SegmentEffortSummaryDto) => e.activityId === currentActivityId,
		) + 1;

		const gpsDataSource: GpsPoint[] = getGpsData();
		let startDist = 0;
		let endDist   = 0;

		if (gpsDataSource.length > 0 && currentEffort.gpsIdEntry && currentEffort.gpsIdExit) {
			if (!_gpsIdMaps.has(gpsDataSource)) {
				const idMap = new Map();
				for (const gp of gpsDataSource) {
					if (gp.gpsId != null) idMap.set(gp.gpsId, gp);
				}
				_gpsIdMaps.set(gpsDataSource, idMap);
			}
			const idMap = must(_gpsIdMaps.get(gpsDataSource), 'idMap set above (has+set)');
			const startPoint = idMap.get(currentEffort.gpsIdEntry) || must(gpsDataSource[0], 'length > 0 (guarded)');
			const endPoint   = idMap.get(currentEffort.gpsIdExit)  || must(gpsDataSource[gpsDataSource.length - 1], 'length > 0 (guarded)');
			startDist = startPoint.distance || 0;
			endDist   = endPoint.distance   || (segment.segmentDistance ?? 0) / 1000;
		}

		const card = document.createElement("div");
		card.className = "det-seg-card";
		setData(card, 'segmentId', String(segment.segmentId));
		setData(card, 'segmentStart', startDist.toFixed(2));
		setData(card, 'segmentEnd', endDist.toFixed(2));
		const effortsId = `seg-efforts-${segment.segmentId}`;

		const bestTime = must(sortedEfforts[0], 'currentEffort found above implies non-empty efforts').elapsedTime;
		const avgTime  = efforts.reduce(
			(sum: number, e: SegmentEffortSummaryDto) => sum + e.elapsedTime, 0,
		) / efforts.length;

		const matchQuality = currentEffort.matchPercentage || 0;
		const matchVariant = matchQuality >= 80 ? "det-badge--success"
		                   : matchQuality >= 50 ? "det-badge--warn"
		                   : "det-badge--danger";

		// eslint-disable-next-line no-unsanitized/property -- user content escapeHtml'd inline; rest is numeric metrics + internal badge/variant literals
		card.innerHTML = `
            <div class="det-seg-card-header">
                <span class="det-seg-card-title">
                    <i class="bi bi-flag-fill" style="color:var(--acc-cyan);"></i>
                    ${escapeHtml(segment.segmentName)}
                </span>
                <span class="det-seg-card-loc">
                    <i class="bi bi-geo-alt"></i> ${startDist.toFixed(1)} – ${endDist.toFixed(1)} km
                </span>
            </div>
            <div class="det-seg-card-body">
                <div class="det-seg-card-stats">
                    <div><strong>Distance:</strong> ${(currentEffort.distance / 1000).toFixed(2)} km</div>
                    <div><strong>Time:</strong> ${formatTime(currentEffort.elapsedTime)}</div>
                    <div><strong>Avg Speed:</strong> ${((currentEffort.averageSpeed || 0) * 3.6).toFixed(1)} km/h</div>
                    ${currentEffort.averageHeartRate ? `<div><strong>Avg HR:</strong> ${Math.round(currentEffort.averageHeartRate)} bpm</div>` : ""}
                    <div><strong>Elevation:</strong> ${Math.round(currentEffort.elevation)} m</div>
                    <div><strong>Match Quality:</strong> <span class="det-badge ${matchVariant}">${matchQuality}%</span></div>
                    <div><strong>Ranking:</strong> ${getRankingBadge(ranking)} of ${efforts.length}</div>
                    <div><strong>Segment:</strong> ${((segment.segmentDistance ?? 0) / 1000).toFixed(2)} km</div>
                </div>

                <button class="btn-cockpit btn-cockpit-ghost btn-cockpit-sm js-seg-efforts-toggle"
                        type="button" data-target="${effortsId}">
                    <i class="bi bi-list-ul"></i> View All ${efforts.length} Efforts
                </button>

                <div class="det-seg-card-efforts" id="${effortsId}">
                    <div style="overflow-x:auto; margin-top:10px;">
                        <table class="det-seg-efforts-table">
                            <thead>
                                <tr>
                                    <th>Rank</th>
                                    <th>Date</th>
                                    <th>Time</th>
                                    <th>Speed</th>
                                    <th>HR</th>
                                    <th>Elevation</th>
                                    <th>Match %</th>
                                </tr>
                            </thead>
                            <tbody>
                                ${sortedEfforts.map((e: SegmentEffortSummaryDto, idx: number) => {
                                    const isCurrent = e.activityId === currentActivityId;
                                    const rowClass  = isCurrent ? "det-row--current" : "";
                                    const rankIcon  = idx === 0 ? "🥇" : idx === 1 ? "🥈" : idx === 2 ? "🥉" : "";
                                    const mVariant  = e.matchPercentage >= 80 ? "det-badge--success"
                                                    : e.matchPercentage >= 50 ? "det-badge--warn"
                                                    : "det-badge--muted";
                                    return `
                                        <tr class="${rowClass}">
                                            <td>${rankIcon}#${idx + 1}</td>
                                            <td>${e.activityDate ? new Date(e.activityDate).toLocaleDateString() : "-"}</td>
                                            <td><strong>${formatTime(e.elapsedTime)}</strong></td>
                                            <td>${((e.averageSpeed || 0) * 3.6).toFixed(1)} km/h</td>
                                            <td>${e.averageHeartRate ? Math.round(e.averageHeartRate) + " bpm" : "-"}</td>
                                            <td>${Math.round(e.elevation)} m</td>
                                            <td><span class="det-badge ${mVariant}">${e.matchPercentage || 0}%</span></td>
                                        </tr>`;
                                }).join("")}
                            </tbody>
                        </table>
                    </div>
                    <div class="det-seg-card-summary">
                        <strong>Best:</strong> ${formatTime(bestTime)} &nbsp;|&nbsp;
                        <strong>Average:</strong> ${formatTime(avgTime)}
                    </div>
                </div>

                <div class="det-seg-card-footer">
                    <a href="/Segments/Statistics?id=${segment.segmentId}"
                       class="btn-cockpit btn-cockpit-ghost btn-cockpit-sm">
                        <i class="bi bi-graph-up"></i> View Full Statistics
                    </a>
                </div>
            </div>`;

		// DC-04: wire the efforts-toggle + footer link instead of string-built inline onclick.
		card.querySelector<HTMLElement>(".js-seg-efforts-toggle")?.addEventListener("click", (e) => {
			const target = readData(e.currentTarget as HTMLElement, 'target');
			if (target) document.getElementById(target)?.classList.toggle("det-visible");
			e.stopPropagation();
		});
		card.querySelector<HTMLElement>(".det-seg-card-footer a")?.addEventListener("click", (e) => e.stopPropagation());

		card.addEventListener("mouseenter", function (this: HTMLElement) {
			const now  = performance.now();
			const last = _hoverThrottle.get(this) ?? 0;
			if (now - last < _HOVER_THROTTLE_MS) return;
			_hoverThrottle.set(this, now);

			const s = readDataNumber(this, 'segmentStart');
			const e = readDataNumber(this, 'segmentEnd');
			highlightSplitRange(s, e);
		});

		card.addEventListener("mouseleave", function (this: HTMLElement) {
			if (!this.classList.contains("det-seg-card--locked")) {
				if (!getLockedSplit()) {
					clearSplitHighlight();
				}
			}
		});

		card.addEventListener("click", function (this: HTMLElement, e: Event) {
			if ((e.target as HTMLElement).closest("button, a")) return;

			const s = readDataNumber(this, 'segmentStart');
			const end = readDataNumber(this, 'segmentEnd');

			if (this.classList.contains("det-seg-card--locked")) {
				this.classList.remove("det-seg-card--locked");
				setLockedSplit(null);
				clearSplitHighlight();
			} else {
				document.querySelectorAll(".det-seg-card").forEach(c => c.classList.remove("det-seg-card--locked"));
				this.classList.add("det-seg-card--locked");
				setLockedSplit({ start: s, end });
				highlightSplitRange(s, end);
			}
		});

		return card;
	} catch (error) {
		console.error(`Error building segment card ${segment.segmentName}:`, error);
		return null;
	}
}
