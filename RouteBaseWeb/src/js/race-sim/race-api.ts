/**
 * The race wire contract. The columnar replay payload is generated from Services/Races/RaceReplayDtos.cs —
 * the replay endpoint still hand-serializes + ETags a FileResult, but it now declares its schema via
 * [ProducesResponseType] metadata (D14 closed, Phase 2), so the shapes live in the OpenAPI doc; the field
 * docs (leg semantics, officialFinishSeconds, columnar rationale) live on the C# records. The CRUD-endpoint
 * bodies (checkpoint row, race summary, clear/paint/import results) were generated already.
 *
 * Columnar on purpose: a 3,892-node course is 4 flat arrays, not 3,892 objects. And the course is sent
 * ONCE for the whole simulated field — each simulated rider adds only his checkpoint times, and the
 * browser runs the pacing model over the shared grid to get his ride. Only real riders ship geometry.
 *
 * `RaceParticipantDto.kind` widens to `string` (was the hand refinement 'real' | 'simulated'): every
 * consumer only compares against the literals, none stores or narrows on the union.
 */

// Generated DTOs; imported so the `as` casts below bind to the source of truth, re-exported for consumers.
import type {
	RaceCheckpointRowDto,
	RaceSummaryDto,
	RaceClearFieldResultDto,
	RacePaintResultDto,
	RaceEmphasisResultDto,
	RaceImportResultDto,
	RaceReplayPayload,
	RaceParticipantDto,
	ShareRaceReplayPayload,
	ApiError,
	UpdateCheckpointRequest,
	RaceImportPreviewDto,
	RaceCsvFieldDto,
	RaceSplitColumnDto,
	RaceImportProfileDto,
	RaceWaveResultDto,
} from '../generated/contracts';

export type {
	RaceCourseDto, RaceCheckpointDto, RaceSplitDto, RaceRealTrackDto, RaceParticipantDto, RaceReplayPayload,
} from '../generated/contracts';

// G2: each response body is BOUND to its endpoint via a ResponseOf-annotated binding (no `as` on
// wire data anywhere in this module); a wrong path/verb fails tsc. (Resolves TODO(openapi-review) D29.)
import type { ResponseOf } from '../generated-helpers/endpoint-types';

/**
 * A 200 does not mean a whole body.
 *
 * When the server throws while WRITING the response — which is exactly what System.Text.Json does when a
 * non-finite double reaches a DTO — the status line and the first chunks are already gone. The body then
 * just stops: Chrome reports ERR_INCOMPLETE_CHUNKED_ENCODING, `response.ok` is `true`, and reading the body
 * fails with something that names neither the server nor the cause ("Unexpected end of JSON input", or a
 * bare "Failed to fetch").
 *
 * That has now cost a hand-debug in the browser twice. The client cannot prevent the server's bug, but it
 * MUST be able to name it — a half-sent body is a server fault, and it is not a 404 and not a dropped
 * connection.
 */
function incompleteResponse(subject: string, cause?: unknown): Error {
	return new Error(
		`${subject}: the server sent an incomplete response. It began replying and then failed `
			+ 'part-way through — most likely the race carries a value it cannot serialize. Check the server log.',
		cause === undefined ? undefined : { cause },
	);
}

export async function fetchRaceReplay(raceId: number, signal?: AbortSignal): Promise<RaceReplayPayload> {
	const response = await fetch(`/api/RacesApi/${raceId}/replay`, { signal: signal ?? null });
	if (!response.ok) throw new Error(`Race ${raceId} replay failed: ${response.status}`);

	let body: string;
	try {
		body = await response.text();
	} catch (cause) {
		// The body STREAM died — the server hung up mid-write. An abort is the caller's own doing, though,
		// and must not be dressed up as a server fault.
		if (signal?.aborted) throw cause;
		throw incompleteResponse(`Race ${raceId}`, cause);
	}

	try {
		return JSON.parse(body) as ResponseOf<'/api/RacesApi/{raceId}/replay', 'get'>;
	} catch (cause) {
		// The stream completed but the JSON does not close — the same failure, buffered rather than chunked.
		throw incompleteResponse(`Race ${raceId}`, cause);
	}
}

