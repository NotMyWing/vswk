using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace VintageKnuckle;

internal static class BlockCollapse {
	public static float Delay(IWorldAccessor world, Block block) => block.BlockMaterial switch {
		EnumBlockMaterial.Leaves when block.Code?.FirstCodePart() is "leavesbranchy" or "leavesbranchystatic" => 1,
		EnumBlockMaterial.Leaves => 0.5f,
		EnumBlockMaterial.Sand when world.Config.GetBool("allowFallingBlocks") => 1,
		EnumBlockMaterial.Gravel when world.Config.GetBool("allowFallingBlocks") => 2,
		_ => 0
	};

	public static bool TryCollapse(IServerWorldAccessor world, IServerPlayer player, Ledge hold) {
		var pos = new BlockPos(hold.X, hold.Y, hold.Z, hold.Dimension);
		var blocks = world.BlockAccessor;
		var block = blocks.GetBlock(pos);
		if (block.Id != hold.BlockId || BlockCollapse.Delay(world, block) == 0) return false;
		if (!world.Claims.TryAccess(player, pos, EnumBlockAccessFlags.BuildOrBreak)) return false;

		if (block.BlockMaterial == EnumBlockMaterial.Leaves) {
			blocks.BreakBlock(pos, player);
			return blocks.GetBlockId(pos) != hold.BlockId;
		}

		var falling = block.GetBehavior<BlockBehaviorUnstableFalling>() ?? new BlockBehaviorUnstableFalling(block);
		falling.createFallingBlock(world, pos);

		return true;
	}
}
