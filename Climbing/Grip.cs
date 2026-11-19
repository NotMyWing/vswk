using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VintageKnuckle;

internal sealed class Grip {
	private const float HangRadius = 1.25f;
	private volatile Ledge? hold;
	private readonly BlockPos block = new(0);
	private readonly Vec3d point = new();
	private readonly HandStamina stamina = new();
	private float cooldown, settleTime;
	private bool pressed, exhausted;

	public float Stamina => this.stamina.Amount;
	public float Strain => 1 - this.Stamina / 100;
	public Ledge? Hold => this.hold;
	public float WarningLevel => this.Hold == null ? 0 : Math.Max(0, 1 - this.Stamina / 25);

	public void Reset() {
		this.Release();
		this.stamina.Set();
		this.cooldown = 0;
		this.exhausted = false;
	}

	public void Press() => this.pressed = true;

	public void Release() {
		this.pressed = false;
		this.hold = null;
	}

	public bool Available => this.Hold == null && this.cooldown <= 0 && this.stamina.Ready;
	public bool CanCatch => this.pressed && this.Available;

	public bool Catch(Ledge ledge) {
		if (!this.CanCatch) return false;

		this.settleTime = 0;
		this.exhausted = false;
		this.block.Set(ledge.X, ledge.Y, ledge.Z);
		this.block.dimension = ledge.Dimension;
		this.point.Set(ledge.HitX, ledge.Top, ledge.HitZ);
		this.hold = ledge;
		return true;
	}

	public void CheckSupport(EntityPlayer player) {
		if (this.Hold is not { } lip) return;
		if (lip.Dimension != player.Pos.Dimension || !lip.HasSupport(player, this.block, this.point)) this.LoseSupport();
	}

	public float SwingRadius(float distance, float scale, float dt) {
		float target = Grip.HangRadius * scale;
		const float easeIn = 0.25f;
		this.settleTime = Math.Min(easeIn, this.settleTime + dt);
		if (distance <= target) return target;

		float speed = 2f * scale * (this.settleTime / easeIn);
		float excess = distance - target;
		float pull = Math.Min(speed * dt, excess * (1 - MathF.Exp(-5f * dt)));
		return distance - pull;
	}

	public void LoseSupport(bool exhausted = false) {
		this.hold = null;
		this.cooldown = exhausted ? 0 : 0.3f;
	}

	public void Launch() {
		if (this.Hold == null) return;

		this.cooldown = HandStamina.JumpDelayMs / 1000f;
		this.exhausted = this.stamina.Jump();
		this.hold = null;
	}

	public Ledge? Tick(float dt, int hands, bool grounded, bool pulling) {
		this.cooldown = Math.Max(0, this.cooldown - dt);

		var lip = this.Hold;
		if (!this.stamina.Tick(dt, lip != null, hands, grounded, pulling)) return null;

		this.exhausted = true;
		this.hold = null;
		return lip;
	}

	public void SyncStamina(float amount, float rest, bool released = false) {
		// a late slip packet would restart the timer
		if (this.exhausted && released && rest <= 0 && amount > 0) this.exhausted = false;
		else if (this.exhausted) rest = rest > 0 ? Math.Min(rest, this.stamina.Rest) : this.stamina.Rest;
		else if (rest > 0) this.exhausted = true;

		// a hand can spend a point while the packet is on its way
		if (rest > 0 || this.stamina.Rest > 0 || this.stamina.Ready != (amount > 0) || Math.Abs(this.Stamina - amount) > 1)
			this.stamina.Set(amount, rest);
	}
}
