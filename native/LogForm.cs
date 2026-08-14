using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace VPBridgeTray
{
    internal sealed class LogForm : Form
    {
        private readonly string logFile;
        private readonly string statusFile;
        private readonly RichTextBox logBox;
        private readonly PictureBox serverIcon;
        private readonly PictureBox vpIcon;
        private readonly PictureBox bcIcon;
        private readonly Label serverText;
        private readonly Label vpText;
        private readonly Label bcText;
        private readonly FileSystemWatcher watcher;
        private readonly Timer statusTimer;
        private readonly CheckBox autoScrollCheck;
        private readonly CheckBox showPingCheck;
        private readonly CheckBox showVpCheck;
        private readonly CheckBox showBcCheck;
        private readonly TextBox searchBox;
        private readonly Button clearSearchButton;
        private readonly List<string> allLines = new List<string>();
        private long lastPosition;

        public LogForm(string logFile, string statusFile, Icon icon)
        {
            this.logFile = logFile;
            this.statusFile = statusFile;
            Text = "VoicePrompter Bridge - Log";
            Icon = icon;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(900, 620);
            MinimumSize = new Size(700, 420);
            SizeGripStyle = SizeGripStyle.Show;

            Panel topPanel = new Panel();
            topPanel.Dock = DockStyle.Top;
            topPanel.Height = 42;
            Controls.Add(topPanel);

            TableLayoutPanel statusPanel = new TableLayoutPanel();
            statusPanel.Dock = DockStyle.Fill;
            statusPanel.ColumnCount = 3;
            statusPanel.RowCount = 1;
            statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34f));
            statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33f));
            statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33f));
            topPanel.Controls.Add(statusPanel);

            serverIcon = new PictureBox(); serverText = new Label();
            vpIcon = new PictureBox(); vpText = new Label();
            bcIcon = new PictureBox(); bcText = new Label();
            statusPanel.Controls.Add(CreateStatusCell(vpIcon, vpText, "VP: Unknown"), 0, 0);
            statusPanel.Controls.Add(CreateStatusCell(serverIcon, serverText, "Server: Unknown"), 1, 0);
            statusPanel.Controls.Add(CreateStatusCell(bcIcon, bcText, "BC: Unknown"), 2, 0);

            clearSearchButton = new Button();
            clearSearchButton.Text = "×";
            clearSearchButton.Font = new Font(Font.FontFamily, 12f, FontStyle.Bold);
            clearSearchButton.Size = new Size(30, 24);
            clearSearchButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            clearSearchButton.Location = new Point(topPanel.ClientSize.Width - 12 - clearSearchButton.Width, 9);
            clearSearchButton.TabStop = false;
            clearSearchButton.Click += delegate { searchBox.Text = String.Empty; searchBox.Focus(); };
            topPanel.Controls.Add(clearSearchButton);
            clearSearchButton.BringToFront();

            searchBox = new TextBox();
            searchBox.Size = new Size(180, 24);
            searchBox.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            searchBox.Location = new Point(clearSearchButton.Left - searchBox.Width - 4, 10);
            searchBox.TextChanged += delegate { RebuildVisibleLog(false); };
            topPanel.Controls.Add(searchBox);
            searchBox.BringToFront();
            SetCueBanner(searchBox, "Search...");

            topPanel.Resize += delegate
            {
                clearSearchButton.Left = topPanel.ClientSize.Width - 12 - clearSearchButton.Width;
                searchBox.Left = clearSearchButton.Left - searchBox.Width - 4;
            };

            Panel filterPanel = new Panel();
            filterPanel.Dock = DockStyle.Bottom;
            filterPanel.Height = 42;
            Controls.Add(filterPanel);

            FlowLayoutPanel filters = new FlowLayoutPanel();
            filters.Dock = DockStyle.Left;
            filters.AutoSize = true;
            filters.WrapContents = false;
            filters.Padding = new Padding(12, 10, 0, 0);
            filterPanel.Controls.Add(filters);

            autoScrollCheck = CreateFilterCheckBox("Always at end", true);
            showPingCheck = CreateFilterCheckBox("Show ping", true);
            showVpCheck = CreateFilterCheckBox("VP messages", true);
            showBcCheck = CreateFilterCheckBox("BC messages", true);
            filters.Controls.Add(autoScrollCheck);
            filters.Controls.Add(showPingCheck);
            filters.Controls.Add(showVpCheck);
            filters.Controls.Add(showBcCheck);

            autoScrollCheck.CheckedChanged += delegate { if (autoScrollCheck.Checked) ScrollToEnd(); };
            showPingCheck.CheckedChanged += FilterChanged;
            showVpCheck.CheckedChanged += FilterChanged;
            showBcCheck.CheckedChanged += FilterChanged;

            Button close = new Button(); close.Text = "Close"; close.Anchor = AnchorStyles.Top | AnchorStyles.Right; close.Size = new Size(80, 30); close.Location = new Point(filterPanel.Width - 95, 6); close.Click += delegate { Close(); }; filterPanel.Controls.Add(close);
            Button clear = new Button(); clear.Text = "Clear"; clear.Anchor = AnchorStyles.Top | AnchorStyles.Right; clear.Size = new Size(80, 30); clear.Location = new Point(filterPanel.Width - 183, 6); clear.Click += ClearClick; filterPanel.Controls.Add(clear);
            filterPanel.Resize += delegate { close.Left = filterPanel.ClientSize.Width - 95; clear.Left = filterPanel.ClientSize.Width - 183; };

            logBox = new RichTextBox();
            logBox.Dock = DockStyle.Fill;
            logBox.ReadOnly = true;
            logBox.WordWrap = false;
            logBox.DetectUrls = false;
            logBox.Font = new Font(FontFamily.GenericMonospace, 9f);
            logBox.BackColor = SystemColors.Window;
            logBox.ForeColor = SystemColors.WindowText;
            Controls.Add(logBox);
            logBox.BringToFront();

            LoadWholeLog();
            string dir = Path.GetDirectoryName(logFile); if (String.IsNullOrEmpty(dir)) dir = "."; Directory.CreateDirectory(dir);
            watcher = new FileSystemWatcher(dir, Path.GetFileName(logFile));
            watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName;
            watcher.Changed += WatcherChanged;
            watcher.Created += WatcherChanged;
            watcher.EnableRaisingEvents = true;

            statusTimer = new Timer(); statusTimer.Interval = 500; statusTimer.Tick += delegate { RefreshStatus(); }; statusTimer.Start(); RefreshStatus();
            Shown += delegate { BeginInvoke((MethodInvoker)delegate { if (autoScrollCheck.Checked) ScrollToEnd(); }); };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (watcher != null) watcher.Dispose();
                if (statusTimer != null) statusTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        private static CheckBox CreateFilterCheckBox(string text, bool value)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.Checked = value;
            c.AutoSize = true;
            c.Margin = new Padding(0, 0, 18, 0);
            return c;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        private static void SetCueBanner(TextBox box, string text)
        {
            const int EM_SETCUEBANNER = 0x1501;
            try { SendMessage(box.Handle, EM_SETCUEBANNER, (IntPtr)1, text); } catch { }
        }

        private static Control CreateStatusCell(PictureBox icon, Label label, string initialText)
        {
            FlowLayoutPanel cell = new FlowLayoutPanel();
            cell.Dock = DockStyle.Fill;
            cell.FlowDirection = FlowDirection.LeftToRight;
            cell.WrapContents = false;
            cell.Padding = new Padding(10, 10, 0, 0);
            cell.Margin = Padding.Empty;
            icon.Size = new Size(14, 14);
            icon.SizeMode = PictureBoxSizeMode.StretchImage;
            icon.Margin = new Padding(0, 0, 7, 0);
            icon.Image = UiIcons.Create(UiIconKind.Disconnected, 14);
            label.Text = initialText;
            label.AutoSize = true;
            label.Margin = new Padding(0, 1, 0, 0);
            cell.Controls.Add(icon);
            cell.Controls.Add(label);
            return cell;
        }

        private void FilterChanged(object sender, EventArgs e)
        {
            RebuildVisibleLog(false);
        }

        private void WatcherChanged(object sender, FileSystemEventArgs e)
        {
            try { BeginInvoke((MethodInvoker)delegate { AppendNewLogContent(); }); } catch { }
        }

        private static string NormalizeLineEndings(string text)
        {
            if (String.IsNullOrEmpty(text)) return text ?? String.Empty;
            return text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", Environment.NewLine);
        }

        private static Color LogColor(string line)
        {
            if (line.IndexOf("VP→BC", StringComparison.Ordinal) >= 0) return Color.FromArgb(0, 92, 153);
            if (line.IndexOf("BC→VP", StringComparison.Ordinal) >= 0) return Color.FromArgb(126, 70, 160);
            if (line.IndexOf("VP→SERVER", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("SERVER→VP", StringComparison.OrdinalIgnoreCase) >= 0) return Color.FromArgb(0, 122, 104);
            if (line.IndexOf("BC→SERVER", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("SERVER→BC", StringComparison.OrdinalIgnoreCase) >= 0) return Color.FromArgb(174, 91, 0);
            return SystemColors.WindowText;
        }

        private static bool IsPingLine(string line)
        {
            return line.IndexOf("→SERVER", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   line.IndexOf("SERVER→", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsVpLine(string line)
        {
            return line.IndexOf("VP→", StringComparison.Ordinal) >= 0 ||
                   line.IndexOf("→VP", StringComparison.Ordinal) >= 0;
        }

        private static bool IsBcLine(string line)
        {
            return line.IndexOf("BC→", StringComparison.Ordinal) >= 0 ||
                   line.IndexOf("→BC", StringComparison.Ordinal) >= 0;
        }

        private bool ShouldShowLine(string line)
        {
            if (!showPingCheck.Checked && IsPingLine(line)) return false;

            bool vp = IsVpLine(line);
            bool bc = IsBcLine(line);
            if (vp && !bc && !showVpCheck.Checked) return false;
            if (bc && !vp && !showBcCheck.Checked) return false;
            if (vp && bc && !showVpCheck.Checked && !showBcCheck.Checked) return false;

            string query = searchBox.Text.Trim();
            if (query.Length > 0 && line.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) return false;
            return true;
        }

        private static IEnumerable<string> SplitLines(string text)
        {
            string normalized = NormalizeLineEndings(text);
            if (normalized.Length == 0) yield break;
            string[] lines = normalized.Split(new[] { Environment.NewLine }, StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
            {
                if (i == lines.Length - 1 && lines[i].Length == 0) continue;
                yield return lines[i];
            }
        }

        private void AppendVisibleLine(string line)
        {
            if (!ShouldShowLine(line)) return;
            logBox.SelectionStart = logBox.TextLength;
            logBox.SelectionLength = 0;
            logBox.SelectionColor = LogColor(line);
            logBox.AppendText(line + Environment.NewLine);
            logBox.SelectionColor = SystemColors.WindowText;
        }

        private void AddRawText(string text)
        {
            foreach (string line in SplitLines(text))
            {
                allLines.Add(line);
                AppendVisibleLine(line);
            }
        }

        private void RebuildVisibleLog(bool forceScroll)
        {
            int oldSelection = logBox.SelectionStart;
            logBox.SuspendLayout();
            try
            {
                logBox.Clear();
                foreach (string line in allLines) AppendVisibleLine(line);
                if (forceScroll || autoScrollCheck.Checked) ScrollToEnd();
                else logBox.SelectionStart = Math.Min(oldSelection, logBox.TextLength);
            }
            finally { logBox.ResumeLayout(); }
        }

        private void LoadWholeLog()
        {
            try
            {
                allLines.Clear();
                logBox.Clear();
                if (!File.Exists(logFile)) { lastPosition = 0; return; }
                string text;
                using (FileStream fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader sr = new StreamReader(fs, Encoding.UTF8, true)) text = sr.ReadToEnd();
                AddRawText(text);
                lastPosition = new FileInfo(logFile).Length;
                if (autoScrollCheck.Checked) ScrollToEnd();
            }
            catch { }
        }

        private void AppendNewLogContent()
        {
            try
            {
                if (!File.Exists(logFile)) return;
                using (FileStream fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length < lastPosition) { LoadWholeLog(); return; }
                    long snapshotEnd = fs.Length;
                    long remaining = snapshotEnd - lastPosition;
                    if (remaining <= 0) return;
                    fs.Seek(lastPosition, SeekOrigin.Begin);
                    using (MemoryStream ms = new MemoryStream())
                    {
                        byte[] buffer = new byte[8192];
                        while (remaining > 0)
                        {
                            int wanted = (int)Math.Min((long)buffer.Length, remaining);
                            int read = fs.Read(buffer, 0, wanted);
                            if (read <= 0) break;
                            ms.Write(buffer, 0, read);
                            remaining -= read;
                        }
                        long bytesRead = ms.Length;
                        if (bytesRead <= 0) return;
                        lastPosition += bytesRead;
                        string add = Encoding.UTF8.GetString(ms.ToArray());
                        if (add.Length > 0) AddRawText(add);
                    }
                }
                if (autoScrollCheck.Checked) ScrollToEnd();
            }
            catch { }
        }

        private void ScrollToEnd() { logBox.SelectionStart = logBox.TextLength; logBox.SelectionLength = 0; logBox.ScrollToCaret(); }

        private void ClearClick(object sender, EventArgs e)
        {
            if (!ConfirmClear()) return;
            try
            {
                string dir = Path.GetDirectoryName(logFile); if (String.IsNullOrEmpty(dir)) dir = ".";
                Directory.CreateDirectory(dir);
                using (FileStream fs = new FileStream(logFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { }
                allLines.Clear();
                logBox.Clear();
                lastPosition = 0;
            }
            catch (Exception ex) { MessageBox.Show("Could not clear the log:\r\n" + ex.Message, "VPBridge Log", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private bool ConfirmClear()
        {
            using (Form f = new Form())
            {
                f.Text = "Clear log"; f.FormBorderStyle = FormBorderStyle.FixedDialog; f.StartPosition = FormStartPosition.CenterParent; f.ClientSize = new Size(360,130); f.MaximizeBox=false; f.MinimizeBox=false;
                Label l=new Label(); l.Text="Are you sure you want to clear the log?"; l.Location=new Point(25,25); l.AutoSize=true; f.Controls.Add(l);
                Button yes=new Button(); yes.Text="Yes"; yes.DialogResult=DialogResult.Yes; yes.Location=new Point(185,75); yes.Size=new Size(70,28); f.Controls.Add(yes);
                Button cancel=new Button(); cancel.Text="Cancel"; cancel.DialogResult=DialogResult.Cancel; cancel.Location=new Point(265,75); cancel.Size=new Size(70,28); f.Controls.Add(cancel);
                f.AcceptButton=yes; f.CancelButton=cancel;
                return f.ShowDialog(this)==DialogResult.Yes;
            }
        }

        private void RefreshStatus()
        {
            bool server=false, vp=false, bc=false, known=false;
            try
            {
                if (File.Exists(statusFile))
                {
                    JavaScriptSerializer js=new JavaScriptSerializer();
                    RuntimeStatus st=js.Deserialize<RuntimeStatus>(File.ReadAllText(statusFile,Encoding.UTF8));
                    if (st!=null) { server=st.serverRunning; vp=st.vpConnected; bc=st.bcConnected; known=true; }
                }
            }
            catch { }
            SetStatus(vpIcon,vpText,"VP",known,vp);
            SetStatus(serverIcon,serverText,"Server",known,server);
            SetStatus(bcIcon,bcText,"BC",known,bc);
        }

        private static void SetStatus(PictureBox p, Label l, string name, bool known, bool value)
        {
            if (p.Image != null) p.Image.Dispose();
            p.Image=UiIcons.Create(known && value ? UiIconKind.Connected : UiIconKind.Disconnected,14);
            l.Text=name+": "+(!known ? "Unknown" : (value ? "Connected" : "Disconnected"));
            if (name=="Server") l.Text=name+": "+(!known ? "Unknown" : (value ? "Running" : "Stopped"));
        }

        private sealed class RuntimeStatus
        {
            public bool serverRunning { get; set; }
            public bool vpConnected { get; set; }
            public bool bcConnected { get; set; }
        }
    }
}
