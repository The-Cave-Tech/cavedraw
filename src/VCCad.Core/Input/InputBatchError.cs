namespace VCCad.Core.Input;

/// <summary>One reason a batch cannot be replayed, and where in the list it is.</summary>
/// <param name="Index">The zero-based position of the offending event.</param>
/// <param name="Message">What is wrong with it.</param>
public sealed record InputBatchError(int Index, string Message)
{
    /// <summary>The error as a person reads it, with the offending index in it.</summary>
    public override string ToString() => $"event index {Index}: {Message}";
}

/// <summary>
/// A batch that is not well formed, carrying the index of the offending event.
///
/// The exception is raised before anything is delivered, so a malformed batch is rejected
/// whole rather than applied until it breaks.
/// </summary>
public sealed class InputBatchException : Exception
{
    /// <summary>Creates the error from every fault found, in order.</summary>
    public InputBatchException(IReadOnlyList<InputBatchError> errors)
        : base(Describe(errors))
    {
        Errors = errors;
        Index = errors.Count > 0 ? errors[0].Index : -1;
    }

    /// <summary>The first offending index; -1 when there are no errors.</summary>
    public int Index { get; }

    /// <summary>Every fault found, in order.</summary>
    public IReadOnlyList<InputBatchError> Errors { get; }

    /// <summary>Creates the error from a single fault.</summary>
    public static InputBatchException Of(int index, string message)
        => new(new[] { new InputBatchError(index, message) });

    private static string Describe(IReadOnlyList<InputBatchError> errors)
        => errors.Count == 0
            ? "The input batch is malformed."
            : "The input batch is malformed: " + string.Join("; ", errors.Select(e => e.ToString()));
}
