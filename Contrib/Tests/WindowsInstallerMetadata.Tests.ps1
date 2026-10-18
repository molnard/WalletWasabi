[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $WixBin,
    [string] $SevenZip = 'C:\Program Files\7-Zip\7z.exe',
    [string] $OutputRoot = (Join-Path ([IO.Path]::GetTempPath()) ('wasabi-msi-metadata-' + [Guid]::NewGuid().ToString('N')))
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$helper = Join-Path $PSScriptRoot '..\Utils\WindowsInstallerMetadata.ps1'
$epoch = 1768480497 # 2026-01-15 12:34:57 UTC, outside this host's current DST season.
$signedPayload = "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe"
$originalPayloadHash = (Get-FileHash -LiteralPath $signedPayload).Hash
if (Test-Path -LiteralPath $OutputRoot) { throw 'OutputRoot must be a new directory.' }
New-Item -ItemType Directory -Path $OutputRoot | Out-Null

function Assert([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Rejected([scriptblock] $Action, [string] $Message, [string] $Reason) {
    $rejected = $false
    try { & $Action | Out-Null } catch {
        if ($_.Exception.ToString() -notlike "*$Reason*") { throw }
        $rejected = $true
    }
    Assert $rejected $Message
}

function Read-Msi([string] $Path) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.OpenDatabase($Path, 0)
    $summary = $database.SummaryInformation(0)
    try {
        return [pscustomobject]@{ Code = $summary.Property(9); Created = $summary.Property(12); Saved = $summary.Property(13) }
    } finally {
        foreach ($item in @($summary, $database, $installer)) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($item) }
    }
}

function Change-Database([string] $Path, [string] $Mode, [string] $EntryFile = '') {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.OpenDatabase($Path, 1)
    $view = $null
    $record = $null
    try {
        if ($Mode -in @('Signature', 'Storage')) {
            $table = if ($Mode -eq 'Signature') { '_Streams' } else { '_Storages' }
            $view = $database.OpenView("INSERT INTO ``$table`` (``Name``, ``Data``) VALUES (?, ?)")
            $record = $installer.CreateRecord(2)
            $record.StringData(1) = if ($Mode -eq 'Signature') { [char]5 + 'DigitalSignature' } else { 'embedded' }
            $record.SetStream(2, $EntryFile)
            $view.Execute($record)
        } else {
            $view = $database.OpenView("UPDATE ``Property`` SET ``Value`` = 'changed installer logic' WHERE ``Property`` = 'ARPCOMMENTS'")
            $view.Execute()
        }
        $database.Commit()
    } finally {
        if ($view) { $view.Close() }
        foreach ($item in @($record, $view, $database, $installer)) {
            if ($item) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($item) }
        }
    }
}

function Build-Fixture([string] $Name, [string] $Contents) {
    $root = Join-Path $OutputRoot $Name
    New-Item -ItemType Directory -Path $root | Out-Null
    Copy-Item -LiteralPath $signedPayload -Destination (Join-Path $root 'signed.exe')
    [IO.File]::WriteAllText((Join-Path $root 'data.txt'), $Contents)
    # Preserve Authenticode bytes while normalizing source file metadata.
    & $helper -Mode Prepare -Path $root -SourceDateEpoch $epoch
    Assert ((Get-FileHash -LiteralPath (Join-Path $root 'signed.exe')).Hash -eq $originalPayloadHash) 'Prepare modified the signed payload.'
    $source = @'
<?xml version="1.0" encoding="utf-8"?>
<Wix xmlns="http://schemas.microsoft.com/wix/2006/wi">
  <Product Id="{217490D4-8797-4D64-A60B-9108DE7B008E}" Name="Metadata test" Language="1033" Version="1.0.0" Manufacturer="Wasabi" UpgradeCode="{EA9DDD21-A6CE-411B-A962-E7825770DC3B}">
    <Package InstallerVersion="200" Compressed="yes" InstallScope="perMachine" />
    <MediaTemplate EmbedCab="yes" />
    <Property Id="ARPCOMMENTS" Value="initial installer logic" />
    <Directory Id="TARGETDIR" Name="SourceDir"><Directory Id="ProgramFiles64Folder"><Directory Id="INSTALLFOLDER" Name="Metadata test">
      <Component Id="SignedPayload" Guid="*"><File Id="SignedFile" Source="$(var.Payload)\signed.exe" KeyPath="yes" /></Component>
      <Component Id="DataPayload" Guid="*"><File Id="DataFile" Source="$(var.Payload)\data.txt" KeyPath="yes" /></Component>
    </Directory></Directory></Directory>
    <Feature Id="Main" Level="1"><ComponentRef Id="SignedPayload" /><ComponentRef Id="DataPayload" /></Feature>
  </Product>
</Wix>
'@
    $wxs = Join-Path $root 'Product.wxs'
    [IO.File]::WriteAllText($wxs, $source)
    $object = Join-Path $root 'Product.wixobj'
    $msi = Join-Path $root 'sample.msi'
    & (Join-Path $WixBin 'candle.exe') -nologo -arch x64 "-dPayload=$root" -out $object $wxs | Out-Host
    Assert ($LASTEXITCODE -eq 0) 'Candle failed.'
    & (Join-Path $WixBin 'light.exe') -nologo -out $msi $object | Out-Host
    Assert ($LASTEXITCODE -eq 0) 'Light or Windows Installer validation failed.'
    & $helper -Mode Finalize -Path $msi -SourceDateEpoch $epoch | Out-Null
    return $msi
}