/**
 * The public replay, from the anonymous share surface: `GET /s/{token}/api/race/replay`.
 *
 * A second function rather than an `apiBase` parameter on `fetchRaceReplay`, because the two endpoints
 * return two different named DTOs: the share family is where the server decides what a stranger receives
 * (see Models/Sharing/ShareRaceContracts.cs — entrants travel with their identity, but no rider's GPS but
 * the owner's). One function returning "whichever of the two" would have to widen its return type to their
 * union and lose exactly the guarantee the split buys. Everything downstream still sees one shape, via the
 * adapter below.
 *
 * Only the READ is parameterized. The nine CRUD writes in this file stay hardcoded to /api/RacesApi and
 * owner-only — they are never called from a share, and the share surface is GET-only by test.
 */
export async function fetchShareRaceReplay(
	apiBase: string, signal?: AbortSignal,
): Promise<RaceReplayPayload> {
	const response = await fetch(`${apiBase}/replay`, { signal: signal ?? null });
	// A share miss is always a 404 — revoked, expired, wrong URL and rate-limited are deliberately
	// indistinguishable — so there is one message for all of them.
	if (!response.ok) throw new Error('This link is no longer available.');

	let body: string;
	try {
		body = await response.text();
	} catch (cause) {
		if (signal?.aborted) throw cause;
		throw incompleteResponse('This race', cause);
	}

	try {
		const shared = JSON.parse(body) as ResponseOf<'/s/{shareToken}/api/race/replay', 'get'>;
		return toReplayPayload(shared);
	} catch (cause) {
		throw incompleteResponse('This race', cause);
	}
}

/**
 * Share payload → the shape every race-sim module already speaks.
 *
 * Pure, and deliberately the ONLY place the two shapes meet: the render modules must not learn that a
 * "share participant" exists, or every future one grows a second code path. Field for field now, because
 * the two payloads differ in WHICH participants they carry rather than in what a participant is.
 */
export function toReplayPayload(shared: ShareRaceReplayPayload): RaceReplayPayload {
	return {
		raceId: shared.raceId,
		raceName: shared.raceName,
		course: shared.course,
		checkpoints: shared.checkpoints,
		costCurve: shared.costCurve,
		costCurveMinBin: shared.costCurveMinBin,
		participants: shared.participants.map((p): RaceParticipantDto => ({
			participantId: p.participantId,
			kind: p.kind,
			name: p.name,
			bib: p.bib,
			category: p.category,
			gender: p.gender,
			club: p.club,
			seed: p.seed,
			color: p.color,
			emphasised: p.emphasised,
			calloutOff: p.calloutOff,
			officialFinishSeconds: p.officialFinishSeconds,
			// The published result travels with the rest of the start list — a DNF is what the EVENT
			// published about its own race, not someone's private data. See ShareRaceParticipantDto.
			finishStatus: p.finishStatus,
			finishStatusRaw: p.finishStatusRaw,
			// When he started is a property of the EVENT's schedule, not of him — the share carries it so
			// a visitor can watch a time trial the way it actually unfolded.
			startOffsetSeconds: p.startOffsetSeconds,
			wave: p.wave,
			discipline: p.discipline,
			splits: p.splits,
			track: p.track,
		})),
	};
}

/** One checkpoint as the CRUD endpoints return it (no `node` — that is a replay-payload concern).
 *  Generated from C# RaceCheckpointRowDto (Services/Races/RaceCrudDtos.cs). */
export type { RaceCheckpointRowDto };

/**
 * Throw with whatever the server actually said.
 *
 * The race endpoints answer a rejection with ApiError `{ code, message }` (D31/D32, RacesApiController),
 * and the message strings are the whole point — "Start and Finish cannot be moved or removed",
 * "Colour must be #rrggbb".
 * Swallowing them for a bare status code would leave the user staring at a 400.
 */
async function fail(response: Response): Promise<never> {
	const body = await response.text();
	let message = body || `HTTP ${response.status}`;
	try {
		const parsed = JSON.parse(body) as Partial<ApiError>;
		if (parsed?.message) message = parsed.message;
	} catch {
		// Not JSON. The raw body is the best we have.
	}
	throw new Error(message);
}

