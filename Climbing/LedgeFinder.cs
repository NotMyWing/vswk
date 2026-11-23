using System.Numerics;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VintageKnuckle;

internal static class LedgeFinder {
	public static Vec3d Feet(EntityPos pos) => new(pos.X, pos.Y, pos.Z);

	public static float ModelScale(EntityPlayer player) {
		var height = player.Properties?.EyeHeight ?? 0;
		return height > 0 ? (float)(height / 1.7) : 1f;
	}

	public static Ledge? FindAt(EntityPlayer player, Vec3f view, Vec3d feet, float sailStep = 0) =>
		LedgeFinder.FindAt(player, view, feet, sailStep, specialOnly: false);

	internal static Ledge? FindSpecialAt(EntityPlayer player, Vec3f view, Vec3d feet) =>
		LedgeFinder.FindAt(player, view, feet, 0, specialOnly: true);

	private static Ledge? FindAt(EntityPlayer player, Vec3f view, Vec3d feet, float sailStep, bool specialOnly) {
		using var owned = GripSearch.For(player) is not { InRay: false } ? GripSearch.Open(player, false) : null;
		var search = GripSearch.For(player)!;
		search.InRay = true;
		try {
			return LedgeFinder.SearchAt(player, view, feet, sailStep, search, specialOnly);
		} finally {
			search.InRay = false;
		}
	}

	private static Ledge? SearchAt(EntityPlayer player, Vec3f view, Vec3d feet, float sailStep, GripSearch search, bool specialOnly) {
		var pos = player.Pos;
		var eye = search.Eye.Set(feet).Add(player.LocalEyePos);
		var look = Vector3.Normalize(new Vector3(view.X, view.Y, view.Z));
		var eyeHeight = (float)player.LocalEyePos.Y;
		var range = GripAim.Range * LedgeFinder.ModelScale(player);
		var ray = new GripRay(eye, look, eyeHeight, range);
		var end = search.End.Set(eye).Add(look.X * ray.Length, look.Y * ray.Length, look.Z * ray.Length);

		var boxes = search.Boxes;
		LedgeFinder.CollectBoxes(player.World.BlockAccessor, pos.Dimension, eye, end, search, boxes);
		search.Entities(ray);
		float bestScore = float.PositiveInfinity;
		Ledge? grip = null;
		var spans = search.Spans;
		int blockCount = specialOnly ? 0 : boxes.Count;
		for (int i = 0; i < blockCount; i++) {
			var box = boxes[i];
			if (box.Block is BlockSupportBeam || box.Part is BookshelfShape.Panel) continue;
			if (box.Y2 < feet.Y - 0.05 || box.Block.BlockMaterial is EnumBlockMaterial.Ice or EnumBlockMaterial.Snow) continue;

			var surface = SurfaceGrip.Find(player, box, eye, end, boxes);
			if (surface != null && surface.Top >= feet.Y - 0.05) {
				var point = new Vector3((float)(surface.HitX - eye.X), (float)(surface.Top - eye.Y), (float)(surface.HitZ - eye.Z));
				float score = ray.Score(point);
				if (score < bestScore) {
					grip = surface;
					bestScore = score;
				}
			}

			FindEdges(box, underside: false);
			if (box.CanGripUnderside && box.Y1 >= feet.Y - 0.05) FindEdges(box, underside: true);
		}

		var nearby = NearbyGrips.Find(player, ray);
		grip = WindmillGrip.Find(player, ray, boxes, nearby.Windmills, sailStep, ref bestScore) ?? grip;
		grip = BeamGrip.Find(player, ray, boxes, search.Beams(eye, range + eyeHeight), ref bestScore) ?? grip;
		return SpearGrip.Find(player, ray, boxes, bestScore, search.Spears) ?? grip;

		void FindEdges(CollisionBox box, bool underside) {
			bool aimedAtFace = underside && box.HitsUnderside(eye, end);
			foreach (var face in BlockFacing.HORIZONTALS) {
				var n = face.Normali;
				var edge = new BlockEdge(box, face, underside);
				if (!GripAim.TryEdge(ray, edge, edge.Span, aimedAtFace, out var full)) continue;
				if (full.Score >= bestScore) continue;

				spans.Clear();
				spans.Add(edge.Span);
				for (int i = 0; i < search.Shadows.Count && spans.Count != 0; i++)
					search.Shadows[i].Trim(edge, spans, eye);
				if (spans.Count == 0) continue;
				foreach (var neighbor in boxes) {
					edge.Trim(spans, neighbor, eye);
					if (spans.Count == 0) break;
				}

				foreach (var span in spans) {
					var contact = full with { Min = Math.Max(span.Min, full.Min), Max = Math.Min(span.Max, full.Max) };
					if (contact.At < span.Min || contact.At > span.Max) {
						if (!GripAim.TryEdge(ray, edge, span, aimedAtFace, out contact)) continue;
					}

					double at = Math.Clamp(contact.At, span.Min, span.Max);
					float score = contact.Score;
					if (score >= bestScore) continue;
					if (LedgeFinder.EdgeBlocked(player, eye, edge, at, search)) {
						var sight = new EdgeSight(player, ray, edge, search, aimedAtFace);
						var visible = search.VisibleEdge(eye, edge, sight);
						if (!contact.AroundBlocker(sight, visible, bestScore, out at, out score)) continue;
					}

					var (hitX, hitZ) = edge.PointAt(at);
					var lip = new Ledge(box.X, box.Y, box.Z, pos.Dimension, box.Block.Id, n.X, n.Z, hitX, hitZ) {
						Top = edge.Height, EdgeMin = span.Min, EdgeMax = span.Max
					};

					bestScore = score;
					grip = lip;
				}
			}
		}
	}

