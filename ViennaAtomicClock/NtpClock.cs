using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace ViennaAtomicClock;

internal sealed record NtpSyncResult(
    bool Success,
    DateTimeOffset CorrectedUtc,
    TimeSpan Offset,
    TimeSpan RoundTrip,
    string Server,
    string? Error = null);

internal static class NtpClient
{
    private static readonly string[] Servers =
    {
        "time.cloudflare.com",
        "time.google.com",
        "pool.ntp.org"
    };

    public static async Task<NtpSyncResult> SynchronizeAsync(CancellationToken cancellationToken)
    {
        var tasks = Servers.Select(server => QueryAsync(server, cancellationToken)).ToArray();
        var results = await Task.WhenAll(tasks);
        var successful = results.Where(r => r.Success).OrderBy(r => r.Offset).ToList();

        if (successful.Count == 0)
        {
            return new NtpSyncResult(false, DateTimeOffset.UtcNow, TimeSpan.Zero, TimeSpan.Zero,
                "Windows-Systemzeit", "Keine Antwort von den Zeitservern");
        }

        // The median offset rejects a single noisy or incorrect server response.
        var medianOffset = successful[successful.Count / 2].Offset;
        var best = successful.OrderBy(r => r.RoundTrip).First();
        return best with
        {
            CorrectedUtc = DateTimeOffset.UtcNow + medianOffset,
            Offset = medianOffset,
            Server = successful.Count > 1 ? $"{successful.Count} NTP-Server" : best.Server
        };
    }

    private static async Task<NtpSyncResult> QueryAsync(string server, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));

            var addresses = await Dns.GetHostAddressesAsync(server, timeout.Token);
            var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                          ?? addresses.First();

            using var udp = new UdpClient(address.AddressFamily);
            udp.Connect(new IPEndPoint(address, 123));

            var request = new byte[48];
            request[0] = 0x23; // Leap 0, NTP v4, client mode.

            var t1 = DateTimeOffset.UtcNow;
            WriteTimestamp(request, 40, t1);
            await udp.SendAsync(request, timeout.Token);
            var response = await udp.ReceiveAsync(timeout.Token);
            var t4 = DateTimeOffset.UtcNow;

            if (response.Buffer.Length < 48)
                throw new InvalidDataException("Ungültige NTP-Antwort");

            var t2 = ReadTimestamp(response.Buffer, 32);
            var t3 = ReadTimestamp(response.Buffer, 40);
            var offset = TimeSpan.FromTicks(((t2 - t1).Ticks + (t3 - t4).Ticks) / 2);
            var roundTrip = (t4 - t1) - (t3 - t2);
            if (roundTrip < TimeSpan.Zero) roundTrip = TimeSpan.Zero;

            return new NtpSyncResult(true, t4 + offset, offset, roundTrip, server);
        }
        catch (Exception ex)
        {
            return new NtpSyncResult(false, DateTimeOffset.UtcNow, TimeSpan.Zero, TimeSpan.Zero, server, ex.Message);
        }
    }

    private static DateTimeOffset ReadTimestamp(byte[] buffer, int offset)
    {
        var seconds = ReadUInt32BigEndian(buffer, offset);
        var fraction = ReadUInt32BigEndian(buffer, offset + 4);
        var ticks = (long)seconds * TimeSpan.TicksPerSecond
                    + (long)(fraction * (double)TimeSpan.TicksPerSecond / uint.MaxValue);
        return new DateTimeOffset(new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc)).AddTicks(ticks);
    }

    private static void WriteTimestamp(byte[] buffer, int offset, DateTimeOffset value)
    {
        var elapsed = value.UtcDateTime - new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var seconds = (uint)elapsed.TotalSeconds;
        var fractionalSeconds = elapsed.TotalSeconds - Math.Floor(elapsed.TotalSeconds);
        var fraction = (uint)(fractionalSeconds * uint.MaxValue);
        WriteUInt32BigEndian(buffer, offset, seconds);
        WriteUInt32BigEndian(buffer, offset + 4, fraction);
    }

    private static uint ReadUInt32BigEndian(byte[] buffer, int offset) =>
        ((uint)buffer[offset] << 24) |
        ((uint)buffer[offset + 1] << 16) |
        ((uint)buffer[offset + 2] << 8) |
        buffer[offset + 3];

    private static void WriteUInt32BigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }
}

internal sealed class PreciseClock
{
    private readonly object _gate = new();
    private DateTimeOffset _baseUtc = DateTimeOffset.UtcNow;
    private long _baseTimestamp = Stopwatch.GetTimestamp();

    public DateTimeOffset UtcNow
    {
        get
        {
            lock (_gate)
            {
                var elapsedTicks = Stopwatch.GetTimestamp() - _baseTimestamp;
                return _baseUtc + TimeSpan.FromSeconds(elapsedTicks / (double)Stopwatch.Frequency);
            }
        }
    }

    public void Set(DateTimeOffset correctedUtc)
    {
        lock (_gate)
        {
            _baseUtc = correctedUtc;
            _baseTimestamp = Stopwatch.GetTimestamp();
        }
    }
}
