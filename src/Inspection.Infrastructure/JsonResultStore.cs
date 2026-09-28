using System.Text.Json;
using System.Text.Json.Serialization;
using Inspection.Core;

namespace Inspection.Infrastructure;

public sealed class JsonResultStore : IResultStore, IResultReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _directory;

    public JsonResultStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
    }

    public string GetResultPath(Guid runId) => Path.Combine(_directory, $"{runId:N}.json");

    public async Task<InspectionResult?> LoadAsync(Guid runId, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(GetResultPath(runId), FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.Asynchronous);
            var result = await JsonSerializer.DeserializeAsync<InspectionResult>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (result is null || result.RunId != runId || result.Assessment is null)
            {
                throw new InvalidDataException("Stored result identity or assessment is invalid.");
            }
            return result;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public async Task SaveAsync(InspectionResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_directory);
        string temporaryPath = Path.Combine(_directory, $"{result.RunId:N}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, result, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, GetResultPath(result.RunId), overwrite: false);
        }
        finally
        {
            // Cleanup failure must not hide the original persistence failure.
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
