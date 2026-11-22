using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace VintageKnuckle;

public sealed class VintageKnuckleServer : ModSystem {
	private ICoreServerAPI api = null!;
	private IServerNetworkChannel channel = null!;
	private Harmony? harmony;
	private long tick;
	private readonly Dictionary<string, Climber> climbers = new();

	public override void StartServerSide(ICoreServerAPI api) {
		this.api = api;
		this.harmony = new Harmony("vintageknuckle.catchimpact");
		CatchImpact.Patch(this.harmony);
		LadderMovement.Patch(this.harmony);
		GripSlots.Patch(this.harmony, new HarmonyMethod(typeof(VintageKnuckleServer), nameof(VintageKnuckleServer.CheckHandSlot)));
		BeamGrip.PatchIndex(this.harmony, EnumAppSide.Server);
		this.harmony.Patch(AccessTools.Method(typeof(EntityProjectileBase), nameof(EntityProjectileBase.CanCollect)),
			postfix: new HarmonyMethod(typeof(VintageKnuckleServer), nameof(VintageKnuckleServer.CheckSpearPickup)));
		this.harmony.Patch(AccessTools.Method(typeof(EntityItem), nameof(EntityItem.CanCollect)),
			postfix: new HarmonyMethod(typeof(VintageKnuckleServer), nameof(VintageKnuckleServer.CheckDroppedSpearPickup)));
		this.harmony.Patch(AccessTools.DeclaredMethod(typeof(EntityBehaviorPlayerPhysics), nameof(EntityBehaviorPlayerPhysics.OnReceivedClientPos)),
			postfix: new HarmonyMethod(typeof(VintageKnuckleServer), nameof(VintageKnuckleServer.AfterPlayerPosition)));
		this.harmony.Patch(AccessTools.Method(typeof(EntityPlayer), nameof(EntityPlayer.ShouldReceiveDamage)),
			postfix: new HarmonyMethod(typeof(VintageKnuckleServer), nameof(VintageKnuckleServer.CheckFallDamage)));

		this.channel = api.Network.RegisterChannel(GripChange.Channel)
			.RegisterMessageType<GripChange>().RegisterMessageType<GripState>();
		this.channel.SetMessageHandler<GripChange>(this.Change);
		api.Event.PlayerNowPlaying += this.Join;
		api.Event.PlayerDisconnect += this.Disconnect;
		api.Event.PlayerDeath += this.Died;
		api.Event.PlayerDimensionChanged += this.Stop;
		api.Event.HandInteract += this.UseHand;
		api.Event.BreakBlock += this.BreakBlock;
		api.Event.BeforeActiveSlotChanged += this.ChangeHotbarSlot;
		api.Event.GameWorldSave += this.SavePlayers;
		this.tick = api.Event.RegisterGameTickListener(this.Tick, 50);
	}

	private static void CheckSpearPickup(EntityProjectileBase __instance, Entity byEntity, ref bool __result) {
		if (!__result || __instance.World.Side != EnumAppSide.Server || !SpearGrip.IsSpear(__instance)) return;
		var server = __instance.Api.ModLoader.GetModSystem<VintageKnuckleServer>();
		if (server.InStance(byEntity) || server.HoldingSpear(__instance.EntityId)) __result = false;
	}

	private static void CheckDroppedSpearPickup(EntityItem __instance, Entity byEntity, ref bool __result) {
		if (!__result || __instance.World.Side != EnumAppSide.Server) return;
		var slot = __instance.Slot;
		if (slot.Itemstack?.Collectible?.GetTool(slot) != EnumTool.Spear) return;
		if (__instance.Api.ModLoader.GetModSystem<VintageKnuckleServer>().InStance(byEntity)) __result = false;
	}

	internal bool InStance(Entity collector) => collector is EntityPlayer { PlayerUID: { } uid } &&
		this.climbers.TryGetValue(uid, out var climber) && climber.Stance;

	private bool HoldingSpear(long id) {
		foreach (var climber in this.climbers.Values) {
			if (climber.Left.Hold?.SpearId == id || climber.Right.Hold?.SpearId == id) return true;
		}
		return false;
	}

