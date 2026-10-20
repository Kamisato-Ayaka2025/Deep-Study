<#
P0 desktop self-check.

Launches the published desktop candidate against an empty, throwaway data root and
asserts observable UI behaviour with Windows UI Automation:

  - the three navigation entries exist and no login is requested
  - each page shows a readable empty state
  - an invalid save surfaces a stable error code in the UI
  - a valid save is persisted and still present after the application restarts

Screenshots and an element tree are written as supporting evidence; the assertions
above are the actual gate. Manual (H) acceptance is not performed here and must not
be inferred from a passing run.

Keystrokes are never injected: the check only uses ValuePattern and InvokePattern,
so it cannot type into whichever window happens to hold focus.
#>
param(
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [Parameter(Mandatory = $true)][string]$EvidencePath,
    [switch]$P2,
    [switch]$P3
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class StudyHarnessWindow {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr handle, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr handle, out Rect rect);
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
}
"@

$UIAutomation = [System.Windows.Automation.AutomationElement]
$TreeScope = [System.Windows.Automation.TreeScope]
$TrueCondition = [System.Windows.Automation.Condition]::TrueCondition
$InvokePattern = [System.Windows.Automation.InvokePattern]::Pattern
$ValuePattern = [System.Windows.Automation.ValuePattern]::Pattern

$ui = Join-Path $EvidencePath 'ui'
New-Item -ItemType Directory -Force $ui | Out-Null
$dataRoot = Join-Path $EvidencePath ('data-' + [Guid]::NewGuid().ToString('N'))
$previousDataRoot = $env:STUDYHARNESS_DATA_ROOT
$ownedProcesses = [Collections.Generic.List[System.Diagnostics.Process]]::new()
$log = [Collections.Generic.List[string]]::new()
$checks = [Collections.Generic.List[object]]::new()

function Write-Log([string]$Text) {
    $line = "$([DateTimeOffset]::Now.ToString('HH:mm:ss')) $Text"
    $log.Add($line)
    Write-Output $line
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "UI self-check assertion failed: $Message" }
    Write-Log "ok: $Message"
}

function Invoke-Check([string]$Id, [scriptblock]$Action) {
    try {
        & $Action
        $checks.Add(@{ id = $Id; status = 'pass' })
    } catch {
        $checks.Add(@{ id = $Id; status = 'fail'; reason = $_.Exception.Message })
        throw
    }
}

function Find-ById($Root, [string]$AutomationId, [int]$TimeoutSeconds = 10) {
    $condition = New-Object System.Windows.Automation.PropertyCondition($UIAutomation::AutomationIdProperty, $AutomationId)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $element = $Root.FindFirst($TreeScope::Descendants, $condition)
        if ($element) { return $element }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)
    return $null
}

function Find-ByText($Root, [string]$Text) {
    $condition = New-Object System.Windows.Automation.PropertyCondition($UIAutomation::NameProperty, $Text)
    return $Root.FindFirst($TreeScope::Descendants, $condition)
}

function Get-VisibleNames($Root) {
    $names = [Collections.Generic.List[string]]::new()
    foreach ($element in $Root.FindAll($TreeScope::Descendants, $TrueCondition)) {
        $name = $element.Current.Name
        if ($name -and -not $element.Current.IsOffscreen) { $names.Add($name) }
    }
    return $names
}

function Get-ControlText($Element) {
    if (-not $Element) { return $null }
    $pattern = $null
    if ($Element.TryGetCurrentPattern($ValuePattern, [ref]$pattern)) { return $pattern.Current.Value }
    return $Element.Current.Name
}

function Click([string]$AutomationId, $Root) {
    $element = Find-ById $Root $AutomationId
    if (-not $element) { throw "Control not found: $AutomationId" }
    $pattern = $element.GetCurrentPattern($InvokePattern)
    $pattern.Invoke()
    Start-Sleep -Milliseconds 600
}

