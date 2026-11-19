using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent.Mechanics;

namespace VintageKnuckle;

internal sealed class WindmillGrip {
	public const int SearchRadius = 16; // room for the wide millwright sails
	private const double CatchAllowance = 0.35;
	private static readonly ConditionalWeakTable<ICoreAPI, Dictionary<AssetLocation, SailShape?>> Shapes = new();
	private static readonly ConditionalWeakTable<BEBehaviorMPBase, WindmillGrip> Rotors = new();

	private readonly BEBehaviorMPBase rotor;
	private readonly ConditionalWeakTable<Ledge, SailShape> checkedGrips = new();
	private readonly Func<int>? sailLength;
	private readonly Func<string>? sailType;
	private int length = -1;
	private string? fabric;
	private AssetLocation? asset;
	private SailShape? sails;
	private readonly float rotationModifier = 1;

	private WindmillGrip(BEBehaviorMPBase rotor) {
		this.rotor = rotor;
		if (rotor is BEBehaviorWindmillRotor vanilla) {
			this.sailLength = () => vanilla.SailLength;
			return;
		}

		if (rotor.Block.Code.Domain != "millwright") return;
		var type = rotor.GetType();
		this.sailLength = type.GetProperty("SailLength")?.GetMethod?.CreateDelegate<Func<int>>(rotor);
		this.sailType = type.GetProperty("SailType")?.GetMethod?.CreateDelegate<Func<string>>(rotor);

		var modifier = type.GetField("sailRotationModifier", BindingFlags.Instance | BindingFlags.NonPublic);
		if (modifier?.GetValue(rotor) is float rotation) this.rotationModifier = rotation;
	}

	internal static WindmillGrip? At(BlockEntity? entity) {
		if (entity?.Block?.Code?.Path is not { } path) return null;
		var name = path.AsSpan();
		int dash = name.IndexOf('-');
		if (dash >= 0) name = name[..dash];
		if (name is not ("windmillrotor" or "windmillrotorud")) return null;
		var rotor = entity.GetBehavior<BEBehaviorMPBase>();
		if (rotor?.AxisSign is not { Length: 3 }) return null;
		return WindmillGrip.Rotors.GetValue(rotor, r => new WindmillGrip(r));
	}

	private SailShape? ReadSails() {
		int length = this.sailLength?.Invoke() ?? 0;
		if (length <= 0) return null;

		var fabric = this.sailType?.Invoke();
		bool client = this.rotor.Api.Side == EnumAppSide.Client;
		if (!client && length == this.length && fabric == this.fabric) return this.sails;

		var path = client ? this.rotor.Shape?.Base : this.ServerShapePath(length, fabric);
		this.length = length;
		this.fabric = fabric;
		if (path == this.asset) return this.sails;

		this.asset = path;
		this.sails = null;
		if (path == null) return null;

		var cache = WindmillGrip.Shapes.GetOrCreateValue(this.rotor.Api);
		if (!cache.TryGetValue(path, out this.sails)) {
			var shape = SailShape.Read(this.rotor.Api, path);
			this.sails = shape?.Elements == null ? null : new SailShape(shape);
			cache[path] = this.sails;
		}
		return this.sails;
	}

	private AssetLocation? ServerShapePath(int length, string? fabric) {
		var block = this.rotor.Block;
		if (this.rotor is BEBehaviorWindmillRotor) {
			var pattern = block.Attributes?["sailedShapes"]["wildcardPath"].AsString();
			return pattern == null ? null : AssetLocation.Create(pattern.Replace("{length}", length.ToString()), block.Code.Domain);
		}

		// millwright only builds the sail path on the client
		var hub = block.Shape.Base;
		string folder = hub.Path[..hub.Path.LastIndexOf('/')];
		string name = block.FirstCodePart() == "windmillrotorud" ? "windmillud" : "windmill";
		if (string.IsNullOrEmpty(fabric)) fabric = "sailcentered";
		return new AssetLocation(hub.Domain, $"{folder}/{fabric}/{name}-{length}blade");
	}

