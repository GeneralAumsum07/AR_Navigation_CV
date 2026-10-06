<#
.SYNOPSIS
  Offline-compile a pinned QNN DLC into an HTP v75 (SM8650) context binary.
.NOTES
  Flag names follow QAIRT's qnn-context-binary-generator. Run it with --help first and fix
  any flag that differs in this SDK build before trusting the output.
  soc_model 57 = SM8650 is from QAIRT's supported-SoC table (inferred; confirm in the SDK docs).
#>
param(
    [Parameter(Mandatory = $true)][string]$Variant,      # e.g. depth_anything_v2-qnn_dlc-w8a16
    [Parameter(Mandatory = $true)][string]$OutName,      # e.g. depth_anything_v2_w8a16
    [int]$SocModel = 57
)
$ErrorActionPreference = 'Stop'
$sdk = $env:QNN_SDK_ROOT
if (-not $sdk) { throw 'QNN_SDK_ROOT is not set' }
$repo = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
$dlc = @(Get-ChildItem -Path (Join-Path $repo "tools\.cache\$Variant") -Recurse -Filter '*.dlc')[0].FullName
$bin = Join-Path $sdk 'bin\x86_64-windows-msvc'
$lib = Join-Path $sdk 'lib\x86_64-windows-msvc'
$out = Join-Path $repo 'tools\.cache\ctx'
New-Item -ItemType Directory -Force $out | Out-Null

# HTP backend extension config: which SoC/HTP architecture to compile for.
$htpCfg = Join-Path $out 'htp_backend_ext_config.json'
@"
{
  "devices": [ { "soc_model": $SocModel, "dsp_arch": "v75" } ]
}
"@ | Set-Content -Path $htpCfg -Encoding ASCII
$extCfg = Join-Path $out 'htp_ext.json'
$extLib = (Join-Path $lib 'QnnHtpNetRunExtensions.dll') -replace '\\', '/'
$htpCfgFwd = $htpCfg -replace '\\', '/'
@"
{
  "backend_extensions": { "shared_library_path": "$extLib", "config_file_path": "$htpCfgFwd" }
}
"@ | Set-Content -Path $extCfg -Encoding ASCII

& (Join-Path $bin 'qnn-context-binary-generator.exe') `
    --backend (Join-Path $lib 'QnnHtp.dll') `
    --model (Join-Path $lib 'QnnModelDlc.dll') `
    --dlc_path $dlc `
    --binary_file "$OutName.ctx" `
    --output_dir $out `
    --config_file $extCfg
if ($LASTEXITCODE -ne 0) { throw "context generation failed ($LASTEXITCODE)" }
Get-ChildItem $out -Filter "$OutName.ctx*" | ForEach-Object { Write-Host $_.FullName $_.Length }
