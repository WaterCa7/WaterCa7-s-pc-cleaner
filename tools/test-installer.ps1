# Installs, checks, updates and uninstalls the setup on a throwaway machine (the CI runner). Never run it on a real PC.
param([Parameter(Mandatory)] [string]$Setup)
$ErrorActionPreference = 'Stop'
$Setup = (Resolve-Path $Setup).Path
$root = Split-Path $PSScriptRoot -Parent
$app = Join-Path $env:ProgramFiles 'WaterCa7s PC Cleaner'
$exe = Join-Path $app 'WaterCa7sPcCleaner.exe'
$uninstallKey = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{6F1B3C2E-8A4D-4F5B-9C7E-2D1A0B9E8F37}_is1'
$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$task = "\WaterCa7s PC Cleaner\Start $sid"
$dataDir = Join-Path $env:LOCALAPPDATA 'WaterCa7sPcCleaner'
$logDir = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }
$setupLog = Join-Path $logDir 'setup.log'
$shots = Join-Path $logDir 'installed-shots'

function Check([bool]$ok, [string]$what) { if (-not $ok) { throw "FAILED: $what" } ; "ok: $what" }

function TaskExists {
    $ErrorActionPreference = 'Continue'
    schtasks.exe /query /tn $task 2>&1 | Out-Null
    return $LASTEXITCODE -eq 0
}

# The program processes that run from the installed exe (the setup and the uninstaller refuse to run while one is open).
function InstalledProcesses {
    @(Get-Process -Name 'WaterCa7sPcCleaner' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })
}

function WaitForProgram([int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if ((InstalledProcesses).Count -gt 0) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function StopProgram {
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        $running = InstalledProcesses
        if ($running.Count -eq 0) { return $true }
        $running | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
    }
    return ((InstalledProcesses).Count -eq 0)
}

function RunSetup([string[]]$extra) {
    $setupArgs = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$setupLog`"") + $extra
    # No -Wait: it would also wait for the program the setup starts after an update.
    $p = Start-Process $Setup -ArgumentList $setupArgs -PassThru
    Check ($p.WaitForExit(600000)) 'the setup finished in 10 minutes'
    Check ($p.ExitCode -eq 0) "setup exit code 0 (was $($p.ExitCode))"
}

function WritableByUsers([string]$path) {
    # Write data, append data, extended attributes, delete child, write attributes, delete, change permissions, take ownership, generic write/all.
    $writeMask = 0x500D0156
    $groups = @('BUILTIN\Users', 'NT AUTHORITY\Authenticated Users')
    @((Get-Acl $path).Access | Where-Object {
        $_.AccessControlType -eq 'Allow' -and
        ($groups -contains $_.IdentityReference.Value) -and
        (([int]$_.FileSystemRights -band $writeMask) -ne 0)
    })
}

function ShowLogs {
    "----- diagnostics -----"
    $newest = Get-ChildItem $env:TEMP -Filter 'Setup Log*.txt' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($newest) { "----- $($newest.FullName) -----"; Get-Content $newest.FullName -ErrorAction SilentlyContinue }
    else { "no Setup Log*.txt in $env:TEMP" }
    if (Test-Path $setupLog) { "----- $setupLog -----"; Get-Content $setupLog -ErrorAction SilentlyContinue }
}

try {
    [xml]$project = Get-Content (Join-Path $root 'src\PcCleaner\PcCleaner.csproj')
    $version = ($project.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1
    Check ($version -match '^\d+\.\d+\.\d+$') "csproj version $version"

    RunSetup @()
    Check (Test-Path $exe) 'the exe is installed'
    Check (Test-Path (Join-Path $app 'WaterCa7sPcCleaner.installed')) 'the marker is installed'
    Check (Test-Path $uninstallKey) 'Programs and Features entry'
    Check ((Get-ItemProperty $uninstallKey).DisplayVersion -match '^\d+\.\d+\.\d+$') 'its version'
    Check (Test-Path (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\WaterCa7s PC Cleaner.lnk')) 'Start menu shortcut'
    Check (Test-Path (Join-Path $env:PUBLIC 'Desktop\WaterCa7s PC Cleaner.lnk')) 'desktop shortcut'
    Check ((WritableByUsers $app).Count -eq 0) 'ordinary users cannot write to the program folder'
    Check ((Get-Item $exe).VersionInfo.FileVersion.StartsWith($version)) "the exe file version starts with $version (was $((Get-Item $exe).VersionInfo.FileVersion))"
    Check (TaskExists) "task $task"
    $xml = [xml](schtasks.exe /query /tn $task /xml | Out-String)
    Check ($xml.Task.Actions.Exec.Command -eq $exe) 'the task runs the installed exe'
    Check ($xml.Task.Principals.Principal.RunLevel -eq 'HighestAvailable') 'the task runs elevated'

    $p = Start-Process $exe -ArgumentList '--screenshots', "`"$shots`"", '--page', 'Dashboard', '--no-scan' -PassThru
    Check ($p.WaitForExit(180000)) 'the installed program finished its screenshot run'
    Check ($p.ExitCode -eq 0) "screenshot run exit code 0 (was $($p.ExitCode))"
    Check ((Get-ChildItem $shots -Filter *.png).Count -ge 1) 'a screenshot was taken'

    # The task starts the program.
    schtasks.exe /run /tn $task
    Check ($LASTEXITCODE -eq 0) "schtasks /run exit code 0 (was $LASTEXITCODE)"
    Check (WaitForProgram 30) 'the task started the installed program'
    Check (StopProgram) 'the program started by the task is stopped'

    RunSetup @() # over the installed copy, silent and plain
    Check (Test-Path $exe) 'the exe is still there after the reinstall'
    Check (TaskExists) 'the task is still there after the reinstall'

    # An update: the program is started again by the setup.
    RunSetup @('/UPDATEDFROM=1.0.0')
    Check (Test-Path $exe) 'the exe is still there after the update'
    Check (WaitForProgram 30) 'the update started the installed program'
    Check (StopProgram) 'the program started by the update is stopped'

    New-Item -ItemType Directory -Force $dataDir | Out-Null
    Set-Content (Join-Path $dataDir 'settings.json') '{}'
    $uninstaller = Join-Path $app 'unins000.exe'
    Check (Test-Path $uninstaller) 'the uninstaller is installed'
    $p = Start-Process $uninstaller -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -PassThru
    $null = $p.WaitForExit(300000)
    # The uninstaller restarts itself from a temp folder; wait for the program folder to go.
    $deadline = (Get-Date).AddSeconds(120)
    while ((Test-Path $app) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 2 }
    Check (-not (Test-Path $app)) 'the program folder is removed'
    Check (-not (Test-Path $uninstallKey)) 'the Programs and Features entry is removed'
    Check (-not (TaskExists)) 'the task is removed'
    Check (Test-Path (Join-Path $dataDir 'settings.json')) 'the settings stay after a silent uninstall'
    Check ((@(Get-ChildItem $env:TEMP -Filter 'Setup Log*.txt' -ErrorAction SilentlyContinue)).Count -ge 1) 'the setup left a log in %TEMP%'
    'installer test passed'
}
catch {
    "ERROR: $($_.Exception.Message)"
    ShowLogs
    throw
}
