using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using Vintagestory.API.Util;
using Plane = System.Numerics.Plane;

namespace VintageKnuckle;

internal sealed class GripMarkers : IRenderer {
	private const EnumRenderStage Stage = EnumRenderStage.AfterOIT;
	private static readonly AccessTools.FieldRef<EntityPlayerShapeRenderer, MultiTextureMeshRef> FirstPersonMesh =
		AccessTools.FieldRefAccess<EntityPlayerShapeRenderer, MultiTextureMeshRef>("firstPersonMeshRef");
	private static readonly AccessTools.FieldRef<EntityPlayerShapeRenderer, MultiTextureMeshRef> ThirdPersonMesh =
		AccessTools.FieldRefAccess<EntityPlayerShapeRenderer, MultiTextureMeshRef>("thirdPersonMeshRef");
	private static readonly AccessTools.FieldRef<EntityPlayerShapeRenderer, RenderMode> PlayerRenderMode =
		AccessTools.FieldRefAccess<EntityPlayerShapeRenderer, RenderMode>("renderMode");

	private static readonly AccessTools.FieldRef<EntityShapeRenderer, ITexPositionSource> RendererTextures =
		AccessTools.FieldRefAccess<EntityShapeRenderer, ITexPositionSource>("defaultTexSource");

	private readonly ICoreClientAPI api;
	private IShaderProgram shader = null!;
	private readonly Grip left, right;
	private readonly Hands local = new();
	private readonly ConditionalWeakTable<EntityPlayer, Hands> others = new();
	internal static readonly AccessTools.FieldRef<EntityPlayerShapeRenderer, EntityPlayer> PlayerOf =
		AccessTools.FieldRefAccess<EntityPlayerShapeRenderer, EntityPlayer>("entityPlayer");
	private readonly Vec4d world = new(), view = new(), clip = new();
	private readonly float[] viewRotation = Mat4f.Create(), relativeClip = Mat4f.Create();
	private readonly BlockPos lightPos = new(0);
	private float[] posed = [];
	private double frameTime;

	public double RenderOrder => 0.9;
	public int RenderRange => 8;

	internal static float ReturnProgress(double elapsed) => GameMath.SmoothStep((float)Math.Clamp(elapsed / 0.22, 0, 1));

	private sealed class HandTextures(ITexPositionSource source, Size2i atlasSize, TextureAtlasPosition missing) : ITexPositionSource {
		public Size2i AtlasSize => atlasSize;
		public TextureAtlasPosition this[string code] {
			get {
				try {
					return source[code] ?? missing;
				} catch (KeyNotFoundException) {
					return missing;
				}
			}
		}
	}

	private sealed class Hands : IDisposable {
		public readonly Hand Left = new(true), Right = new(false);
		public bool Visible => this.Left.Contact != null || this.Right.Contact != null;

		public (float Left, float Right) Spread(float size) => GripMarkers.HandsOverlap(this.Left.Contact, this.Right.Contact, size)
			? (1 - this.Right.Return, 1 - this.Left.Return) : (0, 0);

		public void Dispose() {
			this.Left.Mesh?.Dispose();
			this.Right.Mesh?.Dispose();
		}
	}

	private sealed class Hand(bool left) {
		private readonly ConditionalWeakTable<IAnimator, HangingArm> rigs = new();
		private ShapeElement? source;
		private MultiTextureMeshRef? playerMesh;

		private Ledge? anchor;
		private Palm? lastPalm;
		private double lastHeld, lastFrame = double.NaN;
		private int trackedDimension;
		private byte[]? lastGrip;
		private int lastDimension;
		private GripOrientation heading;

		public Ledge? Contact { get; private set; }
		public float PalmRoll => left ? -1 : 1;
		public float Return { get; private set; }
		public float Strain;
		public readonly float[] RestClip = new float[16];
		public MultiTextureMeshRef? Mesh;

