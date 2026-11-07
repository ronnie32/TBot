param(
    [string]$Version = '3.6.0-beta1',
    [string]$AssemblyVersion = '3.6.0.1',
    [string]$ReleaseName = 'release-build-3.6.0-beta1'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$releaseRoot = Join-Path $repoRoot $ReleaseName
if (Test-Path -LiteralPath $releaseRoot) { throw "Release directory already exists: $releaseRoot" }
New-Item -ItemType Directory -Path $releaseRoot | Out-Null
$goCommit = (git -C (Join-Path $repoRoot 'ogame') rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve OGameD commit' }

$platforms = @(
    @{ Name='win64'; Runtime='win-x64'; OS='windows'; Arch='amd64'; Arm=$null; Binary='ogamed.exe' },
    @{ Name='win32'; Runtime='win-x86'; OS='windows'; Arch='386'; Arm=$null; Binary='ogamed.exe' },
    @{ Name='linux64'; Runtime='linux-x64'; OS='linux'; Arch='amd64'; Arm=$null; Binary='ogamed' },
    @{ Name='linuxarm'; Runtime='linux-arm'; OS='linux'; Arch='arm'; Arm='7'; Binary='ogamed' },
    @{ Name='linuxarm64'; Runtime='linux-arm64'; OS='linux'; Arch='arm64'; Arm=$null; Binary='ogamed' },
    @{ Name='osx64'; Runtime='osx-x64'; OS='darwin'; Arch='amd64'; Arm=$null; Binary='ogamed' },
    @{ Name='osx-arm64'; Runtime='osx-arm64'; OS='darwin'; Arch='arm64'; Arm=$null; Binary='ogamed' }
)

foreach ($platform in $platforms) {
    $packageRoot = Join-Path $releaseRoot $platform.Name
    Write-Output "Building $($platform.Name) $Version"
    dotnet publish (Join-Path $repoRoot 'TBot') -c Release `
        -p:Version=$Version -p:AssemblyVersion=$AssemblyVersion -p:FileVersion=$AssemblyVersion `
        -p:InformationalVersion=$Version -r $platform.Runtime -o $packageRoot `
        -p:PublishSingleFile=true --self-contained true
    if ($LASTEXITCODE -ne 0) { throw "TBot publish failed: $($platform.Name)" }

    $oldOS = $env:GOOS
    $oldArch = $env:GOARCH
    $oldArm = $env:GOARM
    Push-Location (Join-Path $repoRoot 'ogame')
    try {
        $env:GOOS = $platform.OS
        $env:GOARCH = $platform.Arch
        $env:GOARM = $platform.Arm
        go build -ldflags "-X main.version=$Version -X main.commit=$goCommit" `
            -o (Join-Path $packageRoot $platform.Binary) ./cmd/ogamed-tbot
        if ($LASTEXITCODE -ne 0) { throw "OGameD build failed: $($platform.Name)" }
    } finally {
        Pop-Location
        $env:GOOS = $oldOS
        $env:GOARCH = $oldArch
        $env:GOARM = $oldArm
    }

    foreach ($name in @('README.md', 'CHANGELOG.md')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot $name) -Destination $packageRoot
    }
    New-Item -ItemType Directory -Path (Join-Path $packageRoot 'docs'), (Join-Path $packageRoot 'scripts') | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/autofarm-combat-planner.md') -Destination (Join-Path $packageRoot 'docs')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts/compare-combat-observation.py') -Destination (Join-Path $packageRoot 'scripts')
    $null = Get-Content -LiteralPath (Join-Path $packageRoot 'settings.json') -Raw | ConvertFrom-Json
    $null = Get-Content -LiteralPath (Join-Path $packageRoot 'instance_settings.json') -Raw | ConvertFrom-Json
    $unsafeFiles = Get-ChildItem -LiteralPath $packageRoot -Recurse -Force -File | Where-Object {
        $_.Name -match '(?i)(^|\.)(db|sqlite|log|trx)$|cookie|session|e2e|testhost|xunit|TBot\.Tests'
    }
    if ($unsafeFiles) { throw "Unexpected runtime data or tests in package: $($unsafeFiles.FullName -join ', ')" }

    $zipPath = Join-Path $releaseRoot "TBot-v$Version-$($platform.Name).zip"
    # zipfile preserves Unix executable modes for Linux/macOS executables.
    @'
import pathlib, stat, sys, zipfile
root=pathlib.Path(sys.argv[1])
with zipfile.ZipFile(sys.argv[2], 'x', compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
    for path in sorted(root.rglob('*')):
        if not path.is_file(): continue
        info=zipfile.ZipInfo.from_file(path, path.relative_to(root).as_posix())
        info.compress_type=zipfile.ZIP_DEFLATED
        info.create_system=3
        mode=0o755 if path.name in ('TBot','ogamed') else 0o644
        info.external_attr=(stat.S_IFREG|mode)<<16
        with path.open('rb') as source, archive.open(info, 'w') as target:
            import shutil
            shutil.copyfileobj(source,target)
'@ | python - $packageRoot $zipPath
    if ($LASTEXITCODE -ne 0) { throw "Archive failed: $($platform.Name)" }
    Write-Output "Packaged $zipPath"
}
Write-Output "All seven $Version packages built."
