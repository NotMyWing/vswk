using System.Collections.Concurrent;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VintageKnuckle;

internal sealed partial class GripSearch {
	private static readonly ConcurrentDictionary<Type, bool> UsesOwnRay = new(), UsesBehaviorRay = new(), UsesOwnRange = new();
	private readonly Dictionary<Entity, EntityBehaviorSelectionBoxes> nativeBoxes = new();
	private readonly List<Entity> customEntities = [];
	private readonly EntityFilter customFilter;
	private readonly Ray ray = new();
	private readonly Vec3d entityCenter = new(), rayHit = new();
	private AABBIntersectionTest? intersection;
	private IWorldIntersectionSupplier? supplier;
	private Entity? ignored;
	private double dimensionOffset;
	private float radius, height;

	public readonly List<EntityShadow> Shadows = [];
	public readonly List<EntityProjectileBase> Spears = [];
	public bool HasCustomRay => this.customEntities.Count != 0;

	internal readonly record struct EntityShadow(Entity Entity, Cuboidd Bounds, bool BlocksFromInside) {
		public bool BlocksSight(Vec3d eye, Vec3d end) {
			var box = this.Bounds;
			if (!this.BlocksFromInside && box.Contains(eye.X, eye.Y, eye.Z)) return false;
			return EntityShadow.Crosses(box, eye, end);
		}

		public static bool Crosses(Cuboidd box, Vec3d eye, Vec3d end) {
			double near = 0, far = 1;
			return CollisionBox.Clip(eye.X, end.X - eye.X, box.X1, box.X2, ref near, ref far) &&
				CollisionBox.Clip(eye.Y, end.Y - eye.Y, box.Y1, box.Y2, ref near, ref far) &&
				CollisionBox.Clip(eye.Z, end.Z - eye.Z, box.Z1, box.Z2, ref near, ref far) && near < far;
		}

		public void Trim(BlockEdge edge, List<(double Min, double Max)> spans, Vec3d eye, Vec3d? end = null) {
			var box = this.Bounds;
			if (end == null) {
				if (this.BlocksFromInside || !box.Contains(eye.X, eye.Y, eye.Z)) edge.TrimShadow(spans, box, eye);
				return;
			}
			if (!this.PassesInside(eye, end)) edge.TrimWall(spans, box, eye, end);
		}

		public bool PassesInside(Vec3d eye, Vec3d end) {
			var box = this.Bounds;
			// an eye passing inside this entity can see out again
			return !this.BlocksFromInside && Math.Max(eye.X, end.X) > box.X1 && Math.Min(eye.X, end.X) < box.X2 &&
				Math.Max(eye.Y, end.Y) > box.Y1 && Math.Min(eye.Y, end.Y) < box.Y2 &&
				Math.Max(eye.Z, end.Z) > box.Z1 && Math.Min(eye.Z, end.Z) < box.Z2;
		}
	}

	private GripSearch() => this.customFilter = this.CanBlockRay;

	private bool CanBlockRay(Entity entity) => entity != this.Player && entity != this.ignored && entity.IsInteractable && GripSearch.CustomRay(entity);

