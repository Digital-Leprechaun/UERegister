#requires -Version 5.1
[CmdletBinding()]
param([string]$MSBuildPath, [string]$BuiltExecutable, [string]$OutputRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (!$OutputRoot) { $OutputRoot = Join-Path $PSScriptRoot 'Releases' }
if (!$MSBuildPath) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $MSBuildPath = @(& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe') | Select-Object -First 1
    }
}
if (!$MSBuildPath -or !(Test-Path -LiteralPath $MSBuildPath)) { throw 'MSBuild not found. Install Visual Studio with .NET desktop build tools or supply -MSBuildPath.' }
$stage = Join-Path $PSScriptRoot ('UERegister\obj\Packaging\' + [guid]::NewGuid().ToString('N'))
$build = Join-Path $stage 'build'
$payload = Join-Path $stage 'portable'
$null = New-Item -ItemType Directory -Path $build, $payload
Push-Location $PSScriptRoot
try {
    $buildArgs = @('Tests\RegressionTests.csproj', '/t:Rebuild', '/p:Configuration=Release', '/p:SkipReleasePackaging=true', "/p:OutDir=$build\", '/v:minimal', '/nologo')
    if ($BuiltExecutable) {
        $BuiltExecutable = (Resolve-Path -LiteralPath $BuiltExecutable).Path
        $buildArgs += "/p:UERegisterAssemblyPath=$BuiltExecutable"
    }
    & $MSBuildPath @buildArgs
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    if ($BuiltExecutable) { Copy-Item -LiteralPath $BuiltExecutable, ($BuiltExecutable + '.config') -Destination $build -Force }
    & (Join-Path $build 'RegressionTests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Regression tests failed.' }
    $exe = Join-Path $build 'UERegister.exe'
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion
    [xml]$project = Get-Content -LiteralPath 'UERegister\UERegister.csproj'
    if ($project.SelectSingleNode("//*[local-name()='ApplicationVersion']").InnerText -ne $version) { throw 'Publish version does not match executable.' }
    Copy-Item -LiteralPath $exe, ($exe + '.config'), 'README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'CHANGELOG.md' -Destination $payload
    $zip = Join-Path $stage "UERegister-$version-x64-portable.zip"
    Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $zip -CompressionLevel Optimal
    $sha = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($zip)
    try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $sha.Dispose() }
    "$hash  $([IO.Path]::GetFileName($zip))" | Set-Content -LiteralPath (Join-Path $stage 'SHA256SUMS.txt') -Encoding ASCII
    $release = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) $version
    $null = New-Item -ItemType Directory -Path $release -Force
    Copy-Item -LiteralPath $zip, (Join-Path $stage 'SHA256SUMS.txt') -Destination $release -Force
    Write-Host "Release package ready (unsigned): $release"
}
finally { Pop-Location }
