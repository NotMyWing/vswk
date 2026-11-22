using System.Numerics;
using Vintagestory.API.MathTools;

namespace VintageKnuckle;

internal readonly record struct GripRay(Vec3d Eye, Vector3 Look, float EyeHeight, float Range) {
	public float Length => this.Range + (this.Look.Y < 0 ? this.EyeHeight : 0);
	public float Score(Vector3 fromEye) => GripAim.PointScore(fromEye, this.Look, this.EyeHeight, this.Range);
}