		public void Track(IWorldAccessor world, Ledge? grip, int dimension, double frame) {
			bool same = object.ReferenceEquals(grip, this.anchor);
			if (same && frame == this.lastFrame && dimension == this.trackedDimension) return;
			this.lastFrame = frame;
			this.trackedDimension = dimension;
			if (grip != null && !same) {
				this.rigs.Clear();
				this.heading = default;
				this.lastPalm = null;
			}
			this.anchor = grip;

			if (grip?.Dimension == dimension && WindmillGrip.Resolve(world, grip, out _) is { } lip) {
				this.Contact = lip;
				this.lastHeld = frame;
				this.Return = 0;
			} else if (this.Contact != null) {
				this.Return = GripMarkers.ReturnProgress(frame - this.lastHeld);
				if (this.Return >= 1 || this.Contact.Dimension != dimension) this.Contact = null;
			}
		}

		public void Read(byte[]? bytes, EntityPlayer player, double frame) {
			int dimension = player.Pos.Dimension;
			var anchor = this.anchor;
			if (this.lastGrip != bytes || this.lastDimension != dimension) {
				this.lastGrip = bytes;
				this.lastDimension = dimension;
				var grip = bytes is { Length: > 0 } ? SerializerUtil.Deserialize<GripPoint>(bytes) : null;
				anchor = grip?.Dimension == dimension ? grip.ToLedge(dimension) : null;
			}
			this.Track(player.World, anchor, dimension, frame);
		}

		public bool Draws(ShapeElement? element) => this.Mesh != null && this.source == element;

		public HangingArm Rig(EntityPlayer player, IAnimator animator) {
			if (this.rigs.TryGetValue(animator, out var rig)) return rig;
			rig = new HangingArm(player, animator, left);
			this.rigs.Add(animator, rig);
			return rig;
		}

		public Palm Place(EntityPlayer player, HangingArm arm, Ledge lip, float shared, double frame) {
			if (this.Return > 0 && this.lastPalm is { } last) return last;

			float size = player.Properties.Client.Size;
			float spread = shared * (left ? -0.55f : 0.55f) * arm.Width * size;
			double x = lip.FaceZ == 0 ? lip.HitX : Math.Clamp(lip.HitX + lip.FaceZ * spread, lip.EdgeMin, lip.EdgeMax);
			double z = lip.FaceX == 0 ? lip.HitZ : Math.Clamp(lip.HitZ - lip.FaceX * spread, lip.EdgeMin, lip.EdgeMax);
			double y = lip.Top + 0.006 * size;

			var toward = new Vector3((float)(player.Pos.X - x), (float)(player.Pos.Y + player.LocalEyePos.Y * 0.85 - y),
				(float)(player.Pos.Z - z));
			float distance = Math.Max(toward.Length(), arm.Length * size);
			if (distance > 0) toward /= distance;

			var flat = new Vector3(toward.X, 0, toward.Z);
			bool block = lip.SpearId == null && lip.SailPoint == null && !lip.OnBeam;
			var facing = new Vector3(lip.FaceX, 0, lip.FaceZ);
			const float minRun = 0.55f;
			if (block) {
				float belowLip = Math.Clamp(1 - 2 * toward.Y, 0, 1);
				flat += facing * (belowLip * Math.Max(0, minRun - Vector3.Dot(flat, facing)));
			} else {
				facing = Vector3.Cross(ClimbMotion.CameraRight(player.BodyYaw), Vector3.UnitY);
			}

			var heading = this.heading.Follow(frame, Vector3.UnitY, flat, facing);
			float run = Math.Max(minRun, flat.Length());
			float rise = block ? Math.Min(toward.Y, run * 0.7f) : toward.Y;
			var outward = Vector3.Normalize(heading * run + Vector3.UnitY * rise);
			var across = Vector3.Cross(Vector3.UnitY, heading);

			float fromFingertips = arm.Length * (block ? 0.22f : 0.48f);
			var palm = new Palm(x, y, z, across, Vector3.Cross(outward, across), outward, fromFingertips);
			this.lastPalm = palm;
			return palm;
		}

