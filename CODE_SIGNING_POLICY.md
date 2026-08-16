# Code Signing Policy

## Project

- Project: Realtime Translator
- Source repository: https://github.com/kinopeee/interpreter-openai
- License: [MIT](LICENSE)
- Release downloads: https://github.com/kinopeee/interpreter-openai/releases

## Signing provider and current status

The project is preparing an application to the SignPath Foundation open-source
code-signing program. Windows artifacts published before that application is
approved are unsigned.

After approval, releases signed through that program will carry this credit:

> Free code signing provided by SignPath.io, certificate by SignPath Foundation

The certificate and its private key are managed by SignPath and the SignPath
Foundation. They are never copied into this repository or stored as GitHub
Actions secrets.

## Team roles

The current project team is:

- Authors and committers: [@kinopeee](https://github.com/kinopeee)
- Reviewers: [@kinopeee](https://github.com/kinopeee)
- Signing approvers: [@kinopeee](https://github.com/kinopeee)

The project owner is a trusted author and may commit directly. Contributions
from other people are accepted through pull requests and reviewed before they
are merged. Every SignPath signing request requires a separate manual approval
by a signing approver.

All team members with repository or signing access must enable multi-factor
authentication for GitHub and SignPath.

## Source and build provenance

Until the SignPath Foundation application is approved, Windows release artifacts
remain unsigned. The current `.github/workflows/release.yml` Windows job
packages those unsigned artifacts after tests pass. It does not submit files to
SignPath or verify Authenticode signatures.

After approval, signed Windows release artifacts must:

1. Be built from this repository by `.github/workflows/release.yml`.
2. Be built from a release tag matching the repository's `vX.Y.Z` tag policy.
3. Pass the Windows tests before signing.
4. Be produced by the checked-in `scripts/publish-windows.ps1` script.
5. Be submitted to SignPath by the same GitHub Actions run that built them.
6. Be manually approved in SignPath before publication.

Files built locally or uploaded manually are not eligible for project signing.

## Signing scope

The SignPath project may sign only PE files produced from source maintained in
this repository:

- `RealtimeTranslator.App.exe`
- `RealtimeTranslator.App.dll`
- `RealtimeTranslator.Core.dll`
- `RealtimeTranslator.Platform.dll`

The project does not use its SignPath certificate to re-sign third-party
libraries, .NET runtime files, operating-system components, or other upstream
binaries. ZIP archives, checksum files, documentation, and configuration files
are not Authenticode-signed.

The signed files are packaged into
`RealtimeTranslator-<tag>-win-x64.zip`. The SHA-256 checksum is generated only
after the signed files have been downloaded from SignPath.

## Release verification

After SignPath approval, the release workflow must verify the Authenticode
signature of every file in the signing scope before packaging. Users can also
verify the extracted application:

```powershell
$files = @(
  '.\RealtimeTranslator.App.exe',
  '.\RealtimeTranslator.App.dll',
  '.\RealtimeTranslator.Core.dll',
  '.\RealtimeTranslator.Platform.dll'
)
# Replace with the published SignPath Foundation signer identity after approval.
$expectedChainMarker = 'SignPath Foundation'
foreach ($file in $files) {
  $signature = Get-AuthenticodeSignature $file
  if ($signature.Status -ne 'Valid') {
    throw "$file signature status is $($signature.Status)"
  }
  $chain = [System.Security.Cryptography.X509Certificates.X509Chain]::new()
  if (-not $chain.Build($signature.SignerCertificate)) {
    throw "$file certificate chain is invalid"
  }
  $chainSubjects = @(
    $chain.ChainElements | ForEach-Object { $_.Certificate.Subject }
  )
  if (-not ($chainSubjects | Where-Object { $_ -like "*$expectedChainMarker*" })) {
    throw "$file signer chain does not include ${expectedChainMarker}: $($chainSubjects -join '; ')"
  }
}
```

For a SignPath-signed release, each file's `Status` must be `Valid` and the
signer must chain to the certificate supplied by the SignPath Foundation. After
approval, replace `$expectedChainMarker` with that published identity. The
release ZIP must also match its separately published `.sha256` file:

```powershell
$expected = (Get-Content .\RealtimeTranslator-<tag>-win-x64.zip.sha256 -Raw).Trim().Split()[0].ToLowerInvariant()
$actual = (Get-FileHash .\RealtimeTranslator-<tag>-win-x64.zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) { throw "SHA-256 mismatch: expected $expected, got $actual" }
```

## Security and revocation

If a signing credential, build workflow, release artifact, or maintainer
account may have been compromised:

1. Stop approving signing requests and publishing releases.
2. Preserve the relevant GitHub Actions and SignPath audit records.
3. Notify SignPath and the SignPath Foundation.
4. Remove affected release assets.
5. Request certificate revocation when required.
6. Publish a corrected release only after the incident is contained.

Security-sensitive reports should be sent to the project owner through
[@kinopeee](https://github.com/kinopeee). Non-sensitive questions may be filed
in [GitHub Issues](https://github.com/kinopeee/interpreter-openai/issues).

## Privacy

The project's user-facing data practices are documented in the
[Privacy Policy](PRIVACY.md). Realtime Translator has no maintainer-operated
backend service. Network transfers initiated by the app are limited to the
OpenAI API interactions described in that policy.

Changes to this policy are made in the source repository and reviewed with the
same process as other release-related changes.
