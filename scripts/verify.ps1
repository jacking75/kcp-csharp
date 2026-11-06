# 로컬 품질 게이트. 게시나 저장소 설정 변경 없이 검증 산출물만 만든다.
[CmdletBinding()]
param(
    [switch]$SkipRestore,
    [switch]$SkipSoak
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-DotNet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed (exit $LASTEXITCODE)."
    }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
# 이전 실행의 패키지나 커버리지 결과를 성공으로 오인하지 않도록 실행마다 격리한다.
$runDirectory = Join-Path $repoRoot ('artifacts/verify/' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $runDirectory -Force
$coveragePath = Join-Path $runDirectory 'coverage.json'
$packageDirectory = Join-Path $runDirectory 'packages'

Push-Location $repoRoot
try {
    if (-not $SkipRestore) {
        Invoke-DotNet @('restore', 'KcpCSharpR1.sln')
    }
    Invoke-DotNet @('build', 'KcpCSharpR1.sln', '-c', 'Release', '--no-restore')

    $testArguments = @('test', 'KcpCSharpR1.sln', '-c', 'Release', '--no-build', '--no-restore',
        '--logger', 'trx', '--results-directory', (Join-Path $runDirectory 'tests'))
    if ($SkipSoak) { $testArguments += @('--filter', 'Category!=Soak') }
    Invoke-DotNet $testArguments

    # 두 테스트 어셈블리의 코어 커버리지를 하나의 절대 경로에 합친 뒤 게이트를 적용한다.
    $coverageArguments = @('-c', 'Release', '--no-build', '--no-restore',
        '-p:CollectCoverage=true', '-p:Include=[KcpCSharpR1]*',
        '-p:CoverletOutputFormat=json', "-p:CoverletOutput=$coveragePath")
    Invoke-DotNet (@('test', 'tests/KcpCSharpR1.Tests') + $coverageArguments)
    Invoke-DotNet (@('test', 'tests/KcpCSharpR1.SimTests', '--filter', 'Category!=Soak') +
        $coverageArguments + @("-p:MergeWith=$coveragePath", '-p:Threshold=85', '-p:ThresholdType=line'))

    foreach ($package in @('KcpCSharpR1', 'KcpCSharpR1.Transport')) {
        Invoke-DotNet @('pack', "src/$package/$package.csproj", '-c', 'Release',
            '--no-build', '--no-restore', '-o', $packageDirectory)
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $versions = @{}
    $dependencies = @{}
    $packages = @(Get-ChildItem -LiteralPath $packageDirectory -Filter '*.nupkg')
    if ($packages.Count -ne 2) { throw 'Expected exactly two NuGet packages.' }
    foreach ($packageFile in $packages) {
        $archive = [IO.Compression.ZipFile]::OpenRead($packageFile.FullName)
        try {
            $manifest = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec') })
            if ($manifest.Count -ne 1) { throw "Invalid manifest: $packageFile" }
            $reader = New-Object IO.StreamReader($manifest[0].Open())
            try { [xml]$nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $metadata = $nuspec.package.metadata
            $id = [string]$metadata.id
            if ($id -notin @('KcpCSharpR1', 'KcpCSharpR1.Transport')) { throw "Unexpected package: $id" }
            $versions[$id] = [string]$metadata.version
            foreach ($entry in @('README.md', 'LICENSE', "lib/net8.0/$id.dll", "lib/net8.0/$id.xml",
                    "lib/netstandard2.1/$id.dll", "lib/netstandard2.1/$id.xml")) {
                if ($null -eq $archive.GetEntry($entry)) { throw "$id is missing $entry" }
            }
            if ($id -eq 'KcpCSharpR1.Transport') {
                foreach ($group in $metadata.dependencies.group) {
                    $coreDependency = @($group.dependency | Where-Object { $_.id -eq 'KcpCSharpR1' })
                    if ($coreDependency.Count -ne 1) { throw 'Transport must depend on core in each target.' }
                    $dependencies[[string]$group.targetFramework] = [string]$coreDependency[0].version
                }
            }
        }
        finally { $archive.Dispose() }
        $symbols = [IO.Path]::ChangeExtension($packageFile.FullName, '.snupkg')
        if (-not (Test-Path -LiteralPath $symbols)) { throw "Missing symbols: $symbols" }
    }
    if ($versions.Count -ne 2 -or $versions['KcpCSharpR1'] -ne $versions['KcpCSharpR1.Transport']) {
        throw 'Core and Transport package versions must match.'
    }
    foreach ($target in @('net8.0', '.NETStandard2.1')) {
        if ($dependencies[$target] -ne $versions['KcpCSharpR1']) {
            throw "Transport dependency version mismatch for $target."
        }
    }
    # 프로젝트 참조가 아닌 실제 NuGet 설치 경로로 소켓·의존성·타깃 분기를 검증한다.
    & (Join-Path $PSScriptRoot 'verify-package-consumer.ps1') -PackageDirectory $packageDirectory
    Write-Host "Verification passed. Artifacts: $runDirectory"
}
finally { Pop-Location }
