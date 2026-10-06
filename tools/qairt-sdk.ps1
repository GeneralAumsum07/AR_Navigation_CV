<# Shared preflight: the context generator and staged runtimes must use one SDK build. #>
function Assert-QairtSdkVersion([string]$SdkRoot, [string]$RepositoryRoot) {
    $yamlPath = Join-Path $SdkRoot 'sdk.yaml'
    if (!(Test-Path -LiteralPath $yamlPath)) { throw "QAIRT sdk.yaml missing under $SdkRoot" }
    # These two scalar keys are the SDK's own generated identity, not the zip/folder name.
    $yaml = Get-Content -LiteralPath $yamlPath -Raw
    $version = [regex]::Match($yaml, '(?m)^version:\s*([\d.]+)\s*$').Groups[1].Value
    $build = [regex]::Match($yaml, '(?m)^build_id:\s*(\d+)\s*$').Groups[1].Value
    if (!$version -or !$build) { throw "Cannot read QAIRT identity in $yamlPath" }
    $actual = "$version.$build"
    $expected = (Get-Content (Join-Path $RepositoryRoot 'tools\models.lock.json') -Raw | ConvertFrom-Json).qairt
    if ($actual -ne $expected) { throw "QAIRT version mismatch: runtime pin $expected, SDK $actual" }
    return $actual
}
