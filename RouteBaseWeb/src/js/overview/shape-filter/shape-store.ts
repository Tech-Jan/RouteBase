/**
 * shape-filter/shape-store.ts — the single source of truth for painted shapes.
 *
 * State only: holds zones + gates and emits a change event when they mutate.
 * It knows nothing about rendering, input, or the network — those subscribe.
 */

import type { CrossDirection, GateRole, GateShape, Shape, ZoneRole, ZoneShape } from './shape-model';
import { normalizeZone } from './shape-model';

type Listener = () => void;

export class ShapeStore {
	private shapes: Shape[] = [];
	private readonly listeners = new Set<Listener>();

	getAll(): readonly Shape[] {
		return this.shapes;
	}

	get zones(): ZoneShape[] {
		return this.shapes.filter((s): s is ZoneShape => s.kind === 'zone');
	}

	get gates(): GateShape[] {
		return this.shapes.filter((s): s is GateShape => s.kind === 'gate');
	}

	get(id: string): Shape | undefined {
		return this.shapes.find((s) => s.id === id);
	}

	get count(): number {
		return this.shapes.length;
	}

	add(shape: Shape): void {
		this.shapes.push(shape.kind === 'zone' ? normalizeZone(shape) : shape);
		this.emit();
	}

	/** Patch a zone's bounds/role/group; bounds are re-normalised. No-op on a non-zone id. */
	updateZone(id: string, patch: Partial<Omit<ZoneShape, 'id' | 'kind'>>): void {
		const i = this.shapes.findIndex((s) => s.id === id);
		const existing = this.shapes[i];
		if (!existing || existing.kind !== 'zone') return;
		this.shapes[i] = normalizeZone({ ...existing, ...patch });
		this.emit();
	}

	/** Patch a gate's endpoints/role/direction. No-op on a non-gate id. */
	updateGate(id: string, patch: Partial<Omit<GateShape, 'id' | 'kind'>>): void {
		const i = this.shapes.findIndex((s) => s.id === id);
		const existing = this.shapes[i];
		if (!existing || existing.kind !== 'gate') return;
		this.shapes[i] = { ...existing, ...patch };
		this.emit();
	}

	setZoneRole(id: string, role: ZoneRole): void {
		this.updateZone(id, { role });
	}

	setGateRole(id: string, role: GateRole): void {
		this.updateGate(id, { role });
	}

	setGateDirection(id: string, direction: CrossDirection): void {
		this.updateGate(id, { direction });
	}

	remove(id: string): void {
		const before = this.shapes.length;
		this.shapes = this.shapes.filter((s) => s.id !== id);
		if (this.shapes.length !== before) this.emit();
	}

	clear(): void {
		if (this.shapes.length === 0) return;
		this.shapes = [];
		this.emit();
	}

	/** Replace all shapes at once (e.g. loading an existing segment for editing); emits once. */
	load(shapes: Shape[]): void {
		this.shapes = shapes.map((s) => (s.kind === 'zone' ? normalizeZone(s) : s));
		this.emit();
	}

	subscribe(fn: Listener): () => void {
		this.listeners.add(fn);
		return () => this.listeners.delete(fn);
	}

	private emit(): void {
		for (const fn of this.listeners) fn();
	}
}
