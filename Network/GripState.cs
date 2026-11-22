using ProtoBuf;

namespace VintageKnuckle;

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
