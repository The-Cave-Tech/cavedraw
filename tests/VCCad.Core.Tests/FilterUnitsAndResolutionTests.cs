using System.Xml.Linq;
using VCCad.Core.Model;
using VCCad.Core.Raster;
using VCCad.Core.Serialization;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;
using Xunit.Abstractions;

namespace VCCad.Core.Tests;

/// <summary>
/// `primitiveUnits` and `filterRes`: what a step's own numbers **mean**, and the resolution the filter is sampled
/// at.
///
/// Both are the region question the issue names - "why a blur near an edge either grows into the margin or is
/// clipped off" - and both are invisible in a byte comparison: a model that carried the two and an engine that
/// ignored them write the same file and draw different pictures. So every test here asserts the model
/// **and** the pixels, and the numbers each one measures are printed so a reader can see how far apart the two
/// drawings are rather than taking "different" on trust.
/// </summary>
public class FilterUnitsAndResolutionTests
{
    private readonly ITestOutputHelper _output;

    public FilterUnitsAndResolutionTests(ITestOutputHelper output) => _output = output;

    private const string Head =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"200\" viewBox=\"0 0 200 200\">";

    private static SvgImportResult Read(string body) => SvgReader.Read(Head + body + "</svg>");

    private static CadDocument Document(FilterSpec filter)
    {
        CadDocument document = CadDocument.CreateDefault();
        document.AddFilter(filter);
        return document;
    }

    private static FilterSpec RoundTrip(FilterSpec filter)
        => SvgReader.Read(SvgWriter.Write(Document(filter))).Document.FindFilter(filter.Name)!;

    /// <summary>
    /// A block in the middle of the box, sized with it so that the same filter can be asked for the same relative
    /// picture at two sizes.
    /// </summary>
    private static FilterBuffer Block(int size = 40)
    {
        var buffer = new FilterBuffer(size, size);
        for (int y = size / 4; y < size - (size / 4); y++)
        {
            for (int x = size / 4; x < size - (size / 4); x++)
            {
                buffer.Set(x, y, new ColorRgb(1, 0, 0), 1f);
            }
        }

        return buffer;
    }

    private static readonly Rect2D Bounds = new(0, 0, 40, 40);

    // ---------------------------------------------------------------- the model

    /// <summary>
    /// **The two declarations are part of the filter's identity**, so a model that differs only in one of them is a
    /// different filter.
    ///
    /// This is the test that bites when the members are added to the record but not to its own `Equals`: a round
    /// trip would report "the same filter" for a file whose region means one thing and a file whose region means
    /// another, and every other assertion here would pass on a model that had quietly lost them.
    /// </summary>
    [Fact]
    public void TheTwoDeclarationsArePartOfTheFiltersIdentity()
    {
        FilterPrimitive[] primitives = { FilterPrimitive.Blur(2.0, "SourceGraphic") };
        var user = new FilterSpec("f", primitives);
        var boxed = new FilterSpec("f", primitives) { PrimitiveUnitsObjectBoundingBox = true };
        var resolved = new FilterSpec("f", primitives) { FilterResolutionX = 16, FilterResolutionY = 16 };

        Assert.NotEqual(user, boxed);
        Assert.NotEqual(user, resolved);
        Assert.NotEqual(boxed, resolved);

        // And the defaults are SVG's own: user-space primitive lengths, and no resolution of its own.
        Assert.False(user.PrimitiveUnitsObjectBoundingBox);
        Assert.Null(user.FilterResolutionX);
        Assert.False(user.HasFilterResolution);
    }

    /// <summary>
    /// **Both survive a round trip through SVG**, and they are written as the two attributes they came from -
    /// separately, because they are two different questions about the region.
    /// </summary>
    [Fact]
    public void PrimitiveUnitsAndFilterResSurviveTheRoundTrip()
    {
        var original = new FilterSpec("boxed", new[] { FilterPrimitive.Blur(0.05, "SourceAlpha") })
        {
            PrimitiveUnitsObjectBoundingBox = true,
            FilterResolutionX = 64,
            FilterResolutionY = 32,
        };

        XElement written = XDocument.Parse(SvgWriter.Write(Document(original)))
            .Descendants()
            .First(element => element.Name.LocalName == "filter");

        Assert.Equal("objectBoundingBox", (string?)written.Attribute("primitiveUnits"));
        Assert.Equal("64 32", (string?)written.Attribute("filterRes"));

        FilterSpec back = RoundTrip(original);

        Assert.Equal(original, back);
        Assert.True(back.PrimitiveUnitsObjectBoundingBox);
        Assert.Equal(64, back.FilterResolutionX);
        Assert.Equal(32, back.FilterResolutionY);
        Assert.True(back.HasFilterResolution);
    }

