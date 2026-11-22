using ProtoBuf;

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
