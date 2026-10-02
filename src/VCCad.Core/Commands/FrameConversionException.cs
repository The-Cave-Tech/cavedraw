using VCCad.Core.Model;

namespace VCCad.Core.Commands;

/// <summary>
/// A move between containers was refused because the geometry could not be carried across the frame
/// change without being stored in a frame it was never expressed in.
///
/// This is the rule the repository applies everywhere a transform crosses a frame boundary (#165, #172,
/// #173, #174): there is no honest answer to "where does this go" when the destination frame is not
/// invertible, or when the item's own members cannot express the resulting transform - a text block
/// holds an origin and an angle, a placed image holds a rectangle, and a shear or an anisotropic scale
/// is neither. Guessing one would silently write a wrong file, so the move is refused and reported.
/// </summary>
public sealed class FrameConversionException : InvalidOperationException
{
    public FrameConversionException(string message) : base(message)
    {
    }
}