	private static void CheckHandSlot(ItemSlot __instance, ref bool __result) {
		if (!__result || __instance.Inventory is not InventoryBasePlayer { ClassName: "hotbar" } slots ||
			slots.Api is not ICoreServerAPI api || slots.Player is not { } player) return;
		var server = api.ModLoader.GetModSystem<VintageKnuckleServer>();
		if (!server.climbers.TryGetValue(player.PlayerUID, out var climber)) return;
		if (GripSlots.Occupied(climber.Entity, __instance, climber.Left.Hold != null, climber.Right.Hold != null)) __result = false;
	}

	private EnumHandling ChangeHotbarSlot(IServerPlayer player, ActiveSlotChangeEventArgs change) =>
		this.climbers.TryGetValue(player.PlayerUID, out var climber) && climber.Right.Hold != null ?
			GripSlots.ChangeHotbarSlot(player, change) : EnumHandling.PassThrough;

	private void UseHand(IServerPlayer player, EnumHandInteractNw action, float seconds, ref EnumHandling handling) {
		if (action is not (EnumHandInteractNw.StartHeldItemUse or EnumHandInteractNw.StepHeldItemUse)) return;
		if (this.climbers.TryGetValue(player.PlayerUID, out var climber) && climber.Right.Hold != null && !player.Entity.RightHandItemSlot.Empty)
			handling = EnumHandling.PreventDefault;
	}

	private void BreakBlock(IServerPlayer player, BlockSelection selection, ref float dropQuantity, ref EnumHandling handling) {
		if (this.climbers.TryGetValue(player.PlayerUID, out var climber) && climber.Right.Hold != null)
			handling = EnumHandling.PreventSubsequent;
	}

	private static void CheckFallDamage(EntityPlayer __instance, [HarmonyArgument(0)] DamageSource source, ref bool __result) {
		if (!__result || source.Source != EnumDamageSource.Fall || __instance.World.Side != EnumAppSide.Server || CatchImpact.IsCatch(__instance)) return;
		var server = __instance.Api.ModLoader.GetModSystem<VintageKnuckleServer>();
		if (!server.climbers.TryGetValue(__instance.PlayerUID, out var climber) || !climber.Holding) return;
		server.AdvanceStamina(climber);
		if ((climber.Stamina.CanCover(true) && VintageKnuckleServer.Supported(climber, climber.Left)) ||
			(climber.Stamina.CanCover(false) && VintageKnuckleServer.Supported(climber, climber.Right))) __result = false;
	}

	private void Change(IServerPlayer sender, GripChange change) {
		var player = sender.Entity;
		if (player == null || change.Revision <= 0) return;
		this.climbers.TryGetValue(sender.PlayerUID, out var climber);
		if (climber != null && change.Revision <= climber.Revision) return;
		if (climber == null) {
			if (!change.Stance) return;
			climber = this.Track(sender);
		}

		this.AdvanceStamina(climber);
		climber.Revision = change.Revision;
		if (!VintageKnuckleServer.CanClimb(sender) || player.Pos.Dimension != change.Dimension) {
			int dropLeft = change.ChangeLeft ? change.Revision : climber.Left.Revision;
			int dropRight = change.ChangeRight ? change.Revision : climber.Right.Revision;
			climber.Stop();
			this.SendState(climber, dropLeft, dropRight);
			return;
		}

		bool entering = change.Stance && !climber.Stance;
		climber.Stance = change.Stance;
		if (entering && !climber.Left.Pending && !climber.Right.Pending) {
			climber.Positions.Clear();
		}
		if (entering || (change.ChangeLeft && change.Left != null) || (change.ChangeRight && change.Right != null))
			climber.Positions.Record(player, this.api.World.ElapsedMilliseconds);
		bool wasHolding = climber.Holding;
		if (change.Jump) climber.GripJumpReported(change);

		int left = 0, right = 0;
		if (change.ChangeLeft) left = this.Register(climber, change, true);
		if (change.ChangeRight) right = this.Register(climber, change, false);
		bool settled = this.ResolvePending(climber, ref left, ref right, wasHolding);
		if (change.ChangeLeft && climber.Left.Waiting != null) this.SetHold(climber, climber.Left, null);
		if (change.ChangeRight && climber.Right.Waiting != null) this.SetHold(climber, climber.Right, null);
		climber.SettleStamina(ref left, ref right);
		if (climber.Holding) VintageKnuckleServer.ApplyImpact(climber, change, wasHolding);
		if (entering || change.Jump || settled || climber.FinishedPending || left != 0 || right != 0 || climber.Stamina.CorrectionRequired)
			this.SendState(climber, left, right);
		climber.UpdateHunger(climber.Holding && !climber.Grounded);
	}

