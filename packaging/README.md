# Packaging

MSIX packaging for the WaveBench desktop app.

```
pwsh packaging/Package.ps1 -Version 1.0.0.0
```

That publishes the app, stages it with the manifest and the logo set, and
calls `makeappx` from the Windows SDK. Output lands in `packaging/out/`.

## What you need

- The .NET 10 SDK.
- The Windows 10/11 SDK, for `makeappx.exe` and `signtool.exe`. The script
  finds the highest installed version itself.

## Signing

**The build script can sign, but this repository cannot.** Signing needs a
code-signing certificate and its private key, and neither belongs in a public
repository — so a signed release is a step the maintainer performs with their
own certificate, on a machine that holds it:

```
pwsh packaging/Package.ps1 -Version 1.0.0.0 -CertificatePath path\to\cert.pfx
```

The password is prompted for rather than taken on the command line, so it does
not end up in a shell history.

**The manifest's `Publisher` must equal the certificate's subject exactly**, or
Windows refuses to install the package and the error message does not say
which of the two is wrong. The script therefore reads the subject from the
certificate and writes it into the staged manifest, so the two cannot drift
apart. The `CN=TokenGoblin` in the committed manifest is the placeholder used
for unsigned local builds.

An unsigned package installs only with developer mode enabled and the
certificate trusted manually. That is fine for testing and is not a release.

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

## Capabilities

The manifest declares `runFullTrust` and nothing else. In particular it does
**not** declare `internetClient`: WaveBench makes no network calls at runtime,
and a package that asks for network access "just in case" has given away the
first claim on the front of the README. If a future feature needs the network,
that claim changes first.
