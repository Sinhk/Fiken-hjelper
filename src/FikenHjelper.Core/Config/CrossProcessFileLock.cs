namespace FikenHjelper.Core.Config;

/// <summary>
/// Enkel lås på tvers av prosesser basert på en låsfil som åpnes med <see cref="FileShare.None"/>
/// (Windows: delingslås, Linux/macOS: flock). Brukes når flere prosesser (f.eks. Web og MCP-serveren)
/// deler samme datamappe, Fiken-innlogging og rate limit.
/// </summary>
public sealed class CrossProcessFileLock : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private FileStream? _stream;

    private CrossProcessFileLock(FileStream stream)
    {
        _stream = stream;
    }

    /// <summary>Den låste fila. Kan brukes til å lagre små mengder delt tilstand.</summary>
    public FileStream Stream => _stream ?? throw new ObjectDisposedException(nameof(CrossProcessFileLock));

    public static async Task<CrossProcessFileLock> AcquireAsync(
        string path,
        CancellationToken ct = default,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var deadline = DateTimeOffset.UtcNow + (timeout ?? DefaultTimeout);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
                return new CrossProcessFileLock(stream);
            }
            catch (Exception ex) when (IsPermanentFailure(ex))
            {
                // Feil sti eller manglende rettigheter blir ikke bedre av å vente.
                throw;
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(RetryDelay, ct);
            }
            catch (UnauthorizedAccessException) when (DateTimeOffset.UtcNow < deadline)
            {
                // Windows kan gi tilgangsfeil et kort øyeblikk mens en annen prosess holder fila.
                await Task.Delay(RetryDelay, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new TimeoutException(
                    $"Fikk ikke låsen '{Path.GetFileName(path)}' innen tidsfristen. Kjører en annen Fiken-hjelper-prosess som henger?",
                    ex);
            }
        }
    }

    private static bool IsPermanentFailure(Exception ex) =>
        ex is DirectoryNotFoundException or PathTooLongException or DriveNotFoundException
        // Bare Windows gir tilgangsfeil ved låskonflikt. På Linux/macOS betyr den manglende rettigheter.
        || (ex is UnauthorizedAccessException && !OperatingSystem.IsWindows());

    /// <summary>Leser et 64-biters tall fra starten av låsfila (0 hvis tom).</summary>
    public long ReadInt64()
    {
        var stream = Stream;
        if (stream.Length < sizeof(long))
        {
            return 0;
        }

        Span<byte> buffer = stackalloc byte[sizeof(long)];
        stream.Seek(0, SeekOrigin.Begin);
        stream.ReadExactly(buffer);
        return BitConverter.ToInt64(buffer);
    }

    /// <summary>Skriver et 64-biters tall til starten av låsfila.</summary>
    public void WriteInt64(long value)
    {
        var stream = Stream;
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        BitConverter.TryWriteBytes(buffer, value);
        stream.Seek(0, SeekOrigin.Begin);
        stream.Write(buffer);
        stream.Flush();
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _stream, null)?.Dispose();
    }
}
