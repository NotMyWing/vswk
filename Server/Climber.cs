using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace VintageKnuckle;

internal sealed class Climber {
	private const int NativeJumpWaitMs = 200;
	public const string SaveKey = "vintageknuckle-stamina";

	public readonly IServerPlayer Owner;
	public readonly GripPositions Positions = new();
	public readonly GripStamina Stamina = new();
	public EntityPlayer Entity;
	public readonly Hand Left, Right;
	public bool Stance, Grounded;
	public int Revision, LastImpact;
	public long LastTick, NextSync;
	public double FallMotion;
	public GripState? LastSent;
	private bool hungry;
	private readonly BlockPos footing = new(0);
	private readonly Action<Climber>? beforeJump;
	private readonly Queue<long> jumpKeys = new(), gripJumps = new();

	public int InputGraceMs {
		get {
			const int baseWait = 200, maxWait = 1500;
			float ping = this.Owner.Ping;
			if (!float.IsFinite(ping) || ping <= 0) return baseWait;

			return (int)Math.Round(Math.Min(maxWait, baseWait + ping * 1000d));
		}
	}

	public bool Holding => this.Left.Hold != null || this.Right.Hold != null;
	public bool Climbing => this.Stance || this.Holding || this.Left.Pending || this.Right.Pending;
	public bool Rested => this.Left.Stamina.Amount == 100 && this.Right.Stamina.Amount == 100;

	public bool FinishedPending => !this.Stamina.Pending && ((this.LastSent?.LeftPending == true && !this.Left.Pending) ||
		(this.LastSent?.RightPending == true && !this.Right.Pending));
	public bool NeedsSync => this.LastSent?.Revision != this.Revision || this.LastSent.LeftStamina != this.Left.Stamina.Amount ||
		this.LastSent.RightStamina != this.Right.Stamina.Amount;

	public bool CanCatch(Hand hand) {
		if (this.Entity.Swimming) return false;
		var slot = hand == this.Left ? this.Entity.LeftHandItemSlot : this.Entity.RightHandItemSlot;
		return slot is { Empty: true };
	}

	public Climber(IServerPlayer owner, Action<Climber>? beforeJump = null) {
		this.beforeJump = beforeJump;
		this.Owner = owner;
		this.Left = new Hand(GripPoint.LeftKey, this.Stamina.Left);
		this.Right = new Hand(GripPoint.RightKey, this.Stamina.Right);
		this.Entity = owner.Entity;
		this.LastTick = this.Entity.World.ElapsedMilliseconds;
		if (owner.GetModdata(Climber.SaveKey) is not { } data) return;
		var state = SerializerUtil.Deserialize<GripState>(data);
		this.Left.Stamina.Set(state.LeftStamina, state.LeftRest);
		this.Right.Stamina.Set(state.RightStamina, state.RightRest);
	}

	public sealed class Hand(string key, GripStamina.Balance balance) {
		public const int ValidationWindowMs = 50;
		public HandStamina Stamina => balance.Stamina;
		public Ledge? Hold;
		public readonly Vec3d Point = new();
		public readonly BlockPos Block = new(0);
		public int Revision;
		public long CollapseAt, KickAt, GrabAfter, LastSailReach;
		public int Validations;
		private long validationWindow;

		public bool CanValidate(long now) {
			const int checksPerHand = 3;
			long window = now / Hand.ValidationWindowMs;
			if (this.validationWindow != window) {
				this.validationWindow = window;
				this.Validations = 0;
			}
			return this.Validations < checksPerHand;
		}

		public GripAttempt? Active;
		public GripAttempt? Waiting => this.Active is { Valid: null } ? this.Active : null;
		public readonly List<GripAttempt> Released = [];
		public bool Pending => this.Waiting != null || this.Released.Count != 0;

