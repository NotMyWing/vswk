using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VintageKnuckle;

internal static class SpearGrip {
	private static readonly ConditionalWeakTable<ICoreAPI, ConcurrentDictionary<CompositeShape, GripSegment?>> Shafts = new();

	internal static bool IsSpear(Entity entity) => entity is EntityProjectileBase projectile &&
		(projectile.ProjectileStack?.Collectible?.Tool == EnumTool.Spear || projectile.Code?.FirstCodePart() == "spear");

	public static Ledge? Find(EntityPlayer player, GripRay ray, List<CollisionBox> boxes, float bestScore, List<EntityProjectileBase> spears) {
		Ledge? bestGrip = null;
		for (int i = 0; i < spears.Count; i++) {
			var spear = spears[i];
			if (SpearGrip.ShaftAt(spear, ray.Eye) is not { } segment) continue;
			if (segment.Find(player, ray, boxes, ref bestScore, spear) is { } hit)
				bestGrip = Ledge.OnSpear(spear.EntityId, player.Pos.Dimension, hit.X, hit.Y, hit.Z);
		}
		return bestGrip;
	}

	public static Ledge? CheckGrip(EntityPlayer player, Vec3d eye, Vec3d point, long id, out bool waitForPosition) {
		waitForPosition = false;
		var spear = SpearGrip.AtPoint(player, id, point);
		if (spear == null) return null;
		waitForPosition = !LedgeFinder.Visible(player, eye, point, ignore: spear);
		return waitForPosition ? null : Ledge.OnSpear(id, player.Pos.Dimension, point.X, point.Y, point.Z);
	}

	public static EntityProjectileBase? AtPoint(EntityPlayer player, long id, Vec3d point) {
		if (player.World.GetEntityById(id) is not EntityProjectileBase { Alive: true, Stuck: true } spear ||
			spear.Pos.Dimension != player.Pos.Dimension || !SpearGrip.IsSpear(spear)) return null;
		const float positionAllowance = 0.035f;
		return SpearGrip.ShaftAt(spear, point) is { } shaft && shaft.Contains(Vector3.Zero, positionAllowance) ? spear : null;
	}

	public static GripSegment? ShaftAt(Entity spear, Vec3d origin) {
		var client = spear.Properties?.Client;
		if (client?.Shape?.Base == null) return null;
		var shapes = SpearGrip.Shafts.GetOrCreateValue(spear.Api);
		var segment = shapes.GetOrAdd(client.Shape, static (shape, api) => SpearGrip.ReadShaft(shape, api), spear.Api);
		if (segment is not { } local) return null;

		var pos = spear.Pos;
		var shape = client.Shape;
		float degrees = GameMath.DEG2RAD;
		var rotation = Matrix4x4.CreateRotationZ(pos.Roll + shape.rotateZ * degrees) *
			Matrix4x4.CreateRotationY(pos.Yaw + MathF.PI / 2 + shape.rotateY * degrees) *
			Matrix4x4.CreateRotationX(pos.Pitch + shape.rotateX * degrees);
		var pivot = Vector3.UnitY * ((spear.SelectionBox?.Y2 ?? 0) / 2);
		var fromOrigin = new Vector3((float)(pos.X - origin.X), (float)(pos.Y - origin.Y), (float)(pos.Z - origin.Z)) + pivot;
		return new GripSegment(fromOrigin + Vector3.TransformNormal(local.Start * client.Size - pivot, rotation),
			fromOrigin + Vector3.TransformNormal(local.End * client.Size - pivot, rotation));
	}

	private static GripSegment? ReadShaft(CompositeShape shape, ICoreAPI api) {
		var model = SailShape.Read(api, shape.Base);
		if (model?.Elements == null || new SailShape(model, Vector3.Zero).Bounds() is not { } bounds) return null;
		var (min, max) = bounds;
		var center = (min + max) / 2;
		var offset = new Vector3(shape.offsetX - 0.5f, shape.offsetY, shape.offsetZ - 0.5f);
		return new GripSegment(new Vector3(min.X, center.Y, center.Z) + offset, new Vector3(max.X, center.Y, center.Z) + offset);
	}
}
