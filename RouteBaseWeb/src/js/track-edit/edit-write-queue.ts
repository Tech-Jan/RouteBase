/**
 * edit-write-queue.ts — optimistic write-through (A3 §2.2, Decision D-U2). Every accepted op / cursor move
 * enqueues a write; the UI applies the preview immediately (no await). Exactly one request is in flight at a
 * time (FIFO), each carrying the `baseCursor` the mutation was built on so the server can detect multi-tab
 * races. Any failure (HTTP error / network / 409 cursorConflict / 410 gone) FREEZES the queue and fires
 * `onDesync` once — recovery is a whole-session resync (GET session → rebuild), never per-op rollback: the
 * client already ran the authoritative twin validation, so per-op rejection is not an expected path.
 */

import type { EditOp } from './edit-ops';

/** A pending server write. `ops` present ⇒ append-ops (POST …/ops); absent ⇒ cursor move (POST …/cursor). */
export interface WriteJob {
	kind: 'ops' | 'cursor';
	/** Client cursor BEFORE this mutation — the append-ops race token; recorded for cursor moves too. */
	baseCursor: number;
	/** Resulting cursor after the mutation. */
	cursor: number;
	ops?: EditOp[];
}

export interface WriteOptions {
	/** Small JSON payloads ride tab-close / navigation (E11/E12). */
	keepalive: boolean;
}

export type WriteSender = (job: WriteJob, options: WriteOptions) => Promise<void>;
export type DesyncHandler = (reason: unknown) => void;

export class EditWriteQueue {
	private readonly pending: WriteJob[] = [];
	private inFlight = false;
	private frozen = false;
	private drainWaiters: Array<() => void> = [];

	constructor(private readonly sender: WriteSender, private readonly onDesync: DesyncHandler) {}

	/** Enqueue a write. Returns false when the queue is frozen (mutations must be blocked until resync). */
	enqueue(job: WriteJob): boolean {
		if (this.frozen) return false;
		this.pending.push(job);
		void this.pump();
		return true;
	}

	isFrozen(): boolean {
		return this.frozen;
	}

	/** True while a write is in flight or queued — the E11 beforeunload guard. */
	isBusy(): boolean {
		return this.inFlight || this.pending.length > 0;
	}

	/** Recover from a desync freeze: caller has re-fetched + rebuilt state; drop any stale jobs and resume. */
	resync(): void {
		this.pending.length = 0;
		this.inFlight = false;
		this.frozen = false;
	}

	/** Resolves once every enqueued write has been acked (immediately when idle). Save/discard await this. */
	flush(): Promise<void> {
		if (!this.inFlight && this.pending.length === 0) return Promise.resolve();
		return new Promise((resolve) => this.drainWaiters.push(resolve));
	}

	private async pump(): Promise<void> {
		if (this.inFlight || this.frozen) return;
		const job = this.pending.shift();
		if (!job) {
			this.resolveDrain();
			return;
		}
		this.inFlight = true;
		try {
			await this.sender(job, { keepalive: true });
			this.inFlight = false;
			void this.pump();
		} catch (reason) {
			this.inFlight = false;
			this.freeze(reason);
		}
	}

	private freeze(reason: unknown): void {
		if (this.frozen) return;
		this.frozen = true;
		this.pending.length = 0;
		this.resolveDrain();
		this.onDesync(reason);
	}

	private resolveDrain(): void {
		const waiters = this.drainWaiters;
		this.drainWaiters = [];
		for (const resolve of waiters) resolve();
	}
}
