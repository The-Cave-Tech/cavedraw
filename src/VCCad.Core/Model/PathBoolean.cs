using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>What a boolean operation did, so a person and a driver can see what happened.</summary>
public sealed record PathBooleanResult(string Operation, int Inputs, int Objects, int Contours)
{
    /// <summary>Whether the result was empty - a shape subtracted from itself covers nothing.</summary>
    public bool IsEmpty => Objects == 0;
}

/// <summary>Which of the boolean combinations to compute.</summary>
public enum BooleanOp
{
    /// <summary>Everything either path covers.</summary>
    Union,

    /// <summary>The first path, less everything the others cover.</summary>
    Subtract,

    /// <summary>Only what every path covers.</summary>
    Intersect,

    /// <summary>What an odd number of paths cover - the symmetric difference.</summary>
    Exclude,
}

/// <summary>
/// Boolean geometry over closed paths: union, subtract, intersect, exclude.
///
/// The method is *arrangement, classify, stitch*, and it is chosen for a reason worth stating.
///
/// 1. Every input is flattened to outlines and every edge of every outline is **split at every
///    intersection**, so what remains is a set of segments that only meet at their ends.
/// 2. Each distinct segment is **classified by the boolean predicate at its two sides** - the midpoint
///    nudged a hair to the left and to the right. A segment belongs to the result exactly when one side
///    is inside and the other is not, and it is kept pointing the way that puts the inside on its left.
/// 3. The kept segments are **stitched** into contours by walking the planar graph, always taking the
///    next edge clockwise, which traces each face with its interior on the left.
///
/// The reason to prefer this over clipping edges against each other directly: the classification asks
/// "is this point inside the inputs", and that question is answered by the inputs' own fill rules, which
/// are exact and already tested. Nothing in the result depends on arithmetic carried out on intersections
/// beyond their position, so a coincidence that would derail an edge-by-edge clipper - two shapes sharing
/// an edge, a shape subtracted from itself, three outlines through one point - is decided by the same
/// predicate as everything else, and comes out right rather than out of a special case.
///
/// Outlines that are identical are kept **once**, which is what makes congruent shapes work: a shared
/// edge is one segment, classified once, and therefore included once rather than twice or not at all.
/// </summary>
public static class PathBoolean
{
    /// <summary>How far off a segment the side samples sit, relative to the drawing's size.</summary>
    private const double SideEpsilonFraction = 1e-7;

    /// <summary>Coordinates closer together than this are the same point.</summary>
    private const double PointQuantum = 1e-7;

    /// <summary>
    /// Combines paths. Returns null when the result is empty - a shape subtracted from itself covers
    /// nothing, and saying so is better than leaving an invisible object behind.
    /// </summary>
    public static PathItem? Combine(IReadOnlyList<PathItem> paths, BooleanOp op)
    {
        if (paths.Count == 0)
        {
            return null;
        }

        if (paths.Count == 1)
        {
            // Nothing to combine with: the path is its own union and intersection, and subtracting
            // nothing from it leaves it.
            if (op is BooleanOp.Union)
            {
                return Copy(paths[0]);
            }

            return op is BooleanOp.Subtract ? Copy(paths[0]) : null;
        }

        var inputs = new List<(IReadOnlyList<FlattenedOutline> Outlines, FillRule Rule)>();
        foreach (PathItem path in paths)
        {
            inputs.Add((PathFlattener.Flatten(path), path.Fill.Rule));
        }

        bool FilledAt(int owner, Point2D point) => PathFlattener.IsFilled(inputs[owner].Outlines, inputs[owner].Rule, point);

        bool ResultAt(Point2D point)
        {
            switch (op)
            {
                case BooleanOp.Union:
                    for (int i = 0; i < inputs.Count; i++)
                    {
                        if (FilledAt(i, point))
                        {
                            return true;
                        }
                    }

                    return false;

                case BooleanOp.Intersect:
                    for (int i = 0; i < inputs.Count; i++)
                    {
                        if (!FilledAt(i, point))
                        {
                            return false;
                        }
                    }

                    return true;

                case BooleanOp.Exclude:
                    int count = 0;
                    for (int i = 0; i < inputs.Count; i++)
                    {
                        if (FilledAt(i, point))
                        {
                            count++;
                        }
                    }

                    return count % 2 == 1;

                default:
                    // Subtract: the FIRST path survives and the others are cut out of it. That is
                    // Illustrator's Minus Front - the back-most object is the one that remains - and it
                    // is why the inputs are taken in z-order, bottom first.
                    if (!FilledAt(0, point))
                    {
                        return false;
                    }

                    for (int i = 1; i < inputs.Count; i++)
                    {
                        if (FilledAt(i, point))
                        {
                            return false;
                        }
                    }

                    return true;
            }
        }

        List<Segment> edges = CollectEdges(inputs);
        Rect2D bounds = BoundsOf(edges);
        double epsilon = Math.Max(
            Math.Sqrt((bounds.Width * bounds.Width) + (bounds.Height * bounds.Height)) * SideEpsilonFraction,
            1e-9);

        List<Segment> split = Split(edges);
        List<Segment> unique = Distinct(split);

        var kept = new List<Segment>();
        foreach (Segment segment in unique)
        {
            Point2D middle = Midpoint(segment);
            var normal = Normal(segment);
            var left = new Point2D(middle.X + (normal.X * epsilon), middle.Y + (normal.Y * epsilon));
            var right = new Point2D(middle.X - (normal.X * epsilon), middle.Y - (normal.Y * epsilon));

            bool leftInside = ResultAt(left);
            bool rightInside = ResultAt(right);

            if (leftInside == rightInside)
            {
                // Both sides agree: the segment is not on the result's boundary.
                continue;
            }

            // Point it so the filled side is on the left, which is what the traversal assumes.
            kept.Add(leftInside ? segment : segment.Flipped());
        }

        List<List<Point2D>> contours = Stitch(kept);
        if (contours.Count == 0)
        {
            return null;
        }

        // The appearance comes from the object that survives the operation: for a subtract that is the
        // back-most one, because that is the object still there afterwards; for the rest Illustrator
        // takes the front-most, which is the one a person last applied a colour to.
        PathItem result = Copy(op == BooleanOp.Subtract ? paths[0] : paths[^1]);
        result.SubPaths.Clear();
        foreach (List<Point2D> contour in contours)
        {
            SubPath sub = result.AddSubPath(closed: true);
            foreach (Point2D point in contour)
            {
                sub.AppendNode(point);
            }
        }

        result.GeometryChanged();
        return result;
    }

