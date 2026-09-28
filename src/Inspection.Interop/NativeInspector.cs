using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Inspection.Core;

namespace Inspection.Interop;

public sealed record NativeInspectionProgress(int CompletedSamples, int TotalSamples);

public sealed class NativeInspector : IInspector, IDisposable, IAsyncDisposable
{
    [ThreadStatic] private static NativeInspector? _callbackOwner;
    private readonly object _gate = new();
    private readonly INativeInspectionApi _api;
    private readonly NativeInspectorHandle _handle;
    private readonly Action<NativeInspectionProgress>? _progress;
    private Operation? _active;
    private InspectionTerminationException? _fault;
    private bool _disposed;
    private Task? _disposeTask;

    public ulong ProgressCallbackCount => _api.ProgressCallbacks(_handle);

    public NativeInspector(NativeFaultMode faultMode = NativeFaultMode.None, Action<NativeInspectionProgress>? progress = null,
        INativeInspectionApi? api = null)
    {
        if (!Enum.IsDefined(faultMode)) { throw new ArgumentOutOfRangeException(nameof(faultMode)); }
        _api = api ?? PInvokeInspectionApi.Instance;
        if (_api.AbiVersion() != 2 || _api.ResultSize() != Marshal.SizeOf<NativeInspectionResult>())
        {
            throw new InvalidOperationException("Unsupported NativeInspection ABI or result layout.");
        }
        NativeStatus status = _api.Create(faultMode, out NativeInspectorHandle handle);
        if (status != NativeStatus.Ok || handle.IsInvalid)
        {
            handle.Dispose();
            throw new NativeInspectionException("Create", status == NativeStatus.Ok ? NativeStatus.InternalError : status);
        }
        _handle = handle;
        _progress = progress;
    }

