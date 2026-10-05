[CmdletBinding()]
param([switch]$SelfContained)
. (Join-Path $PSScriptRoot 'common.ps1')
$appProject = Join-Path $projectRoot "src/$projectName.App/$projectName.App.csproj"
$releaseDirectory = Join-Path $projectRoot 'release'
$arguments = @('publish', $appProject, '-c', 'Release', '-r', 'win-x64', '-p:Platform=x64', '-p:PublishTrimmed=false', '-o', $releaseDirectory, '--nologo', '--self-contained', $SelfContained.IsPresent.ToString().ToLowerInvariant())
& $dotnet @arguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$nativeLicense = if ($env:MICPILOT_NATIVE_DIR) { Join-Path $env:MICPILOT_NATIVE_DIR 'RNNoise-LICENSE.txt' } else { Join-Path $projectRoot 'native/rnnoise/RNNoise-LICENSE.txt' }
if ($projectName -eq 'MicPilot' -and (Test-Path -LiteralPath $nativeLicense)) {
    Copy-Item -LiteralPath $nativeLicense -Destination $releaseDirectory
}
Write-Output (Join-Path $releaseDirectory "$projectName.exe")
