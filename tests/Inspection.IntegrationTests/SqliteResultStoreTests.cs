using Inspection.Core;
using Inspection.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Inspection.IntegrationTests;

[TestClass]
[TestCategory("SQLite")]
public sealed class SqliteResultStoreTests
{
    private string _directory = null!;
    private string Database => Path.Combine(_directory, "inspection.db");
    private static readonly DateTimeOffset Epoch = DateTimeOffset.UnixEpoch;
    private static InspectionResult Result(int index, int defects = 0, string job = "sqlite-job") =>
        new(Guid.Parse($"00000000-0000-0000-0000-{index:D12}"), job, Epoch, Epoch.AddSeconds(index), new InspectionAssessment(3, defects));

    [TestInitialize]
    public void Initialize() => _directory = Path.Combine(Path.GetTempPath(), "InspectionLabTests", Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "InspectionLabTests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(_directory).StartsWith(root, StringComparison.OrdinalIgnoreCase)) { throw new InvalidOperationException("Invalid cleanup path."); }
        if (Directory.Exists(_directory)) { Directory.Delete(_directory, true); }
    }

    internal static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 2 }.ToString());
        connection.Open();
        return connection;
    }

    private async Task<SqliteResultStore> StoreAsync()
    {
        var store = new SqliteResultStore(Database);
        await store.InitializeAsync();
        return store;
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public async Task CommittedResult_SurvivesNewStoreAndConnection(int defects)
    {
        InspectionResult result = Result(1, defects);
        await (await StoreAsync()).SaveAsync(result, CancellationToken.None);
        Assert.AreEqual(result, await new SqliteResultStore(Database).LoadAsync(result.RunId, CancellationToken.None));
    }

    [TestMethod]
    public async Task DuplicateRunId_RejectsOverwriteAndRetainsOriginal()
    {
        var store = await StoreAsync();
        InspectionResult result = Result(1);
        await store.SaveAsync(result, CancellationToken.None);
        SqliteException error = await Assert.ThrowsExceptionAsync<SqliteException>(() => store.SaveAsync(result with { JobId = "replacement" }, CancellationToken.None));
        Assert.AreEqual(19, error.SqliteErrorCode);
        Assert.AreEqual(result, await store.LoadAsync(result.RunId, CancellationToken.None));
    }

    [TestMethod]
    public async Task Search_UsesInclusiveStartExclusiveEndVerdictAndExactParameterizedJob()
    {
        var store = await StoreAsync();
        string job = "a' OR 1=1 --";
        foreach (var result in new[] { Result(1, 1, job), Result(2, 0, job), Result(3, 1, job), Result(4, 1, "other") })
        {
            await store.SaveAsync(result, CancellationToken.None);
        }
        var query = new InspectionResultQuery(Epoch.AddSeconds(1).ToOffset(TimeSpan.FromHours(9)), Epoch.AddSeconds(3), InspectionVerdict.Fail, job);
        InspectionResultPage page = await store.SearchAsync(query, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { Result(1, 1, job) }, page.Items.ToArray());
        Assert.IsNull(page.NextCursor);
        Assert.IsNull(await store.LoadAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [TestMethod]
    public async Task CursorPagination_UsesRunIdTieBreakerWithoutDuplicates()
    {
        var store = await StoreAsync();
        for (int i = 1; i <= 5; i++) { await store.SaveAsync(Result(i) with { InspectedAtUtc = Epoch }, CancellationToken.None); }
        var ids = new List<Guid>();
        InspectionResultCursor? cursor = null;
        do
        {
            InspectionResultPage page = await store.SearchAsync(new(limit: 2, cursor: cursor), CancellationToken.None);
            ids.AddRange(page.Items.Select(r => r.RunId));
            cursor = page.NextCursor;
        } while (cursor is not null);
        CollectionAssert.AreEqual(Enumerable.Range(1, 5).Reverse().Select(i => Result(i).RunId).ToArray(), ids.ToArray());
    }

    [TestMethod]
    public async Task AbortedInsert_RollsBackAndStoreCanRecover()
    {
        var store = await StoreAsync();
        using var connection = Open(Database);
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_insert AFTER INSERT ON results BEGIN SELECT RAISE(ABORT,'Injected transaction failure'); END";
        command.ExecuteNonQuery();
        await Assert.ThrowsExceptionAsync<SqliteException>(() => store.SaveAsync(Result(1), CancellationToken.None));
        Assert.IsNull(await store.LoadAsync(Result(1).RunId, CancellationToken.None));
        command.CommandText = "DROP TRIGGER fail_insert";
        command.ExecuteNonQuery();
        await store.SaveAsync(Result(1), CancellationToken.None);
        Assert.AreEqual(Result(1), await store.LoadAsync(Result(1).RunId, CancellationToken.None));
    }

    [TestMethod]
    public async Task LockedWriter_FailsWithinDeadlineWithoutPublishingResult()
    {
        var store = await StoreAsync();
        using var connection = Open(Database);
        using var transaction = connection.BeginTransaction();
        SqliteException error = await Assert.ThrowsExceptionAsync<SqliteException>(() => store.SaveAsync(Result(1), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(8)));
        Assert.AreEqual(5, error.SqliteErrorCode);
        transaction.Rollback();
        Assert.IsNull(await store.LoadAsync(Result(1).RunId, CancellationToken.None));
    }

    [TestMethod]
    public async Task UncommittedInsert_IsInvisibleAndRollbackRemovesIt()
    {
        var store = await StoreAsync();
        using var connection = Open(Database);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO results VALUES($id,'pending',0,0,1,0)";
        Guid id = Guid.NewGuid();
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        command.ExecuteNonQuery();
        Assert.IsNull(await store.LoadAsync(id, CancellationToken.None));
        transaction.Rollback();
        Assert.AreEqual(0, (await store.SearchAsync(new(), CancellationToken.None)).Items.Count);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnknownSchema_IsRejectedWithoutChangingIt(bool futureVersion)
    {
        Directory.CreateDirectory(_directory);
        using var connection = Open(Database);
        using var command = connection.CreateCommand();
        command.CommandText = futureVersion ? "PRAGMA user_version=99; PRAGMA application_id=1229869904" : "CREATE TABLE foreign_data(value TEXT)";
        command.ExecuteNonQuery();
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => new SqliteResultStore(Database).InitializeAsync());
        command.CommandText = "PRAGMA user_version";
        Assert.AreEqual(futureVersion ? 99L : 0L, (long)command.ExecuteScalar()!);
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name='results'";
        Assert.AreEqual(0L, (long)command.ExecuteScalar()!);
    }

    [TestMethod]
    public async Task PreCanceledSave_DoesNotCreateDatabase()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => new SqliteResultStore(Database).SaveAsync(Result(1), stop.Token));
        Assert.IsFalse(Directory.Exists(_directory));
    }
}
