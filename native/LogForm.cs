using System;
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
        private readonly TextBox logBox;
        private readonly PictureBox serverIcon;
        private readonly PictureBox vpIcon;
        private readonly PictureBox bcIcon;
        private readonly Label serverText;
        private readonly Label vpText;
        private readonly Label bcText;
        private readonly FileSystemWatcher watcher;
        private readonly Timer statusTimer;
        private long lastPosition;

        public LogForm(string logFile, string statusFile, Icon icon)
        {
            this.logFile = logFile;
            this.statusFile = statusFile;
            Text = "VoicePrompter Bridge - Log";
            Icon = icon;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(900, 580);
            MinimumSize = new Size(620, 380);
            SizeGripStyle = SizeGripStyle.Show;

            TableLayoutPanel statusPanel = new TableLayoutPanel();
            statusPanel.Dock = DockStyle.Top;
            statusPanel.Height = 42;
            statusPanel.ColumnCount = 3;
            statusPanel.RowCount = 1;
            statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34f));
            statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33f));
            statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33f));
            Controls.Add(statusPanel);

            serverIcon = new PictureBox(); serverText = new Label();
            vpIcon = new PictureBox(); vpText = new Label();
            bcIcon = new PictureBox(); bcText = new Label();
            statusPanel.Controls.Add(CreateStatusCell(serverIcon, serverText, "Server: Unknown"), 0, 0);
            statusPanel.Controls.Add(CreateStatusCell(vpIcon, vpText, "VP: Unknown"), 1, 0);
            statusPanel.Controls.Add(CreateStatusCell(bcIcon, bcText, "BC: Unknown"), 2, 0);

            Panel bottom = new Panel(); bottom.Dock = DockStyle.Bottom; bottom.Height = 50; Controls.Add(bottom);
            Button close = new Button(); close.Text = "Close"; close.Anchor = AnchorStyles.Top | AnchorStyles.Right; close.Size = new Size(80, 30); close.Location = new Point(bottom.Width - 95, 10); close.Click += delegate { Close(); }; bottom.Controls.Add(close);
            Button clear = new Button(); clear.Text = "Clear"; clear.Anchor = AnchorStyles.Top | AnchorStyles.Right; clear.Size = new Size(80, 30); clear.Location = new Point(bottom.Width - 183, 10); clear.Click += ClearClick; bottom.Controls.Add(clear);
            bottom.Resize += delegate { close.Left = bottom.ClientSize.Width - 95; clear.Left = bottom.ClientSize.Width - 183; };

            logBox = new TextBox();
            logBox.Dock = DockStyle.Fill;
            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Both;
            logBox.WordWrap = false;
            logBox.AcceptsReturn = true;
            logBox.Font = new Font(FontFamily.GenericMonospace, 9f);
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
            Shown += delegate { BeginInvoke((MethodInvoker)delegate { ScrollToEnd(); }); };
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

        private void WatcherChanged(object sender, FileSystemEventArgs e)
        {
            try { BeginInvoke((MethodInvoker)delegate { AppendNewLogContent(); }); } catch { }
        }

        private static string NormalizeLineEndings(string text)
        {
            if (String.IsNullOrEmpty(text)) return text ?? String.Empty;
            return text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", Environment.NewLine);
        }

        private void LoadWholeLog()
        {
            try
            {
                if (!File.Exists(logFile)) { logBox.Text = ""; lastPosition = 0; return; }
                string text;
                using (FileStream fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader sr = new StreamReader(fs, Encoding.UTF8, true)) text = sr.ReadToEnd();
                logBox.Text = NormalizeLineEndings(text);
                lastPosition = new FileInfo(logFile).Length;
                ScrollToEnd();
            }
            catch { }
        }

        private void AppendNewLogContent()
        {
            try
            {
                if (!File.Exists(logFile)) return;
                bool wasAtEnd = logBox.SelectionStart >= Math.Max(0, logBox.TextLength - 2);
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
                        string add = NormalizeLineEndings(Encoding.UTF8.GetString(ms.ToArray()));
                        if (add.Length > 0) logBox.AppendText(add);
                    }
                }
                if (wasAtEnd) ScrollToEnd();
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
                logBox.Clear(); lastPosition = 0;
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
            SetStatus(serverIcon,serverText,"Server",known,server);
            SetStatus(vpIcon,vpText,"VP",known,vp);
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