	private Quaternion Rotation(out Vector3 angularVelocity) {
		var axis = this.rotor.AxisSign;
		var shaft = new Vector3(axis[0], axis[1], axis[2]);
		float speed = 0;
		if (this.rotor.Network is { } network) {
			speed = network.Speed;
			if (this.rotor.Api.Side == EnumAppSide.Client) {
				bool clockwise = (network.TurnDir == EnumRotDirection.Clockwise) ^ network.DirectionHasReversed;
				speed = clockwise ? network.clientSpeed : -network.clientSpeed;
				if (network.Speed < 0.001f) speed = 0;
			}
			if (this.rotor.IsRotationReversed()) speed = -speed;
		}

		const float radiansPerSecond = 5;
		angularVelocity = shaft * (radiansPerSecond * speed * this.rotor.GearedRatio * this.rotationModifier);
		var facing = Quaternion.CreateFromAxisAngle(Vector3.UnitY, this.rotor.Block.Shape.rotateY * GameMath.DEG2RAD);
		return Quaternion.CreateFromAxisAngle(shaft, this.rotor.AngleRad) * facing;
	}

	private Ledge Contact(Vector3 local, Vector3 offset) {
		var pos = this.rotor.Position;
		double x = pos.X + 0.5 + offset.X, y = pos.Y + 0.5 + offset.Y, z = pos.Z + 0.5 + offset.Z;
		return new Ledge(pos.X, pos.Y, pos.Z, pos.dimension, this.rotor.Block.Id, 0, 1, x, z) {
			SailPoint = local, Top = y, EdgeMin = x - 0.08, EdgeMax = x + 0.08
		};
	}

	public readonly record struct SweepPose(SailShape Shape, Vector3 Center, Quaternion Inverse) {
		public bool CanAim(SailShape.Face face, GripRay ray, Vector3 eyeOffset, double movement, double angle) {
			var eye = Vector3.Transform(eyeOffset - this.Center, this.Inverse);
			var look = Vector3.Transform(ray.Look, this.Inverse);
			var down = Vector3.Transform(-Vector3.UnitY, this.Inverse);
			return face.CanSweep(eye, look, down, ray, movement, angle);
		}

		public void Corners(SailShape.Face face, Span<Vector3> points) {
			face.Corners(points);
			var rotation = Quaternion.Conjugate(this.Inverse);
			for (int i = 0; i < points.Length; i++) points[i] = this.Center + Vector3.Transform(points[i], rotation);
		}
	}

	public SweepPose? ForSweep(GripRay ray, double travel) {
		var hub = this.rotor.Position;
		var center = new Vector3((float)(hub.X + 0.5 - ray.Eye.X), (float)(hub.Y + 0.5 - ray.Eye.Y),
			(float)(hub.Z + 0.5 - ray.Eye.Z));
		double distanceSquared = center.LengthSquared();
		double reach = ray.Range + ray.EyeHeight + travel;
		if (distanceSquared > (WindmillGrip.SearchRadius + reach) * (WindmillGrip.SearchRadius + reach) ||
			this.rotor.Api.World.BlockAccessor.GetBlockEntity(hub) != this.rotor.Blockentity ||
			this.ReadSails() is not { Faces.Count: > 0 } sails) return null;

		double radius = reach + sails.Radius + 0.001;
		return distanceSquared <= radius * radius ? new SweepPose(sails, center, Quaternion.Conjugate(this.Rotation(out _))) : null;
	}

	public static Ledge? Resolve(IWorldAccessor world, Ledge? hold, out Vector3 velocity) =>
		WindmillGrip.Resolve(world, hold, out velocity, out _);

	public static Ledge? Resolve(IWorldAccessor world, Ledge? hold, out Vector3 velocity, out Vector3 angular) {
		velocity = Vector3.Zero;
		angular = Vector3.Zero;
		if (hold?.SailPoint is not Vector3 local) return hold;

		var pos = new BlockPos(hold.X, hold.Y, hold.Z, hold.Dimension);
		var mill = WindmillGrip.At(world.BlockAccessor.GetBlockEntity(pos));
		if (mill == null || mill.rotor.Block.Id != hold.BlockId || mill.ReadSails() is not { } sails) return null;

		if (!mill.checkedGrips.TryGetValue(hold, out var checkedShape) || checkedShape != sails) {
			if (!sails.Contains(local)) return null;
			mill.checkedGrips.AddOrUpdate(hold, sails);
		}

		var offset = Vector3.Transform(local, mill.Rotation(out angular));
		velocity = Vector3.Cross(angular, offset);
		return mill.Contact(local, offset);
	}