	private readonly record struct EdgeSight(EntityPlayer Player, GripRay Ray, BlockEdge Edge, GripSearch Search, bool Underside) : GripAim.ISightLine {
		public Vec3d Eye => this.Ray.Eye;
		public Vec3d PointAt(double at) => this.Edge.SightPoint(at, this.Search.SightPoint);
		public bool Visible(double at, out SightBlocker? blocker) {
			var point = this.PointAt(at);
			blocker = BeamGrip.Blocker(this.Player, this.Eye, point);
			return blocker == null && (!this.Search.HasCustomRay || !this.Search.CustomBlocks(this.Eye, point, null, out blocker));
		}

		public float Score(double at) {
			var eye = this.Ray.Eye;
			var (x, z) = this.Edge.PointAt(at);
			var point = new Vector3((float)(x - eye.X), (float)(this.Edge.Height - eye.Y), (float)(z - eye.Z));
			return GripAim.AimScore(point, this.Ray.Look, this.Underside);
		}
	}

	internal static bool EdgeBlocked(EntityPlayer player, Vec3d eye, BlockEdge edge, double at, GripSearch? search = null) {
		var point = edge.SightPoint(at, search?.SightPoint);
		if (BeamGrip.BlocksSight(player, eye, point)) return true;
		return search != null ? search.HasCustomRay && search.CustomBlocks(eye, point, null, out _) : LedgeFinder.EntityBlocksRay(player, eye, point);
	}

	internal static bool EntityBlocksGrip(EntityPlayer player, Vec3d eye, Vec3d hit, int faceX, int faceZ) {
		const double clearance = 0.00001;
		var point = hit.AddCopy(faceX * clearance, clearance, faceZ * clearance);
		if (BeamGrip.BlocksSight(player, eye, point)) return true;
		return LedgeFinder.EntityBlocksRay(player, eye, point);
	}

	internal static bool Visible(EntityPlayer player, Vec3d eye, Vec3d point, List<CollisionBox>? boxes = null,
		Entity? ignore = null, BlockPos? anchor = null, int targetBlockId = 0) =>
		LedgeFinder.Visible(player, eye, point, boxes, ignore, anchor, targetBlockId, out _);

	internal static bool Visible(EntityPlayer player, Vec3d eye, Vec3d point, List<CollisionBox>? boxes,
		Entity? ignore, BlockPos? anchor, int targetBlockId, out SightBlocker? blocker) {
		boxes ??= LedgeFinder.FindGripBoxes(player.World.BlockAccessor, player.Pos.Dimension, eye, point);
		foreach (var box in boxes) {
			if (anchor != null && box.Block is BlockSupportBeam && box.X == anchor.X && box.Y == anchor.Y && box.Z == anchor.Z) continue;
			if (box.BlocksSight(eye, point)) {
				blocker = new SightBlocker(box);
				return false;
			}
		}

		blocker = BeamGrip.Blocker(player, eye, point, anchor, targetBlockId);
		return blocker == null && !LedgeFinder.EntityBlocksRay(player, eye, point, ignore, out blocker);
	}

	private static bool EntityBlocksRay(EntityPlayer player, Vec3d start, Vec3d end, Entity? ignore = null) =>
		LedgeFinder.EntityBlocksRay(player, start, end, ignore, out _);