    /// <summary>
    /// Divide: cut the paths into their separate regions, one object per region. A shape crossed by two
    /// lines becomes the pieces, which is what a person uses divide for - so unlike the other four this
    /// returns several objects rather than one.
    ///
    /// Each region is one **combination** of the inputs: the part inside a given subset of them and
    /// outside all the others, which is the boolean already built. With N inputs there are 2^N - 1
    /// combinations, so this is capped rather than allowed to explode.
    /// </summary>
    public static IReadOnlyList<PathItem> Divide(IReadOnlyList<PathItem> paths)
    {
        var regions = new List<PathItem>();
        if (paths.Count < 2)
        {
            return regions;
        }

        if (paths.Count > MaxDivideInputs)
        {
            throw new InvalidOperationException(
                $"Divide works on up to {MaxDivideInputs} paths at once; {paths.Count} would give " +
                $"{Math.Pow(2, paths.Count) - 1} combinations.");
        }

        int combinations = 1 << paths.Count;
        for (int mask = 1; mask < combinations; mask++)
        {
            var inside = new List<PathItem>();
            var outside = new List<PathItem>();

            for (int i = 0; i < paths.Count; i++)
            {
                ((mask & (1 << i)) != 0 ? inside : outside).Add(paths[i]);
            }

            PathItem? region = inside.Count == 1 ? Copy(inside[0]) : Combine(inside, BooleanOp.Intersect);
            if (region is null)
            {
                continue;
            }

            if (outside.Count > 0)
            {
                var subtract = new List<PathItem> { region };
                subtract.AddRange(outside);
                region = Combine(subtract, BooleanOp.Subtract);
            }

            if (region is not null && region.SubPaths.Count > 0)
            {
                regions.Add(region);
            }
        }

        return regions;
    }

    /// <summary>How many paths divide takes: 2^N - 1 regions, so eight is already 255.</summary>
    public const int MaxDivideInputs = 8;

    private static List<Segment> CollectEdges(List<(IReadOnlyList<FlattenedOutline> Outlines, FillRule Rule)> inputs)
    {
        var edges = new List<Segment>();
        for (int owner = 0; owner < inputs.Count; owner++)
        {
            foreach (FlattenedOutline outline in inputs[owner].Outlines)
            {
                int count = outline.Points.Count;
                for (int i = 0; i < count; i++)
                {
                    Point2D a = outline.Points[i];
                    Point2D b = outline.Points[(i + 1) % count];
                    if (!a.NearlyEquals(b, PointQuantum))
                    {
                        edges.Add(new Segment(a, b, owner));
                    }
                }
            }
        }

        return edges;
    }

