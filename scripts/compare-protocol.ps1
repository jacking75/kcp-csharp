param([string]$BaselineRef = '4e4d990', [switch]$PrepareOnly)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$outputRoot = Join-Path $repoRoot 'artifacts/protocol-comparison'
$utf8 = New-Object System.Text.UTF8Encoding($false)

Push-Location $repoRoot
try {
    $resolved = git rev-parse --verify ($BaselineRef + '^{commit}')
    if ($LASTEXITCODE -ne 0) { throw '기준 커밋을 찾을 수 없다.' }
    Write-Host "Baseline: $resolved"
    foreach ($variant in 'Baseline', 'Candidate') {
        New-Item -ItemType Directory -Force (Join-Path $outputRoot $variant) | Out-Null
    }
    foreach ($file in Get-ChildItem (Join-Path $repoRoot 'src/KcpCSharpR1') -Filter '*.cs') {
        $original = git show ($resolved + ':src/KcpCSharpR1/' + $file.Name)
        if ($LASTEXITCODE -ne 0) { throw "기준 소스를 읽지 못했다: $($file.Name)" }
        [IO.File]::WriteAllText((Join-Path $outputRoot ('Baseline/' + $file.Name)),
            (($original -join "`n").Replace('namespace KcpCSharpR1;', 'namespace Baseline;')), $utf8)
        [IO.File]::WriteAllText((Join-Path $outputRoot ('Candidate/' + $file.Name)),
            ([IO.File]::ReadAllText($file.FullName).Replace('namespace KcpCSharpR1;', 'namespace Candidate;')), $utf8)
    }
    if (!$PrepareOnly) {
        dotnet run -c Release --project benchmarks/ProtocolComparison/ProtocolComparison.csproj
        if ($LASTEXITCODE -ne 0) { throw '벤치마크 실행에 실패했다.' }
    }
}
finally { Pop-Location }
