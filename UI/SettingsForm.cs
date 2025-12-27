using ASCOM.Utilities;
using Pulsar_DomeDriver.Config;
using System;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;
using System.IO;

namespace Pulsar_DomeDriver.UI
{
    public partial class SettingsForm : Form
    {
        private ConfigManager _config;
        public readonly Profile _profile;

        public SettingsForm(ConfigManager config)
        {
            InitializeComponent();
            _config = config;
            _profile = config._profile;
        }

        private void SettingsForm_Load(object sender, EventArgs e)
        {

            var ports = System.IO.Ports.SerialPort.GetPortNames();
            comboBoxSerialPort.Items.AddRange(ports);

            var _port = _config.SerialPort;

            if (_port != null && !ports.Contains(_port))
            {
                comboBoxSerialPort.Items.Insert(0, _port + " (Unavailable)");
                comboBoxSerialPort.SelectedIndex = 0;
            }

            if (_port != null)
            {
                comboBoxSerialPort.Text = _config.SerialPort;
            }
            else
            {
                comboBoxSerialPort.Text = "";
            }

            txtBoxLogLocation.Text = _config.LogLocation;

            chkBoxMQTT.Checked = _config.UseMQTT;

            chkBoxDebugLog.Checked = _config.DebugLog;
            chkBoxTraceLog.Checked = _config.TraceLog;
            chkBoxHomePark.Checked = _config.HomePark;

            chkBoxGNS.Checked = _config.UseGNS;
            txtBoxGNSLocation.Enabled = _config.UseGNS;
            txtBoxGNSLocation.Text = _config.GNSPath;
            txtBoxGNSDispatherLocation.Text = _config.GNSDispatcherPath;
            txtBoxGNSDispatherLocation.Enabled = chkBoxGNS.Checked;

            checkBoxInternalReset.Checked = _config.SoftReset;
            checkBoxExternalReset.Checked = _config.HardReset;
            txtBoxExternalExe.Text = _config.ResetExe;
            txtBoxExternalExe.Enabled = checkBoxExternalReset.Checked;

            btnReset.Enabled = checkBoxExternalReset.Checked;
            btnTestResetOn.Enabled = checkBoxExternalReset.Checked;
            btnTestResetOff.Enabled = checkBoxExternalReset.Checked;

            txtBoxOnParameters.Enabled = checkBoxExternalReset.Checked;
            txtBoxOnParameters.Text = _config.ResetOnParameters;

            txtBoxOffParameters.Text = _config.ResetOffParameters;
            txtBoxOffParameters.Enabled = checkBoxExternalReset.Checked;

            txtBoxShutterTimeout.Text = _config.ShutterTimeout.ToString();
            txtBoxRotationTimeout.Text = _config.RotationTimeout.ToString();
            txtBoxResetDelay.Text = (_config.ResetDelay / 1000).ToString();
            txtBoxCycleDelay.Text = (_config.CycleDelay / 1000).ToString();

            txtBoxCycleDelay.Enabled = checkBoxExternalReset.Checked;

            txtBoxMQTTip.Text = _config.MQTTip;
            txtBoxMQTTport.Text = _config.MQTTport;

            chkBoxUseLocalhost.Checked = _config.MQTTLocalHost;
            chkBoxUseLocalhost.Enabled = chkBoxMQTT.Checked;
            
            // Ensure consistency: if localhost is checked, IP should be localhost
            if (chkBoxUseLocalhost.Checked)
            {
                txtBoxMQTTip.Text = "localhost";
                txtBoxMQTTport.Text = "1883";
            }
            
            txtBoxMQTTip.Enabled = chkBoxMQTT.Checked && !chkBoxUseLocalhost.Checked;
            txtBoxMQTTport.Enabled = chkBoxMQTT.Checked && !chkBoxUseLocalhost.Checked;
        }

