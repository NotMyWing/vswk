using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace VintageKnuckle;

public sealed partial class VintageKnuckleSystem : ModSystem {
	private const string ModId = "vintageknuckle";
	private static volatile VintageKnuckleSystem? active;

	private ICoreClientAPI api = null!;
	private Harmony? harmony;
	private readonly Grip left = new(), right = new();

	private static readonly AssetLocation StretchSound = new("game", "sounds/effect/ropestretch.ogg");
	private static readonly AssetLocation SlipSound = new("game", "sounds/effect/roperip.ogg");
	private ILoadedSound? strainSound;
	private EntityTalkUtil? jumpVoice;

	private IClientNetworkChannel climbChannel = null!;
	private Ledge? sentLeft, sentRight;
	private bool sentStance;
	private int revision, leftRevision, rightRevision, sentDimension;
	private GripHud? hud;
	private GripMarkers? markers;

	private volatile bool stance;
	private bool Holding => this.left.Hold != null || this.right.Hold != null;
	private bool SentGrips => this.sentLeft != null || this.sentRight != null;
	private bool jumpDown, jumpPressed, jumpedFromGrip;
	private bool swallowLeftUp, swallowRightUp;
	private ItemStack? attackingWith;
	private bool forwardingMouse;
	private long handTick;
	private EntityPlayer? physicsPlayer;

	public override void StartClientSide(ICoreClientAPI api) {
		VintageKnuckleSystem.active?.Dispose();
		this.api = api;

		this.climbChannel = api.Network.RegisterChannel(GripChange.Channel)
			.RegisterMessageType<GripChange>().RegisterMessageType<GripState>()
			.SetMessageHandler<GripState>(this.UpdateGrips);
		VintageKnuckleSystem.active = this;

		this.harmony = new Harmony(VintageKnuckleSystem.ModId);
		this.PatchClient(this.harmony);

		api.Input.RegisterHotKey(VintageKnuckleSystem.ModId, "Vintage Knuckle: climbing stance", GlKeys.V, HotkeyType.CharacterControls);
		api.Input.SetHotKeyHandler(VintageKnuckleSystem.ModId, this.Toggle);

		api.Event.MouseDown += this.MouseDown;
		api.Event.MouseUp += this.MouseUp;
		api.Event.BeforeActiveSlotChanged += this.ChangeHotbarSlot;
		this.handTick = api.Event.RegisterGameTickListener(this.UpdateHands, 20);
		api.Event.LevelFinalize += this.PrepareMarkers;
		api.Event.LeaveWorld += this.Leave;
	}

	private void PrepareMarkers() => this.markers ??= new GripMarkers(this.api, this.left, this.right);

	private EnumHandling ChangeHotbarSlot(ActiveSlotChangeEventArgs change) => this.right.Hold != null ?
		GripSlots.ChangeHotbarSlot(this.api.World.Player, change) : EnumHandling.PassThrough;

	private bool Toggle(KeyCombination _) {
		if (!this.api.Input.MouseGrabbed) return false;

		if (this.stance) {
			this.Stop(keepGrips: true);
			return true;
		}

		if (!this.climbChannel.Connected) {
			this.api.ShowChatMessage("Climbing needs a matching version of Vintage Knuckle on the server.");
			return true;
		}
		var player = this.api.World.Player.Entity;
		if (!player.Alive || player.MountedOn != null) return false;
		if (!this.Holding && !player.LeftHandItemSlot.Empty && !player.RightHandItemSlot.Empty) {
			this.api.ShowChatMessage("Free a hand first.");
			return true;
		}

		this.WatchPlayer(player);
		this.jumpDown = player.Controls.Jump;
		this.stance = true;
		if (this.api.Input.MouseButton.Left) {
			if (this.api.Input.InWorldMouseButton.Left) this.ForwardMouse(EnumMouseButton.Left, false);
			this.left.Press();
			this.swallowLeftUp = true;
		}
		if (this.api.Input.MouseButton.Right && player.RightHandItemSlot.Empty) {
			if (this.api.Input.InWorldMouseButton.Right) this.ForwardMouse(EnumMouseButton.Right, false);
			this.right.Press();
			this.swallowRightUp = true;
		}

		this.PrepareMarkers();

		this.hud ??= new GripHud(this.api, this.left, this.right);
		this.hud.ShowFreeHands = true;
		this.UpdateAim(player, this.CanReach(player));
		this.SyncGrips();
		this.hud.TryOpen();
		return true;
	}

