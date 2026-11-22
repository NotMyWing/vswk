using System.Numerics;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.Client.NoObf;
using Vintagestory.GameContent;

namespace VintageKnuckle;

public sealed partial class VintageKnuckleSystem {
	internal static bool InStance(Entity player) => VintageKnuckleSystem.active is { stance: true } mod && player == mod.api.World.Player?.Entity;

	private void PatchClient(Harmony harmony) {
		FirstPersonArms.Patch(harmony, this.api);
		LadderMovement.Patch(harmony);
		GripSlots.Patch(harmony, new HarmonyMethod(typeof(VintageKnuckleSystem), nameof(VintageKnuckleSystem.CheckHandSlot)));
		harmony.Patch(AccessTools.DeclaredMethod(typeof(EntityShapeRenderer), nameof(EntityShapeRenderer.DoRender3DOpaqueBatched)),
			transpiler: new HarmonyMethod(typeof(VintageKnuckleSystem), nameof(VintageKnuckleSystem.PoseGrips)));
		BeamGrip.PatchIndex(harmony, EnumAppSide.Client);
		harmony.Patch(AccessTools.Method(typeof(EntityProjectileBase), nameof(EntityProjectileBase.SpawnProjectile)),
			postfix: new HarmonyMethod(typeof(VintageKnuckleSystem), nameof(VintageKnuckleSystem.AfterThrow)));

		var physics = AccessTools.DeclaredMethod(typeof(EntityBehaviorPlayerPhysics), nameof(EntityBehaviorPlayerPhysics.SimPhysics));
		harmony.Patch(physics, transpiler: new HarmonyMethod(typeof(VintageKnuckleSystem), nameof(VintageKnuckleSystem.PlayerPhysics)));
		var camera = AccessTools.Method(typeof(ClientMain), "UpdateCameraYawPitch");
		harmony.Patch(camera, prefix: new HarmonyMethod(typeof(VintageKnuckleSystem), nameof(VintageKnuckleSystem.BeforeCameraTurn)),
			postfix: new HarmonyMethod(typeof(VintageKnuckleSystem), nameof(VintageKnuckleSystem.AfterCameraTurn)),
			transpiler: new HarmonyMethod(typeof(VintageKnuckleSystem), nameof(VintageKnuckleSystem.CameraYaw)));
		var tests = AccessTools.Method(typeof(EntityBehaviorControlledPhysics), nameof(EntityBehaviorControlledPhysics.ApplyTests));
		harmony.Patch(tests, transpiler: new HarmonyMethod(typeof(VintageKnuckleSystem), nameof(VintageKnuckleSystem.Landing)));

		var ground = AccessTools.Method(typeof(PModuleOnGround), nameof(PModuleOnGround.Applicable));
		harmony.Patch(ground, postfix: new HarmonyMethod(typeof(VintageKnuckleSystem), nameof(VintageKnuckleSystem.CheckGripJump)));
		harmony.Patch(AccessTools.DeclaredMethod(typeof(ModSystemGliding), "Input_InWorldAction"),
			prefix: new HarmonyMethod(typeof(VintageKnuckleSystem), nameof(VintageKnuckleSystem.AllowGliderInput)));
		var jump = AccessTools.Method(typeof(PModuleOnGround), nameof(PModuleOnGround.DoApply));
		harmony.Patch(jump, prefix: new HarmonyMethod(typeof(VintageKnuckleSystem), nameof(VintageKnuckleSystem.BeforeJump)),
			postfix: new HarmonyMethod(typeof(VintageKnuckleSystem), nameof(VintageKnuckleSystem.AfterGripJump)),
			transpiler: new HarmonyMethod(typeof(VintageKnuckleSystem), nameof(VintageKnuckleSystem.GroundJump)));

		harmony.Patch(AccessTools.Method(typeof(EntityTalkUtil), nameof(EntityTalkUtil.Talk)),
			prefix: new HarmonyMethod(typeof(VintageKnuckleSystem), nameof(VintageKnuckleSystem.OnNewVoiceLine)));
		harmony.Patch(AccessTools.Method(typeof(EntityTalkUtil), "PlaySound",
			[typeof(float), typeof(float), typeof(float), typeof(float), typeof(float)]),
			prefix: new HarmonyMethod(typeof(VintageKnuckleSystem), nameof(VintageKnuckleSystem.PitchJumpVoice)));

		var damage = AccessTools.Method(typeof(EntityPlayer), nameof(EntityPlayer.ShouldReceiveDamage));
		harmony.Patch(damage, postfix: new HarmonyMethod(typeof(VintageKnuckleSystem), nameof(VintageKnuckleSystem.CheckFallDamage)));
	}

