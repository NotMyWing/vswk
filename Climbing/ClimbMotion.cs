using System.Numerics;

namespace VintageKnuckle;

internal static class ClimbMotion {
	public const float PhysicsTicksPerSecond = 60f;

	public static bool RecoilBreaksGrip(Vector3 kick, Vector3 fromGrip) {
		const float breakSpeed = 3f;
		return Vector3.Dot(kick, fromGrip) > breakSpeed * fromGrip.Length();
	}

	public static Vector3 CameraRight(float yaw) => new(-MathF.Cos(yaw), 0, MathF.Sin(yaw));

	private static bool CanPullUp(float heightBelowGrip, float lookY) {
		const float minimumLookY = 0.5f; // 30 degrees above the horizon
		return heightBelowGrip > 0 && lookY >= minimumLookY;
	}

	public static float PullUp(float verticalSpeed, float heightBelowGrip, float lookY, float dt) {
		if (dt <= 0 || !ClimbMotion.CanPullUp(heightBelowGrip, lookY)) return verticalSpeed;
		const float pullSpeed = 1.92f;
		const float slowdownDistance = 0.4f;
		float target = Math.Min(pullSpeed, heightBelowGrip / Math.Max(slowdownDistance / pullSpeed, dt));
		return verticalSpeed + Math.Clamp(target - verticalSpeed, 0, 90f * dt);
	}

	public static Vector3 Steer(Vector3 velocity, Vector3 look, Vector3 right, int forward, int strafe, bool grounded, float dt) {
		if (grounded) return velocity;

		var wish = forward * look + strafe * right;
		wish.Y = 0;
		float strength = wish.Length();
		if (strength < 0.0001f) return velocity;

		wish /= strength;
		const float acceleration = 6f, steeringSpeed = 2f;
		var push = Math.Clamp(steeringSpeed - Vector3.Dot(velocity, wish), 0, acceleration * Math.Min(1, strength) * dt);
		return velocity + wish * push;
	}

	public static Vector3 Swing(Vector3 velocity, Vector3 fromLip, float reach, float dt,
		Vector3? fromOtherLip = null, float otherReach = 0) {
		if (dt <= 0) return velocity;

		var step = velocity * dt;
		var first = ClimbMotion.Limit(step, fromLip, reach);
		if (fromOtherLip is not Vector3 other || (first + other).LengthSquared() <= otherReach * otherReach) {
			return first == step ? velocity : first / dt;
		}

		var second = ClimbMotion.Limit(step, other, otherReach);
		if ((second + fromLip).LengthSquared() <= reach * reach) return second / dt;

		var between = fromLip - other;
		var distance = between.Length();
		if (distance <= Math.Abs(reach - otherReach)) return (reach <= otherReach ? first : second) / dt;

		// the higher hand takes the weight if both arms are too short
		if (distance > reach + otherReach) return first / dt;

		var axis = between / distance;
		float along = (reach * reach - otherReach * otherReach + distance * distance) / (2 * distance);
		var center = axis * along - fromLip;

		var across = step - center;
		across -= axis * Vector3.Dot(across, axis);
		float radius = MathF.Sqrt(Math.Max(0, reach * reach - along * along));
		return (across.LengthSquared() > 0 ? center + Vector3.Normalize(across) * radius : center) / dt;
	}

	public static Vector3 SwingExit(Vector3 velocity, Vector3 fromLip, Vector3 step, float reach, float dt) {
		var end = fromLip + step;
		var normal = Vector3.Normalize(end);
		float distance = fromLip.Length();
		var pull = normal * (Math.Min(0, reach - distance) / dt);

		var tangent = velocity - normal * Vector3.Dot(velocity, normal);
		const float minTangentSpeed = 0.00001f;
		if (tangent.LengthSquared() < minTangentSpeed * minTangentSpeed) return pull;

		float speed = Vector3.Cross(fromLip, velocity).Length() / Math.Max(reach, distance);
		return pull + Vector3.Normalize(tangent) * speed;
	}

	private static Vector3 Limit(Vector3 step, Vector3 fromLip, float reach) {
		var next = fromLip + step;
		return next.LengthSquared() <= reach * reach ? step : Vector3.Normalize(next) * reach - fromLip;
	}
}
