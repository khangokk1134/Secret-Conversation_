namespace ChatClient
{
    partial class RemoteClientForm
    {
        private System.ComponentModel.IContainer components = null;
        private System.Windows.Forms.TextBox txtServerIP;
        private System.Windows.Forms.TextBox txtPort;
        private System.Windows.Forms.TextBox txtUser;
        private System.Windows.Forms.Button btnConnect;
        private System.Windows.Forms.ListBox lstUsers;
        private System.Windows.Forms.RichTextBox rtbChat;
        private System.Windows.Forms.TextBox txtMessage;
        private System.Windows.Forms.Button btnSend;
        private System.Windows.Forms.Button btnGetPubKey;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.Label label2;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null)) components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            txtServerIP = new TextBox();
            txtPort = new TextBox();
            txtUser = new TextBox();
            btnConnect = new Button();
            lstUsers = new ListBox();
            rtbChat = new RichTextBox();
            txtMessage = new TextBox();
            btnSend = new Button();
            btnGetPubKey = new Button();
            label1 = new Label();
            label2 = new Label();
            SuspendLayout();
            // 
            // txtServerIP
            // 
            txtServerIP.Location = new Point(84, 15);
            txtServerIP.Name = "txtServerIP";
            txtServerIP.Size = new Size(120, 35);
            txtServerIP.TabIndex = 0;
            txtServerIP.Text = "127.0.0.1";
            txtServerIP.TextChanged += txtServerIP_TextChanged;
            // 
            // txtPort
            // 
            txtPort.Location = new Point(262, 10);
            txtPort.Name = "txtPort";
            txtPort.Size = new Size(71, 35);
            txtPort.TabIndex = 1;
            txtPort.Text = "5000";
            txtPort.TextChanged += txtPort_TextChanged;
            // 
            // txtUser
            // 
            txtUser.Location = new Point(339, 10);
            txtUser.Name = "txtUser";
            txtUser.Size = new Size(132, 35);
            txtUser.TabIndex = 2;
            txtUser.Text = "alice";
            txtUser.TextChanged += txtUser_TextChanged;
            // 
            // btnConnect
            // 
            btnConnect.Location = new Point(477, 10);
            btnConnect.Name = "btnConnect";
            btnConnect.Size = new Size(142, 37);
            btnConnect.TabIndex = 3;
            btnConnect.Text = "Connect";
            btnConnect.UseVisualStyleBackColor = true;
            btnConnect.Click += btnConnect_Click;
            // 
            // lstUsers
            // 
            lstUsers.FormattingEnabled = true;
            lstUsers.ItemHeight = 30;
            lstUsers.Location = new Point(12, 50);
            lstUsers.Name = "lstUsers";
            lstUsers.Size = new Size(152, 274);
            lstUsers.TabIndex = 4;
            // 
            // rtbChat
            // 
            rtbChat.Location = new Point(170, 50);
            rtbChat.Name = "rtbChat";
            rtbChat.ReadOnly = true;
            rtbChat.Size = new Size(460, 274);
            rtbChat.TabIndex = 5;
            rtbChat.Text = "";
            rtbChat.TextChanged += rtbChat_TextChanged;
            // 
            // txtMessage
            // 
            txtMessage.Location = new Point(12, 335);
            txtMessage.Name = "txtMessage";
            txtMessage.Size = new Size(520, 35);
            txtMessage.TabIndex = 6;
            // 
            // btnSend
            // 
            btnSend.Location = new Point(540, 333);
            btnSend.Name = "btnSend";
            btnSend.Size = new Size(90, 37);
            btnSend.TabIndex = 7;
            btnSend.Text = "Send";
            btnSend.UseVisualStyleBackColor = true;
            btnSend.Click += btnSend_Click;
            // 
            // btnGetPubKey
            // 
            btnGetPubKey.Location = new Point(422, 333);
            btnGetPubKey.Name = "btnGetPubKey";
            btnGetPubKey.Size = new Size(110, 37);
            btnGetPubKey.TabIndex = 8;
            btnGetPubKey.Text = "Get PubKey (debug)";
            btnGetPubKey.UseVisualStyleBackColor = true;
            btnGetPubKey.Click += btnGetPubKey_Click;
            // 
            // label1
            // 
            label1.Location = new Point(8, 12);
            label1.Name = "label1";
            label1.Size = new Size(70, 33);
            label1.TabIndex = 1;
            label1.Text = "Server:";
            // 
            // label2
            // 
            label2.Location = new Point(210, 15);
            label2.Name = "label2";
            label2.Size = new Size(57, 32);
            label2.TabIndex = 0;
            label2.Text = "Port";
            label2.Click += label2_Click;
            // 
            // RemoteClientForm
            // 
            ClientSize = new Size(631, 380);
            Controls.Add(label2);
            Controls.Add(label1);
            Controls.Add(btnGetPubKey);
            Controls.Add(btnSend);
            Controls.Add(txtMessage);
            Controls.Add(rtbChat);
            Controls.Add(lstUsers);
            Controls.Add(btnConnect);
            Controls.Add(txtUser);
            Controls.Add(txtPort);
            Controls.Add(txtServerIP);
            Name = "RemoteClientForm";
            Text = "Secure Chat Client";
            ResumeLayout(false);
            PerformLayout();
        }
    }
}
