using System.Runtime.CompilerServices;
using Inspection.Core;
using Inspection.CppCli;
using Inspection.Infrastructure;
using Inspection.Interop;

namespace Inspection.Host;

internal sealed class HostInspector : IAsyncDisposable
{
    private HostInspector(IInspector inspector) => Inspector = inspector;
    internal IInspector Inspector { get; }

    internal static HostInspector Create(string kind, string fault, Action<NativeInspectionProgress>? progress = null) => new(kind switch
    {
        "managed" => new RangeInspector(),
        "native" => new NativeInspector(HostStorage.NativeFault(fault), progress),
        "cli" => CreateCli(HostStorage.NativeFault(fault), progress),
        _ => throw new ArgumentException("Unknown inspector.", nameof(kind))
    });

    // Load the mixed assembly only when selected, inside Host's setup error boundary.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IInspector CreateCli(NativeFaultMode fault, Action<NativeInspectionProgress>? progress) => new CliInspector(fault, progress);

    public ValueTask DisposeAsync() => Inspector is IAsyncDisposable disposable ? disposable.DisposeAsync() : ValueTask.CompletedTask;
}
