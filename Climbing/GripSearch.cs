using System.Numerics;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VintageKnuckle;

internal sealed partial class GripSearch : IDisposable {
	[ThreadStatic] private static GripSearch? active, spare;
	public readonly GripSweep.Candidates Candidates = new();
	public readonly Vec3d Eye = new(), End = new(), SightPoint = new();
	private readonly Dictionary<(int X, int Y, int Z), Shape> shapes = new();
	private readonly BlockPos pos = new(0);
	private readonly Vec3d beamCenter = new();
	private readonly List<BeamGrip.Beam> beams = [];
	private readonly List<BEBehaviorSupportBeam> beamHolders = [];
	private double beamRadius;
	private Vec3d? sightEye;
	private readonly Dictionary<BlockEdge, GripAim.SightSamples?> edgeSight = new();
	private readonly Dictionary<(GripSegment, Entity?, BlockPos?, int), GripAim.SightSamples?> shaftSight = new();
	private GripSearch? previous;
	private IBlockAccessor blocks = null!;

	public EntityPlayer Player { get; private set; } = null!;
	public readonly List<CollisionBox> Boxes = [];
	public readonly List<(double Min, double Max)> Spans = [];
	public bool InRay;
	private bool cacheShapes;

	internal readonly record struct Shape(Block Block, Cuboidf[]? Parts, bool SelectionOnly, bool GrippableSurface) {
		public CollisionBox BoxAt(int x, int y, int z, Cuboidf part) => new(x, y, z, this.Block, part, this.SelectionOnly, this.GrippableSurface);
	}

	public static GripSearch? For(EntityPlayer player) => GripSearch.active?.Player == player ? GripSearch.active : null;

	public static GripSearch? For(IBlockAccessor world, int dimension) =>
		GripSearch.active is { } search && search.pos.dimension == dimension && search.blocks == world ? search : null;

	public static GripSearch Open(EntityPlayer player, bool cacheShapes) {
		var search = GripSearch.spare ?? new GripSearch();
		GripSearch.spare = null;
		search.Player = player;
		search.blocks = player.World.BlockAccessor;
		search.supplier = player.World as IWorldIntersectionSupplier;
		if (search.intersection != null) search.intersection.bsTester = search.supplier;
		search.dimensionOffset = player.Pos.InternalY - player.Pos.Y;
		search.pos.dimension = player.Pos.Dimension;
		search.cacheShapes = cacheShapes;
		search.previous = GripSearch.active;
		GripSearch.active = search;
		return search;
	}

	public Shape Read(int x, int y, int z) {
		if (this.cacheShapes && this.shapes.TryGetValue((x, y, z), out var shape)) return shape;
		this.pos.Set(x, y, z);
		shape = GripSearch.Read(this.blocks, this.pos);
		if (this.cacheShapes) this.shapes.Add((x, y, z), shape);
		return shape;
	}

	internal static Shape Read(IBlockAccessor world, BlockPos pos, Block? block = null, bool body = false) {
		block ??= world.GetBlock(pos);
		bool selection = false;
		var parts = block.Id == 0 ? null : CollisionBox.Parts(block, world, pos, out selection);
		if (parts is not { Length: > 0 }) return new Shape(block, parts, selection, false);
		if (!body && block is (BlockBookshelf or BlockClutterBookshelf) && BookshelfShape.At(block, world, pos) is { } boards)
			return new Shape(block, boards, SelectionOnly: false, GrippableSurface: false);
		return new Shape(block, parts, selection, CollisionBox.CanGripSurface(block, pos));
	}

	public List<BeamGrip.Beam> Beams(Vec3d eye, double range) {
		double room = this.beamRadius - range;
		if (room >= 0 && this.beamRadius > 0 && this.beamCenter.SquareDistanceTo(eye) <= room * room) return this.beams;

		this.beamCenter.Set(eye);
		this.beamRadius = range + 1;
		this.beams.Clear();
		BeamGrip.CollectNearbyAnchors(this.Player, eye, this.beamRadius, this.beamHolders);
		foreach (var holder in this.beamHolders) {
			if (holder.Beams is not { } placed) continue;
			var pos = holder.Blockentity.Pos;
			var local = new Vector3((float)(eye.X - pos.X), (float)(eye.Y - pos.Y), (float)(eye.Z - pos.Z));
			foreach (var beam in placed) {
				var sections = BeamPath.For(beam);
				var bounds = BeamPath.CrossSection(this.Player.Api, beam.Block);
				double width = Math.Max(Math.Abs(bounds.X), Math.Abs(bounds.Z)) + Math.Max(Math.Abs(bounds.Y), Math.Abs(bounds.W));
				double rangeSq = (this.beamRadius + width) * (this.beamRadius + width);
				foreach (var section in sections) {
					if (section.DistanceSquared(local) > rangeSq) continue;
					this.beams.Add(new BeamGrip.Beam(pos, beam.BlockId, sections, bounds));
					break;
				}
			}
		}
		return this.beams;
	}

