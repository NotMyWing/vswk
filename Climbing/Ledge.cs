using System.Numerics;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VintageKnuckle;

internal sealed record Ledge(int X, int Y, int Z, int Dimension, int BlockId,
	int FaceX, int FaceZ, double HitX, double HitZ) {
	public Vec3d? CaughtFrom { get; init; }
	public Vector3? SailPoint { get; init; }
	public long? SpearId { get; init; }
	public Vec3d Position => new(this.HitX, this.Top, this.HitZ);
	public bool OnBeam { get; init; }
	public float CollapseDelay { get; init; }
	public double Top { get; init; } = Y + 1;
	public double EdgeMin { get; init; } = FaceX == 0 ? X : Z;
	public double EdgeMax { get; init; } = (FaceX == 0 ? X : Z) + 1;

	public static Ledge OnSpear(long spearId, int dimension, double x, double y, double z) =>
		new((int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z), dimension,
			BlockId: 0, FaceX: 0, FaceZ: 1, HitX: x, HitZ: z) {
			SpearId = spearId, Top = y, EdgeMin = x, EdgeMax = x
		};

	public static Ledge OnSupportBeam(BlockPos anchor, int blockId, Vec3d point) =>
		new(anchor.X, anchor.Y, anchor.Z, anchor.dimension, blockId, 0, 1, point.X, point.Z) {
			OnBeam = true, Top = point.Y, EdgeMin = point.X, EdgeMax = point.X
		};

	public bool SameBlock(Ledge? other) => other != null && this.X == other.X && this.Y == other.Y && this.Z == other.Z &&
		this.Dimension == other.Dimension && this.BlockId == other.BlockId;

	public bool HasSupport(EntityPlayer player, BlockPos pos, Vec3d point) {
		if (this.SpearId is long id) return SpearGrip.AtPoint(player, id, point) != null;

		var blocks = player.World.BlockAccessor;
		if (this.OnBeam) return BeamGrip.Supports(blocks, pos, this);
		var block = blocks.GetBlock(pos);
		if (block.Id != this.BlockId) return false;
		if (this.SailPoint.HasValue) return true;

		var parts = CollisionBox.Parts(block, blocks, pos, out bool selectionOnly);
		if (parts == null) return false;

		var face = BlockFacing.FromVector(this.FaceX, 0, this.FaceZ);
		if (face == null) return false;
		foreach (var part in parts) {
			if (part.X1 >= part.X2 || part.Y1 >= part.Y2 || part.Z1 >= part.Z2) continue;
			var box = new CollisionBox(this.X, this.Y, this.Z, block, part, selectionOnly);
			if (box.GrippableSurface && box.OnSurface(point, CollisionBox.ContactTolerance)) return true;
			if (box.EdgeAt(face, point, out _)) return true;
		}
		return false;
	}
}
