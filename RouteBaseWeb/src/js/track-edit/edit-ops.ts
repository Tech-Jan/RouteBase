/**
 * edit-ops.ts — the op/script/stamp/session wire types (A1 §2.1/§2.4/§2.6/§2.7).
 *
 * Mirror of the C# records in Services/TrackEdit/EditOps.cs + TrackEditContracts.cs. Contract changes
 * here are atomic with that file (invariant 8) and freeze once I1 lands. The `op` discriminator values
 * are the grep keys — never build them from fragments.
 */

// The union is GENERATED (D1 wire-enum stamp on the C# TimeModes const class) — retired here in
// G1; the rest of this file stays the hand-frozen chain (invariant 8).
export type TimeMode = import('../generated/contracts').TimeMode;

// gapSeconds: null and absent BOTH occur on the wire — the MVC session GET echoes the script with
// explicit nulls (no DefaultIgnoreCondition), while the session store strips them (TrackEditJson
// WhenWritingNull). Guard with `!= null`, never `!== undefined`.
export type EditOp =
	| { op: 'loadActivity'; activityId: number }
	| { op: 'cropTo'; slot: number; from: number; to: number }
	| { op: 'deleteRange'; slot: number; from: number; to: number; timeMode: TimeMode; gapSeconds?: number | null }
	| { op: 'splitAt'; slot: number; index: number }
	| { op: 'append'; target: number; source: number; timeMode: TimeMode; gapSeconds?: number | null };

export interface EditScript {
	schemaVersion: 1;
	ops: EditOp[];
}

/** Per-source version stamp (invariant 1); mirror of C# TrackVersionStamp. */
export interface TrackVersionStamp {
	activityId: number;
	pointCount: number;
	blobSha256: string;
}

/** Own-session API payload (no userId — server scopes to the caller); mirror of C# EditSessionSnapshot. */
export interface EditSessionState {
	sessionId: string;
	script: EditScript;
	cursor: number;
	stamps: TrackVersionStamp[];
	status: 'active' | 'saved' | 'discarded';
}
