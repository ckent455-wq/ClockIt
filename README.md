# ClockIt!

ClockIt! is a native Windows desktop time clock for a shared workstation or tablet. Employees enter their PIN to open their clock-in screen. After they clock in or out, the app returns to the PIN pad.

## Run ClockIt!

Download the Windows executable from [Releases](../../releases) and open it. On first launch, the person setting up the workstation creates a private four-digit supervisor PIN. Select the gear in the bottom-right corner of the PIN screen and enter the supervisor PIN to add employees and set their PINs.

The supervisor panel lets you review timesheets, add or correct shifts, and delete mistaken entries. **Weekly export** previews completed shifts and employee totals, flags open shifts, and lets you save a CSV. A supervisor can also edit a shift directly from the preview.

To email the report, open **Payroll email** and configure the Gmail sender, Google app password, and payroll recipient. You can test delivery with an attachment-free message before sending the real report.

The large punch buttons and built-in keyboards support tablet use. After ten seconds of inactivity, the live clock and fishbowl appear. The bowl shows one fish per clocked-in employee and is empty when everyone is clocked out. Tap the screen to return to the PIN pad.

## Update ClockIt!

ClockIt checks for updates when it starts. If an update is available, select **Install and restart** and enter the supervisor PIN to approve it. You can also select **Check for updates** in the supervisor panel. ClockIt downloads the executable from the latest GitHub release, verifies the checksum, and restarts with the same local data.

## Build from source

On Windows, run the build script from PowerShell:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1
```

The build uses the Windows .NET Framework compiler and produces `ClockIt!.exe` in the repository directory.

ClockIt! stores local employee records and settings in `%LOCALAPPDATA%\WorkTimeClock` under the Windows account running the app. The source repository and release download do not include those records.
