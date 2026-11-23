using System.Net;
using FikenHjelper.Core.Config;

namespace FikenHjelper.Core.Fiken;

/// <summary>
/// Thread-safe singleton coordinator enforcing Fiken API v2 limits:
/// maximum 1 concurrent request and minimum spacing between requests.
/// Med en låsfil gjelder grensene også på tvers av prosesser (f.eks. Web og MCP-serveren samtidig):
/// låsen holdes under hele kallet, og tidspunktet for siste kall lagres i fila.
/// </summary>
public sealed class FikenRateLimiter : IDisposable
{
    public const string LockFileName = "fiken-api.lock";
    public static readonly TimeSpan DefaultMinimumSpacing = TimeSpan.FromMilliseconds(260);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _minimumSpacing;
    private readonly string? _lockFilePath;
    private DateTimeOffset _nextAllowedRequestUtc = DateTimeOffset.MinValue;
    private CrossProcessFileLock? _currentFileLock;

    public FikenRateLimiter()
        : this(DefaultMinimumSpacing)
    {
    }

    public FikenRateLimiter(TimeSpan minimumSpacing)
        : this(minimumSpacing, lockFilePath: null)
    {
    }

    public FikenRateLimiter(TimeSpan minimumSpacing, string? lockFilePath)
    {
        _minimumSpacing = minimumSpacing < TimeSpan.Zero ? TimeSpan.Zero : minimumSpacing;
        _lockFilePath = string.IsNullOrWhiteSpace(lockFilePath) ? null : lockFilePath;
    }

    /// <summary>Rate limiter som deler grensene med andre prosesser som bruker samme datamappe.</summary>
    public static FikenRateLimiter CreateShared(string dataDirectory) =>
        new(DefaultMinimumSpacing, Path.Combine(dataDirectory, LockFileName));

    public async Task<IDisposable> AcquireSlotAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        if (_lockFilePath is not null)
        {
            try
            {
                _currentFileLock = await CrossProcessFileLock.AcquireAsync(_lockFilePath, cancellationToken);
            }
            catch
            {
                _gate.Release();
                throw;
            }
        }

        return new SlotLease(this);
    }

    public async Task WaitForSpacingAsync(CancellationToken cancellationToken)
    {
        var nextAllowed = _nextAllowedRequestUtc;
        if (_currentFileLock is { } fileLock)
        {
            var sharedTicks = fileLock.ReadInt64();
            if (sharedTicks > 0)
            {
                var shared = new DateTimeOffset(sharedTicks, TimeSpan.Zero);
                if (shared > nextAllowed)
                {
                    nextAllowed = shared;
                }
            }
        }

        var now = DateTimeOffset.UtcNow;
        if (nextAllowed > now)
        {
            // Beskytter mot en klokke som er stilt bakover: aldri vent mer enn ett mellomrom.
            var wait = nextAllowed - now;
            if (wait > _minimumSpacing)
            {
                wait = _minimumSpacing;
            }

            await Task.Delay(wait, cancellationToken);
        }
    }

    public void RecordRequestCompleted()
    {
        _nextAllowedRequestUtc = DateTimeOffset.UtcNow + _minimumSpacing;
        _currentFileLock?.WriteInt64(_nextAllowedRequestUtc.UtcTicks);
    }

    public void Dispose()
    {
        _currentFileLock?.Dispose();
        _gate.Dispose();
    }

    private void ReleaseSlot()
    {
        var fileLock = _currentFileLock;
        _currentFileLock = null;
        fileLock?.Dispose();
        _gate.Release();
    }

    private sealed class SlotLease(FikenRateLimiter owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.ReleaseSlot();
            }
        }
    }
}

/// <summary>
/// Transient HTTP message handler delegating concurrency & rate limiting to <see cref="FikenRateLimiter"/>.
/// </summary>
public sealed class FikenRateLimitHandler : DelegatingHandler
{
    private const int MaxRateLimitRetries = 2;
    private readonly FikenRateLimiter _limiter;
    private readonly bool _ownsLimiter;

    public FikenRateLimitHandler(FikenRateLimiter limiter)
    {
        _limiter = limiter ?? throw new ArgumentNullException(nameof(limiter));
        _ownsLimiter = false;
    }

    public FikenRateLimitHandler(TimeSpan minimumSpacing)
    {
        _limiter = new FikenRateLimiter(minimumSpacing);
        _ownsLimiter = true;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var _ = await _limiter.AcquireSlotAsync(cancellationToken);

        byte[]? bufferedContent = null;
        string? bufferedMediaType = null;
        if (request.Content is not null)
        {
            bufferedContent = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            bufferedMediaType = request.Content.Headers.ContentType?.ToString();
        }

        for (var attempt = 0; ; attempt++)
        {
            await _limiter.WaitForSpacingAsync(cancellationToken);

            using var outgoing = CloneRequest(request, bufferedContent, bufferedMediaType);
            var response = await base.SendAsync(outgoing, cancellationToken);
            _limiter.RecordRequestCompleted();

            if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt >= MaxRateLimitRetries)
            {
                return response;
            }

            var retryDelay = ResolveRetryDelay(response);
            response.Dispose();
            if (retryDelay > TimeSpan.Zero)
            {
                await Task.Delay(retryDelay, cancellationToken);
            }
        }
    }

    private static TimeSpan ResolveRetryDelay(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                return wait;
            }
        }

        return TimeSpan.FromSeconds(1);
    }

    private static HttpRequestMessage CloneRequest(
        HttpRequestMessage source,
        byte[]? bufferedContent,
        string? bufferedMediaType)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri)
        {
            Version = source.Version,
            VersionPolicy = source.VersionPolicy
        };

        foreach (var header in source.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (bufferedContent is not null)
        {
            var content = new ByteArrayContent(bufferedContent);
            if (!string.IsNullOrWhiteSpace(bufferedMediaType))
            {
                content.Headers.TryAddWithoutValidation("Content-Type", bufferedMediaType);
            }
            else if (source.Content is not null)
            {
                foreach (var header in source.Content.Headers)
                {
                    content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            clone.Content = content;
        }

        return clone;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _ownsLimiter)
        {
            _limiter.Dispose();
        }

        base.Dispose(disposing);
    }
}
