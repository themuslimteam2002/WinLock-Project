using AppGuardian.Shared.Models;

namespace AppGuardian.Tests.Models;

/// <summary>
/// Identity derivation. FR-402, ADR-004, SC-06.
/// </summary>
/// <remarks>
/// These tests pin the one decision in <see cref="AppIdentity.DeriveAppId"/> that the specs left open
/// (SC-06 — neither document says what is hashed) so that a later ruling shows up as a failing test
/// rather than as rules that silently stop matching the apps they were written for.
/// </remarks>
public sealed class AppIdentityTests
{
    [Fact]
    public void Derived_ids_are_prefixed_sha256_hex()
    {
        var appId = AppIdentity.DeriveAppId(@"C:\Program Files\Steam\steam.exe", packageFamilyName: null);

        Assert.StartsWith("sha256:", appId, StringComparison.Ordinal);

        // 64 hex characters after the prefix. The format is part of the wire contract (API Design §5),
        // so a change here is a breaking change to every persisted rule.
        var hex = appId["sha256:".Length..];
        Assert.Equal(64, hex.Length);
        Assert.Equal(hex, hex.ToLowerInvariant());
    }

    [Theory]
    [InlineData(@"C:\Foo\App.EXE", @"c:\foo\app.exe")]
    [InlineData(@"C:/Foo/App.exe", @"C:\Foo\App.exe")]
    [InlineData(@"  C:\Foo\App.exe  ", @"C:\Foo\App.exe")]
    public void Path_case_separators_and_surrounding_space_do_not_change_the_id(string left, string right)
    {
        // Windows paths are case-insensitive and both separators are accepted by the OS, so the same
        // executable reached two ways has to resolve to one rule. Otherwise a rule created from a Start
        // menu shortcut would not match the same app launched from Explorer.
        Assert.Equal(
            AppIdentity.DeriveAppId(left, null),
            AppIdentity.DeriveAppId(right, null));
    }

    [Fact]
    public void Different_paths_give_different_ids()
    {
        Assert.NotEqual(
            AppIdentity.DeriveAppId(@"C:\Apps\a.exe", null),
            AppIdentity.DeriveAppId(@"C:\Apps\b.exe", null));
    }

    [Fact]
    public void A_package_family_name_wins_over_the_path()
    {
        // Packaged apps move between versioned install directories on every update. Deriving from the
        // path would orphan the rule each time the app updated, which is exactly what ADR-004 exists to
        // prevent.
        var fromBoth = AppIdentity.DeriveAppId(@"C:\Program Files\WindowsApps\v1\app.exe", "Contoso.App_8wekyb3d8bbwe");
        var fromUpdated = AppIdentity.DeriveAppId(@"C:\Program Files\WindowsApps\v2\app.exe", "Contoso.App_8wekyb3d8bbwe");

        Assert.Equal(fromBoth, fromUpdated);
    }

    [Fact]
    public void A_package_family_name_is_case_insensitive()
    {
        Assert.Equal(
            AppIdentity.DeriveAppId(null, "Contoso.App_8wekyb3d8bbwe"),
            AppIdentity.DeriveAppId(null, "CONTOSO.APP_8WEKYB3D8BBWE"));
    }

    [Fact]
    public void A_packaged_id_never_collides_with_an_unpackaged_one()
    {
        // The "pfn:" / "exe:" prefixes in the normalised string are what guarantee this. Without them a
        // package family name that happened to equal a path would share a rule.
        Assert.NotEqual(
            AppIdentity.DeriveAppId(null, "Contoso.App"),
            AppIdentity.DeriveAppId("Contoso.App", null));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", "  ")]
    public void An_identity_with_neither_path_nor_package_is_refused(string? path, string? pfn)
    {
        // Returning a hash of the empty string would give every unidentifiable app one shared id, and a
        // single rule would then appear to apply to all of them.
        Assert.Throws<ArgumentException>(() => AppIdentity.DeriveAppId(path, pfn));
    }

    [Fact]
    public void WithDerivedId_fills_the_id_from_the_current_fields_and_returns_itself()
    {
        var identity = new AppIdentity { ExecutablePath = @"C:\Apps\editor.exe" };

        var returned = identity.WithDerivedId();

        Assert.Same(identity, returned);
        Assert.Equal(AppIdentity.DeriveAppId(@"C:\Apps\editor.exe", null), identity.AppId);
    }

    [Fact]
    public void File_content_does_not_participate_in_the_id()
    {
        // ADR-004. FileHash is a tamper signal only; if it fed the id, every application update would
        // orphan the user's rules.
        var before = new AppIdentity
        {
            ExecutablePath = @"C:\Apps\editor.exe",
            FileHash = "sha256:" + new string('a', 64),
        }.WithDerivedId();

        var after = new AppIdentity
        {
            ExecutablePath = @"C:\Apps\editor.exe",
            FileHash = "sha256:" + new string('b', 64),
        }.WithDerivedId();

        Assert.Equal(before.AppId, after.AppId);
    }

    [Fact]
    public void IsPackaged_reflects_the_package_family_name_only()
    {
        Assert.True(new AppIdentity { PackageFamilyName = "Contoso.App_8wekyb3d8bbwe" }.IsPackaged);
        Assert.False(new AppIdentity { ExecutablePath = @"C:\Apps\a.exe" }.IsPackaged);

        // Whitespace is not a package. An AUMID alone does not make an app packaged for our purposes,
        // because the PFN is the field the derivation and the enforcement paths both key on.
        Assert.False(new AppIdentity { PackageFamilyName = "  " }.IsPackaged);
        Assert.False(new AppIdentity { AppUserModelId = "Contoso.App!App" }.IsPackaged);
    }
}
