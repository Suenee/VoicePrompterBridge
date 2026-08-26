$ErrorActionPreference = 'Stop'

$AppVersion = '0.8.0'
$RunnerRevision = '0.8.0-ps1.6'
$Repo = $env:SUB_UPGRADE_REPO
$Branch = if ($env:SUB_UPGRADE_BRANCH) { $env:SUB_UPGRADE_BRANCH } else { 'devel' }
$Remote = if ($env:SUB_UPGRADE_REMOTE) { $env:SUB_UPGRADE_REMOTE } else { 'https://github.com/Suenee/VoicePrompterBridge.git' }
if ([string]::IsNullOrWhiteSpace($Repo)) { $Repo = (Get-Location).ProviderPath }
$Repo = [IO.Path]::GetFullPath($Repo).TrimEnd('\','/')
Set-Location $Repo

$LogsDir = Join-Path $Repo 'logs'
New-Item -ItemType Directory -Force -Path $LogsDir | Out-Null
$UpgradeLog = Join-Path $LogsDir 'upgrade.log'
$Utf8 = [Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = $Utf8
$OutputEncoding = $Utf8
try { & chcp.com 65001 *> $null } catch { }
[IO.File]::WriteAllText($UpgradeLog, '', $Utf8)

$Phase = 'SELF-UPDATE'
$StoppedRuntime = $false
$WasRunning = $false
$BackupExe = Join-Path $env:TEMP ("SocketUniverseBridge-{0}.bak.exe" -f [guid]::NewGuid())
$BuildRoot = Join-Path $Repo '.upgrade-build'
$TrafficLog = Join-Path $LogsDir 'SocketUniverseBridge.log'

function Write-Line([string]$Text,[ConsoleColor]$Color=[ConsoleColor]::Gray) {
    [IO.File]::AppendAllText($UpgradeLog,$Text+[Environment]::NewLine,$Utf8)
    Write-Host $Text -ForegroundColor $Color
}
function Info([string]$Text) { Write-Line $Text Gray }
function Warn([string]$Text) { Write-Line ('WARNING: '+$Text) Yellow }
function Fail([string]$PhaseName,[string]$Text) { $script:Phase=$PhaseName; Write-Line ('ERROR: '+$Text) Red; throw [InvalidOperationException]::new($Text) }
function Set-Phase([string]$Name) { $script:Phase=$Name; Info ("=== $Name ===") }

function Run-Native {
    param(
        [Parameter(Mandatory=$true)][string]$PhaseName,
        [Parameter(Mandatory=$true)][string]$Exe,
        [Parameter(Mandatory=$true)][string[]]$ArgumentList,
        [switch]$AllowFailure,
        [switch]$SuppressOutput
    )
    $saved=$ErrorActionPreference
    try {
        $ErrorActionPreference='Continue'
        & $Exe @ArgumentList 2>&1 | ForEach-Object {
            if ($SuppressOutput) { return }
            $line=[string]$_
            if ($line -match '(?i)\b(error|failed|fatal error)\b|MSB\d+.*\berror\b') { Write-Line $line Red }
            elseif ($line -match '(?i)\bwarning\b') { Write-Line $line Yellow }
            else { Write-Line $line Gray }
        }
        $rc=$LASTEXITCODE
    } finally { $ErrorActionPreference=$saved }
    if ($rc -ne 0 -and -not $AllowFailure) { Fail $PhaseName ("$Exe failed with exit code $rc") }
    return $rc
}

function Require-Command([string]$Name) {
    $cmd=Get-Command $Name -ErrorAction SilentlyContinue
    if (-not $cmd) { Fail $Phase "Required command not found: $Name" }
    return $cmd.Source
}
function Get-RunningBridgeProcesses {
    $found=@()
    foreach($n in @('SocketUniverseBridge','SUB.Server','VPBridge','VPBridge.Server')) { $found += @(Get-Process -Name $n -ErrorAction SilentlyContinue) }
    return $found
}
function Stop-BridgeRuntime {
    $procs=@(Get-RunningBridgeProcesses)
    if($procs.Count -eq 0){ Info 'Bridge runtime is not running.'; return }
    $script:WasRunning=$true
    foreach($p in $procs){
        try { Info "Requesting stop: $($p.ProcessName) PID $($p.Id)"; if($p.MainWindowHandle -ne 0){[void]$p.CloseMainWindow()} } catch { Warn "Graceful stop request failed for PID $($p.Id): $($_.Exception.Message)" }
    }
    $deadline=(Get-Date).AddSeconds(2)
    do { Start-Sleep -Milliseconds 100; $alive=@(Get-RunningBridgeProcesses) } while($alive.Count -gt 0 -and (Get-Date) -lt $deadline)
    foreach($p in @(Get-RunningBridgeProcesses)){ Warn "Force stopping $($p.ProcessName) PID $($p.Id) after timeout."; Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    $script:StoppedRuntime=$true
}
function Restore-RunningState {
    if(-not $WasRunning){ Info 'Bridge was stopped before upgrade; leaving SUB stopped.'; return }
    $exe=Join-Path $Repo 'SocketUniverseBridge.exe'
    if(-not(Test-Path $exe)){ Fail 'RESTART' 'Cannot restart SUB: SocketUniverseBridge.exe is missing.' }
    Start-Process -FilePath $exe -WorkingDirectory $Repo | Out-Null
    Write-Line 'Socket Universe Bridge restarted.' Green
}
function Remove-JsonProperty($Object,[string]$Name) {
    if($null -ne $Object -and $null -ne $Object.PSObject.Properties[$Name]) { $Object.PSObject.Properties.Remove($Name) }
}
function Test-LegacyMigration([string]$DatabasePath,[string]$NodeExe) {
    if(-not(Test-Path $DatabasePath)){ return $false }
    $scriptPath=Join-Path $env:TEMP ("sub-migration-check-{0}.cjs" -f [guid]::NewGuid())
    $script=@'
const { DatabaseSync } = require('node:sqlite');
const db = new DatabaseSync(process.argv[2], { readOnly: true });
try {
  const row = db.prepare("SELECT value FROM meta WHERE key='legacy_migrated'").get();
  process.exitCode = row ? 0 : 2;
} finally {
  db.close();
}
'@
    try {
        [IO.File]::WriteAllText($scriptPath,$script,$Utf8)
        & $NodeExe $scriptPath $DatabasePath *> $null
        return ($LASTEXITCODE -eq 0)
    } catch {
        return $false
    } finally {
        Remove-Item $scriptPath -Force -ErrorAction SilentlyContinue
    }
}
function Clean-MigratedConfig([string]$ConfigPath,[string]$DatabasePath,[string]$NodeExe) {
    if(-not(Test-Path $ConfigPath) -or -not(Test-Path $DatabasePath)){ return }
    if(-not(Test-LegacyMigration $DatabasePath $NodeExe)){ Info 'SQLite mailbox migration is not confirmed; legacy JSON settings were kept.'; return }
    try {
        $cfg=Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
        Remove-JsonProperty $cfg 'heartbeat'
        Remove-JsonProperty $cfg 'queue'
        if($cfg.server){ Remove-JsonProperty $cfg.server 'host' }
        if($cfg.logging){
            Remove-JsonProperty $cfg.logging 'enabled'
            Remove-JsonProperty $cfg.logging 'directory'
            if($null -ne $cfg.logging.debugMode){ $cfg.logging.debugMode=([string]$cfg.logging.debugMode).Trim().ToLowerInvariant() }
        }
        [IO.File]::WriteAllText($ConfigPath,($cfg | ConvertTo-Json -Depth 20),$Utf8)
        Write-Line 'Removed migrated mailbox settings and derived defaults from config/vpbridge.json.' Green
    } catch { Fail 'MIGRATION' ("Could not clean migrated config: "+$_.Exception.Message) }
}

try {
    $git=Require-Command 'git.exe'
    $startingCommit=(& $git rev-parse HEAD).Trim()
    Info "Socket Universe Bridge upgrade - application $AppVersion, runner $RunnerRevision"
    Info "Repository: $Repo"
    Info "Branch: $Branch"
    Info "Starting commit: $startingCommit"
    Info 'Runner architecture: upgrade.cmd -> temporary upgrade.ps1'

    Set-Phase 'SELF-UPDATE'
    Run-Native -PhaseName $Phase -Exe $git -ArgumentList @('remote','set-url','origin',$Remote) | Out-Null
    Run-Native -PhaseName $Phase -Exe $git -ArgumentList @('fetch','origin',$Branch) | Out-Null

    & $git diff --quiet --ignore-submodules --
    $trackedDirty=($LASTEXITCODE -ne 0)
    & $git diff --cached --quiet --ignore-submodules --
    if($LASTEXITCODE -ne 0){$trackedDirty=$true}
    if($trackedDirty){
        Warn 'Local tracked changes detected; stashing tracked files before update.'
        Run-Native -PhaseName $Phase -Exe $git -ArgumentList @('stash','push','-m','Socket Universe Bridge automatic pre-upgrade stash') | Out-Null
    }

    $currentBranch=(& $git branch --show-current).Trim()
    if($currentBranch -ne $Branch){ Run-Native -PhaseName $Phase -Exe $git -ArgumentList @('switch',$Branch) | Out-Null }
    Run-Native -PhaseName $Phase -Exe $git -ArgumentList @('pull','--ff-only','origin',$Branch) | Out-Null
    $head=(& $git rev-parse HEAD).Trim(); $originHead=(& $git rev-parse "origin/$Branch").Trim()
    if(-not $head -or -not $originHead -or $head -ne $originHead){ Fail $Phase 'Local HEAD is not identical to origin/devel after update.' }
    foreach($f in @('upgrade.cmd','upgrade.ps1')){
        & $git diff --quiet -- "$f"
        if($LASTEXITCODE -ne 0){ Fail $Phase "$f has local working-tree changes after update." }
        $hb=(& $git rev-parse ("HEAD:$f")).Trim(); $rb=(& $git rev-parse ("origin/$Branch`:$f")).Trim()
        if($hb -ne $rb){ Fail $Phase "$f in HEAD is not identical to origin/devel after update." }
    }
    Write-Line "Build commit: $head" Green

    $config=Join-Path $Repo 'config\vpbridge.json'
    if(-not(Test-Path $config)){ Copy-Item (Join-Path $Repo 'config\vpbridge.example.json') $config; Info 'Created default config/vpbridge.json.' }

    Set-Phase 'DEPENDENCIES'
    $dotnetCmd=Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if(-not $dotnetCmd -or -not ((& $dotnetCmd.Source --list-sdks) -match '^10\.')){
        $winget=Require-Command 'winget.exe'
        Warn '.NET 10 SDK is missing; installing it.'
        Run-Native -PhaseName $Phase -Exe $winget -ArgumentList @('install','--id','Microsoft.DotNet.SDK.10','--exact','--accept-package-agreements','--accept-source-agreements','--silent') | Out-Null
        $env:PATH="$env:ProgramFiles\dotnet;$env:PATH"
    }
    $dotnet=Require-Command 'dotnet.exe'; $npm=Require-Command 'npm.cmd'; $node=Require-Command 'node.exe'
    Run-Native -PhaseName $Phase -Exe $npm -ArgumentList @('install') | Out-Null

    Set-Phase 'BUILD'
    if(Test-Path $BuildRoot){Remove-Item $BuildRoot -Recurse -Force}
    $serverBuild=Join-Path $BuildRoot 'server'; $publishBuild=Join-Path $BuildRoot 'publish'
    New-Item -ItemType Directory -Force -Path $serverBuild,$publishBuild | Out-Null
    Run-Native -PhaseName $Phase -Exe $npm -ArgumentList @('exec','tsc','--','--outDir',$serverBuild) | Out-Null
    Run-Native -PhaseName $Phase -Exe $dotnet -ArgumentList @('restore','native\SocketUniverseBridge.csproj') | Out-Null
    Run-Native -PhaseName $Phase -Exe $dotnet -ArgumentList @('build','native\SocketUniverseBridge.csproj','-c','Release','--no-restore','-warnaserror') | Out-Null
    $auditOut=& $dotnet list native\SocketUniverseBridge.csproj package --vulnerable --include-transitive 2>&1; $auditRc=$LASTEXITCODE
    $auditOut | ForEach-Object { Info ([string]$_) }
    if($auditRc -ne 0){ Fail $Phase 'NuGet vulnerability audit failed.' }
    $auditText=($auditOut -join "`n")
    if($auditText -match 'GHSA-[A-Za-z0-9-]+' -or $auditText -match 'CVE-\d{4}-\d+'){ Fail $Phase 'Vulnerable NuGet dependency detected.' }
    Run-Native -PhaseName $Phase -Exe $dotnet -ArgumentList @('publish','native\SocketUniverseBridge.csproj','-c','Release','-r','win-x64','--self-contained','false','-o',$publishBuild) | Out-Null
    $newExe=Join-Path $publishBuild 'SocketUniverseBridge.exe'
    if(-not(Test-Path $newExe)){Fail $Phase 'Required publish artifact is missing: SocketUniverseBridge.exe'}
    if(-not(Test-Path (Join-Path $serverBuild 'main.js'))){Fail $Phase 'Required TypeScript artifact is missing: main.js'}
    $projectText=Get-Content -LiteralPath (Join-Path $Repo 'native\SocketUniverseBridge.csproj') -Raw -Encoding UTF8
    if($projectText -notmatch '<IncludeNativeLibrariesForSelfExtract>\s*true\s*</IncludeNativeLibrariesForSelfExtract>'){Fail $Phase 'Single-file publish is not configured to bundle native SQLite libraries.'}
    $assetsPath=Join-Path $Repo 'native\obj\project.assets.json'
    if(-not(Test-Path $assetsPath)){Fail $Phase 'NuGet asset graph is missing after restore.'}
    $assetsText=Get-Content -LiteralPath $assetsPath -Raw -Encoding UTF8
    if($assetsText -notmatch 'SQLitePCLRaw\.bundle_e_sqlite3' -or $assetsText -notmatch 'runtimes/win-x64/native/e_sqlite3\.dll'){Fail $Phase 'SQLite native win-x64 runtime is missing from the restored dependency graph.'}
    Write-Line 'SQLite native runtime and single-file bundling verified.' Green
    Write-Line 'All build artifacts verified before deployment.' Green

    Set-Phase 'STOP-RUNTIME'; Stop-BridgeRuntime

    Set-Phase 'MIGRATION'
    $migrationDir=Join-Path $Repo 'config\migration-backup'; New-Item -ItemType Directory -Force -Path $migrationDir | Out-Null
    if(Test-Path $config){Copy-Item $config (Join-Path $migrationDir ("vpbridge-pre-sub-{0}.json" -f (Get-Date -Format 'yyyyMMdd-HHmmss')))}
    Get-ChildItem $migrationDir -Filter '*.json' -ErrorAction SilentlyContinue | Where-Object {$_.LastWriteTime -lt (Get-Date).AddDays(-7)} | Remove-Item -Force -ErrorAction SilentlyContinue
    Clean-MigratedConfig $config (Join-Path $Repo 'data\sub.db') $node

    Set-Phase 'DEPLOY'
    $liveExe=Join-Path $Repo 'SocketUniverseBridge.exe'; if(Test-Path $liveExe){Copy-Item $liveExe $BackupExe -Force}
    try{
        if(Test-Path (Join-Path $Repo 'dist')){Remove-Item (Join-Path $Repo 'dist') -Recurse -Force}
        Copy-Item $serverBuild (Join-Path $Repo 'dist') -Recurse -Force
        Copy-Item $newExe $liveExe -Force
        Remove-Item (Join-Path $Repo 'runtime\VPBridge.Server.exe') -Force -ErrorAction SilentlyContinue
        Remove-Item (Join-Path $Repo 'VPBridge.exe') -Force -ErrorAction SilentlyContinue
        Remove-Item (Join-Path $LogsDir 'vpbridge.log') -Force -ErrorAction SilentlyContinue
        Remove-Item (Join-Path $LogsDir 'vpbridge-tray.log') -Force -ErrorAction SilentlyContinue
        if(-not(Test-Path $TrafficLog)){New-Item -ItemType File -Path $TrafficLog | Out-Null}
    }catch{ if(Test-Path $BackupExe){Copy-Item $BackupExe $liveExe -Force -ErrorAction SilentlyContinue}; throw }

    $runKey='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $legacy=Get-ItemProperty -Path $runKey -Name 'VoicePrompterBridge' -ErrorAction SilentlyContinue
    if($legacy){Set-ItemProperty -Path $runKey -Name 'SocketUniverseBridge' -Value ('"'+$liveExe+'"'); Remove-ItemProperty -Path $runKey -Name 'VoicePrompterBridge' -ErrorAction SilentlyContinue; Write-Line 'Migrated legacy VPB autostart to SocketUniverseBridge.' Green}

    Set-Phase 'DEPENDENCY-CLEANUP'
    $wingetCmd=Get-Command winget.exe -ErrorAction SilentlyContinue
    if($wingetCmd){foreach($id in @('Microsoft.DotNet.SDK.8','Microsoft.DotNet.DesktopRuntime.8','Microsoft.DotNet.Runtime.8','Microsoft.DotNet.AspNetCore.8')){Run-Native -PhaseName $Phase -Exe $wingetCmd.Source -ArgumentList @('uninstall','--id',$id,'--exact','--silent') -AllowFailure -SuppressOutput | Out-Null}}

    Set-Phase 'RESTART'; Restore-RunningState
    Set-Phase 'COMPLETE'
    Remove-Item $BuildRoot -Recurse -Force -ErrorAction SilentlyContinue; Remove-Item $BackupExe -Force -ErrorAction SilentlyContinue
    Info "Upgrade log: $UpgradeLog"
    Write-Line 'STATUS: SUCCESS - phase=COMPLETE' Green
    exit 0
}
catch{
    Write-Line ("FAILED in phase {0}: {1}" -f $Phase,$_.Exception.Message) Red
    if($StoppedRuntime -and $WasRunning){try{Restore-RunningState}catch{Write-Line ("Recovery restart failed: "+$_.Exception.Message) Red}}
    Remove-Item $BuildRoot -Recurse -Force -ErrorAction SilentlyContinue
    Write-Line "STATUS: FAILED - phase=$Phase" Red
    exit 1
}