function Set-ControlText([string]$AutomationId, [string]$Text, $Root) {
    $element = Find-ById $Root $AutomationId
    if (-not $element) { throw "Control not found: $AutomationId" }
    $element.GetCurrentPattern($ValuePattern).SetValue($Text)
    Start-Sleep -Milliseconds 200
}

function Wait-Status($Root, [string]$Expected, [int]$TimeoutSeconds = 15) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $current = ''
    do {
        $element = Find-ById $Root 'Status' 3
        $current = Get-ControlText $element
        if ($current -and $current.Contains($Expected)) { return $current }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    throw "Status never reported '$Expected'; last status: '$current'"
}

function Wait-WorkspaceStatus($Root, [string]$Expected) {
    $deadline = (Get-Date).AddSeconds(15)
    do {
        $current = Get-ControlText (Find-ById $Root 'WorkspaceStatus')
        if ($current -and $current.Contains($Expected)) { return }
        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)
    throw "Workspace status never reported '$Expected'; last status: '$current'"
}
function Start-Shell {
    $env:STUDYHARNESS_DATA_ROOT = $dataRoot
    $process = Start-Process -FilePath $ExecutablePath -PassThru
    $ownedProcesses.Add($process)
    $deadline = (Get-Date).AddSeconds(60)
    do {
        Start-Sleep -Milliseconds 300
        $process.Refresh()
        if ($process.HasExited) { throw "Desktop exited on startup with code $($process.ExitCode)" }
    } while ($process.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline)
    if ($process.MainWindowHandle -eq 0) { throw 'Desktop window never appeared' }
    Start-Sleep -Milliseconds 1200
    return $process
}

function Stop-Shell($Process) {
    if (-not $Process.HasExited) {
        $null = $Process.CloseMainWindow()
        if (-not $Process.WaitForExit(10000)) {
            $Process.Kill()
            if (-not $Process.WaitForExit(10000)) { throw 'Owned desktop process did not exit' }
            throw 'Desktop required forced termination instead of closing normally'
        }
    }
}

