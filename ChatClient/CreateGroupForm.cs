using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace ChatClient
{
    public sealed class CreateGroupForm : Form
    {
        public sealed class UserItem
        {
            public string ClientId { get; set; } = "";
            public string Name { get; set; } = "";
            public bool Online { get; set; }

            public override string ToString() => Online ? Name : $"{Name} (offline)";
        }

        private readonly TextBox _txtRoomName;
        private readonly CheckedListBox _chkUsers;

        public string RoomName => _txtRoomName.Text.Trim();

        public List<string> SelectedClientIds =>
            _chkUsers.CheckedItems
                .OfType<UserItem>()
                .Select(x => x.ClientId)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct()
                .ToList();

        public CreateGroupForm(IEnumerable<UserItem> users)
        {
            Text = "Create Group";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(420, 520);

            var lblName = new Label { Text = "Group name", AutoSize = true, Location = new Point(12, 12) };
            _txtRoomName = new TextBox { Location = new Point(12, 40), Width = 390 };

            var lblMembers = new Label { Text = "Members (tick 2+ people)", AutoSize = true, Location = new Point(12, 80) };
            _chkUsers = new CheckedListBox
            {
                Location = new Point(12, 108),
                Size = new Size(390, 340),
                CheckOnClick = true
            };

            foreach (var u in users.OrderByDescending(x => x.Online).ThenBy(x => x.Name))
                _chkUsers.Items.Add(u, false);

            var btnOk = new Button { Text = "Create", Location = new Point(232, 464), Size = new Size(80, 34), DialogResult = DialogResult.OK };
            var btnCancel = new Button { Text = "Cancel", Location = new Point(322, 464), Size = new Size(80, 34), DialogResult = DialogResult.Cancel };

            Controls.Add(lblName);
            Controls.Add(_txtRoomName);
            Controls.Add(lblMembers);
            Controls.Add(_chkUsers);
            Controls.Add(btnOk);
            Controls.Add(btnCancel);

            AcceptButton = btnOk;
            CancelButton = btnCancel;
        }
    }
}