	public void CacheSight(Vec3d? eye) {
		this.edgeSight.Clear();
		this.shaftSight.Clear();
		this.sightEye = eye;
	}

	public bool HiddenEdge(BlockEdge edge) => this.edgeSight.TryGetValue(edge, out var visible) && visible?.Covered == true;

	public bool MaySee(BlockEdge edge, GripAim.Contact contact, Vec3d eye) {
		if (!this.edgeSight.TryGetValue(edge, out var samples) || samples is not { Count: 0 }) return true;
		if (samples.Covered) return false;
		// native ray tests can describe shapes narrower than the sample spacing
		if (samples.LastBlocked != contact.At) {
			if (!LedgeFinder.EdgeBlocked(this.Player, eye, edge, contact.At, this)) return true;
			samples.LastBlocked = contact.At;
		}
		return contact.Max - contact.Min < GripAim.SightSpacing &&
			(!LedgeFinder.EdgeBlocked(this.Player, eye, edge, contact.Min, this) || !LedgeFinder.EdgeBlocked(this.Player, eye, edge, contact.Max, this));
	}

	public List<double>? VisibleEdge<T>(Vec3d eye, BlockEdge edge, T sight) where T : struct, GripAim.ISightLine {
		if (this.sightEye == null || this.sightEye.SquareDistanceTo(eye) != 0) return null;
		if (!this.edgeSight.TryGetValue(edge, out var visible) && this.customEntities.Count == this.nativeBoxes.Count) {
			this.edgeSight.Add(edge, null);
			return null;
		}
		return visible ?? (this.edgeSight[edge] = GripAim.VisibleSamples(sight, edge.Span.Min, edge.Span.Max));
	}

	public List<double>? VisibleShaft(GripSegment segment, GripSegment.ShaftSight sight) {
		var eye = sight.Ray.Eye;
		if (this.sightEye == null || this.sightEye.SquareDistanceTo(eye) != 0) return null;
		var key = (segment, sight.Spear, sight.Anchor, sight.BlockId);
		if (!this.shaftSight.TryGetValue(key, out var visible) && this.customEntities.Count == this.nativeBoxes.Count) {
			this.shaftSight.Add(key, null);
			return null;
		}
		if (visible == null) {
			var lo = Vector3.Min(Vector3.Zero, Vector3.Min(segment.Start, segment.End));
			var hi = Vector3.Max(Vector3.Zero, Vector3.Max(segment.Start, segment.End));
			var boxes = LedgeFinder.FindGripBoxes(this.blocks, this.pos.dimension, eye.AddCopy(lo.X, lo.Y, lo.Z), eye.AddCopy(hi.X, hi.Y, hi.Z));
			visible = GripAim.VisibleSamples(sight with { Boxes = boxes }, 0, (segment.End - segment.Start).Length());
			this.shaftSight[key] = visible;
		}
		return visible;
	}

	public void Dispose() {
		this.Player = null!;
		this.blocks = null!;
		this.supplier = null;
		this.shapes.Clear();
		this.Candidates.Clear();
		this.customEntities.Clear();
		if (this.intersection != null) this.intersection.bsTester = null;
		this.nativeBoxes.Clear();
		this.CacheSight(null);
		this.Boxes.Clear();
		this.Spans.Clear();
		this.Shadows.Clear();
		this.Spears.Clear();
		this.radius = this.height = 0;
		this.beams.Clear();
		this.beamHolders.Clear();
		this.beamRadius = 0;
		GripSearch.active = this.previous;
		this.previous = null;
		GripSearch.spare = this;
	}
}
