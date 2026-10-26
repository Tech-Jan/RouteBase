/**
 * segment-analysis-wait.ts — shared post-create flow for new segments.
 *
 * Creating a segment queues match analysis as a background job, so the segment has no
 * efforts the instant the create POST returns. This helper shows the analysis toast,
 * waits for that analysis to finish, then opens the segment's statistics page — so the
 * user never lands on an empty segment.
 *
 * Frontend-only: the wait loop is page-resident. If the user navigates away mid-analysis
 * the auto-open is simply skipped (analysis still finishes server-side and the toast
 * auto-resumes on the next page) — see op-toast.ts page-load check.
 */

import { startAnalysisPolling } from './op-polling-bridge';

interface QueueProgress {
	operationType: string;
	isRunning: boolean;
	phase: string;
}

const PROGRESS_URL = '/api/queue/progress';
const POLL_INTERVAL_MS = 1000;
const MAX_POLLS = 180; // ~3 min safety cap so a stuck queue can't trap the user

const delay = (ms: number) => new Promise<void>((r) => window.setTimeout(r, ms));

// Wait generation: a second create supersedes the first loop so a stale wait can't
// navigate the user to the older segment once both analyses finish.
let _waitGen = 0;

/**
 * Poll the unified queue-progress endpoint until the queued single-segment analysis
 * reaches a terminal state. Only reacts to `operationType === 'analysis'`, so an import
 * running ahead of the analysis is ignored. Falls back to 'completed' after the safety
 * cap — best-effort, never traps the user on the current page.
 *
 * ID-04 seam: classification is by the queue's *current* status with no job identity, so a
 * stale 'Completed' from a previous analysis can be mistaken for this one's. Not cleanly
 * fixable client-side — the real fix is a per-job-identity progress channel (Backend-Infra,
 * ID-04). Exported so the classification rules can be characterization-tested.
 */
export async function waitForAnalysis(): Promise<'completed' | 'failed'> {
	let sawAnalysis = false;

	for (let i = 0; i < MAX_POLLS; i++) {
		await delay(POLL_INTERVAL_MS);

		let status: QueueProgress | null = null;
		try {
			const r = await fetch(PROGRESS_URL);
			status = r.ok ? await r.json() : null;
		} catch {
			continue;
		}
		if (!status || status.operationType !== 'analysis') continue;

		if (status.isRunning) { sawAnalysis = true; continue; }
		if (status.phase === 'Failed') return 'failed';
		if (status.phase === 'Completed') return 'completed';
		// Not running and not in a terminal phase: if we've already seen it run, the toast
		// has acknowledged completion — treat as done. Otherwise the worker hasn't picked
		// the item up yet, so keep waiting.
		if (sawAnalysis) return 'completed';
	}
	return 'completed';
}

/**
 * Show the analysis toast, wait for the freshly queued single-segment analysis to finish,
 * then navigate to the new segment's statistics page.
 */
export async function openSegmentWhenAnalyzed(segmentId: number): Promise<void> {
	const gen = ++_waitGen;
	startAnalysisPolling();
	const result = await waitForAnalysis();
	if (gen !== _waitGen) return; // superseded by a newer create — let that one navigate
	if (result === 'failed') {
		window.alert('Segment created, but analysis failed. Opening it anyway.');
	}
	window.location.href = '/Segments/Statistics?id=' + segmentId;
}
