namespace VCCad.Core.Model;

/// <summary>
/// What the file a document was read from was protected with, and what that protection permits.
///
/// This is recorded rather than enforced. A print shop opening a pattern with an owner password -
/// "you may read and print this, but do not edit it" - has work to do, and the work is to lay the
/// artwork onto the paper the press actually has. Whether that is within the file's terms is a
/// judgement for the person and their customer, not for a parser, so the program's job is to **state
/// the facts plainly** and then not get in the way.
///
/// The permission bits are ISO 32000-1 Table 22. Bit numbers there are one-based from the least
/// significant end, so bit 3 (printing) is value 4.
/// </summary>
/// <param name="Cipher">What the file was encrypted with, for reporting: RC4-40, AES-128, AES-256…</param>
/// <param name="Permissions">The /P value exactly as the file wrote it, signed.</param>
/// <param name="Opened">Whether the file was actually decrypted, or is protected and still closed.</param>
/// <param name="OpenedWithOwnerPassword">Whether the owner password was the one that opened it.</param>
public sealed record DocumentSecurity(
    string Cipher,
    int Permissions,
    bool Opened,
    bool OpenedWithOwnerPassword)
{
    /// <summary>Whether the file was protected at all. Always true for an instance of this type.</summary>
    public bool IsEncrypted => true;

    // Bits 1 and 2 are reserved and always clear; bit 2 means "not used". An owner-opened file has
    // everything granted whatever /P says, because /P restricts the USER password.
    private bool Bit(int bit) => (Permissions & (1 << (bit - 1))) != 0;
    private bool Granted(int bit) => OpenedWithOwnerPassword || Bit(bit);

    /// <summary>Bit 3: print the document.</summary>
    public bool CanPrint => Granted(3);

    /// <summary>Bit 4: modify the contents.</summary>
    public bool CanModify => Granted(4);

    /// <summary>Bit 5: copy text and graphics out.</summary>
    public bool CanCopy => Granted(5);

    /// <summary>Bit 6: add or modify annotations and fill form fields.</summary>
    public bool CanAnnotate => Granted(6);

    /// <summary>Bit 9: fill in form fields even when bit 6 is clear.</summary>
    public bool CanFillForms => Granted(9);

    /// <summary>Bit 10: extract text for accessibility.</summary>
    public bool CanExtractForAccessibility => Granted(10);

    /// <summary>Bit 11: assemble the document - insert, rotate or delete pages.</summary>
    public bool CanAssemble => Granted(11);

    /// <summary>Bit 12: print at high resolution, not just as a degraded proof.</summary>
    public bool CanPrintHighResolution => Granted(12);

    /// <summary>
    /// Every permission as a name and a yes/no, in the order a person would ask them.
    ///
    /// Built here rather than in the status bar so the same list reaches the tooltip, the
    /// <c>document.security</c> operation and anything later that has to report it - one statement of
    /// what the file says, not one per consumer.
    /// </summary>
    public IReadOnlyList<(string Name, bool Allowed)> Listed() => new[]
    {
        ("Printing", CanPrint),
        ("High-resolution printing", CanPrintHighResolution),
        ("Copying text and graphics", CanCopy),
        ("Modifying the document", CanModify),
        ("Annotations and form fields", CanAnnotate),
        ("Filling form fields", CanFillForms),
        ("Extraction for accessibility", CanExtractForAccessibility),
        ("Assembling the document", CanAssemble),
    };
}
