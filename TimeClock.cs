using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
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
[assembly: AssemblyVersion("1.0.8.0")]
[assembly: AssemblyFileVersion("1.0.8.0")]

public class Employee
{
    public string Name { get; set; }
    public string PinCredential { get; set; }
    public bool Inactive { get; set; }
    public bool IsSupervisor { get; set; }
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
    private static readonly Color ClockInAccent = Color.FromArgb(83, 212, 131);
    private static readonly Color ClockOutAccent = Color.FromArgb(255, 104, 112);
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
    private readonly TableLayoutPanel pinView;
    private readonly Label punchConfirmation = new Label();
    private readonly Stopwatch confirmationTime = new Stopwatch();
    private readonly TextBox employeePin = new TextBox();
    private readonly Label pinMessage = new Label();
    private readonly Label employeeName = new Label();
    private readonly Button supervisorMenu = new Button { Text = "Supervisor menu" };
    private int selectedEmployeeIndex = -1;
    private int failedAttempts;
    private DateTime retryAfter;
    private bool checkingPin;
    private readonly Label status = new Label();
    private readonly Label elapsed = new Label();
    private readonly Label details = new Label();
    private readonly Label weeklyHours = new Label();
    private readonly Button punch = new PunchButton();
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

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28), ColumnCount = 1, RowCount = 4 };
        employeeView = layout;
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (int height in new[] { 60, 292, 44 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(layout);

        var heading = new Panel { Dock = DockStyle.Fill };
        var signOut = new Button { Text = "Sign out", Dock = DockStyle.Right };
        StyleButton(signOut, false);
        signOut.Click += (sender, args) => ShowPinScreen();
        StyleButton(supervisorMenu, false);
        supervisorMenu.Dock = DockStyle.Right;
        supervisorMenu.Visible = false;
        supervisorMenu.Click += (sender, args) =>
        {
            if (SelectedEmployee == null || !SelectedEmployee.IsSupervisor) return;
            ShowPinScreen();
            ShowSupervisorPanel();
        };
        employeeName.Dock = DockStyle.Fill;
        employeeName.Font = new Font("Segoe UI", 23, FontStyle.Bold);
        employeeName.AutoEllipsis = true;
        heading.Controls.Add(employeeName);
        heading.Controls.Add(supervisorMenu);
        heading.Controls.Add(signOut);
        layout.Controls.Add(heading, 0, 0);

        var card = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28, 16, 24, 16),
            BackColor = Surface, ColumnCount = 2, RowCount = 1 };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(card, 0, 1);

        var summary = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
        summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        summary.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        foreach (int height in new[] { 30, 75, 38, 36 })
            summary.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        summary.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        card.Controls.Add(summary, 0, 0);

        status.Dock = elapsed.Dock = details.Dock = DockStyle.Fill;
        status.Font = new Font("Segoe UI", 12, FontStyle.Bold);
        elapsed.Font = new Font("Segoe UI", 40, FontStyle.Bold);
        details.ForeColor = Muted;
        summary.Controls.Add(status, 0, 1);
        summary.Controls.Add(elapsed, 0, 2);
        summary.Controls.Add(details, 0, 3);
        weeklyHours.Dock = DockStyle.Fill;
        weeklyHours.Font = new Font("Segoe UI", 13, FontStyle.Bold);
        summary.Controls.Add(weeklyHours, 0, 4);
        punch.Anchor = AnchorStyles.None;
        punch.Size = new Size(216, 216);
        punch.Font = new Font("Segoe UI", 24, FontStyle.Bold);
        punch.Click += (sender, args) => PunchAndSignOut(ActiveShift != null);
        card.Controls.Add(punch, 1, 0);

        layout.Controls.Add(new Label { Text = "Shift history", Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft,
            Padding = new Padding(0, 0, 0, 6), Font = new Font("Segoe UI", 13, FontStyle.Bold) }, 0, 2);
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
        layout.Controls.Add(history, 0, 3);

        pinView = BuildPinScreen();
        Controls.Add(pinView);
        punchConfirmation.Dock = DockStyle.Fill;
        punchConfirmation.BackColor = Background;
        punchConfirmation.Font = new Font("Segoe UI", 34, FontStyle.Bold);
        punchConfirmation.TextAlign = ContentAlignment.MiddleCenter;
        punchConfirmation.AccessibleRole = AccessibleRole.Alert;
        punchConfirmation.Visible = false;
        Controls.Add(punchConfirmation);
        ConfigureIdleClock();
        ShowPinScreen();
        timer.Interval = 1000;
        timer.Tick += (sender, args) => UpdateIdleScreen(inactivity.Elapsed);
        timer.Start();
        Activated += (sender, args) => inactivity.Restart();
        Shown += async (sender, args) =>
        {
            employeePin.Focus();
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
        if (checkingPin) return;

        if (confirmationTime.IsRunning)
        {
            if (confirmationTime.Elapsed >= TimeSpan.FromSeconds(2.5)) ShowPinScreen();
            return;
        }

        if (idleClock.Visible)
        {
            RefreshIdleClock();
            return;
        }

        if (startupUpdate != null && ActiveForm == this && selectedEmployeeIndex < 0 &&
            employeePin.TextLength == 0 && CanShowIdleScreen())
        {
            ClockItUpdater.Update update = startupUpdate;
            startupUpdate = null;
            if (ShowUpdater(this, update)) Close();
            return;
        }

        if (inactiveFor >= IdleDelay && CanShowIdleScreen())
        {
            selectedEmployeeIndex = -1;
            employeePin.Clear();
            pinMessage.Text = "";
            RefreshIdleClock();
            employeeView.Visible = false;
            pinView.Visible = false;
            idleClock.Visible = true;
            idleClock.BringToFront();
            idleClock.Focus();
            return;
        }

        RefreshClock();
    }

    private void RefreshIdleClock()
    {
        DateTime now = DateTime.Now;
        idleClock.FishCount = employees.Count(employee => employee.Shifts.Any(shift => !shift.End.HasValue));
        idleClock.SetDate(now);
        idleClock.Text = now.ToString("h:mm tt", CultureInfo.InvariantCulture);
    }

    private bool CanShowIdleScreen()
    {
        return Visible && Enabled && WindowState != FormWindowState.Minimized &&
            !Application.OpenForms.Cast<Form>().Any(form => form != this && form.Visible);
    }

    private void DismissIdleClock()
    {
        idleClock.Visible = false;
        ShowPinScreen();
    }

    private TableLayoutPanel BuildPinScreen()
    {
        var screen = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28, 16, 28, 16), ColumnCount = 1, RowCount = 3 };
        screen.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        screen.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        screen.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        screen.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        screen.Controls.Add(new Label { Text = AppName, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 25, FontStyle.Bold) }, 0, 0);
        var settings = new Button { Text = "\u2699", AccessibleName = "Supervisor settings", Dock = DockStyle.Right };
        StyleButton(settings, false);
        settings.Width = 52;
        settings.Font = new Font("Segoe UI Symbol", 20);
        settings.TabIndex = 1;
        settings.Click += (sender, args) =>
        {
            employeePin.Clear();
            pinMessage.Text = "";
            OpenSupervisor();
            inactivity.Restart();
            employeePin.Focus();
        };
        var footer = new Panel { Dock = DockStyle.Fill, TabIndex = 2 };
        footer.Controls.Add(settings);
        screen.Controls.Add(footer, 0, 2);

        var keypad = new TableLayoutPanel { Anchor = AnchorStyles.None, Size = new Size(340, 420), ColumnCount = 3, RowCount = 8, TabIndex = 1 };
        for (int column = 0; column < 3; column++)
            keypad.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        foreach (int height in new[] { 36, 50, 34, 60, 60, 60, 60, 60 })
            keypad.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        var title = new Label { Text = "Enter your PIN", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 17, FontStyle.Bold) };
        keypad.Controls.Add(title, 0, 0);
        keypad.SetColumnSpan(title, 3);
        employeePin.AccessibleName = "PIN";
        employeePin.UseSystemPasswordChar = true;
        employeePin.MaxLength = 12;
        employeePin.TextAlign = HorizontalAlignment.Center;
        employeePin.Font = new Font("Segoe UI", 22);
        employeePin.BackColor = Input;
        employeePin.ForeColor = Ink;
        employeePin.Dock = DockStyle.Fill;
        employeePin.KeyPress += (sender, args) =>
        {
            if (!char.IsControl(args.KeyChar) && (args.KeyChar < '0' || args.KeyChar > '9'))
                args.Handled = true;
        };
        employeePin.KeyDown += async (sender, args) =>
        {
            if (args.KeyCode != Keys.Enter) return;
            args.SuppressKeyPress = true;
            await SubmitEmployeePin();
        };
        keypad.Controls.Add(employeePin, 0, 1);
        keypad.SetColumnSpan(employeePin, 3);
        pinMessage.Dock = DockStyle.Fill;
        pinMessage.TextAlign = ContentAlignment.MiddleCenter;
        pinMessage.ForeColor = Accent;
        keypad.Controls.Add(pinMessage, 0, 2);
        keypad.SetColumnSpan(pinMessage, 3);

        for (int index = 0; index < 12; index++)
        {
            string key = index < 9 ? (index + 1).ToString(CultureInfo.InvariantCulture) :
                index == 9 ? "Clear" : index == 10 ? "0" : "Delete";
            var button = new Button { Text = key };
            StyleButton(button, false);
            button.Dock = DockStyle.Fill;
            button.Margin = new Padding(4);
            button.Font = new Font("Segoe UI", 16, FontStyle.Bold);
            button.Click += (sender, args) =>
            {
                if (key == "Clear") employeePin.Clear();
                else if (key == "Delete")
                {
                    if (employeePin.TextLength > 0)
                        employeePin.Text = employeePin.Text.Substring(0, employeePin.TextLength - 1);
                }
                else if (employeePin.TextLength < employeePin.MaxLength) employeePin.AppendText(key);
                employeePin.SelectionStart = employeePin.TextLength;
                employeePin.Focus();
            };
            keypad.Controls.Add(button, index % 3, 3 + index / 3);
        }
        var enter = new Button { Text = "Continue" };
        StyleButton(enter, true);
        enter.Dock = DockStyle.Fill;
        enter.Margin = new Padding(4);
        enter.Click += async (sender, args) => await SubmitEmployeePin();
        keypad.Controls.Add(enter, 0, 7);
        keypad.SetColumnSpan(enter, 3);
        screen.Controls.Add(keypad, 0, 1);
        return screen;
    }

    private void ShowPinScreen()
    {
        confirmationTime.Reset();
        punchConfirmation.Visible = false;
        selectedEmployeeIndex = -1;
        employeePin.Clear();
        pinMessage.Text = "";
        employeeView.Visible = false;
        pinView.Visible = true;
        pinView.BringToFront();
        employeePin.Focus();
        inactivity.Restart();
    }

    private Employee SelectedEmployee
    {
        get { return selectedEmployeeIndex < 0 ? null : employees[selectedEmployeeIndex]; }
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

    private sealed class PunchButton : Button
    {
        public PunchButton()
        {
            FlatStyle = FlatStyle.Flat;
            Cursor = Cursors.Hand;
            DoubleBuffered = true;
        }

        protected override void OnSizeChanged(EventArgs args)
        {
            base.OnSizeChanged(args);
            if (ClientSize.Width == 0 || ClientSize.Height == 0) return;
            using (var outline = new GraphicsPath())
            {
                outline.AddEllipse(ClientRectangle);
                Region = new Region(outline);
            }
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            var bounds = ClientRectangle;
            bounds.Inflate(-2, -2);
            args.Graphics.Clear(Surface);
            args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = Enabled ? BackColor : Input;
            using (var brush = new SolidBrush(fill))
                args.Graphics.FillEllipse(brush, bounds);
            if (Focused)
            {
                bounds.Inflate(-7, -7);
                using (var ring = new Pen(ForeColor, 2))
                    args.Graphics.DrawEllipse(ring, bounds);
            }
            TextRenderer.DrawText(args.Graphics, Text, Font, ClientRectangle, Enabled ? ForeColor : Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    private void ResizeHistory()
    {
        if (history.Columns.Count != 4) return;
        int width = Math.Max(0, history.ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
        history.Columns[0].Width = history.Columns[1].Width = width * 3 / 10;
        history.Columns[2].Width = history.Columns[3].Width = width / 5;
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
                addShift.Enabled = index >= 0;
                setPin.Enabled = changeStatus.Enabled = index >= 0 && !employees[index].IsSupervisor;
                changeStatus.Text = EmployeeStatusAction(index >= 0 && employees[index].Inactive);
                refresh();
            };
            foreach (Employee employee in employees) picker.Items.Add(SupervisorEmployeeName(employee));
            editButton.Enabled = delete.Enabled = addShift.Enabled = setPin.Enabled = changeStatus.Enabled = false;
            if (picker.Items.Count > 0) picker.SelectedIndex = 0;
            layout.Controls.Add(tools, 0, 0);
            layout.Controls.Add(grid, 0, 1);
            layout.Controls.Add(footer, 0, 2);
            layout.Controls.Add(settings, 0, 3);
            dialog.ShowDialog(this);
        }
        if (restartingForUpdate) Close();
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
                Value = StartOfWeek(today)
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

            var preview = TimesheetGrid("Row type", "Employee", "Start (local)", "End (local)", "Breaks", "Worked", "Worked hours", "Status");
            preview.AccessibleName = "Weekly payroll preview";
            preview.Columns[1].FillWeight = 117;
            preview.Columns[2].FillWeight = preview.Columns[3].FillWeight = 143;
            preview.Columns[4].FillWeight = preview.Columns[5].FillWeight = 62;
            preview.Columns[6].FillWeight = 101;
            preview.Columns[7].FillWeight = 69;
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
                Duration(BreakTime), Duration(WorkedTime), WorkedTime.TotalHours.ToString("0.0000", CultureInfo.InvariantCulture),
                Employee.Inactive ? "Inactive" : "Active"
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
            csv.AppendLine("Row type,Employee,Week start,Week end,Start in period (local),End in period (local),Break duration,Worked duration,Worked hours,Employee status");
            string weekStart = FirstDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string weekEnd = FirstDay.AddDays(6).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            foreach (PayrollRow row in Rows)
            {
                string[] columns = row.Columns();
                csv.AppendLine(string.Join(",", columns[0], PayrollCsvName(columns[1]), weekStart, weekEnd,
                    columns[2], columns[3], columns[4], columns[5], columns[6], columns[7]));
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
                TimeSpan unpaid = UnpaidTime(shift, from, to, shift.End.Value);
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

    private static TimeSpan UnpaidTime(Shift shift, DateTime from, DateTime to, DateTime shiftEnd)
    {
        long breakTicks = shift.Breaks.Sum(interval => OverlapTicks(from, to, interval.Start, interval.End ?? shiftEnd));
        return TimeSpan.FromTicks(Math.Min((to - from).Ticks, breakTicks));
    }

    private static DateTime StartOfWeek(DateTime localTime)
    {
        return localTime.Date.AddDays(-((int)localTime.DayOfWeek + 6) % 7);
    }

    private static TimeSpan WorkedThisWeek(Employee employee, DateTime now)
    {
        DateTime start = StartOfWeek(now.ToLocalTime()).ToUniversalTime();
        TimeSpan total = TimeSpan.Zero;
        foreach (Shift shift in employee.Shifts)
        {
            DateTime shiftEnd = shift.End ?? now;
            DateTime from = shift.Start < start ? start : shift.Start;
            DateTime to = shiftEnd > now ? now : shiftEnd;
            if (to > from) total += to - from - UnpaidTime(shift, from, to, shiftEnd);
        }
        return total;
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
        using (var dialog = SupervisorDialog(original == null ? "Add shift" : "Edit shift", 806, 575))
        {
            var layout = DialogLayout(dialog, 34, 0, 32, 42, 48);
            layout.Controls.Add(new Label { Text = employee.Name, Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 15, FontStyle.Bold), AutoEllipsis = true }, 0, 0);
            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
            layout.Controls.Add(body, 0, 1);
            var fields = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
                AutoScroll = true, WrapContents = false };
            body.Controls.Add(fields, 0, 0);

            var keypad = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
                WrapContents = false, BackColor = Surface, Padding = new Padding(10) };
            body.Controls.Add(keypad, 1, 0);
            var entry = new Label { Width = 295, Height = 38, Font = new Font("Segoe UI", 14, FontStyle.Bold) };
            keypad.Controls.Add(entry);
            var periods = new FlowLayoutPanel { Width = 295, Height = 45, WrapContents = false };
            keypad.Controls.Add(periods);
            var keys = new TableLayoutPanel { Width = 295, Height = 204, ColumnCount = 3, RowCount = 4 };
            for (int column = 0; column < 3; column++) keys.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
            for (int row = 0; row < 4; row++) keys.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
            keypad.Controls.Add(keys);
            var applyRow = new FlowLayoutPanel { Width = 295, Height = 47 };
            keypad.Controls.Add(applyRow);
            var total = new Label { Dock = DockStyle.Fill, ForeColor = Accent, Font = new Font("Segoe UI", 13, FontStyle.Bold) };
            layout.Controls.Add(total, 0, 2);
            var feedback = new Label { Dock = DockStyle.Fill, ForeColor = Color.LightSalmon };
            layout.Controls.Add(feedback, 0, 3);

            DateTimeSelection target = null;
            string digits = "";
            bool pm = false;
            bool pending = false;
            Button save = null;
            Button apply = null;
            Button amButton = null;
            Button pmButton = null;
            Action refresh = null;
            Action selectTime = () =>
            {
                if (target != null && !target.HasTime) target = null;
                entry.Text = target == null ? "Select a time" : target.FieldName + "   " +
                    (digits.Length == 0 ? target.LocalValue.ToString("h:mm", CultureInfo.InvariantCulture) : digits);
                amButton.BackColor = !pm && target != null ? Highlight : Input;
                pmButton.BackColor = pm && target != null ? Highlight : Input;
                periods.Enabled = keys.Enabled = target != null;
                apply.Enabled = target != null && pending;
            };
            Action<DateTimeSelection> activate = field =>
            {
                target = field;
                digits = "";
                pm = field != null && field.LocalValue.Hour >= 12;
                pending = false;
                selectTime();
                refresh();
            };
            var clockIn = new DateTimeSelection("Clock in", original == null ? (DateTime?)null : original.Start, DateTime.Now);
            var clockOutField = new DateTimeSelection("Clock out", original == null ? null : original.End,
                clockIn.LocalValue, () => clockIn.LocalValue);
            Action<DateTimeSelection> connect = field =>
            {
                field.EditTime += () => activate(field);
                field.ValueChanged += () =>
                {
                    if (field == target && !field.HasTime) activate(null);
                    else refresh();
                };
            };
            connect(clockIn);
            connect(clockOutField);
            fields.Controls.Add(clockIn);
            fields.Controls.Add(clockOutField);

            amButton = AddDialogButton(periods, "AM", 76, false, () => { pm = false; pending = true; selectTime(); refresh(); });
            pmButton = AddDialogButton(periods, "PM", 76, false, () => { pm = true; pending = true; selectTime(); refresh(); });
            string[] labels = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "Clear", "0", "Backspace" };
            for (int index = 0; index < labels.Length; index++)
            {
                string key = labels[index];
                var button = new Button { Text = key };
                StyleButton(button, false);
                button.Margin = new Padding(3);
                button.Dock = DockStyle.Fill;
                button.Click += (sender, args) =>
                {
                    if (key == "Clear") digits = "";
                    else if (key == "Backspace" && digits.Length > 0) digits = digits.Substring(0, digits.Length - 1);
                    else if (key.Length == 1 && digits.Length < 4) digits += key;
                    pending = true;
                    selectTime();
                    refresh();
                };
                keys.Controls.Add(button, index % 3, index / 3);
            }
            apply = AddDialogButton(applyRow, "Apply time", 128, true, () =>
            {
                TimeSpan time;
                if (!TryReadKeypadTime(digits, target.LocalValue.TimeOfDay, pm, out time))
                {
                    feedback.Text = "Enter hours and minutes, such as 815 or 0430.";
                    return;
                }
                pending = false;
                digits = "";
                target.SetTime(time);
                selectTime();
                refresh();
            });

            Func<Shift> readShift = () =>
            {
                var revised = new Shift { Start = clockIn.ReadUtc().Value, End = clockOutField.ReadUtc() };
                if (original != null)
                    foreach (Break interval in original.Breaks)
                        revised.Breaks.Add(new Break { Start = interval.Start, End = interval.End ?? revised.End });
                if (employee.Inactive && !revised.End.HasValue)
                    throw new FormatException("Enter a clock-out time for an inactive employee.");
                ValidateShift(revised, employee.Shifts.Where((shift, index) => index != shiftIndex));
                return revised;
            };
            refresh = () =>
            {
                try
                {
                    Shift revised = readShift();
                    total.Text = "Worked total   " + Duration(revised.Worked(DateTime.UtcNow));
                    feedback.Text = pending ? "Apply the keypad entry before saving." : "";
                    save.Enabled = !pending;
                }
                catch (FormatException error)
                {
                    total.Text = "Worked total   —";
                    feedback.Text = error.Message;
                    save.Enabled = false;
                }
            };
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill };
            save = AddDialogButton(footer, original == null ? "Save shift" : "Save changes", 142, true, () =>
            {
                try
                {
                    Shift revised = readShift();
                    if (SaveChange(records =>
                    {
                        var shifts = records[employeeIndex].Shifts;
                        if (shiftIndex.HasValue) shifts[shiftIndex.Value] = revised;
                        else shifts.Add(revised);
                    }))
                        dialog.DialogResult = DialogResult.OK;
                }
                catch (FormatException error) { feedback.Text = error.Message; }
            });
            dialog.AcceptButton = save;
            dialog.CancelButton = AddDialogButton(footer, "Cancel", 95, false, dialog.Close);
            layout.Controls.Add(footer, 0, 4);
            activate(clockOutField.HasTime ? clockOutField : clockIn);
            return dialog.ShowDialog(owner) == DialogResult.OK;
        }
    }

    private static bool TryReadKeypadTime(string digits, TimeSpan previous, bool pm, out TimeSpan time)
    {
        int number = digits.Length == 0 ? (previous.Hours % 12 == 0 ? 12 : previous.Hours % 12) * 100 + previous.Minutes : int.Parse(digits, CultureInfo.InvariantCulture);
        int hour = number / 100;
        int minute = number % 100;
        time = TimeSpan.Zero;
        if (hour < 1 || hour > 12 || minute > 59) return false;
        time = TimeSpan.FromMinutes((hour % 12 + (pm ? 12 : 0)) * 60 + minute);
        return true;
    }

    private sealed class DateTimeSelection : Panel
    {
        private readonly DateTime? original;
        private readonly DateTimePicker date = new DateTimePicker();
        private readonly Button time = new Button();
        private readonly CheckBox include;
        private TimeSpan localTime;

        public readonly string FieldName;
        public event Action EditTime;
        public event Action ValueChanged;

        public DateTimeSelection(string label, DateTime? original, DateTime defaultTime, Func<DateTime> startTime = null)
        {
            FieldName = label;
            this.original = original;
            Size = new Size(340, 69);
            Margin = new Padding(3, 0, 3, 2);
            if (startTime == null)
                Controls.Add(new Label { Text = label, AutoSize = true, Top = 3 });
            else
            {
                include = new CheckBox { Text = label, AutoSize = true, Checked = original.HasValue };
                Controls.Add(include);
            }

            date.AccessibleName = label + " date";
            date.Format = DateTimePickerFormat.Custom;
            date.CustomFormat = "MMM d, yyyy";
            date.SetBounds(0, 34, 150, 30);
            Controls.Add(date);
            StyleButton(time, false);
            time.AccessibleName = label + " time";
            time.SetBounds(159, 24, 148, 41);
            Controls.Add(time);
            SetLocalValue(original.HasValue ? original.Value.ToLocalTime() : defaultTime);
            time.Click += (sender, args) => { if (EditTime != null) EditTime(); };
            date.ValueChanged += (sender, args) => NotifyChange();
            if (include != null)
            {
                bool initialized = original.HasValue;
                date.Enabled = time.Enabled = include.Checked;
                include.CheckedChanged += (sender, args) =>
                {
                    if (include.Checked && !initialized)
                    {
                        SetLocalValue(startTime());
                        initialized = true;
                    }
                    date.Enabled = time.Enabled = include.Checked;
                    NotifyChange();
                };
            }
        }

        public bool HasTime { get { return include == null || include.Checked; } }
        public DateTime LocalValue { get { return date.Value.Date + localTime; } }

        public void SetTime(TimeSpan value)
        {
            localTime = value;
            time.Text = LocalValue.ToString("h:mm tt", CultureInfo.InvariantCulture);
            NotifyChange();
        }

        public DateTime? ReadUtc()
        {
            if (!HasTime) return null;
            DateTime local = DateTime.SpecifyKind(LocalValue, DateTimeKind.Unspecified);
            if (original.HasValue &&
                local.Ticks / TimeSpan.TicksPerMinute == original.Value.ToLocalTime().Ticks / TimeSpan.TicksPerMinute)
                return original;
            if (TimeZoneInfo.Local.IsInvalidTime(local) || TimeZoneInfo.Local.IsAmbiguousTime(local))
                throw new FormatException(FieldName + " falls within a daylight saving clock change.");
            return local.ToUniversalTime();
        }

        private void SetLocalValue(DateTime value)
        {
            date.Value = value.Date;
            SetTime(TimeSpan.FromMinutes(value.Hour * 60 + value.Minute));
        }

        private void NotifyChange()
        {
            if (ValueChanged != null) ValueChanged();
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
            if (EmployeePinInUse(pin, -1)) return "That PIN is already used by an employee.";

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
                if (EmployeePinInUse(pin, index)) return "That PIN is already used by an employee.";
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
        return File.Exists(supervisorPath) && MatchesPin(File.ReadAllText(supervisorPath), pin);
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

    private bool EmployeePinInUse(string pin, int exceptIndex)
    {
        return employees.Where((employee, index) => index != exceptIndex)
            .Any(employee => MatchesFourDigitPin(employee.PinCredential, pin));
    }

    private async Task SubmitEmployeePin()
    {
        if (checkingPin) return;
        inactivity.Restart();
        string pin = employeePin.Text;
        employeePin.Clear();
        employeePin.Focus();
        if (pin.Length == 0) return;

        DateTime now = DateTime.UtcNow;
        pinMessage.Text = PinRetryMessage(now);
        if (!string.IsNullOrEmpty(pinMessage.Text)) return;

        checkingPin = true;
        pinView.Enabled = false;
        pinMessage.Text = "Checking PIN…";
        List<Employee> roster = employees;
        bool supervisor;
        int[] employeeMatches;
        try
        {
            supervisor = await Task.Run(() => MatchesSupervisorPin(pin));
            employeeMatches = supervisor ? new int[0] : await Task.Run(() => FindEmployeePinMatches(roster, pin));
        }
        catch (Exception)
        {
            if (!IsDisposed) pinMessage.Text = "Could not check the PIN. Try again.";
            return;
        }
        finally
        {
            checkingPin = false;
            if (!IsDisposed)
            {
                pinView.Enabled = true;
                employeePin.Focus();
                inactivity.Restart();
            }
        }
        if (IsDisposed) return;
        pinMessage.Text = "";

        int match;
        if (supervisor)
        {
            failedAttempts = 0;
            match = employees.FindIndex(employee => employee.IsSupervisor);
            if (match < 0)
            {
                if (!SaveChange(records =>
                {
                    if (records.Any(employee => employee.IsSupervisor)) return;
                    string name = "Supervisor";
                    for (int suffix = 2; records.Any(employee => string.Equals(employee.Name, name, StringComparison.OrdinalIgnoreCase)); suffix++)
                        name = "Supervisor (" + suffix + ")";
                    records.Add(new Employee { Name = name, IsSupervisor = true });
                })) return;
                match = employees.FindIndex(employee => employee.IsSupervisor);
            }
        }
        else
        {
            if (employeeMatches.Length > 1)
            {
                pinMessage.Text = "Ask a supervisor to reset your PIN.";
                return;
            }
            if (employeeMatches.Length == 0)
            {
                pinMessage.Text = RecordFailedPinAttempt(DateTime.UtcNow);
                return;
            }
            match = employeeMatches[0];
        }

        failedAttempts = 0;
        selectedEmployeeIndex = match;
        RefreshEmployee();
        pinView.Visible = false;
        employeeView.Visible = true;
        employeeView.BringToFront();
        punch.Focus();
        inactivity.Restart();
    }

    private static int[] FindEmployeePinMatches(List<Employee> roster, string pin)
    {
        int workers = Math.Max(1, Math.Min(4, Environment.ProcessorCount - 1));
        return Enumerable.Range(0, roster.Count).AsParallel().WithDegreeOfParallelism(workers)
            .Where(index => !roster[index].IsSupervisor && !roster[index].Inactive && MatchesPin(roster[index].PinCredential, pin))
            .Take(2).ToArray();
    }

    private void PunchAndSignOut(bool finishShift)
    {
        int employeeIndex = selectedEmployeeIndex;
        Employee employee = SelectedEmployee;
        if (employee == null) return;

        if (!SaveChange(records =>
        {
            if (employeeIndex >= records.Count || records[employeeIndex].Inactive ||
                records[employeeIndex].Name != employee.Name ||
                records[employeeIndex].IsSupervisor != employee.IsSupervisor ||
                records[employeeIndex].PinCredential != employee.PinCredential)
                throw new InvalidOperationException("Employee access has changed. Sign in again.");

            var shifts = records[employeeIndex].Shifts;
            var shift = shifts.LastOrDefault(s => !s.End.HasValue);
            if (finishShift != (shift != null))
                throw new InvalidOperationException("Clock-in status has changed.");

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
            shift.End = now;
        })) return;

        selectedEmployeeIndex = -1;
        employeeView.Visible = false;
        punchConfirmation.Text = finishShift ? "Clocked out successfully" : "Clocked in successfully";
        punchConfirmation.ForeColor = finishShift ? ClockOutAccent : ClockInAccent;
        punchConfirmation.Visible = true;
        punchConfirmation.BringToFront();
        confirmationTime.Restart();
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
        string retryMessage = PinRetryMessage(now);
        if (retryMessage != null) return retryMessage;

        if (MatchesPin(credential, pin))
        {
            failedAttempts = 0;
            return null;
        }

        return RecordFailedPinAttempt(now);
    }

    private string PinRetryMessage(DateTime now)
    {
        return now < retryAfter ?
            "Too many attempts. Try again in " + Math.Ceiling((retryAfter - now).TotalSeconds) + " seconds." : null;
    }

    private string RecordFailedPinAttempt(DateTime now)
    {
        failedAttempts++;
        if (failedAttempts < 5) return "Incorrect PIN.";
        failedAttempts = 0;
        retryAfter = now.AddSeconds(30);
        return "Too many attempts. Try again in 30 seconds.";
    }

    private static bool MatchesPin(string credential, string pin)
    {
        if (string.IsNullOrEmpty(credential)) return false;
        int limit = PinLengthLimit(credential);
        bool validLength = limit == 4 ? IsValidPin(pin, 4) :
            pin != null && pin.Length >= 6 && pin.Length <= limit && IsNumeric(pin);
        return validLength && VerifyCredential(credential, pin);
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
        Employee employee = SelectedEmployee;
        Shift shift = ActiveShift;
        DateTime now = DateTime.UtcNow;
        employeeName.Text = employee == null ? "" : employee.Name;
        status.Text = shift == null ? "Clocked out" : "Clocked in";
        elapsed.Text = Duration(shift == null ? TimeSpan.Zero : shift.Worked(now));
        details.Text = shift == null ? "Time worked" :
            "Time worked   |   Started " + LocalTime(shift.Start);
        weeklyHours.Text = employee == null ? "" : "This week (Mon–Sun)   " + Duration(WorkedThisWeek(employee, now));
        bool hasPin = employee != null && (employee.IsSupervisor || !string.IsNullOrEmpty(employee.PinCredential));
        supervisorMenu.Visible = employee != null && employee.IsSupervisor;
        punch.Enabled = hasPin;
        punch.Text = shift == null ? "Clock In" : "Clock Out";
        punch.BackColor = shift == null ? ClockInAccent : ClockOutAccent;
        punch.ForeColor = Background;
        status.ForeColor = shift == null ? Muted : Accent;
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
