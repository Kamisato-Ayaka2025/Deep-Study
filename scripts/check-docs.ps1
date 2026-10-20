$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$indexPath = Join-Path $root 'docs/changes/INDEX.md'
# -Encoding UTF8 is required: these files are UTF-8 and Windows PowerShell 5.1
# would otherwise decode them with the system ANSI code page.
$index = Get-Content -LiteralPath $indexPath -Raw -Encoding UTF8
$ids = [regex]::Matches($index, '(?m)^\| (TURN-\d{8}-\d{3}) \|') | ForEach-Object { $_.Groups[1].Value }
if ($ids.Count -eq 0) { throw 'No TURN record rows found in the index' }
if (($ids | Sort-Object -Unique).Count -ne $ids.Count) { throw 'Duplicate TURN ID in index' }
$requiredSections = @('修改与原因', '契约与数据影响', '验证', '未完成与下一步', '提交与交接')
$requiredFields = @('时间：', '状态：', '阶段与工作包：', '关联：', 'Git：', '用户目标')
$checked = 0
foreach ($link in [regex]::Matches($index, '\]\(([^)]+)\)')) {
    $relative = $link.Groups[1].Value
    $path = Join-Path (Join-Path $root 'docs/changes') $relative
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing record: $path" }
    $record = Get-Content -LiteralPath $path -Raw -Encoding UTF8
    $turn = [regex]::Match($relative, 'TURN-\d{8}-\d{3}').Value
    if (-not $record.Contains($turn)) { throw "Record $path does not mention its own ID $turn" }
    foreach ($section in $requiredSections) {
        if (-not $record.Contains("## $section")) { throw "Missing section '$section' in $path" }
    }
    foreach ($field in $requiredFields) {
        if (-not $record.Contains($field)) { throw "Missing header field '$field' in $path" }
    }
    $checked++
}
# Every round record must be reachable from the index; orphaned records are invisible to search.
foreach ($file in Get-ChildItem (Join-Path $root 'docs/changes') -Directory | ForEach-Object { Get-ChildItem $_.FullName -Filter '*.md' -File }) {
    if (-not $index.Contains($file.Name)) { throw "Record not indexed: $($file.FullName)" }
}
if (-not (Get-Content -LiteralPath (Join-Path $root 'docs/implementation-status.md') -Raw -Encoding UTF8).Contains('WP-P0-001')) { throw 'Work package status missing' }
Write-Output "Documentation checks passed: $checked records, $($ids.Count) index rows."
