using System.Numerics;
using System.Runtime.CompilerServices;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace VintageKnuckle;

internal sealed class NearbyGrips {
	private static readonly ConditionalWeakTable<IWorldAccessor, NearbyGrips> Searches = new();
	public readonly List<WindmillGrip> Windmills = new();
	private long refresh;
	private (int X, int Y, int Z) minChunk, maxChunk;
	private int dimension;

	public static NearbyGrips Find(EntityPlayer player, GripRay ray) {
		var nearby = NearbyGrips.Searches.GetOrCreateValue(player.World);
		nearby.Refresh(player, ray);
		return nearby;
	}

	// block -1 belongs to chunk -1
	private static int Chunk(int block) => block >> BitOperations.TrailingZeroCount(GlobalConstants.ChunkSize);

	private void Refresh(EntityPlayer player, GripRay ray) {
		var world = player.World;
		var pos = player.Pos;
		int dimension = pos.Dimension;
		long now = world.ElapsedMilliseconds;
		int x = (int)Math.Floor(ray.Eye.X);
		int y = (int)Math.Floor(ray.Eye.Y + pos.InternalY - pos.Y);
		int z = (int)Math.Floor(ray.Eye.Z);
		int radius = WindmillGrip.SearchRadius + 1 + (int)Math.Ceiling(ray.Range + ray.EyeHeight);
		var min = (X: NearbyGrips.Chunk(x - radius), Y: NearbyGrips.Chunk(y - radius), Z: NearbyGrips.Chunk(z - radius));
		var max = (X: NearbyGrips.Chunk(x + radius), Y: NearbyGrips.Chunk(y + radius), Z: NearbyGrips.Chunk(z + radius));
		if (now < this.refresh && this.dimension == dimension &&
			this.minChunk == min && this.maxChunk == max) return;

		this.minChunk = min;
		this.maxChunk = max;
		this.dimension = dimension;
		this.refresh = now + 100;
		this.Windmills.Clear();

		var blocks = world.BlockAccessor;
		for (int cy = min.Y; cy <= max.Y; cy++)
		for (int cz = min.Z; cz <= max.Z; cz++)
		for (int cx = min.X; cx <= max.X; cx++) {
			var entities = blocks.GetChunk(cx, cy, cz)?.BlockEntities;
			if (entities == null) continue;
			foreach (var entity in entities.Values) {
				if (entity.Pos.dimension != dimension) continue;
				if (WindmillGrip.At(entity) is { } mill) this.Windmills.Add(mill);
			}
		}
	}
}
