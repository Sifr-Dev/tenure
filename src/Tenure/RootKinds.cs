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
}