	private static bool CanClimb(IServerPlayer sender) => sender.Entity is { Alive: true, MountedOn: null } player &&
		!player.Controls.IsFlying && !player.Controls.NoClip && !sender.WorldData.FreeMove && !sender.WorldData.NoClip &&
		sender.WorldData.CurrentGameMode != EnumGameMode.Spectator;

	private static void ApplyImpact(Climber climber, GripChange change, bool wasHolding) {
		if (climber.LastImpact >= change.Revision) return;
		climber.LastImpact = change.Revision;
		if (wasHolding) return;
		climber.Grounded = climber.HasFooting();
		if (climber.Grounded) return;

		var player = climber.Entity;
		double drop = player.PositionBeforeFalling.Y - player.Pos.Y;
		CatchImpact.Apply(player, drop, Math.Min(climber.FallMotion, player.Pos.Motion.Y));
		player.PositionBeforeFalling.Set(player.Pos);
		climber.FallMotion = 0;
	}

	private int Register(Climber climber, GripChange change, bool left) {
		var hand = left ? climber.Left : climber.Right;
		var request = left ? change.Left : change.Right;
		var player = climber.Entity;
		long now = this.api.World.ElapsedMilliseconds;
		if (hand.Waiting is { } previous) {
			if (request == null && now >= previous.Starts) this.RememberRelease(climber, hand, previous, left);
			else previous.Valid = false;
		}
		if (hand.KickAt != 0 || (change.Jump && request == null)) climber.KickOff(hand);
		hand.Active = null;
		hand.Revision = change.Revision;

		if (request == null || !change.Stance || !climber.CanCatch(hand)) {
			hand.Drop(player);
			hand.CollapseAt = 0;
			return request == null ? 0 : hand.Revision;
		}

		long starts = Math.Max(now, hand.GrabAfter);
		int grace = climber.InputGraceMs;
		double speed = player.Pos.Motion.Length() * ClimbMotion.PhysicsTicksPerSecond;
		double reach = double.IsFinite(speed) ? Math.Max(3, speed * grace / 1000) : 3;
		var attempt = hand.Active = new GripAttempt(change, starts, starts + grace, reach) {
			ObservedPosition = climber.Positions.Latest - 1,
			PositionGeneration = climber.Positions.Generation,
			PathGeneration = climber.Positions.PathGeneration
		};
		climber.Stamina.Settle();
		if (now >= starts) climber.Stamina.Begin(attempt, left);
		if (!climber.Holding) climber.FallMotion = Math.Min(climber.FallMotion, player.Pos.Motion.Y);
		return 0;
	}

	private void RememberRelease(Climber climber, Climber.Hand hand, GripAttempt attempt, bool left) {
		if (hand.Released.Count >= 4) {
			attempt.Valid = false;
			return;
		}
		climber.Stamina.Begin(attempt, left);
		if (attempt.Valid == null) hand.Released.Add(attempt);
	}

	private void SetHold(Climber climber, Climber.Hand hand, Ledge? hold) {
		var other = hand == climber.Left ? climber.Right : climber.Left;
		if (hold == null || !hold.SameBlock(hand.Hold)) {
			hand.CollapseAt = 0;
			if (hold is { CollapseDelay: > 0 }) {
				hand.CollapseAt = hold.SameBlock(other.Hold) ? other.CollapseAt :
					this.api.World.ElapsedMilliseconds + (long)(1000 * hold.CollapseDelay);
			}
		}

		hand.Set(climber.Entity, hold);
	}

