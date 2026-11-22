using System.Numerics;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VintageKnuckle;

internal static class GripSweep {
	private const double SampleSpacing = 0.05;
	private const double ContactResolution = 0.001;
	private const double Roundoff = 0.00001;
	private const int EarlySamples = 4;
	private const int ExactMissesBeforeTerrainTrim = 4;

	public static Ledge? Turn(EntityPlayer player, Vector2 start, Vector2 end, Vec3d feet) {
		var delta = end - start;
		double yaw = delta.X;
		if (delta.Y == 0 && Math.Abs(yaw) > Math.Tau) yaw = Math.CopySign(Math.Tau, yaw);

		// the yaw circle shrinks towards the poles
		double low = Math.Min(start.Y, end.Y), high = Math.Max(start.Y, end.Y);
		double azimuth = Math.Ceiling(low / Math.PI) * Math.PI <= high
			? 1 : Math.Max(Math.Abs(Math.Cos(low)), Math.Abs(Math.Cos(high)));
		double angleTravel = Math.Sqrt(delta.Y * delta.Y + yaw * azimuth * yaw * azimuth);

		return GripSweep.Find(player, new Path(feet, feet, null, start, yaw, delta.Y), 0, angleTravel, includeStart: true);
	}

	public static Ledge? Step(EntityPlayer player, Vec3f view, Vec3d start, Vec3d end, bool includeStart) {
		double x = end.X - start.X, y = end.Y - start.Y, z = end.Z - start.Z;
		double travel = Math.Sqrt(x * x + y * y + z * z);
		if (travel == 0) return null;
		return GripSweep.Find(player, new Path(start, end, view, default, 0, 0), travel, 0, includeStart);
	}

	private readonly record struct Path(Vec3d Start, Vec3d End, Vec3f? Direction, Vector2 Angles, double Yaw, double Pitch) {
		public Vec3d Feet(double t, Vec3d result) => result.Set(this.Start.X + (this.End.X - this.Start.X) * t,
			this.Start.Y + (this.End.Y - this.Start.Y) * t, this.Start.Z + (this.End.Z - this.Start.Z) * t);

		public Vec3f View(double t, Vec3f result) {
			if (this.Direction != null) return this.Direction;
			float pitch = (float)(this.Angles.Y + this.Pitch * t), yaw = (float)(this.Angles.X + this.Yaw * t);
			float flat = -GameMath.Cos(pitch);
			return result.Set(flat * GameMath.Sin(yaw), GameMath.Sin(pitch), flat * GameMath.Cos(yaw));
		}
	}