		public void Update(ICoreClientAPI api, EntityPlayer player, MultiTextureMeshRef currentMesh, ShapeElement? element) {
			if (this.source == element && this.playerMesh == currentMesh) return;

			this.Mesh?.Dispose();
			this.Mesh = null;
			this.playerMesh = null;

			var model = player.Properties.Client.LoadedShapeForEntity ?? player.Properties.Client.LoadedShape;
			if (model == null || element is not { From: { Length: 3 } from, To: { Length: 3 } to }) return;
			if (player.Properties.Client.Renderer is not EntityShapeRenderer renderer) return;

			var textures = GripMarkers.RendererTextures(renderer);
			if (textures?.AtlasSize is not { } atlasSize || api.EntityTextureAtlas.UnknownTexturePosition is not { } missing) return;
			this.source = element;
			this.playerMesh = currentMesh;

			var root = element.Clone();
			root.ParentElement = null;
			root.RotationOrigin = root.From = new double[3];
			root.RotationX = root.RotationY = root.RotationZ = 0;
			root.To = [to[0] - from[0], to[1] - from[1], to[2] - from[2]];
			root.Children = root.Children?
				.Where(child => child.Name?.StartsWith("ItemAnchor", StringComparison.OrdinalIgnoreCase) != true)
				.ToArray();

			var shape = new Shape {
				Elements = [root], Textures = model.Textures, TextureSizes = model.TextureSizes,
				TextureWidth = model.TextureWidth, TextureHeight = model.TextureHeight
			};
			api.Tesselator.TesselateShape("vintageknuckle-hand", shape, out var mesh, new HandTextures(textures, atlasSize, missing));
			if (mesh.VerticesCount > 0) this.Mesh = api.Render.UploadMultiTextureMesh(mesh);
		}
	}

	public GripMarkers(ICoreClientAPI api, Grip left, Grip right) {
		this.api = api;
		this.left = left;
		this.right = right;
		this.LoadShader();
		api.Event.ReloadShader += this.LoadShader;
		api.Event.RegisterRenderer(this, EnumRenderStage.Before, "vintageknuckle-hands");
		api.Event.RegisterRenderer(this, GripMarkers.Stage, "vintageknuckle-hands");
	}

	private bool LoadShader() {
		this.shader = this.api.Shader.NewShaderProgram();
		this.shader.AssetDomain = "vintageknuckle";
		this.api.Shader.RegisterFileShaderProgram("vkgrip", this.shader);
		return this.shader.Compile();
	}

	public void OnRenderFrame(float dt, EnumRenderStage stage) {
		if (stage == EnumRenderStage.Before) {
			this.frameTime += dt;
			return;
		}
		if (this.shader.LoadError || this.api.World.Player?.Entity is not { } player) return;

		this.ReadLocal(player);
		if (!this.local.Visible) return;
		if (player.Properties.Client.Renderer is not EntityPlayerShapeRenderer renderer ||
			GripMarkers.PlayerRenderMode(renderer) != RenderMode.FirstPerson || player.AnimManager?.Animator is not { } animator) return;
		var mesh = GripMarkers.FirstPersonMesh(renderer) ?? GripMarkers.ThirdPersonMesh(renderer);
		if (mesh == null) return;

		var render = this.api.Render;
		var camera = render.PerspectiveViewMat;
		for (int i = 0; i < 16; i++) this.viewRotation[i] = (float)camera[i];
		this.viewRotation[12] = this.viewRotation[13] = this.viewRotation[14] = 0;
		Mat4f.Mul(this.relativeClip, render.CurrentProjectionMatrix, this.viewRotation);

		var previous = render.CurrentActiveShader;
		previous?.Stop();
		this.shader.Use();
		try {
			this.shader.UniformMatrix("relativeClip", this.relativeClip);
			this.shader.Uniform("ambient", render.AmbientColor);

			var spread = this.local.Spread(player.Properties.Client.Size);
			this.Draw(player, animator, mesh, this.local.Left, spread.Left);
			this.Draw(player, animator, mesh, this.local.Right, spread.Right);
		} finally {
			this.shader.Stop();
			previous?.Use();
		}
	}

	private void ReadLocal(EntityPlayer player) {
		int dimension = player.Pos.Dimension;
		this.local.Left.Track(this.api.World, this.left.Hold, dimension, this.frameTime);
		this.local.Right.Track(this.api.World, this.right.Hold, dimension, this.frameTime);
		this.local.Left.Strain = this.left.Strain;
		this.local.Right.Strain = this.right.Strain;
	}

