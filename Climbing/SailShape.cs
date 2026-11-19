using System.Numerics;
using System.Runtime.InteropServices;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VintageKnuckle;

internal sealed class SailShape {
	private static readonly ElementPose Rest = new();
	private readonly List<Face> faces = new();
	public IReadOnlyList<Face> Faces => this.faces;
	public float Radius { get; }

	public SailShape(Shape shape, Vector3? origin = null) {
		var center = Matrix4x4.CreateTranslation(-(origin ?? new Vector3(0.5f)));
		foreach (var root in shape.Elements) {
			root.TrimTextureNamesAndResolveFaces();
			this.Add(root, center);
		}
		float radiusSquared = 0;
		foreach (var face in this.faces) radiusSquared = Math.Max(radiusSquared, face.FarthestCornerSquared);
		this.Radius = MathF.Sqrt(radiusSquared);
	}

	private void Add(ShapeElement element, Matrix4x4 parent) {
		if (element.From is not { Length: 3 } from || element.To is not { Length: 3 } to) return;

		var local = element.GetLocalTransformMatrix(0, Mat4f.Create(), SailShape.Rest);
		var transform = MemoryMarshal.Cast<float, Matrix4x4>(local)[0] * parent;
		var size = new Vector3((float)(to[0] - from[0]), (float)(to[1] - from[1]),
			(float)(to[2] - from[2])) / 16;

		foreach (var side in BlockFacing.ALLFACES) {
			if (element.FacesResolved?[side.Index] is not { Enabled: true }) continue;

			var normal = side.Normali;
			var corner = new Vector3(normal.X > 0 ? size.X : 0, normal.Y > 0 ? size.Y : 0, normal.Z > 0 ? size.Z : 0);
			var u = normal.X == 0 ? Vector3.UnitX * size.X : Vector3.UnitZ * size.Z;
			var v = normal.Y == 0 ? Vector3.UnitY * size.Y : Vector3.UnitZ * size.Z;

			u = Vector3.TransformNormal(u, transform);
			v = Vector3.TransformNormal(v, transform);
			if (u.LengthSquared() == 0 || v.LengthSquared() == 0) continue;
			this.faces.Add(new Face(Vector3.Transform(corner, transform), u, v));
		}

		if (element.Children == null) return;
		foreach (var child in element.Children) this.Add(child, transform);
	}

	public bool Contains(Vector3 point) {
		const float tolerance = 0.002f;
		foreach (var face in this.faces) {
			if (Vector3.DistanceSquared(point, face.Closest(point)) <= tolerance * tolerance) return true;
		}
		return false;
	}

