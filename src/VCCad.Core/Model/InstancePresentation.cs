namespace VCCad.Core.Model;

/// <summary>
/// The presentation a `use` site establishes for the content it instances: the `fill` and `stroke` its computed
/// style hands down into the definition it draws (issue #117).
///
/// **Why the model needs it.** SVG's `use` draws its target *with the use site's style*: a `<use fill="red">` over a
/// definition that states no fill draws red, and the importer honours that by reading the target with the composed
/// style, so the instance's copy carries red. The copy is what is drawn, so the picture is right - and it is exactly
/// the copy that <see cref="Commands.RefreshInstancesCommand"/> replaces. Re-materialisation clones the
/// **definition**, whose content was read under SVG's own initial values, so without this member an instance loses
/// the paint its own `use` stated the first time the document is refreshed. Measured before this member existed:
/// a `fill="red"` instance came back `R=0,G=0,B=0` from `R=1,G=0,B=0`.
///
/// **It is the inherited presentation, not the whole computed style.** These are the two properties that cascade
/// down the shadow tree the `use` creates - `stroke-width`, the caps, the joins, the miter limit and the dash are
/// members of <see cref="Stroke"/> and travel with it. `mix-blend-mode` is deliberately **not** here: CSS says it
/// does not inherit, so a use site's blend is a fact about the instance group and stays on
/// <see cref="LayerItem.BlendMode"/>. A marker reference is inherited too but is not carried, because this model
/// turns a marker into art at read time rather than into a member a repaint could re-apply; that residue is named
/// where it is lost.
///
/// **Absent when it states nothing.** <see cref="IsDefault"/> is SVG's own initial values - a black fill and no
/// stroke - and an instance whose use site establishes nothing beyond them records nothing, so an ordinary document
/// grows no member and serialises to the bytes it did before. A document whose `use` sites do state a paint records
/// them, which is the whole point.
/// </summary>
/// <param name="Fill">The fill in force on the content the `use` draws.</param>
/// <param name="Stroke">
/// The stroke in force on the content the `use` draws, width and all. <see cref="StrokeSpec.None"/> - no visible
/// stroke - is SVG's initial value and the one an unstated use site leaves behind.
/// </param>
public sealed record InstancePresentation(FillSpec Fill, StrokeSpec Stroke)
{
    /// <summary>SVG's initial values, and what an unstated use site establishes: black fill, no stroke.</summary>
    public static InstancePresentation Default { get; } = new(FillSpec.Solid(ColorRgb.Black), StrokeSpec.None);

    /// <summary>
    /// Whether this is SVG's own initial presentation, and so a use site that stated nothing the definition would
    /// not already have been read with.
    /// </summary>
    public bool IsDefault => Equals(this, Default);

    /// <summary>
    /// The presentation a nested instance inherits from the instance around it, with **its own** stated values
    /// winning.
    ///
    /// A definition is read under SVG's initial values, so a `use` *inside* a definition carries no record of the
    /// outer use's cascade - the copy does, but the copy is what re-materialisation replaces. This puts the outer
    /// presentation back for the nested instance while letting the inner use's own declarations beat it, which is
    /// the order the shadow trees have: a property the inner use states is a property the outer one cannot reach.
    /// A member left at its initial value is indistinguishable from an inherited one, which is exactly the case
    /// where the two answers agree.
    /// </summary>
    public static InstancePresentation Compose(InstancePresentation outer, InstancePresentation? inner)
    {
        if (inner is null)
        {
            return outer;
        }

        return new InstancePresentation(
            Equals(inner.Fill, Default.Fill) ? outer.Fill : inner.Fill,
            Equals(inner.Stroke, Default.Stroke) ? outer.Stroke : inner.Stroke);
    }
}
