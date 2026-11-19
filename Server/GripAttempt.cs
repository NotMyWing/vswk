namespace VintageKnuckle;

internal sealed class GripAttempt(GripChange request, long starts, long deadline, double retryReach) {
	public readonly GripChange Catch = request;
	public readonly long Starts = starts, WaitUntil = deadline;
	public readonly double RetryReach = retryReach;
	public bool? Valid, Eligible;
	public bool Started;
	public Ledge? Contact;
	public int PositionGeneration, PathGeneration;
	public long ObservedPosition, SweptPosition;
	public long NextValidation = starts;

	public void CheckPending(Climber climber, long now, bool canClimb) {
		if (!canClimb || this.Eligible == false || now > this.WaitUntil + Climber.Hand.ValidationWindowMs ||
			this.PositionGeneration != climber.Positions.Generation || this.Catch.Dimension != climber.Entity.Pos.Dimension) {
			this.Valid = false;
			return;
		}

		if (this.PathGeneration != climber.Positions.PathGeneration) {
			this.PathGeneration = climber.Positions.PathGeneration;
			this.Contact = null;
		}
	}

	public bool Expired(long now, long latestPosition) {
		if (now < this.WaitUntil) return false;
		// one more tick to read positions that made the deadline
		return (this.Contact == null && this.ObservedPosition >= latestPosition) || now >= this.WaitUntil + Climber.Hand.ValidationWindowMs;
	}

	public bool Due(long now, long latestPosition, bool canValidate, bool released) {
		if (this.Valid.HasValue || this.Eligible == false || now < this.Starts) return false;
		if (released && this.Contact != null) return this.Eligible == true;

		return canValidate && (now >= this.NextValidation || this.ObservedPosition < latestPosition);
	}
}