	private Ledge? VerifyCatch(Climber climber, Climber.Hand hand, GripAttempt attempt, GripPoint point, bool released,
		out bool waiting, out bool supportedNow) {
		var player = climber.Entity;
		int grace = (int)(attempt.WaitUntil - attempt.Starts);
		bool live = this.api.World.ElapsedMilliseconds <= attempt.WaitUntil;
		var from = climber.Positions.NextPose(player, point, attempt.Starts, attempt.WaitUntil, ref attempt.ObservedPosition,
			attempt.NextValidation == attempt.Starts, live);
		supportedNow = true;
		waiting = true;
		var grip = from != null || live ? GripValidation.Verify(player, point, out waiting, attempt.RetryReach, grace, from) : null;
		if (!released && from != null && grip != null) {
			if (point.OnSail) {
				var sail = WindmillGrip.Resolve(player.World, grip, out _, out var angular);
				supportedNow = sail != null && WindmillGrip.InReach(player, sail, angular, 0.6, grace);
			} else supportedNow = LedgeFinder.InReach(player, grip.Position, 0.6);
		}
		if (from != null && grip == null) waiting = true;
		if (grip != null || !waiting || point.SweptFrom == null || point.OnSail) return grip;
		if (!released && !LedgeFinder.InReach(player, point.Position, 0.6)) return null;

		// the hand can see through an opening between two vanilla position packets
		long since = attempt.Starts - grace;
		if (!released) since = Math.Max(hand.GrabAfter, since);
		var swept = climber.Positions.OnPath(player, point, since, attempt.WaitUntil, ref attempt.SweptPosition);
		if (swept == null) return null;
		grip = GripValidation.Verify(player, point, out _, attempt.RetryReach, grace, swept);
		if (grip != null) waiting = false;
		return grip;
	}

	private bool ResolvePending(Climber climber, ref int left, ref int right, bool wasHolding = false) {
		if (!climber.Left.Pending && !climber.Right.Pending &&
			climber.Left.Active?.Valid != false && climber.Right.Active?.Valid != false) return false;

		var player = climber.Entity;
		long now = this.api.World.ElapsedMilliseconds;
		bool canClimb = !player.Swimming && VintageKnuckleServer.CanClimb(climber.Owner) && climber.Positions.Matches(player);
		bool leftSettled = this.ResolveHand(climber, true, now, canClimb, wasHolding, ref left);
		bool rightSettled = this.ResolveHand(climber, false, now, canClimb, wasHolding, ref right);
		return leftSettled || rightSettled;
	}

	private bool ResolveHand(Climber climber, bool left, long now, bool canClimb, bool wasHolding, ref int rejected) {
		var hand = left ? climber.Left : climber.Right;
		foreach (var released in hand.Released) {
			if (released.Valid == null) released.CheckPending(climber, now, canClimb);
		}
		if (hand.Waiting is { } active) active.CheckPending(climber, now, canClimb && climber.CanCatch(hand));

		while (true) {
			climber.Stamina.Settle();
			var attempt = hand.NextAttempt(now, climber.Positions.Latest);
			if (attempt == null) break;

			climber.Stamina.Begin(attempt, left);
			if (attempt.Valid == false || attempt.Eligible == false) {
				attempt.Valid = false;
				continue;
			}

			bool releasedCatch = attempt != hand.Active;
			bool waiting = true;
			bool supportedNow = true;
			var grip = releasedCatch ? attempt.Contact : null;
			if (grip == null) {
				hand.Validations++;
				var point = (left ? attempt.Catch.Left : attempt.Catch.Right)!;
				grip = this.VerifyCatch(climber, hand, attempt, point, releasedCatch, out waiting, out supportedNow);
				attempt.NextValidation = now + Climber.Hand.ValidationWindowMs;
			}
			if (grip == null) {
				if (!waiting && attempt.Contact == null) attempt.Valid = false;
				continue;
			}

			attempt.Contact = grip;
			if (attempt.Eligible != true || !supportedNow) continue;
			bool alreadyHolding = wasHolding || climber.Holding;
			attempt.Valid = true;
			climber.Stamina.Settle();
			if (!releasedCatch) this.SetHold(climber, hand, grip);
			if (releasedCatch || !climber.Stamina.Exhausted(attempt, left))
				VintageKnuckleServer.ApplyImpact(climber, attempt.Catch, alreadyHolding);
		}

		if (hand.Active is { } current && (current.Valid == false ||
			(current.Valid == null && current.Expired(now, climber.Positions.Latest)))) {
			current.Valid = false;
			hand.Drop(climber.Entity);
			rejected = hand.Revision;
		}
		foreach (var released in hand.Released) {
			if (released.Valid == null && released.Expired(now, climber.Positions.Latest)) released.Valid = false;
		}
		return hand.Released.RemoveAll(static grip => grip.Valid.HasValue) != 0;
	}

