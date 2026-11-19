using HarmonyLib;
using Vintagestory.API.Common;

namespace VintageKnuckle;

internal static class GripSlots {
	public static void Patch(Harmony harmony, HarmonyMethod occupied) {
		harmony.Patch(AccessTools.Method(typeof(ItemSlot), nameof(ItemSlot.CanHold)), postfix: occupied);
		harmony.Patch(AccessTools.Method(typeof(ItemSlot), nameof(ItemSlot.CanTakeFrom)), postfix: occupied);
	}

	public static bool Occupied(EntityPlayer player, ItemSlot slot, bool left, bool right) =>
		(left && slot == player.LeftHandItemSlot) || (right && slot == player.RightHandItemSlot);

	public static EnumHandling ChangeHotbarSlot(IPlayer player, ActiveSlotChangeEventArgs change) {
		var slots = player.InventoryManager.GetHotbarInventory();
		return slots != null && (uint)change.ToSlot < slots.Count && slots[change.ToSlot] is { Empty: false } ?
			EnumHandling.PreventDefault : EnumHandling.PassThrough;
	}
}
