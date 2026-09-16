using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace AppGuardian.Bootstrapper;

/// <summary>
/// Streams the AppGuardian payload from a remote URL to a local file, reporting progress and download speed.
/// </summary>
/// <remarks>
/// The download is streamed directly to disk to avoid holding the full payload in memory. Progress and speed
/// are computed using a running average over a 500 ms window and reported back to the caller via
/// <see cref="DownloadProgress"/>.
/// </remarks>
public sealed class PayloadDownloader
{
    private readonly HttpClient _http;
    private readonly DownloadProgress _progress;
    private readonly CancellationToken _token;
    private readonly Func<string, bool>? _secondaryVerifier;

    private const int BufferSize = 64 * 1024; // 64 KB chunks

    public PayloadDownloader(
        HttpClient http,
        DownloadProgress progress,
        CancellationToken token,
        Func<string, bool>? secondaryVerifier = null)
    {
        _http = http;
        _progress = progress;
        _token = token;
        _secondaryVerifier = secondaryVerifier;
    }

    /// <summary>
    /// Downloads the payload to <paramref name="destinationPath"/>, streaming to disk.
    /// </summary>
    /// <exception cref="OperationCanceledException">If the token is cancelled.</exception>
    /// <exception cref="HttpRequestException">If the server returns an error status.</exception>
    public async Task DownloadAsync(string url, string destinationPath)
    {
        _token.ThrowIfCancellationRequested();

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            uri.AbsolutePath.Contains("/latest/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Bootstrapper payload URLs must use HTTPS and an immutable versioned release path.");
        }

        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, _token);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        var contentStream = await response.Content.ReadAsStreamAsync(_token);

        using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, true);

        var buffer = new byte[BufferSize];
        var totalRead = 0L;
        var lastReportTime = Stopwatch.GetTimestamp();
        var lastReportBytes = 0L;

        int bytesRead;
        while ((bytesRead = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), _token)) != 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), _token);
            totalRead += bytesRead;

            // Report progress and speed every 500 ms, or on the last chunk.
            var now = Stopwatch.GetTimestamp();
            var elapsed = (now - lastReportTime) / (double)Stopwatch.Frequency;

            if (elapsed >= 0.5)
            {
                if (totalBytes > 0)
                {
                    var percent = (float)((double)totalRead / totalBytes * 100);
                    _progress.ReportProgress(percent);
                }

                var bytesSinceLastReport = totalRead - lastReportBytes;
                var speedMbs = bytesSinceLastReport / elapsed / (1024.0 * 1024.0);
                _progress.ReportSpeed($"{speedMbs:F1} MB/s");

                lastReportTime = now;
                lastReportBytes = totalRead;
            }
        }

        await fileStream.FlushAsync(_token);

        // Optional defense in depth for signed releases. The mandatory SHA-256 comparison remains
        // the release trust anchor and is performed by the caller after this download returns.
        if (_secondaryVerifier is not null && !_secondaryVerifier(destinationPath))
        {
            throw new CryptographicException("The downloaded installer did not pass publisher verification.");
        }

        // Final progress: 100%
        _progress.ReportProgress(100f);
        _progress.ReportSpeed("Complete");
    }
}
