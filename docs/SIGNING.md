# Code signing

Builds are **unsigned** unless you provide a code-signing certificate. Unsigned builds work, but Windows SmartScreen will show an "unknown publisher" warning. Never ask users to disable SmartScreen, Smart App Control or Defender to install TICKSHIFT.

## What gets signed

`build/publish.ps1` signs, when `-CertThumbprint` (or `TICKSHIFT_SIGN_THUMBPRINT`) is set:

* `Tickshift.exe`, `Tickshift.ElevatedHelper.exe` and all `Tickshift*.dll` files
* the Inno Setup installer and its uninstaller (`SignTool=tickshift`, `SignedUninstaller=yes`)

Signing uses SHA-256 file digests and an RFC 3161 SHA-256 timestamp (`/fd sha256 /tr <url> /td sha256`), so signatures stay valid after the certificate expires.

## Getting a certificate

* **Organisation Validation (OV)** or **Extended Validation (EV)** code-signing certificate from a public CA. Since June 2023 private keys must live on a hardware token/HSM, so the certificate is usually used through the vendor's CSP (installed in the Windows certificate store and referenced by thumbprint).
* **Azure Trusted Signing** is an alternative; replace the `signtool sign /sha1 …` lines with the Trusted Signing dlib invocation.

## Signing locally

```powershell
# Certificate installed in CurrentUser\My (or on a token whose CSP exposes it there)
Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Format-Table Thumbprint, Subject
./build/publish.ps1 -Version 1.0.0 -CertThumbprint <THUMBPRINT>
```

Verify:

```powershell
signtool verify /pa /v artifacts\publish\win-x64\Tickshift.exe
Get-AuthenticodeSignature artifacts\installer\TICKSHIFT-Setup-1.0.0-x64.exe
```

## Signing in CI

Do not store a PFX in the repository or as a plain secret. Prefer a cloud HSM/signing service with short-lived credentials (for example Azure Trusted Signing with OIDC federation). Add the signing step after `dotnet publish` and pass `-CertThumbprint`/service parameters from protected environment secrets.

## Never

* Commit `.pfx`, `.p12`, `.snk` or private keys (they are git-ignored).
* Describe an unsigned build as signed. The Settings › About page and installer metadata make no signing claims; `publish.ps1` prints a warning for unsigned builds.
