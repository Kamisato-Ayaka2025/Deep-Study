# Study Harness P1 人工验收辅助脚本（H 门禁用，非自动化门禁）。
#
# 它只做三件事：确认本机可达的工具链、按你的选择启动隔离运行人工验证程序、在会话结束后
# 报告遗留进程。真正的隔离与停止行为来自生产 Runner 与生产宿主，不在这里重新实现，
# 因此它观察到的是产品行为，而不是脚本自己的行为。
#
# 它不会替你签署任何 H 项，也不会在工具链缺失时报通过：缺失记 blocked 并以非零码退出。
param(
    [ValidateSet('msvc', 'mingw', 'both')][string]$Toolchain = 'both',
    [ValidateSet('hello', 'escape', 'loop')][string]$Bundle = 'hello',
    [string]$DataRoot
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/environment.ps1"
Set-Location $RepoRoot
if (-not $DataRoot) { $DataRoot = Join-Path $RepoRoot 'artifacts/manual-P1' }
New-Item -ItemType Directory -Force $DataRoot | Out-Null
function Invoke-Dotnet([string[]]$Arguments) {
    & $Dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $LASTEXITCODE" }
}
function Get-Leftovers([string]$Name) {
    return @(Get-Process -Name $Name -ErrorAction SilentlyContinue).Count
}
Write-Output "构建（Release）……"
Invoke-Dotnet @('build', 'StudyHarness.slnx', '-c', 'Release', '-m:1')
$runner = Join-Path $RepoRoot 'src/StudyHarness.Runner/bin/Release/net10.0-windows/StudyHarness.Runner.exe'
$harness = Join-Path $RepoRoot 'tests/StudyHarness.HostProbe/bin/Release/net10.0-windows/StudyHarness.HostProbe.exe'
foreach ($tool in @($runner, $harness)) {
    if (-not (Test-Path -LiteralPath $tool)) { throw "缺少构建产物：$tool" }
}
# 工具链清单由产品自身的发现逻辑给出，脚本不另做判断，否则脚本与产品可能给出不同答案。
$report = & $runner --toolchains | Out-String
Write-Output "工具链发现：$($report.Trim())"
$available = @()
if ($report -match '"toolchains":\[(.*?)\]') { $available = $Matches[1] -replace '"', '' -split ',' | Where-Object { $_ } }
$requested = if ($Toolchain -eq 'both') { @('msvc', 'mingw') } else { @($Toolchain) }
$blocked = @($requested | Where-Object { $available -notcontains $_ })
if ($blocked.Count -gt 0) {
    Write-Output "blocked: 本机未发现工具链 $($blocked -join ', ')。请先按 docs/acceptance/P1.md 的说明安装并加入 PATH。"
}
$failed = 0
foreach ($tool in @($requested | Where-Object { $available -contains $_ })) {
    Write-Output ""
    Write-Output "=== 工具链 $tool，夹具 $Bundle ==="
    & $harness --interactive --runner $runner --data-root $DataRoot --toolchain $tool --bundle $Bundle
    $exit = $LASTEXITCODE
    Write-Output "人工验证程序退出码：$exit"
    if ($exit -ne 0) { $failed++ }
    # 会话关闭后仍活着的进程。桌面程序应给出同样的零；非零必须记为该 H 项失败并保留现场。
    $leftProgram = Get-Leftovers 'program'
    $leftRunner = Get-Leftovers 'StudyHarness.Runner'
    Write-Output "遗留进程检查：program=$leftProgram，StudyHarness.Runner=$leftRunner"
    if ($leftProgram -ne 0 -or $leftRunner -ne 0) { $failed++ }
}
Write-Output ""
Write-Output "运行数据目录：$DataRoot"
if ($failed -gt 0) {
    Write-Output "有 $failed 项未通过或存在遗留进程，请记录复现步骤，不要把本阶段记为通过。"
    exit 1
}
if ($blocked.Count -gt 0) { exit 3 }
Write-Output "本脚本执行的部分各项正常；H 门禁仍需你本人判断并签字。"
exit 0
