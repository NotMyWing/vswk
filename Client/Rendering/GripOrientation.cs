using System.Numerics;

namespace VintageKnuckle;

internal struct GripOrientation {
	private Vector3 direction;
	private double lastFrame;

	public Vector3 Follow(double frame, Vector3 axis, Vector3 preferred, Vector3 fallback) {
		var wanted = preferred - axis * Vector3.Dot(preferred, axis);
		var previous = this.direction - axis * Vector3.Dot(this.direction, axis);
		float strength = wanted.Length();

		if (previous.LengthSquared() < 0.0001f) {
			previous = strength > 0.1f ? wanted : fallback - axis * Vector3.Dot(fallback, axis);
			if (previous.LengthSquared() < 0.0001f) {
				var up = Math.Abs(axis.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
				previous = Vector3.Cross(axis, up);
			}
			this.lastFrame = frame;
		}
		previous = Vector3.Normalize(previous);

		if (strength > 0.001f) {
			float angle = MathF.Atan2(Vector3.Dot(axis, Vector3.Cross(previous, wanted)), Vector3.Dot(previous, wanted));
			float seconds = (float)Math.Clamp(frame - this.lastFrame, 0, 0.1);
			float step = seconds * 8 * Math.Min(1, strength);
			var turn = Quaternion.CreateFromAxisAngle(axis, Math.Clamp(angle, -step, step));
			previous = Vector3.Transform(previous, turn);
		}

		this.lastFrame = frame;
		this.direction = previous;
		return previous;
	}
}