	public bool CustomBlocks(Vec3d from, Vec3d to, Entity? ignore, out SightBlocker? blocker) {
		blocker = null;
		double lengthSq = from.SquareDistanceTo(to);
		double dy = this.dimensionOffset;
		Entity? nearest = null;
		int index = 0;
		if (this.supplier is { } world) {
			// terrain and nearby entities have already been collected for this search
			var test = this.intersection ??= new AABBIntersectionTest(world);
			var ray = this.LoadRay(test, from, to, dy);
			float range = (float)ray.Length;
			double closest = double.MaxValue, hitSq = 0;
			foreach (var entity in this.customEntities) {
				if (entity == ignore || entity.State == EnumEntityState.Despawned || !entity.IsInteractable ||
					!entity.InRangeOf(ray.origin, range * range, range)) continue;
				test.hitPosition = this.rayHit.Set(ray.origin);
				int hitIndex = 0;
				if (!entity.IntersectsRay(ray, test, out double distance, ref hitIndex) || !(distance < closest)) continue;
				nearest = entity;
				index = hitIndex;
				closest = distance;
				hitSq = ray.origin.SquareDistanceTo(test.hitPosition);
			}
			if (nearest == null || hitSq > lengthSq) return false;
		} else {
			if (dy != 0) {
				from = from.AddCopy(0, dy, 0);
				to = to.AddCopy(0, dy, 0);
			}
			var hit = this.TraceCustom(from, to, ignore);
			if (hit?.Entity is not { } entity) return false;
			var relative = hit.HitPosition;
			if (relative != null && from.SquareDistanceTo(entity.Pos.X + relative.X, entity.Pos.Y + relative.Y, entity.Pos.Z + relative.Z) > lengthSq) return false;
			nearest = entity;
			index = hit.SelectionBoxIndex;
		}
		if (this.NativeBox(nearest, index) && !GripSearch.OwnRange(nearest)) blocker = new SightBlocker(nearest, index);
		return true;
	}

	private Ray LoadRay(AABBIntersectionTest test, Vec3d from, Vec3d to, double dy) {
		var ray = this.ray;
		ray.origin.Set(from.X, from.Y + dy, from.Z);
		ray.dir.Set(to.X - from.X, to.Y - from.Y, to.Z - from.Z);
		test.LoadRayAndPos(ray);
		return ray;
	}

	public bool ModelHides(BlockEdge edge, (double Min, double Max) span, Vec3d start, Vec3d end) {
		if (this.nativeBoxes.Count == 0 || this.supplier == null || this.intersection is not { } test) return false;

		// the game only tests entities within the length of the individual ray
		var a = edge.SightPoint(span.Min);
		var b = edge.SightPoint(span.Max);
		static double Gap(double a, double b, double c, double d) =>
			Math.Max(0, Math.Max(Math.Min(a, b), Math.Min(c, d)) - Math.Min(Math.Max(a, b), Math.Max(c, d)));
		double x = Gap(start.X, end.X, a.X, b.X), y = Gap(start.Y, end.Y, a.Y, b.Y), z = Gap(start.Z, end.Z, a.Z, b.Z);
		float shortest = (float)Math.Sqrt(x * x + y * y + z * z);
		double dy = this.dimensionOffset;
		var origin = this.ray.origin;
		Entity? entity = null;
		float longest = (float)(Math.Sqrt(Math.Max(start.SquareDistanceTo(a), start.SquareDistanceTo(b))) +
			2 * Math.Sqrt(start.SquareDistanceTo(end)) + GripAim.SightSpacing);
		foreach (var other in this.customEntities) {
			if (GripSearch.OwnRange(other)) return false;
			if (!other.InRangeOf(origin.Set(start.X, start.Y + dy, start.Z), longest * longest, longest)) continue;
			if (entity != null || !this.nativeBoxes.ContainsKey(other)) return false;
			entity = other;
		}
		if (entity == null || !entity.InRangeOf(origin.Set(start.X, start.Y + dy, start.Z), shortest * shortest, shortest) ||
			!entity.InRangeOf(origin.Set(end.X, end.Y + dy, end.Z), shortest * shortest, shortest)) return false;

		int box = Hit(start, a);
		if (box < 0 || !this.NativeBox(entity, box) || Hit(start, b) != box || Hit(end, a) != box || Hit(end, b) != box) return false;
		// crossing into the model can let the player see out between the two poses
		return Hit(start, end) < 0 && Hit(end, start) < 0;

		int Hit(Vec3d eye, Vec3d point) {
			var ray = this.LoadRay(test, eye, point, dy);
			test.hitPosition = this.rayHit.Set(ray.origin);
			int index = 0;
			return entity.IntersectsRay(ray, test, out _, ref index) && ray.origin.SquareDistanceTo(test.hitPosition) <= eye.SquareDistanceTo(point)
				? index : -1;
		}
	}

