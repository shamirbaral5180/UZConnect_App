param(
    [string]$Sdk = "$env:LOCALAPPDATA\Android\Sdk",
    [string]$JavaHome = $env:JAVA_HOME,
    [string]$Platform = 'android-36',
    [string]$BuildTools = '36.0.0'
)
$ErrorActionPreference = 'Continue'
if (-not $JavaHome) { $JavaHome = 'C:\Program Files\Android\openjdk\jdk-21.0.8' }
$env:JAVA_HOME = $JavaHome
$env:PATH = "$JavaHome\bin;$env:PATH"
$android = "$Sdk\platforms\$Platform\android.jar"
$out = Join-Path $PSScriptRoot 'out'
$work = Join-Path $out 'work'
New-Item -ItemType Directory -Force "$work\classes", "$work\dex" | Out-Null
& "$JavaHome\bin\javac.exe" -source 8 -target 8 -encoding UTF-8 -bootclasspath $android -d "$work\classes" (Get-ChildItem "$PSScriptRoot\src" -Filter '*.java' | ForEach-Object { $_.FullName })
if ($LASTEXITCODE -ne 0) { throw 'Wi-Fi helper compilation failed.' }
$classes = Get-ChildItem "$work\classes" -Filter '*.class' | ForEach-Object { $_.FullName }
& "$Sdk\build-tools\$BuildTools\d8.bat" --lib $android --min-api 19 --output "$work\dex" $classes
if ($LASTEXITCODE -ne 0) { throw 'Wi-Fi helper DEX build failed.' }
& "$JavaHome\bin\jar.exe" cf "$out\uzconnect-wifi.jar" -C "$work\dex" classes.dex
if ($LASTEXITCODE -ne 0) { throw 'Wi-Fi helper packaging failed.' }
