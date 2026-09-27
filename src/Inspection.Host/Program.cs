using System.Globalization;
using Inspection.Core;
using Inspection.Infrastructure;
using Inspection.Interop;

string scenario = "pass";
string inspectorKind = "managed";
string outputDirectory = Path.Combine("artifacts", "results");
TimeSpan? timeout = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--help")
    {
        Console.WriteLine("Inspection.Host [--scenario pass|fail] [--inspector managed|native] [--output DIRECTORY] [--timeout-ms 0..4294967294]");
        return 0;
    }

    if (args[i] is not ("--scenario" or "--output" or "--inspector" or "--timeout-ms") || i + 1 >= args.Length)
    {
        Console.Error.WriteLine("Invalid arguments. Use --help.");
        return 2;
    }

    string option = args[i];
    string value = args[++i];
    if (option == "--scenario") { scenario = value; }
    else if (option == "--inspector") { inspectorKind = value; }
    else if (option == "--timeout-ms")
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long milliseconds) ||
            milliseconds > uint.MaxValue - 1L)
        {
            Console.Error.WriteLine("Timeout must be an integer from 0 through 4294967294 milliseconds.");
            return 2;
        }
        timeout = TimeSpan.FromMilliseconds(milliseconds);
    }
    else { outputDirectory = value; }
}

if (scenario is not ("pass" or "fail") || inspectorKind is not ("managed" or "native") || string.IsNullOrWhiteSpace(outputDirectory))
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

try
{
    double[] samples = scenario == "pass" ? [10, 20, 30, 40] : [10, 20, 30, 140];
    var job = new InspectionJob($"demo-{scenario}", samples, 0, 100);
    var store = new JsonResultStore(outputDirectory);
    using NativeInspector? nativeInspector = inspectorKind == "native" ? new NativeInspector() : null;
    IInspector inspector = nativeInspector is null ? new RangeInspector() : nativeInspector;
    var runner = new InspectionRunner(new SimulatedDevice(), inspector, store);
    await using var engine = new InspectionEngine(runner);
    cancellation.Token.ThrowIfCancellationRequested();
    InspectionRunHandle run = engine.Start(job, timeout).Run ?? throw new InvalidOperationException("Host run was not admitted.");
    using CancellationTokenRegistration registration = cancellation.Token.Register(() => engine.CancelRun(run.RunId));
    InspectionRunSnapshot completed = await run.Completion;
    if (completed.State == InspectionRunState.Succeeded)
    {
        InspectionResult result = completed.ComputedResult!;
        Console.WriteLine(FormattableString.Invariant(
            $"RunId={result.RunId} JobId={result.JobId} Status=Succeeded Verdict={result.Assessment.Verdict} Score={result.Assessment.Score:F2} Defects={result.Assessment.DefectCount} Inspector={inspectorKind}"));
        Console.WriteLine($"Result={store.GetResultPath(result.RunId)}");
        return 0;
    }
    if (completed.State is InspectionRunState.Canceled or InspectionRunState.TimedOut)
    {
        Console.Error.WriteLine($"RunId={run.RunId} Status={completed.State} Stage={completed.Stage} Reason={completed.StopReason}");
        return completed.State == InspectionRunState.TimedOut ? 124 : 130;
    }
    string score = completed.ComputedResult?.Assessment.Score.ToString("F2", CultureInfo.InvariantCulture) ?? "unavailable";
    Console.Error.WriteLine($"RunId={run.RunId} Status=Faulted Stage={completed.Stage} ComputedScore={score}");
    Console.Error.WriteLine((completed.StopError ?? completed.Error)?.GetBaseException().Message);
    return 1;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.Error.WriteLine("Status=Canceled Stage=Setup");
    return 130;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Status=Faulted Stage=Setup Error={exception.Message}");
    return 1;
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}