	public static Ledge? Find(EntityPlayer player, GripRay ray, List<CollisionBox> boxes, List<WindmillGrip> mills, float sweepTime, ref float bestScore) {
		if (mills.Count == 0) return null;
		var eye = ray.Eye;
		var point = GripSearch.For(player)?.SightPoint ?? new Vec3d();
		Ledge? best = null;
		float scoreToBeat = bestScore;
		Span<Vector3> alternatives = stackalloc Vector3[SailShape.Face.AlternateCount];
		Span<(float Score, int Point)> ranked = stackalloc (float, int)[SailShape.Face.AlternateCount];

		foreach (var mill in mills) {
			var hub = mill.rotor.Position;
			var fromHub = new Vector3((float)(eye.X - hub.X - 0.5), (float)(eye.Y - hub.Y - 0.5), (float)(eye.Z - hub.Z - 0.5));
			float distance = fromHub.Length();
			if (distance > WindmillGrip.SearchRadius + ray.Range + ray.EyeHeight) continue;
			if (player.World.BlockAccessor.GetBlockEntity(hub) != mill.rotor.Blockentity || mill.ReadSails() is not { } sails) continue;
			if (distance > sails.Radius + ray.Range + ray.EyeHeight + 0.001f) continue;

			var rotation = mill.Rotation(out var angular);
			float travel = 0;
			int steps = 0;
			if (sweepTime > 0) {
				travel = angular.Length() * Math.Min(sweepTime, 0.1f);
				steps = Math.Clamp((int)MathF.Ceiling(travel * (distance + ray.Range) / 0.15f), 0, 24);
			}

			for (int i = 0; i <= steps; i++) {
				var pose = rotation;
				if (i > 0) {
					var back = Quaternion.CreateFromAxisAngle(Vector3.Normalize(angular), -travel * i / steps);
					pose = back * rotation;
				}

				var inverse = Quaternion.Conjugate(pose);
				var localEye = Vector3.Transform(fromHub, inverse);
				var localLook = Vector3.Transform(ray.Look, inverse);
				var localDown = Vector3.Transform(-Vector3.UnitY, inverse);
				bool foundThisPose = false;
				foreach (var face in sails.Faces) {
					if (!face.Aim(localEye, localLook, ray.Length, out var local)) continue;

					var offset = Vector3.Transform(local, pose);
					float score = ray.Score(offset - fromHub);
					if (score < scoreToBeat && See(local, offset, score)) continue;
					if (!face.CanImprove(localEye, localLook, ray.Length, local, scoreToBeat) ||
						!face.Alternatives(localEye, localLook, localDown, ray.EyeHeight, ray.Range, local, alternatives)) continue;

					int count = 0;
					for (int j = 0; j < alternatives.Length; j++) {
						var alternate = alternatives[j];
						if (Vector3.DistanceSquared(alternate, local) <= 0.000001f) continue;
						float alternateScore = ray.Score(Vector3.Transform(alternate, pose) - fromHub);
						if (alternateScore < scoreToBeat) ranked[count++] = (alternateScore, j);
					}

					var viable = ranked[..count];
					viable.Sort();
					foreach (var candidate in viable) {
						var alternate = alternatives[candidate.Point];
						if (See(alternate, Vector3.Transform(alternate, pose), candidate.Score)) break;
					}
				}
				if (foundThisPose) break;

				bool See(Vector3 local, Vector3 offset, float score) {
					point.Set(hub.X + 0.5 + offset.X, hub.Y + 0.5 + offset.Y, hub.Z + 0.5 + offset.Z);
					if (!LedgeFinder.Visible(player, eye, point, boxes)) return false;
					if (pose != rotation) {
						offset = Vector3.Transform(local, rotation);
						point.Set(hub.X + 0.5 + offset.X, hub.Y + 0.5 + offset.Y, hub.Z + 0.5 + offset.Z);
						if (!LedgeFinder.Visible(player, eye, point)) return false;
					}

					best = mill.Contact(local, offset);
					scoreToBeat = score;
					foundThisPose = true;
					return true;
				}
			}
		}
		bestScore = scoreToBeat;
		return best;
	}

	public static bool InReach(EntityPlayer player, Ledge sail, Vector3 angular, double allowance, int graceMs) {
		return WindmillGrip.TryReach(player, sail, angular, allowance, graceMs, null, null);
	}

