using System.Drawing;
using System.Windows.Forms;

namespace ChatClient
{
    partial class ChatAppForm
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
                components.Dispose();
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        private TableLayoutPanel root;
        private Panel header;
        private Label headerTitle;

        private TableLayoutPanel body;
        private Panel leftPanel;
        private Label lblConvosTitle;

        private Panel rightPanel;

        private Panel chatHeader;
        private Label lblChatTitle;
        private Label lblChatSub;

        private Panel chatScrollHost;
        private FlowLayoutPanel chatFlow;

        private Panel inputPanel;
        private TextBox txtMessage;
        private Button btnSend;

        private TextBox txtServer;
        private TextBox txtPort;
        private TextBox txtUser;
        private Button btnConnect;
        private Label lblStatus;

        private ListView lvConvos;
        private ColumnHeader colName;
        private ColumnHeader colLast;
        private ColumnHeader colUnread;

        // ✅ NEW
        private Button btnCreateGroup;

        // ✅ NEW: context menu
        private ContextMenuStrip convoMenu;
        private ToolStripMenuItem miPin;
        private ToolStripMenuItem miUnpin;
        private ToolStripSeparator miSep1;
        private ToolStripMenuItem miLeaveGroup;
        private ToolStripSeparator miSep2;
        private ToolStripMenuItem miDeleteConvo;

        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();
            root = new TableLayoutPanel();
            header = new Panel();
            headerTitle = new Label();
            txtServer = new TextBox();
            txtPort = new TextBox();
            txtUser = new TextBox();
            btnConnect = new Button();
            lblStatus = new Label();
            body = new TableLayoutPanel();
            leftPanel = new Panel();
            lblConvosTitle = new Label();
            btnCreateGroup = new Button();
            lvConvos = new ListView();
            colName = new ColumnHeader();
            colLast = new ColumnHeader();
            colUnread = new ColumnHeader();
            convoMenu = new ContextMenuStrip(components);
            miPin = new ToolStripMenuItem();
            miUnpin = new ToolStripMenuItem();
            miSep1 = new ToolStripSeparator();
            miLeaveGroup = new ToolStripMenuItem();
            miSep2 = new ToolStripSeparator();
            miDeleteConvo = new ToolStripMenuItem();
            rightPanel = new Panel();
            chatHeader = new Panel();
            lblChatTitle = new Label();
            lblChatSub = new Label();
            inputPanel = new Panel();
            btnSend = new Button();
            txtMessage = new TextBox();
            chatScrollHost = new Panel();
            chatFlow = new FlowLayoutPanel();
            btnSendFile = new Button();
            root.SuspendLayout();
            header.SuspendLayout();
            body.SuspendLayout();
            leftPanel.SuspendLayout();
            convoMenu.SuspendLayout();
            rightPanel.SuspendLayout();
            chatHeader.SuspendLayout();
            inputPanel.SuspendLayout();
            chatScrollHost.SuspendLayout();
            SuspendLayout();
            // 
            // root
            // 
            root.BackColor = Color.White;
            root.ColumnCount = 1;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 20F));
            root.Controls.Add(header, 0, 0);
            root.Controls.Add(body, 0, 1);
            root.Dock = DockStyle.Fill;
            root.Location = new Point(0, 0);
            root.Name = "root";
            root.RowCount = 2;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.Size = new Size(1092, 700);
            root.TabIndex = 0;
            // 
            // header
            // 
            header.BackColor = Color.White;
            header.Controls.Add(headerTitle);
            header.Controls.Add(txtServer);
            header.Controls.Add(txtPort);
            header.Controls.Add(txtUser);
            header.Controls.Add(btnConnect);
            header.Controls.Add(lblStatus);
            header.Dock = DockStyle.Fill;
            header.Location = new Point(3, 3);
            header.Name = "header";
            header.Padding = new Padding(16, 10, 16, 10);
            header.Size = new Size(1086, 50);
            header.TabIndex = 0;
            header.Resize += Header_Resize;
            // 
            // headerTitle
            // 
            headerTitle.AutoSize = true;
            headerTitle.Font = new Font("Segoe UI", 11.5F, FontStyle.Bold, GraphicsUnit.Point);
            headerTitle.Location = new Point(0, 14);
            headerTitle.Name = "headerTitle";
            headerTitle.Size = new Size(209, 31);
            headerTitle.TabIndex = 0;
            headerTitle.Text = "Secure Chat Client";
            // 
            // txtServer
            // 
            txtServer.Location = new Point(215, 12);
            txtServer.Name = "txtServer";
            txtServer.Size = new Size(120, 35);
            txtServer.TabIndex = 1;
            txtServer.Text = "127.0.0.1";
            // 
            // txtPort
            // 
            txtPort.Location = new Point(341, 11);
            txtPort.Name = "txtPort";
            txtPort.Size = new Size(70, 35);
            txtPort.TabIndex = 2;
            txtPort.Text = "5000";
            // 
            // txtUser
            // 
            txtUser.Location = new Point(417, 11);
            txtUser.Name = "txtUser";
            txtUser.Size = new Size(140, 35);
            txtUser.TabIndex = 3;
            txtUser.Text = "Alice";
            // 
            // btnConnect
            // 
            btnConnect.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            btnConnect.Location = new Point(574, 3);
            btnConnect.Name = "btnConnect";
            btnConnect.Padding = new Padding(14, 6, 14, 6);
            btnConnect.Size = new Size(154, 47);
            btnConnect.TabIndex = 4;
            btnConnect.Text = "Connect";
            // 
            // lblStatus
            // 
            lblStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            lblStatus.Location = new Point(743, 12);
            lblStatus.Name = "lblStatus";
            lblStatus.Size = new Size(324, 28);
            lblStatus.TabIndex = 5;
            lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            // 
            // body
            // 
            body.BackColor = Color.White;
            body.ColumnCount = 2;
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330F));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            body.Controls.Add(leftPanel, 0, 0);
            body.Controls.Add(rightPanel, 1, 0);
            body.Dock = DockStyle.Fill;
            body.Location = new Point(3, 59);
            body.Name = "body";
            body.RowCount = 1;
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 20F));
            body.Size = new Size(1086, 638);
            body.TabIndex = 1;
            // 
            // leftPanel
            // 
            leftPanel.BackColor = Color.White;
            leftPanel.Controls.Add(lblConvosTitle);
            leftPanel.Controls.Add(btnCreateGroup);
            leftPanel.Controls.Add(lvConvos);
            leftPanel.Dock = DockStyle.Fill;
            leftPanel.Location = new Point(3, 3);
            leftPanel.Name = "leftPanel";
            leftPanel.Padding = new Padding(12, 10, 8, 12);
            leftPanel.Size = new Size(324, 632);
            leftPanel.TabIndex = 0;
            // 
            // lblConvosTitle
            // 
            lblConvosTitle.AutoSize = true;
            lblConvosTitle.Font = new Font("Segoe UI", 10.5F, FontStyle.Bold, GraphicsUnit.Point);
            lblConvosTitle.Location = new Point(0, 0);
            lblConvosTitle.Name = "lblConvosTitle";
            lblConvosTitle.Size = new Size(151, 30);
            lblConvosTitle.TabIndex = 0;
            lblConvosTitle.Text = "Conversations";
            // 
            // btnCreateGroup
            // 
            btnCreateGroup.AutoSize = true;
            btnCreateGroup.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            btnCreateGroup.Location = new Point(220, 0);
            btnCreateGroup.Name = "btnCreateGroup";
            btnCreateGroup.Padding = new Padding(10, 4, 10, 4);
            btnCreateGroup.Size = new Size(120, 48);
            btnCreateGroup.TabIndex = 2;
            btnCreateGroup.Text = "+ Group";
            btnCreateGroup.UseVisualStyleBackColor = true;
            // 
            // lvConvos
            // 
            lvConvos.Columns.AddRange(new ColumnHeader[] { colName, colLast, colUnread });
            lvConvos.ContextMenuStrip = convoMenu;
            lvConvos.FullRowSelect = true;
            lvConvos.Location = new Point(-3, 33);
            lvConvos.MultiSelect = false;
            lvConvos.Name = "lvConvos";
            lvConvos.Size = new Size(316, 599);
            lvConvos.TabIndex = 1;
            lvConvos.UseCompatibleStateImageBehavior = false;
            lvConvos.View = View.Details;
            lvConvos.SelectedIndexChanged += lvConvos_SelectedIndexChanged;
            // 
            // colName
            // 
            colName.Text = "Name";
            colName.Width = 70;
            // 
            // colLast
            // 
            colLast.Text = "Last";
            colLast.Width = 70;
            // 
            // colUnread
            // 
            colUnread.Text = "Unread";
            colUnread.Width = 80;
            // 
            // convoMenu
            // 
            convoMenu.ImageScalingSize = new Size(24, 24);
            convoMenu.Items.AddRange(new ToolStripItem[] { miPin, miUnpin, miSep1, miLeaveGroup, miSep2, miDeleteConvo });
            convoMenu.Name = "convoMenu";
            convoMenu.Size = new Size(312, 160);
            convoMenu.Opening += ConvoMenu_Opening;
            // 
            // miPin
            // 
            miPin.Name = "miPin";
            miPin.Size = new Size(311, 36);
            miPin.Text = "📌 Ghim";
            miPin.Click += MiPin_Click;
            // 
            // miUnpin
            // 
            miUnpin.Name = "miUnpin";
            miUnpin.Size = new Size(311, 36);
            miUnpin.Text = "📌 Bỏ ghim";
            miUnpin.Click += MiUnpin_Click;
            // 
            // miSep1
            // 
            miSep1.Name = "miSep1";
            miSep1.Size = new Size(308, 6);
            // 
            // miLeaveGroup
            // 
            miLeaveGroup.Name = "miLeaveGroup";
            miLeaveGroup.Size = new Size(311, 36);
            miLeaveGroup.Text = "🚪 Rời nhóm";
            miLeaveGroup.Click += MiLeaveGroup_Click;
            // 
            // miSep2
            // 
            miSep2.Name = "miSep2";
            miSep2.Size = new Size(308, 6);
            // 
            // miDeleteConvo
            // 
            miDeleteConvo.Name = "miDeleteConvo";
            miDeleteConvo.Size = new Size(311, 36);
            miDeleteConvo.Text = "🗑️ Xóa cuộc trò chuyện";
            miDeleteConvo.Click += MiDeleteConvo_Click;
            // 
            // rightPanel
            // 
            rightPanel.BackColor = Color.White;
            rightPanel.Controls.Add(chatHeader);
            rightPanel.Controls.Add(inputPanel);
            rightPanel.Controls.Add(chatScrollHost);
            rightPanel.Dock = DockStyle.Fill;
            rightPanel.Location = new Point(333, 3);
            rightPanel.Name = "rightPanel";
            rightPanel.Padding = new Padding(10, 10, 12, 12);
            rightPanel.Size = new Size(750, 632);
            rightPanel.TabIndex = 1;
            // 
            // chatHeader
            // 
            chatHeader.BackColor = Color.White;
            chatHeader.Controls.Add(lblChatTitle);
            chatHeader.Controls.Add(lblChatSub);
            chatHeader.Location = new Point(10, -7);
            chatHeader.Name = "chatHeader";
            chatHeader.Size = new Size(736, 80);
            chatHeader.TabIndex = 0;
            // 
            // lblChatTitle
            // 
            lblChatTitle.AutoSize = true;
            lblChatTitle.Font = new Font("Segoe UI", 11F, FontStyle.Bold, GraphicsUnit.Point);
            lblChatTitle.Location = new Point(3, 7);
            lblChatTitle.Name = "lblChatTitle";
            lblChatTitle.Size = new Size(61, 30);
            lblChatTitle.TabIndex = 0;
            lblChatTitle.Text = "Chat";
            // 
            // lblChatSub
            // 
            lblChatSub.AutoSize = true;
            lblChatSub.ForeColor = Color.Gray;
            lblChatSub.Location = new Point(0, 24);
            lblChatSub.Name = "lblChatSub";
            lblChatSub.Size = new Size(0, 30);
            lblChatSub.TabIndex = 1;
            // 
            // inputPanel
            // 
            inputPanel.BackColor = Color.White;
            inputPanel.Controls.Add(btnSendFile);
            inputPanel.Controls.Add(btnSend);
            inputPanel.Controls.Add(txtMessage);
            inputPanel.Dock = DockStyle.Bottom;
            inputPanel.Location = new Point(10, 560);
            inputPanel.Name = "inputPanel";
            inputPanel.Padding = new Padding(0, 10, 0, 0);
            inputPanel.Size = new Size(728, 60);
            inputPanel.TabIndex = 1;
            // 
            // btnSend
            // 
            btnSend.Dock = DockStyle.Right;
            btnSend.Location = new Point(618, 10);
            btnSend.MinimumSize = new Size(100, 34);
            btnSend.Name = "btnSend";
            btnSend.Size = new Size(110, 50);
            btnSend.TabIndex = 0;
            btnSend.Text = "Send";
            btnSend.Click += btnSend_Click;
            // 
            // txtMessage
            // 
            txtMessage.Dock = DockStyle.Fill;
            txtMessage.Font = new Font("Segoe UI", 10.5F, FontStyle.Regular, GraphicsUnit.Point);
            txtMessage.Location = new Point(0, 10);
            txtMessage.Multiline = true;
            txtMessage.Name = "txtMessage";
            txtMessage.Size = new Size(728, 50);
            txtMessage.TabIndex = 1;
            // 
            // chatScrollHost
            // 
            chatScrollHost.BackColor = Color.FromArgb(250, 250, 252);
            chatScrollHost.Controls.Add(chatFlow);
            chatScrollHost.Dock = DockStyle.Fill;
            chatScrollHost.Location = new Point(10, 10);
            chatScrollHost.Name = "chatScrollHost";
            chatScrollHost.Padding = new Padding(10);
            chatScrollHost.Size = new Size(728, 610);
            chatScrollHost.TabIndex = 2;
            // 
            // chatFlow
            // 
            chatFlow.AutoScroll = true;
            chatFlow.BackColor = Color.FromArgb(250, 250, 252);
            chatFlow.FlowDirection = FlowDirection.TopDown;
            chatFlow.Location = new Point(0, 61);
            chatFlow.Name = "chatFlow";
            chatFlow.Size = new Size(736, 493);
            chatFlow.TabIndex = 0;
            chatFlow.WrapContents = false;
            // 
            // btnSendFile
            // 
            btnSendFile.Location = new Point(508, 10);
            btnSendFile.Name = "btnSendFile";
            btnSendFile.Size = new Size(104, 50);
            btnSendFile.TabIndex = 1;
            btnSendFile.Text = "Send File";
            btnSendFile.UseVisualStyleBackColor = true;
            btnSendFile.Click += this.btnSendFile_Click;

            // 
            // ChatAppForm
            // 
            AutoScaleDimensions = new SizeF(144F, 144F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1092, 700);
            Controls.Add(root);
            MinimumSize = new Size(980, 620);
            Name = "ChatAppForm";
            StartPosition = FormStartPosition.CenterScreen;
            Text = "Secure Chat Client";
            Load += ChatAppForm_Load;
            root.ResumeLayout(false);
            header.ResumeLayout(false);
            header.PerformLayout();
            body.ResumeLayout(false);
            leftPanel.ResumeLayout(false);
            leftPanel.PerformLayout();
            convoMenu.ResumeLayout(false);
            rightPanel.ResumeLayout(false);
            chatHeader.ResumeLayout(false);
            chatHeader.PerformLayout();
            inputPanel.ResumeLayout(false);
            inputPanel.PerformLayout();
            chatScrollHost.ResumeLayout(false);
            ResumeLayout(false);
        }

        #endregion

        private Button btnSendFile;
    }
}
