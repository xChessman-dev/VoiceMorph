[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$RuntimeRoot,
    [switch]$CpuOnly,
    [switch]$KeepDownloads
)

$ErrorActionPreference = 'Stop'
$taskRuntime = [IO.Path]::GetFullPath($RuntimeRoot)
if ($taskRuntime.TrimEnd('\') -eq [IO.Path]::GetPathRoot($taskRuntime).TrimEnd('\')) { throw 'Choose a dedicated runtime directory, not a drive root.' }
$taskProject = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskDownloads = Join-Path $taskRuntime 'downloads'
$taskPythonDir = Join-Path $taskRuntime 'python'
$taskPython = Join-Path $taskPythonDir 'python.exe'
$taskTemp = Join-Path $taskRuntime 'temp'
$taskVendor = Join-Path $taskRuntime 'vendor'
$taskAssets = Join-Path $taskRuntime 'assets'
$taskFree = ([IO.DriveInfo]::new([IO.Path]::GetPathRoot($taskRuntime))).AvailableFreeSpace
$taskNeeded = if ($CpuOnly) { 2GB } else { 9GB }
if ($taskFree -lt $taskNeeded) { throw 'Not enough free space for the selected optional RVC runtime.' }
New-Item -ItemType Directory -Path $taskRuntime,$taskDownloads,$taskPythonDir,$taskTemp,$taskVendor,$taskAssets -Force | Out-Null

function Get-PinnedFile {
    param([string]$Url,[string]$Destination,[string]$Sha256,[long]$Bytes = 0)
    $taskFullDestination = [IO.Path]::GetFullPath($Destination)
    if (-not $taskFullDestination.StartsWith($taskRuntime + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Download destination escaped the runtime directory.' }
    if (Test-Path -LiteralPath $taskFullDestination) {
        if ((Get-FileHash -LiteralPath $taskFullDestination -Algorithm SHA256).Hash.ToLowerInvariant() -eq $Sha256) { return }
        throw "An existing runtime file has a different checksum: $taskFullDestination. It has not been overwritten."
    }
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($taskFullDestination)) -Force | Out-Null
    $taskPartial = $taskFullDestination + '.part'
    Write-Host ('Downloading {0} ({1:N1} MB)' -f [IO.Path]::GetFileName($taskFullDestination),($Bytes / 1MB))
    & curl.exe --fail --location --retry 3 --continue-at - --silent --show-error --output $taskPartial $Url
    if ($LASTEXITCODE -ne 0) { throw "Download failed: $Url. Partial data remains in the runtime directory for a retry." }
    if ($Bytes -gt 0 -and (Get-Item -LiteralPath $taskPartial).Length -ne $Bytes) { throw 'Unexpected official download size.' }
    if ((Get-FileHash -LiteralPath $taskPartial -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Sha256) { throw 'SHA-256 verification failed. Downloaded content was not executed.' }
    Move-Item -LiteralPath $taskPartial -Destination $taskFullDestination
}

$taskPythonZip = Join-Path $taskDownloads 'python-3.12.10-embed-amd64.zip'
if (-not (Test-Path -LiteralPath $taskPython)) {
    Get-PinnedFile 'https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-amd64.zip' $taskPythonZip '4acbed6dd1c744b0376e3b1cf57ce906f9dc9e95e68824584c8099a63025a3c3' 11133606
    Expand-Archive -LiteralPath $taskPythonZip -DestinationPath $taskPythonDir -Force
}
# The embeddable interpreter stays isolated: no user site or system Python search.
New-Item -ItemType Directory -Path (Join-Path $taskPythonDir 'Lib\site-packages') -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $taskPythonDir 'python312._pth'),"python312.zip`r`n.`r`nLib/site-packages`r`nimport site`r`n",[Text.Encoding]::ASCII)
$taskPipWheel = Join-Path $taskDownloads 'pip-25.2-py3-none-any.whl'
Get-PinnedFile 'https://files.pythonhosted.org/packages/b7/3f/945ef7ab14dc4f9d7f40288d2df998d1837ee0888ec3659c813487572faa/pip-25.2-py3-none-any.whl' $taskPipWheel '6d67a2b4e7f14d8b31b8b52648866fa717f45a1eb70e83002f4331d07e953717' 1752557
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (-not (Test-Path -LiteralPath (Join-Path $taskPythonDir 'Lib\site-packages\pip\__main__.py'))) {
    [IO.Compression.ZipFile]::ExtractToDirectory($taskPipWheel,(Join-Path $taskPythonDir 'Lib\site-packages'))
}

$taskOldTemp = $env:TEMP
$taskOldTmp = $env:TMP
try {
    $env:TEMP = $taskTemp
    $env:TMP = $taskTemp
    $env:NUMBA_CACHE_DIR = Join-Path $taskRuntime 'numba-cache'
    Write-Host 'Installing inference-only packages into the isolated runtime.'
    & $taskPython -I -m pip --isolated install --disable-pip-version-check --no-cache-dir --only-binary=:all: --index-url 'https://pypi.org/simple' -r (Join-Path $taskProject 'runtime\rvc-requirements.txt')
    if ($LASTEXITCODE -ne 0) { throw 'RVC dependencies could not be installed.' }
    $taskFlavor = if ($CpuOnly) { 'cpu' } else { 'cu128' }
    $taskTorchWheel = Join-Path $taskDownloads "torch-2.10.0+$taskFlavor-cp312-cp312-win_amd64.whl"
    [string]$taskInstalledTorch = & $taskPython -I -c "import importlib.util; print(__import__('torch').__version__ if importlib.util.find_spec('torch') else '')"
    if ($LASTEXITCODE -ne 0 -or $taskInstalledTorch.Trim() -ne "2.10.0+$taskFlavor") {
        if ($CpuOnly) {
            Get-PinnedFile 'https://download.pytorch.org/whl/cpu/torch-2.10.0%2Bcpu-cp312-cp312-win_amd64.whl' $taskTorchWheel '21cb5436978ef47c823b7a813ff0f8c2892e266cfe0f1d944879b5fba81bf4e1' 113671330
        } else {
            Get-PinnedFile 'https://download.pytorch.org/whl/cu128/torch-2.10.0%2Bcu128-cp312-cp312-win_amd64.whl' $taskTorchWheel 'fbde8f6a9ec8c76979a0d14df21c10b9e5cab6f0d106a73ca73e2179bc597cae' 2867409626
        }
        & $taskPython -I -m pip --isolated install --disable-pip-version-check --no-cache-dir --no-deps $taskTorchWheel
        if ($LASTEXITCODE -ne 0) { throw 'PyTorch could not be installed.' }
    }
    & $taskPython -I -m pip check
    if ($LASTEXITCODE -ne 0) { throw 'The runtime has conflicting or missing package dependencies.' }
} finally {
    $env:TEMP = $taskOldTemp
    $env:TMP = $taskOldTmp
}

$taskRevision = '81eed5e8f68b6bed1789f682fe78cdd324495afc'
$taskSourceBase = "https://raw.githubusercontent.com/RVC-Project/Retrieval-based-Voice-Conversion-WebUI/$taskRevision/"
$taskSourceFiles = @(
    @('LICENSE','bec61ed1eb9a3748644ddbc50303aa6978e14eeaba46822e59a006c722b52585',1142),
    @('infer/module/models.py','769615b8dbed4775c8083881663a79c0e5ec0b662dd5f0c0a1bc3ccdd9827e99',35798),
    @('infer/module/modules.py','b1aa77b0921468a61492fa89ed1f1212cd22fee3caa551befe44d8ab359fd2f1',17775),
    @('infer/module/commons.py','132a907eff444907861e63684c04c88ae631d03707470858f7c53f857b0cc5c8',5463),
    @('infer/module/attentions.py','8820e6047353d99344fc73fbb6c69d85e4f61066fc498982cc895e9577d4519f',15994),
    @('infer/module/transforms.py','fb0d3320adf32ca0eaa44dd56e8922927f77ec3dd45c775e10c3da1704072c6a',7458),
    @('infer/rmvpe.py','074171aec63e4b68f04d8691c62af50a1d99b9bc6dc56c4d6749fad4ba64853a',24108),
    @('tools/cuda_graph.py','e3b4ce4fc8a33a3a921e70f78ce6965440c770d79d3ce71fb9f215db3a2db1a2',7721)
)
foreach ($taskSource in $taskSourceFiles) {
    Get-PinnedFile ($taskSourceBase + $taskSource[0]) (Join-Path $taskVendor $taskSource[0]) $taskSource[1] $taskSource[2]
}
$taskHubBase = 'https://huggingface.co/lj1995/VoiceConversionWebUI/resolve/e6d0c1a17da07c33557852f9dfa2bd44cc75737d/'
Get-PinnedFile ($taskHubBase + 'hubert_base/config.json') (Join-Path $taskAssets 'hubert_base\config.json') '0346950779dfb7f9316fa74ed846e2b8a22a08eedfdc5387b73f327cb1a4a7cf' 1492
Get-PinnedFile ($taskHubBase + 'hubert_base/preprocessor_config.json') (Join-Path $taskAssets 'hubert_base\preprocessor_config.json') '7c1976a680fb7acc757cd36fb08eef878fa36c70b4c9d2d595df9c608bbbbf0e' 225
Get-PinnedFile ($taskHubBase + 'hubert_base/pytorch_model.bin') (Join-Path $taskAssets 'hubert_base\pytorch_model.bin') 'cc8c20f4b90a520757260197a3ff2505705a7adbd20ad9eeaa4e1a9b38442ef5' 189206711
Get-PinnedFile ($taskHubBase + 'rmvpe.pt') (Join-Path $taskAssets 'rmvpe\rmvpe.pt') '6d62215f4306e3ca278246188607209f09af3dc77ed4232efdd069798c4ec193' 181184272
$taskWorker = Join-Path $taskProject 'runtime\rvc_worker.py'
$taskInfoJson = '{"id":1,"command":"info"}' | & $taskPython -I -B $taskWorker --runtime-root $taskRuntime
if ($LASTEXITCODE -ne 0) { throw 'Installed RVC runtime verification failed.' }
$taskInfo = $taskInfoJson | ConvertFrom-Json
if (-not $taskInfo.ok -or -not $taskInfo.ready) { throw ('Installed runtime is incomplete: ' + $taskInfoJson) }
Write-Host $taskInfoJson

if (-not $KeepDownloads) {
    # Delete only these exact verified installation archives, never any directory.
    foreach ($taskArchive in @($taskPythonZip,$taskPipWheel,$taskTorchWheel)) {
        $taskFullArchive = [IO.Path]::GetFullPath($taskArchive)
        if (-not $taskFullArchive.StartsWith($taskDownloads + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Cleanup target escaped the downloads directory.' }
        if (Test-Path -LiteralPath $taskFullArchive) { Remove-Item -LiteralPath $taskFullArchive }
    }
}
Write-Host "Optional RVC runtime is ready in $taskRuntime. No system Python, PATH, OBS or GPU driver settings were changed."
