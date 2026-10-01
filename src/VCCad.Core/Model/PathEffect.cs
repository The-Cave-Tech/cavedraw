namespace VCCad.Core.Model;

/// <summary>
/// One live path effect, exactly as the file that carries it described it.
///
/// Inkscape does not draw the path an element writes when it has a live path effect on it. It draws the effect's
/// output, and it keeps the path it applied the effect to in <c>inkscape:original-d</c> beside the effect's own
/// element. A reader that only took the drawn geometry would reproduce the picture for the file it was given and
/// lose the reason it looked like that, and the next round trip would write a file with no effect on it at all.
///
/// So this holds the **description**, not a translation of it: the effect's name, its id and every parameter the
/// element carried, keyed and spelled as the file spelled them. <see cref="Effect"/>, <see cref="Id"/> and
/// <see cref="Version"/> are those three attributes read out by name; <see cref="Parameters"/> is everything else,
/// so the element can be written back with each attribute exactly once. Two of the parameters are read into the
/// model proper (<see cref="PathEffects.Translate"/> turns a powerstroke into a <see cref="WidthProfileSpec"/>),
/// and the rest are carried - an effect is somebody's drawing decision, and a parameter this build has no meaning
/// for is not this build's to drop.
/// </summary>
public sealed record PathEffectSpec
{
    public PathEffectSpec(
        string effect,
        string id,
        string version,
        IEnumerable<KeyValuePair<string, string>> parameters)
    {
        Effect = effect;
        Id = id;
        Version = version;

        // Held in the order the element listed them, because the file's own order is what an export writes back.
        // A dictionary would be enough for lookup and would re-order a diff of the round trip for no reason.
        Parameters = parameters.ToArray();
    }

    /// <summary>The effect's own name - <c>powerstroke</c>, <c>bend_path</c>, <c>bspline</c> and so on.</summary>
    public string Effect { get; init; }

    /// <summary>The id the path refers to this effect by.</summary>
    public string Id { get; init; }

    /// <summary>The <c>lpeversion</c> the file was written with, which is how Inkscape reads its own old files.</summary>
    public string Version { get; init; }

