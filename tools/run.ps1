. (Join-Path $PSScriptRoot 'common.ps1')
$executable = Join-Path $projectRoot "release/$projectName.exe"
if (-not (Test-Path -LiteralPath $executable)) { throw 'Run tools/publish.ps1 first.' }
Start-Process -FilePath $executable -WorkingDirectory (Split-Path -Parent $executable) -WindowStyle Normal
