$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$solution = Get-ChildItem -LiteralPath $projectRoot -Filter '*.slnx' | Select-Object -First 1
if (-not $solution) { throw 'Solution file not found.' }
$projectName = $solution.BaseName
$dotnet = $null
foreach ($runtimeRoot in @($env:DOTNET_ROOT_X64, $env:DOTNET_ROOT)) {
    if ($runtimeRoot -and (Test-Path -LiteralPath (Join-Path $runtimeRoot 'dotnet.exe'))) {
        $dotnet = Join-Path $runtimeRoot 'dotnet.exe'
        break
    }
}
if (-not $dotnet) {
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dotnetCommand) { $dotnet = $dotnetCommand.Source }
}
if (-not $dotnet) { throw 'Install .NET 10 SDK or set DOTNET_ROOT_X64 to its directory.' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.build/dotnet-home'
$env:TEMP = Join-Path $projectRoot '.build/temp'
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Path $env:TEMP -Force | Out-Null
