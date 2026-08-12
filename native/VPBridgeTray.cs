using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace VPBridgeTray
{
    internal enum BridgeState { Running, Stopped, Error }

    internal sealed class TrayApplicationContext : ApplicationContext
    {
        private readonly string baseDir;
        private readonly string runtimeDir;
        private readonly string serverExe;
        private readonly string serverScript;
        private readonly string trayLogFile;
        private readonly string mainLogFile;
        private readonly string statusFile;
        private readonly string configFile;
        private readonly NotifyIcon notifyIcon;
        private readonly Icon appIcon;
        private readonly ContextMenuStrip menu;
        private readonly Form menuOwner;
        private readonly ToolStripMenuItem stateItem;
        private readonly ToolStripMenuItem startItem;
        private readonly ToolStripMenuItem stopItem;
        private readonly ToolStripMenuItem restartItem;
        private readonly ToolStripMenuItem settingsItem;
        private readonly ToolStripMenuItem viewLogItem;
        private readonly ToolStripMenuItem exitItem;
        private readonly System.Windows.Forms.Timer processTimer;
        private Process serverProcess;
        private BridgeState state = BridgeState.Stopped;
        private bool exiting;
        private SettingsForm settingsForm;
        private LogForm logForm;

        public TrayApplicationContext()
        {
            baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            runtimeDir = Path.Combine(baseDir, "runtime");
            serverExe = Path.Combine(runtimeDir, "VPBridge.Server.exe");
            serverScript = Path.Combine(baseDir, "dist", "main.js");
            trayLogFile = Path.Combine(baseDir, "logs", "vpbridge-tray.log");
            mainLogFile = Path.Combine(baseDir, "logs", "vpbridge.log");
            statusFile = Path.Combine(runtimeDir, "status.json");
            configFile = Path.Combine(baseDir, "config", "vpbridge.json");

            Directory.CreateDirectory(runtimeDir);
            Directory.CreateDirectory(Path.Combine(baseDir, "logs"));

            try { appIcon = UiIcons.CreateAppIcon(32); }
            catch { appIcon = (Icon)SystemIcons.Application.Clone(); }

            menu = new ContextMenuStrip();
            menu.AutoClose = true;
            menuOwner = new Form();
            menuOwner.FormBorderStyle = FormBorderStyle.None;
            menuOwner.ShowInTaskbar = false;
            menuOwner.StartPosition = FormStartPosition.Manual;
            menuOwner.Size = new Size(1, 1);
            menuOwner.Opacity = 0.01;
            menuOwner.TopMost = true;
            menuOwner.Deactivate += delegate { if (menu.Visible && !menu.Bounds.Contains(Cursor.Position)) menu.Close(ToolStripDropDownCloseReason.AppClicked); };
            menu.Closed += delegate { if (menuOwner.Visible) menuOwner.Hide(); };

            stateItem = new ToolStripMenuItem("Stopped", UiIcons.Create(UiIconKind.Stopped, 20)); stateItem.Enabled = false;
            startItem = new ToolStripMenuItem("Start", UiIcons.Create(UiIconKind.Start, 20), delegate { StartServer(); });
            stopItem = new ToolStripMenuItem("Stop", UiIcons.Create(UiIconKind.Stop, 20), delegate { StopServer(false); });
            restartItem = new ToolStripMenuItem("Restart", UiIcons.Create(UiIconKind.Restart, 20), delegate { RestartServer(); });
            settingsItem = new ToolStripMenuItem("Settings", UiIcons.Create(UiIconKind.Settings, 20), delegate { ShowSettings(); });
            viewLogItem = new ToolStripMenuItem("View log", UiIcons.Create(UiIconKind.Log, 20), delegate { ShowLog(); });
            exitItem = new ToolStripMenuItem("Exit", UiIcons.Create(UiIconKind.Exit, 20), delegate { ExitBridge(); });

            menu.Items.Add(new ToolStripMenuItem("VoicePrompter Bridge v0.6.6") { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(stateItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(startItem); menu.Items.Add(stopItem); menu.Items.Add(restartItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(settingsItem); menu.Items.Add(viewLogItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            notifyIcon = new NotifyIcon();
            notifyIcon.Icon = appIcon;
            notifyIcon.Text = "VoicePrompter Bridge v0.6.6";
            notifyIcon.Visible = true;
            notifyIcon.MouseClick += NotifyIconMouseClick;

            processTimer = new System.Windows.Forms.Timer(); processTimer.Interval = 1000; processTimer.Tick += ProcessTimerTick; processTimer.Start();
            Log("VPBridge tray v0.6.6 started, PID " + Process.GetCurrentProcess().Id);
            StartServer();
        }

        private void NotifyIconMouseClick(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right) ShowTrayMenu(); }

        private void ShowTrayMenu()
        {
            if (menu.Visible) { menu.Close(ToolStripDropDownCloseReason.AppClicked); return; }
            Point cursor = Cursor.Position; menuOwner.Location = cursor;
            if (!menuOwner.Visible) menuOwner.Show();
            menuOwner.Activate(); menu.Show(cursor);
        }

        private void ProcessTimerTick(object sender, EventArgs e)
        {
            if (serverProcess != null && serverProcess.HasExited)
            {
                int exitCode = serverProcess.ExitCode; serverProcess.Dispose(); serverProcess = null;
                WriteStoppedRuntimeStatus();
                if (!exiting && state == BridgeState.Running) SetState(BridgeState.Error, "Server exited unexpectedly (code " + exitCode + ")");
            }
        }

        private void StartServer()
        {
            if (serverProcess != null && !serverProcess.HasExited) { SetState(BridgeState.Running, null); return; }
            try
            {
                EnsureServerRuntime();
                if (!File.Exists(serverScript)) throw new FileNotFoundException("Missing dist\\main.js. Run npm run build first.", serverScript);
                if (!File.Exists(configFile)) throw new FileNotFoundException("Missing config\\vpbridge.json.", configFile);

                ProcessStartInfo psi = new ProcessStartInfo(); psi.FileName = serverExe; psi.Arguments = "\"" + serverScript + "\""; psi.WorkingDirectory = baseDir; psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.WindowStyle = ProcessWindowStyle.Hidden;
                serverProcess = Process.Start(psi);
                if (serverProcess == null) throw new InvalidOperationException("Server process could not be started.");
                Thread.Sleep(300);
                if (serverProcess.HasExited)
                {
                    int code = serverProcess.ExitCode; serverProcess.Dispose(); serverProcess = null;
                    throw new InvalidOperationException("Server stopped immediately after start. Exit code: " + code + ". Use VPBridge-Debug.cmd for details.");
                }
                SetState(BridgeState.Running, "Server STARTED, PID " + serverProcess.Id);
            }
            catch (Exception ex) { SetState(BridgeState.Error, ex.Message); ShowError(ex.Message); }
        }

        private void StopServer(bool silent)
        {
            try
            {
                if (serverProcess != null)
                {
                    if (!serverProcess.HasExited)
                    {
                        int pid = serverProcess.Id; serverProcess.Kill(); serverProcess.WaitForExit(2000); Log("Server STOPPED, PID " + pid);
                    }
                    serverProcess.Dispose(); serverProcess = null;
                }
                WriteStoppedRuntimeStatus();
                if (!silent) SetState(BridgeState.Stopped, null);
            }
            catch (Exception ex) { SetState(BridgeState.Error, "STOP failed: " + ex.Message); if (!silent) ShowError(ex.Message); }
        }

        private void RestartServer()
        {
            Log("RESTART requested - RAM queues will be discarded and config reloaded");
            StopServer(true); SetState(BridgeState.Stopped, null); StartServer();
        }

        private void ShowSettings()
        {
            if (settingsForm != null && !settingsForm.IsDisposed) { settingsForm.Activate(); return; }
            settingsForm = new SettingsForm(configFile, IsServerRunning, delegate { Log("Settings saved - restarting server"); RestartServer(); }, appIcon);
            settingsForm.FormClosed += delegate { settingsForm = null; };
            settingsForm.Show(); settingsForm.Activate();
        }

        private void ShowLog()
        {
            if (logForm != null && !logForm.IsDisposed) { logForm.Activate(); return; }
            logForm = new LogForm(mainLogFile, statusFile, appIcon);
            logForm.FormClosed += delegate { logForm = null; };
            logForm.Show(); logForm.Activate();
        }

        private bool IsServerRunning() { return serverProcess != null && !serverProcess.HasExited && state == BridgeState.Running; }

        private void ExitBridge()
        {
            exiting = true; Log("EXIT requested"); processTimer.Stop(); StopServer(true);
            if (settingsForm != null && !settingsForm.IsDisposed) settingsForm.Close();
            if (logForm != null && !logForm.IsDisposed) logForm.Close();
            notifyIcon.Visible = false; notifyIcon.Dispose(); appIcon.Dispose(); menu.Dispose(); menuOwner.Dispose(); ExitThread();
        }

        private void EnsureServerRuntime()
        {
            if (File.Exists(serverExe)) return;
            string nodeExe = FindNodeExe();
            if (nodeExe == null) throw new InvalidOperationException("Node.js was not found. Install Node.js or ensure node.exe is available in PATH.");
            File.Copy(nodeExe, serverExe, true); Log("Created private Node runtime: runtime\\VPBridge.Server.exe from " + nodeExe);
        }

        private static string FindNodeExe()
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in path.Split(Path.PathSeparator))
            {
                try { string clean = dir.Trim().Trim('"'); if (clean.Length == 0) continue; string candidate = Path.Combine(clean, "node.exe"); if (File.Exists(candidate)) return candidate; } catch { }
            }
            string[] common = new string[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "node.exe") };
            return common.FirstOrDefault(File.Exists);
        }

        private void SetState(BridgeState newState, string detail)
        {
            state = newState; string text = state == BridgeState.Running ? "Running" : (state == BridgeState.Error ? "Error" : "Stopped");
            stateItem.Text = text;
            if (stateItem.Image != null) stateItem.Image.Dispose();
            stateItem.Image = UiIcons.Create(state == BridgeState.Running ? UiIconKind.Running : (state == BridgeState.Error ? UiIconKind.Error : UiIconKind.Stopped), 20);
            startItem.Enabled = state != BridgeState.Running; stopItem.Enabled = state == BridgeState.Running; restartItem.Enabled = true;
            if (detail != null) Log(text + ": " + detail); else Log("State: " + text);
            try { notifyIcon.Text = "VoicePrompter Bridge - " + text; } catch { }
        }

        private void ShowError(string message)
        {
            notifyIcon.BalloonTipTitle = "VPBridge ERROR"; notifyIcon.BalloonTipText = message; notifyIcon.BalloonTipIcon = ToolTipIcon.Error; notifyIcon.ShowBalloonTip(5000);
        }

        private void WriteStoppedRuntimeStatus()
        {
            try
            {
                BridgeConfig cfg = BridgeConfig.Load(configFile);
                string json = "{\r\n  \"serverRunning\": false,\r\n  \"vpConnected\": false,\r\n  \"bcConnected\": false,\r\n  \"host\": \"" + cfg.server.host + "\",\r\n  \"port\": " + cfg.server.port + "\r\n}\r\n";
                Directory.CreateDirectory(runtimeDir); File.WriteAllText(statusFile, json);
            }
            catch { }
        }

        private void Log(string message)
        {
            try { string line = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss.fff") + "  TRAY  " + message + Environment.NewLine; File.AppendAllText(trayLogFile, line); } catch { }
        }
    }

    internal static class Program
    {
        private static Mutex mutex;
        [STAThread]
        private static void Main()
        {
            bool createdNew; mutex = new Mutex(true, "Local\\VoicePrompterBridge.Tray.v0.3", out createdNew);
            if (!createdNew) { MessageBox.Show("VoicePrompter Bridge is already running.", "VPBridge", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            try { Application.Run(new TrayApplicationContext()); }
            finally { if (mutex != null) { mutex.ReleaseMutex(); mutex.Dispose(); } }
        }
    }
}
