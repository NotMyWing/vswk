using System.Numerics;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace VintageKnuckle;

public sealed partial class VintageKnuckleSystem {
	private bool nativeStep, checkedAim, stepStarted;
	private bool landedBeforeGrip;
	private float? unwrappedCameraYaw;

	private readonly Vec3d stepStart = new();
	private readonly Vec3d stepMotion = new();
	private Vec3f stepView = new();
	private int stepDimension, stepPositionVersion;
	private double stepDistance;
	private bool sweepLeft, sweepRight;
	private Vector3? swingExit;
	private Vector3 swingStep;

	private void SweepCamera(EntityPlayer entity, Vector2 previousAim, Vector2 currentAim) {
		var (left, right) = this.Reaching(entity);
		if (!left && !right) return;

		var feet = LedgeFinder.Feet(entity.Pos);
		using var search = GripSearch.Open(entity, cacheShapes: true);
		var lip = GripSweep.Turn(entity, previousAim, currentAim, feet);
		this.ReachForGrips(left, right, lip);
		this.ShowReach(entity, lip != null && this.HasFreeHand(entity) && LedgeFinder.FindAt(entity, this.View(), feet) != null);
		this.checkedAim = true;
		this.SyncGrips();
	}

	private void Tick(EntityPlayer player, EntityPos pos, EntityControls controls, float dt) {
		bool launching = this.jumpedFromGrip;
		this.jumpPressed = this.jumpedFromGrip = false;
		this.jumpDown = controls.Jump;
		this.sweepLeft = this.sweepRight = false;
		this.swingExit = null;
		this.stepStarted = false;
		if (!this.stance && !this.Holding) return;
		if (!player.Alive || player.MountedOn != null || controls.IsFlying || controls.NoClip) return;

		this.stepStarted = true;
		this.stepStart.Set(pos.X, pos.Y, pos.Z);
		this.stepDimension = pos.Dimension;
		this.stepPositionVersion = player.WatchedAttributes.GetInt("positionVersionNumber");
		this.stepView = this.View();
		bool hasInput = !player.Swimming && this.api.Input.MouseGrabbed;

		if (this.stance && hasInput && !launching) {
			var (left, right) = this.Reaching(player);
			if (left || right) {
				this.ReachForGrips(left, right, LedgeFinder.FindAt(player, this.stepView, this.stepStart, dt));
				(this.sweepLeft, this.sweepRight) = this.Reaching(player);
			}
		}

		this.ApplySwing(player, pos, controls, hasInput && controls.TriesToMove, dt);
		this.stepMotion.Set(pos.Motion);
		this.stepDistance = pos.Motion.Length() * dt * ClimbMotion.PhysicsTicksPerSecond;
	}

	private void ApplySwing(EntityPlayer player, EntityPos pos, EntityControls controls, bool moving, float dt) {
		var hand = this.left;
		var otherHand = this.right;
		var hold = WindmillGrip.Resolve(player.World, hand.Hold, out var carry);
		var otherHold = WindmillGrip.Resolve(player.World, otherHand.Hold, out var otherCarry);
		if (hold == null && hand.Hold != null) hand.LoseSupport();
		if (otherHold == null && otherHand.Hold != null) otherHand.LoseSupport();

		if (otherHold?.Dimension == pos.Dimension && (hold?.Dimension != pos.Dimension || otherHold.Top > hold.Top)) {
			(hand, otherHand) = (otherHand, hand);
			(hold, otherHold) = (otherHold, hold);
			(carry, otherCarry) = (otherCarry, carry);
		}
		if (hold == null || hold.Dimension != pos.Dimension) return;

		var velocity = new Vector3((float)pos.Motion.X, (float)pos.Motion.Y, (float)pos.Motion.Z) * ClimbMotion.PhysicsTicksPerSecond;
		var scale = LedgeFinder.ModelScale(player);
		var arm = VintageKnuckleSystem.FromLip(this.stepStart, hold, scale);
		velocity -= carry;

		if (moving) {
			var look = new Vector3(this.stepView.X, this.stepView.Y, this.stepView.Z);
			var forward = Convert.ToInt32(controls.Forward) - Convert.ToInt32(controls.Backward);
			var strafe = Convert.ToInt32(controls.Right) - Convert.ToInt32(controls.Left);
			var rightward = ClimbMotion.CameraRight(this.api.Input.MouseYaw);
			velocity = ClimbMotion.Steer(velocity, look, rightward, forward, strafe, player.OnGround, dt);
			if (forward > 0) velocity.Y = ClimbMotion.PullUp(velocity.Y, -arm.Y, look.Y, dt);
		}

		float hangRadius = hand.SwingRadius(arm.Length(), scale, dt);
		if (otherHold?.Dimension == pos.Dimension && otherHold != hold) {
			var otherArm = VintageKnuckleSystem.FromLip(this.stepStart, otherHold, scale);
			float otherRadius = otherHand.SwingRadius(otherArm.Length(), scale, dt);
			otherArm += (carry - otherCarry) * dt;
			velocity = ClimbMotion.Swing(velocity, arm, hangRadius, dt, otherArm, otherRadius);
		} else {
			var incoming = velocity;
			velocity = ClimbMotion.Swing(velocity, arm, hangRadius, dt);
			if (velocity != incoming) {
				this.swingStep = (velocity + carry) * dt;
				this.swingExit = carry + ClimbMotion.SwingExit(incoming, arm, velocity * dt, hangRadius, dt);
			}
		}

		velocity += carry;
		var ticksPerSecond = ClimbMotion.PhysicsTicksPerSecond;
		pos.Motion.Set(velocity.X / ticksPerSecond, velocity.Y / ticksPerSecond, velocity.Z / ticksPerSecond);
	}

