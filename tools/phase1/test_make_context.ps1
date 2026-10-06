<# Verify the generator's public parameters and invocation without the proprietary SDK. #>
param([string]$Compiler = 'g++')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$script = Join-Path $PSScriptRoot 'make-context.ps1'
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($script, [ref]$tokens, [ref]$errors)
if ($errors) { throw 'Generator script has parse errors' }
$names = @($ast.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath })
if ('Variant' -notin $names -or 'OutName' -notin $names) {
    throw 'Context generator must accept Variant and OutName; refusing to execute an environment setter.'
}
$cache = Join-Path $repo 'tools\.cache\phase1-generator-test'
$bin = Join-Path $cache 'sdk\bin\x86_64-windows-msvc'
New-Item -ItemType Directory -Force $bin | Out-Null
$exe = Join-Path $bin 'qnn-context-binary-generator.exe'
& $Compiler -std=c++17 (Join-Path $PSScriptRoot 'tests\context_generator_stub.cpp') -o $exe
if ($LASTEXITCODE -ne 0) { throw 'Stub compilation failed' }
$variant = Join-Path $repo 'tools\.cache\phase1-test-model'
New-Item -ItemType Directory -Force $variant | Out-Null
Set-Content -LiteralPath (Join-Path $variant 'stub.dlc') -Value 'test fixture, not model weights'
$oldSdk = $env:QNN_SDK_ROOT
$oldArgs = $env:WD_GENERATOR_ARGS
$userSdk = [Environment]::GetEnvironmentVariable('QNN_SDK_ROOT', 'User')
try {
    $env:QNN_SDK_ROOT = Join-Path $cache 'sdk'
    $env:WD_GENERATOR_ARGS = Join-Path $cache 'args.txt'
    & powershell -ExecutionPolicy Bypass -File $script -Variant phase1-test-model -OutName regression_stub
    if ($LASTEXITCODE -ne 0) { throw 'Stub generator invocation failed' }
    $actual = Get-Content $env:WD_GENERATOR_ARGS
    foreach ($flag in '--backend', '--model', '--dlc_path', '--binary_file', '--output_dir', '--config_file') {
        if ($flag -notin $actual) { throw "Missing generator flag $flag" }
    }
    if (!(Test-Path (Join-Path $repo 'tools\.cache\ctx\regression_stub.ctx.bin'))) { throw 'Context output absent' }
    if ([Environment]::GetEnvironmentVariable('QNN_SDK_ROOT', 'User') -ne $userSdk) { throw 'Persistent SDK setting changed' }
    Write-Output 'Context-generator offline contract: PASS (stub output, not a real QNN context)'
}
finally { $env:QNN_SDK_ROOT = $oldSdk; $env:WD_GENERATOR_ARGS = $oldArgs }
