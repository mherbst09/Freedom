using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace ViennaAtomicClock.Weather;

public interface IWeatherProvider
{
    /// <summary>Name of the data source (shown with the required attribution).</summary>
    string Name { get; }

    /// <summary>Forecast, air quality and warnings for one place.</summary>
    Task<WeatherSnapshot> GetSnapshotAsync(WeatherLocation location, CancellationToken cancellation);

    /// <summary>Long-term values for today's date (null if the source has none).</summary>
    Task<ClimateNormals?> GetNormalsAsync(WeatherLocation location, CancellationToken cancellation);

    /// <summary>A layer sampled on a grid around the place for the weather map.</summary>
    Task<WeatherMapGrid> GetMapGridAsync(WeatherLocation center, WeatherMapLayer layer, CancellationToken cancellation);
}

public interface IGeocodingService
{
    Task<IReadOnlyList<WeatherLocation>> SearchAsync(string query, string language, CancellationToken cancellation);
    Task<WeatherLocation?> ReverseAsync(double latitude, double longitude, string language, CancellationToken cancellation);
}

public interface IWeatherCache
{
    WeatherSnapshot? Load(string locationId);
    void Save(WeatherSnapshot snapshot);
    void Remove(string locationId);
}

public interface ILocationService
{
    /// <summary>Device position or null if it is unavailable or not allowed.</summary>
    Task<(double Latitude, double Longitude)?> GetPositionAsync(CancellationToken cancellation);
}

public interface IWeatherNotificationService
{
    void Notify(string title, string message, AlertSeverity? severity);
}

/// <summary>Shared HTTP access: one client, timeout per request and retries with a growing delay.</summary>
public static class WeatherHttp
{
    public static readonly HttpClient Shared = Create();

    private static HttpClient Create()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        })
        {
            Timeout = Timeout.InfiniteTimeSpan // the per-request timeout below applies instead
        };
        // Tile and geocoding services ask for an identifying user agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Freedom/1.0 (Windows desktop weather page)");
        return client;
    }

    public static async Task<string> GetStringAsync(HttpClient client, string url, CancellationToken cancellation,
        int retries = 2, TimeSpan? timeout = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            limit.CancelAfter(timeout ?? TimeSpan.FromSeconds(12));
            try
            {
                using var response = await client.GetAsync(url, limit.Token).ConfigureAwait(false);
                if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
                    throw new HttpRequestException($"Server answered {(int)response.StatusCode}.", null, response.StatusCode);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (!cancellation.IsCancellationRequested && attempt < retries && IsTransient(error))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(700 * (attempt + 1) * (attempt + 1)), cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                throw new TimeoutException("The weather service did not answer in time.");
            }
        }
    }

    private static bool IsTransient(Exception error) => error switch
    {
        OperationCanceledException => true, // our own timeout
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: { } code } => (int)code >= 500 || code == HttpStatusCode.TooManyRequests,
        _ => false
    };
}

/// <summary>Last good snapshot per place as versioned JSON in %AppData%\ViennaAtomicClock\Weather\cache.</summary>
public sealed class WeatherFileCache : IWeatherCache
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
    private readonly string _folder;

    public WeatherFileCache(string? folder = null)
    {
        _folder = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ViennaAtomicClock", "Weather", "cache");
    }

    private string PathFor(string id) => Path.Combine(_folder, string.Concat(id.Where(char.IsLetterOrDigit)) + ".json");

    public WeatherSnapshot? Load(string locationId)
    {
        var path = PathFor(locationId);
        try
        {
            if (!File.Exists(path)) return null;
            var snapshot = JsonSerializer.Deserialize<WeatherSnapshot>(File.ReadAllText(path), Options);
            // Unknown versions or half-written files are treated like "no cache".
            if (snapshot is null || snapshot.Version != WeatherSnapshot.CurrentVersion || snapshot.Hourly.Count == 0) return null;
            return snapshot;
        }
        catch
        {
            return null;
        }
    }

    public void Save(WeatherSnapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(_folder);
            var path = PathFor(snapshot.Location.Id);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(snapshot, Options));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch
        {
            // Without a writable profile the page simply works without offline data.
        }
    }

    public void Remove(string locationId)
    {
        try { File.Delete(PathFor(locationId)); }
        catch { }
    }
}

public enum WeatherLoadError { None, Offline, Failed }

public sealed record WeatherLoadResult(WeatherSnapshot? Snapshot, bool FromCache, WeatherLoadError Error);

/// <summary>
/// Loads weather for the page: cached data first, fresh data in the background, at most one request per
/// place at a time and no more than one refresh per minute unless the user asks.
/// </summary>
public sealed class WeatherService
{
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(3);
    private static readonly TimeSpan NormalsLifetime = TimeSpan.FromDays(7);

    private readonly IWeatherProvider _provider;
    private readonly IWeatherCache _cache;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, Task<WeatherLoadResult>> _running = new();
    private readonly object _gate = new();

    public WeatherService(IWeatherProvider provider, IWeatherCache cache, Func<DateTimeOffset>? now = null)
    {
        _provider = provider;
        _cache = cache;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public IWeatherProvider Provider => _provider;

    public WeatherSnapshot? Cached(string locationId) => _cache.Load(locationId);

    public void Forget(string locationId) => _cache.Remove(locationId);

    public bool IsStale(WeatherSnapshot snapshot) => _now() - snapshot.FetchedAt > StaleAfter;

    public Task<WeatherLoadResult> RefreshAsync(WeatherLocation location, bool force, CancellationToken cancellation)
    {
        lock (_gate)
        {
            // A second click while a request is running joins the running request.
            if (_running.TryGetValue(location.Id, out var running)) return running;
            var task = RefreshCoreAsync(location, force, cancellation);
            _running[location.Id] = task;
            task.ContinueWith(_ => { lock (_gate) _running.Remove(location.Id); }, TaskScheduler.Default);
            return task;
        }
    }

    private async Task<WeatherLoadResult> RefreshCoreAsync(WeatherLocation location, bool force, CancellationToken cancellation)
    {
        await Task.Yield();
        var cached = _cache.Load(location.Id);
        if (!force && cached is not null && _now() - cached.FetchedAt < MinimumInterval)
            return new WeatherLoadResult(cached, true, WeatherLoadError.None);

        try
        {
            var snapshot = await _provider.GetSnapshotAsync(location, cancellation).ConfigureAwait(false);
            snapshot.Location = location;
            if (string.IsNullOrEmpty(location.TimeZoneId)) location.TimeZoneId = snapshot.TimeZoneId;

            if (cached?.Normals is { } normals && _now() - normals.Computed < NormalsLifetime) snapshot.Normals = normals;
            else
            {
                try { snapshot.Normals = await _provider.GetNormalsAsync(location, cancellation).ConfigureAwait(false); }
                catch (Exception error) when (error is not OperationCanceledException) { snapshot.Normals = cached?.Normals; }
            }

            _cache.Save(snapshot);
            return new WeatherLoadResult(snapshot, false, WeatherLoadError.None);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            var offline = error is HttpRequestException { StatusCode: null } or TimeoutException
                          || !System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();
            return new WeatherLoadResult(cached, cached is not null, offline ? WeatherLoadError.Offline : WeatherLoadError.Failed);
        }
    }
}
