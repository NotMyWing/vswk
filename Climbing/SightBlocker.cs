using System.Numerics;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace VintageKnuckle;

internal readonly struct SightBlocker {
	private readonly CollisionBox? terrain;
	private readonly Cuboidd? entity;
	private readonly GripSegment beam;
	private readonly Vector3 fromAnchor;
	private readonly Vector4 bounds;
	private readonly Entity? model;
	private readonly int selection;

	public SightBlocker(Entity model, int selection) => (this.model, this.selection) = (model, selection);
	public SightBlocker(CollisionBox terrain) => this.terrain = terrain;
	public SightBlocker(Cuboidd entity) => this.entity = entity;
	public SightBlocker(GripSegment beam, Vector3 fromAnchor, Vector4 bounds) =>
		(this.beam, this.fromAnchor, this.bounds) = (beam, fromAnchor, bounds);

	private bool Blocks<T>(T sight, double at) where T : struct, GripAim.ISightLine {
		if (this.model != null) {
			return !sight.Visible(at, out var hit) && hit is { } other && other.model == this.model && other.selection == this.selection;
		}
		var eye = sight.Eye;
		var point = sight.PointAt(at);
		if (this.terrain is { } box) return box.BlocksSight(eye, point);
		if (this.entity is { } entity) return GripSearch.EntityShadow.Crosses(entity, eye, point);
		return this.beam.BlocksSight(this.fromAnchor, GripSegment.Offset(point, eye), this.bounds);
	}

	public double Exit<T>(T sight, double blocked, double end) where T : struct, GripAim.ISightLine {
		if (this.model != null) {
			var eye = sight.Eye;
			var first = sight.PointAt(blocked);
			double x = first.X - eye.X, y = first.Y - eye.Y, z = first.Z - eye.Z;
			var last = sight.PointAt(end);
			double dx = last.X - eye.X - x, dy = last.Y - eye.Y - y, dz = last.Z - eye.Z - z;
			double lengthSq = dx * dx + dy * dy + dz * dz;
			double t = lengthSq > 0 ? Math.Clamp(-(x * dx + y * dy + z * dz) / lengthSq, 0, 1) : 0;
			x += dx * t;
			y += dy * t;
			z += dz * t;
			float reach = (float)Math.Sqrt(x * x + y * y + z * z);
			var pos = this.model.Pos;
			double ex = pos.X - eye.X, ez = pos.Z - eye.Z;
			if (ex * ex + ez * ez > reach * reach || Math.Abs(pos.Y - eye.Y) > reach) return blocked;
		}
		if (this.Blocks(sight, end)) return end;

		// the shadow of one convex shape covers a single interval on the line
		while (Math.Abs(end - blocked) > 0.000001) {
			double middle = (blocked + end) / 2;
			if (this.Blocks(sight, middle)) blocked = middle;
			else end = middle;
		}
		return end;
	}
}