		public GripAttempt? NextAttempt(long now, long latestPosition) {
			bool canValidate = this.CanValidate(now);
			var next = this.Waiting;
			if (next != null && !next.Due(now, latestPosition, canValidate, released: false)) next = null;

			foreach (var released in this.Released) {
				if (!released.Due(now, latestPosition, canValidate, released: true)) continue;

				// older retries get a turn even while fresh catches are coming in
				if (next == null || released.NextValidation < next.NextValidation ||
					(released.NextValidation == next.NextValidation && released.Catch.Revision < next.Catch.Revision)) next = released;
			}
			return next;
		}

		public void Drop(EntityPlayer player) {
			this.Active = null;
			this.KickAt = 0;
			this.Set(player, null);
		}

		public int CheckStamina(EntityPlayer player, GripStamina ledger, bool left) {
			if (this.Hold == null || this.Active is not { } grip || this.KickAt != 0) return 0;
			if (ledger.Pending ? !ledger.Exhausted(grip, left) : balance.Holding(grip)) return 0;
			this.Drop(player);
			return this.Revision;
		}

		public void Set(EntityPlayer player, Ledge? hold) {
			if (this.Hold == hold) return;
			this.Hold = hold;
			if (hold != null) {
				if (hold.SailPoint.HasValue) this.LastSailReach = player.World.ElapsedMilliseconds;
				this.Point.Set(hold.HitX, hold.Top, hold.HitZ);
				this.Block.Set(hold.X, hold.Y, hold.Z);
				this.Block.dimension = hold.Dimension;
			}
			// removing a watched attribute makes the game resend the whole tree
			player.WatchedAttributes.SetBytes(key, hold == null ? [] : SerializerUtil.Serialize(GripPoint.ForObservers(hold)));
		}

		public void CancelPending() {
			if (this.Waiting is { } waiting) waiting.Valid = false;
			foreach (var grip in this.Released) grip.Valid ??= false;
			this.Released.Clear();
		}
	}

	public void SettleStamina(ref int left, ref int right) {
		this.Stamina.Settle();
		if (this.Stamina.CorrectionRequired) {
			Correct(this.Left, ref left);
			Correct(this.Right, ref right);
		}
		int slipLeft = this.Left.CheckStamina(this.Entity, this.Stamina, true);
		int slipRight = this.Right.CheckStamina(this.Entity, this.Stamina, false);
		if (slipLeft != 0) left = slipLeft;
		if (slipRight != 0) right = slipRight;

		void Correct(Hand hand, ref int released) {
			hand.Released.RemoveAll(static grip => grip.Valid.HasValue);
			if (hand.Active is not { Valid: false }) return;
			hand.Drop(this.Entity);
			released = hand.Revision;
		}
	}

	public void KickOff(Hand hand) {
		if (hand.Active is not { } grip) return;
		long now = this.Entity.World.ElapsedMilliseconds;
		if (now < grip.Starts) return;

		if (hand.KickAt == 0) this.ChargeJump(hand, grip, now);
		hand.Drop(this.Entity);
	}

	private void ChargeJump(Hand hand, GripAttempt grip, long now) {
		this.Stamina.Jump(grip, hand == this.Left);
		hand.GrabAfter = now + HandStamina.JumpDelayMs;
	}

	public void OnAction(EnumEntityAction action, bool down, ref EnumHandling handling) {
		if (action != EnumEntityAction.Jump || !down || this.Entity.Swimming) return;

		long now = this.Entity.World.ElapsedMilliseconds;
		if (Climber.MatchJump(this.gripJumps, now)) return;
		if (!this.Holding && this.Left.Waiting == null && this.Right.Waiting == null) return;

		this.beforeJump?.Invoke(this);
		this.RememberJump(this.jumpKeys, now);
		Schedule(this.Left);
		Schedule(this.Right);

		void Schedule(Hand hand) {
			if (hand.KickAt != 0 || hand.Active is not { } grip || now < grip.Starts) return;
			this.ChargeJump(hand, grip, now);
			hand.KickAt = now + Climber.NativeJumpWaitMs;
		}
	}