        private void btnOK_Click(object sender, EventArgs e)
        {
            if (!TryReadInt(txtBoxResetDelay, "Reboot Delay (s)", 0, null, out int resetDelay))
                return;
            if (!TryReadInt(txtBoxShutterTimeout, "Shutter Timeout (s)", 10, 600, out int shutterTimeout))
                return;
            if (!TryReadInt(txtBoxRotationTimeout, "Rotation Timeout (s)", 10, 600, out int rotationTimeout))
                return;
            if (!TryReadInt(txtBoxCycleDelay, "Cycle Delay (s)", 0, null, out int cycleDelay))
                return;

            _config.SerialPort = comboBoxSerialPort.Text;
            _config.LogLocation = txtBoxLogLocation.Text;
            _config.DebugLog = chkBoxDebugLog.Checked;
            _config.TraceLog = chkBoxTraceLog.Checked;
            _config.HomePark = chkBoxHomePark.Checked;
            _config.ResetExe = txtBoxExternalExe.Text;
            _config.ResetOffParameters = txtBoxOffParameters.Text;
            _config.ResetOnParameters = txtBoxOnParameters.Text;
            _config.HardReset = checkBoxExternalReset.Checked;
            _config.SoftReset = checkBoxInternalReset.Checked;
            _config.UseMQTT = chkBoxMQTT.Checked;
            _config.UseGNS = chkBoxGNS.Checked;
            _config.GNSPath = txtBoxGNSLocation.Text;
            _config.GNSDispatcherPath = txtBoxGNSDispatherLocation.Text;

            _config.MQTTLocalHost = chkBoxUseLocalhost.Checked;

            if (chkBoxUseLocalhost.Checked)
            {
                _config.MQTTip = "localhost";
                _config.MQTTport = "1883";
            }
            else
            {
                _config.MQTTip = txtBoxMQTTip.Text;
                _config.MQTTport = txtBoxMQTTport.Text;
            }

            _config.ResetDelay = resetDelay;
            _config.ShutterTimeout = shutterTimeout;
            _config.RotationTimeout = rotationTimeout;
            _config.CycleDelay = cycleDelay;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }

