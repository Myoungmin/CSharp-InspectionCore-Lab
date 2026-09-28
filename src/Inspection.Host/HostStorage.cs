using Inspection.Core;
using Inspection.Infrastructure;
using Inspection.Interop;

namespace Inspection.Host;

internal sealed record HostStorage(IResultStore Writer, IResultReader Reader, IResultSearch? Search, string Location)
{
    internal static async Task<HostStorage> CreateAsync(string kind, string output, string fault)
    {
        IResultStore writer;
        IResultReader reader;
        IResultSearch? search = null;
        string location;
        if (kind == "sqlite")
        {
            var sqlite = new SqliteResultStore(Path.Combine(output, "inspection.db"));
            await sqlite.InitializeAsync().ConfigureAwait(false);
            writer = sqlite; reader = sqlite; search = sqlite; location = sqlite.DatabasePath;
        }
        else
        {
            var json = new JsonResultStore(output);
            writer = json; reader = json; location = Path.GetFullPath(output);
        }
        if (fault == "store") { writer = new FailingStore(); }
        return new(writer, reader, search, location);
    }

    internal string ResultLocation(Guid runId) => Reader is JsonResultStore json ? json.GetResultPath(runId) : Location;

    internal static bool ValidFault(string fault, string inspector, bool server) => fault is "none" or "store"
        || (inspector == "native" && fault is "native-inspect" or "native-wait") || (server && fault == "ipc-response");

    internal static NativeFaultMode NativeFault(string fault) => fault switch
    {
        "native-inspect" => NativeFaultMode.ThrowOnInspect,
        "native-wait" => NativeFaultMode.ThrowOnWait,
        _ => NativeFaultMode.None
    };

    private sealed class FailingStore : IResultStore
    {
        public Task SaveAsync(InspectionResult result, CancellationToken cancellationToken) =>
            Task.FromException(new IOException("Injected persistence failure."));
    }
}