export async function addCheckpoint(
	raceId: number, name: string, chainageMeters: number,
): Promise<RaceCheckpointRowDto> {
	const response = await fetch(`/api/RacesApi/${raceId}/checkpoints`, {
		method: 'POST',
		headers: { 'Content-Type': 'application/json' },
		body: JSON.stringify({ name, chainageMeters }),
	});
	if (!response.ok) await fail(response);
	const body: ResponseOf<'/api/RacesApi/{raceId}/checkpoints', 'post'> = await response.json();
	return body;
}

/**
 * Rename, move or re-label a checkpoint. Absent fields are LEFT ALONE — a rename must not have to
 * restate the chainage, or a stale field in the client silently overwrites the other. Generated,
 * request-relaxed to all-optional (D26), so that omission semantics types truthfully.
 */
export type RaceCheckpointEdit = UpdateCheckpointRequest;

export async function updateCheckpoint(
	raceId: number, checkpointId: number, edit: RaceCheckpointEdit,
): Promise<RaceCheckpointRowDto> {
	const response = await fetch(`/api/RacesApi/${raceId}/checkpoints/${checkpointId}`, {
		method: 'PUT',
		headers: { 'Content-Type': 'application/json' },
		body: JSON.stringify(edit),
	});
	if (!response.ok) await fail(response);
	const body: ResponseOf<'/api/RacesApi/{raceId}/checkpoints/{checkpointId}', 'put'> = await response.json();
	return body;
}

export async function deleteCheckpoint(raceId: number, checkpointId: number): Promise<void> {
	const response = await fetch(`/api/RacesApi/${raceId}/checkpoints/${checkpointId}`, { method: 'DELETE' });
	if (!response.ok) await fail(response);
}

/** Delete a race outright — course link, checkpoints, field and results. There is no race trash. */
export async function deleteRace(raceId: number): Promise<void> {
	const response = await fetch(`/api/RacesApi/${raceId}`, { method: 'DELETE' });
	if (!response.ok) await fail(response);
}

/** A race as the list/create/duplicate endpoints summarise it. Generated from C# RaceSummaryDto
 *  (Services/Races/RaceCrudDtos.cs). Note create returns null courseDistanceMeters (the length isn't
 *  recomputed on create), while the /Races list returns the real length. */
export type { RaceSummaryDto };

/**
 * "Save as": deep-copy a visible race into a new one you own — same course, checkpoints, field and
 * imported results, named "<source> (copy)". The rides themselves are shared, not duplicated.
 */
export async function duplicateRace(raceId: number): Promise<RaceSummaryDto> {
	const response = await fetch(`/api/RacesApi/${raceId}/duplicate`, { method: 'POST' });
	if (!response.ok) await fail(response);
	const body: ResponseOf<'/api/RacesApi/{raceId}/duplicate', 'post'> = await response.json();
	return body;
}

/** Who may EDIT a race — mirrors RaceEditPolicy.cs. Owner-only to set. */
export type RaceEditPolicyValue = 'owner' | 'friends' | 'public';

/** Set the race's edit policy. Owner-only; the server 404s a non-owner and 400s an unknown value. */
export async function setEditPolicy(raceId: number, policy: RaceEditPolicyValue): Promise<void> {
	const response = await fetch(`/api/RacesApi/${raceId}/edit-policy`, {
		method: 'PUT',
		headers: { 'Content-Type': 'application/json' },
		body: JSON.stringify({ policy }),
	});
	if (!response.ok) await fail(response);
}

/** Generated from C# RaceClearFieldResultDto (Services/Races/RaceCrudDtos.cs). */
export type { RaceClearFieldResultDto };

/** Empty the field — simulated and real riders both. The course and its checkpoints survive. */
export async function clearField(raceId: number): Promise<RaceClearFieldResultDto> {
	const response = await fetch(`/api/RacesApi/${raceId}/participants`, { method: 'DELETE' });
	if (!response.ok) await fail(response);
	const body: ResponseOf<'/api/RacesApi/{raceId}/participants', 'delete'> = await response.json();
	return body;
}

/** Generated from C# RacePaintResultDto (Services/Races/RaceCrudDtos.cs). */
export type { RacePaintResultDto };