	private static Ledge? Find(EntityPlayer player, Path path, double travel, double angleTravel, bool includeStart) {
		float range = GripAim.Range * LedgeFinder.ModelScale(player);
		float eyeHeight = (float)player.LocalEyePos.Y;
		double reach = range + eyeHeight;
		double displacement = travel + reach * angleTravel;
		if (!double.IsFinite(displacement)) return null;

		using var owned = GripSearch.For(player) is not { InRay: false } ? GripSearch.Open(player, cacheShapes: true) : null;
		var search = GripSearch.For(player)!;
		search.CacheSight(travel == 0 ? path.Start.AddCopy(player.LocalEyePos) : null);
		var feet = new Vec3d();
		var view = new Vec3f();
		var probeEye = new Vec3d();
		double last = -1;
		if (includeStart && At(0) is { } initial) return initial;
		int steps = (int)Math.Clamp(Math.Ceiling(displacement / GripSweep.SampleSpacing), 1, int.MaxValue - 1);
		int tested = 0;
		Ledge? knownHit = null;
		if (travel > 0) {
			int probes = Math.Min(steps, GripSweep.EarlySamples);
			while (tested < probes && knownHit == null) knownHit = At((double)++tested / steps);
		}

		int limit = knownHit != null ? tested : steps;
		int groupSize = travel <= 1 ? steps : Math.Max(1, (int)(steps / travel));
		var candidates = search.Candidates;

		for (int first = 0; first < limit;) {
			int next = first + Math.Min(groupSize, limit - first);
			double from = (double)first / steps, to = (double)next / steps;
			var center = Ray((from + to) * 0.5, new Vec3d());
			double half = (to - from) * 0.5;
			var startEye = path.Feet(from, new Vec3d()).Add(player.LocalEyePos);
			var endEye = path.Feet(to, new Vec3d()).Add(player.LocalEyePos);
			candidates.Gather(player, center, startEye, endEye, travel * half, angleTravel * half);
			if (candidates.Any) {
				for (int i = first; i < next; i++) {
					double after = (double)(i + 1) / steps;
					if (Within((double)i / steps, after) is { } contact) return contact;
				}
			}
			first = next;
		}
		return knownHit ?? (tested < steps ? At(1) : null);

		GripRay Ray(double t, Vec3d eye) {
			var direction = path.View(t, view);
			return new GripRay(path.Feet(t, eye).Add(player.LocalEyePos),
				Vector3.Normalize(new Vector3(direction.X, direction.Y, direction.Z)), eyeHeight, range);
		}

		Ledge? At(double t, bool specialOnly = false) {
			if (t == last) return null;
			last = t;
			path.Feet(t, feet);
			var direction = path.View(t, view);
			var lip = specialOnly ? LedgeFinder.FindSpecialAt(player, direction, feet) : LedgeFinder.FindAt(player, direction, feet);
			return lip != null && travel > 0 && t < 1 ? lip with { CaughtFrom = feet.Clone() } : lip;
		}

		Ledge? Within(double from, double to) {
			double middle = (from + to) * 0.5;
			double half = (to - from) * 0.5;
			var ray = Ray(middle, probeEye);
			if (!candidates.CanReach(ray, travel * half, angleTravel * half) &&
				!candidates.SpecialCanAim(ray, travel * half, angleTravel * half)) return null;
			// refine potential contacts to a milliblock before testing the exact pose
			if (half * displacement <= GripSweep.ContactResolution) return Probe(middle, ray) ?? Probe(to, Ray(to, probeEye));
			return Within(from, middle) ?? Within(middle, to);
		}

		Ledge? Probe(double t, GripRay ray) {
			int index = (int)Math.Round(t * steps);
			if (index > 0 && index <= tested && t == (double)index / steps)
				return index == tested ? knownHit : null;
			bool ordinary = candidates.HasContact(ray);
			if (!ordinary && !candidates.SpecialCanAim(ray, 0, 0)) return null;
			var result = At(t, specialOnly: !ordinary);
			if (result == null) {
				if (ordinary) candidates.ObserveMiss();
				candidates.ObserveSpecialMiss();
			}
			return result;
		}
	}

	internal sealed class Candidates {
		private List<(BlockEdge Edge, (double Min, double Max) Span, CollisionBox? Ceiling)> edges = new(), visible = new();
		private readonly List<CollisionBox> leaves = new();
		private readonly List<GripSegment> shafts = new();
		private readonly List<(WindmillGrip.SweepPose Pose, SailShape.Face Face)> sails = new();
		private readonly List<(double Min, double Max)> spans = new();
		private readonly List<CollisionBox> boxes = new();
		private GripSearch search = null!;
		private List<Cuboidd>? walls;
		private Vec3d startEye = null!, endEye = null!;
		private GripRay specialRay;
		private readonly Vec3d rayEnd = new();
		private double specialMovement;
		private bool fixedEye;
		private int misses, specialMisses;
		public bool Any => this.edges.Count > 0 || this.leaves.Count > 0 || this.shafts.Count > 0 || this.sails.Count > 0;

		public void Clear() {
			this.edges.Clear();
			this.visible.Clear();
			this.boxes.Clear();
			this.leaves.Clear();
			this.sails.Clear();
			this.shafts.Clear();
			this.walls = null;
		}