	internal static Shape? Read(ICoreAPI api, AssetLocation location) =>
		Shape.TryGet(api, location.Clone().WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json"));

	internal (Vector3 Min, Vector3 Max)? Bounds() {
		if (this.faces.Count == 0) return null;
		var min = new Vector3(float.PositiveInfinity);
		var max = new Vector3(float.NegativeInfinity);
		foreach (var face in this.faces) face.ExpandBounds(ref min, ref max);
		return (min, max);
	}

	internal readonly struct Face(Vector3 corner, Vector3 u, Vector3 v) {
		private readonly Vector3 alongU = u / u.LengthSquared(), alongV = v / v.LengthSquared();
		private readonly Vector3 center = corner + (u + v) / 2;
		private readonly float radius = (u + v).Length() / 2;
		public float FarthestCornerSquared => Math.Max(Math.Max(corner.LengthSquared(), (corner + u).LengthSquared()),
			Math.Max((corner + v).LengthSquared(), (corner + u + v).LengthSquared()));

		public Vector3 Closest(Vector3 point) {
			var offset = point - corner;
			return corner + u * Math.Clamp(Vector3.Dot(offset, this.alongU), 0, 1)
				+ v * Math.Clamp(Vector3.Dot(offset, this.alongV), 0, 1);
		}

		public bool Aim(Vector3 eye, Vector3 look, float length, out Vector3 point) {
			point = default;
			float depth = Math.Clamp(Vector3.Dot(this.center - eye, look), 0, length);
			float margin = this.radius + GripAim.Margin;
			if (Vector3.DistanceSquared(eye + look * depth, this.center) > margin * margin) return false;

			point = this.ClosestAlong(eye, look, length);
			return true;
		}

		internal bool CanSweep(Vector3 eye, Vector3 look, Vector3 down, GripRay ray, double movement, double angle) {
			float length = ray.Range + ray.EyeHeight;
			double error = movement + (length + movement) * angle + 0.00001;
			var relative = this.center - eye;
			double depth = Vector3.Dot(relative, look);
			double span = (Math.Abs(Vector3.Dot(u, look)) + Math.Abs(Vector3.Dot(v, look))) / 2d;
			double maxDepth = depth + span + error;
			if (maxDepth <= 0) return false;

			double aim = GripAim.AimMargin(maxDepth) + error;
			var faceRadius = Math.Max(this.radius, (u - v).Length() / 2);
			if (DistanceToLine(this.center, look, length) > faceRadius + aim) return false;
			double reach = ray.Range + movement + 0.00001;
			if (DistanceToLine(this.center, down, ray.EyeHeight) > faceRadius + reach) return false;
			if (Math.Abs(Vector3.Dot(u, v)) > 0.00001f * u.Length() * v.Length()) return true;

			var aimedAt = this.ClosestAlong(eye, look, length);
			if (DistanceToLine(aimedAt, look, length) > aim + length / 4096) return false;
			var closest = this.ClosestAlong(eye, down, ray.EyeHeight);
			return DistanceToLine(closest, down, ray.EyeHeight) <= reach + ray.EyeHeight / 4096;

			float DistanceToLine(Vector3 point, Vector3 direction, float limit) {
				var offset = point - eye;
				float along = Math.Clamp(Vector3.Dot(offset, direction), 0, limit);
				return (offset - direction * along).Length();
			}
		}

		private Vector3 ClosestAlong(Vector3 eye, Vector3 look, float length) {
			Vector3 point = default;
			float near = 0, far = length;
			for (int i = 0; i < 12; i++) {
				float depth = (near + far) / 2;
				var sight = eye + look * depth;
				point = this.Closest(sight);

				if (Vector3.Dot(sight - point, look) < 0) near = depth;
				else far = depth;
			}
			return point;
		}

		public bool CanImprove(Vector3 eye, Vector3 look, float length, Vector3 nearest, float bestScore) {
			var offset = nearest - eye;
			float depth = Math.Clamp(Vector3.Dot(offset, look), 0, length);
			float miss = (offset - look * depth).Length() - length / 4096;
			float uDepth = Vector3.Dot(u, look), vDepth = Vector3.Dot(v, look);
			float minDepth = Vector3.Dot(corner - eye, look) + Math.Min(0, uDepth) + Math.Min(0, vDepth);
			float maxDepth = minDepth + Math.Abs(uDepth) + Math.Abs(vDepth);
			return maxDepth > 0 && miss <= Math.Min(GripAim.Margin, 0.055f + 0.42f * maxDepth)
				&& miss + 0.025f * Math.Max(0, minDepth) < bestScore;
		}

		public const int AlternateCount = 14;

		public bool Alternatives(Vector3 eye, Vector3 look, Vector3 down, float eyeHeight, float reach, Vector3 nearest, Span<Vector3> points) {
			var bodyPoint = this.ClosestAlong(eye, down, eyeHeight);
			if (DistanceToBody(bodyPoint) > reach + eyeHeight / 4096) return false;

			points[0] = bodyPoint;
			points[1] = this.center;
			points[2] = corner;
			points[3] = corner + u;
			points[4] = corner + v;
			points[5] = corner + u + v;
			if (DistanceToBody(bodyPoint) <= reach && DistanceToBody(nearest) > reach) {
				var outside = nearest;
				var inside = bodyPoint;
				for (int i = 0; i < 12; i++) {
					var mid = (outside + inside) / 2;
					if (DistanceToBody(mid) <= reach) inside = mid;
					else outside = mid;
				}
				points[1] = inside;
			}

			points[6] = Edge(corner, u);
			points[7] = Edge(corner, v);
			points[8] = Edge(corner + u, v);
			points[9] = Edge(corner + v, u);
			for (int i = 6; i < 10; i++) points[i + 4] = (points[i] + nearest) / 2;
			return true;

			float DistanceToBody(Vector3 target) {
				var offset = target - eye;
				float downDistance = Math.Clamp(Vector3.Dot(offset, down), 0, eyeHeight);
				return Vector3.Distance(offset, down * downDistance);
			}

			Vector3 Edge(Vector3 start, Vector3 axis) {
				var flat = axis - look * Vector3.Dot(axis, look);
				float length = flat.LengthSquared();
				float along = length > 0.000001f ? Vector3.Dot(eye - start, flat) / length
					: Vector3.Dot(eye - start, axis) / axis.LengthSquared();
				return start + axis * Math.Clamp(along, 0, 1);
			}
		}

		internal void ExpandBounds(ref Vector3 min, ref Vector3 max) {
			min = Vector3.Min(min, Vector3.Min(Vector3.Min(corner, corner + u), Vector3.Min(corner + v, corner + u + v)));
			max = Vector3.Max(max, Vector3.Max(Vector3.Max(corner, corner + u), Vector3.Max(corner + v, corner + u + v)));
		}

		internal void Corners(Span<Vector3> points) {
			points[0] = corner;
			points[1] = corner + u;
			points[2] = corner + v;
			points[3] = corner + u + v;
		}
	}
}