	private static bool HandsOverlap(Ledge? left, Ledge? right, float size) {
		if (left == null || right == null) return false;
		if (left.FaceX != right.FaceX || left.FaceZ != right.FaceZ || left.Top != right.Top) return false;

		double separation = Math.Abs(left.HitX - right.HitX) + Math.Abs(left.HitZ - right.HitZ);
		return separation < 0.16 * size;
	}

	private readonly record struct Palm(double X, double Y, double Z, Vector3 Across, Vector3 Up, Vector3 Outward, float FromFingertips);

	private Hands? HandsFor(EntityPlayer player, EntityPlayer viewer) {
		if (player == viewer) {
			this.ReadLocal(player);
			return this.local;
		}
		if (player.Pos.Dimension != viewer.Pos.Dimension) return null;

		var left = player.WatchedAttributes.GetBytes(GripPoint.LeftKey);
		var right = player.WatchedAttributes.GetBytes(GripPoint.RightKey);
		if (!this.others.TryGetValue(player, out var hands)) {
			if (left is not { Length: > 0 } && right is not { Length: > 0 }) return null;
			hands = this.others.GetOrCreateValue(player);
		}

		hands.Left.Read(left, player, this.frameTime);
		hands.Right.Read(right, player, this.frameTime);
		if (!hands.Visible && left is not { Length: > 0 } && right is not { Length: > 0 }) this.others.Remove(player);
		return hands;
	}

	private static Matrix4x4 AsMatrix(float[] values) => MemoryMarshal.Cast<float, Matrix4x4>(values)[0];

	public float[] PoseArms(EntityPlayer player, IAnimator animator, EntityPlayerShapeRenderer renderer, float[] original, bool shadow) {
		var viewer = this.api.World.Player.Entity;
		if (this.HandsFor(player, viewer) is not { Visible: true } hands) return original;

		var render = this.api.Render;
		var model = GripMarkers.AsMatrix(renderer.ModelMat);
		bool remote = player != viewer;
		bool firstPerson = !shadow && !remote && GripMarkers.PlayerRenderMode(renderer) == RenderMode.FirstPerson;
		var modelMatrix = firstPerson ? model * GripMarkers.AsMatrix(render.CurrentModelviewMatrix) : model;
		if (!Matrix4x4.Invert(modelMatrix, out var toModel)) return original;

		Plane? eyeLevel = null;
		if (firstPerson) {
			// the model matrix starts at the feet
			eyeLevel = Plane.Normalize(new Plane(model.M12, model.M22, model.M32, model.M42 - (float)player.LocalEyePos.Y));
		}

		if (this.posed.Length != original.Length) this.posed = new float[original.Length];
		original.CopyTo(this.posed, 0);

		void Pose(Hand hand, float shared) {
			if (hand.Contact is not { } lip || lip.Dimension != player.Pos.Dimension) return;

			var arm = hand.Rig(player, animator);
			arm.Return = hand.Return;
			var palm = hand.Place(player, arm, lip, shared, this.frameTime);

			if (!firstPerson) {
				var camera = viewer.CameraPos;
				var origin = remote ? new FastVec3d(camera.X, camera.Y, camera.Z) : viewer.Pos.XYZFast;
				double y = palm.Y + (player.Pos.InternalY - player.Pos.Y);
				var contact = new Vector3((float)(palm.X - origin.X), (float)(y - origin.Y), (float)(palm.Z - origin.Z));
				var pad = new Vector3(arm.Width / 2, palm.FromFingertips, hand.PalmRoll > 0 ? arm.Depth : 0);
				var placement = Matrix4x4.CreateTranslation(-pad) * Matrix4x4.CreateScale(player.Properties.Client.Size)
					* Matrix4x4.CreateWorld(contact, palm.Up * hand.PalmRoll, palm.Outward) * toModel;
				arm.PoseOnModel(original, this.posed, pad, placement, this.frameTime, shadow, remote);
				return;
			}

			if (arm.HandPose is { } native) {
				var projection = GripMarkers.AsMatrix(render.CurrentProjectionMatrix);
				var restClip = GripMarkers.AsMatrix(native.AnimModelMatrix) * modelMatrix * projection;
				MemoryMarshal.Cast<float, Matrix4x4>(hand.RestClip.AsSpan())[0] = restClip;
			}

			bool planted = hand.Draws(arm.Hand) && !this.shader.LoadError;
			var wrist = this.FirstPersonWrist(player, arm, palm, toModel);
			arm.PoseFirstPerson(original, this.posed, wrist, this.frameTime, planted, eyeLevel);
		}

		var spread = hands.Spread(player.Properties.Client.Size);
		Pose(hands.Left, spread.Left);
		Pose(hands.Right, spread.Right);
		return this.posed;
	}