	private static Vector3 FromLip(Vec3d pos, Ledge hold, float scale) =>
		new((float)(pos.X - hold.HitX), (float)(pos.Y - hold.Top) + 0.8f * scale, (float)(pos.Z - hold.HitZ));

	private void AfterPlayerStep() {
		var player = this.physicsPlayer;
		if (player == null || player != this.api.World.Player?.Entity) return;

		if (this.nativeStep) {
			this.nativeStep = false;
			return;
		}
		if (!this.stance && !this.Holding) return;

		this.FinishMovement(player, player.Pos);
		this.Tick(player, player.Pos, player.Controls, 1f / ClimbMotion.PhysicsTicksPerSecond);
		this.SyncGrips();
	}

	private void FinishMovement(EntityPlayer player, EntityPos pos) {
		if (!this.stepStarted) return;
		this.stepStarted = false;
		if (this.swingExit is Vector3 exit) {
			this.swingExit = null;
			if (!player.Collided && this.Holding) this.FinishSwing(pos, exit);
		}

		var (left, right) = this.Reaching(player);
		left &= this.sweepLeft;
		right &= this.sweepRight;
		this.sweepLeft = this.sweepRight = false;
		if (!this.stance || (!left && !right)) return;

		var end = LedgeFinder.Feet(pos);
		if (!this.CanReach(player)) {
			this.ShowReach(player, false);
			return;
		}

		double allowance = this.stepDistance + player.Properties.EyeHeight;
		double traveled = end.SquareDistanceTo(this.stepStart);
		bool continuous = this.stepDimension == pos.Dimension && this.stepPositionVersion == player.WatchedAttributes.GetInt("positionVersionNumber") &&
			traveled <= allowance * allowance;

		using var search = GripSearch.Open(player, cacheShapes: true);
		var lip = continuous ? GripSweep.Step(player, this.stepView, this.stepStart, end, includeStart: false)
			: LedgeFinder.FindAt(player, this.stepView, end);
		if (lip?.CaughtFrom is { } caughtFrom && WindmillGrip.Resolve(player.World, lip, out var carry) is { } anchor &&
			((player.OnGround && caughtFrom.Y > end.Y && this.stepMotion.Y < 0) || !LedgeFinder.InReach(player, anchor.Position, 0.6))) {
			if (this.StopAtGrip(player, pos, end, caughtFrom, anchor, carry)) end.Set(caughtFrom);
			else lip = LedgeFinder.FindAt(player, this.stepView, end);
		}
		this.ReachForGrips(left, right, lip);
		bool inReach = lip != null;
		if (lip?.CaughtFrom is { } from && end.SquareDistanceTo(from) > 0)
			inReach = this.HasFreeHand(player) && LedgeFinder.FindAt(player, this.stepView, end) != null;
		this.ShowReach(player, inReach);
		this.checkedAim = true;
	}

	private bool StopAtGrip(EntityPlayer player, EntityPos pos, Vec3d end, Vec3d from, Ledge anchor, Vector3 carry) {
		if (LedgeFinder.BodyBlocked(player, end, from)) return false;

		pos.SetPos(from);
		player.OnGround = player.CollidedVertically = player.CollidedHorizontally = false;
		var outward = VintageKnuckleSystem.FromLip(from, anchor, LedgeFinder.ModelScale(player));
		var velocity = new Vector3((float)this.stepMotion.X, (float)this.stepMotion.Y, (float)this.stepMotion.Z);
		var relative = velocity - carry / ClimbMotion.PhysicsTicksPerSecond;
		float lengthSq = outward.LengthSquared();
		if (lengthSq > 0) velocity -= outward * (Math.Max(0, Vector3.Dot(relative, outward)) / lengthSq);
		pos.Motion.Set(velocity.X, velocity.Y, velocity.Z);
		return true;
	}

	private void FinishSwing(EntityPos pos, Vector3 exit) {
		var traveled = new Vector3((float)(pos.X - this.stepStart.X),
			(float)(pos.Y - this.stepStart.Y), (float)(pos.Z - this.stepStart.Z));

		const float stepTolerance = 0.00001f;
		if (Vector3.DistanceSquared(traveled, this.swingStep) < stepTolerance * stepTolerance) {
			var ticksPerSecond = ClimbMotion.PhysicsTicksPerSecond;
			pos.Motion.Set(exit.X / ticksPerSecond, exit.Y / ticksPerSecond, exit.Z / ticksPerSecond);
		}
	}
}
