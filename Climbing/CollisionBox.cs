using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VintageKnuckle;

internal readonly record struct CollisionBox(int X, int Y, int Z, Block Block, Cuboidf Part, bool SelectionOnly = false) {
	public const double ContactTolerance = 0.0001;
	public bool GrippableSurface => this.Block.Climbable || this.Block.Class == "BlockToolRack" || this.Block.BlockMaterial == EnumBlockMaterial.Leaves;
	public bool CanGripUnderside => (!this.SelectionOnly || CollisionBox.UsesSelectionBox(this.Block)) && !this.Block.SideSolid[BlockFacing.DOWN.Index] &&
		(this.Part.X1 > CollisionBox.ContactTolerance || this.Part.Y1 > CollisionBox.ContactTolerance || this.Part.Z1 > CollisionBox.ContactTolerance ||
		 this.Part.X2 < 1 - CollisionBox.ContactTolerance || this.Part.Y2 < 1 - CollisionBox.ContactTolerance || this.Part.Z2 < 1 - CollisionBox.ContactTolerance);
	public double X1 => this.X + (double)this.Part.X1;
	public double X2 => this.X + (double)this.Part.X2;
	public double Y1 => this.Y + (double)this.Part.Y1;
	public double Y2 => this.Y + (double)this.Part.Y2;
	public double Z1 => this.Z + (double)this.Part.Z1;
	public double Z2 => this.Z + (double)this.Part.Z2;

	private static bool UsesSelectionBox(Block block) => block.Class is "BlockOilLamp" or "BlockTorch" or "BlockTorchHolder" or "BlockToolRack";

	public static Cuboidf[]? Parts(Block block, IBlockAccessor world, BlockPos pos, out bool selectionOnly) {
		if (block is BlockSnowLayer) {
			selectionOnly = false;
			return null;
		}

		var parts = block.GetCollisionBoxes(world, pos);
		selectionOnly = parts is not { Length: > 0 } && (block.BlockMaterial == EnumBlockMaterial.Leaves || CollisionBox.UsesSelectionBox(block));
		return selectionOnly ? block.GetSelectionBoxes(world, pos) : parts;
	}

	public bool EdgeAt(BlockFacing face, Vec3d point, out BlockEdge edge) {
		bool underside = Math.Abs(this.Y1 - point.Y) < Math.Abs(this.Y2 - point.Y);
		edge = new BlockEdge(this, face, underside);
		if (underside && !this.CanGripUnderside) return false;

		double along = face.Normali.X == 0 ? point.X : point.Z;
		var (x, z) = edge.PointAt(along);
		const double tolerance = CollisionBox.ContactTolerance;
		return Math.Abs(x - point.X) <= tolerance && Math.Abs(z - point.Z) <= tolerance &&
			Math.Abs(edge.Height - point.Y) <= tolerance && along >= edge.Span.Min - tolerance && along <= edge.Span.Max + tolerance;
	}

	public bool HitsUnderside(Vec3d eye, Vec3d end) {
		if (eye.Y >= this.Y1 || end.Y < this.Y1) return false;

		double at = (this.Y1 - eye.Y) / (end.Y - eye.Y);
		double x = eye.X + (end.X - eye.X) * at, z = eye.Z + (end.Z - eye.Z) * at;
		return x >= this.X1 && x <= this.X2 && z >= this.Z1 && z <= this.Z2;
	}

	public bool OnSurface(Vec3d point, double tolerance) {
		double x = Math.Max(this.X1 - point.X, point.X - this.X2);
		double y = Math.Max(this.Y1 - point.Y, point.Y - this.Y2);
		double z = Math.Max(this.Z1 - point.Z, point.Z - this.Z2);
		return Math.Abs(Math.Max(x, Math.Max(y, z))) <= tolerance;
	}

	public bool BlocksSight(Vec3d start, Vec3d end) => !this.SelectionOnly &&
		this.Intersect(start, end, out double near, out double far) && Math.Max(0, near) < Math.Min(1, far);

	public bool Intersect(Vec3d start, Vec3d end, out double near, out double far) {
		near = double.NegativeInfinity;
		far = double.PositiveInfinity;
		return CollisionBox.Clip(start.X, end.X - start.X, this.X1, this.X2, ref near, ref far)
			&& CollisionBox.Clip(start.Y, end.Y - start.Y, this.Y1, this.Y2, ref near, ref far)
			&& CollisionBox.Clip(start.Z, end.Z - start.Z, this.Z1, this.Z2, ref near, ref far);
	}

	public bool BlocksBody(Vec3d start, Vec3d end, Cuboidf body) {
		double near = 0, far = 1;
		return !this.SelectionOnly &&
			CollisionBox.Clip(start.X, end.X - start.X, this.X1 - body.X2, this.X2 - body.X1, ref near, ref far, open: true) &&
			CollisionBox.Clip(start.Y, end.Y - start.Y, this.Y1 - body.Y2, this.Y2 - body.Y1, ref near, ref far, open: true) &&
			CollisionBox.Clip(start.Z, end.Z - start.Z, this.Z1 - body.Z2, this.Z2 - body.Z1, ref near, ref far, open: true) && near < far;
	}

	public static bool Clip(double origin, double delta, double min, double max, ref double near, ref double far, bool open = false) {
		if (delta == 0) return open ? origin > min && origin < max : origin >= min && origin <= max;
		double first = (min - origin) / delta, last = (max - origin) / delta;
		if (first > last) (first, last) = (last, first);
		near = Math.Max(near, first);
		far = Math.Min(far, last);
		return near <= far;
	}

	public (double Min, double Max) Span(bool alongX) => alongX ? (this.X1, this.X2) : (this.Z1, this.Z2);
}
