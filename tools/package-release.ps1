[CmdletBinding()]
param(
    [string]$Version = '0.1.0-beta.1',
    [switch]$SkipTests
)
. (Join-Path $PSScriptRoot 'common.ps1')
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[a-zA-Z0-9.-]+)?$') { throw 'Invalid release version.' }
if ($projectName -eq 'MicPilot') {
    $nativeDirectory = if ($env:MICPILOT_NATIVE_DIR) { $env:MICPILOT_NATIVE_DIR } else { Join-Path $projectRoot 'native/rnnoise' }
    foreach ($name in @('rnnoise.dll', 'RNNoise-LICENSE.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $nativeDirectory $name))) { throw "RNNoise is incomplete: $name" }
    }
}
if (-not $SkipTests) {
    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Build or offline checks failed.' }
}
$stage = Join-Path $projectRoot "artifacts/$projectName-$Version-win-x64"
if (Test-Path -LiteralPath $stage) { throw 'Output already exists. Use a new version or a clean checkout.' }
[IO.Directory]::CreateDirectory($stage) | Out-Null
& $dotnet publish (Join-Path $projectRoot "src/$projectName.App/$projectName.App.csproj") -c Release -r win-x64 -p:Platform=x64 -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false "-p:Version=$Version" -p:AssemblyVersion=0.1.0.0 -p:FileVersion=0.1.0.0 --self-contained true -o $stage --nologo -maxcpucount:2
if ($LASTEXITCODE -ne 0) { throw 'Publishing failed.' }
foreach ($name in @('README.md', 'THIRD-PARTY-NOTICES.txt', 'THIRD-PARTY-NOTICES.md', 'INSTALL.md')) {
    $source = Join-Path $projectRoot $name
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $stage }
}
$licenses = Join-Path $stage 'licenses'
[IO.Directory]::CreateDirectory($licenses) | Out-Null
$dotnetRoot = Split-Path -Parent $dotnet
foreach ($name in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
    $source = Join-Path $dotnetRoot $name
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $licenses "dotnet-$name") }
}
$naudioLicense = Join-Path $projectRoot 'docs/NAudio-LICENSE.txt'
if (Test-Path -LiteralPath $naudioLicense) { Copy-Item -LiteralPath $naudioLicense -Destination $licenses }
if ($projectName -eq 'MicPilot') {
    Copy-Item -LiteralPath (Join-Path $nativeDirectory 'RNNoise-LICENSE.txt') -Destination $licenses
}
if ($projectName -eq 'VoiceMorph') {
    [IO.Directory]::CreateDirectory((Join-Path $stage 'tools')) | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'setup-rvc-runtime.ps1') -Destination (Join-Path $stage 'tools')
}
if ($projectName -eq 'StreamCensor') {
    $pluginRoot = Join-Path $projectRoot 'native/streamcensor-filter/out'
    $pluginDll = Join-Path $pluginRoot 'streamcensor-filter/bin/64bit/streamcensor-filter.dll'
    if (-not (Test-Path -LiteralPath $pluginDll)) { throw 'Build the OBS filter before packaging.' }
    $destination = Join-Path $stage 'native/streamcensor-filter/out'
    [IO.Directory]::CreateDirectory($destination) | Out-Null
    Copy-Item -LiteralPath (Join-Path $pluginRoot 'streamcensor-filter'), (Join-Path $pluginRoot 'runtime') -Destination $destination -Recurse
    [IO.Directory]::CreateDirectory((Join-Path $stage 'tools')) | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'run-obs-dev.ps1') -Destination (Join-Path $stage 'tools')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'native/streamcensor-filter/COPYING') -Destination (Join-Path $licenses 'OBS-filter-GPL-2.0.txt')
    $nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
    foreach ($item in @(@('microsoft.windowsappsdk/2.5.1/license.txt','WindowsAppSDK-LICENSE.txt'), @('microsoft.windowsappsdk/2.5.1/NOTICE.txt','WindowsAppSDK-NOTICE.txt'))) {
        $source = Join-Path $nugetRoot $item[0]
        if (-not (Test-Path -LiteralPath $source)) { throw "Missing license: $source" }
        Copy-Item -LiteralPath $source -Destination (Join-Path $licenses $item[1])
    }
}
$forbidden = Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object {
    $_.Extension -in @('.pdb','.wav','.mp3','.mp4','.mkv','.pth','.pt','.index','.onnx','.pem','.key','.pfx','.jks','.keystore') -or
    $_.Name -match '^\.env$|^AGENTS\.md$|^CLAUDE\.md$|^settings\.json$'
}
if ($forbidden) { throw "Unexpected private/development files: $($forbidden.Name -join ', ')" }
$gitSha = & git -c "safe.directory=$($projectRoot.Replace('\','/'))" -C $projectRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot determine source commit.' }
$buildInfo = [ordered]@{ application=$projectName; version=$Version; source="https://github.com/xChessman-dev/$projectName"; commit=$gitSha; platform='win-x64'; selfContained=$true; neuralModelsIncluded=$false }
[IO.File]::WriteAllText((Join-Path $stage 'BUILD-INFO.json'), ($buildInfo | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
$archive = Join-Path $projectRoot "artifacts/$projectName-$Version-win-x64.zip"
Compress-Archive -LiteralPath $stage -DestinationPath $archive -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$archive.sha256", "$hash  $([IO.Path]::GetFileName($archive))" + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
Write-Output "Package: $archive"
Write-Output "SHA256: $hash"
