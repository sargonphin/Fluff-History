# Opt-in installed Windows regression. Run unelevated in the interactive user's session,
# with the management window already open. Does not invoke a command or change user data.
[CmdletBinding()]
param([Parameter(Mandatory)][int]$UiProcessId,
    [ValidateRange(1,100)][int]$Iterations = 10,
    [ValidateSet('Sargonphin.FluffHistory','Sargonphin.FluffHistory.DeveloperPreview')][string]$PackageIdentity = 'Sargonphin.FluffHistory',
    [Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([Environment]::OSVersion.Platform -ne 'Win32NT') { throw 'Requires an installed Windows application.' }
if (Test-Path -LiteralPath $OutputPath) { throw 'Choose a new evidence file.' }
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class InstalledShellLifetimeProbe {
    [ComImport, Guid("A08CE4D0-FA25-44AB-B57C-C7B1C323E0B9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IExplorerCommand {
        [PreserveSig] int GetTitle(IntPtr selection, out IntPtr title);
    }
    [DllImport("ole32.dll", PreserveSig=true)]
    static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IExplorerCommand command);
    public static object Activate() {
        Guid clsid = new Guid("80F01E21-B634-4183-834E-CF09D7242401");
        Guid iid = typeof(IExplorerCommand).GUID;
        IExplorerCommand command;
        // Activate the installed, packaged COM surrogate, rather than LoadLibrary of the DLL.
        Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, IntPtr.Zero, 4, ref iid, out command));
        try {
            IntPtr title;
            Marshal.ThrowExceptionForHR(command.GetTitle(IntPtr.Zero, out title));
            try {
                if (Marshal.PtrToStringUni(title) != "Restore previous version\u2026")
                    throw new InvalidOperationException("Unexpected installed Explorer command.");
            } finally { Marshal.FreeCoTaskMem(title); }
            return command;
        } catch { Marshal.FinalReleaseComObject(command); throw; }
    }
    public static void Release(object command) { Marshal.FinalReleaseComObject(command); }
}
'@
function ManagementWindows {
    @([Windows.Automation.AutomationElement]::RootElement.FindAll(
        [Windows.Automation.TreeScope]::Children,[Windows.Automation.Condition]::TrueCondition) |
        Where-Object { $_.Current.ClassName -eq 'WinUIDesktopWin32WindowClass' -and
            $_.Current.Name -eq 'Fluff-History' })
}
$process = [Diagnostics.Process]::GetProcessById($UiProcessId)
if ($process.ProcessName -ne 'FluffHistory.Desktop' -or $process.SessionId -ne (Get-Process -Id $PID).SessionId) {
    throw 'Specify the management UI in this interactive session.'
}
$null = $process.Handle # Retain identity/exit status even if Windows terminates it.
$windows = @(ManagementWindows)
if ($windows.Count -ne 1 -or $windows[0].Current.ProcessId -ne $UiProcessId) { throw 'Expected one existing management window.' }
$package = Get-AppxPackage -Name $PackageIdentity
if (!$package) { throw 'The product identity is not registered.' }
$agentBefore = @(Get-Process FluffHistory.Agent -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id | Sort-Object)
$cases = New-Object 'Collections.Generic.List[object]'
$failure = $null
try {
    for ($i=1; $i -le $Iterations; $i++) {
        $command = $null
        try {
            $command = [InstalledShellLifetimeProbe]::Activate()
            Start-Sleep -Seconds 2
            if ($process.HasExited) { throw "UI exited during surrogate activation: $($process.ExitCode)." }
        } finally { if ($null -ne $command) { [InstalledShellLifetimeProbe]::Release($command) } }
        Start-Sleep -Seconds 2
        $windows = @(ManagementWindows)
        $alive = !$process.HasExited
        $sameWindow = $windows.Count -eq 1 -and $windows[0].Current.ProcessId -eq $UiProcessId
        $cases.Add([ordered]@{iteration=$i;uiAlive=$alive;sameManagementWindow=$sameWindow})
        if (!$alive -or !$sameWindow) { throw 'Management UI did not survive native menu activation/release.' }
    }
    $agentAfter = @(Get-Process FluffHistory.Agent -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id | Sort-Object)
    if (($agentBefore -join ',') -ne ($agentAfter -join ',')) { throw 'The background agent changed during the probe.' }
} catch { $failure = $_.Exception.Message }
finally {
    [ordered]@{utc=[DateTime]::UtcNow.ToString('O');identityVersion=[string]$package.Version;
        uiProcessId=$UiProcessId;iterationsRequested=$Iterations;passed=($null -eq $failure);
        error=$failure;uiExited=$process.HasExited;exitCode=$(if ($process.HasExited) {$process.ExitCode} else {$null});
        agentBefore=$agentBefore;agentAfter=@(Get-Process FluffHistory.Agent -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id | Sort-Object);
        cases=$cases.ToArray()} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
    $process.Dispose()
}
if ($failure) { throw $failure }
Write-Output "Installed Shell lifetime regression passed ($Iterations activations/releases, same UI and agent)."
