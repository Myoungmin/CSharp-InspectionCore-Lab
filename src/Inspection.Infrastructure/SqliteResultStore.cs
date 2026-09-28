using Inspection.Core;
using Microsoft.Data.Sqlite;

namespace Inspection.Infrastructure;

public sealed class SqliteResultStore : IResultStore, IResultReader, IResultSearch
{
    private const int ApplicationId = 0x494E5350;
    private readonly string _path;
    private readonly string _connectionString;
    private readonly object _initializationGate = new();
    private bool _initialized;

    public SqliteResultStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 2
        }.ToString();
    }

    public string DatabasePath => _path;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized();
    }, cancellationToken);

    public Task SaveAsync(InspectionResult result, CancellationToken cancellationToken) => Task.Run(() =>
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.RunId == Guid.Empty || string.IsNullOrWhiteSpace(result.JobId) || result.Assessment is null || result.InspectedAtUtc < result.StartedAtUtc)
        {
            throw new ArgumentException("Invalid persisted result.", nameof(result));
        }
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO results(run_id, job_id, started_ticks, inspected_ticks, sample_count, defect_count) VALUES ($id,$job,$started,$inspected,$samples,$defects)";
        command.Parameters.AddWithValue("$id", result.RunId.ToString("N"));
        command.Parameters.AddWithValue("$job", result.JobId);
        command.Parameters.AddWithValue("$started", result.StartedAtUtc.UtcTicks);
        command.Parameters.AddWithValue("$inspected", result.InspectedAtUtc.UtcTicks);
        command.Parameters.AddWithValue("$samples", result.Assessment.SampleCount);
        command.Parameters.AddWithValue("$defects", result.Assessment.DefectCount);
        cancellationToken.ThrowIfCancellationRequested();
        command.ExecuteNonQuery();
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        // A successful commit is authoritative; do not turn it into cancellation afterward.
    }, cancellationToken);

    public Task<InspectionResult?> LoadAsync(Guid runId, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_id,job_id,started_ticks,inspected_ticks,sample_count,defect_count FROM results WHERE run_id=$id";
        command.Parameters.AddWithValue("$id", runId.ToString("N"));
        using var reader = command.ExecuteReader();
        cancellationToken.ThrowIfCancellationRequested();
        return reader.Read() ? ReadResult(reader) : null;
    }, cancellationToken);

    public Task<InspectionResultPage> SearchAsync(InspectionResultQuery query, CancellationToken cancellationToken) => Task.Run(() =>
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized();
        using var connection = Open();
        using var command = connection.CreateCommand();
        var conditions = new List<string>();
        void Filter(string sql, string name, object value) { conditions.Add(sql); command.Parameters.AddWithValue(name, value); }
        if (query.FromUtc is { } from) { Filter("inspected_ticks >= $from", "$from", from.UtcTicks); }
        if (query.ToUtc is { } to) { Filter("inspected_ticks < $to", "$to", to.UtcTicks); }
        if (query.JobId is { } job) { Filter("job_id = $job", "$job", job); }
        if (query.Verdict is { } verdict) { conditions.Add(verdict == InspectionVerdict.Pass ? "defect_count = 0" : "defect_count > 0"); }
        if (query.Cursor is { } cursor)
        {
            Filter("(inspected_ticks < $cursorTime OR (inspected_ticks = $cursorTime AND run_id < $cursorId))", "$cursorTime", cursor.InspectedAtUtc.UtcTicks);
            command.Parameters.AddWithValue("$cursorId", cursor.RunId.ToString("N"));
        }
        command.CommandText = "SELECT run_id,job_id,started_ticks,inspected_ticks,sample_count,defect_count FROM results"
            + (conditions.Count == 0 ? "" : " WHERE " + string.Join(" AND ", conditions))
            + " ORDER BY inspected_ticks DESC, run_id COLLATE BINARY DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", query.Limit + 1);
        var items = new List<InspectionResult>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) { cancellationToken.ThrowIfCancellationRequested(); items.Add(ReadResult(reader)); }
        InspectionResultCursor? next = null;
        if (items.Count > query.Limit)
        {
            items.RemoveAt(items.Count - 1);
            next = new(items[^1].InspectedAtUtc, items[^1].RunId);
        }
        return new InspectionResultPage(items.AsReadOnly(), next);
    }, cancellationToken);

    private static InspectionResult ReadResult(SqliteDataReader reader) => new(Guid.ParseExact(reader.GetString(0), "N"),
        reader.GetString(1), new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero), new DateTimeOffset(reader.GetInt64(3), TimeSpan.Zero),
        new InspectionAssessment(reader.GetInt32(4), reader.GetInt32(5)));

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA synchronous=FULL";
            command.ExecuteNonQuery();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private void EnsureInitialized()
    {
        lock (_initializationGate)
        {
            if (_initialized) { return; }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using var connection = Open();
            using (var transaction = connection.BeginTransaction())
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "PRAGMA user_version";
                long version = (long)command.ExecuteScalar()!;
                command.CommandText = "PRAGMA application_id";
                long application = (long)command.ExecuteScalar()!;
                if (version == 0 && application == 0)
                {
                    command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'";
                    if ((long)command.ExecuteScalar()! != 0) { throw new InvalidDataException("Refusing to initialize an unknown database."); }
                    command.CommandText = """
                        CREATE TABLE results (
                            run_id TEXT NOT NULL PRIMARY KEY CHECK(length(run_id)=32),
                            job_id TEXT NOT NULL CHECK(length(job_id)>0),
                            started_ticks INTEGER NOT NULL CHECK(started_ticks>=0),
                            inspected_ticks INTEGER NOT NULL CHECK(inspected_ticks>=started_ticks AND inspected_ticks<=3155378975999999999),
                            sample_count INTEGER NOT NULL CHECK(sample_count>0 AND sample_count<=2147483647),
                            defect_count INTEGER NOT NULL CHECK(defect_count>=0 AND defect_count<=sample_count)
                        );
                        CREATE INDEX ix_results_time ON results(inspected_ticks DESC, run_id DESC);
                        CREATE INDEX ix_results_job_time ON results(job_id, inspected_ticks DESC, run_id DESC);
                        PRAGMA application_id=1229869904;
                        PRAGMA user_version=1;
                        """;
                    command.ExecuteNonQuery();
                }
                else if (version != 1 || application != ApplicationId)
                {
                    throw new InvalidDataException("Unsupported result database schema or application ID.");
                }
                transaction.Commit();
            }
            using var wal = connection.CreateCommand();
            wal.CommandText = "PRAGMA journal_mode=WAL";
            if (!string.Equals((string?)wal.ExecuteScalar(), "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("WAL mode is required for the result database.");
            }
            _initialized = true;
        }
    }
}