	private static void CheckHandSlot(ItemSlot __instance, ref bool __result) {
		if (!__result || VintageKnuckleSystem.active is not { Holding: true, physicsPlayer: { } player } mod) return;
		if (GripSlots.Occupied(player, __instance, mod.left.Hold != null, mod.right.Hold != null)) __result = false;
	}

	private static IEnumerable<CodeInstruction> PoseGrips(IEnumerable<CodeInstruction> code) {
		var matrices = AccessTools.PropertyGetter(typeof(IAnimator), nameof(IAnimator.Matrices));
		foreach (var instruction in code) {
			if (instruction.Calls(matrices)) {
				yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction);
				yield return new CodeInstruction(OpCodes.Ldarg_2);
				yield return CodeInstruction.Call(typeof(VintageKnuckleSystem), nameof(VintageKnuckleSystem.GripPose));
			} else {
				yield return instruction;
			}
		}
	}

	private static float[] GripPose(IAnimator animator, EntityShapeRenderer renderer, bool shadow) {
		var matrices = animator.Matrices;
		if (renderer is not EntityPlayerShapeRenderer model || VintageKnuckleSystem.active?.markers is not { } markers) return matrices;
		return markers.PoseArms(GripMarkers.PlayerOf(model), animator, model, matrices, shadow);
	}

	private static void AfterThrow([HarmonyArgument(0)] Entity projectile, [HarmonyArgument(1)] EntityAgent thrower) {
		if (VintageKnuckleSystem.active is not { stance: true } mod || thrower is not EntityPlayer player ||
			player != mod.api.World.Player.Entity || !SpearGrip.IsSpear(projectile)) return;

		const float recoilFraction = 0.1f;
		var flight = projectile.Pos.Motion;
		var kick = new Vector3((float)flight.X, (float)flight.Y, (float)flight.Z) * -recoilFraction;
		player.Pos.Motion.Add(kick.X, kick.Y, kick.Z);

		float scale = LedgeFinder.ModelScale(player);
		void CheckGrip(Grip hand) {
			if (WindmillGrip.Resolve(player.World, hand.Hold, out _) is not { } lip || lip.Dimension != player.Pos.Dimension) return;

			var arm = VintageKnuckleSystem.FromLip(LedgeFinder.Feet(player.Pos), lip, scale);
			if (!ClimbMotion.RecoilBreaksGrip(kick * ClimbMotion.PhysicsTicksPerSecond, arm)) return;

			hand.LoseSupport();
			mod.PlaySlip(lip, player.Pos.Dimension);
		}

		CheckGrip(mod.left);
		CheckGrip(mod.right);
		mod.SyncGrips();
	}

	private static void CheckFallDamage(EntityPlayer __instance, [HarmonyArgument(0)] DamageSource source, ref bool __result) {
		if (!__result || source.Source != EnumDamageSource.Fall || __instance.World.Side != EnumAppSide.Client) return;
		if (VintageKnuckleSystem.active is not { } mod || __instance != mod.api.World.Player?.Entity) return;
		if (mod.landedBeforeGrip) return;

		int dimension = __instance.Pos.Dimension;
		if (mod.left.Hold?.Dimension == dimension || mod.right.Hold?.Dimension == dimension) __result = false;
	}

	private static void CheckGripJump(Entity entity, EntityControls controls, ref bool __result) {
		var mod = VintageKnuckleSystem.active;
		if (mod == null || entity is not EntityPlayer player || player != mod.api.World.Player?.Entity) return;

		mod.jumpPressed = controls.Jump && !mod.jumpDown;
		mod.jumpDown = controls.Jump;
		mod.jumpedFromGrip = false;

		if (!mod.jumpPressed || !mod.CanJumpFromGrip(player)) return;

		mod.jumpedFromGrip = true;

		// coyote tick
		__result = true;
	}

	private bool CanJumpFromGrip(EntityPlayer player) => this.api.Input.MouseGrabbed && player.Alive &&
		player.MountedOn == null && !player.Swimming && !player.Controls.IsFlying && !player.Controls.NoClip &&
		(this.left.Hold?.Dimension == player.Pos.Dimension || this.right.Hold?.Dimension == player.Pos.Dimension);

	private static bool AllowGliderInput([HarmonyArgument(0)] EnumEntityAction action, [HarmonyArgument(1)] bool down) =>
		action != EnumEntityAction.Jump || !down || VintageKnuckleSystem.active is not { } mod ||
		mod.api.World.Player?.Entity is not { } player || !mod.CanJumpFromGrip(player);

	private static IEnumerable<CodeInstruction> GroundJump(IEnumerable<CodeInstruction> code) =>
		code.MethodReplacer(AccessTools.PropertyGetter(typeof(EntityControls), nameof(EntityControls.Jump)),
			AccessTools.Method(typeof(VintageKnuckleSystem), nameof(VintageKnuckleSystem.WantsJump)));

	private static bool WantsJump(EntityControls controls) {
		if (!controls.Jump) return false;
		if (VintageKnuckleSystem.active is { stance: true } mod && controls == mod.physicsPlayer?.Controls) return mod.jumpPressed;
		return true;
	}

	private struct JumpState(long lastJump) {
		public readonly long Original = lastJump;
		public long BeforeImpulse = lastJump;
		public Vec3d? AirMotion;
	}

	private static void BeforeJump(Entity entity, EntityPos pos, ref long ___lastJump, out JumpState __state) {
		__state = new JumpState(___lastJump);
		if (VintageKnuckleSystem.active is not { jumpedFromGrip: true } mod || entity != mod.api.World.Player?.Entity) return;

		if (!entity.OnGround) __state.AirMotion = pos.Motion.Clone();

		const int jumpCooldownMs = 500;
		___lastJump = Math.Min(___lastJump, entity.World.ElapsedMilliseconds - jumpCooldownMs - 1);
		__state.BeforeImpulse = ___lastJump;
	}

	private static void AfterGripJump(Entity entity, EntityPos pos, ref long ___lastJump, JumpState __state) {
		if (VintageKnuckleSystem.active is not { jumpedFromGrip: true } mod ||
			entity is not EntityPlayer player || player != mod.api.World.Player?.Entity) return;

		// the ground jump applies floor friction even in midair
		if (__state.AirMotion is { } swing) {
			pos.Motion.X = swing.X;
			pos.Motion.Z = swing.Z;
		}

		if (___lastJump == __state.BeforeImpulse) {
			___lastJump = __state.Original;
			mod.jumpedFromGrip = false;
			return;
		}

		pos.Motion.Y = (__state.AirMotion?.Y ?? 0) + pos.Motion.Y * 1.3;
		mod.left.Launch();
		mod.right.Launch();
		mod.SyncGrips(jump: true);

		player.talkUtil?.Talk(EnumTalkType.Thrust);
		mod.jumpVoice = player.talkUtil;
	}

	private static void OnNewVoiceLine(EntityTalkUtil __instance) {
		if (VintageKnuckleSystem.active is { } mod && mod.jumpVoice == __instance) mod.jumpVoice = null;
	}

	private static void PitchJumpVoice(EntityTalkUtil __instance, ref float startPitch, ref float endPitch) {
		if (VintageKnuckleSystem.active?.jumpVoice != __instance) return;
		startPitch *= 0.9f;
		endPitch *= 0.9f;
	}

	private static IEnumerable<CodeInstruction> CameraYaw(IEnumerable<CodeInstruction> code) {
		var wrap = AccessTools.Method(typeof(GameMath), nameof(GameMath.Mod), [typeof(float), typeof(float)]);
		var yaw = AccessTools.Field(typeof(ClientMain), "mouseYaw");
		var instructions = code.ToList();
		for (int i = 0; i < instructions.Count; i++) {
			var instruction = instructions[i];
			if (instruction.Calls(wrap) && i + 1 < instructions.Count && instructions[i + 1].StoresField(yaw)) {
				yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
				yield return CodeInstruction.Call(typeof(VintageKnuckleSystem), nameof(VintageKnuckleSystem.WrapCameraYaw));
			} else {
				yield return instruction;
			}
		}
	}

	private static float WrapCameraYaw(float yaw, float period, ClientMain game) {
		if (VintageKnuckleSystem.active is { stance: true } mod && mod.api.World == game) mod.unwrappedCameraYaw = yaw;
		return GameMath.Mod(yaw, period);
	}

	private static void BeforeCameraTurn(out Vector2? __state) {
		__state = null;
		var mod = VintageKnuckleSystem.active;
		if (mod == null || !mod.stance) return;
		mod.unwrappedCameraYaw = null;
		var player = mod.api.World.Player?.Entity;
		if (player == null || !mod.CanReach(player)) return;
		__state = new Vector2(mod.api.Input.MouseYaw, mod.api.Input.MousePitch);
	}

	private static void AfterCameraTurn(Vector2? __state) {
		var mod = VintageKnuckleSystem.active;
		if (mod == null || !mod.stance || __state is not Vector2 previousAim) return;

		var player = mod.api.World.Player;
		float yaw = mod.api.Input.MouseYaw;
		if (mod.unwrappedCameraYaw is { } rawYaw) yaw = rawYaw;
		else if (player.CameraMode == EnumCameraMode.Overhead) yaw = previousAim.X + GameMath.AngleRadDistance(previousAim.X, yaw);
		mod.unwrappedCameraYaw = null;
		var currentAim = new Vector2(yaw, player.CameraPitch);
		if (currentAim == previousAim) return;

		mod.SweepCamera(player.Entity, previousAim, currentAim);
	}

	private static IEnumerable<CodeInstruction> PlayerPhysics(IEnumerable<CodeInstruction> code) {
		var physics = typeof(EntityBehaviorControlledPhysics);
		var hooks = typeof(VintageKnuckleSystem);
		var motion = AccessTools.Method(physics, nameof(EntityBehaviorControlledPhysics.MotionAndCollision));
		var movement = AccessTools.Method(physics, nameof(EntityBehaviorControlledPhysics.ApplyTests));
		var instructions = code.ToList();
		if (!instructions.Any(op => op.Calls(motion)) || !instructions.Any(op => op.Calls(movement))) return instructions;

		return instructions.MethodReplacer(motion, AccessTools.Method(hooks, nameof(VintageKnuckleSystem.ApplyMotion)))
			.MethodReplacer(movement, AccessTools.Method(hooks, nameof(VintageKnuckleSystem.ApplyMovement)));
	}

	private static void ApplyMotion(EntityBehaviorPlayerPhysics physics, EntityPos pos, EntityControls controls, float dt) {
		physics.MotionAndCollision(pos, controls, dt);

		var mod = VintageKnuckleSystem.active;
		if (mod == null || physics.Entity is not EntityPlayer player || player != mod.physicsPlayer) return;

		mod.nativeStep = true;
		mod.Tick(player, pos, controls, dt);
		mod.SyncGrips();
	}

	private static void ApplyMovement(EntityBehaviorPlayerPhysics physics, EntityPos pos, EntityControls controls, float dt, bool remote) {
		physics.ApplyTests(pos, controls, dt, remote);

		var mod = VintageKnuckleSystem.active;
		if (remote || mod == null || physics.Entity is not EntityPlayer player || player != mod.physicsPlayer) return;

		mod.FinishMovement(player, pos);
		mod.SyncGrips();
	}

	private static IEnumerable<CodeInstruction> Landing(IEnumerable<CodeInstruction> code) =>
		code.MethodReplacer(AccessTools.Method(typeof(Entity), nameof(Entity.OnFallToGround)),
			AccessTools.Method(typeof(VintageKnuckleSystem), nameof(VintageKnuckleSystem.OnLanding)));

	private static void OnLanding(Entity entity, double motionY) {
		// the collision tests have already committed the landing pose
		if (VintageKnuckleSystem.active is { nativeStep: true } mod && entity is EntityPlayer player && player == mod.physicsPlayer) {
			bool held = mod.Holding;
			mod.FinishMovement(player, player.Pos);
			if (!player.OnGround) return;
			if (!held && mod.Holding) {
				mod.landedBeforeGrip = true;
				try {
					entity.OnFallToGround(motionY);
				} finally {
					mod.landedBeforeGrip = false;
				}
				return;
			}
		}
		entity.OnFallToGround(motionY);
	}
}