$a = Build-Fixture 'a' 'same payload'
Start-Sleep -Seconds 3
$b = Build-Fixture 'different-absolute-build-path' 'same payload'
Assert ((Get-FileHash -LiteralPath $a).Hash -eq (Get-FileHash -LiteralPath $b).Hash) 'Identical payloads produced different MSI bytes.'
$aInfo = Read-Msi $a
Assert ($aInfo.Code -match '^\{[0-9A-F]{8}-[0-9A-F]{4}-8[0-9A-F]{3}-[89AB][0-9A-F]{3}-[0-9A-F]{12}\}$') 'PackageCode is not an uppercase UUIDv8.'
$hash = (Get-FileHash -LiteralPath $a).Hash
& $helper -Mode Finalize -Path $a -SourceDateEpoch $epoch | Out-Null
Assert ((Get-FileHash -LiteralPath $a).Hash -eq $hash) 'Finalization is not idempotent.'

$changed = Build-Fixture 'changed-payload' 'different payload'
Assert ((Read-Msi $changed).Code -ne $aInfo.Code) 'A payload change reused PackageCode.'
$logic = Join-Path $OutputRoot 'changed-logic.msi'
Copy-Item -LiteralPath $a -Destination $logic
Change-Database $logic 'Logic'
& $helper -Mode Finalize -Path $logic -SourceDateEpoch $epoch | Out-Null
Assert ((Read-Msi $logic).Code -ne $aInfo.Code) 'An installer database change reused PackageCode.'

$signed = Join-Path $OutputRoot 'signature-stream.msi'
Copy-Item -LiteralPath $a -Destination $signed
# Presence of the Authenticode stream must reject even an invalid signature.
Change-Database $signed 'Signature' $signedPayload
$signedHash = (Get-FileHash -LiteralPath $signed).Hash
Assert-Rejected { & $helper -Mode Finalize -Path $signed -SourceDateEpoch $epoch } 'An MSI with a publisher signature was modified.' 'Refusing to modify a signed MSI'
Assert ((Get-FileHash -LiteralPath $signed).Hash -eq $signedHash) 'Signature refusal changed the MSI.'
$nested = Join-Path $OutputRoot 'nested-storage.msi'
Copy-Item -LiteralPath $a -Destination $nested
Change-Database $nested 'Storage' $a
& $helper -Mode Finalize -Path $nested -SourceDateEpoch $epoch | Out-Null
$nestedHash = (Get-FileHash -LiteralPath $nested).Hash
Start-Sleep -Seconds 3
& $helper -Mode Finalize -Path $nested -SourceDateEpoch $epoch | Out-Null
Assert ((Get-FileHash -LiteralPath $nested).Hash -eq $nestedHash) 'Substorage timestamps are not deterministic.'
$malformed = Join-Path $OutputRoot 'malformed.msi'
[IO.File]::WriteAllText($malformed, 'not a compound file')
$badHash = (Get-FileHash -LiteralPath $malformed).Hash
Assert-Rejected { & $helper -Mode Finalize -Path $malformed -SourceDateEpoch $epoch } 'A malformed MSI was accepted.' 'Invalid Windows Installer package'
Assert ((Get-FileHash -LiteralPath $malformed).Hash -eq $badHash) 'Malformed-package refusal changed the file.'
foreach ($invalidEpoch in @(315532799L, 4354819200L)) {
    Assert-Rejected { & $helper -Mode Finalize -Path $a -SourceDateEpoch $invalidEpoch } 'An epoch outside the DOS date range was accepted.' 'cabinet date range'
}
Assert ((Get-FileHash -LiteralPath $a).Hash -eq $hash) 'Epoch validation modified the MSI.'
$boundary = Join-Path $OutputRoot 'boundaries'
New-Item -ItemType Directory -Path $boundary | Out-Null
[IO.File]::WriteAllText((Join-Path $boundary 'data.txt'), 'boundary fixture')
foreach ($validEpoch in @(315532800L, 4354819199L)) {
    & $helper -Mode Prepare -Path $boundary -SourceDateEpoch $validEpoch
}

