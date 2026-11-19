using System.Numerics;
using Vintagestory.API.MathTools;

namespace VintageKnuckle;

internal static class GripAim {
	public const float Range = 1.65f * 1.18f;
	public const float Margin = 0.48f;
	private const float ConeStart = 0.055f, ConeGrowth = 0.42f, DepthWeight = 0.025f;

	public static double AimMargin(double depth) => Math.Min(GripAim.Margin, GripAim.ConeStart + GripAim.ConeGrowth * depth);

	public static bool TryReach(GripRay ray, BlockEdge edge, (double Min, double Max) span, out (double Min, double Max) reachable) {
		double axis = edge.AlongX ? ray.Eye.X : ray.Eye.Z;
		var (x, z) = edge.PointAt(axis);
		double dx = x - ray.Eye.X, dz = z - ray.Eye.Z;
		double dy = edge.Height - ray.Eye.Y;
		dy -= Math.Clamp(dy, -ray.EyeHeight, 0);
		double reachSq = (double)ray.Range * ray.Range - dx * dx - dz * dz - dy * dy;
		reachable = default;
		if (reachSq < 0) return false;

		double reach = Math.Sqrt(reachSq);
		reachable = (Math.Max(span.Min, axis - reach), Math.Min(span.Max, axis + reach));
		return reachable.Min <= reachable.Max;
	}

	internal interface ISightLine {
		Vec3d Eye { get; }
		Vec3d PointAt(double at);
		bool Visible(double at, out SightBlocker? blocker);
		float Score(double at);
	}

	internal const double SightSpacing = 1.0 / 1024;

	internal sealed class SightSamples : List<double> {
		public bool Covered = true;
		public double LastBlocked = double.NaN;
	}

	internal static SightSamples VisibleSamples<T>(T sight, double min, double max) where T : struct, ISightLine {
		var visible = new SightSamples();
		double at = min;
		while (true) {
			bool clear = sight.Visible(at, out var blocker);
			if (clear) visible.Add(at);
			if (blocker == null) visible.Covered = false;
			if (at >= max) break;
			double next = blocker?.Exit(sight, at, max) ?? at;
			if (next == at) visible.Covered = false;
			at = next > at ? next : Math.Min(max, at + GripAim.SightSpacing);
		}
		return visible;
	}

	internal readonly record struct Contact(double At, double Min, double Max, float Score) {
		public bool AroundBlocker<T>(T sight, List<double>? visible, float limit, out double at, out float score)
			where T : struct, ISightLine {
			double bestAt = this.At, blocked = this.At;
			float best = limit;
			if (visible == null) {
				Scan(this.Min);
				Scan(this.Max);
			} else {
				int index = visible.BinarySearch(blocked);
				if (index < 0) index = ~index;
				if (index > 0 && visible[index - 1] >= this.Min) Refine(visible[index - 1]);
				else if (sight.Visible(this.Min, out _)) Refine(this.Min);
				if (index < visible.Count && visible[index] <= this.Max) Refine(visible[index]);
				else if (sight.Visible(this.Max, out _)) Refine(this.Max);
			}
			at = bestAt;
			score = best;
			return best < limit;

			void Scan(double end) {
				double next = blocked;
				while (next != end) {
					bool clear = sight.Visible(next, out var blocker);
					if (clear && float.IsFinite(sight.Score(next))) {
						Refine(next);
						return;
					}
					double exit = blocker?.Exit(sight, next, end) ?? next;
					double distance = Math.Min(GripAim.SightSpacing, Math.Abs(end - next));
					next = exit != next ? exit : next + Math.CopySign(distance, end - next);
					if (float.IsFinite(best) && sight.Score(next) >= best) return;
				}
				if (sight.Visible(end, out _)) Refine(end);
			}

			void Refine(double next) {
				double previous = next + Math.CopySign(Math.Min(GripAim.SightSpacing, Math.Abs(next - blocked)), blocked - next);
				for (int i = 0; i < 5; i++) {
					double middle = (previous + next) / 2;
					if (float.IsFinite(sight.Score(middle)) && sight.Visible(middle, out _)) next = middle;
					else previous = middle;
				}
				float value = sight.Score(next);
				if (value < best) (bestAt, best) = (next, value);
			}
		}
	}

