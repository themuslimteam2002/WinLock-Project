using System.Text;
using AppGuardian.Shared.Models;
using AppGuardian.Shared.Storage;

namespace AppGuardian.Tests.Storage;

/// <summary>
/// Atomic persistence and corruption handling. NFR-R4, TC-11, SRS §9.1.
/// </summary>
/// <remarks>
/// These touch the real file system, in a per-test temporary directory. A fake would not exercise the
/// property the class exists for — that a replace leaves either the old document or the new one — because
/// that property lives in <c>File.Replace</c> and the write-through flush, not in our own code.
/// </remarks>
public sealed class AtomicJsonStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "appguardian-tests-" + Guid.NewGuid().ToString("N"));

    private string PathFor(string name) => Path.Combine(_directory, name);

    public AtomicJsonStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory is not worth failing a green run over.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact(Timeout = 5000)]
    public async Task A_missing_file_loads_as_fresh_rather_than_as_a_failure()
    {
        var store = new AtomicJsonStore<PolicyDocument>(PathFor("absent.json"));

        var result = await store.LoadAsync();

        // First run. "No file" and "corrupt file" have to be distinguishable, because the service
        // proceeds normally in the first case and reports degraded status in the second.
        Assert.False(result.WasExisting);
        Assert.False(result.IsFailure);
        Assert.Empty(result.Value.Rules);
    }

    [Fact(Timeout = 5000)]
    public async Task A_saved_document_round_trips()
    {
        var store = new AtomicJsonStore<PolicyDocument>(PathFor("policy.json"));
        var document = new PolicyDocument
        {
            ProtectionPaused = true,
            Rules =
            {
                new AppRule
                {
                    Identity = new AppIdentity { ExecutablePath = @"C:\Apps\a.exe", DisplayName = "A" }.WithDerivedId(),
                    Lock = new LockSettings { Enabled = true, TempUnlockSeconds = 600 },
                    Power = new PowerSettings { Profile = PowerProfile.LowImpact, EcoQos = true },
                },
            },
        };

        await store.SaveAsync(document);
        var result = await store.LoadAsync();

        Assert.True(result.WasExisting);
        Assert.False(result.IsFailure);
        Assert.True(result.Value.ProtectionPaused);

        var rule = Assert.Single(result.Value.Rules);
        Assert.Equal(@"C:\Apps\a.exe", rule.Identity.ExecutablePath);
        Assert.Equal(600, rule.Lock.TempUnlockSeconds);
        Assert.Equal(PowerProfile.LowImpact, rule.Power.Profile);
        Assert.True(rule.Power.EcoQos);
    }

    [Fact(Timeout = 5000)]
    public async Task Saving_creates_the_directory()
    {
        var nested = Path.Combine(_directory, "deep", "deeper");
        var store = new AtomicJsonStore<UserSettings>(Path.Combine(nested, "settings.json"));

        await store.SaveAsync(new UserSettings());

        // The service's first write happens before anything else has created %ProgramData%\AppGuardian,
        // so a save that required the folder to exist would fail on a clean install.
        Assert.True(File.Exists(store.Path));
    }

    [Fact(Timeout = 5000)]
    public async Task Saving_leaves_no_temp_file_behind()
    {
        var store = new AtomicJsonStore<UserSettings>(PathFor("settings.json"));

        await store.SaveAsync(new UserSettings());

        // A surviving .tmp would mean the replace did not happen and the file on disk is the old one.
        Assert.False(File.Exists(store.Path + ".tmp"));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact(Timeout = 5000)]
    public async Task Overwriting_replaces_rather_than_appends()
    {
        var store = new AtomicJsonStore<UserSettings>(PathFor("settings.json"));

        await store.SaveAsync(new UserSettings { VerboseLogging = true, StartMinimizedToTray = false });
        await store.SaveAsync(new UserSettings { VerboseLogging = false, StartMinimizedToTray = true });

        var result = await store.LoadAsync();

        Assert.False(result.Value.VerboseLogging);
        Assert.True(result.Value.StartMinimizedToTray);
    }

    [Fact(Timeout = 5000)]
    public async Task An_empty_file_loads_as_fresh()
    {
        var path = PathFor("empty.json");
        await File.WriteAllTextAsync(path, string.Empty);
        var store = new AtomicJsonStore<PolicyDocument>(path);

        var result = await store.LoadAsync();

        // A zero-length file is what a pre-atomic write or a disk-full condition can leave. There is
        // nothing to recover from it, so defaults are the only honest answer — but it is not corruption
        // worth quarantining either.
        Assert.False(result.IsFailure);
        Assert.Empty(result.Value.Rules);
    }

    [Fact(Timeout = 5000)]
    public async Task A_corrupt_file_is_reported_and_preserved_not_overwritten()
    {
        var path = PathFor("policy.json");
        await File.WriteAllTextAsync(path, "{\"rules\": [ this is not json", Encoding.UTF8);
        var store = new AtomicJsonStore<PolicyDocument>(path);

        var result = await store.LoadAsync();

        // Reported, because silently substituting defaults is indistinguishable from "the user had no
        // rules" and would quietly unprotect every app (NFR-R4, TC-11).
        Assert.True(result.IsFailure);
        Assert.NotNull(result.Error);
        Assert.True(result.WasExisting);

        // Preserved, because the file is the only copy of the user's configuration and a human may be
        // able to repair it.
        var quarantined = Directory.GetFiles(_directory, "*.corrupt-*");
        Assert.Single(quarantined);
        Assert.Contains("not json", await File.ReadAllTextAsync(quarantined[0]), StringComparison.Ordinal);
    }

    [Fact(Timeout = 5000)]
    public async Task A_document_that_deserializes_to_null_is_a_failure()
    {
        var path = PathFor("null.json");
        await File.WriteAllTextAsync(path, "null");
        var store = new AtomicJsonStore<PolicyDocument>(path);

        var result = await store.LoadAsync();

        Assert.True(result.IsFailure);
        Assert.NotNull(result.Value);
    }

    [Fact(Timeout = 5000)]
    public async Task Concurrent_saves_and_loads_never_observe_a_partial_document()
    {
        var store = new AtomicJsonStore<PolicyDocument>(PathFor("policy.json"));
        await store.SaveAsync(NewDocument(1));

        var writes = Enumerable.Range(2, 25).Select(i => store.SaveAsync(NewDocument(i)));
        var reads = Enumerable.Range(0, 25).Select(async _ =>
        {
            var result = await store.LoadAsync();

            // Any count other than the full set would mean a reader saw a half-written file. The
            // internal gate serialises our own callers; the atomic replace covers the rest.
            Assert.False(result.IsFailure);
            Assert.True(result.Value.Rules.Count is 0 or 5);
        });

        await Task.WhenAll(writes.Concat(reads));

        static PolicyDocument NewDocument(int seed) => new()
        {
            Rules = Enumerable.Range(0, 5).Select(n => new AppRule
            {
                Identity = new AppIdentity { ExecutablePath = $@"C:\Apps\{seed}-{n}.exe" }.WithDerivedId(),
                Lock = new LockSettings { Enabled = true },
            }).ToList(),
        };
    }
}

