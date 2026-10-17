param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$GamePath,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $projectRoot
$GamePath = (Resolve-Path -LiteralPath $GamePath).ProviderPath
if (!(Test-Path -LiteralPath (Join-Path $GamePath 'Dinkum_Data/Managed/Assembly-CSharp.dll') -PathType Leaf)) {
    throw 'GamePath must point to a Dinkum installation containing Dinkum_Data/Managed/Assembly-CSharp.dll'
}
$version = ([xml](Get-Content -LiteralPath 'src/DinkumSaveProtection/DinkumSaveProtection.csproj' -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid package version' }
function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet exited with $LASTEXITCODE" }
}
$cachePath = Join-Path $projectRoot '.cache'
New-Item -ItemType Directory -Path $cachePath -Force | Out-Null
$loaderZip = Join-Path $cachePath 'BepInEx_win_x64_5.4.23.5.zip'
$loaderUrl = 'https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip'
$loaderHash = '82f9878551030f54657792c0740d9d51a09500eeae1fba21106b0c441e6732c4'
if (!(Test-Path -LiteralPath $loaderZip)) { Invoke-WebRequest -Uri $loaderUrl -OutFile $loaderZip }
if ((Get-FileHash -LiteralPath $loaderZip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $loaderHash) { throw 'Pinned BepInEx download checksum mismatch' }
$loaderReferencePath = Join-Path $cachePath 'bepinex-5.4.23.5'
if (!(Test-Path -LiteralPath (Join-Path $loaderReferencePath 'BepInEx/core/BepInEx.dll'))) { Expand-Archive -LiteralPath $loaderZip -DestinationPath $loaderReferencePath }
Invoke-Dotnet @('build', 'src/DinkumSaveProtection/DinkumSaveProtection.csproj', '-c', 'Release', '--nologo', '-v', 'quiet', "-p:GamePath=$GamePath")
Invoke-Dotnet @('build', 'tools/ProtectionManager/ProtectionManager.csproj', '-c', 'Release', '--nologo', '-v', 'quiet')
$pluginPath = Join-Path $projectRoot 'src/DinkumSaveProtection/bin/Release/netstandard2.1/DinkumSaveProtection.dll'
if (!$SkipTests) {
    $testOutput = @(Invoke-Dotnet @('run', '--project', 'tests/DinkumSaveProtection.Tests', '-c', 'Release', '--', $GamePath))
    $testOutput | Write-Output
    $testSummary = $testOutput | Select-String -Pattern '^(\d+) offline tests passed\.' | Select-Object -Last 1
    if (!$testSummary) { throw 'Offline test count missing' }
    Invoke-Dotnet @('run', '--project', 'tools/ProtectionManager', '-c', 'Release', '--', 'self-test', '--plugin', $pluginPath, '--bepinex', (Join-Path $loaderReferencePath 'BepInEx/core'))
}
$distPath = Join-Path $projectRoot 'dist'
# A fresh stage prevents files from older packages leaking into a new release.
$packagePath = Join-Path $distPath ('.staging/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $packagePath | Out-Null
Copy-Item -LiteralPath $pluginPath -Destination (Join-Path $packagePath 'DinkumSaveProtection.dll') -Force
$receipt = [ordered]@{ Owner = 'io.dinkumsaveprotection.playerdata'; Version = $version; Files = [ordered]@{ 'DinkumSaveProtection.dll' = (Get-FileHash -LiteralPath $pluginPath -Algorithm SHA256).Hash.ToLowerInvariant() } }
[IO.File]::WriteAllText((Join-Path $packagePath 'install-manifest.json'), ($receipt | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
foreach ($document in @('LICENSE', 'README.md', 'README.en.md', 'docs/Installation.md', 'docs/Compatibility.md', 'docs/Verification.md')) {
    $destination = Join-Path $packagePath $document
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot $document) -Destination $destination
}
$managerPath = Join-Path $packagePath 'tools'
New-Item -ItemType Directory -Path $managerPath -Force | Out-Null
foreach ($file in @('ProtectionManager.exe', 'ProtectionManager.dll', 'ProtectionManager.deps.json', 'ProtectionManager.runtimeconfig.json')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot "tools/ProtectionManager/bin/Release/net10.0/$file") -Destination $managerPath
}
$archivePath = Join-Path $projectRoot "dist/DinkumSaveProtection-$version.zip"
Compress-Archive -Path (Join-Path $packagePath '*') -DestinationPath $archivePath -Force
$packageHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$archivePath.sha256", "$packageHash  $([IO.Path]::GetFileName($archivePath))`n", [Text.UTF8Encoding]::new($false))
if (!$SkipTests) {
    $buildReceipt = [ordered]@{ version = $version; offline_tests = [int]$testSummary.Matches[0].Groups[1].Value; installer_isolation = 'passed in temporary fixtures'; package_sha256 = $packageHash; game_launched = $false; plugin_installed = $false }
    [IO.File]::WriteAllText((Join-Path $distPath "build-verification-$version.json"), ($buildReceipt | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
}
Write-Output "Package built: $archivePath"
Write-Output 'No game was launched. Nothing was installed into the game or written to real save directories.'
