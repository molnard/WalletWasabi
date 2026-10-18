[CmdletBinding()]
param(
	[Parameter(Mandatory = $true)]
	[ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
	[string] $Version,

	[ValidateSet('x86', 'x64', 'arm', 'arm64')]
	[string] $Architecture = 'x64',

	[ValidateRange(1, 65535)]
	[int] $Language = 1033
)

$ErrorActionPreference = 'Stop'

function New-NameBasedGuid([Guid] $Namespace, [string] $Name)
{
	# UUIDv5 hashes the namespace in network byte order followed by the UTF-8 name.
	[byte[]] $namespaceBytes = $Namespace.ToByteArray()
	[Array]::Reverse($namespaceBytes, 0, 4)
	[Array]::Reverse($namespaceBytes, 4, 2)
	[Array]::Reverse($namespaceBytes, 6, 2)

	$sha1 = [Security.Cryptography.SHA1]::Create()
	try
	{
		[byte[]] $hash = $sha1.ComputeHash($namespaceBytes + [Text.Encoding]::UTF8.GetBytes($Name))
	}
	finally
	{
		$sha1.Dispose()
	}

	[byte[]] $guidBytes = New-Object byte[] 16
	[Array]::Copy($hash, $guidBytes, 16)
	$guidBytes[6] = ($guidBytes[6] -band 0x0f) -bor 0x50
	$guidBytes[8] = ($guidBytes[8] -band 0x3f) -bor 0x80

	[Array]::Reverse($guidBytes, 0, 4)
	[Array]::Reverse($guidBytes, 4, 2)
	[Array]::Reverse($guidBytes, 6, 2)
	([Guid]::new($guidBytes)).ToString('B').ToUpperInvariant()
}

# Assembly versions have four fields; release tags can omit a trailing zero.
$parsedVersion = [Version] $Version
$normalizedVersion = '{0}.{1}.{2}.{3}' -f $parsedVersion.Major, $parsedVersion.Minor, $parsedVersion.Build, [Math]::Max(0, $parsedVersion.Revision)

# Use the existing upgrade code as the namespace for this product family.
New-NameBasedGuid 'D8E80773-D8CF-4031-B8B3-AA750AF0B413' "$normalizedVersion|$($Architecture.ToLowerInvariant())|$Language"
