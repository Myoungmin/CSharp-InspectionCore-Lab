namespace Inspection.Core;

public sealed class InspectionRunner
{
    private readonly IDevice _device;
    private readonly IInspector _inspector;
    private readonly IResultStore _store;
    private readonly TimeProvider _timeProvider;

    public InspectionRunner(IDevice device, IInspector inspector, IResultStore store, TimeProvider? timeProvider = null)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<InspectionResult> RunAsync(InspectionJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        Guid runId = Guid.NewGuid();
        DateTimeOffset startedAt = _timeProvider.GetUtcNow();
        InspectionStage stage = InspectionStage.Prepare;
        InspectionResult? computedResult = null;
        using var boundary = new PersistenceBoundary(cancellationToken);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _device.PrepareAsync(job, cancellationToken).ConfigureAwait(false);

            stage = InspectionStage.Acquire;
            cancellationToken.ThrowIfCancellationRequested();
            double[] samples = await _device.AcquireAsync(job, cancellationToken).ConfigureAwait(false);

            stage = InspectionStage.Inspect;
            cancellationToken.ThrowIfCancellationRequested();
            InspectionAssessment assessment = await _inspector.InspectAsync(job, samples, cancellationToken).ConfigureAwait(false);
            computedResult = new InspectionResult(runId, job.JobId, startedAt, _timeProvider.GetUtcNow(), assessment);

            // Once persistence wins, late caller cancellation cannot change the outcome.
            boundary.EnterPersistence();
            stage = InspectionStage.Persist;
            await _store.SaveAsync(computedResult, CancellationToken.None).ConfigureAwait(false);
            return computedResult;
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested && !boundary.IsPersisting)
        {
            throw new InspectionCanceledException(runId, stage, exception, cancellationToken);
        }
        catch (Exception exception)
        {
            throw new InspectionRunException(runId, stage, computedResult, exception);
        }
    }

    private sealed class PersistenceBoundary : IDisposable
    {
        private const int Open = 0;
        private const int Canceled = 1;
        private const int Persisting = 2;
        private readonly CancellationToken _token;
        private readonly CancellationTokenRegistration _registration;
        private int _state;

        public PersistenceBoundary(CancellationToken token)
        {
            _token = token;
            _registration = token.Register(() => Interlocked.CompareExchange(ref _state, Canceled, Open));
        }

        public bool IsPersisting => Volatile.Read(ref _state) == Persisting;

        public void EnterPersistence()
        {
            if (_token.IsCancellationRequested)
            {
                Interlocked.CompareExchange(ref _state, Canceled, Open);
            }

            if (Interlocked.CompareExchange(ref _state, Persisting, Open) != Open)
            {
                throw new OperationCanceledException(_token);
            }
        }

        public void Dispose() => _registration.Dispose();
    }
}