    /// <summary>One number is both axes, which is the format's own shorthand and is written back as one number.</summary>
    [Fact]
    public void AOneValueFilterResIsWrittenAsOneNumberAndReadBackToBothAxes()
    {
        var original = new FilterSpec("square", new[] { FilterPrimitive.Blur(1.0) })
        {
            FilterResolutionX = 48,
            FilterResolutionY = 48,
        };

        XElement written = XDocument.Parse(SvgWriter.Write(Document(original)))
            .Descendants()
            .First(element => element.Name.LocalName == "filter");

        Assert.Equal("48", (string?)written.Attribute("filterRes"));

        FilterSpec back = RoundTrip(original);
        Assert.Equal(48, back.FilterResolutionX);
        Assert.Equal(48, back.FilterResolutionY);
        Assert.Equal(original, back);
    }

    /// <summary>An ordinary filter is not buried in attributes that say what a viewer already assumes.</summary>
    [Fact]
    public void ADefaultFilterWritesNeitherAttribute()
    {
        var plain = new FilterSpec("plain", new[] { FilterPrimitive.Blur(2.0) });

        XElement written = XDocument.Parse(SvgWriter.Write(Document(plain)))
            .Descendants()
            .First(element => element.Name.LocalName == "filter");

        Assert.Null(written.Attribute("primitiveUnits"));
        Assert.Null(written.Attribute("filterRes"));
        Assert.Null(written.Attribute("filterUnits"));
    }

    /// <summary>
    /// **A word this build does not know is refused, not guessed at**, and the default is what applies: an
    /// unrecognised `primitiveUnits` that silently became one of the two would size every blur and every offset in
    /// the filter by the wrong unit.
    /// </summary>
    [Fact]
    public void AnUnknownPrimitiveUnitsWordIsReportedAndReadAsTheDefault()
    {
        SvgImportResult result = Read(
            "<defs><filter id=\"f\" primitiveUnits=\"shapeSpace\"><feGaussianBlur stdDeviation=\"2\"/></filter></defs>" +
            "<rect width=\"10\" height=\"10\" filter=\"url(#f)\"/>");

        Assert.Contains(result.Warnings, warning =>
            warning.Contains("primitiveUnits", StringComparison.Ordinal) &&
            warning.Contains("shapeSpace", StringComparison.Ordinal));

        Assert.False(result.Document.FindFilter("f")!.PrimitiveUnitsObjectBoundingBox);
    }

    /// <summary>
    /// **A resolution this build will not allocate is reported and left unset**, rather than stored and ignored.
    ///
    /// A `filterRes` is a request to evaluate the region at that many pixels across, so a number beyond what this
    /// build allocates is a filter that would either be drawn at some other resolution with nothing to say so, or
    /// allocate until the process dies. The file is told which of its numbers was refused.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-16")]
    [InlineData("1e9")]
    [InlineData("12 34 56")]
    [InlineData("wide")]
    public void AFilterResolutionThisBuildWillNotAllocateIsReportedAndNotStored(string value)
    {
        SvgImportResult result = Read(
            $"<defs><filter id=\"f\" filterRes=\"{value}\"><feGaussianBlur stdDeviation=\"2\"/></filter></defs>" +
            "<rect width=\"10\" height=\"10\" filter=\"url(#f)\"/>");

        Assert.Contains(result.Warnings, warning =>
            warning.Contains("filterRes", StringComparison.Ordinal) &&
            warning.Contains(value, StringComparison.Ordinal));

        FilterSpec filter = result.Document.FindFilter("f")!;
        Assert.Null(filter.FilterResolutionX);
        Assert.Null(filter.FilterResolutionY);
        Assert.False(filter.HasFilterResolution);
    }

    /// <summary>
    /// A percentage in a **step's** length has nothing to resolve against and reads as zero, which is said rather
    /// than left to look like a length the file asked for.
    /// </summary>
    [Fact]
    public void APercentagePrimitiveLengthIsReported()
    {
        SvgImportResult result = Read(
            "<defs><filter id=\"f\"><feGaussianBlur stdDeviation=\"5%\"/></filter></defs>" +
            "<rect width=\"10\" height=\"10\" filter=\"url(#f)\"/>");

        Assert.Contains(result.Warnings, warning =>
            warning.Contains("stdDeviation", StringComparison.Ordinal) &&
            warning.Contains("percentage", StringComparison.Ordinal));
    }

