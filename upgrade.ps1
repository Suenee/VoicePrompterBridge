$ErrorActionPreference = 'Stop'

$AppVersion = '0.8.0'
$RunnerRevision = '0.8.0-ps1.2'
$Repo = $env:SUB_UPGRADE_REPO
$Branch = if ($env:SUB_UPGRADE_BRANCH) { $env:SUB_UPGRADE_BRANCH } else { 'devel' }
$Remote = if ($env:SUB_UPGRADE_REMOTE) { $env:SUB_UPGRADE_REMOTE } else { 'https://github.com/Suenee/VoicePrompterBridge.git' }
if ([string]::IsNullOrWhiteSpace($Repo)) { throw 'SUB_UPGRADE_REPO is not set.' }
$Repo = [System.IO.Path]::GetFullPath($Repo).TrimEnd([char[]]@('\','/'))
Set-Location $Repo

$LogsDir = Join-Path $Repo 'logs'
New-Item -ItemType Directory -Force -Path $LogsDir | Out-Null
$UpgradeLog = Join-Path $LogsDir 'upgrade.log'
Set-Content -Path $UpgradeLog -Value '' -Encoding UTF8
$Phase = 'SELF-UPDATE'
$StoppedRuntime = $false
$WasRunning = $false
$BackupExe = Join-Path $env:TEMP ("SocketUniverseBridge-{0}.bak.exe" -f [guid]::NewGuid())
$BuildRoot = Join-Path $Repo '.upgrade-build'
$TrafficLog = Join-Path $LogsDir 'SocketUniverseBridge.log'

function Write-StatusLine {
    param([string]$Text,[ValidateSet('INFO','WARN','ERROR','SUCCESS')][string]$Level='INFO')
    $stamp = Get-Date -Format 'dd.MM.yyyy HH:mm:ss.fff'
    $line = "$stamp  [$Level]  $Text"
    Add-Content -Path $UpgradeLog -Value $line -Encoding UTF8
    $color = switch ($Level) { 'WARN' {'Yellow'} 'ERROR' {'Red'} 'SUCCESS' {'Green'} default {'Gray'} }
    Write-Host $Text -ForegroundColor $color
}

function ConvertTo-NativeArgument {
    param([AllowEmptyString()][string]$Value)
    if ($Value -notmatch '[\s"]') { return $Value }
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('"')
    $slashes = 0
    foreach ($ch in $Value.ToCharArray()) {
        if ($ch -eq '\') { $slashes++; continue }
        if ($ch -eq '"') {
            if ($slashes -gt 0) { [void]$sb.Append(('\' * ($slashes * 2))) }
            [void]$sb.Append('\"')
            $slashes = 0
            continue
        }
        if ($slashes -gt 0) { [void]$sb.Append(('\' * $slashes)); $slashes = 0 }
        [void]$sb.Append($ch)
    }
    if ($slashes -gt 0) { [void]$sb.Append(('\' * ($slashes * 2))) }
    [void]$sb.Append('"')
    return $sb.ToString()
}

function Invoke-Native {
    param([string]$Exe,[string[]]$Args,[switch]$AllowFailure)
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.Arguments = (($Args | ForEach-Object { ConvertTo-NativeArgument $_ }) -join ' ')
    $psi.WorkingDirectory = $Repo
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $p = New-Object System.Diagnostics.Process
    $p.StartInfo = $psi
    [void]$p.Start()
    $stdout = $p.StandardOutput.ReadToEnd()
    $stderr = $p.StandardError.ReadToEnd()
    $p.WaitForExit()
    $code = $p.ExitCode
    foreach ($block in @($stdout,$stderr)) {
        if (-not [string]::IsNullOrWhiteSpace($block)) {
            foreach ($line in ($block -split "`r?`n")) { if ($line.Length -gt 0) { Write-StatusLine $line 'INFO' } }
        }
    }
    if ($code -ne 0 -and -not $AllowFailure) { throw "$Exe failed with exit code $code" }
    return @{ ExitCode=$code; StdOut=$stdout; StdErr=$stderr }
}

function Require-Command {
    param([string]$Name)
    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if (-not $cmd) { throw "Required command not found: $Name" }
    return $cmd.Source
}

function Set-Phase([string]$Name) { $script:Phase=$Name; Write-StatusLine "=== $Name ===" 'INFO' }

function Get-RunningBridgeProcesses {
    $names = @('SocketUniverseBridge','SUB.Server','VPBridge','VPBridge.Server')
    $found = @()
    foreach ($n in $names) { $found += @(Get-Process -Name $n -ErrorAction SilentlyContinue) }
    return $found
}

function Stop-BridgeRuntime {
    $procs = @(Get-RunningBridgeProcesses)
    if ($procs.Count -eq 0) { Write-StatusLine 'Bridge runtime is not running.' 'INFO'; return }
    $script:WasRunning = $true
    foreach ($p in $procs) {
        try {
            Write-StatusLine "Requesting stop: $($p.ProcessName) PID $($p.Id)" 'INFO'
            if ($p.MainWindowHandle -ne 0) { [void]$p.CloseMainWindow() }
        } catch { Write-StatusLine "Graceful stop request failed for PID $($p.Id): $($_.Exception.Message)" 'WARN' }
    }
    $deadline = (Get-Date).AddSeconds(2)
    do { Start-Sleep -Milliseconds 100; $alive = @(Get-RunningBridgeProcesses) } while ($alive.Count -gt 0 -and (Get-Date) -lt $deadline)
    foreach ($p in @(Get-RunningBridgeProcesses)) {
        Write-StatusLine "Force stopping $($p.ProcessName) PID $($p.Id) after timeout." 'WARN'
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    }
    $script:StoppedRuntime = $true
}

function Restore-RunningState {
    if (-not $WasRunning) { Write-StatusLine 'Bridge was stopped before upgrade; leaving SUB stopped.' 'INFO'; return }
    $exe = Join-Path $Repo 'SocketUniverseBridge.exe'
    if (-not (Test-Path $exe)) { throw 'Cannot restart SUB: SocketUniverseBridge.exe is missing.' }
    Start-Process -FilePath $exe -WorkingDirectory $Repo | Out-Null
    Write-StatusLine 'Socket Universe Bridge restarted.' 'SUCCESS'
}

try {
    $git = Require-Command 'git.exe'
    $startingCommit = (& $git rev-parse HEAD).Trim()
    Write-StatusLine "Socket Universe Bridge upgrade - application $AppVersion, runner $RunnerRevision" 'INFO'
    Write-StatusLine "Repository: $Repo" 'INFO'
    Write-StatusLine "Branch: $Branch" 'INFO'
    Write-StatusLine "Starting commit: $startingCommit" 'INFO'
    Write-StatusLine 'Runner architecture: upgrade.cmd -> temporary upgrade.ps1' 'INFO'

    Set-Phase 'SELF-UPDATE'
    Invoke-Native $git @('remote','set-url','origin',$Remote) | Out-Null
    Invoke-Native $git @('fetch','origin',$Branch) | Out-Null
    $tracked = (& $git status --porcelain --untracked-files=no)
    if ($tracked) { throw 'Local tracked/staged changes exist. Upgrade will not overwrite them.' }
    $currentBranch = (& $git branch --show-current).Trim()
    if ($currentBranch -ne $Branch) { Invoke-Native $git @('checkout',$Branch) | Out-Null }
    Invoke-Native $git @('merge','--ff-only',"origin/$Branch") | Out-Null
    $head = (& $git rev-parse HEAD).Trim()
    $originHead = (& $git rev-parse "origin/$Branch").Trim()
    if ($head -ne $originHead) { throw "Repository verification failed: HEAD $head != origin/$Branch $originHead" }
    Write-StatusLine "Build commit: $head" 'SUCCESS'

    if (-not (Test-Path (Join-Path $Repo 'config\vpbridge.json'))) {
        Copy-Item (Join-Path $Repo 'config\vpbridge.example.json') (Join-Path $Repo 'config\vpbridge.json')
        Write-StatusLine 'Created default config/vpbridge.json.' 'INFO'
    }

    Set-Phase 'DEPENDENCIES'
    $dotnetCmd = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if (-not $dotnetCmd -or -not ((& $dotnetCmd.Source --list-sdks) -match '^10\.')) {
        $winget = Require-Command 'winget.exe'
        Write-StatusLine '.NET 10 SDK is missing; installing it.' 'WARN'
        Invoke-Native $winget @('install','--id','Microsoft.DotNet.SDK.10','--exact','--accept-package-agreements','--accept-source-agreements','--silent') | Out-Null
        $env:PATH = "$env:ProgramFiles\dotnet;$env:PATH"
    }
    $dotnet = Require-Command 'dotnet.exe'
    $npm = Require-Command 'npm.cmd'
    Invoke-Native $npm @('install') | Out-Null

    Set-Phase 'BUILD'
    if (Test-Path $BuildRoot) { Remove-Item $BuildRoot -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $BuildRoot | Out-Null
    $serverBuild = Join-Path $BuildRoot 'server'
    $publishBuild = Join-Path $BuildRoot 'publish'
    New-Item -ItemType Directory -Force -Path $serverBuild,$publishBuild | Out-Null
    Invoke-Native $npm @('exec','tsc','--','--outDir',$serverBuild) | Out-Null
    Invoke-Native $dotnet @('restore','native\SocketUniverseBridge.csproj') | Out-Null
    Invoke-Native $dotnet @('build','native\SocketUniverseBridge.csproj','-c','Release','--no-restore','-warnaserror') | Out-Null
    $audit = Invoke-Native $dotnet @('list','native\SocketUniverseBridge.csproj','package','--vulnerable','--include-transitive')
    $auditText = ($audit.StdOut + "`n" + $audit.StdErr)
    if ($auditText -match 'GHSA-[A-Za-z0-9-]+' -or $auditText -match 'CVE-\d{4}-\d+') { throw 'Vulnerable NuGet dependency detected.' }
    Invoke-Native $dotnet @('publish','native\SocketUniverseBridge.csproj','-c','Release','-r','win-x64','--self-contained','false','-o',$publishBuild) | Out-Null
    $newExe = Join-Path $publishBuild 'SocketUniverseBridge.exe'
    if (-not (Test-Path $newExe)) { throw 'Required publish artifact is missing: SocketUniverseBridge.exe' }
    if (-not (Test-Path (Join-Path $serverBuild 'main.js'))) { throw 'Required TypeScript artifact is missing: main.js' }
    Write-StatusLine 'All build artifacts verified before deployment.' 'SUCCESS'

    Set-Phase 'STOP-RUNTIME'
    Stop-BridgeRuntime

    Set-Phase 'MIGRATION'
    $migrationDir = Join-Path $Repo 'config\migration-backup'
    New-Item -ItemType Directory -Force -Path $migrationDir | Out-Null
    $config = Join-Path $Repo 'config\vpbridge.json'
    if (Test-Path $config) { Copy-Item $config (Join-Path $migrationDir ("vpbridge-pre-sub-{0}.json" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))) }
    Get-ChildItem $migrationDir -Filter '*.json' -ErrorAction SilentlyContinue | Where-Object {$_.LastWriteTime -lt (Get-Date).AddDays(-7)} | Remove-Item -Force -ErrorAction SilentlyContinue

    Set-Phase 'DEPLOY'
    $liveExe = Join-Path $Repo 'SocketUniverseBridge.exe'
    if (Test-Path $liveExe) { Copy-Item $liveExe $BackupExe -Force }
    try {
        if (Test-Path (Join-Path $Repo 'dist')) { Remove-Item (Join-Path $Repo 'dist') -Recurse -Force }
        Copy-Item $serverBuild (Join-Path $Repo 'dist') -Recurse -Force
        Copy-Item $newExe $liveExe -Force
        Remove-Item (Join-Path $Repo 'runtime\VPBridge.Server.exe') -Force -ErrorAction SilentlyContinue
        Remove-Item (Join-Path $Repo 'VPBridge.exe') -Force -ErrorAction SilentlyContinue
        Remove-Item (Join-Path $LogsDir 'vpbridge.log') -Force -ErrorAction SilentlyContinue
        Remove-Item (Join-Path $LogsDir 'vpbridge-tray.log') -Force -ErrorAction SilentlyContinue
        if (-not (Test-Path $TrafficLog)) { New-Item -ItemType File -Path $TrafficLog | Out-Null }
    } catch {
        if (Test-Path $BackupExe) { Copy-Item $BackupExe $liveExe -Force -ErrorAction SilentlyContinue }
        throw
    }

    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $legacy = Get-ItemProperty -Path $runKey -Name 'VoicePrompterBridge' -ErrorAction SilentlyContinue
    if ($legacy) {
        Set-ItemProperty -Path $runKey -Name 'SocketUniverseBridge' -Value ('"' + $liveExe + '"')
        Remove-ItemProperty -Path $runKey -Name 'VoicePrompterBridge' -ErrorAction SilentlyContinue
        Write-StatusLine 'Migrated legacy VPB autostart to SocketUniverseBridge.' 'SUCCESS'
    }

    Set-Phase 'DEPENDENCY-CLEANUP'
    $wingetCmd = Get-Command winget.exe -ErrorAction SilentlyContinue
    if ($wingetCmd) {
        foreach ($id in @('Microsoft.DotNet.SDK.8','Microsoft.DotNet.DesktopRuntime.8','Microsoft.DotNet.Runtime.8','Microsoft.DotNet.AspNetCore.8')) {
            $r = Invoke-Native $wingetCmd.Source @('uninstall','--id',$id,'--exact','--silent') -AllowFailure
            if ($r.ExitCode -eq 0) { Write-StatusLine "Removed obsolete $id." 'INFO' }
        }
    }

    Set-Phase 'RESTART'
    Restore-RunningState

    Set-Phase 'COMPLETE'
    Remove-Item $BuildRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $BackupExe -Force -ErrorAction SilentlyContinue
    Write-StatusLine "Upgrade log: $UpgradeLog" 'INFO'
    Write-StatusLine 'STATUS: SUCCESS - phase=COMPLETE' 'SUCCESS'
    exit 0
}
catch {
    Write-StatusLine ("FAILED in phase {0}: {1}" -f $Phase,$_.Exception.Message) 'ERROR'
    if ($StoppedRuntime -and $WasRunning) {
        try { Restore-RunningState } catch { Write-StatusLine ("Recovery restart failed: " + $_.Exception.Message) 'ERROR' }
    }
    Remove-Item $BuildRoot -Recurse -Force -ErrorAction SilentlyContinue
    Write-StatusLine "STATUS: FAILED - phase=$Phase" 'ERROR'
    exit 1
}
