param(
    [Parameter(Mandatory=$true)][string]$Path,
    [double]$ReferenceMeters = [double]::NaN
)
# A missing reading is counted separately, never converted into a zero-distance error.
# Pass a reference only for a stationary, independently measured trial, not a moving sweep.
$rows = @(Get-Content -LiteralPath $Path | Where-Object { $_ -and !$_.StartsWith('#') } | ConvertFrom-Csv)
foreach ($group in ($rows | Group-Object mode)) {
    $valid = @($group.Group | Where-Object { $_.aimed_valid -eq '1' -or $_.aimed_valid -eq 'True' })
    $values = @($valid | ForEach-Object { [double]::Parse($_.aimed_raw_m, [Globalization.CultureInfo]::InvariantCulture) } | Sort-Object)
    $median = [double]::NaN
    if ($values.Count -gt 0) {
        $mid = [int][Math]::Floor($values.Count/2)
        $median = $values[$mid]
        if ($values.Count % 2 -eq 0) { $median = ($values[$mid-1]+$values[$mid])/2 }
    }
    $errors = @()
    if (![double]::IsNaN($ReferenceMeters)) { $errors = @($values | ForEach-Object { [Math]::Abs($_-$ReferenceMeters) }) }
    [pscustomobject]@{
        File = Split-Path $Path -Leaf
        Mode = $group.Name
        Rows = $group.Count
        Valid = $valid.Count
        ValidPercent = [Math]::Round(100*$valid.Count/[Math]::Max(1,$group.Count),1)
        MedianRawMeters = $median
        MeanAbsoluteErrorMeters = if ($errors.Count) { ($errors | Measure-Object -Average).Average } else { [double]::NaN }
        Sources = ($valid | Group-Object aimed_source | ForEach-Object { "$($_.Name):$($_.Count)" }) -join '; '
        FailureReasons = ($group.Group | Group-Object aimed_reason | Sort-Object Count -Descending | Select-Object -First 3 | ForEach-Object { "$($_.Name):$($_.Count)" }) -join '; '
    }
}