	public void GripJumpReported(GripChange change) {
		bool left = change.ChangeLeft && change.Left == null;
		bool right = change.ChangeRight && change.Right == null;
		if (!left && !right) return;

		long now = this.Entity.World.ElapsedMilliseconds;
		// vanilla sends the jump key separately
		if (Climber.MatchJump(this.jumpKeys, now)) return;

		bool kickedLeft = left && (this.Left.Hold != null || this.Left.Waiting != null);
		bool kickedRight = right && (this.Right.Hold != null || this.Right.Waiting != null);
		if (kickedLeft || kickedRight) this.RememberJump(this.gripJumps, now);
	}

	private static bool MatchJump(Queue<long> events, long now) {
		while (events.TryDequeue(out long expires)) {
			if (expires >= now) return true;
		}
		return false;
	}

	private void RememberJump(Queue<long> events, long now) {
		const int maxUnmatched = 16;
		while (events.TryPeek(out long expires) && (expires < now || events.Count >= maxUnmatched)) events.Dequeue();
		events.Enqueue(now + this.InputGraceMs);
	}

	public bool HasFooting() {
		var player = this.Entity;
		if (!player.OnGround || player.CollisionBox is not { } body) return false;

		var pos = player.Pos;
		var world = player.World.BlockAccessor;
		var blockPos = this.footing;
		blockPos.dimension = pos.Dimension;
		double feet = pos.Y + body.Y1;
		const double tolerance = 0.025;
		for (int x = (int)Math.Floor(pos.X + body.X1); x < pos.X + body.X2; x++)
		for (int z = (int)Math.Floor(pos.Z + body.Z1); z < pos.Z + body.Z2; z++)
		for (int y = (int)Math.Floor(feet - tolerance) - 1; y <= feet + tolerance; y++) {
			blockPos.Set(x, y, z);
			var block = world.GetBlock(blockPos);
			if (block.Id == 0 || block.GetCollisionBoxes(world, blockPos) is not { } boxes) continue;
			foreach (var part in boxes) {
				if (part.X1 >= part.X2 || part.Y1 >= part.Y2 || part.Z1 >= part.Z2) continue;
				var box = new CollisionBox(x, y, z, block, part);
				if (Math.Abs(feet - box.Y2) > tolerance) continue;
				if (box.X2 > pos.X + body.X1 && box.X1 < pos.X + body.X2 &&
					box.Z2 > pos.Z + body.Z1 && box.Z1 < pos.Z + body.Z2) return true;
			}
		}
		return false;
	}

	public void UpdateHunger(bool value) {
		if (value == this.hungry) return;
		this.hungry = value;
		if (value) this.Entity.Stats.Set("hungerrate", "vintageknuckle", 0.6f, persistent: false);
		else this.Entity.Stats.Remove("hungerrate", "vintageknuckle");
	}

	public void Stop() {
		this.Positions.Clear();
		this.Stance = false;
		this.jumpKeys.Clear();
		this.gripJumps.Clear();
		this.FallMotion = 0;
		this.Left.CancelPending();
		this.Right.CancelPending();
		this.Stamina.Settle();
		if (this.Left.KickAt != 0) this.KickOff(this.Left);
		if (this.Right.KickAt != 0) this.KickOff(this.Right);
		this.Left.Drop(this.Entity);
		this.Right.Drop(this.Entity);
		this.UpdateHunger(false);
	}

	public GripState Snapshot() => new() {
		Revision = this.Revision,
		LeftStamina = this.Left.Stamina.Amount, RightStamina = this.Right.Stamina.Amount,
		LeftRest = this.Left.Stamina.Rest, RightRest = this.Right.Stamina.Rest,
		LeftPending = this.Stamina.Pending || this.Left.Pending, RightPending = this.Stamina.Pending || this.Right.Pending
	};

	public void Save() {
		var (left, right) = this.Stamina.Confirmed();
		if (left.Amount == 100 && right.Amount == 100) {
			this.Owner.RemoveModdata(Climber.SaveKey);
			return;
		}
		var state = new GripState { LeftStamina = left.Amount, LeftRest = left.Rest, RightStamina = right.Amount, RightRest = right.Rest };
		this.Owner.SetModdata(Climber.SaveKey, SerializerUtil.Serialize(state));
	}
}
