using System.Numerics;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VintageKnuckle;

internal static class BeamGrip {
	public const int MaxLength = 20;
	private static readonly ConditionalWeakTable<IWorldAccessor, AnchorIndex> Loaded = new();

	internal readonly record struct Beam(BlockPos Anchor, int BlockId, GripSegment[] Sections, Vector4 Bounds);

	private sealed class AnchorIndex {
		public readonly Dictionary<(int X, int Y, int Z, int Dimension), HashSet<BEBehaviorSupportBeam>> Chunks = new();
	}

	public static void PatchIndex(Harmony harmony, EnumAppSide side) {
		bool client = side == EnumAppSide.Client;
		var refresh = new HarmonyMethod(typeof(BeamGrip), client ? nameof(BeamGrip.LoadClient) : nameof(BeamGrip.LoadServer));
		var unload = new HarmonyMethod(typeof(BeamGrip), client ? nameof(BeamGrip.UnloadClient) : nameof(BeamGrip.UnloadServer));
		var type = typeof(BEBehaviorSupportBeam);
		harmony.Patch(AccessTools.DeclaredMethod(type, nameof(BEBehaviorSupportBeam.Initialize)), postfix: refresh);
		harmony.Patch(AccessTools.DeclaredMethod(type, nameof(BEBehaviorSupportBeam.AddBeam)), postfix: refresh);
		harmony.Patch(AccessTools.DeclaredMethod(type, nameof(BEBehaviorSupportBeam.BreakBeam)), postfix: refresh);
		harmony.Patch(AccessTools.DeclaredMethod(type, nameof(BEBehaviorSupportBeam.FromTreeAttributes)), postfix: refresh);
		harmony.Patch(AccessTools.DeclaredMethod(typeof(BlockEntityBehavior), nameof(BlockEntityBehavior.OnBlockRemoved)), prefix: unload);
		harmony.Patch(AccessTools.DeclaredMethod(typeof(BlockEntityBehavior), nameof(BlockEntityBehavior.OnBlockUnloaded)), prefix: unload);
	}

	private static void LoadClient(BEBehaviorSupportBeam __instance) => BeamGrip.Index(__instance, EnumAppSide.Client, true);
	private static void LoadServer(BEBehaviorSupportBeam __instance) => BeamGrip.Index(__instance, EnumAppSide.Server, true);
	private static void UnloadClient(BlockEntityBehavior __instance) => BeamGrip.Index(__instance, EnumAppSide.Client, false);
	private static void UnloadServer(BlockEntityBehavior __instance) => BeamGrip.Index(__instance, EnumAppSide.Server, false);

	private static int Chunk(double coordinate) => (int)Math.Floor(coordinate / GlobalConstants.ChunkSize);

	private static void Index(BlockEntityBehavior behavior, EnumAppSide side, bool add) {
		if (behavior is not BEBehaviorSupportBeam beam || beam.Api?.Side != side) return;
		var world = beam.Api.World;
		add &= beam.Beams is { Length: > 0 };
		if (!add && !BeamGrip.Loaded.TryGetValue(world, out _)) return;
		var pos = beam.Blockentity.Pos;
		var chunk = (BeamGrip.Chunk(pos.X), BeamGrip.Chunk(pos.Y), BeamGrip.Chunk(pos.Z), pos.dimension);
		var index = BeamGrip.Loaded.GetOrCreateValue(world).Chunks;
		lock (index) {
			if (add) {
				if (!index.TryGetValue(chunk, out var anchors)) index[chunk] = anchors = new();
				anchors.Add(beam);
			} else if (index.TryGetValue(chunk, out var anchors)) {
				anchors.Remove(beam);
				if (anchors.Count == 0) index.Remove(chunk);
			}
		}
	}

	internal static void CollectNearbyAnchors(EntityPlayer player, Vec3d eye, double reach, List<BEBehaviorSupportBeam> result) {
		result.Clear();
		if (!BeamGrip.Loaded.TryGetValue(player.World, out var loaded)) return;

		double range = BeamGrip.MaxLength + 2 + reach;
		int minX = BeamGrip.Chunk(eye.X - range), maxX = BeamGrip.Chunk(eye.X + range);
		int minY = BeamGrip.Chunk(eye.Y - range), maxY = BeamGrip.Chunk(eye.Y + range);
		int minZ = BeamGrip.Chunk(eye.Z - range), maxZ = BeamGrip.Chunk(eye.Z + range);
		int dimension = player.Pos.Dimension;
		var index = loaded.Chunks;
		lock (index) {
			for (int y = minY; y <= maxY; y++)
			for (int z = minZ; z <= maxZ; z++)
			for (int x = minX; x <= maxX; x++) {
				if (!index.TryGetValue((x, y, z, dimension), out var anchors)) continue;
				foreach (var holder in anchors) {
					var pos = holder.Blockentity.Pos;
					if (eye.SquareDistanceTo(pos.X, pos.Y, pos.Z) <= range * range) result.Add(holder);
				}
			}
		}
	}