	private void SendState(Climber climber, int left = 0, int right = 0) {
		var state = climber.Snapshot();
		state.ReleaseLeft = left;
		state.ReleaseRight = right;
		climber.LastSent = state;
		climber.NextSync = this.api.World.ElapsedMilliseconds + 2000;
		this.channel.SendPacket(state, climber.Owner);
		climber.Stamina.CorrectionRequired = false;
	}

	private void AdvanceStamina(Climber climber, bool notify = true) {
		var now = this.api.World.ElapsedMilliseconds;
		long from = climber.LastTick;
		climber.LastTick = now;
		if (climber.Owner.Entity is { } current && current != climber.Entity) {
			climber.Stop();
			climber.Entity = current;
		}
		if (climber.Left.Active == null && climber.Right.Active == null && climber.Rested && !climber.Stamina.Pending &&
			!climber.Stamina.CorrectionRequired) return;

		if (from < now) {
			climber.Grounded = climber.HasFooting();
			climber.Stamina.Advance(from, now, climber.Left.KickAt == 0 ? climber.Left.Active : null,
				climber.Right.KickAt == 0 ? climber.Right.Active : null, climber.Grounded, climber.Entity.Controls.TriesToMove);
		}
		int left = 0, right = 0;
		climber.SettleStamina(ref left, ref right);
		if (left != 0 || right != 0 || climber.Stamina.CorrectionRequired) {
			climber.UpdateHunger(climber.Holding && !climber.Grounded);
			if (notify) this.SendState(climber, left, right);
		}
	}

	private static void AfterPlayerPosition(EntityBehaviorPlayerPhysics __instance) {
		if (__instance.Entity is not EntityPlayer player || player.Api is not ICoreServerAPI api) return;
		var server = api.ModLoader.GetModSystem<VintageKnuckleServer>();
		if (!server.climbers.TryGetValue(player.PlayerUID, out var climber) || climber.Entity != player || !climber.Climbing) return;
		long now = api.World.ElapsedMilliseconds;
		if (player.Swimming || !VintageKnuckleServer.CanClimb(climber.Owner)) climber.Positions.Clear();
		else if (!climber.Positions.Record(player, now)) return;
		if (!climber.Left.Pending && !climber.Right.Pending) return;
		if (!climber.Holding) climber.FallMotion = Math.Min(climber.FallMotion, player.Pos.Motion.Y);
		if ((climber.Left.Pending && climber.Left.CanValidate(now)) || (climber.Right.Pending && climber.Right.CanValidate(now)))
			server.Update(climber, checkSupport: false);
	}

	private void Tick(float dt) {
		foreach (var climber in this.climbers.Values) this.Update(climber);
	}

	private void Update(Climber climber, bool checkSupport = true) {
		this.AdvanceStamina(climber);
		if (!climber.Climbing) return;
		if (!climber.Holding) {
			var motion = climber.Entity.Pos.Motion.Y;
			climber.FallMotion = climber.Left.Pending || climber.Right.Pending ? Math.Min(climber.FallMotion, motion) : motion;
		}

		int left = this.CheckKick(climber, climber.Left, true);
		int right = this.CheckKick(climber, climber.Right, false);
		bool settled = this.ResolvePending(climber, ref left, ref right);
		if (checkSupport) {
			int unsupportedLeft = this.CheckSupport(climber, climber.Left, true);
			int unsupportedRight = this.CheckSupport(climber, climber.Right, false);
			if (unsupportedLeft != 0) left = unsupportedLeft;
			if (unsupportedRight != 0) right = unsupportedRight;
		}
		climber.SettleStamina(ref left, ref right);
		bool syncDue = climber.NeedsSync && this.api.World.ElapsedMilliseconds >= climber.NextSync;
		if (settled || climber.FinishedPending || left != 0 || right != 0 || syncDue || climber.Stamina.CorrectionRequired) {
			this.SendState(climber, left, right);
		}

		climber.UpdateHunger(climber.Holding && !climber.Grounded);
	}

	private int CheckKick(Climber climber, Climber.Hand hand, bool left) {
		if (hand.KickAt == 0 || this.api.World.ElapsedMilliseconds < hand.KickAt) return 0;
		if (hand.Waiting is { } caught) this.RememberRelease(climber, hand, caught, left);
		climber.KickOff(hand);
		return hand.Revision;
	}

