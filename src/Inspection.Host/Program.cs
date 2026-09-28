using System.Globalization;
using Inspection.Core;
using Inspection.Infrastructure;
using Inspection.Interop;
using Inspection.Host;

if (args.FirstOrDefault() == "--serve") { return await Inspection.Host.ServerProgram.RunAsync(args[1..]); }

string scenario = "pass";
string inspectorKind = "managed";
string outputDirectory = Path.Combine("artifacts", "results");
TimeSpan? timeout = null;
int? repeat = null;
TimeSpan interval = TimeSpan.FromSeconds(1);
bool intervalSpecified = false;
string storeKind = "json";
string fault = "none";
string logPath = Path.Combine("artifacts", "logs", $"{Guid.NewGuid():N}.jsonl");
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--help")
    {
        Console.WriteLine("Inspection.Host [--scenario pass|fail] [--inspector managed|native] [--output DIRECTORY] [--timeout-ms 0..4294967294] [--repeat 1..2147483647 [--interval-ms 0..4294967294]]");
        Console.WriteLine("Inspection.Host --serve [--pipe NAME] [--inspector managed|native] [--output DIRECTORY] [--device-delay-ms 0..60000]");
        Console.WriteLine("[--store json|sqlite] [--log FILE.jsonl] [--fault none|store|native-inspect|native-wait] (server also supports ipc-response)");
        return 0;
    }

    if (args[i] is not ("--scenario" or "--output" or "--inspector" or "--timeout-ms" or "--repeat" or "--interval-ms" or "--store" or "--log" or "--fault") || i + 1 >= args.Length)
    {
        Console.Error.WriteLine("Invalid arguments. Use --help.");
        return 2;
    }

    string option = args[i];
    string value = args[++i];
    if (option == "--scenario") { scenario = value; }
    else if (option == "--inspector") { inspectorKind = value; }
    else if (option == "--store") { storeKind = value; }
    else if (option == "--log") { logPath = value; }
    else if (option == "--fault") { fault = value; }
    else if (option == "--repeat")
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count <= 0)
        {
            Console.Error.WriteLine("Repeat must be an integer from 1 through 2147483647.");
            return 2;
        }
        repeat = count;
    }
    else if (option is "--timeout-ms" or "--interval-ms")
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long milliseconds) ||
            milliseconds > uint.MaxValue - 1L)
        {
            Console.Error.WriteLine("Timeout and interval must be integers from 0 through 4294967294 milliseconds.");
            return 2;
        }
        if (option == "--timeout-ms") { timeout = TimeSpan.FromMilliseconds(milliseconds); }
        else { interval = TimeSpan.FromMilliseconds(milliseconds); intervalSpecified = true; }
    }
    else { outputDirectory = value; }
}

if (intervalSpecified && repeat is null)
{
    Console.Error.WriteLine("--interval-ms requires --repeat.");
    return 2;
}

if (scenario is not ("pass" or "fail") || inspectorKind is not ("managed" or "native") || string.IsNullOrWhiteSpace(outputDirectory)
    || storeKind is not ("json" or "sqlite") || string.IsNullOrWhiteSpace(logPath) || !HostStorage.ValidFault(fault, inspectorKind, server: false))
{
    Console.Error.WriteLine("Scenario must be pass or fail; inspector must be managed or native; output must be a directory path.");
    return 2;
}

using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
Console.CancelKeyPress += cancelHandler;
JsonDiagnosticLog? diagnostics = null;

