using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Serialization;

[assembly: AssemblyTitle("ClockIt!")]
[assembly: AssemblyProduct("ClockIt!")]
[assembly: AssemblyVersion("1.0.1.0")]
[assembly: AssemblyFileVersion("1.0.1.0")]

public class Employee
{
    public string Name { get; set; }
    public string PinCredential { get; set; }
    public bool Inactive { get; set; }
    public List<Shift> Shifts { get; set; }

    public Employee()
    {
        Shifts = new List<Shift>();
    }
}

public class Shift
{
    public DateTime Start { get; set; }
    public DateTime? End { get; set; }
    public List<Break> Breaks { get; set; }

    public Shift()
    {
        Breaks = new List<Break>();
    }

    public TimeSpan BreakTime(DateTime now)
    {
        return TimeSpan.FromTicks(Breaks.Sum(b => Elapsed(b.Start, b.End ?? now).Ticks));
    }

    public TimeSpan Worked(DateTime now)
    {
        TimeSpan result = Elapsed(Start, End ?? now) - BreakTime(now);
        return result < TimeSpan.Zero ? TimeSpan.Zero : result;
    }

    private static TimeSpan Elapsed(DateTime start, DateTime end)
    {
        return end < start ? TimeSpan.Zero : end - start;
    }
}

public class Break
{
    public DateTime Start { get; set; }
    public DateTime? End { get; set; }
}

public class PayrollEmailSettings
{
    public string Sender { get; set; }
    public string Recipient { get; set; }
    public string ProtectedPassword { get; set; }
}

