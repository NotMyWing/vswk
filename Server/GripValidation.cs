using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VintageKnuckle;

internal static class GripValidation {
	public static Ledge? Verify(EntityPlayer player, GripPoint request, out bool waitForPosition, double retryReach = 3, int graceMs = 200, Vec3d? from = null) {
		waitForPosition = false;
		if (!float.IsFinite(request.AcrossX) || !float.IsFinite(request.Height) || !float.IsFinite(request.AcrossZ)) return null;
		var eye = from?.AddCopy(player.LocalEyePos) ?? LedgeFinder.Feet(player.Pos).Add(player.LocalEyePos);
		if (request.OnBeam && (request.OnSail || request.SpearId != 0)) return null;
		if (request.OnSail) return WindmillGrip.CheckGrip(player, request, eye, retryReach, graceMs, out waitForPosition, from);

		var target = request.Position;
		bool belowFeet = !request.OnBeam && request.SpearId == 0 && target.Y < (from?.Y ?? player.Pos.Y) - 0.4;
		if (!LedgeFinder.InReach(player, target, 0.35, from) || belowFeet) {
			waitForPosition = LedgeFinder.InReach(player, target, retryReach, from);
			return null;
		}

		if (request.OnBeam) return BeamGrip.CheckGrip(player, request, eye, target, out waitForPosition);
		if (request.SpearId != 0) return SpearGrip.CheckGrip(player, eye, target, request.SpearId, out waitForPosition);
		return GripValidation.CheckBlock(player, request, eye, target, from, out waitForPosition);
	}

	private static Ledge? CheckBlock(EntityPlayer player, GripPoint request, Vec3d eye, Vec3d target, Vec3d? from, out bool waitForPosition) {
		waitForPosition = false;
		if ((uint)request.Face >= BlockFacing.HORIZONTALS.Length) return null;

		var pos = new BlockPos(request.X, request.Y, request.Z, player.Pos.Dimension);
		var center = new Vec3d(pos.X + 0.5, pos.Y + 0.5, pos.Z + 0.5);
		if (!LedgeFinder.InReach(player, center, 3, from)) return null;
		var world = player.World.BlockAccessor;
		var block = world.GetBlock(pos);
		if (block.Id != request.BlockId || block.Id == 0 || block is BlockSupportBeam ||
			block.BlockMaterial is EnumBlockMaterial.Ice or EnumBlockMaterial.Snow) return null;

		var shape = GripSearch.Read(world, pos, block);
		if (shape.Parts == null) return null;

		var face = BlockFacing.ALLFACES[request.Face];
		var normal = face.Normali;
		double along = normal.X == 0 ? target.X : target.Z;
		const double tolerance = CollisionBox.ContactTolerance;
		List<CollisionBox>? neighbors = null;
		var spans = new List<(double Min, double Max)>();
		foreach (var part in shape.Parts) {
			if (part.X1 >= part.X2 || part.Y1 >= part.Y2 || part.Z1 >= part.Z2) continue;
			var box = shape.BoxAt(pos.X, pos.Y, pos.Z, part);
			bool onSurface = box.GrippableSurface && box.OnSurface(target, tolerance);
			bool onEdge = box.EdgeAt(face, target, out var edge);
			if (!onSurface && !onEdge) continue;

			waitForPosition = true;
			neighbors ??= LedgeFinder.FindGripBoxes(world, pos.dimension, eye, target);
			var surface = !onSurface ? null : box.SelectionOnly ? SurfaceGrip.Contact(player, box, eye, target, neighbors) :
				SurfaceGrip.Find(player, box, eye, target, neighbors);
			if (surface != null &&
				target.SquareDistanceTo(surface.HitX, surface.Top, surface.HitZ) <= tolerance * tolerance) {
				waitForPosition = false;
				return surface with { CollapseDelay = BlockCollapse.Delay(player.World, block) };
			}
			if (!onEdge) continue;

			spans.Clear();
			spans.Add(edge.Span);
			foreach (var neighbor in neighbors) {
				edge.Trim(spans, neighbor, eye);
				if (spans.Count == 0) break;
			}
			foreach (var span in spans) {
				if (along < span.Min - tolerance || along > span.Max + tolerance) continue;
				var (x, z) = edge.PointAt(Math.Clamp(along, span.Min, span.Max));
				var lip = new Ledge(pos.X, pos.Y, pos.Z, pos.dimension, block.Id, normal.X, normal.Z, x, z) {
					Top = edge.Height,
					EdgeMin = span.Min,
					EdgeMax = span.Max,
					CollapseDelay = BlockCollapse.Delay(player.World, block)
				};
				if (LedgeFinder.EdgeBlocked(player, eye, edge, Math.Clamp(along, span.Min, span.Max))) continue;
				waitForPosition = false;
				return lip;
			}
		}
		return null;
	}
}