	private Vector3 FirstPersonWrist(EntityPlayer player, HangingArm arm, Palm palm, Matrix4x4 toModel) {
		var offset = (palm.Outward * (arm.Length - palm.FromFingertips) + palm.Up * (arm.Depth * 0.5f)) * player.Properties.Client.Size;
		this.world.Set(palm.X + offset.X, palm.Y + offset.Y + (player.Pos.InternalY - player.Pos.Y), palm.Z + offset.Z, 1);

		// first person draws the arms with their own fov
		var render = this.api.Render;
		Mat4d.MulWithVec4(render.PerspectiveViewMat, this.world, this.view);
		var projection = render.CurrentProjectionMatrix;
		var worldProjection = render.PerspectiveProjectionMat;
		var wrist = new Vector3((float)(this.view.X * worldProjection[0] / projection[0]),
			(float)(this.view.Y * worldProjection[5] / projection[5]), (float)this.view.Z);
		return Vector3.Transform(wrist, toModel);
	}

	private void Draw(EntityPlayer player, IAnimator animator, MultiTextureMeshRef mesh, Hand hand, float shared) {
		if (hand.Contact is not { } lip || lip.Dimension != player.Pos.Dimension) return;

		var arm = hand.Rig(player, animator);
		hand.Update(this.api, player, mesh, arm.Hand);
		if (hand.Mesh == null) return;

		float size = player.Properties.Client.Size;
		var palm = hand.Place(player, arm, lip, shared, this.frameTime);
		this.world.Set(palm.X, palm.Y + (player.Pos.InternalY - player.Pos.Y), palm.Z, 1);
		var render = this.api.Render;
		Mat4d.MulWithVec4(render.PerspectiveViewMat, this.world, this.view);
		Mat4d.MulWithVec4(render.PerspectiveProjectionMat, this.view, this.clip);

		this.shader.Uniform("anchor", (float)this.clip.X, (float)this.clip.Y, (float)this.clip.Z, (float)this.clip.W);
		if (hand.Return > 0) this.shader.UniformMatrix("restClip", hand.RestClip);
		this.shader.Uniform("handReturn", hand.Return);
		this.shader.Uniform("handSize", arm.Width, arm.Length, arm.Depth);
		this.shader.Uniform("fromFingertips", palm.FromFingertips);
		this.shader.Uniform("palmRoll", hand.PalmRoll);
		this.shader.Uniform("outward", palm.Outward.X, palm.Outward.Y, palm.Outward.Z);
		this.shader.Uniform("across", palm.Across.X, palm.Across.Y, palm.Across.Z);
		this.shader.Uniform("up", palm.Up.X, palm.Up.Y, palm.Up.Z);
		this.shader.Uniform("modelSize", size);
		this.shader.Uniform("strain", hand.Strain);
		this.lightPos.dimension = lip.Dimension;
		this.lightPos.Set((int)Math.Floor(this.world.X), (int)Math.Floor(lip.Top), (int)Math.Floor(this.world.Z));
		this.shader.Uniform("light", this.api.World.BlockAccessor.GetLightRGBs(this.lightPos));

		render.RenderMultiTextureMesh(hand.Mesh, "entityTex");
	}

	public void Dispose() {
		this.api.Event.ReloadShader -= this.LoadShader;
		this.api.Event.UnregisterRenderer(this, EnumRenderStage.Before);
		this.api.Event.UnregisterRenderer(this, GripMarkers.Stage);
		this.local.Dispose();
		this.others.Clear();
		this.shader.Dispose();
	}
}