/**
 * Paint a group of riders; `color` null clears them back to the default.
 *
 * The whole filtered set goes in one call — the server chunks the ids, because SQLite caps a statement
 * at 999 parameters and a painted 508-team field would blow straight through it.
 */
export async function setParticipantColors(
	raceId: number, participantIds: readonly number[], color: string | null,
): Promise<RacePaintResultDto> {
	const response = await fetch(`/api/RacesApi/${raceId}/participants/color`, {
		method: 'PUT',
		headers: { 'Content-Type': 'application/json' },
		body: JSON.stringify({ participantIds, color }),
	});
	// Through fail(), like every other write here. Raw text() showed the user the JSON envelope itself —
	// `Paint failed: {"error":"Colour must be #rrggbb."}` — which is the two-convention trap fail()'s own
	// doc warns about, in the one function that had opted out of it.
	if (!response.ok) await fail(response);
	const body: ResponseOf<'/api/RacesApi/{raceId}/participants/color', 'put'> = await response.json();
	return body;
}

/** Generated from C# RaceEmphasisResultDto (Services/Races/RaceCrudDtos.cs). */
export type { RaceEmphasisResultDto };

/**
 * Emphasise (or un-emphasise) a group of riders — bigger dots, drawn in front. Same filtered set and the
 * same server-side chunking as the paint above.
 */
export async function setParticipantEmphasis(
	raceId: number, participantIds: readonly number[], emphasised: boolean,
): Promise<RaceEmphasisResultDto> {
	const response = await fetch(`/api/RacesApi/${raceId}/participants/emphasis`, {
		method: 'PUT',
		headers: { 'Content-Type': 'application/json' },
		body: JSON.stringify({ participantIds, emphasised }),
	});
	if (!response.ok) await fail(response);
	const body: ResponseOf<'/api/RacesApi/{raceId}/participants/emphasis', 'put'> = await response.json();
	return body;
}

/** The column mapping the user has chosen. `splitColumns` is keyed by checkpointId — the number key
 *  documents what the id IS, and it is what the form encoding below writes as `splitColumns[12]`. */
export interface RaceCsvMappingBody {
	readonly nameColumn: string;
	readonly finishColumn: string | null;
	readonly bibColumn: string | null;
	readonly categoryColumn: string | null;
	readonly genderColumn: string | null;
	readonly clubColumn: string | null;
	readonly statusColumn: string | null;
	readonly gunTimeColumn: string | null;
	readonly netTimeColumn: string | null;
	readonly startOffsetColumn: string | null;
	readonly waveColumn: string | null;
	readonly disciplineColumn: string | null;
	readonly splitColumns: Record<number, string>;
	readonly perLegTimes: boolean;
}

/** What an import did — generated from C# RaceImportResultDto (Services/Races/RaceCrudDtos.cs). A named
 *  contract, not an inline shape; `finishers` is NOT the same as `imported` (DNFs import but don't finish). */
export type { RaceImportResultDto };

/** What the server made of the file — generated from C# RaceImportPreviewDto. */
export type { RaceImportPreviewDto, RaceCsvFieldDto, RaceSplitColumnDto };

/**
 * The multipart body both import endpoints take.
 *
 * The FILE, not its text: `Blob.text()` is unconditionally UTF-8, so decoding in the browser turned every
 * accented name in a Windows-1252 export into U+FFFD before the server ever saw a byte. Sending the bytes
 * is what let RaceCsvDecoder make that call — and what let this module stop parsing CSV altogether.
 *
 * `Content-Type` is deliberately NOT set: the browser must write it itself so the multipart boundary
 * matches the body it just built.
 */
