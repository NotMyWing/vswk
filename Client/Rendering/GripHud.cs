using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace VintageKnuckle;

internal sealed class GripHud : HudElement {
	private readonly Grip left, right;
	private readonly int palm, fist;
	private readonly Vec4f tint = new();
	private float phase;
	public bool ShowFreeHands, LeftInReach, RightInReach;

	public GripHud(ICoreClientAPI api, Grip left, Grip right) : base(api) {
		this.left = left;
		this.right = right;

		int Icon(string name) => api.Render.GetOrLoadTexture(new AssetLocation("vintageknuckle", "textures/gui/" + name + ".png"));
		this.palm = Icon("hand-open");
		this.fist = Icon("hand-grabbed");
	}

	public override void OnRenderGUI(float dt) {
		if (this.capi.HideGuis) return;

		const float cycleSeconds = 1.6f;
		this.phase = (this.phase + dt * MathF.Tau / cycleSeconds) % MathF.Tau;
		this.Draw(this.left, true, this.LeftInReach);
		this.Draw(this.right, false, this.RightInReach);
	}

	private void Draw(Grip grip, bool leftSide, bool inReach) {
		var render = this.capi.Render;
		bool holding = grip.Hold != null;
		if (!holding && !this.ShowFreeHands) return;

		float scale = (float)RuntimeEnv.GUIScale;
		float size = 70f * scale;
		float side = leftSide ? -1 : 1;
		var (sin, cos) = MathF.SinCos(this.phase);
		float x = render.FrameWidth * 0.5f + side * (size * 0.47f + 2.5f * scale * cos);
		float y = (render.FrameHeight - size) * 0.5f + side * 1.5f * scale * sin;

		float opacity = holding || (inReach && grip.Available) ? 1f : 0.48f;
		float cool = 1f - 0.95f * grip.Strain;
		float width = side * size * (holding ? 96f : 104f) / 128f;
		this.tint.Set(1f, cool, cool, opacity);
		render.Render2DTexture(holding ? this.fist : this.palm, MathF.Round(x), MathF.Round(y),
			MathF.Round(width), MathF.Round(size), 50, this.tint);
	}
}
