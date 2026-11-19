using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace VintageKnuckle;

internal static class FirstPersonArms {
	private static readonly ConditionalWeakTable<MeshData, HashSet<int>> ArmJoints = new();

	public static void Patch(Harmony harmony, ICoreClientAPI api) {
		if (!api.ModLoader.IsModEnabled("playermodellib")) return;
		var renderer = AccessTools.TypeByName("PlayerModelLib.CustomPlayerShapeRenderer");
		if (renderer == null) return;

		var build = AccessTools.DeclaredMethod(renderer, "CustomEntityTesselateShapeOffThread");
		var upload = AccessTools.GetDeclaredMethods(renderer).FirstOrDefault(m =>
			m.Name.StartsWith("<CustomTesselateOffThread>", StringComparison.Ordinal) &&
			m.GetParameters() is [{ ParameterType: var type }] && type == typeof(MeshData));
		if (build == null || upload == null) {
			api.Logger.Warning("Vintage Knuckle could not find Player Model Lib's first person mesh builder.");
			return;
		}

		harmony.Patch(build, transpiler: new HarmonyMethod(typeof(FirstPersonArms), nameof(FirstPersonArms.CaptureShape)));
		harmony.Patch(upload, transpiler: new HarmonyMethod(typeof(FirstPersonArms), nameof(FirstPersonArms.FilterArms)));
	}

	private static IEnumerable<CodeInstruction> CaptureShape(IEnumerable<CodeInstruction> code) {
		var build = AccessTools.Method(typeof(ITesselatorAPI), nameof(ITesselatorAPI.TesselateShape),
			[typeof(TesselationMetaData), typeof(Shape), typeof(MeshData).MakeByRefType()]);
		foreach (var instruction in code) {
			if (instruction.Calls(build)) {
				yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
				yield return CodeInstruction.Call(typeof(FirstPersonArms), nameof(FirstPersonArms.Build));
			} else {
				yield return instruction;
			}
		}
	}

	private static void Build(ITesselatorAPI tess, TesselationMetaData meta, Shape shape, out MeshData mesh, EntityPlayerShapeRenderer renderer) {
		var player = GripMarkers.PlayerOf(renderer);
		var left = player.GetBoneName("upperArmLBoneName", "UpperArmL");
		var right = player.GetBoneName("upperArmRBoneName", "UpperArmR");
		tess.TesselateShape(meta, shape, out mesh);
		var arms = new HashSet<int>();
		var body = new HashSet<int>();

		void Visit(ShapeElement element, bool arm) {
			arm |= string.Equals(element.Name, left, StringComparison.OrdinalIgnoreCase) ||
				string.Equals(element.Name, right, StringComparison.OrdinalIgnoreCase);
			if (element.FacesResolved?.Any(face => face != null) == true) {
				var joints = arm ? arms : body;
				joints.Add(element.JointId);
			}

			if (element.Children == null) return;
			foreach (var child in element.Children) Visit(child, arm);
		}

		foreach (var root in shape.Elements) Visit(root, false);
		if (arms.Count == 0) return;

		arms.ExceptWith(body);
		FirstPersonArms.ArmJoints.Add(mesh, arms);
	}

	private static IEnumerable<CodeInstruction> FilterArms(IEnumerable<CodeInstruction> code) {
		var add = AccessTools.Method(typeof(MeshData), nameof(MeshData.AddMeshData),
			[typeof(MeshData), typeof(MeshData.MeshDataFilterDelegate)]);
		var invoke = AccessTools.Method(typeof(MethodBase), nameof(MethodBase.Invoke), [typeof(object), typeof(object[])]);
		var invokeRenderer = AccessTools.Method(typeof(FirstPersonArms), nameof(FirstPersonArms.InvokeWithOptionalPose));
		foreach (var instruction in code.MethodReplacer(invoke, invokeRenderer)) {
			if (instruction.Calls(add)) {
				yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
				yield return CodeInstruction.LoadField(typeof(EntityPlayerShapeRenderer), "renderMode");
				yield return CodeInstruction.Call(typeof(FirstPersonArms), nameof(FirstPersonArms.AddFirstPersonFaces));
			} else {
				yield return instruction;
			}
		}
	}

	private static object? InvokeWithOptionalPose(MethodBase method, object renderer, object?[]? args) {
		// pml still asks custom models for vanilla bones
		if (method.Name == "loadJointIdsRecursive" && args is [null, _]) return null;
		return method.Invoke(renderer, args);
	}

	private static void AddFirstPersonFaces(MeshData target, MeshData source, MeshData.MeshDataFilterDelegate include, RenderMode mode) {
		if (mode == RenderMode.ImmersiveFirstPerson || !FirstPersonArms.ArmJoints.TryGetValue(source, out var arms)) {
			target.AddMeshData(source, include);
			return;
		}

		// the animator can already belong to the next skin by the time this mesh uploads
		target.AddMeshData(source, face => arms.Contains(source.CustomInts.Values[face * 4]));
	}
}