    private static Rect2D BoundsOf(List<Segment> edges)
    {
        if (edges.Count == 0)
        {
            return new Rect2D(0, 0, 0, 0);
        }

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (Segment edge in edges)
        {
            minX = Math.Min(minX, Math.Min(edge.A.X, edge.B.X));
            minY = Math.Min(minY, Math.Min(edge.A.Y, edge.B.Y));
            maxX = Math.Max(maxX, Math.Max(edge.A.X, edge.B.X));
            maxY = Math.Max(maxY, Math.Max(edge.A.Y, edge.B.Y));
        }

        return new Rect2D(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>
    /// Splits every edge wherever another edge crosses or touches it, so that afterwards segments meet
    /// only at their endpoints. Collinear overlapping edges are split at each other's endpoints too,
    /// which is the case a shared border produces.
    /// </summary>
    private static List<Segment> Split(List<Segment> edges)
    {
        var cuts = new List<List<double>>(edges.Count);
        for (int i = 0; i < edges.Count; i++)
        {
            cuts.Add(new List<double> { 0.0, 1.0 });
        }

        for (int i = 0; i < edges.Count; i++)
        {
            for (int j = i + 1; j < edges.Count; j++)
            {
                AddCuts(edges[i], edges[j], cuts[i]);
                AddCuts(edges[j], edges[i], cuts[j]);
            }
        }

        var result = new List<Segment>();
        for (int i = 0; i < edges.Count; i++)
        {
            List<double> sorted = cuts[i].Distinct().OrderBy(t => t).ToList();
            for (int k = 0; k + 1 < sorted.Count; k++)
            {
                double t0 = sorted[k];
                double t1 = sorted[k + 1];
                if (t1 - t0 <= 1e-12)
                {
                    continue;
                }

                Point2D a = Along(edges[i], t0);
                Point2D b = Along(edges[i], t1);
                if (Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2)) > PointQuantum)
                {
                    result.Add(new Segment(a, b, edges[i].Owner));
                }
            }
        }

        return result;
    }

    /// <summary>Where <paramref name="other"/> cuts <paramref name="edge"/>, as parameters along it.</summary>
    private static void AddCuts(Segment edge, Segment other, List<double> into)
    {
        var d1 = new Vector2D(edge.B.X - edge.A.X, edge.B.Y - edge.A.Y);
        var d2 = new Vector2D(other.B.X - other.A.X, other.B.Y - other.A.Y);

        double denominator = Cross(d1, d2);
        var offset = new Vector2D(other.A.X - edge.A.X, other.A.Y - edge.A.Y);

        if (Math.Abs(denominator) > 1e-12)
        {
            // They meet at exactly one point, if that point is within both.
            double t = Cross(offset, d2) / denominator;
            double u = Cross(offset, d1) / denominator;
            if (t > 1e-9 && t < 1 - 1e-9 && u > -1e-9 && u < 1 + 1e-9)
            {
                into.Add(t);
            }

            return;
        }

        // Parallel. If they are also collinear, each one's ends may lie within the other.
        if (Math.Abs(Cross(offset, d1)) > 1e-9 * Math.Max(1.0, Length(d1) * Length(d1)))
        {
            return;
        }

        double lengthSquared = (d1.X * d1.X) + (d1.Y * d1.Y);
        if (lengthSquared < 1e-18)
        {
            return;
        }

        foreach (Point2D point in new[] { other.A, other.B })
        {
            double t = (((point.X - edge.A.X) * d1.X) + ((point.Y - edge.A.Y) * d1.Y)) / lengthSquared;
            if (t > 1e-9 && t < 1 - 1e-9)
            {
                into.Add(t);
            }
        }
    }

    /// <summary>
    /// One segment per distinct piece of geometry. A border two shapes share is one segment, not two -
    /// so it is classified once, and a result can neither double-count it nor miss it.
    /// </summary>
    private static List<Segment> Distinct(List<Segment> segments)
    {
        var seen = new HashSet<(long, long, long, long)>();
        var result = new List<Segment>();

        foreach (Segment segment in segments)
        {
            (long ax, long ay) = Quantise(segment.A);
            (long bx, long by) = Quantise(segment.B);
            bool flipped = (ax, ay).CompareTo((bx, by)) > 0;

            var key = flipped ? (bx, by, ax, ay) : (ax, ay, bx, by);
            if (seen.Add(key))
            {
                result.Add(segment);
            }
        }

        return result;
    }

    private static (long, long) Quantise(Point2D point)
        => ((long)Math.Round(point.X / PointQuantum), (long)Math.Round(point.Y / PointQuantum));

