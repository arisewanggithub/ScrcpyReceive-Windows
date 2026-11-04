# Receiver Store preparation - no reservation or submission yet

**ScrcpyReceive-Windows has no reserved Store identity in this source.**
The sender's existing Partner Center product/submission must not be reused,
altered, withdrawn or resubmitted. The current receiver is a hardware multi-codec audio/video preview,
not ready for sale/submission as the complete playback receiver.

## Available now

- `tools\Publish.ps1`: self-contained, untrimmed native ARM64/x64 app.
- `tools\Package.ps1`: unsigned MSIX, explicit identity/version parameters,
  Windows 11 22H2 baseline, native architecture checks and no-PDB validation.
- CI builds/tests both architectures and validates packages with conspicuously
  **CI-only** identity, never Store credentials. No workflow submits, installs a
  certificate, changes Windows policy or accepts legal terms.

Local packaging validation (not an uploadable reserved identity):

```powershell
.\tools\Package.ps1 -Architecture arm64 -PublishedDirectory .\artifacts\win-arm64 `
  -OutputDirectory .\artifacts\msix-arm64 -Version 0.1.0.0 `
  -PackageName ScreenCopy.Receiver.CIOnly -Publisher 'CN=ScreenCopy Receiver CI Only' `
  -PublisherDisplayName 'CI Only - not a Store identity'
```

The output is unsigned and not expected to install without appropriate
owner-managed signing. Successful schema validation is not Store certification.

## Owner setup after playback acceptance

1. Keep the receiver in its separate **ScrcpyReceive-Windows** GitHub repository.
   The project's own source uses the [MIT License](LICENSE); review the source
   and third-party notices before release. No receiver Store product has been
   created by this implementation.
2. Protect `main` and require **Receiver PR gate**. Use isolated worktrees for
   subsequent development after the first submission. Never disable protection
   to unblock a build.
3. Reserve a **new receiver** MSIX product in Partner Center. The owner must
   personally review declarations/terms; an agent must not accept them.
4. Copy exact receiver Package/Identity/Name, Package/Identity/Publisher,
   PublisherDisplayName and Store product ID from its Product Identity page.
   The sender display name RedRain is a reference, NOT a reserved receiver
   identity. Do not infer identity or invent product IDs.
5. Add receiver-specific GitHub **variables**, not credentials:
   `RECEIVER_STORE_PACKAGE_NAME`, `RECEIVER_STORE_PUBLISHER`,
   `RECEIVER_STORE_PUBLISHER_DISPLAY_NAME`, `RECEIVER_STORE_APP_ID`.
   Feed the first three to `tools\Package.ps1` for each architecture; version
   MAJOR.MINOR.PATCH.0, components <=65535. Then build/test a matching bundle.
6. Add a manual `main`-only Store workflow **after** identity is authorized.
   Default must be package-only, with an explicit publish choice. No automatic
   submission on pushes/tags/PRs. The current CI intentionally has no uploader.
7. Create protected environment **receiver-store-publish**, require owner
   approval, restrict branches to `main`. Future publishing credentials
   `RECEIVER_STORE_TENANT_ID`, `RECEIVER_STORE_CLIENT_ID`,
   `RECEIVER_STORE_CLIENT_SECRET` belong only in this environment's secrets.
   Never expose them to PR jobs, artifacts, logs or `pull_request_target`.
8. Complete the first receiver submission manually: listing, signed-package
   testing, screenshots, privacy URL, availability, ratings, pricing and
   declarations. Explain the desktop receiver's `runFullTrust`/private-network
   capabilities accurately. Do not claim screen capture or working playback
   beyond the implemented hardware audio/video preview. Review FFmpeg/AutoGen LGPL
   distribution/replacement requirements and bundled notices. Replace
   development artwork before release.
9. Subsequent authorized uploader work must refuse pending submissions,
   preserve existing listings, verify bundle hashes/identity, never auto-delete
   drafts and distinguish uploaded/accepted/certified/published states.

Future privacy URL:
https://screencopy.redrain.ccwu.cc/privacy/scrcpyreceive-windows/
The draft describes implemented preview behavior only; verify deployment
and align with working playback and Store disclosures before submission.
