# Packaging

Installer packaging for the WaveBench desktop app and CLI.

```
powershell -File packaging/Package.ps1 -Version 1.0.0.0
```

That publishes both the app and the CLI, then builds an **MSI** into
`packaging/out/`. Pass `-Format Msix` or `-Format Both` for the MSIX as well.

## Why MSI is the default

**An MSIX will not install without a signature Windows already trusts.** Until
somebody holds a code-signing certificate, an MSIX is a file nobody can use.

**An MSI installs unsigned.** The user gets a SmartScreen prompt naming an
unknown publisher and decides for themselves. That is a worse first impression
than a signed installer and an enormously better one than a package that
refuses to install at all — so the MSI is what ships, and the MSIX is kept
ready for the day there is a certificate.

## What the MSI does

- **Per-user**, into `%LocalAppData%\Programs\WaveBench`. No elevation, no UAC
  prompt. An *unsigned* installer asking for administrator rights is exactly
  the prompt a careful user should refuse, and nothing here needs to write
  outside the user's own profile.
- Installs **both** `WaveBench.App.exe` and `wavebench.exe`. The user guide
  documents `wavebench sweep`, `wavebench report` and the rest throughout; an
  installer that delivered only the GUI would make every one of those commands
  a lie for anybody who installed rather than built from source.
- Start Menu shortcut, and a `.wavebench` file association under `HKCU`.
- Major upgrades replace in place; downgrades are refused with a message that
  says so.
- Debug symbols are excluded — they stay in each project's `bin/` for
  diagnosing a stack trace, but they are a third of the package and no user
  opens them.

Verified end to end: installs with exit code 0 and no elevation, 59 files on
disk, the installed `wavebench.exe` solves an operating point, and uninstall
leaves no folder, no shortcut, no file association and no registry key behind.

## What you need

- The .NET 10 SDK.
- **For the MSI:** the WiX toolset — `dotnet tool install --global wix`, plus
  `wix extension add -g WixToolset.UI.wixext` for the installer dialogs.
- **For the MSIX:** the Windows 10/11 SDK, for `makeappx.exe`. The script finds
  the highest installed version itself.

## Signing

**The build script can sign, but this repository cannot.** Signing needs a
code-signing certificate and its private key, and neither belongs in a public
repository — so it is a step the maintainer performs with their own
certificate, on a machine that holds it:

```
powershell -File packaging/Package.ps1 -Version 1.0.0.0 -CertificatePath path\to\cert.pfx
```

The password is prompted for rather than taken on the command line, so it does
not end up in a shell history. Everything built in that run is signed and then
**verified** — signing something and never checking it verifies is how a broken
release gets published.

The signature is timestamped against an RFC 3161 server (`-TimestampUrl`,
DigiCert by default). Without a timestamp the signature stops validating the
day the certificate expires, which for a released artefact is the difference
between signing it and not. That timestamp request is the only network call
anywhere in this repository; it happens at build time and has nothing to do
with the runtime claim on the front of the README.

**Known limitation: `.pfx` files only.** Publicly trusted code-signing
certificates are now issued on hardware tokens or HSMs and cannot be exported
to a `.pfx`, so signing with one means calling `signtool` directly with
`/sha1 <thumbprint>` (or `/n <subject>`) instead of `/f`. This script does not
wrap that yet. It is a small change when somebody has such a certificate to
test it against; writing it blind would be guessing at a flow nobody here can
run.

**For the MSIX, the manifest's `Publisher` must equal the certificate's subject
exactly**, or Windows refuses to install the package and the error message does
not say which of the two is wrong. The script therefore reads the subject from
the certificate and writes it into the staged manifest, so the two cannot drift
apart. The `CN=TokenGoblin` in the committed manifest is the placeholder used
for unsigned local builds.

## The logo set

`packaging/assets/*.png` is generated, not drawn:

```
python packaging/make-logos.py
```

The mark is a damped pressure wave travelling down a duct. Generating it means
a new tile size is a line in a dictionary rather than an image somebody has to
trace, and the output is deterministic — the same script produces
byte-identical files, so the assets stay out of diffs that do not intend to
change them.

## Capabilities (MSIX)

The manifest declares `runFullTrust` and nothing else. In particular it does
**not** declare `internetClient`: WaveBench makes no network calls at runtime,
and a package that asks for network access "just in case" has given away the
first claim on the front of the README. If a future feature needs the network,
that claim changes first.
