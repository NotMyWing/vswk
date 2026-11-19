using System.Numerics;
using System.Runtime.InteropServices;
using Vintagestory.API.Common;

namespace VintageKnuckle;

internal sealed class HangingArm {
	private readonly ElementPose? upper, lower, palm, otherShoulder;
	private readonly Vector3 outward;
	private GripOrientation bend, shadowBend;

	public ElementPose? HandPose => this.palm ?? this.lower;
	public ShapeElement? Hand => this.HandPose?.ForElement;
	public float Return;
	public readonly float Width, Depth, Length;

	public HangingArm(EntityPlayer player, IAnimator animator, bool left) {
		var side = left ? "L" : "R";
		ElementPose? Bone(string part, string name) => animator.GetPosebyName(player.GetBoneName(part + side + "BoneName", name + side));
		this.lower = Bone("lowerArm", "LowerArm");
		var upper = Bone("upperArm", "UpperArm");
		var palm = Bone("hand", "Hand");
		if (HangingArm.BelongsTo(this.lower, upper)) this.upper = upper;
		if (this.lower == null || HangingArm.BelongsTo(palm, this.lower)) this.palm = palm;
		var other = left ? "R" : "L";
		this.otherShoulder = animator.GetPosebyName(player.GetBoneName("upperArm" + other + "BoneName", "UpperArm" + other));
		this.outward = left ? Vector3.UnitZ : -Vector3.UnitZ;

		if (this.Hand is not { } hand) return;
		var size = HangingArm.Pixels(hand.To) - HangingArm.Pixels(hand.From);
		this.Width = size.X;
		this.Depth = size.Z;
		this.Length = this.palm != null ? size.Y : Math.Min(size.Y, Math.Max(size.X, size.Z) * 1.25f);
	}

	private static bool BelongsTo(ElementPose? child, ElementPose? parent) {
		if (parent == null) return false;
		for (var bone = child?.ForElement.ParentElement; bone != null; bone = bone.ParentElement) {
			if (bone == parent.ForElement) return true;
		}
		return false;
	}

	private static Vector3 Pixels(double[]? point) => point is { Length: 3 } ?
		new Vector3((float)point[0], (float)point[1], (float)point[2]) / 16 : Vector3.Zero;

	private static Vector3 Point(ElementPose pose, Vector3 local) =>
		Vector3.Transform(local, MemoryMarshal.Cast<float, Matrix4x4>(pose.AnimModelMatrix)[0]);

	private static Vector3 Pivot(ElementPose pose) {
		var element = pose.ForElement;
		return HangingArm.Point(pose, HangingArm.Pixels(element.RotationOrigin) - HangingArm.Pixels(element.From));
	}

	public void PoseOnModel(float[] original, float[] posed, Vector3 pad, Matrix4x4 placement, double frame, bool shadow, bool remote) {
		if (this.HandPose is not { } hand) return;
		// the stock seraph has the hand and forearm on one bone
		bool separatePalm = this.palm != null && hand.ForElement.JointId > 0 && hand.ForElement.JointId != this.lower?.ForElement.JointId;
		var pivot = separatePalm ? HangingArm.Pixels(hand.ForElement.RotationOrigin) - HangingArm.Pixels(hand.ForElement.From) : pad;
		var target = Vector3.Transform(pivot, placement);
		var wrist = HangingArm.Point(hand, pivot);
		var reached = this.Bend(original, posed, wrist, target, frame, shadow, null, remote);
		if (!separatePalm || reached is not { } end) return;

		var handMatrix = MemoryMarshal.Cast<float, Matrix4x4>(hand.AnimModelMatrix)[0];
		if (!Matrix4x4.Invert(handMatrix, out var toHand)) return;
		placement.Translation += end - target;
		HangingArm.Move(hand, null, this.EaseBack(toHand * placement), original, posed);
	}

	public void PoseFirstPerson(float[] original, float[] posed, Vector3 target, double frame, bool plantedPalm, Plane? eyeLevel) {
		if (this.lower is not { } lower) return;
		var size = HangingArm.Pixels(lower.ForElement.To) - HangingArm.Pixels(lower.ForElement.From);
		var wrist = this.palm != null ? HangingArm.Pivot(this.palm) : HangingArm.Point(lower, new Vector3(size.X / 2, 0, size.Z / 2));
		if (this.Bend(original, posed, wrist, target, frame, false, eyeLevel) == null) return;

		if (plantedPalm && this.palm is { } palm && palm.ForElement.JointId != lower.ForElement.JointId) {
			HangingArm.Move(palm, null, Matrix4x4.CreateScale(0), original, posed);
		}
	}

