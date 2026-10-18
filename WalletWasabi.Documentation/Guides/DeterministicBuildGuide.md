# Guide for deterministic builds

The term *deterministic builds* is [defined](https://reproducible-builds.org/) as follows:

> Reproducible [or deterministic] builds are a set of software development practices that create an independently-verifiable path from source to binary code.

This guide describes how to reproduce Wasabi's release packages. If you get stuck with these instructions, take a look at [how to build Wasabi from source code](https://docs.wasabiwallet.io/using-wasabi/BuildSource.html).

## 1. How releases are produced

Every release artifact is produced by [`Contrib/release.sh`](../../Contrib/release.sh), run by the [release workflow](../../.github/workflows/release.yml) on GitHub-hosted runners when a `v*` tag is pushed:

| Job | Runner | Command | Artifacts |
| --- | --- | --- | --- |
| `debian-package-and-zips` | `ubuntu-latest` | `sudo bash -x ./Contrib/release.sh debian` | `Wasabi-<version>.deb`, `Wasabi-<version>-arm64.deb`, `Wasabi-<version>-linux-{x64,arm64}.{tar.gz,zip}` |
| `appimage-package` | `ubuntu-latest` | `sudo bash -x ./Contrib/release.sh appimage` | `Wasabi-<version>.AppImage`, `Wasabi-<version>-arm64.AppImage` |
| `installer-for-windows` | `windows-latest` | `./Contrib/release.sh wininstaller` | `Wasabi-<version>.msi`, `Wasabi-<version>-win-x64.zip` |
| `macos-packages-and-zips` | `macos-latest` | `./Contrib/release.sh dmg` | `Wasabi-<version>.dmg`, `Wasabi-<version>-arm64.dmg`, `Wasabi-<version>-macOS-{x64,arm64}.zip` |
| `sign-all-packages` | `ubuntu-latest` | `bash -x ./Contrib/release.sh gpgsign` | `SHA256SUMS.asc` (used in section 4) |

To reproduce a release, run **that release's own** `Contrib/release.sh`, checked out from the release tag.

The rest of this guide covers the Linux packages, which are the ones that reproduce byte for byte. Windows and macOS are discussed in section 4.

## 2. Assert correct environment

You need:

* a Linux system (the release workflow uses `ubuntu-latest`) with `git`, `zip`, `tar`, `dpkg-deb` (package `dpkg`), `file` and `curl`;
* the .NET SDK that ships the **same runtime pack** the release was built against (see below);
* root privileges, or one small adaptation (see below).

### Which .NET SDK?

`global.json` pins only the major version and uses `rollForward: latestFeature`, so the release workflow builds with whatever .NET SDK the runner image provides on release day. Do not guess it: the shipped package records the runtime it was built against. Read it from the artifact you want to reproduce:

```sh
# from the .deb
dpkg-deb --fsys-tarfile Wasabi-<version>.deb | tar -xO --wildcards '*WalletWasabi.Fluent.Desktop.runtimeconfig.json'
# or from the tar.gz
tar -xzO --wildcards -f Wasabi-<version>-linux-x64.tar.gz '*WalletWasabi.Fluent.Desktop.runtimeconfig.json'
```

The `"version"` field under `runtimeOptions.framework` is the runtime, for example `10.0.11` for 2.8.2. Then install the **highest** SDK that ships that runtime, which is what `latestFeature` selects; the mapping is published in [dotnet/core `releases.json`](https://github.com/dotnet/core/blob/main/release-notes/10.0/releases.json).

If several SDKs are installed, pin the one you chose in `global.json` before building. Here an example:

```json
{
  "sdk": {
    "version": "10.0.400",
    "allowPrerelease": false,
    "rollForward": "disable"
  }
}
```

A convenient way to get exactly one SDK is the official container image, `mcr.microsoft.com/dotnet/sdk:<sdk-version>-noble`.

## 3. Reproduce the Linux packages

You can see the list of Wasabi releases here: https://github.com/WalletWasabi/WalletWasabi/releases. Each release has a git tag `v<version>`.

```sh
# Fetch exactly the tag (a shallow clone by tag name would silently prefer a same-named branch).
git init WalletWasabi && cd WalletWasabi
git remote add origin https://github.com/WalletWasabi/WalletWasabi.git
git fetch --depth 1 origin refs/tags/v<version>:refs/tags/v<version>
git checkout refs/tags/v<version>

# Same command the release workflow runs.
sudo bash -x ./Contrib/release.sh debian
```

The packages land in `packages/`:

```
packages/Wasabi-<version>.deb
packages/Wasabi-<version>-arm64.deb
packages/Wasabi-<version>-linux-x64.tar.gz
packages/Wasabi-<version>-linux-x64.zip
packages/Wasabi-<version>-linux-arm64.tar.gz
packages/Wasabi-<version>-linux-arm64.zip
```

The `debian` target builds both `linux-x64` and `linux-arm64` in one run.

## 4. Verify builds

Every release ships `SHA256SUMS.asc`, the list of package hashes clearsigned by the zkSNACKs key (`6FB3 872B 5D42 292F 5992 0797 8563 4832 8949 861E`). Verify the signature, then compare your packages against the signed list:

```sh
gpg --keyserver hkps://keys.openpgp.org --recv-keys 6FB3872B5D42292F59920797856348328949861E
gpg --decrypt SHA256SUMS.asc > SHA256SUMS.verified   # prints "Good signature from zkSNACKs" and writes the signed list
cd packages && sha256sum -c --ignore-missing ../SHA256SUMS.verified
```

`sha256sum -c` prints `OK` for every package whose hash matches the published one. If a hash differs, extract both packages and compare them to find out why:

```sh
dpkg-deb -R Wasabi-<version>.deb official
dpkg-deb -R packages/Wasabi-<version>.deb built
diff -r official built
```

### What to expect

* **`.deb`: byte-identical.** [WalletScrutiny](https://walletscrutiny.com/) rebuilt `Wasabi-<version>.deb` from tag `v<version>`
* **`.tar.gz`: byte-identical.** The tarball is created with `--sort=name`, `--mtime=@$SOURCE_DATE_EPOCH`, `--owner=0 --group=0 --numeric-owner` and the atime/ctime PAX headers removed.
* **`.zip`: differs**, because `zip -r` stores the files' modification times (see *Timestamps* above); it contains the same files that go into the `.tar.gz`.

### Windows

The `wininstaller` release target creates an unsigned portable `Wasabi-<version>-win-x64.zip`, then signs and verifies `wassabee.exe` and `wassabeed.exe` before packaging them into the MSI. It also signs and verifies the final MSI. The signed files cannot match an unsigned rebuild byte for byte; their contents can be compared after accounting for Authenticode metadata.

The `winbinaries` target is available only in release tags that define it. For older releases, follow that tag's guide and original `wininstaller` build procedure.

On Windows, check out a release tag containing `winbinaries` and select the matching .NET SDK as described above. Install 7-Zip and put `7z.exe` on `PATH`. From a clean checkout, run the following in Git Bash to build the unsigned Windows binaries and ZIP without WiX or Azure signing credentials:

```bash
RUNNER_OS=Windows ./Contrib/release.sh winbinaries
```

Download the official `Wasabi-<version>-win-x64.zip` and verify its hash against the signed release hash list as described above. Then extract the official and locally built ZIPs into separate directories and compare their contents in PowerShell:

```powershell
Expand-Archive -LiteralPath "C:\Downloads\Wasabi-<version>-win-x64.zip" -DestinationPath official-win
Expand-Archive -LiteralPath "packages\Wasabi-<version>-win-x64.zip" -DestinationPath built-win
git diff --no-index official-win built-win
```

The comparison should report no differences. Compare the extracted files, because ZIP metadata such as timestamps can differ. A match verifies the portable ZIP's payload; it does not by itself verify the signed MSI or its installed executables.

To verify the installed executables, first check the original MSI and both entry-point EXE signatures with the Windows SDK's [SignTool](https://learn.microsoft.com/en-us/windows/win32/seccrypto/signtool), for example `signtool verify /pa /all /v wassabee.exe`. Then compare copies of the extracted MSI payload against the unsigned rebuild. SignTool can remove an embedded signature from a comparison copy with `signtool remove /s wassabee.exe`. Signature removal alone does not guarantee recovery of the original file bytes: the comparison must also account for the PE checksum and signing-related padding, and reject unexplained differences. Keep the original signed files for signature verification.

For automated content comparison, Microsoft's [PE image digest](https://learn.microsoft.com/en-us/windows/win32/debug/pe-format#certificate-data) excludes Authenticode certificate data and the related header fields. A verifier using [ImageGetDigestStream](https://learn.microsoft.com/en-us/windows/win32/api/imagehlp/nf-imagehlp-imagegetdigeststream) must include all documented content flags and separately account for any bytes outside the digest's coverage. A valid publisher signature by itself does not show that the executable matches an independent rebuild.

WalletScrutiny's [Windows verification script v0.2.0](https://walletscrutiny.com/desktop/wasabi/#verificationId=9142e12278e997695238adc4400a58a48fdfa139de423574a6ff481d7edae3c5) needs updating for this signing flow. It currently runs `wininstaller` for both ZIP and MSI checks with a stand-in signer; mandatory signature verification therefore stops both builds at the first unsigned EXE. ZIP verification can use `winbinaries`. MSI verification needs a separate unsigned packaging build without the release signing and verification steps, then must compare extracted executable contents while accounting for Authenticode metadata and the resulting MSI file-size, hash and cabinet differences. Its current comparator does not normalize embedded PE signatures. The existing random WiX identifiers described in [the Windows MSI reproducibility report](https://github.com/WalletWasabi/WalletWasabi/issues/15171) are a separate issue.

### macOS

According to Apple documentation, the signature that is used to ensure the integrity of the software is added into the binary itself - so it will manipulate the content of the files.

> If the code is universal, the object code for each slice (architecture) is signed separately. This signature is stored within the binary file itself.

[Source](https://developer.apple.com/library/archive/documentation/Security/Conceptual/CodeSigningGuide/AboutCS/AboutCS.html#//apple_ref/doc/uid/TP40005929-CH3-SW3)

According to this, it is impossible to have both deterministic build and code signature on macOS. macOS Gatekeeper won't let you run software without it. Thus, Wasabi only applies code signature, but no deterministic build for macOS.

There is an issue [here](https://github.com/WalletWasabi/WalletWasabi/issues/4110) for further discussion.

With the following method you can check the differences by yourself. You will need `7z` to extract the `.dmg` (`sudo apt install p7zip-full`):

```sh
7z x Wasabi-<version>.dmg -oWasabiOsx
git diff --no-index osx-x64/ WasabiOsx/Wasabi\ Wallet.App/Contents/MacOS/
```