	private void MouseDown(MouseEvent e) {
		if (this.forwardingMouse || !this.api.Input.MouseGrabbed) return;
		if (!this.stance) {
			if (this.right.Hold != null && e.Button == EnumMouseButton.Left) {
				e.Handled = true;
				this.swallowLeftUp = true;
			}
			return;
		}
		if (e.Button == EnumMouseButton.Left) {
			this.swallowLeftUp = true;
			e.Handled = true;
			this.PressHand(this.left);
			return;
		}
		if (e.Button != EnumMouseButton.Right) return;

		if (this.swallowRightUp || this.right.Hold != null) {
			e.Handled = true;
			return;
		}
		var slot = this.api.World.Player.Entity.RightHandItemSlot;
		var stack = slot.Itemstack;
		if (stack?.Class == EnumItemClass.Block || stack?.Collectible.GetTool(slot) == EnumTool.Spear) return;

		this.swallowRightUp = true;
		if (stack != null) {
			this.attackingWith = stack;
			this.ForwardMouse(EnumMouseButton.Left, true);
		} else {
			this.PressHand(this.right);
		}
		e.Handled = true;
	}

	private void PressHand(Grip hand) {
		hand.Press();
		var player = this.api.World.Player.Entity;
		var slot = hand == this.left ? player.LeftHandItemSlot : player.RightHandItemSlot;
		if (this.CanReach(player) && slot.Empty && hand.CanCatch) {
			var grip = LedgeFinder.FindAt(player, this.View(), LedgeFinder.Feet(player.Pos));
			if (grip != null) {
				this.CatchAt(hand, grip);
				this.SyncGrips();
			}
		}
	}

	private void MouseUp(MouseEvent e) {
		if (this.forwardingMouse) return;
		if (e.Button == EnumMouseButton.Left) {
			this.left.Release();
			if (this.swallowLeftUp) e.Handled = true;
			this.swallowLeftUp = false;
		} else if (e.Button == EnumMouseButton.Right) {
			this.EndAttack();
			this.right.Release();
			if (this.swallowRightUp) e.Handled = true;
			this.swallowRightUp = false;
		}
		if (this.stance && e.Button is EnumMouseButton.Left or EnumMouseButton.Right && this.api.World.Player?.Entity is { } player)
			this.UpdateAim(player, this.CanReach(player), catchAtPose: false);
		this.SyncGrips();
	}

	private void ForwardMouse(EnumMouseButton button, bool down) {
		this.forwardingMouse = true;
		try {
			((Vintagestory.Client.NoObf.ClientMain)this.api.World).UpdateMouseButtonState(button, down);
		} finally {
			this.forwardingMouse = false;
		}
	}

	private void EndAttack() {
		if (this.attackingWith == null) return;
		this.attackingWith = null;
		this.ForwardMouse(EnumMouseButton.Left, false);
	}

	private void SyncGrips(bool jump = false) {
		var left = this.left.Hold;
		var right = this.right.Hold;
		if (!this.stance && !this.sentStance && !this.SentGrips) return;
		if (this.api.World.Player?.Entity is not { } player || !this.climbChannel.Connected) return;
		if (left == this.sentLeft && right == this.sentRight && this.stance == this.sentStance && this.sentDimension == player.Pos.Dimension) return;

		var change = new GripChange {
			Revision = ++this.revision, Dimension = player.Pos.Dimension, Stance = this.stance,
			ChangeLeft = left != this.sentLeft, ChangeRight = right != this.sentRight,
			Jump = jump
		};
		if (!this.sentStance && !this.SentGrips) this.leftRevision = this.rightRevision = change.Revision;
		if (change.ChangeLeft) {
			this.leftRevision = change.Revision;
			if (left != null) change.Left = GripPoint.ForCatch(left);
		}
		if (change.ChangeRight) {
			this.rightRevision = change.Revision;
			if (right != null) change.Right = GripPoint.ForCatch(right);
		}
		if (change.Left != null || change.Right != null) this.api.Network.SendPlayerPositionPacket();

		this.sentLeft = left;
		this.sentRight = right;
		this.sentStance = this.stance;
		this.sentDimension = change.Dimension;
		this.climbChannel.SendPacket(change);
	}

