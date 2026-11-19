namespace VintageKnuckle;

internal sealed class GripStamina {
	private const int MaxPendingSteps = 128;
	private const double RoundingMargin = 0.01;

	internal sealed class Balance {
		public readonly HandStamina Stamina = new();
		private GripAttempt? last;
		private bool spent;

		public bool Tracking(GripAttempt grip) => this.last == grip;
		public bool Slipped(GripAttempt? grip) => grip != null && grip == this.last && this.spent;

		public bool Holding(GripAttempt? grip) {
			if (grip == null || grip.Valid != true) return false;
			if (grip != this.last) {
				this.last = grip;
				this.spent = !this.Stamina.Ready;
			}
			return !this.spent;
		}

		public void Tick(float dt, bool held, int hands, bool grounded, bool moving) {
			if (this.Stamina.Tick(dt, held, hands, grounded, moving)) this.spent = true;
		}

		public Balance Copy() {
			var copy = new Balance { last = this.last, spent = this.spent };
			copy.Stamina.Set(this.Stamina.Amount, this.Stamina.Rest);
			return copy;
		}
	}

	private enum Action { Tick, Jump, Start }

	private readonly record struct Step(GripAttempt? Left, GripAttempt? Right, long Milliseconds = 0, bool Grounded = false, bool Moving = false, Action Kind = Action.Tick) {
		public float Seconds => this.Milliseconds / 1000f;
		public bool Waiting => this.Kind != Action.Start && (this.Left is { Valid: null, Eligible: not false } || this.Right is { Valid: null, Eligible: not false });
		public float MaximumCost => this.Kind switch {
			Action.Jump => HandStamina.JumpCost,
			Action.Tick when !this.Grounded => HandStamina.HangCost(this.Seconds, 1, this.Moving),
			_ => 0
		};

		public void Apply(Balance left, Balance right) {
			if (this.Kind == Action.Start) {
				if (this.Left != null) this.Left.Eligible = left.Stamina.Ready;
				if (this.Right != null) this.Right.Eligible = right.Stamina.Ready;
				return;
			}
			bool l = left.Holding(this.Left), r = right.Holding(this.Right);
			if (this.Kind == Action.Jump) {
				if (l) left.Stamina.Jump();
				if (r) right.Stamina.Jump();
				return;
			}

			int hands = (l ? 1 : 0) + (r ? 1 : 0);
			left.Tick(this.Seconds, l, hands, this.Grounded, this.Moving);
			right.Tick(this.Seconds, r, hands, this.Grounded, this.Moving);
		}
	}

	public readonly Balance Left = new(), Right = new();
	private readonly List<Step> steps = [];
	private double leftCost, rightCost;
	public bool Pending => this.steps.Count != 0;
	public bool CorrectionRequired { get; set; }

	public void Advance(long from, long until, GripAttempt? left, GripAttempt? right, bool grounded, bool moving) {
		while (from < until) {
			long end = until;
			if (left != null && left.Starts > from) end = Math.Min(end, left.Starts);
			if (right != null && right.Starts > from) end = Math.Min(end, right.Starts);
			if (left?.Starts <= from) this.Begin(left, true);
			if (right?.Starts <= from) this.Begin(right, false);
			this.Record(new Step(left?.Starts <= from ? left : null, right?.Starts <= from ? right : null, end - from, grounded, moving));
			from = end;
		}
	}

	public void Begin(GripAttempt grip, bool left) {
		if (grip.Started) return;
		grip.Started = true;
		var balance = left ? this.Left : this.Right;
		if (!this.Pending) grip.Eligible = balance.Stamina.Ready;
		else if (this.CanCover(left)) grip.Eligible = true;
		else this.Record(new Step(left ? grip : null, left ? null : grip, Kind: Action.Start));
	}