	private Vector3? Bend(float[] original, float[] posed, Vector3 wrist, Vector3 target, double frame, bool shadow, Plane? eyeLevel,
		bool limitStretch = false) {
		if (this.upper is not { } upper || this.lower is not { } lower) return null;
		var shoulderId = upper.ForElement.JointId;
		var elbowId = lower.ForElement.JointId;
		if (shoulderId <= 0 || elbowId <= 0 || shoulderId == elbowId || shoulderId == upper.ForElement.ParentElement?.JointId) return null;

		var shoulder = HangingArm.Pivot(upper);
		var elbow = HangingArm.Pivot(lower);
		var upperLength = Vector3.Distance(shoulder, elbow);
		var lowerLength = Vector3.Distance(elbow, wrist);
		if (upperLength < 0.001f || lowerLength < 0.001f || !float.IsFinite(Vector3.DistanceSquared(shoulder, target))) return null;

		var across = this.otherShoulder == null ? this.outward : shoulder - HangingArm.Pivot(this.otherShoulder);
		var pole = (across.LengthSquared() > 0.0001f ? Vector3.Normalize(across) : this.outward) - Vector3.UnitY;
		if (pole.LengthSquared() < 0.0001f) pole = this.outward;

		var root = shoulder;
		if (eyeLevel is { } eyes) {
			float belowEyes = upperLength * 0.85f;
			float drop = Math.Max(0, Plane.DotCoordinate(eyes, shoulder) + belowEyes);
			root -= eyes.Normal * drop;
		}

		var reach = target - root;
		float distance = reach.Length();
		var direction = distance > 0.0001f ? reach / distance : Vector3.Normalize(wrist - elbow);
		if (limitStretch) {
			float maxReach = 1.2f * (upperLength + lowerLength);
			if (distance > maxReach) {
				distance = maxReach;
				target = root + direction * distance;
			}
		}

		ref var swivel = ref (shadow ? ref this.shadowBend : ref this.bend);
		var side = swivel.Follow(frame, direction, Vector3.Normalize(pole), elbow - shoulder);
		var hinge = Vector3.Cross(direction, side);
		var bend = HangingArm.Elbow(root, direction, distance, upperLength, lowerLength, side);

		var upperTurn = HangingArm.Aim(upper, shoulder, elbow, root, bend, hinge);
		var lowerTurn = HangingArm.Aim(lower, elbow, wrist, bend, target, hinge);
		HangingArm.Move(upper, lower, this.EaseBack(upperTurn), original, posed);
		HangingArm.Move(lower, null, this.EaseBack(lowerTurn), original, posed);

		return target;
	}

	private Matrix4x4 EaseBack(Matrix4x4 turn) => this.Return == 0 ? turn : Matrix4x4.Lerp(turn, Matrix4x4.Identity, this.Return);

	private static Vector3 Elbow(Vector3 shoulder, Vector3 direction, float distance, float upper, float lower, Vector3 side) {
		if (distance < 0.0001f) return shoulder + side * upper;
		if (distance >= upper + lower) return shoulder + direction * (distance * upper / (upper + lower));

		float along = Math.Clamp((upper * upper - lower * lower + distance * distance) / (2 * distance), -upper, upper);
		float height = MathF.Sqrt(Math.Max(0, upper * upper - along * along));
		return shoulder + direction * along + side * height;
	}

	private static Matrix4x4 Aim(ElementPose bone, Vector3 from, Vector3 to, Vector3 start, Vector3 end, Vector3 hinge) {
		var before = to - from;
		var after = end - start;
		var length = after.Length();
		if (length < 0.0001f) return Matrix4x4.CreateScale(0) * Matrix4x4.CreateTranslation(start);

		var forward = Vector3.Normalize(before);
		var model = MemoryMarshal.Cast<float, Matrix4x4>(bone.AnimModelMatrix)[0];
		var axis = Vector3.TransformNormal(Vector3.UnitZ, model);
		if (Vector3.Cross(forward, axis).LengthSquared() < 0.0001f) axis = Vector3.TransformNormal(Vector3.UnitX, model);

		Matrix4x4.Invert(Matrix4x4.CreateWorld(from, forward, axis), out var fromBone);
		var stretch = Matrix4x4.CreateScale(1, 1, length / before.Length());
		return fromBone * stretch * Matrix4x4.CreateWorld(start, after / length, hinge);
	}

	private static void Move(ElementPose pose, ElementPose? stop, Matrix4x4 correction, float[] original, float[] posed) {
		if (pose == stop) return;
		int id = pose.ForElement.JointId;
		var joints = MemoryMarshal.Cast<float, Matrix4x4>(posed.AsSpan());
		if (id > 0 && id < joints.Length) joints[id] = MemoryMarshal.Cast<float, Matrix4x4>(original)[id] * correction;
		if (pose.ChildElementPoses != null) {
			foreach (var child in pose.ChildElementPoses) HangingArm.Move(child, stop, correction, original, posed);
		}
	}
}
