using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace Kapeyn
{
    // All work-hours settings in one window: on/off, days, any start/end time (overnight allowed),
    // and what happens when work hours end.
    sealed class WorkHoursForm : Form
    {
        readonly Settings settings;
        readonly CheckBox enabled, endOff, endLock, endShutdown;
        readonly CheckBox[] days = new CheckBox[7];
        readonly DateTimePicker from, to;
        readonly Label note;
        readonly Button save;
        readonly Panel body;

        public WorkHoursForm(Settings s)
        {
            settings = s;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            Text = "Kapeyn - Work hours";
            Icon = IconArt.CreateIcon(32, true);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(14);

            var root = Column();
            enabled = new CheckBox { Text = "Keep my PC awake during work hours", AutoSize = true, Checked = s.ScheduleEnabled };
            enabled.Font = new Font(Font, FontStyle.Bold);
            root.Controls.Add(enabled);

            body = Column();
            body.Margin = new Padding(0);

            body.Controls.Add(Heading("Days"));
            var dayRow = Row();
            string[] names = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames; // Sunday first
            for (int i = 1; i <= 7; i++)
            {
                int d = i % 7; // Monday first
                days[d] = new CheckBox { Text = names[d], AutoSize = true, Checked = (s.ScheduleDays & (1 << d)) != 0 };
                dayRow.Controls.Add(days[d]);
            }
            body.Controls.Add(dayRow);

            body.Controls.Add(Heading("Hours"));
            var timeRow = Row();
            from = TimePicker(s.ScheduleStartMin);
            to = TimePicker(s.ScheduleEndMin);
            timeRow.Controls.Add(Caption("From"));
            timeRow.Controls.Add(from);
            timeRow.Controls.Add(Caption("to"));
            timeRow.Controls.Add(to);
            body.Controls.Add(timeRow);
            note = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 3) };
            body.Controls.Add(note);

            body.Controls.Add(Heading("When work hours end"));
            endOff = new CheckBox { Text = "Turn Kapeyn off", AutoSize = true, Checked = s.ScheduleEndTurnsOff };
            endLock = new CheckBox { Text = "Also lock the PC", AutoSize = true, Checked = s.EndLock, Margin = new Padding(24, 3, 3, 3) };
            endShutdown = new CheckBox
            {
                Text = "Also shut down the PC (you get 60 seconds to cancel; apps with unsaved work can stop it)",
                AutoSize = true, Checked = s.EndShutdown, Margin = new Padding(24, 3, 3, 3),
            };
            body.Controls.Add(endOff);
            body.Controls.Add(endLock);
            body.Controls.Add(endShutdown);
            root.Controls.Add(body);

            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill,
                Margin = new Padding(0, 12, 0, 0),
            };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            save = new Button { Text = "Save", AutoSize = true, DialogResult = DialogResult.OK };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(save);
            root.Controls.Add(buttons);
            Controls.Add(root);
            AcceptButton = save;
            CancelButton = cancel;

            enabled.CheckedChanged += delegate { UpdateState(); };
            endOff.CheckedChanged += delegate { UpdateState(); };
            endLock.CheckedChanged += delegate { if (endLock.Checked) endShutdown.Checked = false; };
            endShutdown.CheckedChanged += delegate { if (endShutdown.Checked) endLock.Checked = false; };
            from.ValueChanged += delegate { UpdateState(); };
            to.ValueChanged += delegate { UpdateState(); };
            foreach (var d in days) d.CheckedChanged += delegate { UpdateState(); };
            UpdateState();
        }

        static FlowLayoutPanel Column()
        {
            return new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };
        }

        static FlowLayoutPanel Row()
        {
            return new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Margin = new Padding(0) };
        }

        Label Heading(string text)
        {
            return new Label { Text = text, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 12, 3, 3) };
        }

        static Label Caption(string text)
        {
            return new Label { Text = text, AutoSize = true, Margin = new Padding(3, 7, 3, 3) };
        }

        static DateTimePicker TimePicker(int minutes)
        {
            return new DateTimePicker
            {
                Format = DateTimePickerFormat.Custom,
                CustomFormat = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern,
                ShowUpDown = true,
                Width = 110,
                Value = DateTime.Today.AddMinutes(minutes),
            };
        }

        static int Minutes(DateTimePicker p)
        {
            return p.Value.Hour * 60 + p.Value.Minute;
        }

        int DayMask()
        {
            int mask = 0;
            for (int d = 0; d < 7; d++) if (days[d].Checked) mask |= 1 << d;
            return mask;
        }

        void UpdateState()
        {
            body.Enabled = enabled.Checked;
            endLock.Enabled = endOff.Checked;
            endShutdown.Enabled = endOff.Checked;

            int a = Minutes(from), b = Minutes(to);
            bool ok = true;
            if (a == b) { note.Text = "Start and end can't be the same time."; ok = false; }
            else if (DayMask() == 0) { note.Text = "Pick at least one day."; ok = false; }
            else if (b < a) note.Text = "Overnight: ends at " + Format.TimeOfDay(b) + " the next day (" + Format.Duration((1440 - a + b) * 60) + ").";
            else note.Text = Format.Duration((b - a) * 60) + " a day.";
            save.Enabled = ok || !enabled.Checked;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (DialogResult != DialogResult.OK) return;
            settings.ScheduleEnabled = enabled.Checked;
            int a = Minutes(from), b = Minutes(to);
            if (a != b && DayMask() != 0)
            {
                settings.ScheduleDays = DayMask();
                settings.ScheduleStartMin = a;
                settings.ScheduleEndMin = b;
            }
            settings.ScheduleEndTurnsOff = endOff.Checked;
            settings.EndLock = endOff.Checked && endLock.Checked;
            settings.EndShutdown = endOff.Checked && endShutdown.Checked;
        }
    }

    // "Work hours are over - shutting down in 60 s" with Shut down now / Cancel.
    sealed class ShutdownForm : Form
    {
        readonly Label message;
        readonly Timer timer = new Timer();
        int remaining;

        public ShutdownForm(int seconds)
        {
            remaining = seconds;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            Text = "Kapeyn";
            Icon = IconArt.CreateIcon(32, true);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            TopMost = true;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(16);

            var root = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
            root.Controls.Add(new Label { Text = "Work hours are over.", AutoSize = true, Font = new Font(Font.FontFamily, Font.Size * 1.25f, FontStyle.Bold) });
            message = new Label { AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
            root.Controls.Add(message);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 14, 0, 0) };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            var now = new Button { Text = "Shut down now", AutoSize = true, DialogResult = DialogResult.OK };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(now);
            root.Controls.Add(buttons);
            Controls.Add(root);
            CancelButton = cancel;

            timer.Interval = 1000;
            timer.Tick += delegate
            {
                remaining--;
                UpdateText();
                if (remaining <= 0)
                {
                    timer.Stop();
                    DialogResult = DialogResult.OK;
                }
            };
            UpdateText();
            Shown += delegate { Activate(); cancel.Focus(); timer.Start(); };
        }

        void UpdateText()
        {
            message.Text = "Kapeyn will shut down this PC in " + Math.Max(0, remaining) + " seconds.\nSave your work, or click Cancel to keep working.";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
