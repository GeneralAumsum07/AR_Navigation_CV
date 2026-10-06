<#
.SYNOPSIS
  Build libwalldepth.so and its two test executables for arm64-v8a with Unity's bundled NDK
  and CMake, then stage the plugin plus the pinned QNN runtime libraries for the APK.
#>
param(
    [string]$UnityAndroid = 'C:\Program Files\Unity\Hub\Editor\6000.3.5f1\Editor\Data\PlaybackEngines\AndroidPlayer'
)
$ErrorActionPreference = 'Stop'
$sdk = $env:QNN_SDK_ROOT
if (-not $sdk) { throw 'QNN_SDK_ROOT is not set (Task 7, Step 1)' }
$here = Split-Path -Parent $PSCommandPath
$repo = Split-Path -Parent (Split-Path -Parent $here)
. (Join-Path $repo 'tools\qairt-sdk.ps1')
$sdkVersion = Assert-QairtSdkVersion $sdk $repo
$ndk = Join-Path $UnityAndroid 'NDK'
$cmakeBin = Join-Path $UnityAndroid 'SDK\cmake\3.22.1\bin'
$build = Join-Path $here 'build'
function Fwd([string]$p) { $p -replace '\\', '/' }

& (Join-Path $cmakeBin 'cmake.exe') -S $here -B $build -G Ninja `
    "-DCMAKE_MAKE_PROGRAM=$(Fwd (Join-Path $cmakeBin 'ninja.exe'))" `
    "-DCMAKE_TOOLCHAIN_FILE=$(Fwd (Join-Path $ndk 'build\cmake\android.toolchain.cmake'))" `
    -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=android-25 -DANDROID_STL=c++_static `
    -DCMAKE_BUILD_TYPE=Release "-DQNN_SDK_ROOT=$(Fwd $sdk)"
if ($LASTEXITCODE -ne 0) { throw "cmake configure failed ($LASTEXITCODE)" }
& (Join-Path $cmakeBin 'cmake.exe') --build $build
if ($LASTEXITCODE -ne 0) { throw "cmake build failed ($LASTEXITCODE)" }

# Unity treats Plugins/Android/libs/<abi>/*.so as Android plugins for that ABI by folder convention.
$dest = Join-Path $repo 'Assets\Plugins\Android\libs\arm64-v8a'
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item (Join-Path $build 'libwalldepth.so') $dest -Force
foreach ($f in 'libQnnHtp.so', 'libQnnHtpV75Stub.so', 'libQnnSystem.so') {
    Copy-Item (Join-Path $sdk "lib\aarch64-android\$f") $dest -Force
}
# The skeleton runs on the Hexagon DSP; the "unsigned" build loads on production phones via
# unsigned PD, the same file Task 7 used with qnn-net-run.
Copy-Item (Join-Path $sdk 'lib\hexagon-v75\unsigned\libQnnHtpV75Skel.so') $dest -Force
# Keep the SDK's notices with the incorporated runtime in the APK. The whole Models
# directory is generated/ignored; proprietary SDK binaries and notices stay out of git.
$modelDir = Join-Path $repo 'Assets\StreamingAssets\Models'
New-Item -ItemType Directory -Force $modelDir | Out-Null
foreach ($notice in 'LICENSE.pdf', 'NOTICE.txt', 'QNN_NOTICE.txt') {
    Copy-Item (Join-Path $sdk $notice) (Join-Path $modelDir "QAIRT_$notice") -Force
}
# Record identity and exact staged bytes so packaging can be checked without guessing
# from filenames. A successful compile alone says nothing about which .so reached the APK.
$hashes = @{}
Get-ChildItem $dest -Filter *.so | ForEach-Object { $hashes[$_.Name] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
@{ qairt = $sdkVersion; libraries = $hashes } | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $modelDir 'qairt-runtime.json') -Encoding UTF8
Get-ChildItem $dest -Filter *.so | ForEach-Object { '{0,-24} {1,12:N0} bytes' -f $_.Name, $_.Length }
