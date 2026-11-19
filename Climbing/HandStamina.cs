namespace VintageKnuckle;

internal sealed class HandStamina {
	public const int JumpDelayMs = 300;
	public const float JumpCost = 12;
	public static float Recovery(float dt, bool grounded) => dt * (grounded ? 15 : 3);
	public static float HangCost(float dt, int hands, bool moving) => dt * (moving ? 12 : 8) / hands;

	public float Amount { get; private set; } = 100;
	public float Rest { get; private set; }
	public bool Ready => this.Rest <= 0 && this.Amount > 0;

	public void Set(float amount = 100, float rest = 0) {
		this.Amount = amount;
		this.Rest = rest;
	}

	public bool Tick(float dt, bool held, int hands, bool grounded, bool moving) {
		this.Rest = Math.Max(0, this.Rest - dt);
		if (!held) {
			this.Amount = Math.Min(100, this.Amount + HandStamina.Recovery(dt, grounded));
			return false;
		}

		return !grounded && this.Spend(HandStamina.HangCost(dt, hands, moving));
	}

	public bool Jump() => this.Spend(HandStamina.JumpCost);

	private bool Spend(float cost) {
		this.Amount = Math.Max(0, this.Amount - cost);
		if (this.Amount > 0) return false;
		this.Rest = 1;
		return true;
	}
}
