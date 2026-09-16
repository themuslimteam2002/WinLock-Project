using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace AppGuardian.Service.Discovery;

/// <summary>
/// Extracts an application icon as a base64 PNG. FR-404.
/// </summary>
/// <remarks>
/// PNG rather than the native ICO because the dashboard renders it directly and WPF's ICO decoding
/// picks an arbitrary frame from a multi-resolution icon, often the 16×16 one, which looks wrong at
/// list size.
/// <para>
/// Icons are fetched one at a time through <c>apps.getIcon</c>, never bundled into a list response:
/// a 32×32 PNG is roughly 2 KB, so two hundred apps would be 400 KB and would exceed the 256 KB
/// envelope cap (SC-15c). That is the whole reason this is a separate message.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class IconExtractor
{
    public static string? TryExtractPng(string executablePath, ILogger log)
    {
        try
        {
            if (!File.Exists(executablePath))
            {
                return null;
            }

            using var icon = Icon.ExtractAssociatedIcon(executablePath);

            if (icon is null)
            {
                return null;
            }

            // ToBitmap loses the alpha channel on some icon formats, but the alternative — decoding
            // the PNG-compressed frame of a Vista-era icon by hand — is far more code than a slightly
            // imperfect edge justifies for a 32-pixel list thumbnail.
            using var bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();

            bitmap.Save(stream, ImageFormat.Png);

            return Convert.ToBase64String(stream.ToArray());
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // A missing icon is cosmetic. Returning null lets the UI show a placeholder rather than
            // failing the whole app list over one unreadable resource.
            log.LogDebug(ex, "Icon for {Path} could not be extracted.", executablePath);
            return null;
        }
    }
}