internal sealed class TimeClock : Form, IMessageFilter
{
    private const string AppName = "ClockIt!";
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(10);
    private static readonly Color Background = Color.FromArgb(18, 23, 30);
    private static readonly Color Surface = Color.FromArgb(26, 34, 44);
    private static readonly Color Input = Color.FromArgb(36, 46, 58);
    private static readonly Color Border = Color.FromArgb(63, 78, 94);
    private static readonly Color Ink = Color.FromArgb(237, 243, 249);
    private static readonly Color Muted = Color.FromArgb(169, 184, 200);
    private static readonly Color Accent = Color.FromArgb(100, 222, 200);
    private static readonly Color Highlight = Color.FromArgb(30, 82, 84);
    private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(List<Employee>));

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);

    private readonly string dataPath;
    private readonly string supervisorPath;
    private readonly string payrollEmailPath;
    private readonly Stopwatch inactivity = Stopwatch.StartNew();
    private ClockItUpdater.Update startupUpdate;
    private bool manualUpdateChecked;
    private readonly FishbowlClock idleClock = new FishbowlClock();
    private readonly TableLayoutPanel employeeView;
    private readonly Button managePin = new Button { Text = "Set employee PIN" };
    private int failedAttempts;
    private DateTime retryAfter;
    private readonly ComboBox people = new ComboBox();
    private readonly List<int> activeEmployeeIndexes = new List<int>();
    private readonly Label status = new Label();
    private readonly Label elapsed = new Label();
    private readonly Label details = new Label();
    private readonly Button punch = new Button();
    private readonly Button clockOut = new Button();
    private readonly ListView history = new ListView();
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    private List<Employee> employees;

    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        bool acquired;
        using (var mutex = new Mutex(true, @"Global\WorkTimeClock.Desktop", out acquired))
        {
            if (!acquired)
            {
                MessageBox.Show(AppName + " is already open on this computer.", AppName);
                return;
            }

            try
            {
                Application.Run(new TimeClock());
            }
            catch (Exception error)
            {
                MessageBox.Show(error.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private TimeClock() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkTimeClock"))
    {
    }

    private TimeClock(string dataDirectory)
    {
        dataPath = Path.Combine(dataDirectory, "time-records.xml");
        supervisorPath = Path.Combine(dataDirectory, "supervisor-pin.txt");
        payrollEmailPath = Path.Combine(dataDirectory, "payroll-email.xml");
        employees = File.Exists(dataPath) ? ReadRecords() : new List<Employee>();
        Text = AppName;
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(780, 620);
        MinimumSize = new Size(700, 620);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 11);
        BackColor = Background;
        HandleCreated += (sender, args) => UseDarkCaption(this);
        ForeColor = Ink;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28), ColumnCount = 1, RowCount = 5 };
        employeeView = layout;
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (int height in new[] { 63, 54, 265, 41 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(layout);

        var heading = new Panel { Dock = DockStyle.Fill };
        var header = new Label { Text = AppName, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 25, FontStyle.Bold) };
        var settings = new Button { Text = "\u2699", AccessibleName = "Supervisor settings", Dock = DockStyle.Right };
        StyleButton(settings, false);
        settings.Width = 52;
        settings.Font = new Font("Segoe UI Symbol", 20);
        settings.Click += (sender, args) => OpenSupervisor();
        var settingsHost = new Panel { Dock = DockStyle.Right, Width = 52, Padding = new Padding(0, 2, 0, 9) };
        settingsHost.Controls.Add(settings);
        heading.Controls.Add(header);
        heading.Controls.Add(settingsHost);
        layout.Controls.Add(heading, 0, 0);

        var selection = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        selection.Controls.Add(new Label { Text = "Employee", AutoSize = true, Margin = new Padding(0, 8, 10, 0) });
        ConfigureEmployeePicker(people);
        selection.Controls.Add(people);
        layout.Controls.Add(selection, 0, 1);

        var card = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20, 15, 20, 12),
            BackColor = Surface, ColumnCount = 1, RowCount = 4 };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (int height in new[] { 30, 75, 42, 89 })
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.Controls.Add(card, 0, 2);

        status.Dock = elapsed.Dock = details.Dock = DockStyle.Fill;
        status.ForeColor = Accent;
        status.Font = new Font("Segoe UI", 12, FontStyle.Bold);
        elapsed.Font = new Font("Segoe UI", 43, FontStyle.Bold);
        details.ForeColor = Muted;
        card.Controls.Add(status, 0, 0);
        card.Controls.Add(elapsed, 0, 1);
        card.Controls.Add(details, 0, 2);

        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        StyleButton(punch, true);
        StyleButton(clockOut, false);
        punch.Dock = clockOut.Dock = DockStyle.Fill;
        punch.Font = clockOut.Font = new Font("Segoe UI", 17, FontStyle.Bold);
        punch.Margin = new Padding(0, 2, 8, 7);
        clockOut.Margin = new Padding(8, 2, 0, 7);
        punch.Click += (sender, args) => PromptForPunch(false);
        clockOut.Click += (sender, args) => PromptForPunch(true);
        clockOut.Text = "Clock out";
        actions.Controls.Add(punch, 0, 0);
        actions.Controls.Add(clockOut, 1, 0);
        card.Controls.Add(actions, 0, 3);

        layout.Controls.Add(new Label { Text = "Shift history", Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft,
            Padding = new Padding(0, 0, 0, 6), Font = new Font("Segoe UI", 13, FontStyle.Bold) }, 0, 3);
        history.Dock = DockStyle.Fill;
        history.View = View.Details;
        history.BackColor = Surface;
        history.ForeColor = Ink;
        history.OwnerDraw = true;
        history.DrawColumnHeader += (sender, args) =>
        {
            using (var brush = new SolidBrush(Input)) args.Graphics.FillRectangle(brush, args.Bounds);
            DrawHistoryText(args.Graphics, args.Header.Text, args.Bounds, args.Font);
        };
        history.DrawItem += (sender, args) => { };
        history.DrawSubItem += (sender, args) =>
        {
            using (var brush = new SolidBrush(args.Item.Selected ? Highlight : Surface))
                args.Graphics.FillRectangle(brush, args.Bounds);
            DrawHistoryText(args.Graphics, args.SubItem.Text, args.Bounds, history.Font);
        };
        history.FullRowSelect = true;
        history.AccessibleName = "Shift history";
        history.Resize += (sender, args) => ResizeHistory();
        history.Columns.Add("Clock in");
        history.Columns.Add("Clock out");
        history.Columns.Add("Break time");
        history.Columns.Add("Time worked");
        layout.Controls.Add(history, 0, 4);

        ConfigureIdleClock();
        people.SelectedIndexChanged += (sender, args) => RefreshEmployee();
        PopulateEmployees(-1);
        timer.Interval = 1000;
        timer.Tick += (sender, args) => UpdateIdleScreen(inactivity.Elapsed);
        timer.Start();
        Activated += (sender, args) => inactivity.Restart();
        Shown += async (sender, args) =>
        {
            Application.AddMessageFilter(this);
            RunFirstTimeSetup();
            if (!IsDisposed && !manualUpdateChecked) await CheckForStartupUpdateAsync();
        };
        FormClosed += (sender, args) =>
        {
            Application.RemoveMessageFilter(this);
            timer.Dispose();
        };
    }

    private void ConfigureIdleClock()
    {
        idleClock.Dock = DockStyle.Fill;
        idleClock.FlatStyle = FlatStyle.Flat;
        idleClock.FlatAppearance.BorderSize = 0;
        idleClock.FlatAppearance.MouseOverBackColor = Background;
        idleClock.FlatAppearance.MouseDownBackColor = Background;
        idleClock.BackColor = Background;
        idleClock.ForeColor = Accent;
        idleClock.Font = new Font("Segoe UI", 84, FontStyle.Bold, GraphicsUnit.Pixel);
        idleClock.AccessibleDescription = "Tap or press a key to return to the time clock.";
        idleClock.Visible = false;
        idleClock.Click += (sender, args) => DismissIdleClock();
        Controls.Add(idleClock);
        KeyPreview = true;
        KeyDown += (sender, args) =>
        {
            if (!idleClock.Visible) return;
            DismissIdleClock();
            args.SuppressKeyPress = true;
        };
    }

    public bool PreFilterMessage(ref Message message)
    {
        switch (message.Msg)
        {
            case 0x0100: // WM_KEYDOWN
            case 0x0104: // WM_SYSKEYDOWN
            case 0x0201: // WM_LBUTTONDOWN
            case 0x0204: // WM_RBUTTONDOWN
            case 0x020A: // WM_MOUSEWHEEL
            case 0x0246: // WM_POINTERDOWN
                inactivity.Restart();
                break;
        }
        return false;
    }

    private void UpdateIdleScreen(TimeSpan inactiveFor)
    {
        if (idleClock.Visible)
        {
            idleClock.Text = DateTime.Now.ToString("h:mm tt", CultureInfo.InvariantCulture);
            return;
        }

        if (startupUpdate != null && ActiveForm == this && people.SelectedIndex < 0 &&
            !people.DroppedDown && CanShowIdleScreen())
        {
            ClockItUpdater.Update update = startupUpdate;
            startupUpdate = null;
            if (ShowUpdater(this, update)) Close();
            return;
        }

        if (inactiveFor >= IdleDelay && CanShowIdleScreen())
        {
            people.SelectedIndex = -1;
            idleClock.Text = DateTime.Now.ToString("h:mm tt", CultureInfo.InvariantCulture);
            employeeView.Visible = false;
            idleClock.Visible = true;
            idleClock.BringToFront();
            idleClock.Focus();
            return;
        }

        RefreshClock();
    }

    private bool CanShowIdleScreen()
    {
        return Visible && Enabled && WindowState != FormWindowState.Minimized &&
            !Application.OpenForms.Cast<Form>().Any(form => form != this && form.Visible);
    }

    private void DismissIdleClock()
    {
        inactivity.Restart();
        idleClock.Visible = false;
        employeeView.Visible = true;
        people.Focus();
    }

    private int SelectedEmployeeIndex
    {
        get { return people.SelectedIndex < 0 ? -1 : activeEmployeeIndexes[people.SelectedIndex]; }
    }

    private Employee SelectedEmployee
    {
        get { return SelectedEmployeeIndex < 0 ? null : employees[SelectedEmployeeIndex]; }
    }

    private Shift ActiveShift
    {
        get { return SelectedEmployee == null ? null : SelectedEmployee.Shifts.LastOrDefault(s => !s.End.HasValue); }
    }

    private static void StyleButton(Button button, bool primary)
    {
        button.Size = new Size(154, 43);
        button.Margin = new Padding(0, 0, 10, 0);
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = primary ? Accent : Input;
        button.ForeColor = primary ? Background : Ink;
        button.FlatAppearance.BorderColor = primary ? Accent : Border;
        button.UseVisualStyleBackColor = false;
        button.Font = new Font("Segoe UI", 11, FontStyle.Bold);
    }

    private void ResizeHistory()
    {
        if (history.Columns.Count != 4) return;
        int width = Math.Max(0, history.ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
        history.Columns[0].Width = history.Columns[1].Width = width * 3 / 10;
        history.Columns[2].Width = history.Columns[3].Width = width / 5;
    }

    private void PopulateEmployees(int index)
    {
        people.Items.Clear();
        activeEmployeeIndexes.Clear();
        for (int employeeIndex = 0; employeeIndex < employees.Count; employeeIndex++)
        {
            if (employees[employeeIndex].Inactive) continue;
            activeEmployeeIndexes.Add(employeeIndex);
            people.Items.Add(employees[employeeIndex].Name);
        }
        people.SelectedIndex = activeEmployeeIndexes.IndexOf(index);
        RefreshEmployee();
    }

    private static void UseDarkCaption(Form window)
    {
        int enabled = 1;
        DwmSetWindowAttribute(window.Handle, 20, ref enabled, sizeof(int));
    }

    private static void DrawHistoryText(Graphics graphics, string value, Rectangle bounds, Font font)
    {
        bounds.Inflate(-5, 0);
        TextRenderer.DrawText(graphics, value, font, bounds, Ink,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private const string EditTimeFormat = "yyyy-MM-dd HH:mm:ss";

    private static void ConfigureEmployeePicker(ComboBox picker)
    {
        picker.DropDownStyle = ComboBoxStyle.DropDownList;
        picker.AccessibleName = "Employee";
        picker.Width = 225;
        picker.FlatStyle = FlatStyle.Flat;
        picker.BackColor = Input;
        picker.ForeColor = Ink;
    }

    private Form SupervisorDialog(string title, int width, int height)
    {
        var dialog = new Form
        {
            Text = title, ClientSize = new Size(width, height), StartPosition = FormStartPosition.CenterParent,
            Font = Font, BackColor = Background, ForeColor = Ink, AutoScaleMode = AutoScaleMode.Dpi,
            ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false, MaximizeBox = false
        };
        dialog.HandleCreated += (sender, args) => UseDarkCaption(dialog);
        return dialog;
    }

    private static TableLayoutPanel DialogLayout(Form dialog, params int[] rowHeights)
    {
        var layout = new TableLayoutPanel
        {
            ColumnCount = 1, RowCount = rowHeights.Length, Padding = new Padding(18), Dock = DockStyle.Fill
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (int height in rowHeights)
            layout.RowStyles.Add(height == 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, height));
        dialog.Controls.Add(layout);
        return layout;
    }

    private static DataGridView TimesheetGrid(params string[] columns)
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, BackgroundColor = Surface, GridColor = Border, BorderStyle = BorderStyle.None,
            EnableHeadersVisualStyles = false, RowHeadersVisible = false, MultiSelect = false, ReadOnly = true,
            AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, ColumnHeadersHeight = 34,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect
        };
        grid.RowTemplate.Height = 31;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Input;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Ink;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Input;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Ink;
        grid.DefaultCellStyle.BackColor = Surface;
        grid.DefaultCellStyle.ForeColor = Ink;
        grid.DefaultCellStyle.SelectionBackColor = Highlight;
        grid.DefaultCellStyle.SelectionForeColor = Ink;
        foreach (string column in columns)
        {
            int index = grid.Columns.Add(column, column);
            grid.Columns[index].SortMode = DataGridViewColumnSortMode.NotSortable;
        }
        return grid;
    }

    private static Button AddDialogButton(FlowLayoutPanel parent, string label, int width, bool primary, Action action)
    {
        var button = new Button { Text = label };
        StyleButton(button, primary);
        button.Size = new Size(width, 35);
        button.Click += (sender, args) => action();
        parent.Controls.Add(button);
        return button;
    }

    private void RunFirstTimeSetup()
    {
        if (File.Exists(supervisorPath)) return;

        Enabled = false;
        bool configured;
        try
        {
            configured = PromptForSupervisorPin("Set up " + AppName,
                "The person setting up this app is the supervisor. Create a private four-digit PIN.");
        }
        finally
        {
            Enabled = true;
        }

        if (configured) ShowSupervisorPanel();
        else Close();
    }

    private void OpenSupervisor()
    {
        if (AuthorizeSupervisor()) ShowSupervisorPanel();
    }

    private void ShowSupervisorPanel()
    {
        int previousEmployee = SelectedEmployeeIndex;
        bool restartingForUpdate = false;
        using (var dialog = SupervisorDialog("Supervisor timesheets", 760, 594))
        {
            var layout = DialogLayout(dialog, 48, 0, 45, 44, 30);
            var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            var picker = new ComboBox();
            ConfigureEmployeePicker(picker);
            tools.Controls.Add(new Label { Text = "Employee", AutoSize = true, Margin = new Padding(0, 7, 6, 0) });
            tools.Controls.Add(picker);
            var grid = TimesheetGrid("Clock in", "Clock out", "Break time", "Time worked");
            grid.AccessibleName = "Employee timesheet";
            grid.Columns[0].FillWeight = grid.Columns[1].FillWeight = 130;
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 7, 0, 0), WrapContents = false };

            Action refresh = () =>
            {
                grid.Rows.Clear();
                if (picker.SelectedIndex < 0) return;
                var shifts = employees[picker.SelectedIndex].Shifts;
                DateTime now = DateTime.UtcNow;
                foreach (int index in Enumerable.Range(0, shifts.Count).OrderByDescending(i => shifts[i].Start))
                {
                    Shift shift = shifts[index];
                    int row = grid.Rows.Add(LocalTime(shift.Start), shift.End.HasValue ? LocalTime(shift.End.Value) : "In progress",
                        Duration(shift.BreakTime(now)), Duration(shift.Worked(now)));
                    grid.Rows[row].Tag = index;
                }
            };

            AddDialogButton(tools, "Add employee", 130, false, () =>
            {
                if (!AddEmployee()) return;
                picker.Items.Add(employees[employees.Count - 1].Name);
                picker.SelectedIndex = employees.Count - 1;
            });
            Button setPin = AddDialogButton(tools, "Set employee PIN", 155, false, () =>
            {
                if (picker.SelectedIndex >= 0) ManageEmployeePin(picker.SelectedIndex);
            });
            Action edit = () =>
            {
                if (picker.SelectedIndex < 0 || grid.CurrentRow == null) return;
                if (ShowShiftEditor(dialog, picker.SelectedIndex, (int)grid.CurrentRow.Tag)) refresh();
            };
            Button addShift = AddDialogButton(footer, "Add shift", 115, true, () =>
            {
                if (picker.SelectedIndex >= 0 && ShowShiftEditor(dialog, picker.SelectedIndex, null)) refresh();
            });
            Button editButton = AddDialogButton(footer, "Edit shift", 115, false, edit);
            Button delete = AddDialogButton(footer, "Delete shift", 125, false, () =>
            {
                if (picker.SelectedIndex < 0 || grid.SelectedRows.Count != 1) return;
                if (DeleteShift(dialog, picker.SelectedIndex, (int)grid.SelectedRows[0].Tag)) refresh();
            });
            Button changeStatus = null;
            changeStatus = AddDialogButton(footer, "Deactivate employee", 155, false, () =>
            {
                int index = picker.SelectedIndex;
                if (index < 0 || !ChangeEmployeeStatus(dialog, index)) return;
                picker.Items[index] = SupervisorEmployeeName(employees[index]);
                picker.SelectedIndex = index;
                changeStatus.Text = EmployeeStatusAction(employees[index].Inactive);
            });
            AddDialogButton(footer, "Weekly export", 145, true, () =>
            {
                if (ExportWeeklyTimesheets(dialog)) refresh();
            });
            var settings = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            AddDialogButton(settings, "Change supervisor PIN", 186, false, ChangeSupervisorPin);
            AddDialogButton(settings, "Payroll email", 128, false, () => ConfigurePayrollEmail(dialog));
            AddDialogButton(settings, "Check for updates", 164, false, () =>
            {
                manualUpdateChecked = true;
                startupUpdate = null;
                restartingForUpdate = ShowUpdater(dialog);
                if (restartingForUpdate) dialog.DialogResult = DialogResult.OK;
            });
            Button close = AddDialogButton(settings, "Close", 95, false, dialog.Close);
            dialog.CancelButton = close;
            grid.SelectionChanged += (sender, args) => editButton.Enabled = delete.Enabled = grid.SelectedRows.Count == 1;
            grid.CellDoubleClick += (sender, args) => { if (args.RowIndex >= 0) edit(); };
            picker.SelectedIndexChanged += (sender, args) =>
            {
                int index = picker.SelectedIndex;
                addShift.Enabled = setPin.Enabled = changeStatus.Enabled = index >= 0;
                changeStatus.Text = EmployeeStatusAction(index >= 0 && employees[index].Inactive);
                refresh();
            };
            foreach (Employee employee in employees) picker.Items.Add(SupervisorEmployeeName(employee));
            editButton.Enabled = delete.Enabled = addShift.Enabled = setPin.Enabled = changeStatus.Enabled = false;
            if (picker.Items.Count > 0) picker.SelectedIndex = Math.Max(0, previousEmployee);
            layout.Controls.Add(tools, 0, 0);
            layout.Controls.Add(grid, 0, 1);
            layout.Controls.Add(footer, 0, 2);
            layout.Controls.Add(settings, 0, 3);
            dialog.ShowDialog(this);
        }
        if (restartingForUpdate) Close();
        else PopulateEmployees(previousEmployee);
    }

    private static string SupervisorEmployeeName(Employee employee)
    {
        return employee.Name + (employee.Inactive ? " (Inactive)" : "");
    }

    private static string EmployeeStatusAction(bool inactive)
    {
        return inactive ? "Reactivate employee" : "Deactivate employee";
    }

    private bool ChangeEmployeeStatus(Form owner, int index)
    {
        Employee employee = employees[index];
        bool deactivate = !employee.Inactive;
        if (deactivate && employee.Shifts.Any(shift => !shift.End.HasValue))
        {
            MessageBox.Show(owner, "Close the employee's open shift with Edit shift before deactivating them.",
                "Open shift", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        string title = EmployeeStatusAction(employee.Inactive);
        string message = deactivate
            ? "Deactivate " + employee.Name + "? They will be removed from the punch screen. Their shifts stay available for payroll."
            : "Reactivate " + employee.Name + "? They will be able to clock in again.";
        if (MessageBox.Show(owner, message, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return false;

        return SaveChange(records => records[index].Inactive = deactivate);
    }

    private static string BuildDisplay()
    {
        Assembly assembly = typeof(TimeClock).Assembly;
        string version = "v" + assembly.GetName().Version.ToString(3);
        var stamp = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(item => item.Key == "BuildUtc");
        return version + "   |   " + (stamp == null ? "Build date unavailable" : "Built " + stamp.Value);
    }

    private async Task CheckForStartupUpdateAsync()
    {
        try
        {
            ClockItUpdater.Update update = await ClockItUpdater.CheckAsync(
                Application.ExecutablePath, typeof(TimeClock).Assembly.GetName().Version);
            if (!IsDisposed && !manualUpdateChecked) startupUpdate = update;
        }
        catch (Exception)
        {
            // An unavailable update service should never prevent employees from clocking in.
        }
    }

    private static string DescribeUpdate(ClockItUpdater.Update update)
    {
        return update == null ? "You're up to date." :
            "GitHub has an updated " + update.VersionName + " build. ClockIt will restart after installation.";
    }

    private bool ShowUpdater(Form owner, ClockItUpdater.Update availableAtStartup = null)
    {
        using (var dialog = SupervisorDialog("ClockIt! updates", 510, 186))
        {
            var layout = DialogLayout(dialog, 0, 44);
            var message = new Label
            {
                Dock = DockStyle.Fill,
                Text = availableAtStartup == null ? "Checking GitHub for the latest ClockIt build…" : DescribeUpdate(availableAtStartup)
            };
            layout.Controls.Add(message, 0, 0);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill };
            ClockItUpdater.Update update = availableAtStartup;
            bool busy = update == null;
            Button install = null;
            Button close = null;
            string path = Application.ExecutablePath;
            install = AddDialogButton(actions, "Install and restart", 160, true, async () =>
            {
                if (busy || update == null) return;
                busy = true;
                install.Enabled = close.Enabled = false;
                try
                {
                    if (availableAtStartup != null && !AuthorizeSupervisor()) return;

                    message.Text = "Downloading and verifying " + update.VersionName + "…";
                    await ClockItUpdater.InstallAsync(update, path);
                    busy = false;
                    dialog.DialogResult = DialogResult.OK;
                }
                catch (Exception error)
                {
                    message.Text = "Could not prepare the update. " + error.Message;
                }
                finally
                {
                    busy = false;
                    install.Enabled = close.Enabled = true;
                }
            });
            close = AddDialogButton(actions, "Close", 95, false, dialog.Close);
            install.Enabled = close.Enabled = !busy;
            dialog.CancelButton = close;
            dialog.FormClosing += (sender, args) => { if (busy) args.Cancel = true; };
            dialog.Shown += async (sender, args) =>
            {
                if (availableAtStartup != null) return;
                try
                {
                    update = await ClockItUpdater.CheckAsync(path, typeof(TimeClock).Assembly.GetName().Version);
                    message.Text = DescribeUpdate(update);
                    install.Enabled = update != null;
                }
                catch (Exception error)
                {
                    message.Text = "Could not check for updates. " + error.Message;
                }
                finally
                {
                    busy = false;
                    close.Enabled = true;
                }
            };
            layout.Controls.Add(actions, 0, 1);
            return dialog.ShowDialog(owner) == DialogResult.OK;
        }
    }

    private bool ExportWeeklyTimesheets(Form owner)
    {
        bool shiftsChanged = false;
        using (var dialog = SupervisorDialog("Weekly payroll report", 925, 563))
        using (var totalFont = new Font(Font, FontStyle.Bold))
        {
            var layout = DialogLayout(dialog, 43, 31, 0, 31, 40, 43);
            var period = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            period.Controls.Add(new Label { Text = "Week starting", AutoSize = true, Margin = new Padding(0, 6, 10, 0) });
            DateTime today = DateTime.Today;
            var weekStart = new DateTimePicker
            {
                AccessibleName = "Week starting", Format = DateTimePickerFormat.Custom,
                CustomFormat = "dddd, MMM d, yyyy", Width = 244, MaxDate = today,
                Value = today.AddDays(-((int)today.DayOfWeek + 6) % 7)
            };
            period.Controls.Add(weekStart);
            var dates = new Label { AutoSize = true, ForeColor = Muted, Margin = new Padding(10, 6, 0, 0) };
            period.Controls.Add(dates);
            layout.Controls.Add(period, 0, 0);

            PayrollEmailSettings email = ReadPayrollEmail();
            layout.Controls.Add(new Label
            {
                Dock = DockStyle.Fill, AutoEllipsis = true,
                Text = email == null ? "Set up Payroll email in the supervisor panel." : "To: " + email.Recipient
            }, 0, 1);

            var preview = TimesheetGrid("Row type", "Employee", "Start (local)", "End (local)", "Breaks", "Worked", "Worked hours");
            preview.AccessibleName = "Weekly payroll preview";
            preview.Columns[1].FillWeight = 117;
            preview.Columns[2].FillWeight = preview.Columns[3].FillWeight = 143;
            preview.Columns[4].FillWeight = preview.Columns[5].FillWeight = 62;
            preview.Columns[6].FillWeight = 101;
            layout.Controls.Add(preview, 0, 2);
            var reportNotice = new Label { Dock = DockStyle.Fill, ForeColor = Muted };
            layout.Controls.Add(reportNotice, 0, 3);
            var feedback = new Label { Dock = DockStyle.Fill, ForeColor = Ink };
            layout.Controls.Add(feedback, 0, 4);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill };
            WeeklyPayrollReport report = null;
            Action refresh = null;
            bool sending = false;
            Func<PayrollRow> selectedShift = () =>
            {
                if (preview.SelectedRows.Count != 1) return null;
                var row = preview.SelectedRows[0].Tag as PayrollRow;
                return row != null && !row.IsTotal ? row : null;
            };
            Action editShift = () =>
            {
                PayrollRow row = selectedShift();
                if (sending || row == null) return;
                int employeeIndex = employees.IndexOf(row.Employee);
                int shiftIndex = row.Employee.Shifts.IndexOf(row.SourceShift);
                if (employeeIndex < 0 || shiftIndex < 0 || !ShowShiftEditor(dialog, employeeIndex, shiftIndex)) return;

                shiftsChanged = true;
                Shift updated = employees[employeeIndex].Shifts[shiftIndex];
                refresh();
                foreach (DataGridViewRow previewRow in preview.Rows)
                {
                    if (((PayrollRow)previewRow.Tag).SourceShift != updated) continue;
                    preview.CurrentCell = previewRow.Cells[0];
                    previewRow.Selected = true;
                    break;
                }
                feedback.Text = "Shift saved. Payroll totals updated.";
            };
            Button edit = AddDialogButton(actions, "Edit shift", 115, false, editShift);
            Button saveCsv = AddDialogButton(actions, "Save CSV", 110, false, () =>
            {
                if (report.Rows.Count == 0) return;
                using (var save = new SaveFileDialog
                {
                    Title = "Save weekly timesheet", Filter = "CSV files (*.csv)|*.csv",
                    DefaultExt = "csv", AddExtension = true, OverwritePrompt = true,
                    FileName = PayrollFileName(report.FirstDay)
                })
                {
                    if (save.ShowDialog(dialog) != DialogResult.OK) return;
                    if (!string.Equals(Path.GetExtension(save.FileName), ".csv", StringComparison.OrdinalIgnoreCase))
                    {
                        feedback.Text = "Choose a filename ending in .csv.";
                        return;
                    }

                    try
                    {
                        string csv = report.ToCsv();
                        WriteAtomically(save.FileName, stream =>
                        {
                            using (var writer = new StreamWriter(stream, new UTF8Encoding(true), 1024, true))
                                writer.Write(csv);
                        });
                        feedback.Text = "CSV saved.";
                    }
                    catch (Exception error)
                    {
                        feedback.Text = "Could not save the CSV. " + error.Message;
                    }
                }
            });
            Button send = null;
            Button close = null;
            send = AddDialogButton(actions, "Send to payroll", 151, true, async () =>
            {
                if (sending || email == null || report.Rows.Count == 0) return;

                sending = true;
                weekStart.Enabled = edit.Enabled = saveCsv.Enabled = send.Enabled = close.Enabled = false;
                feedback.Text = "Sending through Gmail…";
                try
                {
                    using (var message = CreatePayrollMessage(email, report.FirstDay, report.ToCsv(), report.OpenShifts))
                        await Task.Run(() => SendGmail(email, message));
                    feedback.Text = "Gmail accepted the report for delivery.";
                }
                catch (Exception error)
                {
                    feedback.Text = GmailSendError(error);
                }
                finally
                {
                    sending = false;
                    weekStart.Enabled = saveCsv.Enabled = send.Enabled = close.Enabled = true;
                    edit.Enabled = selectedShift() != null;
                }
            });
            close = AddDialogButton(actions, "Close", 95, false, dialog.Close);
            refresh = () =>
            {
                report = BuildWeeklyReport(employees, weekStart.Value);
                dates.Text = "Through " + report.FirstDay.AddDays(6).ToString("dddd, MMM d, yyyy");
                preview.Rows.Clear();
                foreach (PayrollRow row in report.Rows)
                {
                    int index = preview.Rows.Add(row.Columns());
                    preview.Rows[index].Tag = row;
                    if (row.IsTotal)
                    {
                        preview.Rows[index].DefaultCellStyle.Font = totalFont;
                        preview.Rows[index].DefaultCellStyle.BackColor = Input;
                    }
                }
                preview.ClearSelection();
                bool hasRows = report.Rows.Count > 0;
                saveCsv.Enabled = hasRows;
                send.Enabled = hasRows && email != null;
                reportNotice.Text = (hasRows ? "Completed shifts and employee totals." : "No completed shifts in this week.") +
                    OpenShiftNote(report.OpenShifts);
                feedback.Text = "";
            };
            preview.SelectionChanged += (sender, args) => edit.Enabled = !sending && selectedShift() != null;
            preview.CellDoubleClick += (sender, args) => { if (args.RowIndex >= 0) editShift(); };
            edit.Enabled = false;
            weekStart.ValueChanged += (sender, args) => refresh();
            refresh();
            dialog.AcceptButton = saveCsv;
            dialog.CancelButton = close;
            dialog.FormClosing += (sender, args) => { if (sending) args.Cancel = true; };
            layout.Controls.Add(actions, 0, 5);
            dialog.ShowDialog(owner);
        }
        return shiftsChanged;
    }

    private void ConfigurePayrollEmail(Form owner)
    {
        PayrollEmailSettings previous = ReadPayrollEmail();
        using (var dialog = SupervisorDialog("Payroll email", 474, 390))
        {
            var layout = DialogLayout(dialog, 48, 0, 34, 55, 43);
            layout.Controls.Add(new Label
            {
                Text = "Use a Gmail account with 2-Step Verification and a Google app password.",
                Dock = DockStyle.Fill, ForeColor = Muted
            }, 0, 0);
            var fields = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            TextBox sender = AddField(fields, "Gmail sender address", false, 254);
            TextBox recipient = AddField(fields, "Payroll recipient address", false, 254);
            TextBox password = AddField(fields, "Google app password", true, 32);
            password.ShortcutsEnabled = true;
            if (previous != null)
            {
                sender.Text = previous.Sender;
                recipient.Text = previous.Recipient;
            }
            layout.Controls.Add(fields, 0, 1);
            var testToPayroll = new CheckBox
            {
                Dock = DockStyle.Fill, Text = "Send test to payroll recipient"
            };
            layout.Controls.Add(testToPayroll, 0, 2);
            var feedback = new Label
            {
                Dock = DockStyle.Fill, ForeColor = Muted,
                Text = previous == null ? "" : "Leave the app password blank to keep the saved password for this sender."
            };
            layout.Controls.Add(feedback, 0, 3);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill };
            Button save = AddDialogButton(actions, "Save", 100, true, () =>
            {
                try
                {
                    string from = SingleEmailAddress(sender.Text, "Gmail sender address");
                    string to = SingleEmailAddress(recipient.Text, "Payroll recipient address");
                    var settings = new PayrollEmailSettings
                    {
                        Sender = from, Recipient = to, ProtectedPassword = ResolveAppPassword(from, password.Text, previous)
                    };
                    WriteAtomically(payrollEmailPath, stream => new XmlSerializer(typeof(PayrollEmailSettings)).Serialize(stream, settings));
                    dialog.DialogResult = DialogResult.OK;
                }
                catch (FormatException error) { feedback.Text = error.Message; }
                catch (Exception) { feedback.Text = "Could not save the payroll email settings."; }
            });
            bool testing = false;
            Button test = null;
            Button cancel = null;
            test = AddDialogButton(actions, "Send test email", 150, false, async () =>
            {
                if (testing) return;
                testing = true;
                fields.Enabled = testToPayroll.Enabled = save.Enabled = test.Enabled = cancel.Enabled = false;
                try
                {
                    string from = SingleEmailAddress(sender.Text, "Gmail sender address");
                    var settings = new PayrollEmailSettings
                    {
                        Sender = from, ProtectedPassword = ResolveAppPassword(from, password.Text, previous)
                    };
                    string to = testToPayroll.Checked
                        ? SingleEmailAddress(recipient.Text, "Payroll recipient address") : from;
                    feedback.Text = "Sending test email through Gmail…";
                    using (var message = CreateTestMessage(from, to))
                        await Task.Run(() => SendGmail(settings, message));
                    feedback.Text = "Gmail accepted the test email for " + to + ". Check that inbox for the message.";
                }
                catch (FormatException error) { feedback.Text = error.Message; }
                catch (Exception error) { feedback.Text = GmailSendError(error); }
                finally
                {
                    testing = false;
                    fields.Enabled = testToPayroll.Enabled = save.Enabled = test.Enabled = cancel.Enabled = true;
                }
            });
            cancel = AddDialogButton(actions, "Cancel", 95, false, dialog.Close);
            dialog.AcceptButton = save;
            dialog.CancelButton = cancel;
            dialog.FormClosing += (source, args) => { if (testing) args.Cancel = true; };
            layout.Controls.Add(actions, 0, 4);
            new TouchKeyboard(dialog).Attach(false, sender, recipient, password);
            dialog.ShowDialog(owner);
        }
    }

    private static string ResolveAppPassword(string from, string entered, PayrollEmailSettings previous)
    {
        string password = string.Concat(entered.Where(character => !char.IsWhiteSpace(character)));
        if (password.Length == 0 && previous != null &&
            string.Equals(from, previous.Sender, StringComparison.OrdinalIgnoreCase))
            return previous.ProtectedPassword;

        if (password.Length != 16)
            throw new FormatException("Enter the 16-character Google app password.");

        byte[] secret = Encoding.UTF8.GetBytes(password);
        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(secret, null, DataProtectionScope.CurrentUser));
        }
        finally { Array.Clear(secret, 0, secret.Length); }
    }

    private PayrollEmailSettings ReadPayrollEmail()
    {
        if (!File.Exists(payrollEmailPath)) return null;
        try
        {
            using (var stream = File.OpenRead(payrollEmailPath))
            {
                var settings = (PayrollEmailSettings)new XmlSerializer(typeof(PayrollEmailSettings)).Deserialize(stream);
                SingleEmailAddress(settings.Sender, "Gmail sender address");
                SingleEmailAddress(settings.Recipient, "Payroll recipient address");
                return string.IsNullOrEmpty(settings.ProtectedPassword) ? null : settings;
            }
        }
        catch (IOException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (FormatException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string SingleEmailAddress(string input, string label)
    {
        string value = (input ?? "").Trim();
        try
        {
            var address = new MailAddress(value);
            if (address.Address == value && string.IsNullOrEmpty(address.DisplayName)) return address.Address;
        }
        catch (FormatException) { }
        catch (ArgumentException) { }
        throw new FormatException(label + " must be a single email address.");
    }

    private static string PayrollFileName(DateTime firstDay)
    {
        return "Timesheets-" + firstDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv";
    }

    private static MailMessage CreatePayrollMessage(PayrollEmailSettings email, DateTime firstDay, string csv, int openShifts)
    {
        string start = firstDay.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
        string end = firstDay.AddDays(6).ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
        var message = new MailMessage(email.Sender, email.Recipient)
        {
            Subject = "Weekly timesheets | " + start + " to " + end,
            Body = "Attached is the weekly payroll timesheet for " + start + " through " + end + "." + OpenShiftNote(openShifts)
        };
        var utf8 = new UTF8Encoding(true);
        byte[] bytes = utf8.GetPreamble().Concat(utf8.GetBytes(csv)).ToArray();
        message.Attachments.Add(new Attachment(new MemoryStream(bytes, false), PayrollFileName(firstDay), "text/csv"));
        return message;
    }

    private static MailMessage CreateTestMessage(string sender, string recipient)
    {
        return new MailMessage(sender, recipient)
        {
            Subject = AppName + " Gmail test",
            Body = "This is a test from " + AppName + " to confirm that email can reach this address. It contains no payroll data."
        };
    }

    private static string GmailSendError(Exception error)
    {
        if (error is CryptographicException)
            return "Windows could not unlock the app password. Enter a new one in Payroll email.";

        var smtp = error as SmtpException;
        if (smtp != null && smtp.StatusCode != SmtpStatusCode.GeneralFailure)
            return "Gmail rejected the send. Check the sender, app password, and recipient.";

        return "Gmail send could not be confirmed. Check Gmail Sent before retrying.";
    }

    private static void SendGmail(PayrollEmailSettings email, MailMessage message)
    {
        byte[] secret = ProtectedData.Unprotect(Convert.FromBase64String(email.ProtectedPassword), null, DataProtectionScope.CurrentUser);
        try
        {
            using (var smtp = new SmtpClient("smtp.gmail.com", 587)
            {
                DeliveryMethod = SmtpDeliveryMethod.Network, EnableSsl = true, UseDefaultCredentials = false, Timeout = 30000,
                Credentials = new NetworkCredential(email.Sender, Encoding.UTF8.GetString(secret))
            })
                smtp.Send(message);
        }
        finally { Array.Clear(secret, 0, secret.Length); }
    }

    private static string OpenShiftNote(int count)
    {
        if (count == 0) return "";
        return count == 1 ? " One open shift was excluded." : " " + count + " open shifts were excluded.";
    }

    private sealed class PayrollRow
    {
        public Employee Employee;
        public Shift SourceShift;
        public DateTime? Start;
        public DateTime? End;
        public TimeSpan BreakTime;
        public TimeSpan WorkedTime;

        public bool IsTotal { get { return SourceShift == null; } }

        public string[] Columns()
        {
            return new[]
            {
                IsTotal ? "Employee total" : "Shift", Employee.Name,
                Start.HasValue ? PayrollTime(Start.Value) : "", End.HasValue ? PayrollTime(End.Value) : "",
                Duration(BreakTime), Duration(WorkedTime), WorkedTime.TotalHours.ToString("0.0000", CultureInfo.InvariantCulture)
            };
        }
    }

    private sealed class WeeklyPayrollReport
    {
        public readonly DateTime FirstDay;
        public readonly List<PayrollRow> Rows = new List<PayrollRow>();
        public int OpenShifts;

        public WeeklyPayrollReport(DateTime firstDay)
        {
            FirstDay = firstDay;
        }

        public string ToCsv()
        {
            var csv = new StringBuilder();
            csv.AppendLine("Row type,Employee,Week start,Week end,Start in period (local),End in period (local),Break duration,Worked duration,Worked hours");
            string weekStart = FirstDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string weekEnd = FirstDay.AddDays(6).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            foreach (PayrollRow row in Rows)
            {
                string[] columns = row.Columns();
                csv.AppendLine(string.Join(",", columns[0], PayrollCsvName(columns[1]), weekStart, weekEnd,
                    columns[2], columns[3], columns[4], columns[5], columns[6]));
            }
            return csv.ToString();
        }
    }

    private static WeeklyPayrollReport BuildWeeklyReport(IEnumerable<Employee> employees, DateTime firstDay)
    {
        DateTime localStart = DateTime.SpecifyKind(firstDay.Date, DateTimeKind.Local);
        DateTime start = localStart.ToUniversalTime();
        DateTime end = localStart.AddDays(7).ToUniversalTime();
        DateTime now = DateTime.UtcNow;
        var report = new WeeklyPayrollReport(localStart);

        foreach (Employee employee in employees.OrderBy(person => person.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var total = new PayrollRow { Employee = employee };
            int firstRow = report.Rows.Count;
            foreach (Shift shift in employee.Shifts.OrderBy(item => item.Start))
            {
                if (!shift.End.HasValue)
                {
                    if (shift.Start < end && now > start) report.OpenShifts++;
                    continue;
                }

                if (shift.Start >= end || shift.End.Value <= start) continue;
                DateTime from = shift.Start < start ? start : shift.Start;
                DateTime to = shift.End.Value > end ? end : shift.End.Value;
                if (to < from) continue;
                TimeSpan gross = to - from;
                long breakTicks = shift.Breaks.Sum(interval =>
                    OverlapTicks(from, to, interval.Start, interval.End ?? shift.End.Value));
                TimeSpan unpaid = TimeSpan.FromTicks(Math.Min(gross.Ticks, breakTicks));
                TimeSpan worked = gross - unpaid;
                total.BreakTime += unpaid;
                total.WorkedTime += worked;
                report.Rows.Add(new PayrollRow
                {
                    Employee = employee, SourceShift = shift, Start = from, End = to, BreakTime = unpaid, WorkedTime = worked
                });
            }

            if (report.Rows.Count > firstRow) report.Rows.Add(total);
        }
        return report;
    }

    private static long OverlapTicks(DateTime from, DateTime to, DateTime intervalStart, DateTime intervalEnd)
    {
        DateTime overlapStart = from > intervalStart ? from : intervalStart;
        DateTime overlapEnd = to < intervalEnd ? to : intervalEnd;
        return Math.Max(0, (overlapEnd - overlapStart).Ticks);
    }

    private static string PayrollTime(DateTime utc)
    {
        return utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static string PayrollCsvName(string name)
    {
        string value = name ?? "";
        string trimmed = value.TrimStart();
        if (trimmed.Length > 0 && "=+-@".IndexOf(trimmed[0]) >= 0) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private bool DeleteShift(Form owner, int employeeIndex, int shiftIndex)
    {
        Employee employee = employees[employeeIndex];
        Shift shift = employee.Shifts[shiftIndex];
        using (var dialog = SupervisorDialog("Delete shift", 475, 208))
        {
            var layout = DialogLayout(dialog, 0, 45);
            string end = shift.End.HasValue ? LocalTime(shift.End.Value) : "In progress";
            layout.Controls.Add(new Label
            {
                Text = "Permanently delete this shift and its breaks?\r\n\r\n" +
                    employee.Name + "\r\nClock in: " + LocalTime(shift.Start) + "\r\nClock out: " + end,
                Dock = DockStyle.Fill, AutoEllipsis = true
            }, 0, 0);

            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            Button cancel = AddDialogButton(actions, "Cancel", 95, false, dialog.Close);
            Button delete = AddDialogButton(actions, "Delete shift", 125, false, () =>
            {
                if (SaveChange(records => records[employeeIndex].Shifts.RemoveAt(shiftIndex)))
                    dialog.DialogResult = DialogResult.OK;
            });
            delete.ForeColor = Color.LightSalmon;
            dialog.AcceptButton = cancel;
            dialog.CancelButton = cancel;
            layout.Controls.Add(actions, 0, 1);
            dialog.ActiveControl = cancel;
            return dialog.ShowDialog(owner) == DialogResult.OK;
        }
    }

    private bool ShowShiftEditor(Form owner, int employeeIndex, int? shiftIndex)
    {
        Employee employee = employees[employeeIndex];
        Shift original = shiftIndex.HasValue ? employee.Shifts[shiftIndex.Value] : null;
        using (var dialog = SupervisorDialog(original == null ? "Add shift" : "Edit shift", 748, 508))
        {
            var layout = DialogLayout(dialog, 37, 78, 31, 0, 48, 37, 43);
            layout.Controls.Add(new Label { Text = employee.Name, Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 15, FontStyle.Bold), AutoEllipsis = true }, 0, 0);

            var fields = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            var clockIn = new DateTimeSelection("Clock in", original == null ? (DateTime?)null : original.Start, DateTime.Now);
            var clockOutField = new DateTimeSelection("Clock out", original == null ? null : original.End,
                clockIn.LocalValue, () => clockIn.LocalValue);
            fields.Controls.Add(clockIn);
            fields.Controls.Add(clockOutField);
            layout.Controls.Add(fields, 0, 1);
            layout.Controls.Add(new Label { Text = "Breaks", Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft }, 0, 2);

            var breaks = TimesheetGrid("Break start", "Break end");
            breaks.AccessibleName = "Break times";
            foreach (Break interval in original == null ? Enumerable.Empty<Break>() : original.Breaks)
                DisplayBreak(breaks.Rows[breaks.Rows.Add()], interval);
            layout.Controls.Add(breaks, 0, 3);

            var breakActions = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 0) };
            AddDialogButton(breakActions, "Add break", 113, false, () =>
            {
                Break interval = ShowBreakEditor(dialog, null, clockIn.LocalValue);
                if (interval == null) return;
                DataGridViewRow row = breaks.Rows[breaks.Rows.Add()];
                DisplayBreak(row, interval);
                breaks.CurrentCell = row.Cells[0];
            });
            Action editBreak = () =>
            {
                if (breaks.SelectedRows.Count != 1) return;
                DataGridViewRow row = breaks.SelectedRows[0];
                Break interval = ShowBreakEditor(dialog, (Break)row.Tag, clockIn.LocalValue);
                if (interval != null) DisplayBreak(row, interval);
            };
            Button edit = AddDialogButton(breakActions, "Edit break", 113, false, editBreak);
            Button remove = AddDialogButton(breakActions, "Remove break", 136, false, () =>
            {
                if (breaks.SelectedRows.Count == 1) breaks.Rows.Remove(breaks.SelectedRows[0]);
            });
            edit.Enabled = remove.Enabled = breaks.SelectedRows.Count == 1;
            breaks.SelectionChanged += (sender, args) => edit.Enabled = remove.Enabled = breaks.SelectedRows.Count == 1;
            breaks.CellDoubleClick += (sender, args) => { if (args.RowIndex >= 0) editBreak(); };
            breakActions.Controls.Add(new Label
            {
                Text = "Local time", AutoSize = true, ForeColor = Muted,
                Margin = new Padding(3, 8, 0, 0)
            });
            layout.Controls.Add(breakActions, 0, 4);

            var feedback = new Label { ForeColor = Color.LightSalmon, Dock = DockStyle.Fill };
            layout.Controls.Add(feedback, 0, 5);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill };
            Button save = AddDialogButton(footer, original == null ? "Save shift" : "Save changes", 142, true, () =>
            {
                try
                {
                    var revised = new Shift { Start = clockIn.ReadUtc().Value, End = clockOutField.ReadUtc() };
                    foreach (DataGridViewRow row in breaks.Rows) revised.Breaks.Add((Break)row.Tag);
                    if (employee.Inactive && !revised.End.HasValue)
                        throw new FormatException("Enter a clock-out time for an inactive employee.");
                    ValidateShift(revised, employee.Shifts.Where((shift, index) => index != shiftIndex));
                    if (SaveChange(records =>
                    {
                        var shifts = records[employeeIndex].Shifts;
                        if (shiftIndex.HasValue) shifts[shiftIndex.Value] = revised;
                        else shifts.Add(revised);
                    }))
                        dialog.DialogResult = DialogResult.OK;
                }
                catch (FormatException error)
                {
                    feedback.Text = error.Message;
                }
            });
            Button cancel = AddDialogButton(footer, "Cancel", 95, false, dialog.Close);
            dialog.AcceptButton = save;
            dialog.CancelButton = cancel;
            layout.Controls.Add(footer, 0, 6);
            return dialog.ShowDialog(owner) == DialogResult.OK;
        }
    }

    private static void DisplayBreak(DataGridViewRow row, Break interval)
    {
        row.Tag = interval;
        row.Cells[0].Value = LocalTime(interval.Start);
        row.Cells[1].Value = interval.End.HasValue ? LocalTime(interval.End.Value) : "In progress";
    }

    private Break ShowBreakEditor(Form owner, Break original, DateTime defaultTime)
    {
        using (var dialog = SupervisorDialog(original == null ? "Add break" : "Edit break", 410, 305))
        {
            var layout = DialogLayout(dialog, 29, 77, 77, 0, 43);
            layout.Controls.Add(new Label { Text = "Local time", ForeColor = Muted, Dock = DockStyle.Fill }, 0, 0);
            var start = new DateTimeSelection("Break start", original == null ? (DateTime?)null : original.Start, defaultTime);
            var end = new DateTimeSelection("Break end", original == null ? null : original.End, start.LocalValue, () => start.LocalValue);
            layout.Controls.Add(start, 0, 1);
            layout.Controls.Add(end, 0, 2);
            var feedback = new Label { Dock = DockStyle.Fill, ForeColor = Color.LightSalmon };
            layout.Controls.Add(feedback, 0, 3);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill };
            Break result = null;
            dialog.AcceptButton = AddDialogButton(footer, "Save break", 118, true, () =>
            {
                try
                {
                    DateTime from = start.ReadUtc().Value;
                    DateTime? until = end.ReadUtc();
                    if (until < from) throw new FormatException("A break end must be at or after its start.");
                    result = new Break { Start = from, End = until };
                    dialog.DialogResult = DialogResult.OK;
                }
                catch (FormatException error)
                {
                    feedback.Text = error.Message;
                }
            });
            dialog.CancelButton = AddDialogButton(footer, "Cancel", 95, false, dialog.Close);
            layout.Controls.Add(footer, 0, 4);
            return dialog.ShowDialog(owner) == DialogResult.OK ? result : null;
        }
    }

    private sealed class DateTimeSelection : Panel
    {
        private readonly string label;
        private readonly DateTime? original;
        private readonly DateTimePicker date = new DateTimePicker();
        private readonly ComboBox hour = new ComboBox();
        private readonly ComboBox minute = new ComboBox();
        private readonly ComboBox period = new ComboBox();
        private readonly CheckBox include;

        public DateTimeSelection(string label, DateTime? original, DateTime defaultTime, Func<DateTime> startTime = null)
        {
            this.label = label;
            this.original = original;
            Size = new Size(337, 70);
            Margin = new Padding(0, 0, 9, 0);
            if (startTime == null)
                Controls.Add(new Label { Text = label, AutoSize = true, Top = 3 });
            else
            {
                include = new CheckBox { Text = label, AutoSize = true, Checked = original.HasValue };
                Controls.Add(include);
            }

            var inputs = new FlowLayoutPanel { Location = new Point(0, 26), Size = new Size(337, 39), WrapContents = false };
            date.AccessibleName = label + " date";
            date.Format = DateTimePickerFormat.Custom;
            date.CustomFormat = "MMM d, yyyy";
            date.Width = 150;
            date.Margin = new Padding(0, 0, 8, 0);
            date.CalendarForeColor = Ink;
            date.CalendarMonthBackground = Surface;
            inputs.Controls.Add(date);
            ConfigureTimeDropdown(inputs, hour, label + " hour", 45,
                Enumerable.Range(1, 12).Select(value => value.ToString(CultureInfo.InvariantCulture)).ToArray());
            AddTimeSeparator(inputs);
            ConfigureTimeDropdown(inputs, minute, label + " minute", 49,
                Enumerable.Range(0, 60).Select(value => value.ToString("00", CultureInfo.InvariantCulture)).ToArray());
            ConfigureTimeDropdown(inputs, period, label + " AM or PM", 54, new[] { "AM", "PM" });
            period.Margin = new Padding(5, 0, 0, 0);
            Controls.Add(inputs);
            SetLocalValue(original.HasValue ? original.Value.ToLocalTime() : defaultTime);
            if (include != null)
            {
                bool initialized = original.HasValue;
                inputs.Enabled = include.Checked;
                include.CheckedChanged += (sender, args) =>
                {
                    if (include.Checked && !initialized)
                    {
                        SetLocalValue(startTime());
                        initialized = true;
                    }
                    inputs.Enabled = include.Checked;
                };
            }
        }

        public DateTime LocalValue
        {
            get
            {
                int hourOfDay = (hour.SelectedIndex + 1) % 12 + period.SelectedIndex * 12;
                return date.Value.Date.AddHours(hourOfDay).AddMinutes(minute.SelectedIndex);
            }
        }

        public DateTime? ReadUtc()
        {
            if (include != null && !include.Checked) return null;
            DateTime local = DateTime.SpecifyKind(LocalValue, DateTimeKind.Unspecified);
            if (original.HasValue)
            {
                DateTime previous = original.Value.ToLocalTime();
                if (local.Ticks / TimeSpan.TicksPerMinute == previous.Ticks / TimeSpan.TicksPerMinute) return original;
            }
            if (TimeZoneInfo.Local.IsInvalidTime(local) || TimeZoneInfo.Local.IsAmbiguousTime(local))
                throw new FormatException(label + " falls within a daylight saving clock change.");
            return local.ToUniversalTime();
        }

        private void SetLocalValue(DateTime value)
        {
            date.Value = value;
            hour.SelectedIndex = (value.Hour + 11) % 12;
            minute.SelectedIndex = value.Minute;
            period.SelectedIndex = value.Hour / 12;
        }

        private static void ConfigureTimeDropdown(FlowLayoutPanel inputs, ComboBox picker, string name, int width, string[] options)
        {
            picker.AccessibleName = name;
            picker.Width = width;
            picker.DropDownStyle = ComboBoxStyle.DropDownList;
            picker.FlatStyle = FlatStyle.Flat;
            picker.BackColor = Input;
            picker.ForeColor = Ink;
            picker.Margin = new Padding(0);
            picker.Items.AddRange(options);
            inputs.Controls.Add(picker);
        }

        private static void AddTimeSeparator(FlowLayoutPanel inputs)
        {
            inputs.Controls.Add(new Label { Text = ":", Width = 10, Height = 27, Margin = new Padding(0), TextAlign = ContentAlignment.MiddleCenter });
        }
    }

    private static void ValidateShift(Shift shift, IEnumerable<Shift> otherShifts)
    {
        DateTime now = DateTime.UtcNow;
        if (shift.Start > now || shift.End > now || shift.Breaks.Any(interval => interval.Start > now || interval.End > now))
            throw new FormatException("Shift and break times cannot be in the future.");
        if (shift.End < shift.Start) throw new FormatException("Clock out must be at or after clock in.");
        if (otherShifts.Any(other => shift.Start < (other.End ?? DateTime.MaxValue) &&
            other.Start < (shift.End ?? DateTime.MaxValue)))
            throw new FormatException("This shift overlaps another shift for the employee.");

        shift.Breaks.Sort((first, second) => first.Start.CompareTo(second.Start));
        DateTime earliestStart = shift.Start;
        foreach (Break interval in shift.Breaks)
        {
            if (interval.End < interval.Start) throw new FormatException("A break end must be at or after its start.");
            if (shift.End.HasValue && (!interval.End.HasValue || interval.End > shift.End))
                throw new FormatException("Each break must end within a completed shift.");
            if (interval.Start < earliestStart)
                throw new FormatException("Breaks must start within the shift and cannot overlap.");
            earliestStart = interval.End ?? DateTime.MaxValue;
        }
    }

    private bool AddEmployee()
    {
        return ShowPinDialog("Add employee", "Create a four-digit employee PIN.", true, true, (name, pin) =>
        {
            if (name.Length == 0 || employees.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
                return "Enter a unique employee name.";

            if (MatchesSupervisorPin(pin)) return "Choose a different employee PIN.";

            return SaveChange(records => records.Add(new Employee { Name = name, PinCredential = CreateCredential(pin) }))
                ? null : "Try saving again.";
        });
    }

    private void ManageEmployeePin(int index)
    {
        ShowPinDialog("Set employee PIN", "Create a four-digit employee PIN for " + employees[index].Name + ".",
            true, false, (name, pin) =>
            {
                if (MatchesSupervisorPin(pin)) return "Choose a different employee PIN.";
                return SaveChange(records => records[index].PinCredential = CreateCredential(pin))
                    ? null : "Try saving again.";
            });
    }

    private bool AuthorizeSupervisor()
    {
        string credential = File.ReadAllText(supervisorPath);
        return ShowPinDialog("Supervisor access", "Enter the supervisor PIN.", false, false,
            (name, pin) => Authenticate(credential, pin), PinLengthLimit(credential));
    }

    private void ChangeSupervisorPin()
    {
        PromptForSupervisorPin("Change supervisor PIN", "Choose a new four-digit PIN for supervisor access.");
    }

    private bool PromptForSupervisorPin(string title, string instructions)
    {
        return ShowPinDialog(title, instructions, true, false, (name, pin) => SaveSupervisorPin(pin));
    }

    private bool MatchesSupervisorPin(string pin)
    {
        return File.Exists(supervisorPath) && MatchesFourDigitPin(File.ReadAllText(supervisorPath), pin);
    }

    private static bool MatchesFourDigitPin(string credential, string pin)
    {
        return !string.IsNullOrEmpty(credential) && PinLengthLimit(credential) == 4 && VerifyCredential(credential, pin);
    }

    private string SaveSupervisorPin(string pin)
    {
        if (employees.Any(employee => MatchesFourDigitPin(employee.PinCredential, pin)))
            return "That PIN is already used by an employee.";

        WriteAtomically(supervisorPath, stream =>
        {
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), 1024, true))
                writer.Write(CreateCredential(pin));
        });
        return null;
    }

    private void PromptForPunch(bool finishShift)
    {
        if (SelectedEmployee == null || string.IsNullOrEmpty(SelectedEmployee.PinCredential)) return;
        string action = finishShift ? clockOut.Text : punch.Text;
        if (ShowPinDialog(action, "Enter the employee PIN for " + SelectedEmployee.Name + ".", false, false,
            (name, pin) => RecordPunch(finishShift, pin), PinLengthLimit(SelectedEmployee.PinCredential)))
            inactivity.Restart();
    }

    private string RecordPunch(bool finishShift, string pin)
    {
        int employeeIndex = SelectedEmployeeIndex;
        if (employeeIndex < 0 || SelectedEmployee.Inactive || string.IsNullOrEmpty(SelectedEmployee.PinCredential))
            return "Ask a supervisor to set your employee PIN.";

        string error = Authenticate(SelectedEmployee.PinCredential, pin);
        if (error != null) return error;

        if (SaveChange(records =>
        {
            var shifts = records[employeeIndex].Shifts;
            var shift = shifts.LastOrDefault(s => !s.End.HasValue);
            if (finishShift && shift == null) return;

            DateTime now = DateTime.UtcNow;
            if (shift == null)
            {
                shifts.Add(new Shift { Start = now });
                return;
            }

            Break activeBreak = shift.Breaks.LastOrDefault(b => !b.End.HasValue);
            Break lastBreak = shift.Breaks.LastOrDefault();
            DateTime lastEvent = lastBreak == null ? shift.Start : lastBreak.End ?? lastBreak.Start;
            if (now < lastEvent) throw new InvalidOperationException("The Windows clock is earlier than the last punch.");

            if (activeBreak != null) activeBreak.End = now;
            if (finishShift) shift.End = now;
            else if (activeBreak == null) shift.Breaks.Add(new Break { Start = now });
        }))
        {
            RefreshEmployee();
            return null;
        }

        return "Try recording the punch again.";
    }

    private bool ShowPinDialog(string title, string instructions, bool create, bool includeName,
        Func<string, string, string> submit, int pinLengthLimit = 4)
    {
        using (var dialog = new Form
        {
            Text = title, ClientSize = new Size(435, 290), StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            ShowInTaskbar = false, Font = Font, BackColor = Surface, ForeColor = Ink, AutoScaleMode = AutoScaleMode.Dpi
        })
        {
            dialog.HandleCreated += (sender, args) => UseDarkCaption(dialog);
            var content = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, Padding = new Padding(20), FlowDirection = FlowDirection.TopDown,
                WrapContents = false
            };
            dialog.Controls.Add(content);
            content.Controls.Add(new Label { Text = instructions, Width = 390, Height = 42, ForeColor = Muted });
            TextBox name = includeName ? AddField(content, "Employee name", false, 80) : null;
            TextBox pin = AddField(content, "PIN", true, pinLengthLimit);
            TextBox confirmation = create ? AddField(content, "Confirm PIN", true, 4) : null;
            var feedback = new Label { Width = 390, Height = 27, ForeColor = Color.LightSalmon };
            content.Controls.Add(feedback);
            var buttons = new FlowLayoutPanel { Width = 390, Height = 46, FlowDirection = FlowDirection.RightToLeft };
            var accept = new Button { Text = create ? "Save" : "Confirm" };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
            StyleButton(accept, true);
            StyleButton(cancel, false);
            accept.Size = cancel.Size = new Size(105, 36);
            buttons.Controls.Add(accept);
            buttons.Controls.Add(cancel);
            content.Controls.Add(buttons);
            dialog.ClientSize = new Size(445, includeName ? 345 : create ? 287 : 229);
            dialog.AcceptButton = accept;
            dialog.CancelButton = cancel;
            dialog.ActiveControl = name ?? pin;
            accept.Click += (sender, args) =>
            {
                string error;
                try
                {
                    if (create && !IsValidPin(pin.Text, 4)) error = "Use exactly four digits.";
                    else if (create && pin.Text != confirmation.Text) error = "The PINs do not match.";
                    else error = submit(name == null ? "" : name.Text.Trim(), pin.Text);
                }
                catch (Exception exception)
                {
                    error = "Could not complete the request. " + exception.Message;
                }

                if (error == null)
                {
                    dialog.DialogResult = DialogResult.OK;
                    return;
                }

                feedback.Text = error;
                pin.Focus();
                pin.SelectAll();
            };
            var keyboard = new TouchKeyboard(dialog);
            keyboard.Attach(false, name);
            keyboard.Attach(true, pin, confirmation);
            return dialog.ShowDialog(Form.ActiveForm ?? this) == DialogResult.OK;
        }
    }

    private static TextBox AddField(FlowLayoutPanel content, string label, bool secret, int maxLength)
    {
        content.Controls.Add(new Label { Text = label, AutoSize = true });
        var field = new TextBox
        {
            Width = 390, BackColor = Input, ForeColor = Ink, BorderStyle = BorderStyle.FixedSingle,
            AccessibleName = label, UseSystemPasswordChar = secret, MaxLength = maxLength,
            ShortcutsEnabled = !secret, Margin = new Padding(3, 0, 3, 6)
        };
        content.Controls.Add(field);
        return field;
    }

    private sealed class TouchKeyboard : Panel
    {
        private readonly Form dialog;
        private readonly FlowLayoutPanel textKeys;
        private readonly FlowLayoutPanel numberKeys;
        private readonly List<Button> letters = new List<Button>();
        private TextBox target;
        private bool uppercase;
        private bool symbols;
        private bool open;

        public TouchKeyboard(Form dialog)
        {
            this.dialog = dialog;
            Dock = DockStyle.Bottom;
            BackColor = Surface;
            AccessibleName = "On-screen keyboard";
            Visible = false;
            textKeys = BuildTextKeyboard();
            numberKeys = BuildNumberPad();
            Controls.Add(textKeys);
            Controls.Add(numberKeys);
            dialog.Controls.Add(this);
            SendToBack();
        }

        public void Attach(bool numeric, params TextBox[] fields)
        {
            foreach (TextBox field in fields.Where(item => item != null))
            {
                field.Enter += (sender, args) => ShowFor(field, numeric);
                field.MouseUp += (sender, args) => ShowFor(field, numeric);
                dialog.Shown += (sender, args) =>
                {
                    if (field.Focused) ShowFor(field, numeric);
                };
            }
        }

        private void ShowFor(TextBox field, bool numeric)
        {
            if (!dialog.Visible || !field.Enabled || field.ReadOnly) return;
            target = field;
            FlowLayoutPanel keys = numeric ? numberKeys : textKeys;
            int previousHeight = open ? Height : 0;
            if (open && keys.Visible) return;
            SuspendLayout();
            textKeys.Visible = !numeric;
            numberKeys.Visible = numeric;
            Height = keys.Controls.Count * keys.Controls[0].Height + keys.Padding.Vertical;
            Visible = true;
            open = true;
            SendToBack();
            ResizeDialog(Height - previousHeight);
            ResumeLayout();
        }

        private void HideKeyboard()
        {
            if (!open) return;
            Visible = false;
            open = false;
            ResizeDialog(-Height);
        }

        private void ResizeDialog(int change)
        {
            if (change == 0) return;
            Rectangle area = Screen.FromControl(dialog).WorkingArea;
            dialog.ClientSize = new Size(dialog.ClientSize.Width, dialog.ClientSize.Height + change);
            dialog.Top = Math.Max(area.Top, Math.Min(dialog.Top - change / 2, area.Bottom - dialog.Height));
        }

        private FlowLayoutPanel CreateKeyboard()
        {
            return new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                Padding = new Padding(7, 5, 7, 5), AutoScroll = false
            };
        }

        private static FlowLayoutPanel AddKeyRow(FlowLayoutPanel keyboard)
        {
            var row = new FlowLayoutPanel
            {
                Width = 427, Height = 43, WrapContents = false, Margin = new Padding(0)
            };
            keyboard.Controls.Add(row);
            return row;
        }

        private FlowLayoutPanel BuildNumberPad()
        {
            var keyboard = CreateKeyboard();
            for (int first = 1; first <= 7; first += 3)
            {
                var row = AddKeyRow(keyboard);
                foreach (int digit in Enumerable.Range(first, 3))
                    AddCharacterKey(row, digit.ToString(CultureInfo.InvariantCulture), 120);
            }
            var controls = AddKeyRow(keyboard);
            AddKey(controls, "Backspace", 120, Backspace);
            AddCharacterKey(controls, "0", 120);
            AddKey(controls, "Done", 120, HideKeyboard);
            return keyboard;
        }

        private FlowLayoutPanel BuildTextKeyboard()
        {
            var keyboard = CreateKeyboard();
            var digits = AddKeyRow(keyboard);
            foreach (char digit in "1234567890") AddCharacterKey(digits, digit.ToString(), 38);

            foreach (string keys in new[] { "qwertyuiop", "asdfghjkl", "zxcvbnm" })
            {
                var row = AddKeyRow(keyboard);
                foreach (char letter in keys)
                    letters.Add(AddCharacterKey(row, letter.ToString(), 38));
            }

            AddCharacterKey((FlowLayoutPanel)keyboard.Controls[2], "@", 38);
            foreach (char punctuation in "._-")
                AddCharacterKey((FlowLayoutPanel)keyboard.Controls[3], punctuation.ToString(), 38);

            var actions = AddKeyRow(keyboard);
            AddKey(actions, "Shift", 57, () =>
            {
                uppercase = !uppercase;
                RefreshTextKeys();
            });
            AddKey(actions, "Symbols", 76, () =>
            {
                symbols = !symbols;
                RefreshTextKeys();
            });
            AddKey(actions, "Space", 86, () => Insert(" "));
            AddKey(actions, "Backspace", 92, Backspace);
            AddKey(actions, "Done", 62, HideKeyboard);

            return keyboard;
        }

        private void RefreshTextKeys()
        {
            const string alphabet = "qwertyuiopasdfghjklzxcvbnm";
            const string punctuation = @"!#$%&'*/=?^|~\:;()[]{}<>,+";
            for (int index = 0; index < letters.Count; index++)
            {
                char key = symbols ? punctuation[index] : uppercase ? char.ToUpperInvariant(alphabet[index]) : alphabet[index];
                letters[index].Text = letters[index].AccessibleName = key.ToString();
            }
        }

        private Button AddCharacterKey(FlowLayoutPanel row, string text, int width)
        {
            var key = new TouchKey();
            ConfigureKey(row, key, text, width);
            key.Click += (sender, args) => Insert(key.Text);
            return key;
        }

        private static void AddKey(FlowLayoutPanel row, string text, int width, Action action)
        {
            var key = new TouchKey();
            ConfigureKey(row, key, text, width);
            key.Click += (sender, args) => action();
        }

        private static void ConfigureKey(FlowLayoutPanel row, Button key, string text, int width)
        {
            StyleButton(key, false);
            key.Text = key.AccessibleName = text;
            key.Size = new Size(width, 39);
            key.Margin = new Padding(2);
            row.Controls.Add(key);
        }

        private void Insert(string text)
        {
            if (target == null || !target.Enabled || target.ReadOnly) return;
            if (target.TextLength - target.SelectionLength + text.Length <= target.MaxLength)
                target.SelectedText = text;
        }

        private void Backspace()
        {
            if (target == null || !target.Enabled || target.ReadOnly) return;
            if (target.SelectionLength == 0 && target.SelectionStart > 0)
                target.Select(target.SelectionStart - 1, 1);
            target.SelectedText = "";
        }

        private sealed class TouchKey : Button
        {
            public TouchKey()
            {
                SetStyle(ControlStyles.Selectable, false);
                TabStop = false;
            }
        }
    }

    private string Authenticate(string credential, string pin)
    {
        DateTime now = DateTime.UtcNow;
        if (now < retryAfter)
            return "Too many attempts. Try again in " + Math.Ceiling((retryAfter - now).TotalSeconds) + " seconds.";

        int limit = PinLengthLimit(credential);
        bool validLength = limit == 4 ? IsValidPin(pin, 4) :
            pin != null && pin.Length >= 6 && pin.Length <= limit && IsNumeric(pin);
        if (validLength && VerifyCredential(credential, pin))
        {
            failedAttempts = 0;
            return null;
        }

        failedAttempts++;
        if (failedAttempts < 5) return "Incorrect PIN.";
        failedAttempts = 0;
        retryAfter = now.AddSeconds(30);
        return "Too many attempts. Try again in 30 seconds.";
    }

    private static int PinLengthLimit(string credential)
    {
        return credential.StartsWith("4:", StringComparison.Ordinal) ? 4 : 12;
    }

    private static bool IsValidPin(string pin, int length)
    {
        return pin != null && pin.Length == length && IsNumeric(pin);
    }

    private static bool IsNumeric(string value)
    {
        return value.All(digit => digit >= '0' && digit <= '9');
    }

    private static string CreateCredential(string pin)
    {
        var salt = new byte[16];
        using (var random = RandomNumberGenerator.Create()) random.GetBytes(salt);
        return "4:" + Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(DerivePin(pin, salt));
    }

    private static bool VerifyCredential(string credential, string pin)
    {
        if (PinLengthLimit(credential) == 4) credential = credential.Substring(2);
        string[] parts = credential.Split(':');
        if (parts.Length != 2) return false;
        byte[] salt = Convert.FromBase64String(parts[0]);
        byte[] expected = Convert.FromBase64String(parts[1]);
        if (salt.Length != 16 || expected.Length != 32) return false;
        byte[] actual = DerivePin(pin, salt);
        int difference = 0;
        for (int i = 0; i < expected.Length; i++) difference |= expected[i] ^ actual[i];
        return difference == 0;
    }

    private static byte[] DerivePin(string pin, byte[] salt)
    {
        using (var derivation = new Rfc2898DeriveBytes(pin, salt, 600000, HashAlgorithmName.SHA256))
            return derivation.GetBytes(32);
    }

    private static void WriteAtomically(string path, Action<Stream> write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew))
            {
                write(stream);
                stream.Flush(true);
            }

            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private List<Employee> ReadRecords()
    {
        using (var stream = File.OpenRead(dataPath))
            return (List<Employee>)Serializer.Deserialize(stream);
    }

    private bool SaveChange(Action<List<Employee>> change)
    {
        try
        {
            var records = File.Exists(dataPath) ? ReadRecords() : new List<Employee>();
            change(records);
            WriteAtomically(dataPath, stream => Serializer.Serialize(stream, records));
            employees = records;
            return true;
        }
        catch (Exception error)
        {
            MessageBox.Show(Form.ActiveForm ?? this, "Could not save the change. " + error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private void RefreshEmployee()
    {
        history.BeginUpdate();
        history.Items.Clear();
        if (SelectedEmployee != null)
        {
            foreach (Shift shift in SelectedEmployee.Shifts.Where(s => s.End.HasValue).OrderByDescending(s => s.Start))
            {
                DateTime end = shift.End.Value;
                history.Items.Add(new ListViewItem(new[] { LocalTime(shift.Start), LocalTime(end),
                    Duration(shift.BreakTime(end)), Duration(shift.Worked(end)) }));
            }
        }
        history.EndUpdate();
        ResizeHistory();
        RefreshClock();
    }

    private void RefreshClock()
    {
        Shift shift = ActiveShift;
        DateTime now = DateTime.UtcNow;
        bool onBreak = shift != null && shift.Breaks.Any(b => !b.End.HasValue);
        status.Text = SelectedEmployee == null ? "Select an employee" :
            shift == null ? "Clocked out" : onBreak ? "On break" : "Clocked in";
        elapsed.Text = Duration(shift == null ? TimeSpan.Zero : shift.Worked(now));
        details.Text = employees.Count == 0 ? "A supervisor can add employees from the gear menu." :
            shift == null ? "Time worked" :
            "Time worked   |   Started " + LocalTime(shift.Start) + "   |   Break time " + Duration(shift.BreakTime(now));
        bool hasPin = SelectedEmployee != null && !string.IsNullOrEmpty(SelectedEmployee.PinCredential);
        managePin.Enabled = SelectedEmployee != null;
        if (SelectedEmployee != null && !hasPin)
            details.Text = "Ask a supervisor to set your employee PIN.";
        punch.Enabled = hasPin;
        punch.Text = shift == null ? "Clock in" : onBreak ? "End break" : "Start break";
        clockOut.Enabled = hasPin && shift != null;
    }

    private static string LocalTime(DateTime utc)
    {
        return utc.ToLocalTime().ToString("MMM d, yyyy h:mm tt");
    }

    private static string Duration(TimeSpan value)
    {
        return string.Format("{0:00}:{1:00}:{2:00}", (long)value.TotalHours, value.Minutes, value.Seconds);
    }
}
