using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VintageKnuckle;

internal sealed class BookshelfShape {
	private static readonly ConditionalWeakTable<BlockEntity, BookshelfShape> Shelves = new();
	private static readonly ConditionalWeakTable<ICoreAPI, ConcurrentDictionary<AssetLocation, Board[]>> Frames = new();
	private static readonly ElementPose Rest = new();

	private Placement placement;
	private Cuboidf[]? parts;

	// panels block the reach to a shelf behind them
	internal sealed class Panel : Cuboidf;
	private readonly record struct Board(Vector3 Size, Matrix4x4 Transform, bool IsPanel);
	private readonly record struct Placement(string Type, string? BackType, string? Variant, Vector3 Rotation, Vector3 Offset);

	public static Cuboidf[]? At(Block block, IBlockAccessor world, BlockPos pos) {
		var entity = world.GetBlockEntity(pos);
		if (entity?.Api == null) return null;

		Placement placed;
		if (block is BlockBookshelf && entity is BlockEntityBookshelf { Type: { } type } shelf) {
			placed = new Placement(type, null, null, new Vector3(0, shelf.MeshAngleRad, 0), Vector3.Zero);
		} else if (block is BlockClutterBookshelf && entity.GetBehavior<BEBehaviorClutterBookshelf>() is { Type: { }, Variant: { } } be) {
			placed = new Placement(be.Type, be.Type2, be.Variant, new Vector3(be.rotateX, be.rotateY, be.rotateZ),
				new Vector3(be.offsetX, be.offsetY, be.offsetZ));
		} else return null;

		var shape = BookshelfShape.Shelves.GetOrCreateValue(entity);
		if (shape.placement == placed) return shape.parts;

		shape.parts = BookshelfShape.Read(entity, block, placed);
		shape.placement = placed;
		return shape.parts;
	}

	public static bool CoveredAbove(IBlockAccessor blocks, BlockPos pos, BlockEdge edge, Vec3d point) {
		if (point.Y < pos.Y + 1 - CollisionBox.ContactTolerance) return false;
		var above = pos.UpCopy();
		var cover = GripSearch.Read(blocks, above);
		if (cover.Parts == null) return false;

		double along = edge.AlongX ? point.X : point.Z;
		foreach (var part in cover.Parts) {
			var box = cover.BoxAt(above.X, above.Y, above.Z, part);
			if (edge.CoveredBy(box, out var span) && along > span.Min && along < span.Max) return true;
		}
		return false;
	}

	private static Cuboidf[]? Read(BlockEntity entity, Block block, Placement placed) {
		var rotation = placed.Rotation;
		AssetLocation path;
		AssetLocation? back = null;
		bool solidBack = false;
		if (block is BlockBookshelf shelf) {
			if (shelf.Cshape?.Base is not { } template) return null;
			path = template.Clone();
			path.Path = path.Path.Replace("{type}", placed.Type).Replace("{material}", ((BlockEntityBookshelf)entity).Material);
		} else {
			var be = entity.GetBehavior<BEBehaviorClutterBookshelf>();
			if (((BlockClutterBookshelf)block).GetTypeProps(be.Type, null, be) is not BookShelfTypeProps props) return null;
			path = props.ShapePath;
			if (props.group.DoubleSided) back = props.ShapePath2;
			solidBack = placed.Variant == "full";
			rotation.Y += props.Rotation.Y * GameMath.DEG2RAD;
		}

		var center = new Vector3(0.5f);
		var transform = Matrix4x4.CreateTranslation(-center) * Matrix4x4.CreateRotationZ(rotation.Z) *
			Matrix4x4.CreateRotationY(rotation.Y) * Matrix4x4.CreateRotationX(rotation.X) *
			Matrix4x4.CreateTranslation(center + placed.Offset);
		var parts = new List<Cuboidf>();
		var front = back != null || solidBack ? Matrix4x4.CreateTranslation(0, 0, 0.5f) * transform : transform;
		if (!BookshelfShape.AddFrame(entity.Api, path, front, parts)) return null;
		if (back != null) {
			var reverse = Matrix4x4.CreateScale(-1, 1, -1) * Matrix4x4.CreateTranslation(1, 0, 0.5f) * transform;
			if (!BookshelfShape.AddFrame(entity.Api, back, reverse, parts)) return null;
		} else if (solidBack) {
			BookshelfShape.AddBoxes(parts, new Vector3(1, 1, 0.5f), transform, panel: false);
		}
		return parts.ToArray();
	}