		public void Gather(EntityPlayer player, GripRay ray, Vec3d startEye, Vec3d endEye, double movement, double angle) {
			this.edges.Clear();
			this.leaves.Clear();
			this.shafts.Clear();
			this.sails.Clear();
			this.misses = this.specialMisses = 0;
			this.walls = null;
			this.startEye = startEye;
			this.endEye = endEye;
			this.fixedEye = movement == 0;
			this.specialRay = ray;
			this.specialMovement = movement;
			var search = this.search = GripSearch.For(player)!;
			search.Entities(ray, movement);
			foreach (var spear in search.Spears) {
				if (SpearGrip.ShaftAt(spear, ray.Eye) is { } shaft) this.shafts.Add(shaft);
			}
			BeamGrip.CollectSegments(ray.Eye, search.Beams(ray.Eye, ray.Range + ray.EyeHeight + movement), this.shafts);
			foreach (var mill in NearbyGrips.Find(player, ray).Windmills) {
				if (mill.ForSweep(ray, movement) is not { } sail) continue;
				foreach (var face in sail.Shape.Faces) {
					if (sail.CanAim(face, ray, Vector3.Zero, movement, angle)) this.sails.Add((sail, face));
				}
			}
			double range = ray.Range + GripSweep.Roundoff;
			double floor = Math.Min(startEye.Y, endEye.Y) - ray.EyeHeight - 0.05;
			double ceiling = Math.Max(startEye.Y, endEye.Y) + range;
			var min = new Vec3d(Math.Min(startEye.X, endEye.X) - range, floor,
				Math.Min(startEye.Z, endEye.Z) - range);
			var max = new Vec3d(Math.Max(startEye.X, endEye.X) + range, ceiling,
				Math.Max(startEye.Z, endEye.Z) + range);
			var end = this.RayEnd(ray, angle);
			double pad = movement + (ray.Range + ray.EyeHeight) * angle + GripSweep.Roundoff;
			min.Set(Math.Max(min.X, Math.Min(ray.Eye.X, end.X) - pad), Math.Max(min.Y, Math.Min(ray.Eye.Y, end.Y) - pad),
				Math.Max(min.Z, Math.Min(ray.Eye.Z, end.Z) - pad));
			max.Set(Math.Min(max.X, Math.Max(ray.Eye.X, end.X) + pad), Math.Min(max.Y, Math.Max(ray.Eye.Y, end.Y) + pad),
				Math.Min(max.Z, Math.Max(ray.Eye.Z, end.Z) + pad));
			LedgeFinder.CollectBoxes(player.World.BlockAccessor, player.Pos.Dimension, min, max, search, this.boxes);
			foreach (var box in this.boxes) {
				if (box.Y2 < floor - GripSweep.Roundoff) continue;
				if (box.Block is BlockSupportBeam || box.Block.BlockMaterial is EnumBlockMaterial.Ice or EnumBlockMaterial.Snow) continue;
				if (box.Block.BlockMaterial == EnumBlockMaterial.Leaves) this.leaves.Add(box);
				foreach (var face in BlockFacing.HORIZONTALS) {
					Add(new BlockEdge(box, face, underside: false), null);
					if (box.CanGripUnderside) Add(new BlockEdge(box, face, underside: true), box);
				}
			}

			void Add(BlockEdge edge, CollisionBox? underside) {
				if (edge.Height < floor - GripSweep.Roundoff || edge.Height > ceiling) return;
				var span = edge.Span;
				if (!GripSweep.CanAim(ray, edge, span, underside.HasValue, movement, angle)) return;
				if (this.fixedEye && (!GripAim.TryReach(ray, edge, span, out span) || this.search.HiddenEdge(edge))) return;
				this.spans.Clear();
				this.spans.Add(span);
				foreach (var shadow in this.search.Shadows) {
					shadow.Trim(edge, this.spans, startEye, this.fixedEye ? null : endEye);
					if (this.spans.Count == 0) return;
				}

				foreach (var part in this.spans) this.edges.Add((edge, part, underside));
			}
		}