	public static bool BlocksSight(EntityPlayer player, Vec3d eye, Vec3d point, BlockPos? targetAnchor = null, int targetBlockId = 0) =>
		BeamGrip.Blocker(player, eye, point, targetAnchor, targetBlockId).HasValue;

	public static SightBlocker? Blocker(EntityPlayer player, Vec3d eye, Vec3d point, BlockPos? targetAnchor = null, int targetBlockId = 0) {
		if (!BeamGrip.Loaded.TryGetValue(player.World, out _)) return null;
		using var owned = GripSearch.For(player) == null ? GripSearch.Open(player, false) : null;
		var search = GripSearch.For(player)!;
		var delta = GripSegment.Offset(point, eye);
		foreach (var beam in search.Beams(eye, Math.Sqrt(eye.SquareDistanceTo(point)))) {
			var pos = beam.Anchor;
			var fromAnchor = new Vector3((float)(eye.X - pos.X), (float)(eye.Y - pos.Y), (float)(eye.Z - pos.Z));
			if (targetAnchor != null && pos.Equals(targetAnchor) && beam.BlockId == targetBlockId && BeamGrip.Contains(beam.Sections, fromAnchor + delta)) continue;
			foreach (var section in beam.Sections) {
				if (section.BlocksSight(fromAnchor, delta, beam.Bounds)) return new SightBlocker(section, fromAnchor, beam.Bounds);
			}
		}
		return null;
	}

	private static bool Contains(GripSegment[] sections, Vector3 point) {
		foreach (var section in sections) {
			if (section.Contains(point)) return true;
		}
		return false;
	}

	public static void CollectSegments(Vec3d eye, List<Beam> beams, List<GripSegment> result) {
		foreach (var beam in beams) {
			var pos = beam.Anchor;
			var origin = new Vector3((float)(pos.X - eye.X), (float)(pos.Y - eye.Y), (float)(pos.Z - eye.Z));
			foreach (var section in beam.Sections) result.Add(new GripSegment(origin + section.Start, origin + section.End));
		}
	}

	public static Ledge? Find(EntityPlayer player, GripRay ray, List<CollisionBox> boxes, List<Beam> beams, ref float bestScore) {
		Ledge? best = null;
		foreach (var beam in beams) {
			var pos = beam.Anchor;
			var origin = new Vector3((float)(pos.X - ray.Eye.X), (float)(pos.Y - ray.Eye.Y), (float)(pos.Z - ray.Eye.Z));
			foreach (var section in beam.Sections) {
				var segment = new GripSegment(origin + section.Start, origin + section.End);
				if (segment.Find(player, ray, boxes, ref bestScore, anchor: pos, targetBlockId: beam.BlockId) is { } point)
					best = Ledge.OnSupportBeam(pos, beam.BlockId, point);
			}
		}
		return best;
	}

	public static bool Supports(IBlockAccessor world, BlockPos anchor, Ledge hold) {
		var point = new Vector3((float)(hold.HitX - anchor.X), (float)(hold.Top - anchor.Y), (float)(hold.HitZ - anchor.Z));
		const int maxOffset = BeamGrip.MaxLength + 2;
		if (point.LengthSquared() > maxOffset * maxOffset) return false;
		if (world.GetBlockEntity(anchor)?.GetBehavior<BEBehaviorSupportBeam>()?.Beams is not { } beams) return false;

		foreach (var beam in beams) {
			if (beam.BlockId != hold.BlockId) continue;
			if (BeamGrip.Contains(BeamPath.For(beam), point)) return true;
		}
		return false;
	}

	public static Ledge? CheckGrip(EntityPlayer player, GripPoint request, Vec3d eye, Vec3d point, out bool waitForPosition) {
		waitForPosition = false;
		var pos = new BlockPos(request.X, request.Y, request.Z, player.Pos.Dimension);
		var hold = Ledge.OnSupportBeam(pos, request.BlockId, point);
		if (!BeamGrip.Supports(player.World.BlockAccessor, pos, hold)) return null;

		waitForPosition = !LedgeFinder.Visible(player, eye, point, anchor: pos, targetBlockId: request.BlockId);
		return waitForPosition ? null : hold;
	}
}
