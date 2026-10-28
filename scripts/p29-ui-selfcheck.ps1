param([string]$ExecutablePath,[string]$EvidencePath)
$ErrorActionPreference='Stop'
$evidence=[IO.Path]::GetFullPath($EvidencePath)
$data=Join-Path $evidence 'synthetic-data'
if(Test-Path $data){throw 'Fresh synthetic root required'}
New-Item -ItemType Directory -Force $data | Out-Null
Set-Content (Join-Path $data 'p29-synthetic.marker') 'Synthetic P29 only'
$previous=$env:STUDYHARNESS_DATA_ROOT
try {
 $env:STUDYHARNESS_DATA_ROOT=$data
 foreach($pass in @('ui','restart')) {
  $process=Start-Process -FilePath $ExecutablePath -ArgumentList ('--p29-selfcheck --evidence "'+$evidence+'"') -PassThru -WindowStyle Hidden
  if(-not $process.WaitForExit(60000)){ $process.Kill(); throw 'P29 timeout' }
  if($process.ExitCode -ne 0){throw "P29 failed: $evidence"}
  $report=Get-Content (Join-Path $evidence "$pass.json") -Raw | ConvertFrom-Json
  if($report.failed -ne 0){throw 'P29 checks failed'}
  if($pass -eq 'ui') {
   $before=@(Get-ChildItem -LiteralPath $data -Recurse -File -Filter '*.db' | ForEach-Object { [PSCustomObject]@{Path=$_.FullName;Hash=(Get-FileHash -LiteralPath $_.FullName).Hash} })
  } else {
   foreach($file in $before){if((Get-FileHash -LiteralPath $file.Path).Hash -ne $file.Hash){throw "Source changed: $($file.Path)"}}
   $before | ConvertTo-Json | Set-Content (Join-Path $evidence 'source-preserved.json')
  }
  Write-Output "P29 $pass passed $(@($report.checks).Count) checks"
 }
} finally {$env:STUDYHARNESS_DATA_ROOT=$previous}