[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$SkipTests
)
. (Join-Path $PSScriptRoot 'common.ps1')
$appProject = Join-Path $projectRoot "src/$projectName.App/$projectName.App.csproj"
& $dotnet build $appProject -c $Configuration -p:Platform=x64 --nologo -maxcpucount:2
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if (-not $SkipTests) {
    $testProject = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'tests') -Filter '*.csproj' -Recurse | Select-Object -First 1
    if ($testProject) {
        & $dotnet build $testProject.FullName -c $Configuration -p:Platform=x64 --nologo -maxcpucount:2
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        & $dotnet run --project $testProject.FullName -c $Configuration -p:Platform=x64 --no-build
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}