    /// <summary>
    /// Walks the kept segments into contours: from each unused edge, repeatedly take the next edge
    /// clockwise at the vertex reached. Always turning as far clockwise as possible is what traces a
    /// face with its interior on the left, so a hole comes out wound against its container by itself.
    /// </summary>
    private static List<List<Point2D>> Stitch(List<Segment> kept)
    {
        var contours = new List<List<Point2D>>();
        if (kept.Count == 0)
        {
            return contours;
        }

        // Outgoing edges by quantised start point.
        var outgoing = new Dictionary<(long, long), List<int>>();
        for (int i = 0; i < kept.Count; i++)
        {
            (long x, long y) = Quantise(kept[i].A);
            if (!outgoing.TryGetValue((x, y), out List<int>? list))
            {
                list = new List<int>();
                outgoing[(x, y)] = list;
            }

            list.Add(i);
        }

        var used = new bool[kept.Count];

        for (int start = 0; start < kept.Count; start++)
        {
            if (used[start])
            {
                continue;
            }

            var contour = new List<Point2D>();
            int current = start;

            while (true)
            {
                used[current] = true;
                contour.Add(kept[current].A);

                (long x, long y) = Quantise(kept[current].B);
                int next = ChooseNext(kept, used, outgoing, current, (x, y));
                if (next < 0)
                {
                    break;
                }

                current = next;
            }

            if (contour.Count >= 3)
            {
                contours.Add(contour);
            }
        }

        return contours;
    }

    /// <summary>
    /// The next edge at a vertex: the first one met turning clockwise from the way we came in. That is
    /// the edge bounding the same face.
    /// </summary>
    private static int ChooseNext(
        List<Segment> kept,
        bool[] used,
        Dictionary<(long, long), List<int>> outgoing,
        int current,
        (long, long) vertex)
    {
        if (!outgoing.TryGetValue(vertex, out List<int>? candidates))
        {
            return -1;
        }

        var incoming = new Vector2D(kept[current].B.X - kept[current].A.X, kept[current].B.Y - kept[current].A.Y);
        double incomingAngle = Math.Atan2(incoming.Y, incoming.X);

        int best = -1;
        double bestTurn = double.MaxValue;

        foreach (int candidate in candidates)
        {
            if (used[candidate])
            {
                continue;
            }

            var direction = new Vector2D(
                kept[candidate].B.X - kept[candidate].A.X, kept[candidate].B.Y - kept[candidate].A.Y);

            // How far clockwise from the reversed incoming direction: 0 means straight back the way we
            // came, which is the first thing a clockwise sweep finds.
            double turn = (incomingAngle + Math.PI) - Math.Atan2(direction.Y, direction.X);
            turn = ((turn % (2 * Math.PI)) + (2 * Math.PI)) % (2 * Math.PI);

            if (turn < bestTurn)
            {
                bestTurn = turn;
                best = candidate;
            }
        }

        return best;
    }

    private static Point2D Along(Segment segment, double t)
        => new(segment.A.X + ((segment.B.X - segment.A.X) * t), segment.A.Y + ((segment.B.Y - segment.A.Y) * t));

    private static Point2D Midpoint(Segment segment)
        => new((segment.A.X + segment.B.X) / 2, (segment.A.Y + segment.B.Y) / 2);

    private static Vector2D Normal(Segment segment)
    {
        var direction = new Vector2D(segment.B.X - segment.A.X, segment.B.Y - segment.A.Y);
        double length = Math.Sqrt((direction.X * direction.X) + (direction.Y * direction.Y));
        return length < 1e-12
            ? new Vector2D(0, 0)
            : new Vector2D(-direction.Y / length, direction.X / length);
    }

    private static double Length(Vector2D v) => Math.Sqrt((v.X * v.X) + (v.Y * v.Y));

    private static double Cross(Vector2D a, Vector2D b) => (a.X * b.Y) - (a.Y * b.X);

    /// <summary>
    /// A deep copy: the source's appearance **and** its geometry. Both are wanted - the geometry for a
    /// one-path region, and the appearance for a result built from contours.
    /// </summary>
    private static PathItem Copy(PathItem source)
    {
        PathItem copy = new()
        {
            Name = source.Name,
            Fill = source.Fill,
            Stroke = source.Stroke,
            Opacity = source.Opacity,
            Shape = source.Shape,
        };

        foreach (SubPath sub in source.SubPaths)
        {
            SubPath target = copy.AddSubPath(sub.IsClosed);
            foreach (PathNode node in sub.Nodes)
            {
                target.Nodes.Add(new PathNode(node.Anchor, node.InHandle, node.OutHandle));
            }
        }

        copy.GeometryChanged();
        return copy;
    }

    private readonly record struct Segment(Point2D A, Point2D B, int Owner)
    {
        public Segment Flipped() => new(B, A, Owner);
    }
}
