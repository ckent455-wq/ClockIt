# ClockIt!

ClockIt! is a native Windows desktop time clock for a shared workstation or tablet. Employees select their name and use a PIN to clock in, take breaks, and clock out.

## Run ClockIt!

Download the Windows executable from [Releases](../../releases) and open it. On first launch, the person setting up the workstation creates a private four-digit supervisor PIN. Use the gear icon to add employees and set their PINs.

The supervisor panel lets you review timesheets, add or correct shifts and breaks, and delete mistaken entries. **Weekly export** previews completed shifts and employee totals, flags open shifts, and lets you save a CSV. A supervisor can also edit a shift directly from the preview.

To email the report, open **Payroll email** and configure the Gmail sender, Google app password, and payroll recipient. You can test delivery with an attachment-free message before sending the real report.

The large punch buttons and built-in keyboards support tablet use. After ten seconds of inactivity, the live clock and fishbowl appear. Tap the screen to return to the employee picker.

## Build from source

On Windows, run the build script from PowerShell:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1
```

The build uses the Windows .NET Framework compiler and produces `ClockIt!.exe` in the repository directory.

ClockIt! stores local employee records and settings in `%LOCALAPPDATA%\WorkTimeClock` under the Windows account running the app. The source repository and release download do not include those records.