        private void btnLog_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog folderDialog = new FolderBrowserDialog())
            {
                folderDialog.Description = "Select a folder";
                folderDialog.ShowNewFolderButton = true;

                DialogResult result = folderDialog.ShowDialog();
                if (result == DialogResult.OK && !string.IsNullOrWhiteSpace(folderDialog.SelectedPath))
                {
                    //MessageBox.Show("Selected folder: " + folderDialog.SelectedPath);
                    // You can also store the path or use it elsewhere
                }
                txtBoxLogLocation.Text = folderDialog.SelectedPath.ToString();
            }
        }

        private void checkBoxExternalReset_CheckedChanged(object sender, EventArgs e)
        {
            txtBoxExternalExe.Enabled = checkBoxExternalReset.Checked;
            txtBoxOffParameters.Enabled = checkBoxExternalReset.Checked;
            txtBoxOnParameters.Enabled = checkBoxExternalReset.Checked;
            txtBoxCycleDelay.Enabled = checkBoxExternalReset.Checked;
            btnTestResetOff.Enabled = checkBoxExternalReset.Checked;
            btnTestResetOn.Enabled = checkBoxExternalReset.Checked;
            btnReset.Enabled = checkBoxExternalReset.Checked;
        }

        private void checkBoxUseInternalReset_CheckedChanged(object sender, EventArgs e)
        {
        }

        private void chkBoxMQTT_CkeckedChanged(object sender, EventArgs e)
        {
            chkBoxUseLocalhost.Enabled = chkBoxMQTT.Checked;
            txtBoxMQTTip.Enabled = chkBoxMQTT.Checked && !chkBoxUseLocalhost.Checked;
            txtBoxMQTTport.Enabled = chkBoxMQTT.Checked && !chkBoxUseLocalhost.Checked;
        }

        private void chkBoxGNS_CheckedChanged(object sender, EventArgs e)
        {
            btnGNS.Enabled = chkBoxGNS.Checked;
            txtBoxGNSLocation.Enabled = chkBoxGNS.Checked;
            txtBoxGNSDispatherLocation.Enabled = chkBoxGNS.Checked;
            btnDispatcher.Enabled = chkBoxGNS.Checked;
        }

        private void btnReset_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog fileDialog = new OpenFileDialog())
            {
                fileDialog.Title = "Select Reset Executable";
                fileDialog.Filter = "Executable Files (*.exe)|*.exe";
                fileDialog.CheckFileExists = true;
                fileDialog.CheckPathExists = true;

                DialogResult result = fileDialog.ShowDialog();
                if (result == DialogResult.OK && !string.IsNullOrWhiteSpace(fileDialog.FileName))
                {
                    txtBoxExternalExe.Text = fileDialog.FileName;
                }
            }
        }

        private void btnTestResetOff_Click(object sender, EventArgs e)
        {
            string exePath = _config.ResetExe;
            string parameters = _config.ResetOffParameters;

            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = parameters,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                using (var process = Process.Start(startInfo))
                {
                    // Optional: wait for it to finish
                    process?.WaitForExit();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to launch reset tool: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btnTestResetOn_Click(object sender, EventArgs e)
        {
            string exePath = _config.ResetExe;
            string parameters = _config.ResetOnParameters;

            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = parameters,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                using (var process = Process.Start(startInfo))
                {
                    // Optional: wait for it to finish
                    process?.WaitForExit();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to launch reset tool: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btnGNS_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog fileDialog = new OpenFileDialog())
            {
                fileDialog.Title = "Select GNS Executable";
                fileDialog.Filter = "Executable Files (*.exe)|*.exe";
                fileDialog.CheckFileExists = true;
                fileDialog.CheckPathExists = true;

                DialogResult result = fileDialog.ShowDialog();
                if (result == DialogResult.OK && !string.IsNullOrWhiteSpace(fileDialog.FileName))
                {
                    txtBoxGNSLocation.Text = fileDialog.FileName;
                }
            }
        }

        private void btnDispatcher_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog fileDialog = new OpenFileDialog())
            {
                fileDialog.Title = "Select GNS Dispatcher";
                fileDialog.Filter = "Executable Files (*.exe)|*.exe";
                fileDialog.CheckFileExists = true;
                fileDialog.CheckPathExists = true;

                DialogResult result = fileDialog.ShowDialog();
                if (result == DialogResult.OK && !string.IsNullOrWhiteSpace(fileDialog.FileName))
                {
                    txtBoxGNSDispatherLocation.Text = fileDialog.FileName;
                }
            }
        }

        private void chkBoxMQTT_CheckedChanged(object sender, EventArgs e)
        {
            chkBoxUseLocalhost.Enabled = chkBoxMQTT.Checked;
            txtBoxMQTTip.Enabled = chkBoxMQTT.Checked && !chkBoxUseLocalhost.Checked;
            txtBoxMQTTport.Enabled = chkBoxMQTT.Checked && !chkBoxUseLocalhost.Checked;
        }

        private void chkBoxUseLocalhost_CheckedChanged(object sender, EventArgs e)
        {
            if (chkBoxUseLocalhost.Checked)
            {
                txtBoxMQTTip.Text = "localhost";
                txtBoxMQTTport.Text = "1883";
            }
            // When unchecked, just enable the text boxes - user can enter custom values
            
            txtBoxMQTTip.Enabled = !chkBoxUseLocalhost.Checked;
            txtBoxMQTTport.Enabled = !chkBoxUseLocalhost.Checked;
        }

        private bool TryReadInt(TextBox textBox, string fieldName, int? minValue, int? maxValue, out int value)
        {
            if (!int.TryParse(textBox.Text, out value))
            {
                MessageBox.Show($"{fieldName} must be a whole number.", "Invalid value", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                textBox.Focus();
                textBox.SelectAll();
                return false;
            }

            if (minValue.HasValue && value < minValue.Value)
            {
                MessageBox.Show($"{fieldName} must be at least {minValue.Value}.", "Invalid value", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                textBox.Focus();
                textBox.SelectAll();
                return false;
            }

            if (maxValue.HasValue && value > maxValue.Value)
            {
                MessageBox.Show($"{fieldName} must be at most {maxValue.Value}.", "Invalid value", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                textBox.Focus();
                textBox.SelectAll();
                return false;
            }

            return true;
        }
    }
}