$extracted = Join-Path $OutputRoot 'extracted'
New-Item -ItemType Directory -Path $extracted | Out-Null
& $SevenZip e -tCompound -y "-o$extracted" $a '*.cab' '[5]SummaryInformation' | Out-Null
Assert ($LASTEXITCODE -eq 0) 'Cannot extract the embedded cabinet.'
# COM's displayed DATE uses the current native DST bias. Inspect the raw UTC
# FILETIME values instead of converting that display using .NET historical DST.
$summaryBytes = [IO.File]::ReadAllBytes((Join-Path $extracted '[5]SummaryInformation'))
$section = [BitConverter]::ToUInt32($summaryBytes, 44)
$propertyCount = [BitConverter]::ToUInt32($summaryBytes, $section + 4)
$found = 0
for ($i = 0; $i -lt $propertyCount; $i++) {
    $propertyId = [BitConverter]::ToUInt32($summaryBytes, $section + 8 + 8 * $i)
    if ($propertyId -in @(12, 13)) {
        $propertyOffset = $section + [BitConverter]::ToUInt32($summaryBytes, $section + 12 + 8 * $i)
        Assert ([BitConverter]::ToUInt32($summaryBytes, $propertyOffset) -eq 64) 'A summary timestamp is not a FILETIME.'
        Assert ([BitConverter]::ToInt64($summaryBytes, $propertyOffset + 4) -eq [DateTimeOffset]::FromUnixTimeSeconds($epoch).UtcDateTime.ToFileTimeUtc()) 'A raw summary timestamp differs from SOURCE_DATE_EPOCH.'
        $found++
    }
}
Assert ($found -eq 2) 'Missing source timestamps in summary information.'
$cab = Join-Path $extracted 'cab1.cab'
$bytes = [IO.File]::ReadAllBytes($cab)
$offset = [BitConverter]::ToUInt32($bytes, 16)
$count = [BitConverter]::ToUInt16($bytes, 28)
for ($i = 0; $i -lt $count; $i++) {
    $date = [BitConverter]::ToUInt16($bytes, $offset + 10)
    $time = [BitConverter]::ToUInt16($bytes, $offset + 12)
    Assert ($date -eq ((2026 - 1980) * 512 + 1 * 32 + 15)) 'Cabinet file date differs from the source date.'
    Assert ($time -eq (12 * 2048 + 34 * 32 + 28)) 'Cabinet file time differs from the source date (including native DST bias).'
    $offset += 16
    while ($bytes[$offset] -ne 0) { $offset++ }
    $offset++
}
& $SevenZip e -y "-o$extracted" $cab | Out-Null
Assert ($LASTEXITCODE -eq 0) 'Cannot extract the cabinet payload.'
Assert ((Get-FileHash -LiteralPath (Join-Path $extracted 'SignedFile')).Hash -eq $originalPayloadHash) 'MSI finalization modified the embedded signed binary.'
Assert ((Get-AuthenticodeSignature (Join-Path $extracted 'SignedFile')).Status -eq 'Valid') 'The embedded payload signature no longer validates.'

Write-Output "Passed: byte equality, flat/nested storage idempotence, payload/database PackageCode changes, source dates, signed/malformed refusal, epoch bounds, embedded Authenticode. Evidence: $OutputRoot"