	private EntitySelection? TraceCustom(Vec3d from, Vec3d to, Entity? ignore) {
		var previous = this.ignored;
		this.ignored = ignore;
		try {
			BlockSelection? block = null;
			EntitySelection? hit = null;
			this.Player.World.RayTraceForSelection(from, to, ref block, ref hit, static (_, _) => false, this.customFilter);
			return hit;
		} finally {
			this.ignored = previous;
		}
	}

	public void Entities(GripRay ray, double travel = 0) {
		float range = ray.Range + 4 + (float)travel;
		float tall = range + ray.EyeHeight;
		var eye = ray.Eye;
		double x = eye.X - this.entityCenter.X, z = eye.Z - this.entityCenter.Z;
		double room = this.radius - range;
		if (room >= 0 && x * x + z * z <= room * room && Math.Abs(eye.Y - this.entityCenter.Y) <= this.height - tall)
			return;

		this.entityCenter.Set(eye);
		this.radius = range + 1;
		this.height = tall + 1;
		var player = this.Player;
		var origin = eye.AddCopy(0, player.Pos.InternalY - player.Pos.Y, 0);
		var entities = player.World.GetEntitiesAround(origin, this.radius, this.height);
		this.Shadows.Clear();
		this.Spears.Clear();
		this.customEntities.Clear();
		this.nativeBoxes.Clear();
		foreach (var entity in entities) {
			if (entity == player || entity.Pos.Dimension != player.Pos.Dimension) continue;
			if (entity is EntityProjectileBase { Alive: true, Stuck: true } spear && SpearGrip.IsSpear(spear)) this.Spears.Add(spear);
			if (!entity.IsInteractable) continue;
			if (GripSearch.CustomRay(entity)) {
				this.customEntities.Add(entity);
				if (GripSearch.NativeSelection(entity) is { } boxes) this.nativeBoxes[entity] = boxes;
			} else if (entity.SelectionBox is { } bounds) {
				this.Shadows.Add(new EntityShadow(entity, bounds.ToDouble().Translate(entity.Pos.X, entity.Pos.Y, entity.Pos.Z), entity.CanAttackInside));
			}
		}
	}

	private static EntityBehaviorSelectionBoxes? NativeSelection(Entity entity) {
		if (!entity.trickleDownRayIntersects || GripSearch.OwnRay(entity) || entity.SidedProperties?.Behaviors is not { } behaviors) return null;
		EntityBehaviorSelectionBoxes? selection = null;
		foreach (var behavior in behaviors) {
			var type = behavior.GetType();
			if (type == typeof(EntityBehaviorSelectionBoxes)) {
				if (selection != null) return null;
				selection = (EntityBehaviorSelectionBoxes)behavior;
			} else if (GripSearch.UsesBehaviorRay.GetOrAdd(type, static t => t.GetMethod(nameof(EntityBehavior.IntersectsRay),
				[typeof(Ray), typeof(AABBIntersectionTest), typeof(double).MakeByRefType(), typeof(int).MakeByRefType(),
				typeof(EnumHandling).MakeByRefType()])?.DeclaringType != typeof(EntityBehavior))) return null;
		}
		return selection;
	}

	private bool NativeBox(Entity entity, int index) => this.nativeBoxes.TryGetValue(entity, out var boxes) &&
		index >= 0 && index <= boxes.selectionBoxes.Length;

	internal static bool OwnRange(Entity entity) => GripSearch.UsesOwnRange.GetOrAdd(entity.GetType(),
		static t => t.GetMethod(nameof(Entity.InRangeOf), [typeof(Vec3d), typeof(float), typeof(float)])?.DeclaringType != typeof(Entity));

	public static bool CustomRay(Entity entity) => entity.trickleDownRayIntersects || GripSearch.OwnRay(entity);

	private static bool OwnRay(Entity entity) => GripSearch.UsesOwnRay.GetOrAdd(entity.GetType(),
		static type => type.GetMethod(nameof(Entity.IntersectsRay), [typeof(Ray), typeof(AABBIntersectionTest),
			typeof(double).MakeByRefType(), typeof(int).MakeByRefType()])?.DeclaringType != typeof(Entity));
}