    /// <summary>Every other parameter the element carried, verbatim and in the file's order.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Parameters { get; init; }    /// <summary>One parameter's text, or null when the element did not carry it.</summary>
    public string? Parameter(string name)
    {
        foreach (KeyValuePair<string, string> parameter in Parameters)
        {
            if (string.Equals(parameter.Key, name, StringComparison.Ordinal))
            {
                return parameter.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// One parameter as a number, or the fallback when it is absent or is not one.
    ///
    /// Invariant culture, because a locale that writes a decimal comma would otherwise read <c>miter_limit="4"</c>
    /// correctly and <c>scale_width="1.5"</c> as fifteen - a document that draws one way on the machine that made
    /// it and another way on the machine that opened it.
    /// </summary>
    public double Number(string name, double fallback = 0.0)
        => double.TryParse(
            Parameter(name),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out double value)
            ? value
            : fallback;

    public bool Equals(PathEffectSpec? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (Effect != other.Effect || Id != other.Id || Version != other.Version ||
            Parameters.Count != other.Parameters.Count)
        {
            return false;
        }

        for (int i = 0; i < Parameters.Count; i++)
        {
            if (Parameters[i].Key != other.Parameters[i].Key || Parameters[i].Value != other.Parameters[i].Value)
            {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Effect);
        hash.Add(Id);
        hash.Add(Version);
        foreach (KeyValuePair<string, string> parameter in Parameters)
        {
            hash.Add(parameter.Key);
            hash.Add(parameter.Value);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// What a live path effect becomes in this model: a stroke to draw, or a refusal that names the effect.
///
/// There is no third answer on purpose. An effect this build does not implement must not come out looking like a
/// path with nothing on it, because that is a plausible picture of the wrong thing - the file's own geometry is
/// what the path already holds, and leaving it alone is honest while re-drawing it as an un-effected path is not.
/// </summary>
public sealed record PathEffectTranslation(
    PathEffectSpec Effect,
    StrokeSpec? Stroke,
    string? Refusal,
    IReadOnlyList<string> Notes)
{
    /// <summary>Whether the effect was translated and there is a stroke to draw.</summary>
    public bool IsSupported => Refusal is null && Stroke is not null;

    /// <summary>A refusal that names the effect, which is what a report has to say.</summary>
    public static PathEffectTranslation Unsupported(PathEffectSpec effect, string reason)
        => new(effect, null, reason, Array.Empty<string>());
}

/// <summary>
/// The live path effects this build understands, and the translation of one into the model's own stroke.
///
/// **A translation, not a renderer.** Inkscape's powerstroke and this repository's width profile are the same
/// idea - a stroke whose width varies along the path - written down in two parameterisations, so the work is to
/// read one into the other and let the outline machinery that already exists draw it. That is what keeps the
/// canvas and the PDF agreeing: there is still one answer to "what shape does this stroke cover".
///
/// The translation is a **pure function** of the effect and the path. It reads nothing from the document and
/// writes nothing to it, so calling it twice gives the same stroke and a renderer cannot apply the effect again
/// on the next frame.
/// </summary>
public static class PathEffects
{
    /// <summary>The one effect name this build implements.</summary>
    public const string PowerStroke = "powerstroke";

    /// <summary>Every effect name this build implements, for a report to compare a file's effect against.</summary>
    public static IReadOnlyList<string> Implemented { get; } = new[] { PowerStroke };

    /// <summary>Whether this build has a translation for an effect name, compared the way the file spells it.</summary>
    public static bool IsImplemented(string effect)
        => Implemented.Any(name => string.Equals(name, effect, StringComparison.Ordinal));

    /// <summary>
    /// The effect a path says it has, as the reference the file wrote on it.
    ///
    /// The reference is an id - <c>inkscape:path-effect="#path-effect128881-2"</c> - and it is held with the
    /// element's other foreign attributes, which is what carries it through a save and a re-open without the model
    /// having to invent a place for it.
    /// </summary>
    public static string? ReferenceOn(LayerItem item)
    {
        if (!item.ForeignAttributes.TryGetValue("inkscape:path-effect", out string? reference))
        {
            return null;
        }

        string id = reference.Trim().TrimStart('#');
        return id.Length == 0 ? null : id;
    }

    /// <summary>
    /// The path the file applied its effect to, as the file kept it beside the effect's output.
    ///
    /// Inkscape writes the effect's **result** in <c>d</c> and the path it was applied to in
    /// <c>inkscape:original-d</c>. The result is what a viewer that cannot run the effect draws, and the original
    /// is what the effect was run on - so an effect this build understands is applied to the original, and the
    /// result is the fallback for one it does not. Returned as the data string; reading it into geometry is the
    /// SVG path parser's job, not the model's.
    /// </summary>
    public static string? SourcePathData(LayerItem item)
        => item.ForeignAttributes.TryGetValue("inkscape:original-d", out string? data) && data.Trim().Length > 0
            ? data
            : null;

    /// <summary>
    /// The stroke to draw for one live path effect, or a refusal that names it.
    ///
    /// <paramref name="stroke"/> is what the file otherwise says the path is stroked with; its width profile and
    /// its cap and join are replaced by the effect's own, because the effect is what decides the shape. Its
    /// colour, dash and opacity are left alone: a powerstroke says how wide the line is, not what colour it is.
    /// </summary>
    public static PathEffectTranslation Translate(PathEffectSpec effect, PathItem path, StrokeSpec stroke)
    {
        if (!IsImplemented(effect.Effect))
        {
            return PathEffectTranslation.Unsupported(
                effect,
                $"'{effect.Effect}' is a live path effect this build does not implement, so '" +
                $"{path.Name}' keeps the geometry the file drew and is not redrawn without it. " +
                $"Implemented: {string.Join(", ", Implemented)}.");
        }

        int curves = CurveCount(path);
        if (curves <= 0)
        {
            return PathEffectTranslation.Unsupported(
                effect, $"'{effect.Effect}' is parameterised along a path, and this path has no segments");
        }

        (WidthProfileSpec? profile, string? refusal) = PowerStrokeProfile(effect, path, curves);
        if (profile is null)
        {
            return PathEffectTranslation.Unsupported(effect, refusal ?? $"'{effect.Effect}' could not be read");
        }

        var notes = new List<string>();
        string? interpolator = effect.Parameter("interpolator_type");
        if (interpolator is { Length: > 0 } && !string.Equals(interpolator, "Linear", StringComparison.Ordinal))
        {
            // The model's smooth interpolation is one easing, and Inkscape offers five curves through the same
            // points. The widths **at the points** are exact either way and only the run between two of them
            // differs, so this is a substitution to report rather than a reason to refuse - the same shape as a
            // font that cannot be supplied being named rather than quietly replaced.
            notes.Add(
                $"the file interpolates between width points with '{interpolator}', which this build draws as a " +
                "smooth ease; the widths at the points are the file's own");
        }

        StrokeSpec translated = stroke with
        {
            WidthProfile = profile,
            Cap = Cap(effect.Parameter("start_linecap_type"), stroke.Cap, notes, "start"),
            Join = Join(effect.Parameter("linejoin_type"), stroke.Join, notes),
            MiterLimit = effect.Parameter("miter_limit") is null
                ? stroke.MiterLimit
                : Math.Max(1.0, effect.Number("miter_limit", stroke.MiterLimit)),
        };

        // The end cap is the stroke's own and the start cap was just set, so a file that asks for two different
        // ones gets the end's - the model has one cap per stroke, and saying so is better than drawing a shape the
        // file did not ask for. The note above has already named the substitution.
        translated = translated with
        {
            Cap = Cap(effect.Parameter("end_linecap_type"), translated.Cap, notes, "end"),
        };

        return new PathEffectTranslation(effect, translated, null, notes);
    }

    /// <summary>
    /// The powerstroke's offset points, read as a width profile.
    ///
    /// **The parameterisation is the file's, and it is not arc length.** Inkscape stores each offset point as
    /// <c>curve_index + t</c> over the whole path - the integer part names the segment the knot sits on - and
    /// scales that axis to the path's length before interpolating, which is the same thing as interpolating over
    /// the fraction of the way through the path. So a point at <c>3.5</c> of an eight-segment path is at position
    /// <c>3.5 / 8</c>, which is what the profile wants.
    ///
    /// **Only one subpath can be expressed.** A <see cref="WidthProfileSpec"/> is read per subpath, from 0 at its
    /// start to 1 at its end, so a file whose knots are spread over several subpaths is asking for a different
    /// profile on each of them - which is a shape this model has no way to hold. Rather than draw every subpath
    /// with the first one's profile, that case is refused and named.
    /// </summary>
    public static (WidthProfileSpec? Profile, string? Refusal) PowerStrokeProfile(
        PathEffectSpec effect, PathItem path, int curves)
    {
        string? text = effect.Parameter("offset_points");
        if (text is null || text.Trim().Length == 0)
        {
            return (null, "'powerstroke' carries no offset points, so there is no width to build");
        }

        var points = new List<(double Position, double Offset)>();
        foreach ((double along, double offset) in OffsetPoints(text))
        {
            if (along < 0.0 || along > curves)
            {
                // A knot outside the path's own parameter range belongs to another subpath, or to a path the file
                // has since changed. Either way this profile cannot place it.
                return (null,
                    $"a 'powerstroke' knot sits at {along:0.####} of {curves} segments, which is off the path");
            }

            points.Add((along, offset));
        }

        if (points.Count == 0)
        {
            return (null, "'powerstroke' carries no offset points this build can read");
        }

        if (path.SubPaths.Count > 1)
        {
            // The refusal above cannot catch this on its own: knots inside the first subpath's range are perfectly
            // placeable, and the second subpath would silently be drawn with them.
            return (null,
                "a 'powerstroke' on a path of several subpaths needs one width profile per subpath, which the " +
                "model's single profile per stroke cannot hold");
        }

        double scale = effect.Number("scale_width", 1.0);
        bool smooth = !string.Equals(effect.Parameter("interpolator_type"), "Linear", StringComparison.Ordinal);
        WidthInterpolation interpolation = smooth ? WidthInterpolation.Cubic : WidthInterpolation.Linear;

        var profile = new List<WidthPoint>(points.Count);
        foreach ((double along, double offset) in points)
        {
            // The stored value is the **offset of the knot from the centreline**, so the drawn width is twice it:
            // Inkscape builds the outline as the path moved out by the offset, and the path moved in by it, and
            // fills between them.
            double width = Math.Max(0.0, 2.0 * Math.Abs(offset) * scale);
            profile.Add(new WidthPoint(along / curves, width, width, interpolation));
        }

        // An effect with no name of its own takes a name from the effect, which is what the stroke will refer to
        // and what a person will see in the profile list.
        string name = effect.Id.Length > 0 ? effect.Id : "Power stroke";
        return (new WidthProfileSpec(name, profile), null);
    }

    /// <summary>
    /// The pairs Inkscape writes into <c>offset_points</c>: <c>t,offset</c> entries separated by a bar.
    ///
    /// A file with a single point has no bar at all, so a chunk with no bar is read as one point rather than as
    /// nothing - which is what a naive split on the separator would produce, quietly turning a constant-width
    /// powerstroke into a refusal.
    /// </summary>
    private static IEnumerable<(double Along, double Offset)> OffsetPoints(string text)
    {
        foreach (string chunk in text.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] numbers = chunk.Split(
                new[] { ',', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i + 1 < numbers.Length; i += 2)
            {
                if (Number(numbers[i], out double along) && Number(numbers[i + 1], out double offset))
                {
                    yield return (along, offset);
                }
            }
        }
    }

    private static bool Number(string text, out double value)
        => double.TryParse(
            text,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out value);

    /// <summary>
    /// How many segments the path has, which is the axis the effect's knots are stored on.
    ///
    /// A closed subpath has one more segment than it has gaps between nodes, because its last node joins the first.
    /// Counting nodes instead would place every knot on a closed path one segment too far along, and the profile
    /// would be skewed rather than wrong-looking - which is exactly the kind of error that survives a review.
    /// </summary>
    public static int CurveCount(PathItem path)
    {
        int total = 0;
        foreach (SubPath sub in path.SubPaths)
        {
            if (sub.Nodes.Count < 2)
            {
                continue;
            }

            total += sub.IsClosed ? sub.Nodes.Count : sub.Nodes.Count - 1;
        }

        return total;
    }

    private static StrokeCap Cap(string? name, StrokeCap fallback, List<string> notes, string which)
        => name switch
        {
            "zerowidth" or "butt" => StrokeCap.Butt,
            "square" => StrokeCap.Square,
            "round" => StrokeCap.Round,
            "peak" => Peak(fallback, notes, which),
            _ => fallback,
        };

    private static StrokeCap Peak(StrokeCap fallback, List<string> notes, string which)
    {
        // A peak cap runs the two edges out to a point past the end. Nothing in this model draws one, and a butt
        // end is the closest that does not invent a spike the file's own parameters would have to size.
        notes.Add($"the file asks for a 'peak' {which} cap, which this build draws as a butt end");
        return fallback;
    }

    private static StrokeJoin Join(string? name, StrokeJoin fallback, List<string> notes)
        => name switch
        {
            "bevel" => StrokeJoin.Bevel,
            "round" => StrokeJoin.Round,
            "miter" => StrokeJoin.Miter,

            // An extrapolated arc is a mitre whose outer corner is filleted at the join's radius. This model's
            // mitre is the same shape without the fillet, and its inner corner - which is what the join case in
            // the corpus is about - is identical either way.
            "extrp_arc" => StrokeJoin.Miter,

            "spiro" => Spiro(fallback, notes),
            _ => fallback,
        };

    private static StrokeJoin Spiro(StrokeJoin fallback, List<string> notes)
    {
        notes.Add("the file asks for a 'spiro' join, which this build draws as a round one");
        return StrokeJoin.Round;
    }
}
