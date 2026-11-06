# 배포한 DLL과 의존성만으로 새 소비자를 복원·빌드·실행한다. 게시 작업은 하지 않는다.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDirectory,
    [string]$DependencySource = 'https://api.nuget.org/v3/index.json',
    [ValidateRange(27001, 27198)]
    [int]$Port = 27180
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-ConsumerDotNet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed (exit $LASTEXITCODE)."
    }
}

function Write-ConsumerFile {
    param([string]$Path, [string]$Content)
    [IO.File]::WriteAllText($Path, $Content, (New-Object Text.UTF8Encoding($false)))
}

function Escape-Xml {
    param([string]$Value)
    return [Security.SecurityElement]::Escape($Value)
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$sourceDirectory = (Resolve-Path -LiteralPath $PackageDirectory).Path
if ($DependencySource -notmatch '^https?://') {
    $DependencySource = (Resolve-Path -LiteralPath $DependencySource).Path
}
$runDirectory = Join-Path $repoRoot ('artifacts/package-consumer/' + [Guid]::NewGuid().ToString('N'))
$feed = Join-Path $runDirectory 'feed'
$cache = Join-Path $runDirectory 'cache'
$null = New-Item -ItemType Directory -Path $feed, $cache -Force

Add-Type -AssemblyName System.IO.Compression.FileSystem
$versions = @{}
$packageFiles = @{}
foreach ($file in @(Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.nupkg')) {
    $archive = [IO.Compression.ZipFile]::OpenRead($file.FullName)
    try {
        $entries = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec') })
        if ($entries.Count -ne 1) { throw "Invalid package manifest: $file" }
        $reader = New-Object IO.StreamReader($entries[0].Open())
        try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $id = [string]$manifest.package.metadata.id
        if ($id -notin @('KcpCSharpR1', 'KcpCSharpR1.Transport')) { continue }
        if ($versions.ContainsKey($id)) { throw "More than one version of $id found. Supply a fresh pack directory." }
        $versions[$id] = [string]$manifest.package.metadata.version
        $packageFiles[$id] = $file.FullName
        Copy-Item -LiteralPath $file.FullName -Destination $feed
    }
    finally { $archive.Dispose() }
}
if ($versions.Count -ne 2 -or $versions['KcpCSharpR1'] -ne $versions['KcpCSharpR1.Transport']) {
    throw 'Expected one Core and one Transport package with matching versions.'
}
$version = $versions['KcpCSharpR1.Transport']
$packageVersionPath = $version.ToLowerInvariant()

# 저장소/사용자 빌드 속성, NuGet 피드, 전역 캐시가 소비자 검증을 대신하지 않게 격리한다.
Write-ConsumerFile (Join-Path $runDirectory 'Directory.Build.props') @'
<Project>
  <PropertyGroup>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <NuGetAudit>false</NuGetAudit>
    <DisableImplicitNuGetFallbackFolder>true</DisableImplicitNuGetFallbackFolder>
    <RestoreFallbackFolders></RestoreFallbackFolders>
  </PropertyGroup>
</Project>
'@
Write-ConsumerFile (Join-Path $runDirectory 'Directory.Build.targets') '<Project />'
$config = Join-Path $runDirectory 'NuGet.Config'
$configTemplate = @'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="fresh-local-packages" value="__FEED__" />
    <add key="expected-dependencies" value="__DEPENDENCIES__" />
  </packageSources>
  <fallbackPackageFolders><clear /></fallbackPackageFolders>
  <packageSourceMapping>
    <clear />
    <packageSource key="fresh-local-packages">
      <package pattern="KcpCSharpR1" />
      <package pattern="KcpCSharpR1.Transport" />
    </packageSource>
    <packageSource key="expected-dependencies">
      <package pattern="System.Threading.Channels" />
    </packageSource>
  </packageSourceMapping>
</configuration>
'@
Write-ConsumerFile $config ($configTemplate.Replace('__FEED__', (Escape-Xml $feed)).Replace(
    '__DEPENDENCIES__', (Escape-Xml $DependencySource)))

$projectTemplate = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>__FRAMEWORK__</TargetFramework>
    <OutputType>__OUTPUT__</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="KcpCSharpR1.Transport" Version="[__VERSION__]" PrivateAssets="all" />
  </ItemGroup>
</Project>
'@

function Assert-ConsumerAssets {
    param([string]$ProjectDirectory, [string]$Framework)
    $assets = Get-Content -Raw -LiteralPath (Join-Path $ProjectDirectory 'obj/project.assets.json') | ConvertFrom-Json
    $expectedIds = @('KcpCSharpR1', 'KcpCSharpR1.Transport')
    if ($Framework -eq 'netstandard2.1') { $expectedIds += 'System.Threading.Channels' }
    $libraries = @($assets.libraries.PSObject.Properties)
    if ($libraries.Count -ne $expectedIds.Count) { throw "Unexpected dependency count for $Framework." }
    foreach ($library in $libraries) {
        $id = $library.Name.Split('/')[0]
        if ($library.Value.type -ne 'package' -or $id -notin $expectedIds) {
            throw "Unexpected consumer dependency: $($library.Name) ($($library.Value.type))."
        }
    }
    $folders = @($assets.packageFolders.PSObject.Properties.Name)
    if ($folders.Count -ne 1 -or [IO.Path]::GetFullPath($folders[0]).TrimEnd('\', '/') -ne $cache.TrimEnd('\', '/')) {
        throw "Consumer restore used a non-isolated package folder: $($folders -join ', ')."
    }
    $target = $assets.targets.PSObject.Properties[$Framework].Value
    foreach ($id in @('KcpCSharpR1', 'KcpCSharpR1.Transport')) {
        $library = $target.PSObject.Properties["$id/$version"].Value
        $compileAssets = @($library.compile.PSObject.Properties.Name)
        if ("lib/$Framework/$id.dll" -notin $compileAssets) { throw "Wrong compile asset for $id/$Framework." }
        $restored = Join-Path $cache ($id.ToLowerInvariant() + '/' + $version.ToLowerInvariant() + '/' +
            $id.ToLowerInvariant() + '.' + $version.ToLowerInvariant() + '.nupkg')
        if ((Get-FileHash -LiteralPath $restored).Hash -ne (Get-FileHash -LiteralPath $packageFiles[$id]).Hash) {
            throw "Restored $id differs from the freshly packed input."
        }
    }
}

$probeSource = Join-Path $repoRoot 'tests/PackageConsumer/PackageProbe.cs'
$programSource = Join-Path $repoRoot 'tests/PackageConsumer/Program.cs'
Push-Location $runDirectory
try {
    foreach ($framework in @('net8.0', 'netstandard2.1')) {
        $modeDirectory = Join-Path $runDirectory $framework
        $null = New-Item -ItemType Directory -Path $modeDirectory -Force
        $project = Join-Path $modeDirectory 'PackageConsumer.csproj'
        $output = if ($framework -eq 'net8.0') { 'Exe' } else { 'Library' }
        Write-ConsumerFile $project ($projectTemplate.Replace('__FRAMEWORK__', $framework).Replace(
            '__OUTPUT__', $output).Replace('__VERSION__', (Escape-Xml $version)))
        Copy-Item -LiteralPath $probeSource -Destination $modeDirectory
        if ($framework -eq 'net8.0') { Copy-Item -LiteralPath $programSource -Destination $modeDirectory }

        Invoke-ConsumerDotNet @('restore', $project, '--configfile', $config, '--packages', $cache,
            '-p:RestoreFallbackFolders=')
        Assert-ConsumerAssets $modeDirectory $framework
        Invoke-ConsumerDotNet @('build', $project, '-c', 'Release', '--no-restore')

        if ($framework -eq 'net8.0') {
            Invoke-ConsumerDotNet @((Join-Path $modeDirectory 'bin/Release/net8.0/PackageConsumer.dll'),
                '.NETCoreApp,Version=v8.0', [string]$Port)
        }
        else {
            # net8 호스트가 NuGet의 가까운 net8 자산을 다시 선택하지 않도록 복원한 std 자산을 명시한다.
            $hostDirectory = Join-Path $runDirectory 'netstandard-host'
            $null = New-Item -ItemType Directory -Path $hostDirectory -Force
            Copy-Item -LiteralPath $programSource -Destination $hostDirectory
            $hostProject = Join-Path $hostDirectory 'NetStandardHost.csproj'
            $hostTemplate = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../netstandard2.1/PackageConsumer.csproj" />
    <Reference Include="KcpCSharpR1"><HintPath>__CORE__</HintPath><Private>true</Private></Reference>
    <Reference Include="KcpCSharpR1.Transport"><HintPath>__TRANSPORT__</HintPath><Private>true</Private></Reference>
  </ItemGroup>
</Project>
'@
            $coreDll = Join-Path $cache "kcpcsharpr1/$packageVersionPath/lib/netstandard2.1/KcpCSharpR1.dll"
            $transportDll = Join-Path $cache "kcpcsharpr1.transport/$packageVersionPath/lib/netstandard2.1/KcpCSharpR1.Transport.dll"
            Write-ConsumerFile $hostProject ($hostTemplate.Replace('__CORE__', (Escape-Xml $coreDll)).Replace(
                '__TRANSPORT__', (Escape-Xml $transportDll)))
            Invoke-ConsumerDotNet @('restore', $hostProject, '--configfile', $config, '--packages', $cache,
                '-p:RestoreFallbackFolders=')
            Invoke-ConsumerDotNet @('build', $hostProject, '-c', 'Release', '--no-restore')
            Invoke-ConsumerDotNet @((Join-Path $hostDirectory 'bin/Release/net8.0/NetStandardHost.dll'),
                '.NETStandard,Version=v2.1', [string]$Port)
        }
    }
    Write-Host "Package consumer verification passed. Artifacts: $runDirectory"
    Write-Host 'netstandard2.1 assets were compiled as .NET Standard and executed on a .NET 8 host; this is not an engine/AOT certification.'
}
finally { Pop-Location }