	private static bool TryReach(EntityPlayer player, Ledge sail, Vector3 angular, double allowance, int graceMs,
		Vec3d? from, Predicate<Vec3d>? visible) {
		var point = sail.Position;
		bool Reachable() => LedgeFinder.InReach(player, point, allowance, from) && (visible == null || visible(point));
		if (Reachable()) return true;

		float speed = angular.Length();
		if (speed == 0 || !float.IsFinite(speed)) return false;

		var axis = angular / speed;
		var offset = new Vector3((float)(point.X - sail.X - 0.5), (float)(point.Y - sail.Y - 0.5), (float)(point.Z - sail.Z - 0.5));
		var radius = Vector3.Cross(axis, offset).Length();
		float arc = Math.Min(MathF.PI, speed * graceMs / 1000f);
		const float stepLength = 0.35f;
		int steps = (int)MathF.Ceiling(arc * Math.Min(radius, WindmillGrip.SearchRadius) / stepLength);
		if (steps == 0) return false;

		// the client's rotor can lead or trail the server
		var turn = Quaternion.CreateFromAxisAngle(axis, arc / steps);
		var back = Quaternion.Conjugate(turn);
		var ahead = offset;
		var behind = offset;
		bool At(Vector3 spoke) {
			point.Set(sail.X + 0.5 + spoke.X, sail.Y + 0.5 + spoke.Y, sail.Z + 0.5 + spoke.Z);
			return Reachable();
		}

		for (int i = 0; i < steps; i++) {
			ahead = Vector3.Transform(ahead, turn);
			behind = Vector3.Transform(behind, back);
			if (At(behind) || At(ahead)) return true;
		}
		return false;
	}

	public static Ledge? CheckGrip(EntityPlayer player, GripPoint request, Vec3d eye, double retryReach, int graceMs,
		out bool waitForPosition, Vec3d? from = null) {
		waitForPosition = false;
		if (request.SpearId != 0) return null;

		var hub = new Vec3d(request.X + 0.5, request.Y + 0.5, request.Z + 0.5);
		if (!LedgeFinder.InReach(player, hub, WindmillGrip.SearchRadius + 3, from)) {
			waitForPosition = LedgeFinder.InReach(player, hub, WindmillGrip.SearchRadius + retryReach, from);
			return null;
		}

		var sail = WindmillGrip.Resolve(player.World, request.ToLedge(player.Pos.Dimension), out _, out var angular);
		if (sail == null) return null;
		var live = sail.Position;
		bool visible = LedgeFinder.InReach(player, live, WindmillGrip.CatchAllowance, from)
			? LedgeFinder.Visible(player, eye, live)
			: WindmillGrip.PhaseVisible(player, sail, angular, graceMs, eye, from);
		if (visible) return sail;

		waitForPosition = WindmillGrip.TryReach(player, sail, angular, retryReach, graceMs, from, null);
		return null;
	}

	private static bool PhaseVisible(EntityPlayer player, Ledge sail, Vector3 angular, int graceMs, Vec3d eye, Vec3d? from) {
		var low = eye.Clone();
		var high = eye.Clone();
		double farthest = 0;
		bool reachable = false;
		WindmillGrip.TryReach(player, sail, angular, WindmillGrip.CatchAllowance, graceMs, from, Include);
		if (!reachable) return false;

		// a phase-adjusted catch must see the live sail too
		var live = sail.Position;
		Include(live);
		using var search = GripSearch.Open(player, cacheShapes: false);
		double sightRange = Math.Sqrt(farthest);
		LedgeFinder.CollectBoxes(player.World.BlockAccessor, player.Pos.Dimension, low, high, search, search.Boxes);
		search.Beams(eye, sightRange);
		search.Entities(new GripRay(eye, default, 0, (float)sightRange));
		return Visible(live) && WindmillGrip.TryReach(player, sail, angular, WindmillGrip.CatchAllowance, graceMs, from, Visible);

		bool Visible(Vec3d point) => LedgeFinder.Visible(player, eye, point, search.Boxes);

		bool Include(Vec3d point) {
			reachable = true;
			low.Set(Math.Min(low.X, point.X), Math.Min(low.Y, point.Y), Math.Min(low.Z, point.Z));
			high.Set(Math.Max(high.X, point.X), Math.Max(high.Y, point.Y), Math.Max(high.Z, point.Z));
			farthest = Math.Max(farthest, eye.SquareDistanceTo(point));
			return false;
		}
	}
}
