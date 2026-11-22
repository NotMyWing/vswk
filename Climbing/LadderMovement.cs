using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VintageKnuckle;

internal static class LadderMovement {
	internal static void Patch(Harmony harmony) {
		var physics = typeof(EntityBehaviorControlledPhysics);
		var patch = new HarmonyMethod(typeof(LadderMovement), nameof(LadderMovement.CheckLadders));
		harmony.Patch(AccessTools.Method(physics, nameof(EntityBehaviorControlledPhysics.ApplyTests)), transpiler: patch);
		harmony.Patch(AccessTools.Method(physics, nameof(EntityBehaviorControlledPhysics.HandleSneaking)), transpiler: patch);
	}

	private static IEnumerable<CodeInstruction> CheckLadders(IEnumerable<CodeInstruction> code) {
		var check = AccessTools.Method(typeof(Block), nameof(Block.IsClimbable));
		foreach (var op in code) {
			if (op.Calls(check)) {
				yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(op);
				yield return CodeInstruction.Call(typeof(LadderMovement), nameof(LadderMovement.CanClimb));
			} else yield return op;
		}
	}

	private static bool CanClimb(Block block, BlockPos pos, EntityBehaviorControlledPhysics physics) {
		if (!block.IsClimbable(pos)) return false;
		if (physics.Entity is not EntityPlayer player) return true;
		return player.World.Side == EnumAppSide.Client ? !VintageKnuckleSystem.InStance(player) :
			!player.Api.ModLoader.GetModSystem<VintageKnuckleServer>().InStance(player);
	}
}