	public static bool TryEdge(GripRay ray, BlockEdge edge, (double Min, double Max) span, bool aimedAtFace, out Contact contact) {
		contact = default;
		if (!GripAim.TryReach(ray, edge, span, out var reachable)) return false;

		var (x, z) = edge.PointAt(reachable.Min);
		double along = edge.AlongX ? x - ray.Eye.X : z - ray.Eye.Z;
		double across = edge.AlongX ? z - ray.Eye.Z : x - ray.Eye.X;
		double height = edge.Height - ray.Eye.Y;
		double alongLook = edge.AlongX ? ray.Look.X : ray.Look.Z;
		double acrossLook = edge.AlongX ? ray.Look.Z : ray.Look.X;
		double upLook = ray.Look.Y;
		double width = Math.Sqrt(acrossLook * acrossLook + upLook * upLook);
		double lookLength = Math.Sqrt(alongLook * alongLook + width * width);
		if (!(lookLength > 0)) return false;

		double alignment = alongLook / lookLength;
		double viewSlope = width / lookLength;
		double depth = (along * alongLook + across * acrossLook + height * upLook) / lookLength;

		// only one component of perpendicular miss changes as the contact slides along the edge
		double fixedMissSq = across * across + height * height;
		double movingMiss = 0;
		if (width > 0) {
			double fixedMiss = (across * upLook - height * acrossLook) / width;
			fixedMissSq = fixedMiss * fixedMiss;
			movingMiss = along * viewSlope - alignment * (across * acrossLook + height * upLook) / width;
		}

		return new AimLine(depth, alignment, movingMiss, viewSlope, fixedMissSq)
			.Closest(reachable.Min, reachable.Max - reachable.Min, aimedAtFace, out contact);
	}

	public static bool TryShaft(GripRay ray, GripSegment segment, out Contact contact) {
		contact = default;
		var start = segment.Start;
		double x = (double)segment.End.X - start.X, y = (double)segment.End.Y - start.Y, z = (double)segment.End.Z - start.Z;
		double length = Math.Sqrt(x * x + y * y + z * z);
		if (length == 0) {
			float score = ray.Score(start);
			contact = new Contact(0, 0, 0, score);
			return float.IsFinite(score);
		}

		double lx = ray.Look.X, ly = ray.Look.Y, lz = ray.Look.Z;
		double lookLength = Math.Sqrt(lx * lx + ly * ly + lz * lz);
		if (!(lookLength > 0)) return false;
		lx /= lookLength;
		ly /= lookLength;
		lz /= lookLength;

		double depth = start.X * lx + start.Y * ly + start.Z * lz;
		double along = x * lx + y * ly + z * lz;
		double dx = x - along * lx, dy = y - along * ly, dz = z - along * lz;
		double width = Math.Sqrt(dx * dx + dy * dy + dz * dz);
		double mx = start.X - depth * lx, my = start.Y - depth * ly, mz = start.Z - depth * lz;
		double miss = width > 0 ? (mx * dx + my * dy + mz * dz) / width : 0;
		double fixedSq = Math.Max(0, mx * mx + my * my + mz * mz - miss * miss);
		return new AimLine(depth, along / length, miss, width / length, fixedSq).Closest(0, length, false, out contact);
	}