	private static bool AddFrame(ICoreAPI api, AssetLocation path, Matrix4x4 transform, List<Cuboidf> parts) {
		var frame = BookshelfShape.Frames.GetOrCreateValue(api).GetOrAdd(path, BookshelfShape.ReadFrame, api);
		foreach (var board in frame) {
			BookshelfShape.AddBoxes(parts, board.Size, board.Transform * transform, board.IsPanel);
		}
		return frame.Length > 0;
	}

	private static Board[] ReadFrame(AssetLocation path, ICoreAPI api) {
		if (SailShape.Read(api, path)?.Elements is not { } roots) return [];
		var boards = new List<Board>();
		foreach (var root in roots) BookshelfShape.Add(root, Matrix4x4.Identity, boards);
		return boards.ToArray();
	}

	private static void Add(ShapeElement element, Matrix4x4 parent, List<Board> boards) {
		if (element.Name is not { } name || element.From is not { Length: 3 } from || element.To is not { Length: 3 } to) return;
		bool board = name is "top" or "bottom" || name.StartsWith("shelf", StringComparison.Ordinal) && name != "shelf vertical";
		bool panel = name is "n" or "N" or "e" or "E" or "w" or "W" or "shelf vertical" || name.StartsWith("glass", StringComparison.OrdinalIgnoreCase);
		if (!board && !panel && !name.StartsWith("origin", StringComparison.Ordinal)) return;

		var local = element.GetLocalTransformMatrix(0, Mat4f.Create(), BookshelfShape.Rest);
		var transform = MemoryMarshal.Cast<float, Matrix4x4>(local)[0] * parent;
		if (board || panel) {
			var size = new Vector3((float)(to[0] - from[0]), (float)(to[1] - from[1]), (float)(to[2] - from[2])) / 16;
			boards.Add(new Board(size, transform, panel));
		}
		if (element.Children == null) return;
		foreach (var child in element.Children) BookshelfShape.Add(child, transform, boards);
	}

	private static void AddBoxes(List<Cuboidf> parts, Vector3 size, Matrix4x4 transform, bool panel) {
		// shorter boxes follow the lip of a diagonal board
		var axis = size.X >= size.Z ? Vector3.UnitX : Vector3.UnitZ;
		var direction = Vector3.Abs(Vector3.TransformNormal(axis, transform));
		int slices = 1;
		if (Math.Max(direction.X, Math.Max(direction.Y, direction.Z)) < 0.9999f)
			slices = Math.Max(1, (int)MathF.Ceiling(Vector3.Dot(axis, size) * 8));

		var step = size * axis / slices;
		var sliceSize = size - step * (slices - 1);
		for (int i = 0; i < slices; i++) {
			var slice = Matrix4x4.CreateTranslation(step * i) * transform;
			parts.Add(BookshelfShape.Bounds(sliceSize, slice, panel ? new Panel() : new Cuboidf()));
		}
	}

	private static Cuboidf Bounds(Vector3 size, Matrix4x4 transform, Cuboidf box) {
		var low = new Vector3(float.PositiveInfinity);
		var high = new Vector3(float.NegativeInfinity);
		for (int z = 0; z <= 1; z++)
		for (int y = 0; y <= 1; y++)
		for (int x = 0; x <= 1; x++) {
			var point = Vector3.Transform(new Vector3(x * size.X, y * size.Y, z * size.Z), transform);
			low = Vector3.Min(low, point);
			high = Vector3.Max(high, point);
		}
		// keep touching boards flush after a quarter turn
		box.Set(MathF.Round(low.X, 6), MathF.Round(low.Y, 6), MathF.Round(low.Z, 6),
			MathF.Round(high.X, 6), MathF.Round(high.Y, 6), MathF.Round(high.Z, 6));
		return box;
	}
}
