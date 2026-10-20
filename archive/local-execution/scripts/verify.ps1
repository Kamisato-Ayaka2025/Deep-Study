param([ValidateSet('P0','P1','P2','P3','P4','PSEC')][string]$Phase = 'P0', [switch]$RealApi)
. "$PSScriptRoot/environment.ps1"
Set-Location $RepoRoot
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,6)
$out = Join-Path $RepoRoot "artifacts/acceptance/$Phase/$runId"
New-Item -ItemType Directory -Force $out | Out-Null
$checks = [Collections.Generic.List[object]]::new()
# Declared up front so checks that never ran are reported as notRun instead of
# silently disappearing from the summary. P1 adds the toolchain probe P0 does not need:
# without both toolchains the isolation checks have nothing to compile.
$checkOrder = if ($Phase -ne 'P0') {
    @('documentation', 'locked-restore', 'build', 'toolchains', 'tests', 'test-count', 'publish', 'ui-selfcheck', 'package')
} else {
    @('documentation', 'locked-restore', 'build', 'tests', 'test-count', 'publish', 'ui-selfcheck', 'package')
}
if ($Phase -eq 'PSEC') { $checkOrder = @('documentation', 'locked-restore', 'build', 'toolchains', 'tests', 'test-count', 'real-code-readiness') }
$testFilter = if ($Phase -eq 'P0') { 'FullyQualifiedName!~StudyHarness.Tests.Runner.' } elseif ($Phase -in @('P1','P2','P3','P4')) { 'Gate!=PSEC' } else { $null }
if ($Phase -eq 'P4') { $checkOrder += 'p4-ui-selfcheck' }
if ($Phase -in @('P2','P3','P4')) { $checkOrder += 'real-api' }
$started = [DateTimeOffset]::UtcNow.ToString('o')
function Invoke-Check([string]$Name, [scriptblock]$Action) {
    $log = Join-Path $out "$Name.log"
    try {
        & $Action *> $log
        $checks.Add(@{ id=$Name; status='pass'; evidence="$Name.log" })
    } catch {
        $_.Exception.Message | Add-Content $log
        # A check that could not run because the machine lacks something it needs is recorded
        # as blocked, not as a failure of the thing under test -- and still fails the gate.
        $status = if ($_.Exception.Message.StartsWith('blocked:')) { 'blocked' } else { 'fail' }
        $checks.Add(@{ id=$Name; status=$status; evidence="$Name.log" })
        throw
    }
}
function Invoke-Dotnet([string[]]$Arguments) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Dotnet @Arguments 2>&1 | ForEach-Object { $_.ToString() }; $code = $LASTEXITCODE }
    finally { $ErrorActionPreference = $previous }
    if ($code -ne 0) { throw "dotnet failed: $code" }
}
# [Convert]::ToHexString and [SHA256]::HashData are .NET Core only; Windows
# PowerShell 5.1 ships .NET Framework, so use APIs both hosts provide.
function Get-Sha256Hex([byte[]]$Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return (($sha.ComputeHash($Bytes) | ForEach-Object { $_.ToString('X2') }) -join '') }
    finally { $sha.Dispose() }
}
function Read-Json([string]$Path) { Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json }
try {
    Invoke-Check 'documentation' { & "$PSScriptRoot/check-docs.ps1" }
    Invoke-Check 'locked-restore' { Invoke-Dotnet @('restore','StudyHarness.slnx','--locked-mode','--disable-parallel','-m:1') }
    Invoke-Check 'build' { Invoke-Dotnet @('build','StudyHarness.slnx','--no-restore','-c','Release','-m:1') }
    if ($Phase -ne 'P0') {
        Invoke-Check 'toolchains' {
            # The product's own toolchain discovery answers this, so the gate cannot disagree
            # with what a run would find. A machine missing either toolchain is blocked: the
            # isolation checks have nothing to compile and must not be recorded as passing.
            $runner = Join-Path $RepoRoot 'src/StudyHarness.Runner/bin/Release/net10.0-windows/StudyHarness.Runner.exe'
            if (-not (Test-Path -LiteralPath $runner)) { throw 'blocked: the built Runner was not found' }
            $report = & $runner --toolchains | Out-String
            Write-Output $report
            if ($LASTEXITCODE -eq 0) { return }
            $names = if ($report -match '"missing":\[(.*?)\]') { $Matches[1] } else { 'unknown' }
            throw "blocked: toolchain(s) not available on this machine: $names"
        }
    }
    # Two loggers on purpose: the trx file is what the counter assertion reads, and the detailed
    # console logger puts each check's own raw output -- the isolation fixture's native error
    # numbers among them -- into tests.log, where a reviewer reads it without opening the trx.
    Invoke-Check 'tests' {
        $phaseFilter = if ($testFilter) { @('--filter',$testFilter) } else { @() }
        Invoke-Dotnet (@('test','tests/StudyHarness.Tests','--no-build','--no-restore','-c','Release','--logger',"trx;LogFileName=$($Phase.ToLower()).trx",'--logger','console;verbosity=detailed','--results-directory',"$out/tests",'-m:1') + $phaseFilter)
    }
    Invoke-Check 'test-count' {
        [xml]$trx = Get-Content -LiteralPath "$out/tests/$($Phase.ToLower()).trx" -Raw -Encoding UTF8
        $counters = $trx.TestRun.ResultSummary.Counters
        if ([int]$counters.total -eq 0 -or [int]$counters.failed -ne 0 -or [int]$counters.passed -ne [int]$counters.total -or [int]$counters.executed -ne [int]$counters.total) { throw 'Empty, failed or skipped tests' }
        if ($Phase -ne 'P0') {
            $names = @($trx.TestRun.Results.UnitTestResult | ForEach-Object { $_.testName })
            foreach ($suite in @('RunnerSmokeTests', 'RunnerIsolationTests', 'RunnerStopTests', 'RunnerProtocolTests', 'RunnerOutputTests', 'RunnerResourceTests', 'SafeRunFilesTests', 'ModelEgressTests')) {
                if (@($names | Where-Object { $_ -like "*.$suite.*" }).Count -eq 0) { throw "Missing foundation suite: $suite" }
            }
            if ($Phase -in @('P2','P3','P4')) {
                foreach ($suite in @('KnowledgeTests','DeepSeekTests')) {
                    if (@($names | Where-Object { $_ -like "*.$suite.*" }).Count -eq 0) { throw "Missing P2 suite: $suite" }
                }
            }
            $volumeCount = @($names | Where-Object { $_ -like '*.RunnerVolumeTests.*' }).Count
            if ($Phase -in @('P3','P4')) {
                foreach ($suite in @('CourseGraphTests','PlanningTests','PlanningServiceTests','PlanningViewModelTests')) {
                    if (@($names | Where-Object { $_ -like "*.$suite.*" }).Count -eq 0) { throw "Missing P3 suite: $suite" }
                }
            }
            if ($Phase -in @('P1','P2','P3','P4') -and $volumeCount -ne 0) { throw 'P1 scope unexpectedly includes PSEC volume tests' }
            if ($Phase -eq 'P4') {
                foreach ($suite in @('LearningTests','GenerationTests')) {
                    if (@($names | Where-Object { $_ -like "*.$suite.*" }).Count -eq 0) { throw "Missing P4 suite: $suite" }
                }
            }
            if ($Phase -eq 'PSEC' -and $volumeCount -lt 2) { throw 'PSEC volume tests missing' }
        }
        Write-Output "Executed $($counters.executed), passed $($counters.passed)."
    }
    if ($Phase -eq 'PSEC') {
        Invoke-Check 'real-code-readiness' {
            # Never infer product readiness from the current fixture suite.
            # Replace this blocker only when PSEC.md's integration evidence and
            # enforcement exist; a settings/manifest boolean cannot bypass it.
            throw 'blocked: PSEC integration evidence incomplete: disk lifecycle, full attacks, host file lifecycle, real provider egress, independent policy review; real code remains disabled'
        }
    } else {
    Invoke-Check 'publish' {
        Invoke-Dotnet @('publish','src/StudyHarness.Desktop','--no-restore','-c','Release','-o',"$out/candidate",'-m:1')
        if ($Phase -ne 'P0') { Invoke-Dotnet @('publish','src/StudyHarness.Runner','--no-restore','-c','Release','-o',"$out/candidate/runner",'-m:1') }
    }
    Invoke-Check 'ui-selfcheck' {
        # Runs the real desktop application and asserts observable behaviour; see
        # Plan.md section 11.2. Screenshots are supporting evidence only.
        & "$PSScriptRoot/ui-selfcheck.ps1" -ExecutablePath (Join-Path $out 'candidate/StudyHarness.Desktop.exe') -EvidencePath $out -P2:($Phase -in @('P2','P3','P4')) -P3:($Phase -in @('P3','P4'))
        $summaryPath = Join-Path $out 'ui/ui-summary.json'
        if (-not (Test-Path -LiteralPath $summaryPath)) { throw 'UI self-check produced no summary' }
        $ui = Read-Json $summaryPath
        foreach ($required in @('empty-database-startup','courses-empty-state','settings-error-code-visible','settings-save','settings-persist-after-restart')) {
            if (@($ui.checks | Where-Object { $_.id -eq $required -and $_.status -eq 'pass' }).Count -ne 1) { throw "Missing successful UI check: $required" }
        }
        if ($Phase -in @('P2','P3','P4') -and @($ui.checks | Where-Object { $_.id -eq 'p2-restart-knowledge-and-preview-denial' -and $_.status -eq 'pass' }).Count -ne 1) { throw 'Missing P2 UI checks' }
        if ([int]$ui.failed -ne 0 -or [int]$ui.leftoverProcesses -ne 0) { throw "UI self-check reported $($ui.failed) failed checks and $($ui.leftoverProcesses) leftover processes" }
        if ($Phase -in @('P3','P4')) {
            foreach ($required in @('p3-draft-edit-and-consent','p3-confirm-freeze-and-graph','p3-frozen-restart')) {
                if (@($ui.checks | Where-Object { $_.id -eq $required -and $_.status -eq 'pass' }).Count -ne 1) { throw "Missing P3 UI check: $required" }
            }
        }
        Write-Output "UI checks passed: $(@($ui.checks).Count)."
    }
    Invoke-Check 'package' {
        Compress-Archive -Path "$out/candidate/*" -DestinationPath "$out/StudyHarness-$Phase-win-x64.zip"
        Get-FileHash "$out/StudyHarness-$Phase-win-x64.zip" -Algorithm SHA256 | Format-List
    }
    }
    if ($Phase -eq 'P4') {
        Invoke-Check 'p4-ui-selfcheck' {
            & "$PSScriptRoot/p4-ui-selfcheck.ps1" -ExecutablePath (Join-Path $out 'candidate/StudyHarness.Desktop.exe') -EvidencePath (Join-Path $out 'p4-ui')
        }
    }
    if ($Phase -in @('P2','P3','P4')) {
        Invoke-Check 'real-api' {
            if (-not $RealApi) { throw 'blocked: real API check requires -RealApi and user authorization (P2: minimal OK; P3: synthetic simple C plan, no real code execution).' }
            $probe = Join-Path $RepoRoot 'tests/StudyHarness.HostProbe/bin/Release/net10.0-windows/StudyHarness.HostProbe.exe'
            $realRoot = if ($env:STUDYHARNESS_DATA_ROOT) { $env:STUDYHARNESS_DATA_ROOT } else { Join-Path $env:LOCALAPPDATA 'StudyHarness' }
            if ($Phase -eq 'P4') { & $probe --p4-real --data-root $realRoot --evidence-root (Join-Path $out 'real-theory') } elseif ($Phase -eq 'P3') { & $probe --p3-real --data-root $realRoot --evidence-root (Join-Path $out 'real-synthetic') } else { & $probe --p2-real --data-root $realRoot }
            if ($LASTEXITCODE -ne 0) { throw 'blocked: real API request did not succeed; see sanitized result' }
        }
    }
} catch { Write-Warning $_.Exception.Message }
finally {
    $files = @(Get-ChildItem src,tests,schemas,scripts,fixtures,probes,prompts -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' })
    $files += Get-Item global.json, Directory.Build.props, NuGet.Config, StudyHarness.slnx, VERSION, requirement.md, Plan.md
    # Hash build inputs including relative names; mutable narrative reports are excluded.
    $hashList = ($files | Sort-Object FullName | ForEach-Object { $_.FullName.Substring($RepoRoot.Length).Replace('\','/') + ':' + (Get-FileHash $_.FullName -Algorithm SHA256).Hash }) -join "`n"
    $treeHash = Get-Sha256Hex ([Text.Encoding]::UTF8.GetBytes($hashList))
    $ran = @($checks | ForEach-Object { $_.id })
    $notRun = @($checkOrder | Where-Object { $ran -notcontains $_ })
    foreach ($name in $notRun) { $checks.Add(@{ id=$name; status='notRun'; evidence=$null }) }
    $failed = @($checks | Where-Object { $_.status -eq 'fail' -or $_.status -eq 'blocked' }).Count
    @{ phase=$Phase; acceptanceScope=$(if ($Phase -eq 'P1') { 'fixed-fixture-foundation' } elseif ($Phase -eq 'P2') { 'knowledge-and-model' } elseif ($Phase -eq 'P3') { 'planning-and-course-graph' } elseif ($Phase -eq 'P4') { 'theory-learning-loop' } elseif ($Phase -eq 'PSEC') { 'real-code-security-prerequisites' } else { 'engineering-shell' }); scopeDecision='ADR-006'; testFilter=$testFilter; realCodeEnabled=$false; psecStatus='blocked'; memoryRequirement='deferred:R-MEM-01'; outOfScope=$(if ($Phase -in @('P1','P2','P3','P4')) { @('Gate=PSEC: RunnerVolumeTests (2 toolchain cases; known failing in prior full gate)', 'Full security and real workflow integration: docs/acceptance/PSEC.md') } else { @() }); commitOrTreeHash=$treeHash; environment=@{ os=[Environment]::OSVersion.VersionString; sdk=(& $Dotnet --version); powershell=$PSVersionTable.PSVersion.ToString() }; startedAt=$started; checks=$checks.ToArray(); failed=$failed; blocked=@($checks | Where-Object status -eq 'blocked').Count; skipped=$notRun.Count; manualPending=$true; desktopVisualPending=$true } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "$out/summary.json" -Encoding UTF8
    Write-Output "Evidence: $out"
}
if ($failed -gt 0) { exit 1 }
