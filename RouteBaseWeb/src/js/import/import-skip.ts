// import-skip.ts — how the import reports a file it is NOT importing.
//
// Skipping is always the right call: re-importing a ride you merged away would double-count its distance.
// What was wrong was the message. "Duplicate" about a ride sitting in your trash says "you already have this,
// the file is redundant" — when in fact the ride is one click from coming back, and you have no way to find
// it. That is precisely the dead end the merge incident left behind.
//
// Pure — no DOM, so it is unit-testable without mocks (import.ts wires the DOM at module load).

/** Outcome codes the server sends for a file it did not import. Mirrors C# ImportSkipReason. */
export const SKIP_OUTCOMES = ['skipped', 'in-trash', 'in-work'] as const;

/** Every outcome that means "already imported, not importing it again". */
export function isSkip(outcome: string): boolean {
	return (SKIP_OUTCOMES as readonly string[]).includes(outcome);
}

/** The badge for a skip. The tooltip (detail) names the ride so it can actually be found again. */
export function skipBadge(outcome: string): string {
	if (outcome === 'in-trash') return 'In trash — skipped';
	if (outcome === 'in-work') return 'Awaiting review — skipped';
	return 'Duplicate — skipped';
}
