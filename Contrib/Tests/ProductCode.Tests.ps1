$ErrorActionPreference = 'Stop'
$helper = Join-Path $PSScriptRoot '..\Get-ProductCode.ps1'

function Assert-Equal($Actual, $Expected, [string] $Case)
{
	if ($Actual -cne $Expected)
	{
		throw "$Case`: expected '$Expected', got '$Actual'."
	}
	[PSCustomObject]@{ Case = $Case; Result = 'PASS'; Value = $Actual }
}

# Dot-source the helper so the UUIDv5 primitive can be checked against the RFC.
. $helper -Version '2.8.4' | Out-Null
$rfc = New-NameBasedGuid '6ba7b810-9dad-11d1-80b4-00c04fd430c8' 'www.example.com'
Assert-Equal $rfc '{2ED6657D-E927-568B-95E1-2665A8AEA6A2}' 'RFC 9562 Appendix A.4 UUIDv5 vector'

$expected = '{AA41F6B7-7E1E-5E11-B101-775DE3D1CCBC}'
Assert-Equal (& $helper -Version '2.8.4') $expected 'Known product identity'
Assert-Equal (& $helper -Version '2.8.4') $expected 'Repeated invocation'
Assert-Equal (& $helper -Version '2.8.4.0') $expected 'MSBuild and release tag version equivalence'
Assert-Equal (& $helper -Version '2.8.4' -Architecture 'X64') $expected 'Architecture case normalization'

$variants = @(
	@{ Version = '2.8.5'; Architecture = 'x64'; Language = 1033 },
	@{ Version = '2.8.4.1'; Architecture = 'x64'; Language = 1033 },
	@{ Version = '2.8.4'; Architecture = 'x86'; Language = 1033 },
	@{ Version = '2.8.4'; Architecture = 'arm64'; Language = 1033 },
	@{ Version = '2.8.4'; Architecture = 'x64'; Language = 1031 }
)
$identities = @($expected)
foreach ($variant in $variants)
{
	$actual = & $helper @variant
	if ($actual -cin $identities)
	{
		throw "Different version, architecture or language reused ProductCode '$actual'."
	}
	if ($actual -cnotmatch '^\{[0-9A-F]{8}-[0-9A-F]{4}-5[0-9A-F]{3}-[89AB][0-9A-F]{3}-[0-9A-F]{12}\}$')
	{
		throw "Unexpected UUIDv5 format '$actual'."
	}
	$identities += $actual
	[PSCustomObject]@{ Case = "Variant $($variant.Version)|$($variant.Architecture)|$($variant.Language)"; Result = 'PASS'; Value = $actual }
}

foreach ($invalid in @(
	@{ Version = '2.8' },
	@{ Version = '2.8.4-preview' },
	@{ Version = '2.8.4'; Architecture = 'invalid' },
	@{ Version = '2.8.4'; Language = 0 }
))
{
	$rejected = $false
	try
	{
		& $helper @invalid | Out-Null
	}
	catch
	{
		$rejected = $true
	}
	if (-not $rejected)
	{
		throw 'Invalid installer identity input was accepted.'
	}
	[PSCustomObject]@{ Case = "Reject invalid input $($invalid | ConvertTo-Json -Compress)"; Result = 'PASS'; Value = '' }
}