	private void UpdateGrips(GripState state) {
		if (!this.sentStance && !this.SentGrips && state.Revision != this.revision) return;

		Ledge? Update(Grip hand, ref Ledge? sent, int revision) {
			if (state.Revision < revision) return null;

			bool left = hand == this.left;
			float amount = left ? state.LeftStamina : state.RightStamina;
			float rest = left ? state.LeftRest : state.RightRest;
			bool pending = left ? state.LeftPending : state.RightPending;
			if (!pending) hand.SyncStamina(amount, rest, released: sent == null);

			int released = left ? state.ReleaseLeft : state.ReleaseRight;
			if (sent == null || released != revision || hand.Hold != sent) return null;

			bool exhausted = rest > 0;
			var slipped = exhausted ? sent : null;
			hand.LoseSupport(exhausted);
			sent = null;
			this.swingExit = null;
			return slipped;
		}

		var leftSlip = Update(this.left, ref this.sentLeft, this.leftRevision);
		var rightSlip = Update(this.right, ref this.sentRight, this.rightRevision);
		if (leftSlip != null) this.PlaySlip(leftSlip, this.sentDimension);
		if (rightSlip != null && rightSlip != leftSlip) this.PlaySlip(rightSlip, this.sentDimension);
	}

	private void WatchPlayer(EntityPlayer? player) {
		if (this.physicsPlayer == player) return;

		if (this.physicsPlayer != null) {
			this.physicsPlayer.AfterPhysicsTick -= this.AfterPlayerStep;
			this.left.Reset();
			this.right.Reset();
		}

		this.physicsPlayer = player;
		this.nativeStep = this.checkedAim = this.stepStarted = false;
		this.sweepLeft = this.sweepRight = false;
		this.swingExit = null;
		this.jumpDown = player?.Controls.Jump == true;
		this.jumpPressed = this.jumpedFromGrip = false;
		if (player != null) player.AfterPhysicsTick += this.AfterPlayerStep;
	}

	private void UpdateHands(float dt) {
		if (this.api.IsGamePaused || this.api.World.Player?.Entity is not { } player) return;
		this.WatchPlayer(player);

		var controls = player.Controls;
		bool canMove = player.Alive && player.MountedOn == null && !controls.IsFlying && !controls.NoClip;
		bool canClimb = canMove && !player.Swimming;
		bool hasInput = canClimb && this.api.Input.MouseGrabbed;

		if (this.attackingWith != null && (!hasInput || !object.ReferenceEquals(this.attackingWith, player.RightHandItemSlot.Itemstack))) {
			this.EndAttack();
		}
		if (!canMove && (this.stance || this.Holding)) this.Stop();

		var dimension = player.Pos.Dimension;
		this.left.CheckSupport(player);
		this.right.CheckSupport(player);

		int hands = (this.left.Hold != null ? 1 : 0) + (this.right.Hold != null ? 1 : 0);
		bool moving = hasInput && controls.TriesToMove;
		var before = (this.left.Available, this.right.Available);
		var leftSlip = this.left.Tick(dt, hands, player.OnGround, moving);
		var rightSlip = this.right.Tick(dt, hands, player.OnGround, moving);

		this.UpdateStrainSound(canClimb && !player.OnGround
			? Math.Max(this.left.WarningLevel, this.right.WarningLevel) : 0);
		if (leftSlip != null) this.PlaySlip(leftSlip, dimension);
		if (rightSlip != null && rightSlip != leftSlip) this.PlaySlip(rightSlip, dimension);

		if (this.stance && (!this.checkedAim || before != (this.left.Available, this.right.Available))) {
			this.UpdateAim(player, hasInput);
		}
		this.checkedAim = false;

		if (!this.stance && !this.Holding && this.hud?.IsOpened() == true) this.hud.TryClose();
		this.SyncGrips();
	}

	private bool CanReach(EntityPlayer player) => this.api.Input.MouseGrabbed && !this.api.IsGamePaused && player.Alive
		&& player.MountedOn == null && !player.Swimming && !player.Controls.IsFlying && !player.Controls.NoClip;

	private Vec3f View() => EntityPos.GetViewVector(this.api.World.Player.CameraPitch, this.api.Input.MouseYaw);

	private (bool Left, bool Right) Reaching(EntityPlayer player) =>
		(this.left.CanCatch && player.LeftHandItemSlot.Empty, this.right.CanCatch && player.RightHandItemSlot.Empty);

	private bool HasFreeHand(EntityPlayer player) =>
		(this.left.Available && player.LeftHandItemSlot.Empty) || (this.right.Available && player.RightHandItemSlot.Empty);

	private void UpdateAim(EntityPlayer player, bool hasInput, bool catchAtPose = true) {
		var lip = hasInput && this.HasFreeHand(player) ? LedgeFinder.FindAt(player, this.View(), LedgeFinder.Feet(player.Pos)) : null;
		if (catchAtPose && lip != null) {
			var (left, right) = this.Reaching(player);
			this.ReachForGrips(left, right, lip);
		}
		this.ShowReach(player, lip != null);
	}

