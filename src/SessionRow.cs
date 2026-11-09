using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace TrayToys
{
    /// <summary>
    /// 「应用音量」页里的一个应用行：图标 + 名称 + 音量滑块 + 百分比 + 静音。
    ///
    /// 同一个应用可能有多路音频会话（例如浏览器多个标签），这里按 AppKey 合并成一行，
    /// 拖动滑块时对该应用的所有会话一起生效。
    /// 已暂停（Inactive）的应用用灰色文字表示，但仍然可以调节音量。
    /// </summary>
    internal class SessionRow : Panel
    {
        private const int RowWidth = 384;
        private const int RowHeight = 40;

        private readonly Label nameLabel;
        private readonly TrackBar slider;
        private readonly Label percentLabel;
        private readonly CheckBox muteBox;
        private readonly Button initButton;

        private List<AudioSessionInfo> targets = new List<AudioSessionInfo>();
        private bool updating; // 程序更新控件时抑制回写，避免"设控件 → 触发事件 → 再设音量"的循环

        internal string AppKey;

        internal SessionRow(AudioSessionInfo sample, Image icon)
        {
            this.AppKey = sample.AppKey;
            this.Size = new Size(RowWidth, RowHeight);
            this.Margin = new Padding(0, 0, 0, 2);

            PictureBox iconBox = new PictureBox();
            iconBox.Location = new Point(2, 11);
            iconBox.Size = new Size(18, 18);
            iconBox.SizeMode = PictureBoxSizeMode.Zoom;
            if (icon != null) iconBox.Image = icon;

            nameLabel = new Label();
            nameLabel.Location = new Point(24, 11);
            nameLabel.Size = new Size(88, 18);
            nameLabel.AutoEllipsis = true;
            nameLabel.Text = sample.IsSystemSounds ? "系统提示音" : sample.ProcessName;

            slider = new TrackBar();
            slider.Location = new Point(114, 5);
            slider.Size = new Size(100, 30);
            slider.AutoSize = false;
            slider.Minimum = 0;
            slider.Maximum = 100;
            slider.TickStyle = TickStyle.None;
            slider.SmallChange = 1;
            slider.LargeChange = 5;

            percentLabel = new Label();
            percentLabel.Location = new Point(216, 11);
            percentLabel.Size = new Size(42, 18);
            percentLabel.TextAlign = ContentAlignment.MiddleRight;

            muteBox = new CheckBox();
            muteBox.Text = "静音";
            muteBox.Location = new Point(260, 10);
            muteBox.Size = new Size(48, 20);

            initButton = new Button();
            initButton.Text = "初始化";
            initButton.Location = new Point(310, 7);
            initButton.Size = new Size(72, 26);
            initButton.Click += delegate { ResetToDefault(); };

            slider.ValueChanged += delegate
            {
                if (updating) return;
                percentLabel.Text = slider.Value + "%";
                ApplyVolume(slider.Value / 100f);
            };

            muteBox.CheckedChanged += delegate
            {
                if (updating) return;
                ApplyMute(muteBox.Checked);
            };

            this.Controls.Add(iconBox);
            this.Controls.Add(nameLabel);
            this.Controls.Add(slider);
            this.Controls.Add(percentLabel);
            this.Controls.Add(muteBox);
            this.Controls.Add(initButton);
        }

        /// <summary>用最新枚举到的会话数据刷新本行显示。</summary>
        internal void UpdateFrom(List<AudioSessionInfo> group)
        {
            targets = group;

            bool active = false;
            for (int i = 0; i < group.Count; i++)
            {
                if (group[i].IsActive) active = true;
            }

            AudioSessionInfo first = group[0];
            Color fore = active ? SystemColors.ControlText : SystemColors.GrayText;
            // 颜色没变就不要设：属性 setter 会触发 Invalidate，每秒一次纯属白白重绘
            if (nameLabel.ForeColor != fore)
            {
                nameLabel.ForeColor = fore;
                percentLabel.ForeColor = fore;
            }

            int pct = (int)Math.Round(first.Volume * 100f);

            updating = true;
            // 与滑块当前值相差 2% 以上才覆盖：用户正在拖动时不会被刷新打断
            if (Math.Abs(slider.Value - pct) > 2) slider.Value = pct;
            if (muteBox.Checked != first.Muted) muteBox.Checked = first.Muted;
            // 文本同理：没变就别设，否则每秒都在重绘这个 Label
            string pctText = slider.Value + "%";
            if (percentLabel.Text != pctText) percentLabel.Text = pctText;
            updating = false;
        }

        /// <summary>把这个应用的所有会话恢复为满音量 100% 并取消静音。</summary>
        internal void ResetToDefault()
        {
            for (int i = 0; i < targets.Count; i++)
            {
                AudioSessionManager.SetVolume(targets[i], 1f);
                AudioSessionManager.SetMute(targets[i], false);
            }

            updating = true;
            slider.Value = 100;
            muteBox.Checked = false;
            percentLabel.Text = "100%";
            updating = false;
        }

        private void ApplyVolume(float volume)
        {
            for (int i = 0; i < targets.Count; i++)
            {
                AudioSessionManager.SetVolume(targets[i], volume);
            }

            // 调成非默认音量后才需要看守：该应用被关闭再打开时要恢复 100%。
            // （调回 100% 时不用通知 —— 守卫自己会判定"没事可做"并暂停轮询。）
            if (volume < 0.995f) SessionResetKeeper.NotifyVolumeChanged();
        }

        private void ApplyMute(bool mute)
        {
            for (int i = 0; i < targets.Count; i++)
            {
                AudioSessionManager.SetMute(targets[i], mute);
            }
            if (mute) SessionResetKeeper.NotifyVolumeChanged();
        }
    }
}