function Save-Evidence($Root, $Process, [string]$Name) {
    try {
        $rect = New-Object StudyHarnessWindow+Rect
        if (-not [StudyHarnessWindow]::GetWindowRect($Process.MainWindowHandle, [ref]$rect)) { throw 'Window bounds unavailable' }
        $width = $rect.Right - $rect.Left
        $height = $rect.Bottom - $rect.Top
        if ($width -gt 0 -and $height -gt 0) {
            $bitmap = New-Object System.Drawing.Bitmap($width, $height)
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                # Capture this HWND, never the screen rectangle: another application may
                # cover our window and must not appear in acceptance artifacts.
                $hdc = $graphics.GetHdc()
                try {
                    if (-not [StudyHarnessWindow]::PrintWindow($Process.MainWindowHandle, $hdc, 2)) { throw 'Window capture failed' }
                } finally { $graphics.ReleaseHdc($hdc) }
                $bitmap.Save((Join-Path $ui "$Name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
            } finally { $graphics.Dispose(); $bitmap.Dispose() }
        }
    } catch {
        Write-Log "warning: screenshot '$Name' unavailable ($($_.Exception.Message))"
    }
    $tree = [Collections.Generic.List[string]]::new()
    foreach ($element in $Root.FindAll($TreeScope::Descendants, $TrueCondition)) {
        # Not $name: PowerShell variables are case-insensitive and would clobber $Name.
        $elementName = $element.Current.Name
        if ($elementName) { $tree.Add("$($element.Current.ControlType.ProgrammaticName) | $elementName") }
    }
    Set-Content -LiteralPath (Join-Path $ui "$Name-tree.txt") -Value $tree -Encoding UTF8
}

$first = $null
$second = $null
$savedModel = 'deepseek-ui-selfcheck'
try {
    Invoke-Check 'empty-database-startup' {
        if (-not [Environment]::UserInteractive) { throw 'An interactive desktop session is required' }
        New-Item -ItemType Directory -Force $dataRoot | Out-Null
        $script:first = Start-Shell
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($first.MainWindowHandle)
        foreach ($id in @('NavChat', 'NavCourses', 'NavSettings')) {
            Assert-True ([bool](Find-ById $root $id)) "navigation entry $id is present without any login step"
        }
        $names = Get-VisibleNames $root
        Assert-True ([bool](Find-ByText $root '从一个学习目标开始')) 'main page shows a readable learning entry'
        Assert-True ([bool](Find-ByText $root '无需登录 · 数据保存在本机')) 'shell states that no account is required'
        Assert-True (-not ($names -contains '登录') -and -not ($names -contains 'Login')) 'no login control is offered'
        Assert-True (Test-Path (Join-Path $dataRoot 'app.db')) 'empty database is created on first launch'
        Save-Evidence $root $first 'chat'
    }
    Invoke-Check 'courses-empty-state' {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($first.MainWindowHandle)
        Click 'NavCourses' $root
        Assert-True ([bool](Find-ByText $root '还没有学科')) 'courses page shows its empty state'
        Save-Evidence $root $first 'courses'
    }
    Invoke-Check 'settings-error-code-visible' {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($first.MainWindowHandle)
        Click 'NavSettings' $root
        Assert-True ([bool](Find-ByText $root '尚未保存密钥')) 'settings page reports that no key is stored'
        Assert-True ((Get-ControlText (Find-ById $root 'Model')) -eq 'deepseek-chat') 'settings load the stored model name'
        Set-ControlText 'Model' ' bad name ' $root
        Click 'Save' $root
        $status = Wait-Status $root 'VALIDATION_ERROR'
        Write-Log "status: $status"
        Save-Evidence $root $first 'settings-error'
    }
    Invoke-Check 'settings-save' {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($first.MainWindowHandle)
        Set-ControlText 'Model' $savedModel $root
        Click 'Save' $root
        $status = Wait-Status $root '设置已保存'
        Write-Log "status: $status"
        Save-Evidence $root $first 'settings-saved'
    }
    Invoke-Check 'settings-persist-after-restart' {
        Stop-Shell $first
        Start-Sleep -Seconds 2
        $script:second = Start-Shell
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($second.MainWindowHandle)
        Click 'NavSettings' $root
        $model = Get-ControlText (Find-ById $root 'Model')
        Assert-True ($model -eq $savedModel) "saved model survives a restart (expected '$savedModel', got '$model')"
        $bytes = [IO.File]::ReadAllBytes((Join-Path $dataRoot 'app.db'))
        Assert-True ([Text.Encoding]::UTF8.GetString($bytes).Contains($savedModel)) 'settings are stored in the local database, not only in memory'
        Assert-True (Test-Path (Join-Path $dataRoot 'logs/operations.jsonl')) 'operations are written to the redacted log'
        Save-Evidence $root $second 'settings-restart'
    }
    if ($P2 -or $P3) {
        Invoke-Check 'p2-restart-knowledge-and-preview-denial' {
            Stop-Shell $second
            $probe = Join-Path (Split-Path $PSScriptRoot -Parent) 'tests/StudyHarness.HostProbe/bin/Release/net10.0-windows10.0.19041.0/StudyHarness.HostProbe.exe'
            & $probe --p2-seed --data-root $dataRoot
            if ($LASTEXITCODE -ne 0) { throw 'P2 seed failed' }
            $script:second = Start-Shell
            $root = [System.Windows.Automation.AutomationElement]::FromHandle($second.MainWindowHandle)
            Click 'NavWorkspace' $root
            Assert-True ((Get-ControlText (Find-ById $root 'LibraryPath')).Contains('library-A')) 'library selection survives restart'
            Click 'SearchKnowledge' $root
            $list = Find-ById $root 'KnowledgeFragments'
            $condition = New-Object System.Windows.Automation.PropertyCondition($UIAutomation::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
            $items = $list.FindAll($TreeScope::Children, $condition)
            Assert-True ($items.Count -eq 3) 'PDF and Markdown content appear in actual desktop list'
            $items[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            Click 'AddReference' $root
            Click 'PrepareModel' $root
            Wait-WorkspaceStatus $root '请核对下方完整内容'
            $preview = Get-ControlText (Find-ById $root 'ModelPreview')
            Assert-True ($preview.Contains('https://api.deepseek.com/chat/completions') -and $preview.Contains('OriginalMaterial')) 'preview includes destination and selected source'
            Save-Evidence $root $second 'p2-preview'
            Click 'DenyModel' $root
            Assert-True ((Get-ControlText (Find-ById $root 'WorkspaceStatus')).Contains('CONSENT_DENIED')) 'refusal is visible and local materials remain'
            Click 'PrepareModel' $root
            Wait-WorkspaceStatus $root '请核对下方完整内容'
            Set-ControlText 'ModelPrompt' 'changed after preview' $root
            Click 'AllowModel' $root
            Assert-True ((Get-ControlText (Find-ById $root 'WorkspaceStatus')).Contains('CONSENT_REQUIRED')) 'changed payload cannot reuse preview'
            Click 'PrepareModel' $root
            Wait-WorkspaceStatus $root '请核对下方完整内容'
            Click 'AllowModel' $root
            Assert-True ((Get-ControlText (Find-ById $root 'WorkspaceStatus')).Contains('AUTH_INVALID')) 'missing key fails visibly without invented response'
            Save-Evidence $root $second 'p2-error'
        }
    }
    if ($P3) {
        Invoke-Check 'p3-draft-edit-and-consent' {
            $root = [System.Windows.Automation.AutomationElement]::FromHandle($second.MainWindowHandle)
            Click 'NavChat' $root
            Click 'NewPlan' $root
            Set-ControlText 'PlanRequest' 'P3 synthetic draft' $root
            Set-ControlText 'PlanHours' '0' $root
            Click 'SavePlan' $root
            Assert-True ((Get-ControlText (Find-ById $root 'PlanningStatus')).Contains('VALIDATION_ERROR')) 'zero hours rejected before draft persistence or dispatch'
            Set-ControlText 'PlanHours' '2' $root
            Click 'SavePlan' $root
            Click 'PreparePlan' $root
            Assert-True ((Get-ControlText (Find-ById $root 'PlanPreview')).Contains('4096')) 'unknown model capability uses disclosed conservative output limit'
            Click 'DenyPlan' $root
            Assert-True ((Get-ControlText (Find-ById $root 'PlanningStatus')).Contains('CONSENT_DENIED')) 'planning refusal is visible'
            Save-Evidence $root $second 'p3-draft'
        }
        Invoke-Check 'p3-confirm-freeze-and-graph' {
            Stop-Shell $second
            $probe = Join-Path (Split-Path $PSScriptRoot -Parent) 'tests/StudyHarness.HostProbe/bin/Release/net10.0-windows10.0.19041.0/StudyHarness.HostProbe.exe'
            & $probe --p3-seed --data-root $dataRoot
            if ($LASTEXITCODE -ne 0) { throw 'P3 seed failed' }
            $script:second = Start-Shell
            $root = [System.Windows.Automation.AutomationElement]::FromHandle($second.MainWindowHandle)
            $summary = Get-ControlText (Find-ById $root 'PlanSummary')
            Assert-True ($summary.Contains('P3 待确认合成数学') -and $summary.Contains('第1周') -and $summary.Contains('固定来源')) 'restored proposal shows full schedule and versioned sources'
            Assert-True (-not (Find-ById $root 'ConfirmPlan').Current.IsEnabled) 'freeze acknowledgement is required'
            (Find-ById $root 'AckFrozen').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
            Start-Sleep -Milliseconds 300
            Click 'ConfirmPlan' $root
            Assert-True ((Get-ControlText (Find-ById $root 'PlanningStatus')).Contains('计划已确认冻结')) 'confirmation commits and reports frozen plan'
            Assert-True (-not (Find-ById $root 'PlanRequest').Current.IsEnabled -and -not (Find-ById $root 'PlanHours').Current.IsEnabled) 'confirmed input controls are read-only'
            Assert-True (-not (Find-ById $root 'SavePlan').Current.IsEnabled) 'confirmed plan has no save path'
            Click 'NavCourses' $root
            Click 'Course-n2' $root
            Assert-True ((Get-ControlText (Find-ById $root 'NodeDetail')).Contains('可自由生成')) 'dependent course is freely available with no lesson'
            Click 'Course-n1' $root
            Assert-True ((Get-ControlText (Find-ById $root 'NodeDetail')).Contains('可自由生成')) 'root course eligibility is separate from absent lesson'
            Save-Evidence $root $second 'p3-graph'
            # Opening a course creates an owned reader window. Close that fixture window
            # before opening the main window's popup; otherwise focus can collapse it.
            $readerCondition = New-Object System.Windows.Automation.PropertyCondition($UIAutomation::NameProperty, 'Study Harness · 理论学习')
            $reader = $root.FindFirst($TreeScope::Descendants, $readerCondition)
            if ($reader) { $reader.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() }
            $selector = Find-ById $root 'SubjectSelector'
            $selector.SetFocus()
            $selector.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
            $condition = New-Object System.Windows.Automation.PropertyCondition($UIAutomation::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
            $popupDeadline = (Get-Date).AddSeconds(5)
            do {
                Start-Sleep -Milliseconds 100
                $items = $selector.FindAll($TreeScope::Descendants, $condition)
            } while ($items.Count -ne 3 -and (Get-Date) -lt $popupDeadline)
            Assert-True ($items.Count -eq 3) "two seeded subjects and saved draft remain separate (actual $($items.Count))"
            $items[1].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            $selector.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
            Click 'NavChat' $root
            Assert-True ((Get-ControlText (Find-ById $root 'PlanSummary')).Contains('P3 已确认合成数学')) 'another confirmed subject is independently browsable'
            Save-Evidence $root $second 'p3-frozen'
        }
        Invoke-Check 'p3-frozen-restart' {
            Stop-Shell $second
            $script:second = Start-Shell
            $root = [System.Windows.Automation.AutomationElement]::FromHandle($second.MainWindowHandle)
            Assert-True (-not (Find-ById $root 'PlanRequest').Current.IsEnabled) 'freeze survives desktop restart'
            Assert-True ((Get-ControlText (Find-ById $root 'PlanSummary')).Contains('P3 待确认合成数学')) 'confirmed candidate content survives restart'
            Assert-True (Test-Path (Join-Path $dataRoot 'planning.db')) 'planning persistence exists separately from settings'
        }
    }
} finally {
    $cleanupErrors = @()
    foreach ($process in $ownedProcesses) {
        try { Stop-Shell $process } catch { $cleanupErrors += $_.Exception.Message }
    }
    $env:STUDYHARNESS_DATA_ROOT = $previousDataRoot
    $leftover = @($ownedProcesses | Where-Object { -not $_.HasExited })
    if ($cleanupErrors.Count -gt 0) {
        $checks.Add(@{ id='cleanup'; status='fail'; reason=($cleanupErrors -join '; ') })
    }
    Write-Log "leftover desktop processes: $($leftover.Count)"
    Set-Content -LiteralPath (Join-Path $ui 'ui-selfcheck.log') -Value $log -Encoding UTF8
    @{
        executable = $ExecutablePath
        dataRoot   = $dataRoot
        checks     = $checks.ToArray()
        failed     = @($checks | Where-Object status -eq 'fail').Count
        leftoverProcesses = $leftover.Count
        automated  = $true
        manualAcceptance = 'pending'
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ui 'ui-summary.json') -Encoding UTF8
}