	private int CheckSupport(Climber climber, Climber.Hand hand, bool left) {
		if (hand.Hold is not { } hold) return 0;
		var player = climber.Entity;
		bool supported = VintageKnuckleServer.Supported(climber, hand);
		if (supported && hand.CollapseAt != 0 && this.api.World.ElapsedMilliseconds >= hand.CollapseAt && climber.Stamina.CanCover(left)) {
			hand.CollapseAt = 0;
			var block = this.api.World.BlockAccessor.GetBlock(hand.Block);
			if (BlockCollapse.Delay(this.api.World, block) == 0) return 0;
			var slot = left ? player.LeftHandItemSlot : player.RightHandItemSlot;
			supported = slot is { Empty: true } && GripValidation.Verify(player, GripPoint.ForCatch(hold), out _) != null;
			if (supported) supported = !BlockCollapse.TryCollapse(this.api.World, climber.Owner, hold);
		}
		if (supported) return 0;
		hand.Drop(player);
		return hand.Revision;
	}

	private static bool Supported(Climber climber, Climber.Hand hand) {
		var player = climber.Entity;
		if (hand.Hold is not { } hold || player.Pos.Dimension != hold.Dimension || !VintageKnuckleServer.CanClimb(climber.Owner)) return false;
		long now = player.World.ElapsedMilliseconds;
		if (hand.KickAt != 0 && now >= hand.KickAt) return false;
		if (hold.SailPoint.HasValue) {
			var sail = WindmillGrip.Resolve(player.World, hold, out _, out var angular);
			if (sail == null) return false;
			int grace = climber.InputGraceMs;
			if (WindmillGrip.InReach(player, sail, angular, 0.6, grace)) hand.LastSailReach = now;
			return now - hand.LastSailReach <= grace;
		}
		return LedgeFinder.InReach(player, hand.Point, 0.6) && hold.HasSupport(player, hand.Block, hand.Point);
	}

	private Climber Track(IServerPlayer player) {
		var climber = new Climber(player, owner => this.AdvanceStamina(owner));
		this.climbers.Add(player.PlayerUID, climber);
		player.InWorldAction += climber.OnAction;
		return climber;
	}

	private void Join(IServerPlayer player) {
		this.Disconnect(player);
		foreach (var key in new[] { GripPoint.LeftKey, GripPoint.RightKey }) {
			if (player.Entity.WatchedAttributes.GetBytes(key) is { Length: > 0 }) player.Entity.WatchedAttributes.SetBytes(key, []);
		}
		if (player.GetModdata(Climber.SaveKey) != null) this.Track(player);
	}

	private void Disconnect(IServerPlayer player) {
		if (!this.climbers.Remove(player.PlayerUID, out var climber)) return;
		climber.Owner.InWorldAction -= climber.OnAction;
		this.AdvanceStamina(climber, notify: false);
		if (climber.Left.Waiting is { } leftWaiting) leftWaiting.Valid = false;
		if (climber.Right.Waiting is { } rightWaiting) rightWaiting.Valid = false;
		int left = 0, right = 0;
		this.ResolvePending(climber, ref left, ref right);
		climber.Stop();
		climber.Save();
	}

	private void Stop(IPlayer player) {
		if (!this.climbers.TryGetValue(player.PlayerUID, out var climber) || !climber.Climbing) return;
		int left = climber.Left.Revision, right = climber.Right.Revision;
		this.AdvanceStamina(climber, notify: false);
		climber.Stop();
		this.SendState(climber, left, right);
	}

	private void Died(IServerPlayer player, DamageSource source) => this.Stop(player);

	private void SavePlayers() {
		foreach (var climber in this.climbers.Values) climber.Save();
	}

	public override void Dispose() {
		if (this.api == null) return;
		this.api.Event.UnregisterGameTickListener(this.tick);
		this.api.Event.PlayerNowPlaying -= this.Join;
		this.api.Event.PlayerDisconnect -= this.Disconnect;
		this.api.Event.PlayerDeath -= this.Died;
		this.api.Event.PlayerDimensionChanged -= this.Stop;
		this.api.Event.HandInteract -= this.UseHand;
		this.api.Event.BreakBlock -= this.BreakBlock;
		this.api.Event.BeforeActiveSlotChanged -= this.ChangeHotbarSlot;
		this.api.Event.GameWorldSave -= this.SavePlayers;
		foreach (var climber in this.climbers.Values.ToArray()) this.Disconnect(climber.Owner);
		this.harmony?.UnpatchAll(this.harmony.Id);
	}
}
