# Installer signing status

## Current MVP state

AppGuardian installers are currently unsigned during the MVP phase. Windows SmartScreen may show a
warning because the file has no established publisher reputation. The bootstrapper still verifies a
release-specific SHA-256 hash before it launches the downloaded installer.

## Safe user instructions

Only download AppGuardian from the official release page. Before bypassing a SmartScreen prompt, compare
the SHA-256 value published with that release to the value printed by:

```powershell
Get-FileHash -Algorithm SHA256 .\AppGuardian-<version>-x64-setup.exe
```

When the download source and SHA-256 value match, choose **More info**, then **Run anyway**. Do not bypass
the warning for a file obtained from email, a mirror, or an unexpected link.

## Post-launch signing plan

1. Obtain an EV code-signing certificate for the AppGuardian publisher.
2. Store certificate access in the CI signing service; never commit certificate files or passwords.
3. Enable `SignTool` and `SignedUninstaller` in `installer/AppGuardian.iss`.
4. Sign the bootstrapper, installer, and uninstaller with SHA-256 plus RFC 3161 timestamping.
5. Set `ExpectedPublisherSubject` in the bootstrapper and retain SHA-256 payload verification as a
   defense in depth control.
6. Add CI verification that rejects unsigned release artifacts and validates the signer subject and chain.