/// <summary>The integrity hash used for the policy tamper check. NFR-S3.</summary>
public sealed class IntegrityHashTests
{
    [Fact]
    public void The_hash_is_lowercase_sha256_hex()
    {
        var hash = AtomicJsonStore<PolicyDocument>.ComputeIntegrityHash(new { rules = Array.Empty<int>() });

        Assert.Equal(64, hash.Length);
        Assert.Equal(hash.ToLowerInvariant(), hash);
    }

    [Fact]
    public void Equal_payloads_hash_equally()
    {
        var first = AtomicJsonStore<PolicyDocument>.ComputeIntegrityHash(new { a = 1, b = "x" });
        var second = AtomicJsonStore<PolicyDocument>.ComputeIntegrityHash(new { a = 1, b = "x" });

        // Stability across processes is the whole point: the hash is written by one service run and
        // checked by the next.
        Assert.Equal(first, second);
    }

    [Fact]
    public void A_changed_payload_changes_the_hash()
    {
        var before = AtomicJsonStore<PolicyDocument>.ComputeIntegrityHash(new { locked = true });
        var after = AtomicJsonStore<PolicyDocument>.ComputeIntegrityHash(new { locked = false });

        // The tamper this catches is someone flipping a lock off in the file by hand.
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void The_hash_is_independent_of_which_generic_argument_computed_it()
    {
        // The method is static on a generic type only because that is where it lives; it must not depend
        // on T, or the service and a future migration tool would disagree about the same document.
        Assert.Equal(
            AtomicJsonStore<PolicyDocument>.ComputeIntegrityHash(new { a = 1 }),
            AtomicJsonStore<UserSettings>.ComputeIntegrityHash(new { a = 1 }));
    }
}

/// <summary>Path resolution shared by every process and the installer. SRS §9.1, FR-103.</summary>
public sealed class StoragePathsTests
{
    [Fact]
    public void Machine_data_lives_under_the_common_application_data_folder()
    {
        var root = StoragePaths.MachineRoot;

        Assert.Contains(PolicyConstants.DataFolderName, root, StringComparison.Ordinal);
        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            root,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Policy_credentials_audit_and_logs_are_all_machine_scoped()
    {
        // Anything the service owns has to be outside the user profile: a per-user policy file would be
        // unreadable by LocalSystem before logon and would not survive the user being deleted.
        foreach (var path in new[]
                 {
                     StoragePaths.PolicyFile,
                     StoragePaths.CredentialsFile,
                     StoragePaths.AuditFile,
                     StoragePaths.LogFolder,
                 })
        {
            Assert.StartsWith(StoragePaths.MachineRoot, path, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Only_the_settings_file_is_per_user()
    {
        Assert.StartsWith(StoragePaths.UserRoot, StoragePaths.SettingsFile, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(StoragePaths.UserRoot, StoragePaths.MachineRoot);
    }

    [Fact]
    public void File_names_come_from_the_shared_constants()
    {
        // FR-103's "remove user data" is implemented by the uninstaller against these same constants.
        // A path assembled from a literal anywhere else is a file the uninstaller will miss.
        Assert.EndsWith(PolicyConstants.PolicyFileName, StoragePaths.PolicyFile, StringComparison.Ordinal);
        Assert.EndsWith(PolicyConstants.CredentialsFileName, StoragePaths.CredentialsFile, StringComparison.Ordinal);
        Assert.EndsWith(PolicyConstants.AuditFileName, StoragePaths.AuditFile, StringComparison.Ordinal);
        Assert.EndsWith(PolicyConstants.SettingsFileName, StoragePaths.SettingsFile, StringComparison.Ordinal);
    }

    [Fact]
    public void The_audit_log_is_not_the_diagnostic_log()
    {
        // The audit log is a record with an ACL behind it; the diagnostic logs rotate freely. Putting one
        // inside the other would let log rotation delete audit history.
        Assert.False(StoragePaths.AuditFile.StartsWith(StoragePaths.LogFolder, StringComparison.OrdinalIgnoreCase));
    }
}