    public Task<InspectionAssessment> InspectAsync(InspectionJob job, ReadOnlyMemory<double> samples, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_fault is not null) { throw new InvalidOperationException("The native adapter is faulted; restart the Host.", _fault); }
            if (_active is not null) { throw new InvalidOperationException("The native adapter already has an active operation."); }
            ArgumentNullException.ThrowIfNull(job);
            cancellationToken.ThrowIfCancellationRequested();
            var operation = new Operation(this, job, samples.ToArray(), cancellationToken);
            _active = operation;
            // Keep the blocking native join off the ThreadPool.
            try { _ = Task.Factory.StartNew(operation.Execute, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default); }
            catch { _active = null; throw; }
            return operation.Completion.Task;
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        if (_callbackOwner == this) { throw new InvalidOperationException("Dispose cannot run inside this adapter's progress callback."); }
        lock (_gate)
        {
            if (_disposeTask is not null) { return new(_disposeTask); }
            _disposed = true;
            _active?.RequestStop();
            _disposeTask = FinishDisposalAsync(_active?.Completion.Task ?? Task.CompletedTask);
            return new(_disposeTask);
        }
    }

    private async Task FinishDisposalAsync(Task completion)
    {
        try { await completion.ConfigureAwait(false); }
        catch { /* Inspection errors remain observable through the operation task. */ }
        _handle.Dispose();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnProgress(nint context, int completed, int total)
    {
        // The context is valid until join, or for process lifetime on an unconfirmed join.
        var operation = (Operation)GCHandle.FromIntPtr(context).Target!;
        try { operation.Report(completed, total); }
        catch (Exception exception) { operation.RecordCallbackFailure(exception); }
    }

    private sealed class Operation(NativeInspector owner, InspectionJob job, double[] samples, CancellationToken token)
    {
        private readonly object _gate = new();
        private bool _started;
        private bool _finished;
        private bool _stopRequested;
        private Exception? _stopError;
        private Exception? _callbackError;
        internal TaskCompletionSource<InspectionAssessment> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void RequestStop()
        {
            lock (_gate)
            {
                if (_finished || _stopRequested) { return; }
                Volatile.Write(ref _stopRequested, true);
                if (_started) { DeliverStop(); }
            }
        }

        private void DeliverStop()
        {
            try
            {
                NativeStatus status = owner._api.RequestStop(owner._handle);
                if (status != NativeStatus.Ok) { _stopError = new NativeInspectionException("RequestStop", status); }
            }
            catch (Exception exception) { _stopError = exception; }
        }

        internal void Report(int completed, int total)
        {
            if (Volatile.Read(ref _stopRequested)) { return; }
            NativeInspector? previousOwner = _callbackOwner;
            _callbackOwner = owner;
            try { owner._progress?.Invoke(new(completed, total)); }
            finally { _callbackOwner = previousOwner; }
        }

        internal void RecordCallbackFailure(Exception exception)
        {
            _callbackError ??= exception;
            RequestStop();
        }

        internal unsafe void Execute()
        {
            bool retained = false;
            bool joined = false;
            GCHandle context = default;
            CancellationTokenRegistration registration = default;
            InspectionAssessment? assessment = null;
            Exception? error = null;
            try
            {
                owner._handle.DangerousAddRef(ref retained);
                context = GCHandle.Alloc(this);
                registration = token.Register(RequestStop);
                lock (_gate)
                {
                    if (_stopRequested) { throw new OperationCanceledException(token); }
                    NativeStatus status = owner._api.Start(owner._handle, samples,
                        job.LowerBound, job.UpperBound, (nint)(delegate* unmanaged[Cdecl]<nint, int, int, void>)&OnProgress, GCHandle.ToIntPtr(context));
                    if (status != NativeStatus.Ok) { throw new NativeInspectionException("Inspect", status); }
                    _started = true;
                }
                // Nothing may skip join after a successful Start.
                NativeStatus waitStatus;
                NativeStatus operationStatus;
                NativeInspectionResult result;
                try { waitStatus = owner._api.Wait(owner._handle, out operationStatus, out result); }
                catch (Exception exception) { RequestStop(); throw new InspectionTerminationException(false, exception); }
                if (waitStatus != NativeStatus.Ok)
                {
                    RequestStop();
                    throw new InspectionTerminationException(false, new NativeInspectionException("Wait", waitStatus));
                }
                joined = true;
                registration.Dispose();
                lock (_gate)
                {
                    _finished = true;
                    if (_stopError is not null) { throw new InspectionTerminationException(true, _stopError); }
                }
                if (_callbackError is not null) { throw new InvalidOperationException("Native progress observer failed.", _callbackError); }
                token.ThrowIfCancellationRequested();
                if (_stopRequested || operationStatus == NativeStatus.Canceled) { throw new OperationCanceledException(token); }
                if (operationStatus != NativeStatus.Ok) { throw new NativeInspectionException("Inspect", operationStatus); }
                if (result.SampleCount != samples.Length || result.DefectCount < 0 || result.DefectCount > result.SampleCount ||
                    result.SampleCount == 0 || !double.IsFinite(result.Score) ||
                    Math.Abs(result.Score - 100.0 * (result.SampleCount - result.DefectCount) / result.SampleCount) > 1e-9)
                {
                    throw new InvalidOperationException("NativeInspection returned inconsistent result data.");
                }
                assessment = new(result.SampleCount, result.DefectCount);
            }
            catch (Exception exception) { error = exception; }
            finally
            {
                registration.Dispose();
                lock (_gate) { _finished = true; }
                if (!_started || joined)
                {
                    if (context.IsAllocated) { context.Free(); }
                    if (retained) { owner._handle.DangerousRelease(); }
                }
                // If join is unconfirmed, retain BOTH the context root and SafeHandle reference.
                // Dispose/finalization must not invalidate work of unknown lifetime.
                lock (owner._gate)
                {
                    owner._fault = error as InspectionTerminationException;
                    owner._active = null;
                    if (error is not null) { Completion.SetException(error); }
                    else { Completion.SetResult(assessment!); }
                }
            }
        }
    }
}
