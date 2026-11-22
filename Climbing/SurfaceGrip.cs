using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VintageKnuckle;

internal static class SurfaceGrip {
	public static bool Intersects(CollisionBox box, Vec3d eye, Vec3d end, out double at) {
		at = 0;
		if (!box.Intersect(eye, end, out double near, out double far)) return false;

		at = near < 0 && box.SelectionOnly ? far : near;
		return at >= 0 && at <= 1.000001;
	}

	public static Ledge? Find(EntityPlayer player, CollisionBox box, Vec3d eye, Vec3d end, List<CollisionBox> boxes) {
		if (!box.GrippableSurface || !SurfaceGrip.Intersects(box, eye, end, out double at)) return null;

		var hit = eye.AddCopy((end.X - eye.X) * at, (end.Y - eye.Y) * at, (end.Z - eye.Z) * at);
		return SurfaceGrip.Contact(player, box, eye, hit, boxes);
	}

	public static Ledge? Contact(EntityPlayer player, CollisionBox box, Vec3d eye, Vec3d hit, List<CollisionBox> boxes) {
		foreach (var other in boxes) {
			if (other != box && other.BlocksSight(eye, hit)) return null;
		}

		var face = SurfaceGrip.Face(player, box, eye, hit);
		var (min, max) = box.Span(face.Normali.Z != 0);
		var grip = new Ledge(box.X, box.Y, box.Z, player.Pos.Dimension, box.Block.Id, face.Normali.X, face.Normali.Z, hit.X, hit.Z) {
			Top = hit.Y, EdgeMin = min, EdgeMax = max
		};

		return LedgeFinder.EntityBlocksGrip(player, eye, hit, grip.FaceX, grip.FaceZ) ? null : grip;
	}

	public static BlockFacing Face(EntityPlayer player, CollisionBox box, Vec3d eye, Vec3d hit) {
		const double tolerance = CollisionBox.ContactTolerance;
		if (Math.Abs(hit.X - box.X1) < tolerance) return BlockFacing.WEST;
		if (Math.Abs(hit.X - box.X2) < tolerance) return BlockFacing.EAST;
		if (Math.Abs(hit.Z - box.Z1) < tolerance) return BlockFacing.NORTH;
		if (Math.Abs(hit.Z - box.Z2) < tolerance) return BlockFacing.SOUTH;
		return BlockFacing.FromVector(eye.X - hit.X, 0, eye.Z - hit.Z) ?? BlockFacing.HorizontalFromAngle(player.Pos.Yaw);
	}
}
