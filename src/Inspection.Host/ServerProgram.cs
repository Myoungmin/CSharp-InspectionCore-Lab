using System.Globalization;
using System.Text.RegularExpressions;
using Inspection.Core;
using Inspection.Infrastructure;
using Inspection.Interop;

namespace Inspection.Host;

internal static class ServerProgram
{
    internal static async Task<int> RunAsync(string[] args)
    {
        string pipeName = "InspectionLab";
        string inspectorKind = "managed";
        string output = Path.Combine("artifacts", "results");
        int delayMilliseconds = 0;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--help")
            {
                Console.WriteLine("Inspection.Host --serve [--pipe NAME] [--inspector managed|native] [--output DIRECTORY] [--device-delay-ms 0..60000]\nType exit or press Ctrl+C to stop and await accepted work.");
                return 0;
            }
            if (i + 1 >= args.Length) { return InvalidArguments(); }
            string option = args[i], value = args[++i];
            switch (option)
            {
                case "--pipe": pipeName = value; break;
                case "--inspector": inspectorKind = value; break;
                case "--output": output = value; break;
                case "--device-delay-ms":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out delayMilliseconds) || delayMilliseconds > 60_000) { return InvalidArguments(); }
                    break;
                default: return InvalidArguments();
            }
        }
        if (!Regex.IsMatch(pipeName, "\\A[A-Za-z0-9_-]{1,80}\\z") || inspectorKind is not ("managed" or "native") || string.IsNullOrWhiteSpace(output))
        {
            return InvalidArguments();
        }
        using var stopping = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; stopping.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var store = new JsonResultStore(output);
            await using NativeInspector? native = inspectorKind == "native" ? new NativeInspector() : null;
            IInspector inspector = native is null ? new RangeInspector() : native;
            await using var engine = new InspectionEngine(new InspectionRunner(new DelayedDevice(delayMilliseconds), inspector, store));
            await using var server = new InspectionPipeServer(pipeName, new RequestDispatcher(engine, store));
            Task serving = server.RunAsync(stopping.Token);
            Task console = ReadShutdownAsync(stopping);
            Console.WriteLine($"Listening Pipe={pipeName} ProtocolVersion=1 Inspector={inspectorKind}");
            await Task.WhenAny(serving, console);
            await stopping.CancelAsync();
            await serving;
            await console;
            // Engine disposal joins accepted work before the native adapter is disposed.
            return 0;
        }
        catch (Exception exception)
        {
            await stopping.CancelAsync();
            Console.Error.WriteLine($"Server failed: {exception.Message}");
            return 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static async Task ReadShutdownAsync(CancellationTokenSource stopping)
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                // Console's synchronized reader can block even through ReadLineAsync.
                // A background read must not prevent listener readiness or cancellation.
                string? line = await Task.Run(Console.ReadLine).WaitAsync(stopping.Token);
                if (line == "exit") { await stopping.CancelAsync(); return; }
                if (line is null) { await Task.Delay(Timeout.InfiniteTimeSpan, stopping.Token); }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    private static int InvalidArguments() { Console.Error.WriteLine("Invalid server arguments. Use --serve --help."); return 2; }

    private sealed class DelayedDevice(int milliseconds) : IDevice
    {
        private readonly SimulatedDevice _device = new();
        public Task PrepareAsync(InspectionJob job, CancellationToken token) => _device.PrepareAsync(job, token);
        public async Task<double[]> AcquireAsync(InspectionJob job, CancellationToken token)
        {
            if (milliseconds > 0) { await Task.Delay(milliseconds, token).ConfigureAwait(false); }
            return await _device.AcquireAsync(job, token).ConfigureAwait(false);
        }
    }
}
