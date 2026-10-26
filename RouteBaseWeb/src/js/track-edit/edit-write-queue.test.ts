// @vitest-environment node
import { describe, expect, it, vi } from 'vitest';
import { must } from '../core/invariant';
import { EditWriteQueue, type WriteJob, type WriteOptions, type WriteSender } from './edit-write-queue';

const tick = (): Promise<void> => new Promise((resolve) => setTimeout(resolve, 0));

const opsJob = (baseCursor: number, cursor: number): WriteJob =>
	({ kind: 'ops', baseCursor, cursor, ops: [{ op: 'cropTo', slot: 0, from: 0, to: 1 }] });
const cursorJob = (baseCursor: number, cursor: number): WriteJob => ({ kind: 'cursor', baseCursor, cursor });

/** A sender whose every request stays pending until the test resolves/rejects it in order. */
function deferredSender() {
	const calls: Array<{ job: WriteJob; options: WriteOptions }> = [];
	const settlers: Array<{ resolve: () => void; reject: (reason: unknown) => void }> = [];
	const sender: WriteSender = (job, options) => {
		calls.push({ job, options });
		return new Promise<void>((resolve, reject) => settlers.push({ resolve, reject }));
	};
	return { sender, calls, settlers };
}

describe('edit-write-queue', () => {
	it('writes dispatch FIFO with one in flight', async () => {
		const { sender, calls, settlers } = deferredSender();
		const queue = new EditWriteQueue(sender, () => {});
		queue.enqueue(opsJob(0, 1));
		queue.enqueue(opsJob(1, 2));
		queue.enqueue(opsJob(2, 3));

		expect(calls).toHaveLength(1); // only the head is in flight
		must(settlers[0], 'settler 0').resolve();
		await tick();
		expect(calls).toHaveLength(2);
		must(settlers[1], 'settler 1').resolve();
		await tick();
		expect(calls).toHaveLength(3);
		expect(calls.map((c) => c.job.cursor)).toEqual([1, 2, 3]); // order preserved
	});

	it('payload carries baseCursor of the enqueuing mutation', async () => {
		const { sender, calls } = deferredSender();
		const queue = new EditWriteQueue(sender, () => {});
		queue.enqueue(cursorJob(5, 4)); // an undo from cursor 5 → 4
		await tick();
		expect(must(calls[0], 'one send').job).toMatchObject({ kind: 'cursor', baseCursor: 5, cursor: 4 });
	});

	it('failure freezes the queue and fires onDesync exactly once', async () => {
		const onDesync = vi.fn();
		let sent = 0;
		const sender: WriteSender = () => {
			sent++;
			return sent === 2 ? Promise.reject(new Error('boom')) : Promise.resolve();
		};
		const queue = new EditWriteQueue(sender, onDesync);
		queue.enqueue(opsJob(0, 1));
		queue.enqueue(opsJob(1, 2)); // this one rejects
		queue.enqueue(opsJob(2, 3)); // never sent

		await tick();
		await tick();
		expect(sent).toBe(2);
		expect(onDesync).toHaveBeenCalledTimes(1);
		expect(queue.isFrozen()).toBe(true);
	});

	it('no further enqueues accepted while frozen', async () => {
		const sender: WriteSender = () => Promise.reject(new Error('boom'));
		const queue = new EditWriteQueue(sender, () => {});
		queue.enqueue(opsJob(0, 1));
		await tick();
		expect(queue.isFrozen()).toBe(true);
		expect(queue.enqueue(opsJob(1, 2))).toBe(false); // rejected while frozen
	});

	it('resync unfreezes and clears the queue', async () => {
		let sent = 0;
		const sender: WriteSender = () => {
			sent++;
			return sent === 1 ? Promise.reject(new Error('boom')) : Promise.resolve();
		};
		const queue = new EditWriteQueue(sender, () => {});
		queue.enqueue(opsJob(0, 1));
		await tick();
		expect(queue.isFrozen()).toBe(true);

		queue.resync();
		expect(queue.isFrozen()).toBe(false);
		expect(queue.enqueue(opsJob(0, 1))).toBe(true);
		await tick();
		expect(sent).toBe(2); // dispatched after resync
	});

	it('flush resolves only when the queue is drained', async () => {
		const { sender, settlers } = deferredSender();
		const queue = new EditWriteQueue(sender, () => {});
		await expect(queue.flush()).resolves.toBeUndefined(); // immediate when idle

		queue.enqueue(opsJob(0, 1));
		queue.enqueue(opsJob(1, 2));
		let drained = false;
		const flushed = queue.flush().then(() => { drained = true; });

		must(settlers[0], 'settler 0').resolve();
		await tick();
		expect(drained).toBe(false); // still one in flight
		must(settlers[1], 'settler 1').resolve();
		await flushed;
		expect(drained).toBe(true);
	});

	it('sender is invoked with keepalive', async () => {
		const { sender, calls } = deferredSender();
		const queue = new EditWriteQueue(sender, () => {});
		queue.enqueue(opsJob(0, 1));
		await tick();
		expect(must(calls[0], 'one send').options.keepalive).toBe(true);
	});
});
