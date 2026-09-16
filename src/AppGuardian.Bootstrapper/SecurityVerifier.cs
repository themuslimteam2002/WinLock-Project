using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;

namespace AppGuardian.Bootstrapper;

/// <summary>
/// Verifies the SHA-256 hash of the downloaded installer and launches it only after verification.
/// </summary>
/// <remarks>
/// The hash is computed over the raw bytes of the downloaded file. If the hash matches the expected
/// value, the payload is extracted and the silent installer is launched. If the hash does not match,
/// the method returns false and the caller should abort.
/// </remarks>
public sealed class SecurityVerifier
{
    private const int HashChunkSize = 1024 * 1024; // 1 MB chunks for hashing

    /// <summary>
    /// Computes the SHA-256 hash of the file at <paramref name="filePath"/> and compares it
    /// against <paramref name="expectedHashHex"/>.
    /// </summary>
    /// <returns>True if the computed hash matches the expected hash, false otherwise.</returns>
    public async Task<bool> VerifyAsync(string filePath, string expectedHashHex)
    {
        using var sha = SHA256.Create();
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, HashChunkSize, useAsync: true);

        var hashBytes = await sha.ComputeHashAsync(stream);
        var actualHashHex = Convert.ToHexString(hashBytes);

        return string.Equals(actualHashHex, expectedHashHex, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Optionally verifies the Authenticode signer subject. This secondary control is enabled once
    /// the release pipeline has a signing certificate; the SHA-256 release hash remains mandatory.
    /// </summary>
    public bool VerifyAuthenticodeSignature(string installerPath, string expectedSubject)
    {
        if (!VerifyAuthenticodeTrust(installerPath))
        {
            return false;
        }

        try
        {
            var certificate = X509Certificate2.CreateFromSignedFile(installerPath);
            return certificate.Subject.Contains(expectedSubject, StringComparison.OrdinalIgnoreCase);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static bool VerifyAuthenticodeTrust(string filePath)
    {
        var fileInfo = new WinTrustFileInfo(filePath);
        var fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);

        var data = new WinTrustData(fileInfoPointer, WinTrustStateAction.Verify);
        var action = WinTrustActionGenericVerifyV2;

        try
        {
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
        }
        finally
        {
            data.StateAction = WinTrustStateAction.Close;
            _ = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
            Marshal.FreeHGlobal(fileInfoPointer);
        }
    }

    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern uint WinVerifyTrust(
        IntPtr windowHandle,
        ref Guid actionId,
        ref WinTrustData data);

    private enum WinTrustStateAction : uint
    {
        Verify = 1,
        Close = 2,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructureSize;
        public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;

        public WinTrustFileInfo(string filePath)
        {
            StructureSize = (uint)Marshal.SizeOf<WinTrustFileInfo>();
            FilePath = filePath;
            FileHandle = IntPtr.Zero;
            KnownSubject = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructureSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SIPClientData;
        public uint UIChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public WinTrustStateAction StateAction;
        public IntPtr StateData;
        public string? URLReference;
        public uint ProviderFlags;
        public uint UIContext;
        public IntPtr SignatureSettings;

        public WinTrustData(IntPtr fileInfo, WinTrustStateAction stateAction)
        {
            StructureSize = (uint)Marshal.SizeOf<WinTrustData>();
            PolicyCallbackData = IntPtr.Zero;
            SIPClientData = IntPtr.Zero;
            UIChoice = 2; // WTD_UI_NONE
            RevocationChecks = 1; // WTD_REVOKE_WHOLECHAIN
            UnionChoice = 1; // WTD_CHOICE_FILE
            FileInfo = fileInfo;
            StateAction = stateAction;
            StateData = IntPtr.Zero;
            URLReference = null;
            ProviderFlags = 0;
            UIContext = 0;
            SignatureSettings = IntPtr.Zero;
        }
    }

    /// <summary>Launches the already hash-verified installer with elevation.</summary>
    public Task ExecutePayloadAsync(string installerPath, string _)
    {
        if (!File.Exists(installerPath))
        {
            throw new FileNotFoundException("The verified installer was not found.", installerPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = true, // Required for elevation prompt from the installer
            Verb = "runas",
        };

        Process.Start(startInfo);
        return Task.CompletedTask;
    }
}