	private void ShowReach(EntityPlayer player, bool inReach) {
		if (this.hud == null) return;
		this.hud.LeftInReach = inReach && this.left.Available && player.LeftHandItemSlot.Empty;
		this.hud.RightInReach = inReach && this.right.Available && player.RightHandItemSlot.Empty;
	}

	private void ReachForGrips(bool reachLeft, bool reachRight, Ledge? lip) {
		if (lip == null) return;
		if (reachLeft) this.CatchAt(this.left, lip);
		if (reachRight) this.CatchAt(this.right, lip);
	}

	private void CatchAt(Grip hand, Ledge lip) {
		if (!hand.Catch(lip)) return;
		if (hand == this.right && this.api.Input.InWorldMouseButton.Left) {
			this.attackingWith = null;
			this.ForwardMouse(EnumMouseButton.Left, false);
		}
		if (lip.SpearId.HasValue) return;
		var world = this.api.World;

		var block = world.BlockAccessor.GetBlock(lip.BlockId);
		var selection = new BlockSelection {
			Block = block, Position = new BlockPos(lip.X, lip.Y, lip.Z, lip.Dimension),
			Face = BlockFacing.FromNormal(new Vec3i(lip.FaceX, 0, lip.FaceZ)),
			HitPosition = new Vec3d(lip.HitX - lip.X, lip.Top - lip.Y, lip.HitZ - lip.Z)
		};
		if (block?.GetSounds(world.BlockAccessor, selection)?.Break is { } sound)
			world.PlaySoundAt(sound, lip.HitX, lip.Top + selection.Position.InternalY - lip.Y, lip.HitZ, lip.Dimension);
	}

	private void PlaySlip(Ledge lip, int dimension) {
		var pos = this.api.World.Player.Entity.Pos;
		if (lip.Dimension == dimension && pos.Dimension == dimension && WindmillGrip.Resolve(this.api.World, lip, out _) is { } point)
			this.api.World.PlaySoundAt(VintageKnuckleSystem.SlipSound, point.HitX, point.Top + pos.InternalY - pos.Y, point.HitZ, null, false, 12, 0.6f);
	}

	private void UpdateStrainSound(float level) {
		if (level <= 0) {
			this.strainSound?.Stop();
			this.strainSound?.Dispose();
			this.strainSound = null;
			return;
		}

		float volume = level * 0.65f;
		if (this.strainSound == null) {
			this.strainSound = this.api.World.LoadSound(new SoundParams {
				Location = VintageKnuckleSystem.StretchSound, ShouldLoop = true, DisposeOnFinish = false,
				RelativePosition = true, Position = new Vec3f(), Volume = volume
			});
			this.strainSound?.Start();
		} else {
			this.strainSound.SetVolume(volume);
		}
	}

	private void Stop(bool keepGrips = false) {
		this.EndAttack();
		this.stance = false;
		this.stepStarted = false;
		this.sweepLeft = this.sweepRight = false;
		if (!keepGrips || this.left.Hold == null) this.left.Release();
		if (!keepGrips || this.right.Hold == null) this.right.Release();

		if (this.hud != null) {
			this.hud.ShowFreeHands = false;
			this.hud.LeftInReach = this.hud.RightInReach = false;
		}
		if (!this.Holding) {
			this.swingExit = null;
			this.UpdateStrainSound(0);
			this.hud?.TryClose();
		}

		this.SyncGrips();
	}

	private void Leave() {
		this.jumpVoice = null;
		this.Stop();
		this.sentLeft = this.sentRight = null;
		this.sentStance = false;
		this.swallowLeftUp = this.swallowRightUp = false;
		this.jumpDown = this.jumpedFromGrip = false;
		this.WatchPlayer(null);
		this.left.Reset();
		this.right.Reset();

		this.sweepLeft = this.sweepRight = false;
		this.swingExit = null;

		this.markers?.Dispose();
		this.markers = null;
		this.hud?.Dispose();
		this.hud = null;
	}

	public override void Dispose() {
		if (VintageKnuckleSystem.active != this) return;
		VintageKnuckleSystem.active = null;
		this.api.Input.SetHotKeyHandler(VintageKnuckleSystem.ModId, null);
		this.api.Event.MouseDown -= this.MouseDown;
		this.api.Event.MouseUp -= this.MouseUp;
		this.api.Event.BeforeActiveSlotChanged -= this.ChangeHotbarSlot;
		this.api.Event.UnregisterGameTickListener(this.handTick);
		this.api.Event.LevelFinalize -= this.PrepareMarkers;
		this.api.Event.LeaveWorld -= this.Leave;
		this.harmony?.UnpatchAll(VintageKnuckleSystem.ModId);
		this.Leave();
	}
}
