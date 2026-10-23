# Writes update.json and update.json.sig for a release (docs/superpowers/specs/2026-10-17-auto-update-design.md).
# The private key (PEM) comes from $env:UPDATE_SIGNING_KEY; the signature is checked against the public key before exit.
# Needs PowerShell 7 (.NET ECDsa PEM import).
param(
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$Setup,
    [Parameter(Mandatory)] [string]$NotesFile,
    [Parameter(Mandatory)] [string]$OutDir,
    [string]$PublicKeyPem
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or newer is required' }
if (-not $env:UPDATE_SIGNING_KEY) { throw 'UPDATE_SIGNING_KEY is not set' }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Bad version: $Version" }
if (-not $PublicKeyPem) { $PublicKeyPem = Get-Content -Raw (Join-Path $PSScriptRoot 'update-signing-public.pem') }

$name = Split-Path $Setup -Leaf
$size = (Get-Item $Setup).Length
$manifest = [ordered]@{
    version   = $Version
    published = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    notes     = (Get-Content -Raw -Encoding utf8 $NotesFile).Trim()
    files     = [ordered]@{
        'win-x64-setup' = [ordered]@{
            name   = $name
            url    = "https://github.com/WaterCa7/WaterCa7-s-pc-cleaner/releases/download/v$Version/$name"
            size   = $size
            sha256 = (Get-FileHash -Algorithm SHA256 $Setup).Hash.ToLowerInvariant()
        }
    }
}
New-Item -ItemType Directory -Force $OutDir | Out-Null
$json = [System.Text.UTF8Encoding]::new($false).GetBytes(($manifest | ConvertTo-Json -Depth 5))
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'update.json'), $json)

$sha256 = [System.Security.Cryptography.HashAlgorithmName]::SHA256
$der = [System.Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence
$key = [System.Security.Cryptography.ECDsa]::Create()
$key.ImportFromPem($env:UPDATE_SIGNING_KEY)
$signature = $key.SignData($json, $sha256, $der)
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'update.json.sig'), $signature)

$public = [System.Security.Cryptography.ECDsa]::Create()
$public.ImportFromPem($PublicKeyPem)
if (-not $public.VerifyData($json, $signature, $sha256, $der)) { throw 'The signature does not match the public key' }
"update.json: version $Version, $name, $size bytes"