	private static bool EntityBlocksRay(EntityPlayer player, Vec3d start, Vec3d end, Entity? ignore, out SightBlocker? blocker) {
		blocker = null;
		using var owned = GripSearch.For(player) == null ? GripSearch.Open(player, false) : null;
		var search = GripSearch.For(player)!;
		double lengthSq = start.SquareDistanceTo(end);
		search.Entities(new GripRay(start, default, 0, (float)Math.Sqrt(lengthSq)));
		foreach (var shadow in search.Shadows) {
			if (shadow.Entity != ignore && shadow.BlocksSight(start, end)) {
				blocker = new SightBlocker(shadow.Bounds);
				return true;
			}
		}
		if (!search.HasCustomRay) return false;

		return search.CustomBlocks(start, end, ignore, out blocker);
	}

	internal static bool InReach(EntityPlayer player, Vec3d point, double allowance = 0, Vec3d? from = null) {
		var pos = player.Pos;
		double dy = point.Y - (from?.Y ?? pos.Y);
		double height = Math.Max(player.LocalEyePos.Y, player.Properties.EyeHeight);
		double y = dy - Math.Clamp(dy, 0, height);
		double x = point.X - (from?.X ?? pos.X), z = point.Z - (from?.Z ?? pos.Z);
		double reach = GripAim.Range * LedgeFinder.ModelScale(player) + allowance;
		return x * x + y * y + z * z <= reach * reach;
	}

	internal static List<CollisionBox> FindGripBoxes(IBlockAccessor world, int dimension, Vec3d eye, Vec3d end) {
		var boxes = new List<CollisionBox>();
		LedgeFinder.CollectBoxes(world, dimension, eye, end, GripSearch.For(world, dimension), boxes);
		return boxes;
	}

	internal static bool BodyBlocked(EntityPlayer player, Vec3d start, Vec3d end) {
		if (player.CollisionBox is not { } body) return true;
		var delta = end.SubCopy(start);
		double longest = Math.Max(Math.Abs(delta.X), Math.Max(Math.Abs(delta.Y), Math.Abs(delta.Z)));
		int pieces = Math.Max(1, (int)Math.Ceiling(longest / 4));
		var from = start;
		var boxes = new List<CollisionBox>();
		for (int i = 1; i <= pieces; i++) {
			double t = (double)i / pieces;
			var to = i == pieces ? end : start.AddCopy(delta.X * t, delta.Y * t, delta.Z * t);
			var low = new Vec3d(Math.Min(from.X, to.X) + body.X1, Math.Min(from.Y, to.Y) + body.Y1, Math.Min(from.Z, to.Z) + body.Z1);
			var high = new Vec3d(Math.Max(from.X, to.X) + body.X2, Math.Max(from.Y, to.Y) + body.Y2, Math.Max(from.Z, to.Z) + body.Z2);
			LedgeFinder.CollectBoxes(player.World.BlockAccessor, player.Pos.Dimension, low, high, null, boxes, body: true);
			foreach (var box in boxes) {
				if (box.BlocksBody(from, to, body)) return true;
			}
			from = to;
		}
		return false;
	}

	internal static void CollectBoxes(IBlockAccessor world, int dimension, Vec3d eye, Vec3d end, GripSearch? search, List<CollisionBox> boxes, bool body = false) {
		boxes.Clear();
		var pos = search == null ? new BlockPos(dimension) : null;

		double minX = Math.Min(eye.X, end.X) - GripAim.Margin, maxX = Math.Max(eye.X, end.X) + GripAim.Margin;
		double minY = Math.Min(eye.Y, end.Y) - GripAim.Margin, maxY = Math.Max(eye.Y, end.Y) + GripAim.Margin;
		double minZ = Math.Min(eye.Z, end.Z) - GripAim.Margin, maxZ = Math.Max(eye.Z, end.Z) + GripAim.Margin;

		for (int y = (int)Math.Floor(minY) - 1; y <= (int)Math.Floor(maxY) + 1; y++)
		for (int z = (int)Math.Floor(minZ) - 1; z <= (int)Math.Floor(maxZ) + 1; z++)
		for (int x = (int)Math.Floor(minX) - 1; x <= (int)Math.Floor(maxX) + 1; x++) {
			var shape = search != null ? search.Read(x, y, z) : GripSearch.Read(world, pos!.Set(x, y, z), body: body);
			if (shape.Parts == null) continue;

			foreach (var part in shape.Parts) {
				if (part.X1 >= part.X2 || part.Y1 >= part.Y2 || part.Z1 >= part.Z2) continue;

				var box = shape.BoxAt(x, y, z, part);
				if (box.X2 >= minX && box.X1 <= maxX && box.Y2 >= minY && box.Y1 <= maxY &&
					box.Z2 >= minZ && box.Z1 <= maxZ) {
					boxes.Add(box);
				}
			}
		}
	}
}
