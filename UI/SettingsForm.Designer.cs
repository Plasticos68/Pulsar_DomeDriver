namespace Pulsar_DomeDriver.UI
{
    partial class SettingsForm
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(SettingsForm));
            this.btnLog = new System.Windows.Forms.Button();
            this.label1 = new System.Windows.Forms.Label();
            this.btnCancel = new System.Windows.Forms.Button();
            this.btnOK = new System.Windows.Forms.Button();
            this.label2 = new System.Windows.Forms.Label();
            this.comboBoxSerialPort = new System.Windows.Forms.ComboBox();
            this.txtBoxLogLocation = new System.Windows.Forms.TextBox();
            this.txtBoxExternalExe = new System.Windows.Forms.TextBox();
            this.btnReset = new System.Windows.Forms.Button();
            this.label3 = new System.Windows.Forms.Label();
            this.txtBoxOffParameters = new System.Windows.Forms.TextBox();
            this.checkBoxExternalReset = new System.Windows.Forms.CheckBox();
            this.checkBoxInternalReset = new System.Windows.Forms.CheckBox();
            this.label5 = new System.Windows.Forms.Label();
            this.txtBoxShutterTimeout = new System.Windows.Forms.TextBox();
            this.groupBox1 = new System.Windows.Forms.GroupBox();
            this.txtBoxRotationTimeout = new System.Windows.Forms.TextBox();
            this.label4 = new System.Windows.Forms.Label();
            this.txtBoxCycleDelay = new System.Windows.Forms.TextBox();
            this.label11 = new System.Windows.Forms.Label();
            this.btnTestResetOn = new System.Windows.Forms.Button();
            this.label10 = new System.Windows.Forms.Label();
            this.txtBoxOnParameters = new System.Windows.Forms.TextBox();
            this.label9 = new System.Windows.Forms.Label();
            this.txtBoxResetDelay = new System.Windows.Forms.TextBox();
            this.label8 = new System.Windows.Forms.Label();
            this.btnTestResetOff = new System.Windows.Forms.Button();
            this.label7 = new System.Windows.Forms.Label();
            this.label6 = new System.Windows.Forms.Label();
            this.chkBoxDebugLog = new System.Windows.Forms.CheckBox();
            this.chkBoxTraceLog = new System.Windows.Forms.CheckBox();
            this.chkBoxMQTT = new System.Windows.Forms.CheckBox();
            this.chkBoxGNS = new System.Windows.Forms.CheckBox();
            this.txtBoxGNSLocation = new System.Windows.Forms.TextBox();
            this.btnGNS = new System.Windows.Forms.Button();
            this.txtBoxGNSDispatherLocation = new System.Windows.Forms.TextBox();
            this.btnDispatcher = new System.Windows.Forms.Button();
            this.label12 = new System.Windows.Forms.Label();
            this.groupBox2 = new System.Windows.Forms.GroupBox();
            this.groupBox3 = new System.Windows.Forms.GroupBox();
            this.label14 = new System.Windows.Forms.Label();
            this.txtBoxMQTTip = new System.Windows.Forms.TextBox();
            this.txtBoxMQTTport = new System.Windows.Forms.TextBox();
            this.label13 = new System.Windows.Forms.Label();
            this.groupBox4 = new System.Windows.Forms.GroupBox();
            this.groupBox1.SuspendLayout();
            this.groupBox2.SuspendLayout();
            this.groupBox3.SuspendLayout();
            this.groupBox4.SuspendLayout();
            this.SuspendLayout();
            // 
            // btnLog
            // 
            this.btnLog.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.btnLog.Image = ((System.Drawing.Image)(resources.GetObject("btnLog.Image")));
            this.btnLog.Location = new System.Drawing.Point(819, 106);
            this.btnLog.Margin = new System.Windows.Forms.Padding(4);
            this.btnLog.Name = "btnLog";
            this.btnLog.Size = new System.Drawing.Size(56, 41);
            this.btnLog.TabIndex = 39;
            this.btnLog.UseVisualStyleBackColor = true;
            this.btnLog.Click += new System.EventHandler(this.btnLog_Click);
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.label1.Location = new System.Drawing.Point(38, 110);
            this.label1.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(214, 32);
            this.label1.TabIndex = 38;
            this.label1.Text = "Log file location";
            // 
            // btnCancel
            // 
            this.btnCancel.BackgroundImageLayout = System.Windows.Forms.ImageLayout.Stretch;
            this.btnCancel.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.btnCancel.Image = ((System.Drawing.Image)(resources.GetObject("btnCancel.Image")));
            this.btnCancel.Location = new System.Drawing.Point(1539, 208);
            this.btnCancel.Margin = new System.Windows.Forms.Padding(4, 2, 4, 2);
            this.btnCancel.Name = "btnCancel";
            this.btnCancel.Size = new System.Drawing.Size(148, 106);
            this.btnCancel.TabIndex = 36;
            this.btnCancel.UseVisualStyleBackColor = true;
            this.btnCancel.Click += new System.EventHandler(this.btnCancel_Click);
            // 
            // btnOK
            // 
            this.btnOK.BackgroundImageLayout = System.Windows.Forms.ImageLayout.Stretch;
            this.btnOK.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.btnOK.Image = ((System.Drawing.Image)(resources.GetObject("btnOK.Image")));
            this.btnOK.Location = new System.Drawing.Point(1310, 208);
            this.btnOK.Margin = new System.Windows.Forms.Padding(4, 2, 4, 2);
            this.btnOK.Name = "btnOK";
            this.btnOK.Size = new System.Drawing.Size(148, 106);
            this.btnOK.TabIndex = 35;
            this.btnOK.UseVisualStyleBackColor = true;
            this.btnOK.Click += new System.EventHandler(this.btnOK_Click);
            // 
            // label2
            // 
            this.label2.AutoSize = true;
            this.label2.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.label2.Location = new System.Drawing.Point(89, 60);
            this.label2.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(132, 32);
            this.label2.TabIndex = 40;
            this.label2.Text = "Com Port";
            // 
            // comboBoxSerialPort
            // 
            this.comboBoxSerialPort.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.comboBoxSerialPort.FormattingEnabled = true;
            this.comboBoxSerialPort.Location = new System.Drawing.Point(363, 50);
            this.comboBoxSerialPort.Margin = new System.Windows.Forms.Padding(4);
            this.comboBoxSerialPort.Name = "comboBoxSerialPort";
            this.comboBoxSerialPort.Size = new System.Drawing.Size(147, 40);
            this.comboBoxSerialPort.TabIndex = 41;
            // 
            // txtBoxLogLocation
            // 
            this.txtBoxLogLocation.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.txtBoxLogLocation.Location = new System.Drawing.Point(312, 106);
            this.txtBoxLogLocation.Margin = new System.Windows.Forms.Padding(4);
            this.txtBoxLogLocation.Name = "txtBoxLogLocation";
            this.txtBoxLogLocation.Size = new System.Drawing.Size(481, 39);
            this.txtBoxLogLocation.TabIndex = 42;
            // 
            // txtBoxExternalExe
            // 
            this.txtBoxExternalExe.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.txtBoxExternalExe.Location = new System.Drawing.Point(332, 311);
            this.txtBoxExternalExe.Margin = new System.Windows.Forms.Padding(4);
            this.txtBoxExternalExe.Name = "txtBoxExternalExe";
            this.txtBoxExternalExe.Size = new System.Drawing.Size(481, 39);
            this.txtBoxExternalExe.TabIndex = 45;
            // 
            // btnReset
            // 
            this.btnReset.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.btnReset.Image = ((System.Drawing.Image)(resources.GetObject("btnReset.Image")));
            this.btnReset.Location = new System.Drawing.Point(840, 300);
            this.btnReset.Margin = new System.Windows.Forms.Padding(4);
            this.btnReset.Name = "btnReset";
            this.btnReset.Size = new System.Drawing.Size(59, 41);
            this.btnReset.TabIndex = 44;
            this.btnReset.UseVisualStyleBackColor = true;
            this.btnReset.Click += new System.EventHandler(this.btnReset_Click);
            // 
            // label3
            // 
            this.label3.AutoSize = true;
            this.label3.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.label3.Location = new System.Drawing.Point(59, 314);
            this.label3.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label3.Name = "label3";
            this.label3.Size = new System.Drawing.Size(141, 32);
            this.label3.TabIndex = 43;
            this.label3.Text = "Reset exe";
            // 
            // txtBoxOffParameters
            // 
            this.txtBoxOffParameters.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.txtBoxOffParameters.Location = new System.Drawing.Point(332, 474);
            this.txtBoxOffParameters.Margin = new System.Windows.Forms.Padding(4);
            this.txtBoxOffParameters.Name = "txtBoxOffParameters";
            this.txtBoxOffParameters.Size = new System.Drawing.Size(354, 39);
            this.txtBoxOffParameters.TabIndex = 46;
            // 
            // checkBoxExternalReset
            // 
            this.checkBoxExternalReset.AutoSize = true;
            this.checkBoxExternalReset.CheckAlign = System.Drawing.ContentAlignment.MiddleRight;
            this.checkBoxExternalReset.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.checkBoxExternalReset.Location = new System.Drawing.Point(488, 61);
            this.checkBoxExternalReset.Margin = new System.Windows.Forms.Padding(4);
            this.checkBoxExternalReset.Name = "checkBoxExternalReset";
            this.checkBoxExternalReset.Size = new System.Drawing.Size(226, 36);
            this.checkBoxExternalReset.TabIndex = 48;
            this.checkBoxExternalReset.Text = "External Reset";
            this.checkBoxExternalReset.UseVisualStyleBackColor = true;
            this.checkBoxExternalReset.CheckedChanged += new System.EventHandler(this.checkBoxExternalReset_CheckedChanged);
            // 
            // checkBoxInternalReset
            // 
            this.checkBoxInternalReset.AutoSize = true;
            this.checkBoxInternalReset.CheckAlign = System.Drawing.ContentAlignment.MiddleRight;
            this.checkBoxInternalReset.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.checkBoxInternalReset.Location = new System.Drawing.Point(154, 61);
            this.checkBoxInternalReset.Margin = new System.Windows.Forms.Padding(4);
            this.checkBoxInternalReset.Name = "checkBoxInternalReset";
            this.checkBoxInternalReset.Size = new System.Drawing.Size(216, 36);
            this.checkBoxInternalReset.TabIndex = 49;
            this.checkBoxInternalReset.Text = "Internal Reset";
            this.checkBoxInternalReset.UseVisualStyleBackColor = true;
            // 
            // label5
            // 
            this.label5.AutoSize = true;
            this.label5.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.label5.Location = new System.Drawing.Point(57, 151);
            this.label5.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label5.Name = "label5";
            this.label5.Size = new System.Drawing.Size(255, 32);
            this.label5.TabIndex = 50;
            this.label5.Text = "Shutter Timeout (s)";
            // 
            // txtBoxShutterTimeout
            // 
            this.txtBoxShutterTimeout.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.txtBoxShutterTimeout.Location = new System.Drawing.Point(332, 148);
            this.txtBoxShutterTimeout.Margin = new System.Windows.Forms.Padding(4);
            this.txtBoxShutterTimeout.Name = "txtBoxShutterTimeout";
            this.txtBoxShutterTimeout.Size = new System.Drawing.Size(118, 39);
            this.txtBoxShutterTimeout.TabIndex = 51;
            // 
            // groupBox1
            // 
            this.groupBox1.Controls.Add(this.txtBoxRotationTimeout);
            this.groupBox1.Controls.Add(this.label4);
            this.groupBox1.Controls.Add(this.txtBoxCycleDelay);
            this.groupBox1.Controls.Add(this.label11);
            this.groupBox1.Controls.Add(this.btnTestResetOn);
            this.groupBox1.Controls.Add(this.label10);
            this.groupBox1.Controls.Add(this.txtBoxOnParameters);
            this.groupBox1.Controls.Add(this.label9);
            this.groupBox1.Controls.Add(this.txtBoxResetDelay);
            this.groupBox1.Controls.Add(this.label8);
            this.groupBox1.Controls.Add(this.checkBoxInternalReset);
            this.groupBox1.Controls.Add(this.checkBoxExternalReset);
            this.groupBox1.Controls.Add(this.btnTestResetOff);
            this.groupBox1.Controls.Add(this.txtBoxShutterTimeout);
            this.groupBox1.Controls.Add(this.label7);
            this.groupBox1.Controls.Add(this.label5);
            this.groupBox1.Controls.Add(this.label6);
            this.groupBox1.Controls.Add(this.txtBoxExternalExe);
            this.groupBox1.Controls.Add(this.txtBoxOffParameters);
            this.groupBox1.Controls.Add(this.label3);
            this.groupBox1.Controls.Add(this.btnReset);
            this.groupBox1.FlatStyle = System.Windows.Forms.FlatStyle.Popup;
            this.groupBox1.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.groupBox1.Location = new System.Drawing.Point(1051, 371);
            this.groupBox1.Margin = new System.Windows.Forms.Padding(4);
            this.groupBox1.Name = "groupBox1";
            this.groupBox1.Padding = new System.Windows.Forms.Padding(4);
            this.groupBox1.Size = new System.Drawing.Size(952, 599);
            this.groupBox1.TabIndex = 52;
            this.groupBox1.TabStop = false;
            this.groupBox1.Text = "Reset";
            // 
            // txtBoxRotationTimeout
            // 
            this.txtBoxRotationTimeout.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.txtBoxRotationTimeout.Location = new System.Drawing.Point(781, 148);
            this.txtBoxRotationTimeout.Margin = new System.Windows.Forms.Padding(4);
            this.txtBoxRotationTimeout.Name = "txtBoxRotationTimeout";
            this.txtBoxRotationTimeout.Size = new System.Drawing.Size(118, 39);
            this.txtBoxRotationTimeout.TabIndex = 62;
            // 
            // label4
            // 
            this.label4.AutoSize = true;
            this.label4.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.label4.Location = new System.Drawing.Point(482, 155);
            this.label4.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label4.Name = "label4";
            this.label4.Size = new System.Drawing.Size(270, 32);
            this.label4.TabIndex = 61;
            this.label4.Text = "Rotation Timeout (s)";
            // 
            // txtBoxCycleDelay
            // 
            this.txtBoxCycleDelay.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.txtBoxCycleDelay.Location = new System.Drawing.Point(777, 220);
            this.txtBoxCycleDelay.Margin = new System.Windows.Forms.Padding(4);
            this.txtBoxCycleDelay.Name = "txtBoxCycleDelay";
            this.txtBoxCycleDelay.Size = new System.Drawing.Size(118, 39);
            this.txtBoxCycleDelay.TabIndex = 60;
            // 
            // label11
            // 
            this.label11.AutoSize = true;
            this.label11.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.label11.Location = new System.Drawing.Point(513, 227);
            this.label11.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label11.Name = "label11";
            this.label11.Size = new System.Drawing.Size(204, 32);
            this.label11.TabIndex = 59;
            this.label11.Text = "Cycle Delay (s)";
            // 
            // btnTestResetOn
            // 
            this.btnTestResetOn.BackgroundImageLayout = System.Windows.Forms.ImageLayout.Zoom;
            this.btnTestResetOn.Image = ((System.Drawing.Image)(resources.GetObject("btnTestResetOn.Image")));
            this.btnTestResetOn.Location = new System.Drawing.Point(838, 373);
            this.btnTestResetOn.Margin = new System.Windows.Forms.Padding(4);
            this.btnTestResetOn.Name = "btnTestResetOn";
            this.btnTestResetOn.Size = new System.Drawing.Size(61, 64);
            this.btnTestResetOn.TabIndex = 58;
            this.btnTestResetOn.UseVisualStyleBackColor = true;
            this.btnTestResetOn.Click += new System.EventHandler(this.btnTestResetOn_Click);
            // 
            // label10
            // 
            this.label10.AutoSize = true;
            this.label10.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.label10.Location = new System.Drawing.Point(738, 396);
            this.label10.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label10.Name = "label10";
            this.label10.Size = new System.Drawing.Size(69, 32);
            this.label10.TabIndex = 57;
            this.label10.Text = "Test";
            // 
            // txtBoxOnParameters
            // 
            this.txtBoxOnParameters.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.txtBoxOnParameters.Location = new System.Drawing.Point(332, 392);
            this.txtBoxOnParameters.Margin = new System.Windows.Forms.Padding(4);
            this.txtBoxOnParameters.Name = "txtBoxOnParameters";
            this.txtBoxOnParameters.Size = new System.Drawing.Size(354, 39);
            this.txtBoxOnParameters.TabIndex = 56;
            // 
            // label9
            // 
            this.label9.AutoSize = true;
            this.label9.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.label9.Location = new System.Drawing.Point(57, 400);
            this.label9.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label9.Name = "label9";
            this.label9.Size = new System.Drawing.Size(205, 32);
            this.label9.TabIndex = 55;
            this.label9.Text = "On Parameters";
            // 
            // txtBoxResetDelay
            // 
            this.txtBoxResetDelay.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.txtBoxResetDelay.Location = new System.Drawing.Point(332, 229);
            this.txtBoxResetDelay.Margin = new System.Windows.Forms.Padding(4);
            this.txtBoxResetDelay.Name = "txtBoxResetDelay";
            this.txtBoxResetDelay.Size = new System.Drawing.Size(118, 39);
            this.txtBoxResetDelay.TabIndex = 54;
            // 
            // label8
            // 
            this.label8.AutoSize = true;
            this.label8.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.label8.Location = new System.Drawing.Point(57, 233);
            this.label8.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label8.Name = "label8";
            this.label8.Size = new System.Drawing.Size(225, 32);
            this.label8.TabIndex = 53;
            this.label8.Text = "Reboot Delay (s)";
            // 
            // btnTestResetOff
            // 
            this.btnTestResetOff.BackgroundImageLayout = System.Windows.Forms.ImageLayout.Zoom;
            this.btnTestResetOff.Image = ((System.Drawing.Image)(resources.GetObject("btnTestResetOff.Image")));
            this.btnTestResetOff.Location = new System.Drawing.Point(838, 467);
            this.btnTestResetOff.Margin = new System.Windows.Forms.Padding(4);
            this.btnTestResetOff.Name = "btnTestResetOff";
            this.btnTestResetOff.Size = new System.Drawing.Size(61, 64);
            this.btnTestResetOff.TabIndex = 52;
            this.btnTestResetOff.UseVisualStyleBackColor = true;
            this.btnTestResetOff.Click += new System.EventHandler(this.btnTestResetOff_Click);
            // 
            // label7
            // 
            this.label7.AutoSize = true;
            this.label7.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.label7.Location = new System.Drawing.Point(738, 490);
            this.label7.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label7.Name = "label7";
            this.label7.Size = new System.Drawing.Size(69, 32);
            this.label7.TabIndex = 51;
            this.label7.Text = "Test";
            // 
            // label6
            // 
            this.label6.AutoSize = true;
            this.label6.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.label6.Location = new System.Drawing.Point(57, 481);
            this.label6.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label6.Name = "label6";
            this.label6.Size = new System.Drawing.Size(205, 32);
            this.label6.TabIndex = 50;
            this.label6.Text = "Off Parameters";
            // 
            // chkBoxDebugLog
            // 
            this.chkBoxDebugLog.AutoSize = true;
            this.chkBoxDebugLog.CheckAlign = System.Drawing.ContentAlignment.MiddleRight;
            this.chkBoxDebugLog.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.chkBoxDebugLog.Location = new System.Drawing.Point(34, 52);
            this.chkBoxDebugLog.Margin = new System.Windows.Forms.Padding(4);
            this.chkBoxDebugLog.Name = "chkBoxDebugLog";
            this.chkBoxDebugLog.Size = new System.Drawing.Size(170, 36);
            this.chkBoxDebugLog.TabIndex = 54;
            this.chkBoxDebugLog.Text = "Debug log";
            this.chkBoxDebugLog.UseVisualStyleBackColor = true;
            // 
            // chkBoxTraceLog
            // 
            this.chkBoxTraceLog.AutoSize = true;
            this.chkBoxTraceLog.CheckAlign = System.Drawing.ContentAlignment.MiddleRight;
            this.chkBoxTraceLog.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.chkBoxTraceLog.Location = new System.Drawing.Point(422, 52);
            this.chkBoxTraceLog.Margin = new System.Windows.Forms.Padding(4);
            this.chkBoxTraceLog.Name = "chkBoxTraceLog";
            this.chkBoxTraceLog.Size = new System.Drawing.Size(158, 36);
            this.chkBoxTraceLog.TabIndex = 55;
            this.chkBoxTraceLog.Text = "Trace log";
            this.chkBoxTraceLog.UseVisualStyleBackColor = true;
            // 
            // chkBoxMQTT
            // 
            this.chkBoxMQTT.AutoSize = true;
            this.chkBoxMQTT.CheckAlign = System.Drawing.ContentAlignment.MiddleRight;
            this.chkBoxMQTT.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.chkBoxMQTT.Location = new System.Drawing.Point(30, 54);
            this.chkBoxMQTT.Margin = new System.Windows.Forms.Padding(4);
            this.chkBoxMQTT.Name = "chkBoxMQTT";
            this.chkBoxMQTT.Size = new System.Drawing.Size(119, 36);
            this.chkBoxMQTT.TabIndex = 56;
            this.chkBoxMQTT.Text = "MQTT";
            this.chkBoxMQTT.UseVisualStyleBackColor = true;
            this.chkBoxMQTT.CheckedChanged += new System.EventHandler(this.chkBoxMQTT_CkeckedChanged);
            // 
            // chkBoxGNS
            // 
            this.chkBoxGNS.AutoSize = true;
            this.chkBoxGNS.CheckAlign = System.Drawing.ContentAlignment.MiddleRight;
            this.chkBoxGNS.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.chkBoxGNS.Location = new System.Drawing.Point(39, 97);
            this.chkBoxGNS.Margin = new System.Windows.Forms.Padding(4);
            this.chkBoxGNS.Name = "chkBoxGNS";
            this.chkBoxGNS.Size = new System.Drawing.Size(101, 36);
            this.chkBoxGNS.TabIndex = 57;
            this.chkBoxGNS.Text = "GNS";
            this.chkBoxGNS.UseVisualStyleBackColor = true;
            this.chkBoxGNS.CheckedChanged += new System.EventHandler(this.chkBoxGNS_CheckedChanged);
            // 
            // txtBoxGNSLocation
            // 
            this.txtBoxGNSLocation.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.txtBoxGNSLocation.Location = new System.Drawing.Point(307, 95);
            this.txtBoxGNSLocation.Margin = new System.Windows.Forms.Padding(4);
            this.txtBoxGNSLocation.Name = "txtBoxGNSLocation";
            this.txtBoxGNSLocation.Size = new System.Drawing.Size(481, 39);
            this.txtBoxGNSLocation.TabIndex = 58;
            // 
            // btnGNS
            // 
            this.btnGNS.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.btnGNS.Image = ((System.Drawing.Image)(resources.GetObject("btnGNS.Image")));
            this.btnGNS.Location = new System.Drawing.Point(814, 96);
            this.btnGNS.Margin = new System.Windows.Forms.Padding(4);
            this.btnGNS.Name = "btnGNS";
            this.btnGNS.Size = new System.Drawing.Size(56, 41);
            this.btnGNS.TabIndex = 59;
            this.btnGNS.UseVisualStyleBackColor = true;
            this.btnGNS.Click += new System.EventHandler(this.btnGNS_Click);
            // 
            // txtBoxGNSDispatherLocation
            // 
            this.txtBoxGNSDispatherLocation.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.txtBoxGNSDispatherLocation.Location = new System.Drawing.Point(308, 186);
            this.txtBoxGNSDispatherLocation.Margin = new System.Windows.Forms.Padding(4);
            this.txtBoxGNSDispatherLocation.Name = "txtBoxGNSDispatherLocation";
            this.txtBoxGNSDispatherLocation.Size = new System.Drawing.Size(481, 39);
            this.txtBoxGNSDispatherLocation.TabIndex = 62;
            // 
            // btnDispatcher
            // 
            this.btnDispatcher.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.btnDispatcher.Image = ((System.Drawing.Image)(resources.GetObject("btnDispatcher.Image")));
            this.btnDispatcher.Location = new System.Drawing.Point(815, 186);
            this.btnDispatcher.Margin = new System.Windows.Forms.Padding(4);
            this.btnDispatcher.Name = "btnDispatcher";
            this.btnDispatcher.Size = new System.Drawing.Size(56, 41);
            this.btnDispatcher.TabIndex = 61;
            this.btnDispatcher.UseVisualStyleBackColor = true;
            this.btnDispatcher.Click += new System.EventHandler(this.btnDispatcher_Click);
            // 
            // label12
            // 
            this.label12.AutoSize = true;
            this.label12.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.label12.Location = new System.Drawing.Point(34, 190);
            this.label12.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label12.Name = "label12";
            this.label12.Size = new System.Drawing.Size(218, 32);
            this.label12.TabIndex = 60;
            this.label12.Text = "GNS Dispatcher";
            // 
            // groupBox2
            // 
            this.groupBox2.Controls.Add(this.txtBoxGNSLocation);
            this.groupBox2.Controls.Add(this.txtBoxGNSDispatherLocation);
            this.groupBox2.Controls.Add(this.chkBoxGNS);
            this.groupBox2.Controls.Add(this.btnDispatcher);
            this.groupBox2.Controls.Add(this.btnGNS);
            this.groupBox2.Controls.Add(this.label12);
            this.groupBox2.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.groupBox2.Location = new System.Drawing.Point(51, 654);
            this.groupBox2.Margin = new System.Windows.Forms.Padding(4);
            this.groupBox2.Name = "groupBox2";
            this.groupBox2.Padding = new System.Windows.Forms.Padding(4);
            this.groupBox2.Size = new System.Drawing.Size(956, 316);
            this.groupBox2.TabIndex = 63;
            this.groupBox2.TabStop = false;
            this.groupBox2.Text = "GNS";
            // 
            // groupBox3
            // 
            this.groupBox3.Controls.Add(this.label14);
            this.groupBox3.Controls.Add(this.txtBoxMQTTip);
            this.groupBox3.Controls.Add(this.txtBoxMQTTport);
            this.groupBox3.Controls.Add(this.chkBoxMQTT);
            this.groupBox3.Controls.Add(this.label13);
            this.groupBox3.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.groupBox3.Location = new System.Drawing.Point(55, 371);
            this.groupBox3.Margin = new System.Windows.Forms.Padding(4);
            this.groupBox3.Name = "groupBox3";
            this.groupBox3.Padding = new System.Windows.Forms.Padding(4);
            this.groupBox3.Size = new System.Drawing.Size(956, 247);
            this.groupBox3.TabIndex = 64;
            this.groupBox3.TabStop = false;
            this.groupBox3.Text = "MQTT";
            // 
            // label14
            // 
            this.label14.AutoSize = true;
            this.label14.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.label14.Location = new System.Drawing.Point(34, 102);
            this.label14.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label14.Name = "label14";
            this.label14.Size = new System.Drawing.Size(40, 32);
            this.label14.TabIndex = 63;
            this.label14.Text = "IP";
            // 
            // txtBoxMQTTip
            // 
            this.txtBoxMQTTip.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.txtBoxMQTTip.Location = new System.Drawing.Point(308, 95);
            this.txtBoxMQTTip.Margin = new System.Windows.Forms.Padding(4);
            this.txtBoxMQTTip.Name = "txtBoxMQTTip";
            this.txtBoxMQTTip.Size = new System.Drawing.Size(481, 39);
            this.txtBoxMQTTip.TabIndex = 58;
            // 
            // txtBoxMQTTport
            // 
            this.txtBoxMQTTport.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.txtBoxMQTTport.Location = new System.Drawing.Point(308, 155);
            this.txtBoxMQTTport.Margin = new System.Windows.Forms.Padding(4);
            this.txtBoxMQTTport.Name = "txtBoxMQTTport";
            this.txtBoxMQTTport.Size = new System.Drawing.Size(481, 39);
            this.txtBoxMQTTport.TabIndex = 62;
            // 
            // label13
            // 
            this.label13.AutoSize = true;
            this.label13.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.label13.Location = new System.Drawing.Point(34, 155);
            this.label13.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label13.Name = "label13";
            this.label13.Size = new System.Drawing.Size(66, 32);
            this.label13.TabIndex = 60;
            this.label13.Text = "Port";
            // 
            // groupBox4
            // 
            this.groupBox4.Controls.Add(this.chkBoxTraceLog);
            this.groupBox4.Controls.Add(this.chkBoxDebugLog);
            this.groupBox4.Controls.Add(this.txtBoxLogLocation);
            this.groupBox4.Controls.Add(this.label1);
            this.groupBox4.Controls.Add(this.btnLog);
            this.groupBox4.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.groupBox4.Location = new System.Drawing.Point(51, 167);
            this.groupBox4.Margin = new System.Windows.Forms.Padding(4);
            this.groupBox4.Name = "groupBox4";
            this.groupBox4.Padding = new System.Windows.Forms.Padding(4);
            this.groupBox4.Size = new System.Drawing.Size(956, 176);
            this.groupBox4.TabIndex = 65;
            this.groupBox4.TabStop = false;
            this.groupBox4.Text = "Log";
            // 
            // SettingsForm
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(11F, 24F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(2070, 1030);
            this.Controls.Add(this.groupBox4);
            this.Controls.Add(this.groupBox3);
            this.Controls.Add(this.groupBox2);
            this.Controls.Add(this.groupBox1);
            this.Controls.Add(this.comboBoxSerialPort);
            this.Controls.Add(this.label2);
            this.Controls.Add(this.btnCancel);
            this.Controls.Add(this.btnOK);
            this.Margin = new System.Windows.Forms.Padding(4);
            this.Name = "SettingsForm";
            this.Text = "SettingsForm";
            this.Load += new System.EventHandler(this.SettingsForm_Load);
            this.groupBox1.ResumeLayout(false);
            this.groupBox1.PerformLayout();
            this.groupBox2.ResumeLayout(false);
            this.groupBox2.PerformLayout();
            this.groupBox3.ResumeLayout(false);
            this.groupBox3.PerformLayout();
            this.groupBox4.ResumeLayout(false);
            this.groupBox4.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.Button btnLog;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.Button btnCancel;
        private System.Windows.Forms.Button btnOK;
        private System.Windows.Forms.Label label2;
        private System.Windows.Forms.ComboBox comboBoxSerialPort;
        private System.Windows.Forms.TextBox txtBoxLogLocation;
        private System.Windows.Forms.TextBox txtBoxExternalExe;
        private System.Windows.Forms.Button btnReset;
        private System.Windows.Forms.Label label3;
        private System.Windows.Forms.TextBox txtBoxOffParameters;
        private System.Windows.Forms.CheckBox checkBoxExternalReset;
        private System.Windows.Forms.CheckBox checkBoxInternalReset;
        private System.Windows.Forms.Label label5;
        private System.Windows.Forms.TextBox txtBoxShutterTimeout;
        private System.Windows.Forms.GroupBox groupBox1;
        private System.Windows.Forms.Button btnTestResetOff;
        private System.Windows.Forms.Label label7;
        private System.Windows.Forms.Label label6;
        private System.Windows.Forms.TextBox txtBoxResetDelay;
        private System.Windows.Forms.Label label8;
        private System.Windows.Forms.TextBox txtBoxOnParameters;
        private System.Windows.Forms.Label label9;
        private System.Windows.Forms.Button btnTestResetOn;
        private System.Windows.Forms.Label label10;
        private System.Windows.Forms.TextBox txtBoxCycleDelay;
        private System.Windows.Forms.Label label11;
        private System.Windows.Forms.CheckBox chkBoxDebugLog;
        private System.Windows.Forms.CheckBox chkBoxTraceLog;
        private System.Windows.Forms.CheckBox chkBoxMQTT;
        private System.Windows.Forms.TextBox txtBoxRotationTimeout;
        private System.Windows.Forms.Label label4;
        private System.Windows.Forms.CheckBox chkBoxGNS;
        private System.Windows.Forms.TextBox txtBoxGNSLocation;
        private System.Windows.Forms.Button btnGNS;
        private System.Windows.Forms.TextBox txtBoxGNSDispatherLocation;
        private System.Windows.Forms.Button btnDispatcher;
        private System.Windows.Forms.Label label12;
        private System.Windows.Forms.GroupBox groupBox2;
        private System.Windows.Forms.GroupBox groupBox3;
        private System.Windows.Forms.Label label14;
        private System.Windows.Forms.TextBox txtBoxMQTTip;
        private System.Windows.Forms.TextBox txtBoxMQTTport;
        private System.Windows.Forms.Label label13;
        private System.Windows.Forms.GroupBox groupBox4;
    }
}