	private readonly record struct AimLine(double Depth, double Alignment, double MovingMiss, double ViewSlope, double FixedMissSq) {
		public bool Closest(double start, double length, bool aimedAtFace, out Contact contact) {
			contact = default;
			double min = 0, max = length;
			if (!GripAim.ClipLinear(-this.Depth, -this.Alignment, ref min, ref max)) return false;
			if (this.Depth + this.Alignment * (this.Alignment > 0 ? max : min) <= 0) return false;

			double allowedSq = (double)GripAim.Margin * GripAim.Margin - this.FixedMissSq;
			if (allowedSq < 0) return false;
			if (this.ViewSlope > 0) {
				double allowed = Math.Sqrt(allowedSq);
				min = Math.Max(min, (-allowed - this.MovingMiss) / this.ViewSlope);
				max = Math.Min(max, (allowed - this.MovingMiss) / this.ViewSlope);
			}
			if (min > max) return false;

			if (!aimedAtFace) {
				double cone = GripAim.ConeStart + GripAim.ConeGrowth * this.Depth;
				double growth = GripAim.ConeGrowth * this.Alignment;
				if (!GripAim.ClipCone(this.ViewSlope * this.ViewSlope - growth * growth, this.MovingMiss * this.ViewSlope - cone * growth,
					this.FixedMissSq + this.MovingMiss * this.MovingMiss - cone * cone, ref min, ref max)) return false;
			}

			double depthSlope = GripAim.DepthWeight * this.Alignment;
			double at = depthSlope >= 0 ? min : max;
			if (this.ViewSlope > Math.Abs(depthSlope)) {
				double closest = -this.MovingMiss / this.ViewSlope;
				double bias = depthSlope * Math.Sqrt(this.FixedMissSq / (this.ViewSlope * this.ViewSlope - depthSlope * depthSlope));
				at = Math.Clamp(closest - bias / this.ViewSlope, min, max);
			}
			double contactDepth = this.Depth + this.Alignment * at;
			if (contactDepth <= 0) {
				// take a step inside the open end of the cone
				at = Math.Clamp(at + Math.CopySign(0.00000001, this.Alignment), min, max);
				contactDepth = this.Depth + this.Alignment * at;
				if (contactDepth <= 0) return false;
			}
			double miss = this.MovingMiss + this.ViewSlope * at;
			float score = (float)(Math.Sqrt(this.FixedMissSq + miss * miss) + GripAim.DepthWeight * contactDepth);
			contact = new Contact(start + at, start + min, start + max, score);
			return float.IsFinite(score);
		}
	}

	private static bool ClipLinear(double value, double slope, ref double min, ref double max) {
		if (slope == 0) return value <= 0;
		if (slope < 0) min = Math.Max(min, -value / slope);
		else max = Math.Min(max, -value / slope);
		return min <= max;
	}

	private static bool ClipCone(double a, double b, double c, ref double min, ref double max) {
		if (a == 0) return GripAim.ClipLinear(c, 2 * b, ref min, ref max);

		double discriminant = b * b - a * c;
		if (discriminant < 0 || (a < 0 && discriminant == 0)) return a < 0;

		double root = -b - Math.CopySign(Math.Sqrt(discriminant), b);
		double first = root / a, last = root == 0 ? first : c / root;
		if (first > last) (first, last) = (last, first);
		if (a > 0) {
			min = Math.Max(min, first);
			max = Math.Min(max, last);
		} else if (min <= first) {
			// positive depth keeps the cone radius positive, so only one branch can intersect the span
			max = Math.Min(max, first);
		} else {
			min = Math.Max(min, last);
		}
		return min <= max;
	}

	public static float PointScore(Vector3 point, Vector3 look, float eyeHeight, float range) {
		var fromBody = point;
		fromBody.Y -= Math.Clamp(point.Y, -eyeHeight, 0);
		return fromBody.LengthSquared() <= range * range ? GripAim.AimScore(point, look) : float.PositiveInfinity;
	}

	internal static float AimScore(Vector3 point, Vector3 look, bool aimedAtFace = false) {
		float depth = Vector3.Dot(point, look);
		if (depth <= 0) return float.PositiveInfinity;
		float miss = (point - look * depth).Length();
		float margin = aimedAtFace ? GripAim.Margin : (float)GripAim.AimMargin(depth);
		if (miss > margin) return float.PositiveInfinity;
		return miss + GripAim.DepthWeight * depth;
	}
}
