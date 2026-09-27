using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Inspection.Client;
using Inspection.Contracts;

namespace Inspection.IntegrationTests;

internal sealed class IpcProcessFixture : IAsyncDisposable
{
    private readonly Process _host;
    private readonly Task<string> _errors;
    private Task<string> _output = Task.FromResult("");
    private string _ready = "";
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(20));
    internal string PipeName { get; } = "InspectionTest_" + Guid.NewGuid().ToString("N");
    internal string DirectoryPath { get; }
    internal string ResultsPath => Path.Combine(DirectoryPath, "results");
    internal string Root { get; }
    internal CancellationToken Token => _deadline.Token;
    internal string Configuration => new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;

    private IpcProcessFixture(string inspector, int delay, bool blockedStorage)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "InspectionLab.sln"))) { directory = directory.Parent; }
        Root = directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        DirectoryPath = Path.Combine(Root, "artifacts", "ipc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        if (blockedStorage) { File.WriteAllText(ResultsPath, "Storage failure fixture"); }
        _host = Start("Inspection.Host", "--serve", "--pipe", PipeName, "--inspector", inspector,
            "--device-delay-ms", delay.ToString(System.Globalization.CultureInfo.InvariantCulture), "--output", ResultsPath);
        _errors = _host.StandardError.ReadToEndAsync();
    }

    internal static async Task<IpcProcessFixture> LaunchAsync(string inspector = "managed", int delay = 0, bool blockedStorage = false)
    {
        var fixture = new IpcProcessFixture(inspector, delay, blockedStorage);
        try
        {
            string? ready = await fixture._host.StandardOutput.ReadLineAsync(fixture.Token);
            Assert.IsNotNull(ready, "Host exited before readiness: " + (fixture._host.HasExited ? await fixture._errors : ""));
            StringAssert.StartsWith(ready, "Listening Pipe=" + fixture.PipeName);
            fixture._ready = $"HostProcessId={fixture._host.Id}\n{ready}\n";
            fixture._output = fixture._host.StandardOutput.ReadToEndAsync();
            return fixture;
        }
        catch { await fixture.DisposeAsync(); throw; }
    }

    internal Process Start(string project, params string[] arguments)
    {
        string dll = Path.Combine(Root, "src", project, "bin", Configuration, "net9.0", project + ".dll");
        Assert.IsTrue(File.Exists(dll), "Required process binary is missing: " + dll);
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.ArgumentList.Add(dll);
        foreach (string argument in arguments) { info.ArgumentList.Add(argument); }
        return Process.Start(info) ?? throw new InvalidOperationException("Cannot launch " + project);
    }

    internal Task<InspectionConnection> ConnectAsync() => InspectionConnection.ConnectAsync(PipeName, Token);
    internal async Task<NamedPipeClientStream> RawAsync()
    {
        var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(Token); return pipe; }
        catch { pipe.Dispose(); throw; }
    }

    internal async Task<ResponseEnvelope> SendAsync<T>(string type, T payload, Guid? id = null)
    {
        await using var connection = await ConnectAsync();
        return await connection.SendAsync(InspectionConnection.CreateRequest(type, payload, id), Token);
    }

    internal async Task<StatusDto> UntilAsync(Func<StatusDto, bool> condition, GetStatusRequest? query = null)
    {
        await using var connection = await ConnectAsync();
        while (true)
        {
            StatusDto status = Payload<StatusDto>(await connection.SendAsync(InspectionConnection.CreateRequest("GetStatus", query ?? new()), Token));
            if (condition(status)) { return status; }
            // Poll an observable protocol state; elapsed time never substitutes for a state assertion.
            await Task.Delay(10, Token);
        }
    }

    internal static T Payload<T>(ResponseEnvelope response)
    {
        Assert.IsNull(response.ErrorCode, response.ErrorMessage);
        return response.Payload!.Value.Deserialize<T>() ?? throw new AssertFailedException("Missing payload.");
    }

    internal static byte[] Frame(object request)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(request);
        byte[] frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    internal async Task<ResponseEnvelope> ReadAsync(Stream pipe)
    {
        byte[] header = new byte[4];
        await pipe.ReadExactlyAsync(header, Token);
        int size = BinaryPrimitives.ReadInt32LittleEndian(header);
        Assert.IsTrue(size is > 0 and <= Protocol.MaxFrameBytes);
        byte[] body = new byte[size];
        await pipe.ReadExactlyAsync(body, Token);
        return JsonSerializer.Deserialize<ResponseEnvelope>(body)!;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_host.HasExited)
            {
                await _host.StandardInput.WriteLineAsync("exit");
                await _host.StandardInput.FlushAsync();
                await _host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            string errors = await _errors;
            await File.WriteAllTextAsync(Path.Combine(DirectoryPath, "host.stderr.log"), errors);
            await File.WriteAllTextAsync(Path.Combine(DirectoryPath, "host.stdout.log"), _ready + await _output);
            Assert.AreEqual(0, _host.ExitCode, errors);
        }
        finally
        {
            if (!_host.HasExited) { _host.Kill(entireProcessTree: true); await _host.WaitForExitAsync(); }
            _host.Dispose();
            _deadline.Dispose();
        }
    }
}
