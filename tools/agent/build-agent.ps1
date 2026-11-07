param(
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'

$agentRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $agentRoot '..\..')
$outDir = Join-Path $agentRoot 'out'
$workDir = Join-Path $outDir 'work'

function Find-Jdk {
    if ($env:JAVA_HOME -and (Test-Path (Join-Path $env:JAVA_HOME 'bin\javac.exe'))) {
        return $env:JAVA_HOME
    }

    $candidates = @(
        'C:\Program Files\Android\openjdk',
        'C:\Program Files (x86)\Android\openjdk',
        'C:\Program Files\Eclipse Adoptium',
        'C:\Program Files\Microsoft'
    ) | Where-Object { Test-Path $_ } | ForEach-Object {
        Get-ChildItem $_ -Directory -ErrorAction SilentlyContinue |
            Where-Object { Test-Path (Join-Path $_.FullName 'bin\javac.exe') } |
            Sort-Object Name -Descending |
            Select-Object -First 1 -ExpandProperty FullName
    }

    $jdk = $candidates | Select-Object -First 1
    if (-not $jdk) {
        throw 'A JDK with javac.exe was not found. Set JAVA_HOME.'
    }
    return $jdk
}

function Find-AndroidSdk {
    if ($env:ANDROID_SDK_ROOT -and (Test-Path $env:ANDROID_SDK_ROOT)) { return $env:ANDROID_SDK_ROOT }
    if ($env:ANDROID_HOME -and (Test-Path $env:ANDROID_HOME)) { return $env:ANDROID_HOME }
    $local = Join-Path $env:LOCALAPPDATA 'Android\Sdk'
    if (Test-Path $local) { return $local }
    throw 'Android SDK not found. Set ANDROID_SDK_ROOT.'
}

$jdk = Find-Jdk
$sdk = Find-AndroidSdk
$buildTools = Get-ChildItem (Join-Path $sdk 'build-tools') -Directory |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1 -ExpandProperty FullName
$platforms = Get-ChildItem (Join-Path $sdk 'platforms') -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName 'android.jar') } |
    Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName
$androidJar = Join-Path $platforms 'android.jar'

if (-not (Test-Path $androidJar)) { throw "android.jar not found under $platforms" }
if (-not (Test-Path (Join-Path $buildTools 'aapt2.exe'))) { throw "aapt2.exe not found under $buildTools" }

$env:JAVA_HOME = $jdk
$env:PATH = (Join-Path $jdk 'bin') + ';' + $env:PATH

Write-Host "JDK:         $jdk"
Write-Host "Build tools: $buildTools"
Write-Host "Platform:    $platforms"

if ($Clean) {
    Remove-Item $workDir -Recurse -Force -ErrorAction SilentlyContinue
}
else {
    Remove-Item $workDir -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Force $workDir | Out-Null
New-Item -ItemType Directory -Force (Join-Path $workDir 'classes') | Out-Null
New-Item -ItemType Directory -Force (Join-Path $workDir 'dex') | Out-Null
New-Item -ItemType Directory -Force $outDir | Out-Null

$manifest = Join-Path $agentRoot 'AndroidManifest.xml'
$baseApk = Join-Path $workDir 'base.apk'

& (Join-Path $buildTools 'aapt2.exe') link `
    -o $baseApk `
    -I $androidJar `
    --manifest $manifest `
    --min-sdk-version 19 `
    --target-sdk-version 19 `
    --version-code 1 `
    --version-name 1.0
if ($LASTEXITCODE -ne 0) { throw 'aapt2 link failed' }

$javaFiles = Get-ChildItem (Join-Path $agentRoot 'src') -Recurse -Filter *.java | ForEach-Object { $_.FullName }
& (Join-Path $jdk 'bin\javac.exe') `
    -source 8 -target 8 -encoding UTF-8 `
    -bootclasspath $androidJar `
    -d (Join-Path $workDir 'classes') `
    $javaFiles
if ($LASTEXITCODE -ne 0) { throw 'javac failed' }

$classFiles = Get-ChildItem (Join-Path $workDir 'classes') -Recurse -Filter *.class | ForEach-Object { $_.FullName }
& (Join-Path $buildTools 'd8.bat') `
    --lib $androidJar `
    --min-api 19 `
    --output (Join-Path $workDir 'dex') `
    $classFiles
if ($LASTEXITCODE -ne 0) { throw 'd8 failed' }

# Add classes.dex to the APK without relying on the deprecated Android build system.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open($baseApk, [System.IO.Compression.ZipArchiveMode]::Update)
try {
    $entry = $zip.GetEntry('classes.dex')
    if ($entry) { $entry.Delete() }
    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
        $zip,
        (Join-Path $workDir 'dex\classes.dex'),
        'classes.dex',
        [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
}
finally {
    $zip.Dispose()
}

$aligned = Join-Path $workDir 'aligned.apk'
& (Join-Path $buildTools 'zipalign.exe') -f 4 $baseApk $aligned
if ($LASTEXITCODE -ne 0) { throw 'zipalign failed' }

$keystore = Join-Path $agentRoot 'uzconnect-debug.jks'
if (-not (Test-Path $keystore)) {
    & (Join-Path $jdk 'bin\keytool.exe') `
        -genkeypair `
        -keystore $keystore `
        -storepass uzconnect `
        -keypass uzconnect `
        -alias uzconnect `
        -keyalg RSA `
        -keysize 2048 `
        -validity 10000 `
        -dname 'CN=UZConnect Agent, OU=Local, O=UZConnect, C=US'
    if ($LASTEXITCODE -ne 0) { throw 'keytool failed' }
}

$finalApk = Join-Path $outDir 'uzconnect-agent.apk'
& (Join-Path $buildTools 'apksigner.bat') sign `
    --ks $keystore `
    --ks-pass pass:uzconnect `
    --key-pass pass:uzconnect `
    --v1-signing-enabled true `
    --v2-signing-enabled false `
    --out $finalApk `
    $aligned
if ($LASTEXITCODE -ne 0) { throw 'apksigner failed' }

& (Join-Path $buildTools 'apksigner.bat') verify --print-certs $finalApk | Write-Host
Write-Host "Built: $finalApk"
