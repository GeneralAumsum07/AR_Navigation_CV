<# Numeric and validation regressions without a phone or the proprietary QNN SDK. #>
param([string]$Compiler = 'g++')
$ErrorActionPreference = 'Stop'
$native = Split-Path -Parent $PSScriptRoot
$repo = Split-Path -Parent (Split-Path -Parent $native)
$cache = Join-Path $repo 'tools\.cache\native-tests'
New-Item -ItemType Directory -Force $cache | Out-Null
$convert = Join-Path $cache 'convert.exe'
# GCC's IEEE binary16 storage type is the host equivalent of ARM Clang's __fp16.
& $Compiler -std=c++17 '-D__fp16=_Float16' -I (Join-Path $native 'src') (Join-Path $PSScriptRoot 'convert_test.cpp') -o $convert
if ($LASTEXITCODE -ne 0) { throw 'Conversion test compilation failed' }
& $convert
if ($LASTEXITCODE -ne 0) { throw 'Conversion test failed' }
$selftest = Join-Path $cache 'selftest.exe'
& $Compiler -std=c++17 -I (Join-Path $native 'include') (Join-Path $PSScriptRoot 'selftest.cpp') (Join-Path $PSScriptRoot 'selftest_stub.cpp') -o $selftest
if ($LASTEXITCODE -ne 0) { throw 'Self-test fixture compilation failed' }
$inputPath = Join-Path $cache 'input.rgb'
$expected = Join-Path $cache 'expected.raw'
[IO.File]::WriteAllBytes($inputPath, [byte[]](0,0,0,0,0,0,0,0,0,0,0,0))
[IO.File]::WriteAllBytes($expected, [byte[]]([BitConverter]::GetBytes([single]1) + [BitConverter]::GetBytes([single]2)))
$old = $env:WD_SELFTEST_NAN
try {
    $env:WD_SELFTEST_NAN = '0'
    & $selftest unused-context unused-libraries $inputPath $expected 1
    if ($LASTEXITCODE -ne 0) { throw 'Correct output was rejected' }
    $env:WD_SELFTEST_NAN = '1'
    & $selftest unused-context unused-libraries $inputPath $expected 1
    if ($LASTEXITCODE -ne 1) { throw 'NaN output was not rejected' }
    Write-Output 'Native offline regressions: PASS'
}
finally { $env:WD_SELFTEST_NAN = $old }
