using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace VintageKnuckle;

internal sealed class GripPositions {
	// vanilla can deliver several movement packets in one tick
	private readonly Sample[] samples = new Sample[128];
	private int next, count, dimension, version;
	private readonly record struct Sample(double X, double Y, double Z, double Speed, long Entered, long Time);
	public long Latest { get; private set; }
	public int Generation { get; private set; }
	public int PathGeneration { get; private set; }

	public void Clear() {
		this.Generation++;
		this.ClearPath();
	}

	private void ClearPath() {
		this.count = this.next = 0;
		this.PathGeneration++;
	}

	public bool Matches(EntityPlayer player) => this.dimension == player.Pos.Dimension &&
		this.version == player.WatchedAttributes.GetInt("positionVersionNumber");

	public bool Record(EntityPlayer player, long now) {
		var pos = player.Pos;
		int version = player.WatchedAttributes.GetInt("positionVersionNumber");
		if (this.dimension != pos.Dimension || this.version != version) this.Clear();
		this.dimension = pos.Dimension;
		this.version = version;

		var sample = new Sample(pos.X, pos.Y, pos.Z, pos.Motion.Length(), now, now);
		if (this.count > 0) {
			var last = this.Last(0);
			if (last.X == sample.X && last.Y == sample.Y && last.Z == sample.Z) {
				this.samples[(this.next + this.samples.Length - 1) % this.samples.Length] = sample with { Entered = last.Entered };
				return false;
			}
			double ticks = Math.Clamp((now - last.Time) * 0.06, 1, 6);
			double allowance = Math.Min(GlobalConstants.ChunkSize, player.Properties.EyeHeight + Math.Max(last.Speed, sample.Speed) * ticks);
			double dx = pos.X - last.X, dy = pos.Y - last.Y, dz = pos.Z - last.Z;
			// a packet gap breaks the path but can still provide a new live pose
			if (dx * dx + dy * dy + dz * dz > allowance * allowance) this.ClearPath();
		}

		this.samples[this.next] = sample;
		this.next = (this.next + 1) % this.samples.Length;
		this.count = Math.Min(this.count + 1, this.samples.Length);
		this.Latest++;
		return true;
	}

	private Sample Last(int back) => this.samples[(this.next + this.samples.Length - 1 - back) % this.samples.Length];

	public Vec3d? NextPose(EntityPlayer player, GripPoint request, long since, long until, ref long observed, bool first, bool live) {
		var pos = player.Pos;
		Vec3d? from = null, target = null;
		for (long i = Math.Max(observed + 1, this.Latest - this.count + 1); i <= this.Latest; i++) {
			observed = i;
			var sample = this.Last((int)(this.Latest - i));
			if (sample.Time < since || sample.Entered > until) continue;
			if (live && sample.X == pos.X && sample.Y == pos.Y && sample.Z == pos.Z) return null;
			(from ??= new Vec3d()).Set(sample.X, sample.Y, sample.Z);
			if (first || request.OnSail || LedgeFinder.InReach(player, target ??= request.Position, 0.35, from)) return from;
		}
		return null;
	}

	public Vec3d? OnPath(EntityPlayer player, GripPoint request, long since, long until, ref long observed) {
		if (request.SweptFrom is not { } from || !float.IsFinite(from.X) || !float.IsFinite(from.Y) || !float.IsFinite(from.Z)) return null;
		double x = request.X + (double)from.X, y = request.Y + (double)from.Y, z = request.Z + (double)from.Z;

		for (long i = Math.Max(observed + 1, this.Latest - this.count + 2); i <= this.Latest; i++) {
			observed = i;
			int back = (int)(this.Latest - i);
			var b = this.Last(back);
			var a = this.Last(back + 1);
			if (a.Time < since || b.Entered > until) continue;
			double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
			double lengthSq = dx * dx + dy * dy + dz * dz;
			if (lengthSq == 0) continue;

			double t = Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy + (z - a.Z) * dz) / lengthSq, 0, 1);
			double px = a.X + t * dx, py = a.Y + t * dy, pz = a.Z + t * dz;
			double ex = x - px, ey = y - py, ez = z - pz;
			const double tolerance = 0.05;
			if (ex * ex + ey * ey + ez * ez > tolerance * tolerance) continue;

			if (LedgeFinder.BodyBlocked(player, new Vec3d(a.X, a.Y, a.Z), new Vec3d(b.X, b.Y, b.Z))) return null;
			return new Vec3d(px, py, pz);
		}
		return null;
	}
}