    /// <summary>
    /// A height is not a distance, so bounding-box primitive units do not scale `surfaceScale` - and a file that
    /// asked for them is told that rather than having its bevel quietly drawn at another height.
    /// </summary>
    [Fact]
    public void ABoundingBoxSurfaceScaleIsReportedAsNotScaled()
    {
        SvgImportResult result = Read(
            "<defs><filter id=\"f\" primitiveUnits=\"objectBoundingBox\">" +
            "<feSpecularLighting surfaceScale=\"3\"><feDistantLight azimuth=\"40\" elevation=\"50\"/>" +
            "</feSpecularLighting></filter></defs>" +
            "<rect width=\"10\" height=\"10\" filter=\"url(#f)\"/>");

        Assert.Contains(result.Warnings, warning =>
            warning.Contains("surfaceScale", StringComparison.Ordinal) &&
            warning.Contains("height", StringComparison.Ordinal));
    }

    /// <summary>The sidecar carries both, and a document that does not use them is byte-identical to before.</summary>
    [Fact]
    public void TheTwoDeclarationsSurviveTheSidecar()
    {
        CadDocument document = Document(new FilterSpec("boxed", new[] { FilterPrimitive.Blur(0.05) })
        {
            PrimitiveUnitsObjectBoundingBox = true,
            FilterResolutionX = 40,
            FilterResolutionY = 20,
        });

        string json = VccadDocumentSerializer.Serialize(document);
        FilterSpec back = VccadDocumentSerializer.Deserialize(json).FindFilter("boxed")!;

        Assert.True(back.PrimitiveUnitsObjectBoundingBox);
        Assert.Equal(40, back.FilterResolutionX);
        Assert.Equal(20, back.FilterResolutionY);
        Assert.Equal(document.FindFilter("boxed"), back);

        // A filter that uses neither does not grow members for them, so an older sidecar and this one agree. The
        // names are the sidecar's own members - the serializer writes property names verbatim - so this bites when
        // either is written unconditionally.
        string plainJson = VccadDocumentSerializer.Serialize(
            Document(new FilterSpec("plain", new[] { FilterPrimitive.Blur(1.0) })));

        Assert.DoesNotContain("PrimitiveUnitsObjectBoundingBox", plainJson, StringComparison.Ordinal);
        Assert.DoesNotContain("FilterResolutionX", plainJson, StringComparison.Ordinal);
        Assert.DoesNotContain("FilterResolutionY", plainJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// **A model that carries a resolution this build will not allocate is refused by the engine**, not evaluated
    /// at some other resolution in silence.
    ///
    /// The reader never stores one - it reports the number and leaves the resolution unset - so reaching this means
    /// the model was built by hand or came off a sidecar, and either way an engine that quietly drew it at the
    /// caller's scale would make "filterRes" a field nothing honours.
    /// </summary>
    [Theory]
    [InlineData(100000, 100000)]
    [InlineData(0, 0)]
    [InlineData(-4, -4)]
    [InlineData(12, null)]
    public void AModelCarryingAResolutionThisBuildWillNotEvaluateAtIsRefused(int? width, int? height)
    {
        var filter = new FilterSpec("bad", new[] { FilterPrimitive.Blur(1.0) })
        {
            FilterResolutionX = width,
            FilterResolutionY = height,
        };

        ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(() => new FilterEngine(filter));

        Assert.Contains("resolution", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The same member at a size this build does allocate is taken, so the refusal is about the number.</summary>
    [Fact]
    public void AModelCarryingAResolutionThisBuildWillEvaluateAtIsAccepted()
    {
        var filter = new FilterSpec("fine", new[] { FilterPrimitive.Blur(1.0) })
        {
            FilterResolutionX = FilterSpec.MaximumFilterResolution,
            FilterResolutionY = FilterSpec.MaximumFilterResolution,
        };

        Assert.True(filter.HasFilterResolution);
        Assert.NotNull(new FilterEngine(filter));
    }

    // ---------------------------------------------------------------- the pixels

    /// <summary>
    /// **A length in bounding-box units is not a length in user units**, and the difference is the picture.
    ///
    /// The same radius - a fiftieth of the box under one reading, half a pixel under the other - is applied to the
    /// same block. Under `objectBoundingBox` the shape's own 40 units become the unit, so the blur spreads across
    /// the region and dims the middle of the block; under SVG's default it barely touches it. An engine that
    /// carried the member and measured lengths at the caller's scale either way would draw the *same* buffer here.
    /// </summary>
    [Fact]
    public void ABoundingBoxBlurIsNotAUserSpaceBlur()
    {
        FilterPrimitive[] primitives = { FilterPrimitive.Blur(0.2, "SourceAlpha") };

        var userUnits = new FilterSpec("user", primitives);
        var boxUnits = new FilterSpec("box", primitives) { PrimitiveUnitsObjectBoundingBox = true };

        FilterBuffer user = new FilterEngine(userUnits).Evaluate(Block(), Bounds);
        FilterBuffer box = new FilterEngine(boxUnits).Evaluate(Block(), Bounds);

        int centre = user.Width / 2;
        float userCentre = user.AlphaAt(centre, centre);
        float boxCentre = box.AlphaAt(centre, centre);

        _output.WriteLine(
            $"primitiveUnits userSpaceOnUse: centre alpha {userCentre:0.####}, lit pixels {user.OpaquePixels()} " +
            $"of {user.Width * user.Height}");
        _output.WriteLine(
            $"primitiveUnits objectBoundingBox: centre alpha {boxCentre:0.####}, lit pixels {box.OpaquePixels()} " +
            $"of {box.Width * box.Height}");

        // The same radius is a fifth of a pixel under one reading and eight of them under the other, so the
        // bounding-box blur spreads across the shape and dims the middle of the block while the user-space one
        // barely touches it. An engine that measured lengths at the caller's scale either way draws one buffer here.
        Assert.False(user.Matches(box), "the two unit systems must not draw the same pixels");
        Assert.True(
            boxCentre < userCentre - 0.05f,
            $"a bounding-box blur must spread across the shape: centre {boxCentre} against {userCentre}");
        Assert.True(
            box.OpaquePixels() > user.OpaquePixels(),
            $"and reach further: {box.OpaquePixels()} lit pixels against {user.OpaquePixels()}");
    }

    /// <summary>
    /// **A bounding-box length scales with the shape, and a user-space one does not** - which is the property the
    /// declaration exists for.
    ///
    /// The same filter over a small box and a large one. Under bounding-box units the blur is the same *fraction*
    /// of each, so the same relative point is softened by the same amount at both sizes; under user units the
    /// radius is the same number of pixels in both, so it is a larger part of the small picture than of the large
    /// one and the two are not the same shape at two scales.
    /// </summary>
    [Fact]
    public void ABoundingBoxLengthFollowsTheShapeAndAUserSpaceOneDoesNot()
    {
        FilterPrimitive[] primitives = { FilterPrimitive.Blur(2.0, "SourceAlpha") };
        var region = new { X = 0.0, Y = 0.0, Width = 1.0, Height = 1.0 };

        // 0.05 of the box at either size, because a bounding-box length is a fraction of the shape.
        FilterPrimitive[] relative = { FilterPrimitive.Blur(0.05, "SourceAlpha") };
        var boxUnits = new FilterSpec("box", relative)
        {
            PrimitiveUnitsObjectBoundingBox = true,
            X = region.X,
            Y = region.Y,
            Width = region.Width,
            Height = region.Height,
        };

        FilterBuffer boxSmall = new FilterEngine(boxUnits).Evaluate(Block(40), new Rect2D(0, 0, 40, 40));
        FilterBuffer boxLarge = new FilterEngine(boxUnits).Evaluate(Block(80), new Rect2D(0, 0, 80, 80));

        // The same **relative** point: one unit outside the block on the small shape, two on the large one - the
        // same fiftieth of the box. The blur is the same fraction of each, so the soft edge has the same profile.
        float boxSmallEdge = boxSmall.AlphaAt(9, 20);
        float boxLargeEdge = boxLarge.AlphaAt(18, 40);

        _output.WriteLine(
            $"objectBoundingBox, 0.05 of the box: relative edge alpha {boxSmallEdge:0.####} on 40 units, " +
            $"{boxLargeEdge:0.####} on 80");

        // The same filter's 2.0 measured in user units is 2 pixels at both sizes - a twentieth of the small shape
        // and a fortieth of the large one - so the relative edge is softer on the small one: at the same relative
        // sample the blur has travelled half as far in units of its own sigma.
        var userUnits = new FilterSpec("user", primitives)
        {
            X = region.X,
            Y = region.Y,
            Width = region.Width,
            Height = region.Height,
        };

        FilterBuffer userSmall = new FilterEngine(userUnits).Evaluate(Block(40), new Rect2D(0, 0, 40, 40));
        FilterBuffer userLarge = new FilterEngine(userUnits).Evaluate(Block(80), new Rect2D(0, 0, 80, 80));

        float userSmallEdge = userSmall.AlphaAt(9, 20);
        float userLargeEdge = userLarge.AlphaAt(18, 40);

        _output.WriteLine(
            $"userSpaceOnUse, 2 units: relative edge alpha {userSmallEdge:0.####} on 40, {userLargeEdge:0.####} on 80");

        // The comparison is between the two gaps rather than against a fixed number, so the assertion is the
        // property - a shape-relative length does not change with the shape, a user-space one does - and not a
        // tolerance chosen to fit one pair of measurements.
        float boxGap = Math.Abs(boxSmallEdge - boxLargeEdge);
        float userGap = Math.Abs(userSmallEdge - userLargeEdge);

        _output.WriteLine($"relative gap across the two sizes: {boxGap:0.####} in box units, {userGap:0.####} in user units");

        // The bounding-box blur is genuinely soft at the relative sample - a fifth of a unit out - which is what a
        // reading of 0.05 as a user-space length would make vanish entirely.
        Assert.True(boxSmallEdge > 0.1f, $"0.05 of a 40-unit box must blur: edge alpha {boxSmallEdge}");
        Assert.True(
            userGap > 0.05f && boxGap < userGap / 2f,
            $"a bounding-box length must follow the shape ({boxGap}) where a user-space one does not ({userGap})");
    }

    /// <summary>
    /// **A resolution changes the sampling, and that is visible.**
    ///
    /// The same blur is evaluated at 12 pixels across the region and at 48. Everything else - the region, the
    /// source, the radius - is identical, so a difference between the two buffers can only come from the
    /// resolution: the coarse one computes the blur on twelve pixels and the result stretched over the region is
    /// blockier, with far fewer distinct levels in it. An engine that read the member and evaluated at the caller's
    /// scale regardless would hand back the same 48x48 buffer twice.
    /// </summary>
    [Fact]
    public void AFilterResolutionChangesTheSampling()
    {
        FilterPrimitive[] primitives = { FilterPrimitive.Blur(3.0, "SourceAlpha") };

        var coarseSpec = new FilterSpec("coarse", primitives)
        {
            FilterResolutionX = 12,
            FilterResolutionY = 12,
        };

        var fineSpec = new FilterSpec("fine", primitives);

        FilterBuffer coarse = new FilterEngine(coarseSpec).Evaluate(Block(), Bounds);
        FilterBuffer fine = new FilterEngine(fineSpec).Evaluate(Block(), Bounds);

        int coarseLevels = DistinctAlphaLevels(coarse);
        int fineLevels = DistinctAlphaLevels(fine);

        _output.WriteLine(
            $"filterRes=12: {coarse.Width}x{coarse.Height}, {coarseLevels} distinct alpha levels, " +
            $"peak {PeakAlpha(coarse):0.####}");
        _output.WriteLine(
            $"no filterRes: {fine.Width}x{fine.Height}, {fineLevels} distinct alpha levels, " +
            $"peak {PeakAlpha(fine):0.####}");

        Assert.Equal(12, coarse.Width);
        Assert.Equal(12, coarse.Height);
        Assert.Equal(48, fine.Width);

        // The resolution is the size of the picture, and the picture is genuinely sampled differently.
        Assert.False(coarse.Matches(fine), "a filter evaluated at another resolution must not draw the same pixels");
        Assert.True(
            coarseLevels < fineLevels,
            $"a coarse resolution must quantise the picture: {coarseLevels} levels against {fineLevels}");
    }

    /// <summary>
    /// And the same holds on the path the canvas takes, where the region is already rendered and the engine is
    /// asked to filter it in place: the result comes back at the resolution the file named, so the caller stretches
    /// it over the rectangle it computed.
    /// </summary>
    [Fact]
    public void AFilterResolutionChangesTheSamplingInPlaceToo()
    {
        var filter = new FilterSpec("coarse", new[] { FilterPrimitive.Blur(3.0, "SourceAlpha") })
        {
            FilterResolutionX = 12,
            FilterResolutionY = 12,
        };

        (int x, int y, int width, int height) = FilterEngine.RegionPixels(filter, Bounds, scale: 1.0);
        Assert.Equal(48, width);

        var region = new FilterBuffer(width, height);
        region.Blit(Block(), -x, -y);

        FilterBuffer filtered = new FilterEngine(filter).EvaluateInPlace(region, objectBounds: Bounds);

        _output.WriteLine(
            $"in place: region {width}x{height} in, {filtered.Width}x{filtered.Height} out, " +
            $"{DistinctAlphaLevels(filtered)} distinct alpha levels");

        Assert.Equal(12, filtered.Width);
        Assert.Equal(12, filtered.Height);
        Assert.True(filtered.OpaquePixels() > 0, "the coarse evaluation must still draw the shape");
    }

    /// <summary>
    /// **The two ways in agree.** <see cref="FilterEngine.Evaluate"/> places a source in a region it allocates;
    /// <see cref="FilterEngine.EvaluateInPlace"/> filters a region the caller already rendered. Given the same
    /// region and the same object box they must draw the same picture, or the canvas and a caller that evaluates
    /// directly would disagree about a filter that names bounding-box units.
    /// </summary>
    [Fact]
    public void EvaluatingDirectlyAndInPlaceAgreeUnderBoundingBoxUnits()
    {
        var filter = new FilterSpec("boxed", new[] { FilterPrimitive.Blur(0.05, "SourceAlpha") })
        {
            PrimitiveUnitsObjectBoundingBox = true,
        };

        FilterBuffer direct = new FilterEngine(filter).Evaluate(Block(), Bounds);

        (int x, int y, int width, int height) = FilterEngine.RegionPixels(filter, Bounds, scale: 1.0);
        var region = new FilterBuffer(width, height);
        region.Blit(Block(), -x, -y);
        FilterBuffer inPlace = new FilterEngine(filter)
            .EvaluateInPlace(region, objectBounds: Bounds);

        _output.WriteLine(
            $"direct {direct.Width}x{direct.Height} centre {direct.AlphaAt(direct.Width / 2, direct.Height / 2):0.####}, " +
            $"in place {inPlace.Width}x{inPlace.Height} centre {inPlace.AlphaAt(inPlace.Width / 2, inPlace.Height / 2):0.####}");

        Assert.True(direct.Matches(inPlace, 0.002f), "the two evaluation paths must draw the same picture");
    }

    /// <summary>
    /// A box is measured from the shape when an in-place caller does not state one, and it is the shape's own
    /// extent: the result is exactly the result for that box stated by hand, which is what keeps the canvas - which
    /// knows the geometry but does not pass it - drawing a bounding-box filter the way a caller that does passes it.
    /// </summary>
    [Fact]
    public void AnInPlaceCallerThatStatesNoBoxHasItMeasuredFromTheShape()
    {
        var filter = new FilterSpec("boxed", new[] { FilterPrimitive.Blur(0.05, "SourceAlpha") })
        {
            PrimitiveUnitsObjectBoundingBox = true,
        };

        (int x, int y, int width, int height) = FilterEngine.RegionPixels(filter, Bounds, scale: 1.0);
        var region = new FilterBuffer(width, height);
        region.Blit(Block(), -x, -y);

        FilterBuffer measured = new FilterEngine(filter).EvaluateInPlace(region);

        // The block covers 20 of the 40 units, so that is the box the engine measures - region origin and all.
        FilterBuffer stated = new FilterEngine(filter)
            .EvaluateInPlace(region, objectBounds: new Rect2D(10, 10, 20, 20));

        _output.WriteLine(
            $"measured-box sigma from a 20-unit shape; measured centre " +
            $"{measured.AlphaAt(measured.Width / 2, measured.Height / 2):0.####} against stated " +
            $"{stated.AlphaAt(stated.Width / 2, stated.Height / 2):0.####}");

        Assert.True(measured.Matches(stated, 0f), "a measured box must be the box it measures");
    }

    private static int DistinctAlphaLevels(FilterBuffer buffer)
    {
        var levels = new HashSet<int>();
        for (int y = 0; y < buffer.Height; y++)
        {
            for (int x = 0; x < buffer.Width; x++)
            {
                levels.Add((int)Math.Round(buffer.AlphaAt(x, y) * 255));
            }
        }

        return levels.Count;
    }

    private static float PeakAlpha(FilterBuffer buffer)
    {
        float peak = 0;
        for (int y = 0; y < buffer.Height; y++)
        {
            for (int x = 0; x < buffer.Width; x++)
            {
                peak = Math.Max(peak, buffer.AlphaAt(x, y));
            }
        }

        return peak;
    }
}
