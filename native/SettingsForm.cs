using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Windows.Forms;

namespace VPBridgeTray
{
    internal sealed class SettingsForm : Form
    {
        private readonly string configFile;
        private readonly Action onSaved;
        private readonly Func<bool> isServerRunning;
        private readonly BridgeConfig original;
        private readonly RadioButton localRadio;
        private readonly RadioButton allRadio;
        private readonly NumericUpDown portBox;
        private readonly TextBox apiKeyBox;
        private readonly Button copyButton;
        private readonly Button regenerateButton;
        private readonly Button saveButton;
        private readonly Button cancelButton;
        private readonly ToolTip tips = new ToolTip();

        public SettingsForm(string configFile, Func<bool> isServerRunning, Action onSaved, Icon icon)
        {
            this.configFile = configFile;
            this.onSaved = onSaved;
            this.isServerRunning = isServerRunning;
            this.original = BridgeConfig.Load(configFile);

            Text = "VoicePrompter Bridge - Settings";
            Icon = icon;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(500, 330);

            Label networkTitle = Title("Network", 20, 18);
            Controls.Add(networkTitle);

            localRadio = new RadioButton(); localRadio.Text = "Local only (127.0.0.1)"; localRadio.Location = new Point(30, 55); localRadio.AutoSize = true;
            allRadio = new RadioButton(); allRadio.Text = "All Interfaces (0.0.0.0)"; allRadio.Location = new Point(260, 55); allRadio.AutoSize = true;
            Controls.Add(localRadio); Controls.Add(allRadio);

            Label portLabel = new Label(); portLabel.Text = "Port"; portLabel.Location = new Point(30, 92); portLabel.AutoSize = true;
            portBox = new NumericUpDown(); portBox.Minimum = 1; portBox.Maximum = 65535; portBox.Location = new Point(100, 88); portBox.Width = 110;
            Controls.Add(portLabel); Controls.Add(portBox);

            Label secTitle = Title("Security", 20, 135); Controls.Add(secTitle);
            Label keyLabel = new Label(); keyLabel.Text = "API key (required for All Interfaces)"; keyLabel.Location = new Point(30, 170); keyLabel.AutoSize = true; Controls.Add(keyLabel);
            apiKeyBox = new TextBox(); apiKeyBox.Location = new Point(30, 194); apiKeyBox.Width = 355; apiKeyBox.ReadOnly = true; apiKeyBox.Font = new Font(FontFamily.GenericMonospace, 9f); Controls.Add(apiKeyBox);

            copyButton = new Button(); copyButton.Location = new Point(395, 191); copyButton.Size = new Size(36, 28); copyButton.Image = UiIcons.Create(UiIconKind.Copy, 18); copyButton.Click += CopyClick; tips.SetToolTip(copyButton, "Copy API key to clipboard"); Controls.Add(copyButton);
            regenerateButton = new Button(); regenerateButton.Location = new Point(437, 191); regenerateButton.Size = new Size(36, 28); regenerateButton.Image = UiIcons.Create(UiIconKind.Regenerate, 18); regenerateButton.Click += RegenerateClick; tips.SetToolTip(regenerateButton, "Generate a new API key"); Controls.Add(regenerateButton);

            Label note = new Label(); note.Text = "WS:// is used. In Local only mode the API key is not required for connections."; note.Location = new Point(30, 230); note.Size = new Size(440, 36); note.ForeColor = Color.DimGray; Controls.Add(note);

            saveButton = new Button(); saveButton.Text = "Save"; saveButton.Location = new Point(315, 282); saveButton.Size = new Size(75, 30); saveButton.Click += SaveClick; Controls.Add(saveButton);
            cancelButton = new Button(); cancelButton.Text = "Cancel"; cancelButton.Location = new Point(398, 282); cancelButton.Size = new Size(75, 30); cancelButton.Click += delegate { Close(); }; Controls.Add(cancelButton);
            AcceptButton = saveButton; CancelButton = cancelButton;

            localRadio.Checked = original.server.mode != "all";
            allRadio.Checked = original.server.mode == "all";
            portBox.Value = original.server.port;
            apiKeyBox.Text = BridgeConfig.IsValidApiKey(original.security.apiKey) ? original.security.apiKey : BridgeConfig.GenerateApiKey();
        }

        private static Label Title(string text, int x, int y)
        {
            Label l = new Label(); l.Text = text; l.Location = new Point(x,y); l.AutoSize = true; l.Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold); return l;
        }

        private void CopyClick(object sender, EventArgs e)
        {
            try { Clipboard.SetText(apiKeyBox.Text); } catch { }
        }

        private void RegenerateClick(object sender, EventArgs e)
        {
            apiKeyBox.Text = BridgeConfig.GenerateApiKey();
        }

        private void SaveClick(object sender, EventArgs e)
        {
            int port = (int)portBox.Value;
            string mode = allRadio.Checked ? "all" : "local";
            string key = apiKeyBox.Text.Trim();

            if (mode == "all" && !BridgeConfig.IsValidApiKey(key))
            {
                MessageBox.Show("A valid API key is required for All Interfaces.", "VPBridge Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!PortAvailableConsideringCurrent(mode, port))
            {
                MessageBox.Show("Port " + port + " is already in use by another service.\r\n\r\nPlease choose another port.", "VPBridge Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            BridgeConfig cfg = BridgeConfig.Load(configFile);
            cfg.server.mode = mode;
            cfg.server.host = mode == "all" ? "0.0.0.0" : "127.0.0.1";
            cfg.server.port = port;
            cfg.security.apiKey = key;

            try
            {
                cfg.Save(configFile);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not save configuration:\r\n" + ex.Message, "VPBridge Settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (onSaved != null) onSaved();
            Close();
        }

        private bool PortAvailableConsideringCurrent(string mode, int port)
        {
            if (isServerRunning() && original.server.port == port)
            {
                try
                {
                    IPEndPoint[] listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
                    foreach (IPEndPoint ep in listeners)
                    {
                        if (ep.Port != port) continue;
                        bool ours = original.server.mode == "all"
                            ? ep.Address.Equals(IPAddress.Any) || ep.Address.Equals(IPAddress.IPv6Any)
                            : ep.Address.Equals(IPAddress.Loopback) || ep.Address.Equals(IPAddress.IPv6Loopback);
                        if (!ours) return false;
                    }
                    return true;
                }
                catch { return true; }
            }

            TcpListener listener = null;
            try
            {
                listener = new TcpListener(mode == "all" ? IPAddress.Any : IPAddress.Loopback, port);
                listener.ExclusiveAddressUse = true;
                listener.Start();
                return true;
            }
            catch { return false; }
            finally { if (listener != null) try { listener.Stop(); } catch { } }
        }
    }
}
