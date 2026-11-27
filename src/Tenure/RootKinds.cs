namespace Tenure;

public static class RootKinds
{
    // Values mirror Microsoft.Diagnostics.Tracing.Parsers.Clr.GCRootKind so the snapshot
    // stays stable if the trace parser enum changes order.
    public const int Stack = 0;
    public const int Finalizer = 1;
    public const int Handle = 2;
    public const int Older = 3;
    public const int SizedRef = 4;
    public const int Overflow = 5;

    public static string NameOf(int kind) => kind switch
    {
        Stack => "Stack",
        Finalizer => "Finalizer",
        Handle => "Handle",
        Older => "Older",
        SizedRef => "SizedRef",
        Overflow => "Overflow",
        _ => $"Kind {kind}",
    };

    /// <summary>
    /// True only for the six kinds pinned above, so a snapshot cannot smuggle an unrecognised root
    /// kind name into ownership evidence and have it read back as a known root.
    /// </summary>
    public static bool IsKnown(int kind) => kind is >= Stack and <= Overflow;

    public static bool IsKnownName(string? name) =>
        name is "Stack" or "Finalizer" or "Handle" or "Older" or "SizedRef" or "Overflow";
}
