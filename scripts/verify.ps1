param([ValidateSet('P0','P2','P3','P4','P5','P6','P7','P8','P9','P10','P11','P12')][string]$Phase = 'P7', [switch]$RealApi, [string]$AuthorizedPdf, [string]$P9RealEvidenceRoot, [ValidatePattern('^[0-9a-f]{40}$')][string]$BuildMetadataRevision)
. "$PSScriptRoot/environment.ps1"
Set-Location $RepoRoot
$out = Join-Path $RepoRoot ('artifacts/acceptance/' + $Phase + '/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,6))
New-Item -ItemType Directory -Force $out | Out-Null
$checks = [Collections.Generic.List[object]]::new()
$order = @('documentation','locked-restore','build','tests','test-count','publish','no-execution-dependencies','ui-selfcheck','p4-ui-selfcheck','p5-ui-selfcheck','p6-ui-selfcheck','p7-ui-selfcheck','web-ui-selfcheck','real-api','p6-real-api','p7-real-api','web-real-api','web-oi-real-api','package')
if ($Phase -eq 'P8') { $order = @($order | Where-Object { $_ -ne 'package' }) + @('p8-ui-selfcheck','p8-math','p8-network','p8-cross','p8-c','p8-cpp','p8-text','p8-oi','p8-ocr','p8-open-web','package') }
if ($Phase -eq 'P9') { $order = @('p9-quality-preflight') + @($order | Where-Object { $_ -notin @('real-api','p6-real-api','p7-real-api','web-real-api','web-oi-real-api','package') }) + @('p8-ui-selfcheck','p9-ui-selfcheck','p9-algorithms','p9-linear','p9-cpp','package') }
if ($Phase -in @('P10','P11','P12')) { $order = @($order | Where-Object { $_ -notin @('real-api','p6-real-api','p7-real-api','web-real-api','web-oi-real-api','package') }) + @('p8-ui-selfcheck','p9-ui-selfcheck','p10-ui-selfcheck','package') }
if ($Phase -in @('P11','P12')) { $order = @($order | Where-Object { $_ -ne 'package' }) + @('p11-ui-selfcheck','hybrid-ui-selfcheck','package') }
$started = [DateTimeOffset]::UtcNow.ToString('o')
function Check([string]$Name, [scriptblock]$Action) {
    try { & $Action *> (Join-Path $out "$Name.log"); $checks.Add(@{id=$Name;status='pass';evidence="$Name.log"}) }
    catch { $_.Exception.Message | Add-Content (Join-Path $out "$Name.log"); $checks.Add(@{id=$Name;status='fail';evidence="$Name.log"}); throw }
}
function Dotnet([string[]]$Arguments) {
    # Reproduce the metadata of a pre-commit verified build without changing any
    # evidence hash. The source checkout is still recorded separately below.
    if ($BuildMetadataRevision -and $Arguments[0] -in @('build','publish')) {
        $Arguments += "-p:SourceRevisionId=$BuildMetadataRevision"
    }
    & $Dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $LASTEXITCODE" }
}
try {
    if ($BuildMetadataRevision) {
        if ($Phase -ne 'P9' -or -not $P9RealEvidenceRoot) { throw 'Pinned metadata requires P9 exact-module evidence validation' }
        & git cat-file -e "$BuildMetadataRevision^{commit}"
        if ($LASTEXITCODE -ne 0) { throw 'Unknown build metadata revision' }
        @{sourceCommit=(git rev-parse HEAD).Trim();informationalVersionRevision=$BuildMetadataRevision;note='Only build metadata pinned; exact real-evidence module SHA256 checks remain mandatory.'} | ConvertTo-Json | Set-Content (Join-Path $out 'build-metadata.json') -Encoding UTF8
    }
    if ($Phase -eq 'P9') {
        Check 'p9-quality-preflight' {
            if (-not $P9RealEvidenceRoot) { throw 'P9 requires separately reviewed real evidence via -P9RealEvidenceRoot; acquire and review before the full gate.' }
            foreach ($scenario in @('algorithms','linear','cpp')) {
                $reviewRoot = Join-Path $P9RealEvidenceRoot $scenario
                $review = Get-Content -LiteralPath (Join-Path $reviewRoot 'quality-review.json') -Raw -Encoding UTF8 | ConvertFrom-Json
                if ($review.schemaVersion -ne 1 -or $review.scenario -ne $scenario -or $review.status -ne 'passed' -or $review.reviewKind -ne 'agent-static' -or $review.manualAcceptance -ne $false) {
                    throw "P9 $scenario content quality is not passed; structural success is insufficient"
                }
                foreach ($name in @('sources.json','confirmed-plan.json','reader.json','selection.json','p9-real-summary.json')) {
                    $binding = @($review.files | Where-Object { $_.name -eq $name })
                    if ($binding.Count -ne 1 -or (Get-FileHash -LiteralPath (Join-Path $reviewRoot $name) -Algorithm SHA256).Hash -ne $binding[0].sha256) {
                        throw "P9 $scenario quality review missing or stale for $name"
                    }
                }
            }
            Write-Output 'Three current source/plan/lesson static reviews passed; human acceptance remains pending.'
        }
    }
    Check 'documentation' { & "$PSScriptRoot/check-docs.ps1" }
    Check 'locked-restore' { Dotnet @('restore','StudyHarness.slnx','--locked-mode','--disable-parallel','-m:1') }
    Check 'build' { Dotnet @('build','StudyHarness.slnx','--no-restore','-c','Release','-m:1') }
    Check 'tests' { Dotnet @('test','tests/StudyHarness.Tests','--no-build','--no-restore','-c','Release','--logger','trx;LogFileName=current.trx','--results-directory',"$out/tests",'-m:1') }
    Check 'test-count' {
        [xml]$trx = Get-Content "$out/tests/current.trx" -Raw -Encoding UTF8
        $c = $trx.TestRun.ResultSummary.Counters
        if ([int]$c.total -eq 0 -or [int]$c.passed -ne [int]$c.total -or [int]$c.executed -ne [int]$c.total) { throw 'Empty, failed or skipped tests' }
        $names = @($trx.TestRun.Results.UnitTestResult | ForEach-Object { $_.testName })
        foreach ($suite in @('DeepSeekTheoryTests','WebLearningTests','CppTests','P6Tests','LabMaterialTests','KnowledgeTests','GenerationTests','LearningTests','ModelEgressTests')) {
            if (@($names | Where-Object { $_ -like "*.$suite.*" }).Count -eq 0) { throw "Missing suite $suite" }
        }
        if ($Phase -in @('P10','P11','P12')) {
            foreach ($suite in @('P10QuizFoundationTests','OptionPermutationTests')) {
                if (@($names | Where-Object { $_ -like "*.$suite.*" }).Count -eq 0) { throw "Missing P10 suite $suite" }
            }
        }
        if ($Phase -in @('P11','P12')) {
            if (@($names | Where-Object { $_ -like '*.P11SnapshotTests.*' }).Count -lt 2 -or
                @($names | Where-Object { $_ -like '*.GenerationTests.P11_*' }).Count -lt 6) { throw 'Missing P11 performance/behavior regression suites' }
        }
        if (@($names | Where-Object { $_ -like '*Tests.Runner.*' }).Count -ne 0) { throw 'Retired tests leaked into current gate' }
        Write-Output "Executed $($c.executed), passed $($c.passed), skipped 0. Retired Runner tests are archived, not counted as passing."
    }
    Check 'publish' { Dotnet @('publish','src/StudyHarness.Desktop','--no-restore','-c','Release','-o',"$out/candidate",'-m:1') }
    Check 'no-execution-dependencies' {
        $bad = @(Get-ChildItem "$out/candidate" -Recurse -File | Where-Object { $_.Name -match '(?i)(StudyHarness\.Runner|^gcc\.|^g\+\+\.|^cl\.exe|\.vhdx?$)' })
        if ($bad.Count -ne 0) { throw 'Retired runtime/compiler/disk found in package' }
        $sources = Get-ChildItem "$RepoRoot/src" -Recurse -Filter '*.cs' | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
        if ($sources | Select-String -Pattern 'Process\.Start|new ProcessStartInfo|CreateProcessW|RunnerHost') { throw 'Code launch API found in active product' }
        Write-Output 'No lab execution or compiler dependency in active source/published candidate.'
    }
    Check 'ui-selfcheck' {
        if ($Phase -eq 'P12') { & "$PSScriptRoot/p12-ui-selfcheck.ps1" -ExecutablePath "$out/candidate/StudyHarness.Desktop.exe" -EvidencePath "$out/p12-ui" }
        else { & "$PSScriptRoot/ui-selfcheck.ps1" -ExecutablePath "$out/candidate/StudyHarness.Desktop.exe" -EvidencePath $out -P2 -P3 }
    }
    Check 'p4-ui-selfcheck' { & "$PSScriptRoot/p4-ui-selfcheck.ps1" -ExecutablePath "$out/candidate/StudyHarness.Desktop.exe" -EvidencePath "$out/p4-ui" }
    Check 'p5-ui-selfcheck' { & "$PSScriptRoot/p5-ui-selfcheck.ps1" -ExecutablePath "$out/candidate/StudyHarness.Desktop.exe" -EvidencePath "$out/p5-ui" }
    Check 'p6-ui-selfcheck' { & "$PSScriptRoot/p6-ui-selfcheck.ps1" -ExecutablePath "$out/candidate/StudyHarness.Desktop.exe" -EvidencePath "$out/p6-ui" }
    Check 'p7-ui-selfcheck' { & "$PSScriptRoot/p7-ui-selfcheck.ps1" -ExecutablePath "$out/candidate/StudyHarness.Desktop.exe" -EvidencePath "$out/p7-ui" }
    Check 'web-ui-selfcheck' { & "$PSScriptRoot/web-ui-selfcheck.ps1" -ExecutablePath "$out/candidate/StudyHarness.Desktop.exe" -EvidencePath "$out/web-ui" }
    if ($Phase -notin @('P9','P10','P11','P12')) {
    Check 'real-api' {
        if (-not $RealApi) { throw 'Real DeepSeek synthetic material check requires -RealApi; Fake is not real acceptance.' }
        $dataRoot = if ($env:STUDYHARNESS_DATA_ROOT) { $env:STUDYHARNESS_DATA_ROOT } else { Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'StudyHarness' }
        & "$RepoRoot/tests/StudyHarness.HostProbe/bin/Release/net10.0-windows10.0.19041.0/StudyHarness.HostProbe.exe" --p5-real --data-root $dataRoot --evidence-root "$out/real-lab"
        if ($LASTEXITCODE -ne 0) { throw 'Real material generation/export check failed; inspect sanitized summary' }
    }
    Check 'p6-real-api' {
        if (-not $RealApi) { throw 'Real DeepSeek synthetic tutor check requires -RealApi' }
        $dataRoot = if ($env:STUDYHARNESS_DATA_ROOT) { $env:STUDYHARNESS_DATA_ROOT } else { Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'StudyHarness' }
        & "$RepoRoot/tests/StudyHarness.HostProbe/bin/Release/net10.0-windows10.0.19041.0/StudyHarness.HostProbe.exe" --p6-real --data-root $dataRoot --evidence-root "$out/real-tutor"
        if ($LASTEXITCODE -ne 0) { throw 'Real tutoring check failed' }
    }
    Check 'p7-real-api' {
        if (-not $RealApi) { throw 'Real C++ course check requires -RealApi' }
        $dataRoot = if ($env:STUDYHARNESS_DATA_ROOT) { $env:STUDYHARNESS_DATA_ROOT } else { Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'StudyHarness' }
        & "$RepoRoot/tests/StudyHarness.HostProbe/bin/Release/net10.0-windows10.0.19041.0/StudyHarness.HostProbe.exe" --p7-real --data-root $dataRoot --evidence-root "$out/real-cpp"
        if ($LASTEXITCODE -ne 0) { throw 'Real C++ course check failed' }
    }
    Check 'web-real-api' {
        if (-not $RealApi) { throw 'Real web learning requires -RealApi' }
        $dataRoot = if ($env:STUDYHARNESS_DATA_ROOT) { $env:STUDYHARNESS_DATA_ROOT } else { Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'StudyHarness' }
        & "$RepoRoot/tests/StudyHarness.HostProbe/bin/Release/net10.0-windows10.0.19041.0/StudyHarness.HostProbe.exe" --web-real --data-root $dataRoot --evidence-root "$out/real-web"
        if ($LASTEXITCODE -ne 0) { throw 'Empty-library real web course failed' }
    }
    Check 'web-oi-real-api' {
        if (-not $RealApi) { throw 'Real specified-site learning requires -RealApi' }
        $dataRoot = if ($env:STUDYHARNESS_DATA_ROOT) { $env:STUDYHARNESS_DATA_ROOT } else { Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'StudyHarness' }
        & "$RepoRoot/tests/StudyHarness.HostProbe/bin/Release/net10.0-windows10.0.19041.0/StudyHarness.HostProbe.exe" --web-real --oi-wiki --data-root $dataRoot --evidence-root "$out/real-web-oi"
        if ($LASTEXITCODE -ne 0) { throw 'Specified OI Wiki real web course failed' }
    }
    }
    if ($Phase -eq 'P8') {
        Check 'p8-ui-selfcheck' { & "$PSScriptRoot/p8-ui-selfcheck.ps1" -ExecutablePath "$out/candidate/StudyHarness.Desktop.exe" -EvidencePath "$out/p8-ui" }
        foreach ($scenario in @('math','network','cross','c','cpp','text','oi','ocr','open-web')) {
            Check "p8-$scenario" {
                if (-not $RealApi) { throw 'P8 real material verification requires -RealApi' }
                $dataRoot = if ($env:STUDYHARNESS_DATA_ROOT) { $env:STUDYHARNESS_DATA_ROOT } else { Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'StudyHarness' }
                $probeArgs = @('--p8-real','--scenario',$scenario,'--data-root',$dataRoot,'--evidence-root',"$out/p8-real-$scenario")
                if ($scenario -eq 'ocr') {
                    if (-not $AuthorizedPdf) { throw 'P8 OCR requires -AuthorizedPdf for the previously authorized fingerprint and pages 101/201/401 only' }
                    $probeArgs += @('--authorized-pdf',$AuthorizedPdf)
                }
                & "$RepoRoot/tests/StudyHarness.HostProbe/bin/Release/net10.0-windows10.0.19041.0/StudyHarness.HostProbe.exe" @probeArgs
                if ($LASTEXITCODE -ne 0) { throw "P8 $scenario failed; preserve evidence and inspect the failure" }
            }
        }
    }
    if ($Phase -eq 'P9') {
        Check 'p8-ui-selfcheck' { & "$PSScriptRoot/p8-ui-selfcheck.ps1" -ExecutablePath "$out/candidate/StudyHarness.Desktop.exe" -EvidencePath "$out/p8-ui" }
        Check 'p9-ui-selfcheck' { & "$PSScriptRoot/p9-ui-selfcheck.ps1" -ExecutablePath "$out/candidate/StudyHarness.Desktop.exe" -EvidencePath "$out/p9-ui" }
        foreach ($scenario in @('algorithms','linear','cpp')) {
            Check "p9-$scenario" {
                $realRoot = if ($P9RealEvidenceRoot) { Join-Path $P9RealEvidenceRoot $scenario } else { Join-Path $out "p9-real-$scenario" }
                $summaryPath = Join-Path $realRoot 'p9-real-summary.json'
                if (-not (Test-Path -LiteralPath $summaryPath)) {
                    if ($P9RealEvidenceRoot) { throw "Missing prevalidated P9 evidence: $scenario; no automatic paid replacement" }
                    if (-not $RealApi) { throw 'P9 real checks require -RealApi; no fake substitution' }
                    $dataRoot = if ($env:STUDYHARNESS_DATA_ROOT) { $env:STUDYHARNESS_DATA_ROOT } else { Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'StudyHarness' }
                    & "$RepoRoot/tests/StudyHarness.HostProbe/bin/Release/net10.0-windows10.0.19041.0/StudyHarness.HostProbe.exe" --p9-real --scenario $scenario --data-root $dataRoot --evidence-root $realRoot
                    if ($LASTEXITCODE -ne 0) { throw "P9 real $scenario failed; diagnose before further paid work" }
                }
                $summary = Get-Content -LiteralPath $summaryPath -Raw -Encoding UTF8 | ConvertFrom-Json
                if (-not $summary.success -or $summary.codeExecuted -or $summary.newOcrCalls -ne 0 -or @($summary.modules).Count -ne 3) { throw 'Invalid P9 evidence scope' }
                foreach ($module in $summary.modules) {
                    if ($module.name -notin @('StudyHarness.Application.dll','StudyHarness.Infrastructure.dll','StudyHarness.Contracts.dll')) { throw 'Unexpected evidence module' }
                    if ((Get-FileHash -LiteralPath (Join-Path "$out/candidate" $module.name) -Algorithm SHA256).Hash -ne $module.sha256) { throw 'Real evidence binary differs from candidate; do not silently reuse stale evidence' }
                }
                @{evidence=(Resolve-Path -LiteralPath $summaryPath).Path;sha256=(Get-FileHash -LiteralPath $summaryPath -Algorithm SHA256).Hash;checks=@($summary.checks).Count;reused=[bool]$P9RealEvidenceRoot} | ConvertTo-Json
            }
        }
    }
    if ($Phase -in @('P10','P11','P12')) {
        Check 'p8-ui-selfcheck' { & "$PSScriptRoot/p8-ui-selfcheck.ps1" -ExecutablePath "$out/candidate/StudyHarness.Desktop.exe" -EvidencePath "$out/p8-ui" }
        Check 'p9-ui-selfcheck' { & "$PSScriptRoot/p9-ui-selfcheck.ps1" -ExecutablePath "$out/candidate/StudyHarness.Desktop.exe" -EvidencePath "$out/p9-ui" }
        Check 'p10-ui-selfcheck' { & "$PSScriptRoot/p10-ui-selfcheck.ps1" -ExecutablePath "$out/candidate/StudyHarness.Desktop.exe" -EvidencePath "$out/p10-ui" }
    }
    if ($Phase -in @('P11','P12')) { Check 'p11-ui-selfcheck' { & "$PSScriptRoot/p11-ui-selfcheck.ps1" -ExecutablePath "$out/candidate/StudyHarness.Desktop.exe" -EvidencePath "$out/p11-ui" } }
    if ($Phase -in @('P11','P12')) { Check 'hybrid-ui-selfcheck' { & "$PSScriptRoot/hybrid-ui-selfcheck.ps1" -ExecutablePath "$out/candidate/StudyHarness.Desktop.exe" -EvidencePath "$out/hybrid-ui" } }
    Check 'package' { if ($Phase -eq 'P12') { Copy-Item "$RepoRoot/docs/acceptance/P12.md" "$out/candidate/P12-acceptance.md" }; if ($Phase -in @('P11','P12')) { Copy-Item "$RepoRoot/docs/acceptance/P11.md" "$out/candidate/P11-acceptance.md" }; if ($Phase -in @('P10','P11','P12')) { Copy-Item "$RepoRoot/docs/acceptance/P10.md" "$out/candidate/P10-acceptance.md" }; Copy-Item "$RepoRoot/README.md" "$out/candidate/README.md"; Copy-Item "$RepoRoot/docs/acceptance/P7.md" "$out/candidate/P7-acceptance.md"; if ($Phase -eq 'P9') { Copy-Item "$RepoRoot/docs/acceptance/P9.md" "$out/candidate/P9-acceptance.md" }; if ($Phase -eq 'P8') { Copy-Item "$RepoRoot/docs/acceptance/P8.md" "$out/candidate/P8-acceptance.md" }; Compress-Archive -Path "$out/candidate/*" -DestinationPath "$out/StudyHarness-materials-win-x64.zip"; Get-FileHash "$out/StudyHarness-materials-win-x64.zip" -Algorithm SHA256 }
} catch { Write-Warning $_.Exception.Message }
finally {
    $ran = @($checks | ForEach-Object { $_.id }); foreach ($id in $order) { if ($ran -notcontains $id) { $checks.Add(@{id=$id;status='notRun'}) } }
    $hashLines = @(Get-ChildItem src,tests,schemas,scripts,prompts -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } | Sort-Object FullName | ForEach-Object { $_.FullName.Substring($RepoRoot.Length) + ':' + (Get-FileHash $_.FullName -Algorithm SHA256).Hash })
    $hashLines += @(Get-Item requirement.md,Plan.md,StudyHarness.slnx,global.json,Directory.Build.props,VERSION | ForEach-Object { $_.Name + ':' + (Get-FileHash $_.FullName -Algorithm SHA256).Hash })
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $treeHash = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($hashLines -join "`n"))))).Replace('-','') } finally { $sha.Dispose() }
    $version = (Get-Content "$RepoRoot/VERSION" -Raw).Trim()
    $commit = (git rev-parse HEAD).Trim()
    if (Test-Path "$out/StudyHarness-materials-win-x64.zip") {
        @{version=$version;commit=$commit;tag="v$version";sourceHash=$treeHash;packageSha256=(Get-FileHash "$out/StudyHarness-materials-win-x64.zip" -Algorithm SHA256).Hash;manualPending=$true} | ConvertTo-Json | Set-Content "$out/release-manifest.json" -Encoding UTF8
    }
    $failed = @($checks | Where-Object { $_.status -ne 'pass' }).Count
    @{phase=$Phase;acceptanceScope='ADR-009-materials-only';commitOrTreeHash=$treeHash;startedAt=$started;checks=$checks.ToArray();failed=$failed;codeExecution='removed';psecStatus='retired-not-passed';manualPending=$true} | ConvertTo-Json -Depth 8 | Set-Content "$out/summary.json" -Encoding UTF8
    Write-Output "Evidence: $out"
}
if ($failed -ne 0) { exit 1 }
