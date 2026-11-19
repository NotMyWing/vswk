using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace VintageKnuckle;

internal static class CatchImpact {
	[ThreadStatic] private static EntityPlayer? catchingPlayer;
	public static bool IsCatch(Entity player) => CatchImpact.catchingPlayer == player;

	public static void Apply(EntityPlayer player, double drop, double yMotion) {
		if (!double.IsFinite(drop) || drop <= 0 || !double.IsFinite(yMotion) || yMotion >= 0) return;

		var health = player.GetBehavior("health");
		if (health == null) return;

		var fallStart = LedgeFinder.Feet(player.Pos).Add(0, drop, 0);
		var previous = CatchImpact.catchingPlayer;
		CatchImpact.catchingPlayer = player;
		try {
			health.OnFallToGround(fallStart, yMotion);
		} finally {
			CatchImpact.catchingPlayer = previous;
		}
	}

	public static void Patch(Harmony harmony) {
		harmony.Patch(AccessTools.Method(typeof(Entity), nameof(Entity.ReceiveDamage)),
			prefix: new HarmonyMethod(typeof(CatchImpact), nameof(CatchImpact.ScaleFallHit)));
	}

	private static void ScaleFallHit(Entity __instance, [HarmonyArgument(0)] DamageSource source, [HarmonyArgument(1)] ref float damage) {
		if (CatchImpact.IsCatch(__instance) && source.Source == EnumDamageSource.Fall) damage /= 3;
	}
}
