using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VintageKnuckle;

internal sealed class BeamPath {
	private static readonly ConditionalWeakTable<PlacedBeam, BeamPath> Paths = new();
	private static readonly ConditionalWeakTable<ICoreAPI, ConcurrentDictionary<AssetLocation, Vector4>> Bounds = new();
	private static readonly MeshData[] Whole = [BeamPath.CenterLine(1)];
	private static readonly MeshData[] Quarters = [BeamPath.CenterLine(0.25f), BeamPath.CenterLine(0.5f), BeamPath.CenterLine(0.75f), BeamPath.Whole[0]];

	private (Vector3 Start, Vector3 End, float Slump, bool Partial)? shape;
	private GripSegment[] sections = [];

	public static GripSegment[] For(PlacedBeam beam) {
		var path = BeamPath.Paths.GetOrCreateValue(beam);
		var start = new Vector3(beam.Start.X, beam.Start.Y, beam.Start.Z);
		var end = new Vector3(beam.End.X, beam.End.Y, beam.End.Z);
		bool partial = beam.Block is BlockSupportBeam { PartialEnds: true };
		var shape = (start, end, beam.SlumpPerMeter, partial);
		if (path.shape == shape) return path.sections;

		var delta = end - start;
		if (beam.SlumpPerMeter == 0 && delta.LengthSquared() >= 1.125f * 1.125f) {
			double horizontal = Math.Sqrt((double)delta.X * delta.X + (double)delta.Z * delta.Z);
			double length = Math.Sqrt(horizontal * horizontal + (double)delta.Y * delta.Y);
			float yaw = (float)Math.Atan2(-delta.X, -delta.Z) + GameMath.PIHALF;
			float pitch = (float)Math.Atan2(horizontal, -delta.Y) + GameMath.PIHALF;
			float extend = 0.25f * Math.Max(Math.Abs(MathF.Sin(yaw) * MathF.Cos(yaw)), Math.Abs(MathF.Sin(pitch) * MathF.Cos(pitch)));
			double renderedEnd = length + extend;
			double lastSection = Math.Floor(renderedEnd + extend) - extend;
			if (renderedEnd - lastSection < 0.01) renderedEnd = lastSection;
			var direction = delta / (float)length;
			path.sections = [new GripSegment(start - direction * extend, start + direction * (float)renderedEnd)];
		} else {
			// let the game bend the center line the same way it bends the beam
			var mesh = ModSystemSupportBeamPlacer.generateMesh(beam.Start, beam.End, BlockFacing.ALLFACES[beam.FacingIndex],
				partial ? BeamPath.Quarters : BeamPath.Whole, beam.SlumpPerMeter);
			path.sections = new GripSegment[mesh.VerticesCount / 2];
			for (int i = 0; i < path.sections.Length; i++) {
				var a = new Vector3(mesh.xyz.AsSpan(i * 6, 3));
				var b = new Vector3(mesh.xyz.AsSpan(i * 6 + 3, 3));
				path.sections[i] = new GripSegment(a, b);
			}
		}
		path.shape = shape;
		return path.sections;
	}

	private static MeshData CenterLine(float length) {
		var mesh = new MeshData(2, 0);
		mesh.AddVertex(1, 0.125f, 0.5f, 0, 0);
		mesh.AddVertex(1 - length, 0.125f, 0.5f, 0, 0);
		return mesh;
	}

	internal static Vector4 CrossSection(ICoreAPI api, Block? block) {
		if (block?.Shape?.Base is not { } location) return new Vector4(-0.125f, -0.125f, 0.125f, 0.125f);
		var cache = BeamPath.Bounds.GetOrCreateValue(api);
		return cache.GetOrAdd(location, static (path, api) => {
			var shape = SailShape.Read(api, path);
			if (shape?.Elements == null || new SailShape(shape, Vector3.Zero).Bounds() is not { } bounds)
				return new Vector4(-0.125f, -0.125f, 0.125f, 0.125f);

			var (min, max) = bounds;
			return new Vector4(min.Y - 0.125f, min.Z - 0.5f, max.Y - 0.125f, max.Z - 0.5f);
		}, api);
	}
}