function importForm(file: File, mapping: RaceCsvMappingBody): FormData {
	const form = new FormData();
	form.append('file', file);
	form.append('nameColumn', mapping.nameColumn);
	form.append('perLegTimes', String(mapping.perLegTimes));
	for (const [key, value] of [
		['finishColumn', mapping.finishColumn], ['bibColumn', mapping.bibColumn],
		['categoryColumn', mapping.categoryColumn], ['genderColumn', mapping.genderColumn],
		['clubColumn', mapping.clubColumn], ['statusColumn', mapping.statusColumn],
		['gunTimeColumn', mapping.gunTimeColumn], ['netTimeColumn', mapping.netTimeColumn],
		['startOffsetColumn', mapping.startOffsetColumn], ['waveColumn', mapping.waveColumn],
		['disciplineColumn', mapping.disciplineColumn],
	] as const) {
		if (value) form.append(key, value);
	}
	// MVC binds a Dictionary<int,string> from `prefix[key]=value`, which is why the ids stay numbers all
	// the way through rather than being flattened into a JSON blob nothing can typecheck.
	for (const [checkpointId, column] of Object.entries(mapping.splitColumns)) {
		form.append(`splitColumns[${checkpointId}]`, column);
	}
	return form;
}

/**
 * What the import WOULD do — nothing is written. Same importer, so the numbers shown are the numbers
 * that will be stored; a preview computed a second way would be a second thing to keep honest.
 */
export async function previewRaceCsv(
	raceId: number, file: File, mapping: RaceCsvMappingBody,
): Promise<RaceImportPreviewDto> {
	const response = await fetch(`/api/RacesApi/${raceId}/import/preview`, {
		method: 'POST',
		body: importForm(file, mapping),
	});
	if (!response.ok) await fail(response);
	const parsed: ResponseOf<'/api/RacesApi/{raceId}/import/preview', 'post'> = await response.json();
	return parsed;
}

/**
 * Assign a start offset to every entrant in a wave — one number per block, keyed by the block's own
 * label. A null value clears that block back to a mass start.
 */
export async function setRaceWaveOffsets(
	raceId: number, offsets: Record<string, number | null>,
): Promise<RaceWaveResultDto> {
	const response = await fetch(`/api/RacesApi/${raceId}/waves`, {
		method: 'PUT',
		headers: { 'Content-Type': 'application/json' },
		body: JSON.stringify({ offsets }),
	});
	if (!response.ok) await fail(response);
	const parsed: ResponseOf<'/api/RacesApi/{raceId}/waves', 'put'> = await response.json();
	return parsed;
}

/** A saved column mapping, resolved onto this race's checkpoints — generated from C# RaceImportProfileDto. */
export type { RaceImportProfileDto };

/** This user's saved mappings, resolved onto THIS race. Per user, not per race — see the C# store. */
export async function listRaceImportProfiles(raceId: number): Promise<RaceImportProfileDto[]> {
	const response = await fetch(`/api/RacesApi/${raceId}/import/profiles`);
	if (!response.ok) await fail(response);
	const parsed: ResponseOf<'/api/RacesApi/{raceId}/import/profiles', 'get'> = await response.json();
	return parsed;
}

export async function saveRaceImportProfile(
	raceId: number, name: string, mapping: RaceCsvMappingBody,
): Promise<void> {
	const response = await fetch(`/api/RacesApi/${raceId}/import/profiles`, {
		method: 'PUT',
		headers: { 'Content-Type': 'application/json' },
		// splitColumns travels as the checkpointId-keyed map it is here; the SERVER converts it to the
		// portable (name + seq) form, because only the server knows this race's checkpoints.
		body: JSON.stringify({ ...mapping, name }),
	});
	if (!response.ok) await fail(response);
}

export async function deleteRaceImportProfile(raceId: number, name: string): Promise<void> {
	const response = await fetch(
		`/api/RacesApi/${raceId}/import/profiles/${encodeURIComponent(name)}`, { method: 'DELETE' });
	if (!response.ok) await fail(response);
}

/**
 * Import a results CSV, replacing the simulated field. Through fail(), like every other write here — the
 * panel used to read the raw body text and show the user the JSON envelope itself
 * (`Import failed: {"error":"Column 'name' is required."}`), the two-convention trap fail()'s doc warns of.
 */
export async function importRaceCsv(
	raceId: number, file: File, mapping: RaceCsvMappingBody,
): Promise<RaceImportResultDto> {
	const response = await fetch(`/api/RacesApi/${raceId}/import`, {
		method: 'POST',
		body: importForm(file, mapping),
	});
	if (!response.ok) await fail(response);
	const parsed: ResponseOf<'/api/RacesApi/{raceId}/import', 'post'> = await response.json();
	return parsed;
}
