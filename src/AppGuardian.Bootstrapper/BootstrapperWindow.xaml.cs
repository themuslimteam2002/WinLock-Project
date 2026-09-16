using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AppGuardian.Bootstrapper;

/// <summary>
/// Bootstrapper window that downloads, verifies, and launches the AppGuardian payload.
/// </summary>
/// <remarks>
/// This window is the entire UI of the bootstrapper. It is intentionally minimal: a progress bar,
/// a speed label, and a cancel button. The download and verification logic lives in PayloadDownloader
/// and SecurityVerifier, which report back through IProgress and the dispatcher.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed in OnClosed")]
public partial class BootstrapperWindow : Window
{
    private readonly CancellationTokenSource _cts = new();
    private readonly HttpClient _http;
    private readonly DownloadProgress _progress;

    /// <summary>
    /// Expected SHA-256 hash of the downloaded payload.
    /// </summary>
    /// <remarks>
    /// This value MUST be updated whenever a new payload is published. The marketing website
    /// embeds the same hash in the download page so the bootstrapper and the web download button
    /// agree on what is trusted.
    /// </remarks>
    // Stamped by scripts/Update-BootstrapperHash.ps1 for each immutable release payload.
    private const string ExpectedHash = "85BE2775AC86E39C28BF560270FE1CC1B03EAC0195FE209A063E1AC05D4568F2";

    /// <summary>
    /// Immutable release URL. Never use a mutable alias such as /latest/ for executable content.
    /// </summary>
    private const string PayloadUrl = "https://github.com/themuslimteam2002/WinLock-Project/releases/download/v0.1.0/AppGuardian-0.1.0-x64-setup.exe";

    // Empty during the unsigned MVP phase. Populate with the Authenticode subject after signing is enabled.
    private const string ExpectedPublisherSubject = "";

    public BootstrapperWindow()
    {
        InitializeComponent();

        // HttpClient is configured to match the download endpoint's TLS requirements.
        _http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        _progress = new DownloadProgress();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        StatusLabel.Text = "Preparing download...";

        if (ExpectedHash.Length != 64 || ExpectedHash.Contains('_', StringComparison.Ordinal))
        {
            ShowError("This bootstrapper was not stamped with a release integrity hash.");
            return;
        }

        var tempPath = Path.Combine(Path.GetTempPath(), "AppGuardian", "AppGuardian-0.1.0-x64-setup.exe");
        var tempDir = Path.GetDirectoryName(tempPath)!;

        if (Directory.Exists(tempDir))
        {
            Directory.Delete(tempDir, true);
        }

        Directory.CreateDirectory(tempDir);

        try
        {
            var verifier = new SecurityVerifier();
            var downloader = new PayloadDownloader(
                _http,
                _progress,
                _cts.Token,
                string.IsNullOrWhiteSpace(ExpectedPublisherSubject)
                    ? null
                    : path => verifier.VerifyAuthenticodeSignature(path, ExpectedPublisherSubject));

            // Wire up progress reporting. The downloader computes and reports speed internally;
            // we just bind the values here.
            _progress.ProgressChanged += OnProgressChanged;
            _progress.SpeedChanged += OnSpeedChanged;

            await downloader.DownloadAsync(PayloadUrl, tempPath);

            StatusLabel.Text = "Verifying integrity...";

            if (!await verifier.VerifyAsync(tempPath, ExpectedHash))
            {
                StatusLabel.Text = "Security verification failed.";
                ShowError("The downloaded file did not pass integrity verification. " +
                          "This could indicate a corrupted download or a man-in-the-middle attack.");
                return;
            }

            StatusLabel.Text = "Installing AppGuardian...";
            await verifier.ExecutePayloadAsync(tempPath, tempDir);
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = "Download cancelled.";
        }
        catch (Exception ex)
        {
            ShowError($"Failed to install AppGuardian: {ex.Message}");
        }
        finally
        {
            _cts.Dispose();
            _http.Dispose();
        }
    }

    private void OnProgressChanged(object? sender, float percent)
    {
        // Reported from the download loop, which is not guaranteed to be on the UI thread.
        Dispatcher.InvokeAsync(() => DownloadProgress.Value = percent);
    }

    private void OnSpeedChanged(object? sender, string speedText)
    {
        Dispatcher.InvokeAsync(() => SpeedLabel.Text = speedText);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        StatusLabel.Text = "Cancelling...";
        CancelButton.IsEnabled = false;
    }

    private void CancelButton_MouseEnter(object sender, MouseEventArgs e)
    {
        CancelButton.Background = new SolidColorBrush(Color.FromRgb(0x5A, 0x82, 0xF2));
    }

    private void CancelButton_MouseLeave(object sender, MouseEventArgs e)
    {
        CancelButton.Background = new SolidColorBrush(Color.FromRgb(0x82, 0x9A, 0xF2));
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _cts.Dispose();
        _http.Dispose();
    }

    private void ShowError(string message)
    {
        MessageBox.Show(message, "AppGuardian Installer", MessageBoxButton.OK, MessageBoxImage.Error);
        Close();
    }
}

/// <summary>
/// Holds download progress state and raises events for the UI to bind to.
/// </summary>
public sealed class DownloadProgress
{
    public event EventHandler<float>? ProgressChanged;
    public event EventHandler<string>? SpeedChanged;

    internal void ReportProgress(float percent)
    {
        ProgressChanged?.Invoke(this, percent);
    }

    internal void ReportSpeed(string speedText)
    {
        SpeedChanged?.Invoke(this, speedText);
    }
}
