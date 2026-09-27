using System.Globalization;
using Inspection.Core;
using Inspection.Infrastructure;
using Inspection.Interop;

string scenario = "pass";
string inspectorKind = "managed";
string outputDirectory = Path.Combine("artifacts", "results");
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--help")
    {
        Console.WriteLine("Inspection.Host [--scenario pass|fail] [--inspector managed|native] [--output DIRECTORY]");
        return 0;
    }

    if (args[i] is not ("--scenario" or "--output" or "--inspector") || i + 1 >= args.Length)
    {
        Console.Error.WriteLine("Invalid arguments. Use --help.");
        return 2;
    }

    string option = args[i];
    string value = args[++i];
    if (option == "--scenario") { scenario = value; }
    else if (option == "--inspector") { inspectorKind = value; }
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
    InspectionResult result = await runner.RunAsync(job, cancellation.Token);
    Console.WriteLine(FormattableString.Invariant(
        $"RunId={result.RunId} JobId={result.JobId} Status=Succeeded Verdict={result.Assessment.Verdict} Score={result.Assessment.Score:F2} Defects={result.Assessment.DefectCount} Inspector={inspectorKind}"));
    Console.WriteLine($"Result={store.GetResultPath(result.RunId)}");
    return 0;
}
catch (InspectionCanceledException exception)
{
    Console.Error.WriteLine($"RunId={exception.RunId} Status=Canceled Stage={exception.Stage}");
    return 130;
}
catch (InspectionRunException exception)
{
    string score = exception.ComputedResult?.Assessment.Score.ToString("F2", CultureInfo.InvariantCulture) ?? "unavailable";
    Console.Error.WriteLine($"RunId={exception.RunId} Status=Faulted Stage={exception.Stage} ComputedScore={score}");
    Console.Error.WriteLine(exception.InnerException?.Message);
    return 1;
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
