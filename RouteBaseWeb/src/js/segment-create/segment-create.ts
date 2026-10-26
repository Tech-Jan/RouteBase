// segment-create.ts — Segment creation page: drawing session, routing, save.
// The MapLibre engine lives in segCreateMap on MapShell (registry style switcher
// + bfcache recovery). This file owns drawing state, routing orchestration,
// stats and save — no engine/DOM-map calls of its own.

import { asyncCallback } from '../core/async-listener';
import { must } from '../core/invariant';
import { createMapShell } from '../core/map-shell';
import { defaultBasemapId } from '../core/basemap-registry';
import type { ActivityGpsPointDto } from '../core/core-types';
import { segCreateMap } from './segment-create-map';
import { fetchRoute, type Waypoint } from './segment-create-routing';
import {
	startRoute,
	extendRoute,
	routeDistanceMeters,
	buildSavePayload,
	createLegQueue,
	exceedsPointCap,
	type RoutePoint,
} from './segment-create-route-state';
import type { CreatedSegmentResponse } from '../generated/contracts';

// ==================== STATE ====================

	let isDrawing = false;
	let waypoints: Waypoint[] = [];
	let fullRoutePoints: RoutePoint[] = [];
	let previewTimer: ReturnType<typeof setTimeout> | undefined = undefined;
	const legQueue = createLegQueue(); // SG2-01: serialize + invalidate route-leg fetches
	let previewEpoch = 0;              // SG2-04: drop a stale preview fetch that outlived a newer move

	// ==================== MAP SHELL ====================

	const shell = createMapShell({
		containerId: "map",
		styleSwitcher: {
			containerId: "segCreateStyleSwitcher",
			btnClass: "btn-cockpit btn-cockpit-ghost btn-cockpit-sm",
		},
		deferInit: true,
		handleBfcache: true,
		createMapLibre(containerId, styleUrl, savedCam, basemapId) {
			segCreateMap.init(containerId, styleUrl, savedCam, basemapId);
			return segCreateMap.getMap();
		},
		onMapLibreReady() {
			rearmDrawing();
		},
		switchMapLibreStyle(_m, styleUrl, basemapId) {
			segCreateMap.setStyle(styleUrl, basemapId);
		},
		destroyMapLibre() {
			segCreateMap.destroy();
		},
	});

	const defaultId = defaultBasemapId(); // keyless page → 'liberty'
	shell.switchToBasemap(defaultId);
	shell.setActiveBasemap(defaultId);

	// ==================== ACTIVITY LOADING ====================

	document
		.getElementById("activitySelect")
		?.addEventListener("change", asyncCallback('load activity gps', async function (this: HTMLSelectElement) {
			const activityId = this.value;
			segCreateMap.setActivity([]);

			if (!activityId) return;

			try {
				const response = await fetch(`/api/activities/${activityId}/gps`);
				if (!response.ok) throw new Error(`HTTP ${response.status}`);

				const gpsData: ActivityGpsPointDto[] = await response.json();
				if (gpsData && gpsData.length > 0) {
					const coords: [number, number][] = gpsData.map(
						(p) => [p.gpsLat, p.gpsLong] as [number, number],
					);
					segCreateMap.setActivity(coords);
					segCreateMap.fitToActivity();
				}
			} catch (error: unknown) {
				console.error("Error loading activity:", error);
				const msg = error instanceof Error ? error.message : String(error);
				alert("Failed to load activity: " + msg);
			}
		}));

	// ==================== DRAWING CONTROLS ====================

	document.getElementById("btnDraw")?.addEventListener("click", () => {
		isDrawing ? finishDrawing() : startDrawing();
	});

	document.getElementById("btnClear")?.addEventListener("click", clearAll);

	function startDrawing(): void {
		isDrawing = true;
		legQueue.invalidate(); // SG2-01: drop any leg fetch from a prior session
		waypoints = [];
		fullRoutePoints = [];
		segCreateMap.clearAll();

		const btnDraw = document.getElementById("btnDraw");
		if (btnDraw) {
			btnDraw.innerHTML = '<i class="bi bi-check"></i> Finish Drawing';
			btnDraw.className = "btn-cockpit btn-cockpit-amber";
		}

		segCreateMap.onClick(asyncCallback('draw click', handleClick));
		segCreateMap.onMouseMove(handleMove);
		segCreateMap.setDrawingCursor(true);
	}

	function finishDrawing(): void {
		isDrawing = false;
		segCreateMap.offClick();
		segCreateMap.offMouseMove();
		segCreateMap.setDrawingCursor(false);
		clearTimeout(previewTimer); // kill a pending preview so it can't paint after finish
		segCreateMap.clearPreview();

		const btnDraw = document.getElementById("btnDraw");
		if (btnDraw) {
			btnDraw.innerHTML = '<i class="bi bi-pencil"></i> Draw Segment';
			btnDraw.className = "btn-cockpit btn-cockpit-primary";
		}

		if (fullRoutePoints.length >= 2 && !exceedsPointCap(fullRoutePoints)) {
			(document.getElementById("btnSave") as HTMLButtonElement).disabled = false;
		}
	}

	function clearAll(): void {
		legQueue.invalidate(); // SG2-01: a leg still in flight must not re-insert into the cleared route
		waypoints = [];
		fullRoutePoints = [];
		segCreateMap.clearAll();
		updateStats();
		(document.getElementById("btnSave") as HTMLButtonElement).disabled = true;
		if (isDrawing) finishDrawing();
	}

	// Re-bind drawing to a fresh Map instance (first init / bfcache). Layer + state
	// replay is automatic (module state); only the handlers need re-arming.
	function rearmDrawing(): void {
		if (!isDrawing) return;
		segCreateMap.onClick(asyncCallback('draw click', handleClick));
		segCreateMap.onMouseMove(handleMove);
		segCreateMap.setDrawingCursor(true);
	}

	// ==================== MAP INTERACTION ====================

	function handleMove(lat: number, lng: number): void {
		if (!isDrawing || waypoints.length === 0) {
			segCreateMap.clearPreview();
			return;
		}

		clearTimeout(previewTimer);
		previewTimer = setTimeout(
			asyncCallback('segment preview', () => {
				// Re-check at fire time: a Clear between schedule and fire empties the array.
				const anchor = waypoints[waypoints.length - 1];
				return anchor === undefined ? Promise.resolve() : showPreview(anchor, lat, lng);
			}),
			150,
		);
	}

	async function handleClick(lat: number, lng: number): Promise<void> {
		if (!isDrawing) return;

		const newPoint: Waypoint = { lat, lng };
		waypoints.push(newPoint);
		segCreateMap.setWaypoints(waypoints.map((w) => [w.lat, w.lng]));
		segCreateMap.clearPreview();

		if (waypoints.length >= 2) {
			// SG2-01: route the leg through legQueue so overlapping fetches apply in click order and a
			// leg that outlives a Clear is dropped. routeLeg returns coords; application happens here.
			const start = must(waypoints[waypoints.length - 2], 'waypoints[-2] (length>=2 guarded)');
			const end = must(waypoints[waypoints.length - 1], 'waypoints[-1] (length>=2 guarded)');
			const coords = await legQueue.run(() => routeLeg(start, end));
			if (coords) {
				fullRoutePoints = extendRoute(fullRoutePoints, coords);
				updateDisplay();
			}
		} else {
			fullRoutePoints = startRoute(newPoint.lat, newPoint.lng);
			updateDisplay();
		}
	}

	// ==================== ROUTING ====================

	async function showPreview(start: Waypoint, lat: number, lng: number): Promise<void> {
		const myEpoch = ++previewEpoch; // SG2-04: only the latest preview should apply
		const snapEnabled = (document.getElementById("snapToRoads") as HTMLInputElement).checked;

		if (!snapEnabled) {
			segCreateMap.setPreview([[start.lat, start.lng], [lat, lng]], true);
			return;
		}

		const coords = await fetchRoute(start, { lat, lng });
		if (myEpoch !== previewEpoch) return; // superseded by a newer mouse move
		if (coords && coords.length > 0) {
			segCreateMap.setPreview(coords, false);
		} else {
			segCreateMap.setPreview([[start.lat, start.lng], [lat, lng]], true);
		}
	}

	// SG2-01: returns the leg's coords (routed, or straight/route-failure fallback) — the caller
	// applies them via legQueue so ordering + invalidation are honored. No module-state mutation here.
	async function routeLeg(start: Waypoint, end: Waypoint): Promise<[number, number][]> {
		const straight: [number, number][] = [
			[start.lat, start.lng],
			[end.lat, end.lng],
		];
		const snapEnabled = (document.getElementById("snapToRoads") as HTMLInputElement).checked;
		if (!snapEnabled) return straight;

		const coords = await fetchRoute(start, end);
		return coords && coords.length > 0 ? coords : straight;
	}

	// ==================== DISPLAY ====================

	function updateDisplay(): void {
		segCreateMap.setRoute(fullRoutePoints.map((p) => [p.lat, p.lng]));
		updateStats();
	}

	function updateStats(): void {
		(document.getElementById("pointsDisplay") as HTMLElement).textContent = String(
			fullRoutePoints.length,
		);

		if (fullRoutePoints.length >= 2) {
			const distance = routeDistanceMeters(fullRoutePoints);
			(document.getElementById("distanceDisplay") as HTMLElement).textContent = (
				distance / 1000
			).toFixed(2);
		} else {
			(document.getElementById("distanceDisplay") as HTMLElement).textContent = "--";
		}

		// SG2-05: warn + block save the moment the route exceeds the server's cap, while still drawing,
		// instead of a late opaque 400 after the user has already named the segment and clicked Save.
		const overCap = exceedsPointCap(fullRoutePoints);
		const hint = document.getElementById("pointsCapHint");
		if (hint) (hint as HTMLElement).hidden = !overCap;
		if (overCap) (document.getElementById("btnSave") as HTMLButtonElement).disabled = true;
	}

	// ==================== SAVE ====================

	document.getElementById("segmentForm")?.addEventListener("submit", asyncCallback('save segment', async (e: Event) => {
		e.preventDefault();

		const btnSave = document.getElementById("btnSave") as HTMLButtonElement;
		if (btnSave.disabled) return; // SG2-02: a save is already in flight — ignore the duplicate submit

		const segmentName = (document.getElementById("segmentName") as HTMLInputElement).value;
		const activityType = (document.getElementById("activityType") as HTMLSelectElement).value;

		const statusDiv = document.getElementById("saveStatus") as HTMLElement;

		// The page already owns a status line for save outcomes — say it there rather than in a modal
		// that covers the map the user has to draw on to fix it.
		if (fullRoutePoints.length < 2) {
			statusDiv.style.display = "block";
			statusDiv.className = "seg-create-status seg-create-status--error";
			statusDiv.textContent = "Draw a segment with at least 2 points first.";
			return;
		}

		btnSave.disabled = true; // guard the whole in-flight window; clearAll() leaves it disabled on success
		statusDiv.style.display = "block";
		statusDiv.className = "seg-create-status seg-create-status--info";
		statusDiv.textContent = "Saving segment…";

		try {
			const response = await fetch("/api/SegmentsApi", {
				method: "POST",
				headers: { "Content-Type": "application/json" },
				body: JSON.stringify(buildSavePayload(segmentName, activityType, fullRoutePoints)),
			});

			if (response.ok) {
				// SA-09 (SS-05): analysisQueued=false → segment saved but background analysis couldn't
				// be enqueued (queue busy); efforts absent until re-analyzed. Surface it instead of a
				// plain success (segmentId is a server int — safe interpolation, same as the success line).
				const segment: CreatedSegmentResponse = await response.json();
				if (segment.analysisQueued === false) {
					statusDiv.className = "seg-create-status seg-create-status--error";
					// eslint-disable-next-line no-unsanitized/property -- segmentId is a server int (typed number)
					statusDiv.innerHTML = `<strong>Segment created, but analysis could not be queued (queue busy).</strong> Open <a href="/Segments/Statistics?id=${segment.segmentId}">Statistics</a> and press Re-analyze.`;
				} else {
					statusDiv.className = "seg-create-status seg-create-status--success";
					// eslint-disable-next-line no-unsanitized/property -- segmentId is a server int (typed number)
					statusDiv.innerHTML = `<strong>Segment created.</strong> <a href="/Segments/Statistics?id=${segment.segmentId}">View Statistics</a>`;
				}

				(document.getElementById("segmentForm") as HTMLFormElement).reset();
				clearAll();
			} else {
				const error = await response.json();
				statusDiv.className = "seg-create-status seg-create-status--error";
				// ApiError is {code, message} — the server's four distinct BadRequest messages surface here.
				statusDiv.textContent = `Error: ${error.message || "Failed to save segment"}`;
				btnSave.disabled = false; // let the user retry after a validation error
			}
		} catch (error: unknown) {
			console.error("Error:", error);
			statusDiv.className = "seg-create-status seg-create-status--error";
			statusDiv.textContent = "Error: Failed to save segment";
			btnSave.disabled = false; // let the user retry after a network error
		}
	}));
