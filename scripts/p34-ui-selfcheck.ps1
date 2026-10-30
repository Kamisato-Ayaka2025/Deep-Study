param([Parameter(Mandatory=$true)][string]$ExecutablePath,[Parameter(Mandatory=$true)][string]$EvidencePath)
$ErrorActionPreference='Stop'
$evidence=[IO.Path]::GetFullPath($EvidencePath)
$data=Join-Path $evidence 'synthetic-data'
if(Test-Path -LiteralPath $data){throw 'Fresh synthetic root required'}
New-Item -ItemType Directory -Force $data | Out-Null
Set-Content (Join-Path $data 'p34-synthetic.marker') 'P34 synthetic only'
$previous=$env:STUDYHARNESS_DATA_ROOT
try {
 $env:STUDYHARNESS_DATA_ROOT=$data
 foreach($pass in @('ui','restart')) {
  $process=Start-Process -FilePath $ExecutablePath -ArgumentList ('--p34-selfcheck --evidence "'+$evidence+'"') -PassThru -WindowStyle Hidden
  if(-not $process.WaitForExit(60000)){ $process.Kill(); throw 'P34 desktop timeout' }
  if($process.ExitCode -ne 0){throw "P34 desktop failed: $evidence"}
  $report=Get-Content (Join-Path $evidence "$pass.json") -Raw | ConvertFrom-Json
  if($report.failed -ne 0){throw 'P34 checks failed'}
  Write-Output "P34 $pass passed $(@($report.checks).Count) checks"
 }
} finally {$env:STUDYHARNESS_DATA_ROOT=$previous}