	public bool CanCover(bool left) {
		var stamina = (left ? this.Left : this.Right).Stamina;
		if (!stamina.Ready) return false;
		if (stamina.Amount - (left ? this.leftCost : this.rightCost) > GripStamina.RoundingMargin) return true;
		float amount = stamina.Amount;
		foreach (var step in this.steps) {
			if ((left ? step.Left : step.Right) is not { Valid: not false, Eligible: not false }) continue;
			amount -= step.MaximumCost;
			if (amount <= 0) return false;
		}
		return true;
	}

	public bool Exhausted(GripAttempt grip, bool left) {
		var balance = left ? this.Left : this.Right;
		if (balance.Slipped(grip)) return true;
		if (!this.Pending) return false;
		bool holding = balance.Tracking(grip);
		float amount = balance.Stamina.Amount;
		foreach (var step in this.steps) {
			if (step.Kind != Action.Tick) continue;
			if ((left ? step.Left : step.Right) != grip) {
				if (!holding) amount = Math.Min(100, amount + HandStamina.Recovery(step.Seconds, step.Grounded));
				continue;
			}
			holding = true;
			if (step.Grounded) continue;
			int hands = (left ? step.Right : step.Left) is { Valid: not false, Eligible: not false } ? 2 : 1;
			amount -= HandStamina.HangCost(step.Seconds, hands, step.Moving);
			if (amount <= 0) return true;
		}
		return false;
	}

	public void Jump(GripAttempt grip, bool left) {
		this.Begin(grip, left);
		this.Record(new Step(left ? grip : null, left ? null : grip, Kind: Action.Jump));
	}

	private void Record(Step step) {
		if (this.Pending && this.TryMerge(step)) return;
		if (this.steps.Count == GripStamina.MaxPendingSteps) this.Settle();
		if (this.steps.Count == GripStamina.MaxPendingSteps) {
			foreach (var pending in this.steps) {
				if (pending.Left is { Valid: null } left) left.Valid = false;
				if (pending.Right is { Valid: null } right) right.Valid = false;
			}
			this.Settle();
			this.CorrectionRequired = true;
		}
		if (this.Pending || step.Waiting) {
			this.steps.Add(step);
			this.AccountCost(step, step.MaximumCost);
		} else step.Apply(this.Left, this.Right);
	}

	private bool TryMerge(Step step) {
		var previous = this.steps[^1];
		if (step.Kind != Action.Tick || previous != (step with { Milliseconds = previous.Milliseconds })) return false;
		var merged = previous with { Milliseconds = previous.Milliseconds + step.Milliseconds };
		double increase = (double)merged.MaximumCost - previous.MaximumCost;
		if (!step.Grounded) {
			// a slip changes both hands' rates on the next tick
			double cost = Math.Max(step.MaximumCost, increase) + GripStamina.RoundingMargin;
			if (step.Left is { Valid: not false, Eligible: not false } && this.Left.Stamina.Amount - this.leftCost <= cost ||
				step.Right is { Valid: not false, Eligible: not false } && this.Right.Stamina.Amount - this.rightCost <= cost) return false;
		}
		this.steps[^1] = merged;
		this.AccountCost(merged, increase);
		return true;
	}

	private void AccountCost(Step step, double cost) {
		if (step.Left != null) this.leftCost += cost;
		if (step.Right != null) this.rightCost += cost;
	}

	public void Settle() {
		int settled = 0;
		while (settled < this.steps.Count && !this.steps[settled].Waiting) {
			var step = this.steps[settled++];
			this.AccountCost(step, -step.MaximumCost);
			step.Apply(this.Left, this.Right);
		}
		if (settled != 0) this.steps.RemoveRange(0, settled);
		if (!this.Pending) this.leftCost = this.rightCost = 0;
	}

	public (HandStamina Left, HandStamina Right) Confirmed() {
		if (!this.Pending) return (this.Left.Stamina, this.Right.Stamina);
		var left = this.Left.Copy();
		var right = this.Right.Copy();
		foreach (var step in this.steps) {
			if (step.Kind != Action.Start) step.Apply(left, right);
		}
		return (left.Stamina, right.Stamina);
	}
}