		public void ObserveMiss() {
			if (++this.misses != GripSweep.ExactMissesBeforeTerrainTrim) return;
			var walls = this.walls ??= BlockEdge.SolidWalls(this.boxes);
			Span<Vector3> corners = stackalloc Vector3[8];
			for (int i = this.leaves.Count - 1; i >= 0; i--) {
				var leaf = this.leaves[i];
				int corner = 0;
				for (int x = 0; x < 2; x++)
				for (int y = 0; y < 2; y++)
				for (int z = 0; z < 2; z++) {
					var eye = this.specialRay.Eye;
					corners[corner++] = new Vector3((float)((x == 0 ? leaf.X1 : leaf.X2) - eye.X),
						(float)((y == 0 ? leaf.Y1 : leaf.Y2) - eye.Y), (float)((z == 0 ? leaf.Z1 : leaf.Z2) - eye.Z));
				}
				if (this.Hidden(corners)) this.leaves.RemoveAt(i);
			}

			this.visible.Clear();
			foreach (var (edge, span, ceiling) in this.edges) {
				if (this.fixedEye && this.search.HiddenEdge(edge)) continue;
				this.spans.Clear();
				this.spans.Add(span);
				foreach (var wall in walls) {
					edge.TrimWall(this.spans, wall, this.startEye, this.fixedEye ? null : this.endEye);
					if (this.spans.Count == 0) break;
				}
				if (this.spans.Count == 0) continue;

				var first = edge.SightPoint(span.Min);
				var last = edge.SightPoint(span.Max);
				double lowX = Math.Min(Math.Min(this.startEye.X, this.endEye.X), first.X) - GripSweep.Roundoff;
				double lowY = Math.Min(Math.Min(this.startEye.Y, this.endEye.Y), first.Y) - GripSweep.Roundoff;
				double lowZ = Math.Min(Math.Min(this.startEye.Z, this.endEye.Z), first.Z) - GripSweep.Roundoff;
				double highX = Math.Max(Math.Max(this.startEye.X, this.endEye.X), last.X) + GripSweep.Roundoff;
				double highY = Math.Max(Math.Max(this.startEye.Y, this.endEye.Y), last.Y) + GripSweep.Roundoff;
				double highZ = Math.Max(Math.Max(this.startEye.Z, this.endEye.Z), last.Z) + GripSweep.Roundoff;
				foreach (var neighbor in this.boxes) {
					if (neighbor.X2 < lowX || neighbor.X1 > highX || neighbor.Y2 < lowY || neighbor.Y1 > highY ||
						neighbor.Z2 < lowZ || neighbor.Z1 > highZ) continue;
					if (this.fixedEye) edge.Trim(this.spans, neighbor, this.startEye);
					else edge.TrimThroughout(this.spans, neighbor, this.startEye, this.endEye);
					if (this.spans.Count == 0) break;
				}
				foreach (var part in this.spans) {
					if (!this.fixedEye && this.search.ModelHides(edge, part, this.startEye, this.endEye)) continue;
					this.visible.Add((edge, part, ceiling));
				}
			}
			(this.edges, this.visible) = (this.visible, this.edges);
		}

		public void ObserveSpecialMiss() {
			if ((this.shafts.Count == 0 && this.sails.Count == 0) || ++this.specialMisses != GripSweep.ExactMissesBeforeTerrainTrim) return;
			this.walls ??= BlockEdge.SolidWalls(this.boxes);
			Span<Vector3> corners = stackalloc Vector3[4];
			for (int i = this.shafts.Count - 1; i >= 0; i--) {
				if (this.shafts[i].InReach(this.specialRay, Vector3.Zero, this.specialMovement) is not { } shaft) {
					this.shafts.RemoveAt(i);
					continue;
				}
				corners[0] = shaft.Start;
				corners[1] = shaft.End;
				if (this.Hidden(corners[..2])) this.shafts.RemoveAt(i);
			}
			for (int i = this.sails.Count - 1; i >= 0; i--) {
				var (pose, face) = this.sails[i];
				pose.Corners(face, corners);
				if (this.Hidden(corners)) this.sails.RemoveAt(i);
			}
		}

