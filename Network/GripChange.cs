using System.Numerics;
using ProtoBuf;
using Vintagestory.API.MathTools;

namespace VintageKnuckle;

[ProtoContract]
internal sealed class GripChange {
	public const string Channel = "vintageknuckle-grips-v6";
	[ProtoMember(1)] public int Revision;
	[ProtoMember(2)] public bool Stance;
	[ProtoMember(3)] public int Dimension;
	[ProtoMember(4)] public bool ChangeLeft;
	[ProtoMember(5)] public bool ChangeRight;
	[ProtoMember(6)] public GripPoint? Left;
	[ProtoMember(7)] public GripPoint? Right;
	[ProtoMember(10)] public bool Jump;
}

[ProtoContract]
internal sealed class GripState {
	[ProtoMember(1)] public int ReleaseLeft;
	[ProtoMember(2)] public int ReleaseRight;
	[ProtoMember(3)] public int Revision;
	[ProtoMember(4)] public float LeftStamina;
	[ProtoMember(5)] public float RightStamina;
	[ProtoMember(6)] public float LeftRest;
	[ProtoMember(7)] public float RightRest;
	[ProtoMember(8)] public bool LeftPending;
	[ProtoMember(9)] public bool RightPending;
}

[ProtoContract]
internal sealed class GripPoint {
	public const string LeftKey = "vintageknuckle-left", RightKey = "vintageknuckle-right";
	[ProtoMember(1)] public int X;
	[ProtoMember(2)] public int Y;
	[ProtoMember(3)] public int Z;
	[ProtoMember(4)] public float AcrossX;
	[ProtoMember(5)] public float Height;
	[ProtoMember(6)] public float AcrossZ;
	[ProtoMember(7)] public int Face;
	[ProtoMember(8)] public int BlockId;
	[ProtoMember(9)] public long SpearId;
	[ProtoMember(10)] public float EdgeStart;
	[ProtoMember(11)] public float EdgeEnd;
	[ProtoMember(12)] public int Dimension;
	[ProtoMember(13)] public bool OnSail;
	[ProtoMember(14)] public bool OnBeam;
	[ProtoMember(15)] public Vec3f? SweptFrom;

	public Vec3d Position => new(this.X + (double)this.AcrossX, this.Y + (double)this.Height, this.Z + (double)this.AcrossZ);

	public static GripPoint ForCatch(Ledge lip) {
		var point = GripPoint.FromLedge(lip);
		if (!point.OnSail && lip.CaughtFrom is { } feet)
			point.SweptFrom = new Vec3f((float)(feet.X - lip.X), (float)(feet.Y - lip.Y), (float)(feet.Z - lip.Z));
		return point;
	}

	public static GripPoint ForObservers(Ledge lip) {
		var point = GripPoint.FromLedge(lip);
		point.Dimension = lip.Dimension;
		if (point.OnSail || point.OnBeam) return point;

		int along = lip.FaceX == 0 ? lip.X : lip.Z;
		point.EdgeStart = (float)(lip.EdgeMin - along);
		point.EdgeEnd = (float)(lip.EdgeMax - along);
		return point;
	}

	private static GripPoint FromLedge(Ledge lip) {
		var point = new GripPoint { X = lip.X, Y = lip.Y, Z = lip.Z, BlockId = lip.BlockId };
		if (lip.SailPoint is Vector3 sail) {
			point.OnSail = true;
			point.AcrossX = sail.X;
			point.Height = sail.Y;
			point.AcrossZ = sail.Z;
			return point;
		}

		point.SpearId = lip.SpearId ?? 0;
		point.OnBeam = lip.OnBeam;
		point.AcrossX = (float)(lip.HitX - lip.X);
		point.Height = (float)(lip.Top - lip.Y);
		point.AcrossZ = (float)(lip.HitZ - lip.Z);
		if (!point.OnBeam) point.Face = BlockFacing.FromNormal(new Vec3i(lip.FaceX, 0, lip.FaceZ)).Index;
		return point;
	}

	public Ledge ToLedge(int dimension) {
		if (this.OnSail) {
			return new Ledge(this.X, this.Y, this.Z, dimension, this.BlockId, 0, 1, this.X, this.Z) {
				SailPoint = new Vector3(this.AcrossX, this.Height, this.AcrossZ)
			};
		}

		var point = this.Position;
		if (this.OnBeam) return Ledge.OnSupportBeam(new BlockPos(this.X, this.Y, this.Z, dimension), this.BlockId, point);
		if (this.SpearId != 0) return Ledge.OnSpear(this.SpearId, dimension, point.X, point.Y, point.Z);
		var n = BlockFacing.ALLFACES[this.Face].Normali;
		var along = n.X == 0 ? this.X : this.Z;
		return new Ledge(this.X, this.Y, this.Z, dimension, this.BlockId, n.X, n.Z, point.X, point.Z) {
			Top = point.Y, EdgeMin = along + (double)this.EdgeStart, EdgeMax = along + (double)this.EdgeEnd
		};
	}
}
