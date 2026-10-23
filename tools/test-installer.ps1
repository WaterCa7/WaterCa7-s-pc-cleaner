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

# A mutex of this session (the program's single-copy place, the setup's SetupMutex). This script runs elevated, so it may open both.
function MutexExists([string]$name) {
    $m = $null
    try {
        if (-not [System.Threading.Mutex]::TryOpenExisting("Local\$name", [ref]$m)) { return $false }
        $m.Dispose()
    }
    catch [System.UnauthorizedAccessException] { } # it exists, only this account may not open it
    return $true
}

function WaitForMutex([string]$name, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (MutexExists $name) { return $true }
        Start-Sleep -Milliseconds 200
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

# The first runs pass /LOG; the update run does not, as the program's own update does not: SetupLogging=yes must leave the log in %TEMP%.
# -Mode /SILENT is how the program runs an update (only the progress window).
function RunSetup([string[]]$extra, [switch]$NoLog, [string]$Mode = '/VERYSILENT') {
    $setupArgs = @($Mode, '/SUPPRESSMSGBOXES', '/NORESTART') + $extra
    if (-not $NoLog) { $setupArgs += "/LOG=`"$setupLog`"" }
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
    Check ((Get-ItemProperty $uninstallKey).DisplayVersion -eq $version) "its version is $version"
    Check (Test-Path (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\WaterCa7s PC Cleaner.lnk')) 'Start menu shortcut'
    Check (Test-Path (Join-Path $env:PUBLIC 'Desktop\WaterCa7s PC Cleaner.lnk')) 'desktop shortcut'
    Check ((WritableByUsers $app).Count -eq 0) 'ordinary users cannot write to the program folder'
    Check ((Get-Item $exe).VersionInfo.FileVersion.StartsWith("$version.")) "the exe file version starts with $version. (was $((Get-Item $exe).VersionInfo.FileVersion))"
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

    # An update the way the program hands it off: the program is open (it holds its single-copy mutex), the setup starts and waits
    # for it, the program goes away (here: it is stopped), the setup installs and starts the program again.
    Check ((InstalledProcesses).Count -eq 0) 'no installed program is running before the update over a running copy'
    $null = Start-Process $exe -PassThru
    Check (WaitForMutex 'WaterCa7sPcCleaner.Instance' 60) 'the installed program runs and holds its mutex'
    $p = Start-Process $Setup -ArgumentList '/SILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/UPDATEDFROM=1.0.0' -PassThru
    # Inno creates SetupMutex only after InitializeSetup, so it is looked for once the setup goes on to install.
    Start-Sleep -Seconds 3
    Check (-not $p.HasExited) 'the update setup waits for the open program'
    Check ((InstalledProcesses).Count -gt 0) 'the setup has not replaced the program while it was open'
    Check (StopProgram) 'the open program is stopped while the setup waits for it'
    $setupMutexSeen = $false
    $deadline = (Get-Date).AddMinutes(10)
    while (-not $p.HasExited -and (Get-Date) -lt $deadline) {
        if (-not $setupMutexSeen -and (MutexExists 'WaterCa7sPcCleaner.Setup')) { $setupMutexSeen = $true }
        Start-Sleep -Milliseconds 50
    }
    Check ($p.WaitForExit(1000)) 'the update setup over a running copy finished in 10 minutes'
    Check $setupMutexSeen 'the setup held WaterCa7sPcCleaner.Setup while it installed'
    Check ($p.ExitCode -eq 0) "update setup over a running copy exit code 0 (was $($p.ExitCode))"
    Check (WaitForProgram 60) 'the update over a running copy started the program again'
    Check (StopProgram) 'the program started by that update is stopped'

    # An update: the program is started again by the setup. Old setup logs must not satisfy the SetupLogging check below.
    Get-ChildItem $env:TEMP -Filter 'Setup Log*.txt' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    Check ((InstalledProcesses).Count -eq 0) 'no installed program is running before the update'
    RunSetup @('/UPDATEDFROM=1.0.0') -NoLog -Mode '/SILENT'
    Check (Test-Path $exe) 'the exe is still there after the update'
    Check (WaitForProgram 30) 'the update started the installed program'
    Check (StopProgram) 'the program started by the update is stopped'
    Check (TaskExists) 'the task is still there after the update'
    Check ((@(Get-ChildItem $env:TEMP -Filter 'Setup Log*.txt' -ErrorAction SilentlyContinue)).Count -ge 1) 'the update (run without /LOG) left a setup log in %TEMP%'

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
    # The last native command (schtasks, looking for the removed task) left exit code 1, and the CI shell would report it as the script's.
    $global:LASTEXITCODE = 0
    'installer test passed'
}
catch {
    "ERROR: $($_.Exception.Message)"
    ShowLogs
    throw
}