		private bool Hidden(ReadOnlySpan<Vector3> corners) {
			var origin = this.specialRay.Eye;
			var start = GripSegment.Offset(this.startEye, origin);
			var end = GripSegment.Offset(this.endEye, origin);
			foreach (var shadow in this.search.Shadows) {
				if (shadow.Entity is EntityProjectileBase || shadow.PassesInside(this.startEye, this.endEye)) continue;
				var box = shadow.Bounds;
				if (Hides(Offset(box.X1, box.Y1, box.Z1), Offset(box.X2, box.Y2, box.Z2), corners)) return true;
			}
			foreach (var wall in this.walls!) {
				if (Hides(Offset(wall.X1, wall.Y1, wall.Z1), Offset(wall.X2, wall.Y2, wall.Z2), corners)) return true;
			}
			foreach (var box in this.boxes) {
				if (box.SelectionOnly || box.Block is BlockSupportBeam) continue;
				if (Hides(Offset(box.X1, box.Y1, box.Z1), Offset(box.X2, box.Y2, box.Z2), corners)) return true;
			}
			return false;

			Vector3 Offset(double x, double y, double z) => new((float)(x - origin.X), (float)(y - origin.Y), (float)(z - origin.Z));

			bool Hides(Vector3 min, Vector3 max, ReadOnlySpan<Vector3> points) {
				min += new Vector3((float)GripSweep.Roundoff);
				max -= new Vector3((float)GripSweep.Roundoff);
				if (min.X >= max.X || min.Y >= max.Y || min.Z >= max.Z) return false;
				// a convex blocker hides every intermediate line when it hides the endpoint lines
				foreach (var point in points) {
					if (!Candidates.Crosses(start, point, min, max) || !Candidates.Crosses(end, point, min, max)) return false;
				}
				return true;
			}
		}

		private static bool Crosses(Vector3 start, Vector3 end, Vector3 min, Vector3 max) {
			double near = 0, far = 1;
			var delta = end - start;
			return CollisionBox.Clip(start.X, delta.X, min.X, max.X, ref near, ref far, open: true) &&
				CollisionBox.Clip(start.Y, delta.Y, min.Y, max.Y, ref near, ref far, open: true) &&
				CollisionBox.Clip(start.Z, delta.Z, min.Z, max.Z, ref near, ref far, open: true) && near < far;
		}

		public bool CanReach(GripRay ray, double movement, double angle) {
			foreach (var (edge, span, ceiling) in this.edges) {
				if (this.fixedEye && this.search.HiddenEdge(edge)) continue;
				if (edge.Height < ray.Eye.Y - ray.EyeHeight - 0.05 - movement - GripSweep.Roundoff) continue;
				if (GripSweep.CanAim(ray, edge, span, ceiling.HasValue, movement, angle)) return true;
			}

			if (this.leaves.Count == 0) return false;
			var end = this.RayEnd(ray, angle);
			double pad = movement + (ray.Range + ray.EyeHeight) * angle + GripSweep.Roundoff;
			foreach (var box in this.leaves) {
				if (box.Y2 < ray.Eye.Y - ray.EyeHeight - 0.05 - movement - GripSweep.Roundoff) continue;
				if (box.X2 >= Math.Min(ray.Eye.X, end.X) - pad && box.X1 <= Math.Max(ray.Eye.X, end.X) + pad &&
					box.Y2 >= Math.Min(ray.Eye.Y, end.Y) - pad && box.Y1 <= Math.Max(ray.Eye.Y, end.Y) + pad &&
					box.Z2 >= Math.Min(ray.Eye.Z, end.Z) - pad && box.Z1 <= Math.Max(ray.Eye.Z, end.Z) + pad) return true;
			}
			return false;
		}