try
{
    diagnostics = new JsonDiagnosticLog(logPath);
    diagnostics.Write("HostStarting", new { Store = storeKind, Inspector = inspectorKind, Fault = fault });
    double[] samples = scenario == "pass" ? [10, 20, 30, 40] : [10, 20, 30, 140];
    var job = new InspectionJob($"demo-{scenario}", samples, 0, 100);
    HostStorage store = await HostStorage.CreateAsync(storeKind, outputDirectory, fault);
    await using NativeInspector? nativeInspector = inspectorKind == "native"
        ? new NativeInspector(HostStorage.NativeFault(fault), progress: progress => Console.WriteLine($"NativeProgress={progress.CompletedSamples}/{progress.TotalSamples}")) : null;
    IInspector inspector = nativeInspector is null ? new RangeInspector() : nativeInspector;
    var runner = new InspectionRunner(new SimulatedDevice(), inspector, store.Writer);
    await using var engine = new InspectionEngine(runner, diagnostics: diagnostics);
    cancellation.Token.ThrowIfCancellationRequested();
    InspectionRunSnapshot completed;
    int? autoExitCode = null;
    if (repeat is { } count)
    {
        InspectionAutoHandle auto = engine.StartAuto(job, interval, timeout, count).Auto
            ?? throw new InvalidOperationException("Host auto session was not admitted.");
        diagnostics.Write("AutoAdmitted", autoId: auto.AutoId, jobId: job.JobId);
        using CancellationTokenRegistration registration = cancellation.Token.Register(() =>
        {
            engine.StopAuto(auto.AutoId);
            if (engine.GetAuto(auto.AutoId)?.CurrentRunId is { } runId) { engine.CancelRun(runId); }
        });
        InspectionAutoSnapshot summary = await auto.Completion;
        diagnostics.Write("AutoCompleted", new { summary.State, summary.StopReason, summary.StartedRuns, summary.CompletedRuns }, autoId: auto.AutoId, error: summary.Error);
        Console.WriteLine($"AutoId={summary.AutoId} State={summary.State} Reason={summary.StopReason} StartedRuns={summary.StartedRuns} CompletedRuns={summary.CompletedRuns}");
        if (summary.Error is not null && summary.StopReason == InspectionAutoStopReason.SchedulingFailed)
        {
            Console.Error.WriteLine($"AutoError={summary.Error.Message}");
        }
        completed = summary.LastRun ?? throw new InvalidOperationException("Auto session completed without a run outcome.");
        autoExitCode = summary.State == InspectionAutoState.Completed ? 0
            : summary.State == InspectionAutoState.Faulted ? 1
            : summary.StopReason == InspectionAutoStopReason.RunTimedOut ? 124 : 130;
    }
    else
    {
        InspectionRunHandle run = engine.Start(job, timeout).Run ?? throw new InvalidOperationException("Host run was not admitted.");
        diagnostics.Write("RunAdmitted", runId: run.RunId, jobId: job.JobId);
        using CancellationTokenRegistration registration = cancellation.Token.Register(() => engine.CancelRun(run.RunId));
        completed = await run.Completion;
    }
    if (completed.State == InspectionRunState.Succeeded)
    {
        InspectionResult result = completed.ComputedResult!;
        Console.WriteLine(FormattableString.Invariant(
            $"RunId={result.RunId} JobId={result.JobId} Status=Succeeded Verdict={result.Assessment.Verdict} Score={result.Assessment.Score:F2} Defects={result.Assessment.DefectCount} Inspector={inspectorKind}"));
        Console.WriteLine($"Result={store.ResultLocation(result.RunId)}");
        return autoExitCode ?? 0;
    }
    if (completed.State is InspectionRunState.Canceled or InspectionRunState.TimedOut)
    {
        Console.Error.WriteLine($"RunId={completed.RunId} Status={completed.State} Stage={completed.Stage} Reason={completed.StopReason}");
        return autoExitCode ?? (completed.State == InspectionRunState.TimedOut ? 124 : 130);
    }
    string score = completed.ComputedResult?.Assessment.Score.ToString("F2", CultureInfo.InvariantCulture) ?? "unavailable";
    Console.Error.WriteLine($"RunId={completed.RunId} Status=Faulted Stage={completed.Stage} ComputedScore={score} TerminationConfirmed={completed.TerminationConfirmed}");
    Console.Error.WriteLine((completed.StopError ?? completed.Error)?.GetBaseException().Message);
    return 1;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    diagnostics?.Write("HostCanceled");
    Console.Error.WriteLine("Status=Canceled Stage=Setup");
    return 130;
}
catch (Exception exception)
{
    diagnostics?.Write("HostFaulted", error: exception);
    Console.Error.WriteLine($"Status=Faulted Stage=Setup Error={exception.Message}");
    return 1;
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
    diagnostics?.Write("HostStopped");
    diagnostics?.Dispose();
}
