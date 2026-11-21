# ClockIt!

ClockIt! is a native Windows desktop time clock for a shared workstation or tablet. Employees enter their PIN to open their clock-in screen, then tap the green **Clock In** or red **Clock Out** button. After confirming the punch, the app shows the fishbowl: the employee's fish drops in when they clock in, or floats to the surface and disappears when they clock out. Tap the screen to return to the PIN pad.

## Run ClockIt!

Download the Windows executable from [Releases](../../releases) and open it. On first launch, the person setting up the workstation creates a private four-digit supervisor PIN. Enter that PIN on the main PIN pad to clock in or out as Supervisor. The **Supervisor menu** button on that screen opens timesheets and settings. The gear in the bottom-right corner of the PIN pad also opens the menu after PIN verification.

The supervisor panel lets you review timesheets, add or correct shifts, and delete mistaken entries. **Weekly export** previews completed shifts and employee totals, flags open shifts, and lets you save a PDF. A supervisor can also edit a shift directly from the preview.

To email the PDF report, open **Payroll email** and configure the Gmail sender, Google app password, and payroll recipient. You can test delivery with an attachment-free message before sending the real report.

The large punch buttons and built-in keyboards support tablet use. After ten seconds of inactivity, the live clock and fishbowl appear. The bowl shows one fish per clocked-in person and switches between Christmas, spring flowers, summer beach, and fall leaves and pumpkins based on the month. Tap the screen to return to the PIN pad.

## Update ClockIt!

ClockIt checks for updates when it starts. If an update is available, select **Install and restart** and enter the supervisor PIN to approve it. You can also select **Check for updates** in the supervisor panel. ClockIt downloads the executable from the latest GitHub release, verifies the checksum, and restarts with the same local data.

## Build from source

On Windows, run the build script from PowerShell:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1
```

The build uses the Windows .NET Framework compiler and produces `ClockIt!.exe` in the repository directory.

ClockIt! stores local employee records and settings in `%LOCALAPPDATA%\WorkTimeClock` under the Windows account running the app. The source repository and release download do not include those records.