		public bool HasContact(GripRay ray) {
			double floor = ray.Eye.Y - ray.EyeHeight - 0.05 - GripSweep.Roundoff;
			Vec3d? end = null;
			foreach (var (edge, span, ceiling) in this.edges) {
				if (edge.Height < floor || (this.fixedEye && this.search.HiddenEdge(edge))) continue;
				bool aimedAtFace = ceiling is { } box && box.HitsUnderside(ray.Eye, end ??= this.RayEnd(ray, 0));
				if (GripAim.TryEdge(ray, edge, span, aimedAtFace, out var contact) &&
					(!this.fixedEye || this.search.MaySee(edge, contact, ray.Eye))) return true;
			}
			if (this.leaves.Count == 0) return false;
			end ??= this.RayEnd(ray, 0);
			foreach (var box in this.leaves) {
				if (box.Y2 < floor || !LeafGrip.Intersects(box, ray.Eye, end, out double at)) continue;
				var hit = ray.Eye.AddCopy((end.X - ray.Eye.X) * at, (end.Y - ray.Eye.Y) * at, (end.Z - ray.Eye.Z) * at);
				if (hit.Y < floor || !float.IsFinite(ray.Score(GripSegment.Offset(hit, ray.Eye)))) continue;
				bool blocked = false;
				foreach (var other in this.boxes) {
					if (other == box || !other.BlocksSight(ray.Eye, hit)) continue;
					blocked = true;
					break;
				}
				if (blocked) continue;
				var face = LeafGrip.Face(this.search.Player, box, ray.Eye, hit);
				if (!LedgeFinder.EntityBlocksGrip(this.search.Player, ray.Eye, hit, face.Normali.X, face.Normali.Z)) return true;
			}
			return false;
		}

		public bool SpecialCanAim(GripRay ray, double movement, double angle) {
			if (this.shafts.Count == 0 && this.sails.Count == 0) return false;
			var eyeOffset = GripSegment.Offset(ray.Eye, this.specialRay.Eye);
			foreach (var shaft in this.shafts) {
				if (shaft.CanAim(ray, eyeOffset, movement, angle)) return true;
			}
			foreach (var (pose, face) in this.sails) {
				if (pose.CanAim(face, ray, eyeOffset, movement, angle)) return true;
			}
			return false;
		}

		private Vec3d RayEnd(GripRay ray, double angle) {
			float length = ray.Look.Y - angle < 0 ? ray.Range + ray.EyeHeight : ray.Range;
			return this.rayEnd.Set(ray.Eye).Add(length * ray.Look.X, length * ray.Look.Y, length * ray.Look.Z);
		}
	}

	private static bool CanAim(GripRay ray, BlockEdge edge, (double Min, double Max) span, bool underside, double movement, double angle) {
		var reach = ray with { Range = (float)(ray.Range + movement + GripSweep.Roundoff) };
		if (!GripAim.TryReach(reach, edge, span, out var clipped)) return false;

		var (ax, az) = edge.PointAt(clipped.Min);
		var (bx, bz) = edge.PointAt(clipped.Max);
		float y = (float)(edge.Height - ray.Eye.Y);
		var a = new Vector3((float)(ax - ray.Eye.X), y, (float)(az - ray.Eye.Z));
		var b = new Vector3((float)(bx - ray.Eye.X), y, (float)(bz - ray.Eye.Z));
		double error = movement + (ray.Range + ray.EyeHeight + movement) * angle + GripSweep.Roundoff;
		return GripSegment.PassesAim(a, b, ray.Look, error, underside);
	}
}
