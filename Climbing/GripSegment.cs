using System.Numerics;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace VintageKnuckle;

internal readonly record struct GripSegment(Vector3 Start, Vector3 End) {
	public static Vector3 Offset(Vec3d point, Vec3d origin) =>
		new((float)(point.X - origin.X), (float)(point.Y - origin.Y), (float)(point.Z - origin.Z));

	public bool CanAim(GripRay ray, Vector3 eyeOffset, double movement, double angle) {
		if (this.InReach(ray, eyeOffset, movement) is not { } segment) return false;
		double error = movement + (ray.Range + ray.EyeHeight + movement) * angle + 0.00001;
		return GripSegment.PassesAim(segment.Start, segment.End, ray.Look, error);
	}

	public GripSegment? InReach(GripRay ray, Vector3 eyeOffset, double movement, double tolerance = 0.00001) {
		var start = this.Start - eyeOffset;
		var axis = this.End - this.Start;
		double reach = ray.Range + movement + tolerance;
		double first = 1, last = 0;

		if (axis.Y == 0) {
			Clip(0, 1);
		} else {
			double feet = (-ray.EyeHeight - (double)start.Y) / axis.Y;
			double eye = -(double)start.Y / axis.Y;
			double low = Math.Clamp(Math.Min(feet, eye), 0, 1), high = Math.Clamp(Math.Max(feet, eye), 0, 1);
			Clip(0, low);
			Clip(low, high);
			Clip(high, 1);
		}

		return first <= last ? new GripSegment(start + (float)first * axis, start + (float)last * axis) : null;

		void Clip(double low, double high) {
			var point = start;
			var delta = axis;
			double y = start.Y + axis.Y * ((low + high) * 0.5);
			if (y < -ray.EyeHeight) {
				point.Y += ray.EyeHeight;
			} else if (y <= 0) {
				point.Y = delta.Y = 0;
			}

			double speedSq = GripSegment.Dot(delta, delta);
			double distanceSq = GripSegment.Dot(point, point);
			if (speedSq > 0) {
				double projection = GripSegment.Dot(point, delta);
				double center = -projection / speedSq;
				double remainder = reach * reach - distanceSq + projection * projection / speedSq;
				if (remainder < 0) return;
				double width = Math.Sqrt(remainder / speedSq);
				low = Math.Max(low, center - width);
				high = Math.Min(high, center + width);
			} else if (distanceSq > reach * reach) return;
			if (low > high) return;

			first = Math.Min(first, low);
			last = Math.Max(last, high);
		}
	}

	internal static bool PassesAim(Vector3 a, Vector3 b, Vector3 look, double error, bool underside = false) {
		float da = Vector3.Dot(a, look), db = Vector3.Dot(b, look);
		double depth = Math.Max(da, db) + error;
		if (depth <= 0) return false;

		a -= look * da;
		b -= look * db;
		var along = b - a;
		float length = along.LengthSquared();
		float offset = length > 0 ? Math.Clamp(-Vector3.Dot(a, along) / length, 0, 1) : 0;
		double margin = (underside ? GripAim.Margin : GripAim.AimMargin(depth)) + error;
		return (a + offset * along).LengthSquared() <= margin * margin;
	}

	private static double Dot(Vector3 a, Vector3 b) => (double)a.X * b.X + (double)a.Y * b.Y + (double)a.Z * b.Z;

	public bool Contains(Vector3 point, float tolerance = 0.002f) => this.DistanceSquared(point) <= tolerance * tolerance;

	public float DistanceSquared(Vector3 point) {
		var delta = this.End - this.Start;
		float lengthSq = delta.LengthSquared();
		if (lengthSq == 0) return float.PositiveInfinity;

		float at = Math.Clamp(Vector3.Dot(point - this.Start, delta) / lengthSq, 0, 1);
		return Vector3.DistanceSquared(point, this.Start + delta * at);
	}

	public bool BlocksSight(Vector3 eye, Vector3 ray, Vector4 bounds) {
		var pad = new Vector3(Math.Max(Math.Abs(bounds.X), Math.Abs(bounds.Z)) + Math.Max(Math.Abs(bounds.Y), Math.Abs(bounds.W)));
		var above = Vector3.Min(eye, eye + ray) - Vector3.Max(this.Start, this.End) - pad;
		var below = Vector3.Min(this.Start, this.End) - Vector3.Max(eye, eye + ray) - pad;
		if (above.X > 0 || above.Y > 0 || above.Z > 0 || below.X > 0 || below.Y > 0 || below.Z > 0) return false;

		var segment = this.End - this.Start;
		float length = segment.Length();
		if (length <= 0 || !float.IsFinite(length)) return false;
		var axis = segment / length;
		var difference = eye - this.Start;
		float near = 0, far = 0.99999f;
		if (!Clip(Vector3.Dot(difference, axis), Vector3.Dot(ray, axis), 0, length, ref near, ref far)) return false;

		var side = Vector3.Cross(axis, Vector3.UnitY);
		float horizontal = side.Length();
		side = horizontal > 0.00001f ? side / horizontal : Vector3.UnitZ;
		var up = Vector3.Cross(axis, side);
		return Clip(Vector3.Dot(difference, up), Vector3.Dot(ray, up), bounds.X, bounds.Z, ref near, ref far)
			&& Clip(Vector3.Dot(difference, side), Vector3.Dot(ray, side), bounds.Y, bounds.W, ref near, ref far);

		static bool Clip(float origin, float delta, float min, float max, ref float near, ref float far) {
			if (Math.Abs(delta) < 0.000001f) return origin > min && origin < max;
			float first = (min - origin) / delta, last = (max - origin) / delta;
			near = Math.Max(near, Math.Min(first, last));
			far = Math.Min(far, Math.Max(first, last));
			return near < far;
		}
	}

	public Vec3d? Find(EntityPlayer player, GripRay ray, List<CollisionBox> boxes, ref float bestScore,
		Entity? spear = null, BlockPos? anchor = null, int targetBlockId = 0) {
		if (this.Start == this.End || this.InReach(ray, Vector3.Zero, 0, tolerance: 0) is not { } reachable) return null;
		if (!GripAim.TryShaft(ray, reachable, out var contact) || contact.Score >= bestScore) return null;

		var delta = reachable.End - reachable.Start;
		float length = delta.Length();
		var axis = length > 0 ? delta / length : Vector3.Zero;
		var sight = new ShaftSight(player, ray, reachable.Start, axis, boxes, spear, anchor, targetBlockId);
		double at = contact.At;
		float score = sight.Score(at);
		if (float.IsFinite(score) && score >= bestScore) return null;
		if (!float.IsFinite(score) || !sight.Visible(at, out _)) {
			var visible = GripSearch.For(player)?.VisibleShaft(reachable, sight);
			if (!contact.AroundBlocker(sight, visible, bestScore, out at, out score)) return null;
		}

		bestScore = score;
		return sight.PointAt(at);
	}

	internal readonly record struct ShaftSight(EntityPlayer Player, GripRay Ray, Vector3 Start, Vector3 Axis,
		List<CollisionBox> Boxes, Entity? Spear, BlockPos? Anchor, int BlockId) : GripAim.ISightLine {
		public Vec3d PointAt(double at) {
			var point = this.Start + (float)at * this.Axis;
			return this.Ray.Eye.AddCopy(point.X, point.Y, point.Z);
		}

		public float Score(double at) => this.Ray.Score(this.Start + (float)at * this.Axis);
		public Vec3d Eye => this.Ray.Eye;
		public bool Visible(double at, out SightBlocker? blocker) =>
			LedgeFinder.Visible(this.Player, this.Eye, this.PointAt(at), this.Boxes, this.Spear, this.Anchor, this.BlockId, out blocker);
	}
}
