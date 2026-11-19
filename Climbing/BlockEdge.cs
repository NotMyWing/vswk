using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VintageKnuckle;

internal readonly record struct BlockEdge {
	private const double Clearance = 0.00001;
	private readonly bool positiveFace, underside;
	private readonly double plane;
	public bool AlongX { get; }
	public double Height { get; }
	public (double Min, double Max) Span { get; }
	private double SightPlane => this.plane + (this.positiveFace ? BlockEdge.Clearance : -BlockEdge.Clearance);
	private double SightHeight => this.Height + (this.underside ? -BlockEdge.Clearance : BlockEdge.Clearance);

	public BlockEdge(CollisionBox box, BlockFacing face, bool underside) {
		var normal = face.Normali;
		this.AlongX = normal.Z != 0;
		this.positiveFace = normal.X + normal.Z > 0;
		this.underside = underside;

		var across = box.Span(!this.AlongX);
		this.plane = this.positiveFace ? across.Max : across.Min;
		this.Height = underside ? box.Y1 : box.Y2;
		this.Span = box.Span(this.AlongX);
	}

	public (double X, double Z) PointAt(double along) => this.AlongX ? (along, this.plane) : (this.plane, along);

	public Vec3d SightPoint(double along, Vec3d? point = null) =>
		(point ?? new Vec3d()).Set(this.AlongX ? along : this.SightPlane, this.SightHeight, this.AlongX ? this.SightPlane : along);

	public bool CoveredBy(CollisionBox neighbor, out (double Min, double Max) span) {
		span = default;
		if (neighbor.SelectionOnly || neighbor.Y1 > this.Height || neighbor.Y2 < this.Height) return false;
		var (lo, hi) = neighbor.Span(!this.AlongX);
		bool outside = this.positiveFace ? lo <= this.plane && hi > this.plane : lo < this.plane && hi >= this.plane;
		bool covered = (this.underside ? neighbor.Y1 < this.Height : neighbor.Y2 > this.Height) && lo <= this.plane && hi >= this.plane;
		if (!outside && !covered) return false;
		span = neighbor.Span(this.AlongX);
		return true;
	}

	public void Trim(List<(double Min, double Max)> spans, CollisionBox neighbor, Vec3d eye) {
		if (neighbor.SelectionOnly) return;
		this.TrimCovered(spans, neighbor);
		this.TrimShadow(spans, eye, neighbor.Span(!this.AlongX), (neighbor.Y1, neighbor.Y2), neighbor.Span(this.AlongX));
	}

	public void TrimThroughout(List<(double Min, double Max)> spans, CollisionBox box, Vec3d start, Vec3d end) {
		if (box.SelectionOnly) return;
		this.TrimCovered(spans, box);
		this.TrimShadow(spans, start, box.Span(!this.AlongX), (box.Y1, box.Y2), box.Span(this.AlongX), end: end);
	}

	private readonly record struct Wall((double Min, double Max) X, (double Min, double Max) Y, (double Min, double Max) Z, bool Joined = false) : IComparable<Wall> {
		public Wall? Bridge(Wall other) {
			if (this.X.Min > other.X.Min) return other.Bridge(this);
			if (this.X.Min == other.X.Min || this.X.Max < other.X.Min || this.X.Max >= other.X.Max) return null;
			var y = (Min: Math.Max(this.Y.Min, other.Y.Min), Max: Math.Min(this.Y.Max, other.Y.Max));
			var z = (Min: Math.Max(this.Z.Min, other.Z.Min), Max: Math.Min(this.Z.Max, other.Z.Max));
			return y.Min < y.Max && z.Min < z.Max ? new Wall((this.X.Min, other.X.Max), y, z, true) : null;
		}

		public Wall Rotate() => new(this.Y, this.Z, this.X, this.Joined);

		public int CompareTo(Wall other) {
			int y = this.Y.CompareTo(other.Y);
			if (y != 0) return y;
			int z = this.Z.CompareTo(other.Z);
			return z != 0 ? z : this.X.CompareTo(other.X);
		}
	}

	public static List<Cuboidd> SolidWalls(List<CollisionBox> boxes) {
		var runs = new List<Wall>(boxes.Count);
		var bridges = new List<Wall>();
		CollisionBox? previous = null;
		foreach (var box in boxes) {
			if (box.SelectionOnly || box.Block is BlockSupportBeam) continue;
			var wall = new Wall((box.X1, box.X2), (box.Y1, box.Y2), (box.Z1, box.Z2));
			runs.Add(wall);
			if (previous is { } last && last.X == box.X && last.Y == box.Y && last.Z == box.Z) {
				var neighbor = new Wall((last.X1, last.X2), (last.Y1, last.Y2), (last.Z1, last.Z2));
				for (int axis = 0; axis < 3; axis++) {
					if (wall.Bridge(neighbor) is { } bridge) {
						for (int back = axis; back < 3; back++) bridge = bridge.Rotate();
						bridges.Add(bridge);
					}
					wall = wall.Rotate();
					neighbor = neighbor.Rotate();
				}
			}
			previous = box;
		}
		var walls = new List<Cuboidd>();
		Join(runs);
		if (bridges.Count != 0) Join(bridges);
		return walls;

		void Join(List<Wall> runs) {
			// join touching faces without covering the holes between them
			for (int axis = 0; axis < 3; axis++) {
				runs.Sort();
				int count = 0;
				for (int i = 0; i < runs.Count; i++) {
					var run = runs[i];
					if (count > 0) {
						var last = runs[count - 1];
						if (run.Y == last.Y && run.Z == last.Z && run.X.Min <= last.X.Max) {
							runs[count - 1] = last with { X = (last.X.Min, Math.Max(last.X.Max, run.X.Max)), Joined = true };
							continue;
						}
					}
					runs[count++] = run;
				}
				runs.RemoveRange(count, runs.Count - count);
				for (int i = 0; i < runs.Count; i++) {
					var run = runs[i];
					runs[i] = run.Rotate();
				}
			}

			foreach (var run in runs) {
				if (run.Joined) walls.Add(new Cuboidd(run.X.Min, run.Y.Min, run.Z.Min, run.X.Max, run.Y.Max, run.Z.Max));
			}
		}
	}

	public void TrimWall(List<(double Min, double Max)> spans, Cuboidd wall, Vec3d eye, Vec3d? end = null) {
		var x = (wall.X1, wall.X2);
		var z = (wall.Z1, wall.Z2);
		this.TrimShadow(spans, eye, this.AlongX ? z : x, (wall.Y1, wall.Y2), this.AlongX ? x : z, end: end);
	}

	public void TrimCovered(List<(double Min, double Max)> spans, CollisionBox neighbor) {
		if (this.CoveredBy(neighbor, out var covered)) BlockEdge.Subtract(spans, covered.Min, covered.Max);
	}

	public void TrimShadow(List<(double Min, double Max)> spans, Cuboidd box, Vec3d eye) {
		var x = (box.X1, box.X2);
		var z = (box.Z1, box.Z2);
		// give entity silhouettes room for the float coordinates sent over the wire
		this.TrimShadow(spans, eye, this.AlongX ? z : x, (box.Y1, box.Y2), this.AlongX ? x : z, BlockEdge.Clearance);
	}

	private void TrimShadow(List<(double Min, double Max)> spans, Vec3d eye,
		(double Min, double Max) across, (double Min, double Max) height, (double Min, double Max) along, double padding = 0, Vec3d? end = null) {
		if (spans.Count == 0 || !this.ProjectShadow(eye, across, height, along, out var shadow)) return;
		if (end != null) {
			// a convex blocker hides this overlap for the entire move
			if (!this.ProjectShadow(end, across, height, along, out var other)) return;
			shadow = (Math.Max(shadow.Min, other.Min), Math.Min(shadow.Max, other.Max));
			if (shadow.Min >= shadow.Max) return;
		}
		BlockEdge.Subtract(spans, shadow.Min - padding, shadow.Max + padding);
	}

	private bool ProjectShadow(Vec3d eye, (double Min, double Max) across, (double Min, double Max) height,
		(double Min, double Max) along, out (double Min, double Max) shadow) {
		shadow = default;
		double eyePlane = this.AlongX ? eye.Z : eye.X;
		double eyeAlong = this.AlongX ? eye.X : eye.Z;
		double near = 0, far = 1;
		if (!BlockEdge.Clip(eyePlane, this.SightPlane - eyePlane, across.Min, across.Max, ref near, ref far)) return false;
		if (!BlockEdge.Clip(eye.Y, this.SightHeight - eye.Y, height.Min, height.Max, ref near, ref far)) return false;

		near = Math.Max(near, double.Epsilon);
		double shadowMin = eyeAlong + (along.Min - eyeAlong) / (along.Min < eyeAlong ? near : far);
		double shadowMax = eyeAlong + (along.Max - eyeAlong) / (along.Max > eyeAlong ? near : far);
		shadow = (shadowMin, shadowMax);
		return true;
	}

	private static void Subtract(List<(double Min, double Max)> spans, double lo, double hi) {
		for (int i = spans.Count - 1; i >= 0; i--) {
			var (start, end) = spans[i];
			if (hi <= start || lo >= end) continue;
			if (lo > start) {
				spans[i] = (start, lo);
				if (hi < end) spans.Add((hi, end));
			} else if (hi < end) {
				spans[i] = (hi, end);
			} else {
				spans.RemoveAt(i);
			}
		}
	}

	private static bool Clip(double start, double delta, double lo, double hi, ref double near, ref double far) {
		if (delta == 0) return start >= lo && start <= hi;

		double a = (lo - start) / delta, b = (hi - start) / delta;
		near = Math.Max(near, Math.Min(a, b));
		far = Math.Min(far, Math.Max(a, b));
		return near < far;
	}
}
