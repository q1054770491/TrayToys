using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace TrayToys
{
    /// <summary>「管理电量协议」：列出 battery_protocols.json 里的条目，可删除。</summary>
    internal class ProtocolManageForm : Form
    {
        private ListBox lst;
        private Button btnDelete;
        private Button btnClose;

        public ProtocolManageForm()
        {
            this.Text = "管理电量协议";
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.AutoScaleMode = AutoScaleMode.None; // 必须先关自动缩放，再做像素布局
            this.ClientSize = new Size(500, 330);
            this.Font = new Font("Microsoft YaHei UI", 9f);

            Label title = new Label();
            title.Text = "已配置的 2.4G 设备（数据来自 battery_protocols.json）：";
            title.Location = new Point(12, 12);
            title.AutoSize = true;

            lst = new ListBox();
            lst.Location = new Point(12, 36);
            lst.Size = new Size(476, 220);
            lst.HorizontalScrollbar = true;

            btnDelete = new Button();
            btnDelete.Text = "删除选中";
            btnDelete.Location = new Point(12, 272);
            btnDelete.Size = new Size(90, 30);
            btnDelete.Click += BtnDelete_Click;

            btnClose = new Button();
            btnClose.Text = "关闭";
            btnClose.Location = new Point(398, 272);
            btnClose.Size = new Size(90, 30);
            btnClose.DialogResult = DialogResult.OK;

            this.Controls.Add(title);
            this.Controls.Add(lst);
            this.Controls.Add(btnDelete);
            this.Controls.Add(btnClose);
            this.CancelButton = btnClose;

            RefreshList();
        }

        private void RefreshList()
        {
            lst.Items.Clear();
            List<BatteryProtocol> list = BatteryProtocolStore.Protocols;
            for (int i = 0; i < list.Count; i++)
                lst.Items.Add(list[i].Describe());
            if (lst.Items.Count > 0) lst.SelectedIndex = 0;
        }

        private void BtnDelete_Click(object sender, EventArgs e)
        {
            int i = lst.SelectedIndex;
            if (i < 0 || i >= BatteryProtocolStore.Protocols.Count) return;

            BatteryProtocol p = BatteryProtocolStore.Protocols[i];
            if (MessageBox.Show("确定删除该协议？\r\n\r\n" + p.Describe(), "确认删除",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            BatteryProtocolStore.Remove(p);
            Logger.Info("已删除电量协议: " + p.Describe());
            RefreshList();
        }
    }
}
