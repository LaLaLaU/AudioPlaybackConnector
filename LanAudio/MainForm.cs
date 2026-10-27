using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace AudioPlaybackConnector.Lan;

internal sealed class MainForm : Form
{
    private readonly Preferences preferences = Preferences.Load();
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill, Padding = new Point(18, 10) };
    private readonly ComboBox sourceDevices = DeviceCombo(), outputDevices = DeviceCombo(), addresses = DeviceCombo();
    private readonly TextBox sendCode = new(), receiverCode = new(), allowedSender = new();
    private readonly ComboBox receivers = DeviceCombo();
    private readonly NumericUpDown port = new() { Minimum = 1024, Maximum = 65535 }, destinationPort = new() { Minimum = 1024, Maximum = 65535 }, bufferMs = new() { Minimum = 20, Maximum = 500, Increment = 20 };
    private readonly Label status = new() { AutoSize = true, Text = "就绪", ForeColor = Color.FromArgb(35, 84, 150), Font = new Font("Microsoft YaHei UI", 13, FontStyle.Bold) };
    private readonly Label metrics = new() { AutoSize = true, Text = "选择接收或发送模式，按开始。", ForeColor = Color.DimGray };
    private readonly TextBox log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9), BackColor = Color.FromArgb(246, 248, 251) };
    private readonly ProgressBar level = new() { Maximum = 100, Dock = DockStyle.Fill, Height = 9 };
    private readonly Button receiveButton = ActionButton("开始接收"), sendButton = ActionButton("开始发送"), stopButton = new() { Text = "停止", AutoSize = true, Enabled = false };
    private readonly Button refreshButton = new() { Text = "刷新设备 / IP", AutoSize = true };
    private readonly TrackBar volume = new() { Minimum = 0, Maximum = 100, Value = 100, TickFrequency = 20, Width = 220, Height = 34 };
    private readonly NotifyIcon tray = new() { Text = "AudioPlaybackConnector LAN", Visible = true };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
    private readonly List<Control> configuration = new();
    private AudioSender? sender;
    private AudioReceiver? receiver;
    private DiscoveryListener? discovery;
    private string? chosenReceiver;
    private bool busy, closing, allowClose;
    private DateTime lastPersistentLog = DateTime.MinValue;

    internal MainForm()
    {
        Text = "AudioPlaybackConnector · 局域网音频";
        Font = new Font("Microsoft YaHei UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(820, 820);
        MinimumSize = new Size(780, 780);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.White;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        tray.Icon = Icon;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22, 18, 22, 14), ColumnCount = 1, RowCount = 5 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 8));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        var header = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        header.Controls.Add(new Label { AutoSize = true, Text = "让另一台电脑的声音，在这里播放", Font = new Font(Font.FontFamily, 17, FontStyle.Bold) });
        header.Controls.Add(new Label { AutoSize = true, Text = $"{Environment.MachineName}  ·  家庭局域网  ·  无需虚拟声卡", ForeColor = Color.DimGray, Margin = new Padding(0, 7, 0, 0) });
        root.Controls.Add(header, 0, 0);
        root.Controls.Add(tabs, 0, 1);
        root.Controls.Add(level, 0, 2);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(2, 14, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85));
        footer.Controls.Add(status, 0, 0);
        footer.Controls.Add(metrics, 0, 1);
        stopButton.Dock = DockStyle.Fill;
        footer.Controls.Add(stopButton, 1, 0);
        footer.SetRowSpan(stopButton, 2);
        root.Controls.Add(footer, 0, 3);
        root.Controls.Add(new Label { AutoSize = true, Text = "APCL v1  ·  48 kHz / 双声道  ·  连接码加密传输  ·  关闭窗口即退出", ForeColor = Color.Gray }, 0, 4);
        Controls.Add(root);
        BuildReceiverTab();
        BuildSenderTab();
        BuildHelpTab();
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示窗口", null, (_, _) => RestoreWindow());
        menu.Items.Add("停止音频", null, async (_, _) => await StopAsync());
        menu.Items.Add("退出", null, (_, _) => Close());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => RestoreWindow();
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); };
        stopButton.Click += async (_, _) => await StopAsync();
        receiveButton.Click += async (_, _) => await StartReceiverAsync();
        sendButton.Click += async (_, _) => await StartSenderAsync();
        refreshButton.Click += (_, _) => RefreshDevices();
        volume.ValueChanged += (_, _) => { if (receiver is not null) receiver.Buffer.Volume = volume.Value / 100f; };
        timer.Tick += async (_, _) => await RefreshStatsAsync();
        FormClosing += OnClosing;
        chosenReceiver = string.IsNullOrWhiteSpace(preferences.TargetIp) ? null : preferences.TargetIp;
        receiverCode.Text = preferences.ReceiverCode;
        sendCode.Text = preferences.SenderCode;
        allowedSender.Text = preferences.AllowedSender;
        port.Value = Math.Clamp(preferences.Port, 1024, 65535);
        destinationPort.Value = port.Value;
        port.ValueChanged += (_, _) => { if (destinationPort.Value != port.Value) destinationPort.Value = port.Value; };
        destinationPort.ValueChanged += (_, _) => { if (port.Value != destinationPort.Value) port.Value = destinationPort.Value; };
        bufferMs.Value = Math.Clamp(preferences.BufferMs, 20, 500);
        RefreshDevices();
        Log("程序已就绪。接收电脑开始接收后会在局域网广播，发送页自动列出；没有自动开启音频或修改防火墙。");
        try { discovery = new DiscoveryListener(); }
        catch (SocketException) { Log($"发现端口 UDP {DiscoveryProtocol.Port} 被占用，仍可粘贴连接信息手动填写。"); }
        timer.Start();
    }

    private static ComboBox DeviceCombo() => new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private static Button ActionButton(string text) => new() { Text = text, AutoSize = true, Padding = new Padding(14, 7, 14, 7), BackColor = Color.FromArgb(34, 97, 192), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };

    private TableLayoutPanel MakePage(string title)
    {
        var page = new TabPage(title) { BackColor = Color.White, Padding = new Padding(18) };
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 0, AutoScroll = true };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.Controls.Add(table);
        tabs.TabPages.Add(page);
        return table;
    }

    private void AddRow(TableLayoutPanel page, string label, Control control, int height = 43, bool config = true)
    {
        int row = page.RowCount++;
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        page.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(65, 73, 85) }, 0, row);
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(3, 6, 3, 6);
        page.Controls.Add(control, 1, row);
        if (config) configuration.Add(control);
    }

    private static Label Hint(string text) => new() { Text = text, Dock = DockStyle.Fill, ForeColor = Color.DimGray, AutoSize = false, Padding = new Padding(0, 5, 0, 0) };

    private void BuildReceiverTab()
    {
        var page = MakePage("接收声音 · 当前电脑");
        AddRow(page, "本机接收 IP", addresses);
        AddRow(page, "接收端口", port);
        AddRow(page, "连接码", receiverCode);
        var buttons = new FlowLayoutPanel { WrapContents = false };
        var copy = new Button { Text = "复制连接信息", AutoSize = true };
        var renew = new Button { Text = "生成新连接码", AutoSize = true };
        copy.Click += (_, _) => CopyConnection();
        renew.Click += (_, _) => receiverCode.Text = AudioProtocol.NewPairingCode();
        buttons.Controls.Add(copy); buttons.Controls.Add(renew);
        AddRow(page, "", buttons, 43);
        AddRow(page, "声音播放到", outputDevices);
        AddRow(page, "接收缓冲（ms）", bufferMs);
        allowedSender.PlaceholderText = "可选：只接收 SUPERU 的 IP，例如 192.168.1.3";
        AddRow(page, "允许的发送 IP", allowedSender);
        AddRow(page, "接收音量", volume, 45, false);
        var actions = new FlowLayoutPanel(); actions.Controls.Add(receiveButton); actions.Controls.Add(refreshButton);
        AddRow(page, "", actions, 58, false);
        int row = page.RowCount++;
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var hint = Hint("先开始接收，再把连接信息粘贴到 SUPERU。\n有线网络可尝试 60–100 ms，Wi-Fi 可从 120–200 ms 开始。\n显示的 IP 供发送端选择；监听使用所有本机 IPv4 网卡。首版单向工作。");
        page.Controls.Add(hint, 0, row); page.SetColumnSpan(hint, 2);
    }

    private void BuildSenderTab()
    {
        var page = MakePage("发送声音 · SUPERU");
        AddRow(page, "采集的播放设备", sourceDevices);
        AddRow(page, "", Hint("采集输出到该设备的声音。开始后播放游戏、音乐或视频。"), 48, false);
        receivers.DropDownStyle = ComboBoxStyle.DropDown;
        receivers.SelectedIndexChanged += (_, _) => ApplyReceiver(receivers.SelectedItem as DiscoveredReceiver);
        receivers.Text = chosenReceiver ?? "";
        AddRow(page, "接收电脑", receivers);
        sendCode.PlaceholderText = "默认已填好，两台电脑保持一致即可";
        AddRow(page, "接收电脑连接码", sendCode);
        var paste = new Button { Text = "粘贴连接信息（自动填写 IP、端口、连接码）", AutoSize = true };
        paste.Click += (_, _) => PasteConnection();
        AddRow(page, "", paste, 46);
        AddRow(page, "接收电脑端口", destinationPort);
        var actions = new FlowLayoutPanel(); actions.Controls.Add(sendButton);
        AddRow(page, "", actions, 60, false);
        int row = page.RowCount++;
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var hint = Hint("连接两台电脑到同一个家庭网络，两边运行此程序。\n接收电脑点“开始接收”后会出现在上面的列表里，选中即填写 IP 和端口。\n发送状态只表示数据已发出，是否收到以接收端显示为准。\n环回采集会保留 SUPERU 本地播放；本机静音、独占模式及受保护内容可能影响采集。\n无需安装 Voicemeeter 或 VB-CABLE。当前协议不与 VBAN 互通。");
        page.Controls.Add(hint, 0, row); page.SetColumnSpan(hint, 2);
    }

    private void BuildHelpTab()
    {
        var page = MakePage("说明与日志");
        var bluetooth = new Button { Text = "打开原版蓝牙接收程序", AutoSize = true };
        bluetooth.Click += (_, _) => OpenBluetooth();
        AddRow(page, "蓝牙模式", bluetooth, 45, false);
        AddRow(page, "快速开始", Hint("① 当前电脑：接收页 → 开始接收。\n② SUPERU：发送页 → 从列表选择接收电脑 → 选择播放设备 → 开始发送。\n③ 当前电脑出现“接收中”，再检查耳机音量。换过连接码时再复制、粘贴连接信息。"), 90, false);
        AddRow(page, "收不到声音", Hint("列表里没有接收电脑时，确认它已点“开始接收”，并且两台电脑不在访客 Wi-Fi。\n家庭网络设为“专用”，出现 Windows 防火墙提示时允许此程序在专用网络通信。\nVPN / 代理可能影响路由，请使用实体网卡的家庭局域网 IP。\n音频使用 UDP 29864，发现使用 UDP 29865；程序不会自动更改网络、防火墙或系统音量。"), 115, false);
        var openLog = new Button { Text = "打开日志文件夹", AutoSize = true };
        openLog.Click += (_, _) => { Directory.CreateDirectory(Preferences.DirectoryPath); Process.Start(new ProcessStartInfo(Preferences.DirectoryPath) { UseShellExecute = true }); };
        AddRow(page, "诊断日志", openLog, 40, false);
        int row = page.RowCount++;
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.Controls.Add(log, 0, row); page.SetColumnSpan(log, 2);
    }

    private void RefreshDevices()
    {
        try
        {
            string oldSource = (sourceDevices.SelectedItem as AudioDevice)?.Id ?? preferences.SourceDeviceId;
            string oldOutput = (outputDevices.SelectedItem as AudioDevice)?.Id ?? preferences.OutputDeviceId;
            var devices = AudioDevices.ListOutputs();
            foreach (var (combo, oldId) in new[] { (sourceDevices, oldSource), (outputDevices, oldOutput) })
            {
                combo.Items.Clear(); combo.Items.AddRange(devices.Cast<object>().ToArray());
                int selected = devices.FindIndex(d => d.Id == oldId);
                if (combo.Items.Count > 0) combo.SelectedIndex = Math.Max(0, selected);
            }
            string? oldIp = (addresses.SelectedItem as LocalAddress)?.Address;
            LocalAddress[] ips = LocalNetwork.Endpoints();
            addresses.Items.Clear(); addresses.Items.AddRange(ips);
            if (ips.Length > 0) addresses.SelectedIndex = Math.Max(0, Array.FindIndex(ips, ip => ip.Address == oldIp));
            Log($"找到 {devices.Count} 个播放设备；本机 IP：{string.Join(" / ", ips.Select(ip => ip.ToString()))}。");
        }
        catch (Exception ex) { Log("无法枚举设备：" + ex.Message); }
    }

    private async Task StartReceiverAsync()
    {
        if (busy || sender is not null || receiver is not null) return;
        try
        {
            string id = SelectedDevice(outputDevices);
            string code = receiverCode.Text;
            IPAddress? expected = string.IsNullOrWhiteSpace(allowedSender.Text) ? null : LocalNetwork.ParseV4(allowedSender.Text);
            int selectedPort = (int)port.Value, selectedBuffer = (int)bufferMs.Value;
            SetBusy(true); SavePreferences();
            receiver = await Task.Run(() => new AudioReceiver(id, selectedPort, code, selectedBuffer, expected));
            receiver.Buffer.Volume = volume.Value / 100f;
            Log($"开始监听 UDP {selectedPort}，缓冲 {selectedBuffer} ms。等待发送电脑。");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private async Task StartSenderAsync()
    {
        if (busy || sender is not null || receiver is not null) return;
        try
        {
            string id = SelectedDevice(sourceDevices);
            IPAddress ip = LocalNetwork.ParseV4(receivers.Text);
            if (IPAddress.IsLoopback(ip) || LocalNetwork.Addresses().Contains(ip.ToString()))
                throw new ArgumentException("请填写另一台电脑的 IP。本程序不允许把声音发回本机，避免回声循环。");
            string code = sendCode.Text;
            int selectedPort = (int)port.Value;
            SetBusy(true); SavePreferences();
            sender = await Task.Run(() => new AudioSender(id, ip, selectedPort, code));
            Log($"开始发送到 {ip}:{selectedPort}。是否收到以接收电脑为准。");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private async Task StopAsync()
    {
        if (busy) return;
        SetBusy(true);
        AudioSender? oldSender = sender; sender = null;
        AudioReceiver? oldReceiver = receiver; receiver = null;
        try { await Task.Run(() => { try { oldSender?.Dispose(); } finally { oldReceiver?.Dispose(); } }); }
        catch (Exception ex) { Log("停止时遇到问题：" + ex.Message); }
        finally { SetBusy(false); level.Value = 0; status.Text = "已停止"; metrics.Text = "可以重新选择设备或切换发送 / 接收模式。"; }
        if (oldSender is not null || oldReceiver is not null) Log("音频已停止，连接已释放。");
    }

    private void SetBusy(bool value)
    {
        busy = value;
        bool running = sender is not null || receiver is not null;
        foreach (Control control in configuration) control.Enabled = !value && !running;
        receiveButton.Enabled = sendButton.Enabled = refreshButton.Enabled = !value && !running;
        stopButton.Enabled = !value && running;
        if (value) status.Text = "正在处理…";
    }

    private async Task RefreshStatsAsync()
    {
        if (busy || closing) return;
        string? failure = sender?.Failure ?? receiver?.Failure;
        if (failure is not null) { await StopAsync(); ShowError(new InvalidOperationException(failure)); return; }
        if (sender is not null)
        {
            SenderSnapshot s = sender.Snapshot();
            status.Text = s.HasRecentAudio ? "发送中 · 以接收端状态为准" : "正在采集 · 等待播放声音";
            metrics.Text = $"已发 {s.Packets:N0} 包 / {s.Bytes / 1048576d:F2} MB  ·  音量峰值 {s.Peak:P0}";
            level.Value = s.HasRecentAudio ? Math.Clamp((int)(s.Peak * 100), 0, 100) : 0;
        }
        if (receiver is not null)
        {
            ReceiverSnapshot r = receiver.Snapshot();
            status.Text = r.HasRecentAudio ? $"接收中 · 来自 {r.Peer}" : r.Packets == 0 ? "监听中 · 等待 SUPERU" : "监听中 · 发送端暂时没有声音";
            metrics.Text = $"已收 {r.Packets:N0} 包  ·  缓冲 {r.Buffer.Milliseconds:F0} ms  ·  补静音 {r.Buffer.MissingFrames / 48d:F0} ms  ·  无效包 {r.InvalidPackets:N0}";
            receiver.Buffer.Volume = volume.Value / 100f;
            level.Value = r.HasRecentAudio ? 55 : 0;
            if (r.InvalidPackets > 0 && r.Packets == 0) metrics.Text += "（请核对连接码）";
        }
        if ((sender is not null || receiver is not null) && DateTime.Now - lastPersistentLog > TimeSpan.FromSeconds(30))
        { Log(status.Text + "；" + metrics.Text); lastPersistentLog = DateTime.Now; }
        RefreshReceivers();
    }

    private void RefreshReceivers()
    {
        if (discovery is null || sender is not null || receiver is not null) return;
        if (discovery.Failure is not null) { Log("设备发现已停止：" + discovery.Failure); discovery.Dispose(); discovery = null; return; }
        DiscoveredReceiver[] found = discovery.Snapshot();
        string current = receivers.Text;
        bool same = receivers.Items.Count == found.Length && found.Zip(receivers.Items.Cast<DiscoveredReceiver>())
            .All(pair => pair.First.Address.Equals(pair.Second.Address) && pair.First.AudioPort == pair.Second.AudioPort && pair.First.Name == pair.Second.Name);
        if (same) return;
        receivers.BeginUpdate();
        receivers.Items.Clear();
        receivers.Items.AddRange(found.Cast<object>().ToArray());
        receivers.Text = current;
        int match = Array.FindIndex(found, item => item.Address.ToString() == chosenReceiver);
        if (match >= 0) receivers.SelectedIndex = match;
        receivers.EndUpdate();
        if (found.Length > 0 && string.IsNullOrWhiteSpace(current)) Log($"发现 {found.Length} 台接收电脑，可在发送页直接选择。");
    }

    private void ApplyReceiver(DiscoveredReceiver? receiver)
    {
        if (receiver is null) return;
        chosenReceiver = receiver.Address.ToString();
        destinationPort.Value = receiver.AudioPort;
        Log($"已选择 {receiver.Name}（{receiver.Address}:{receiver.AudioPort}）。");
    }

    private static string SelectedDevice(ComboBox combo) => (combo.SelectedItem as AudioDevice)?.Id ?? throw new InvalidOperationException("没有可用的播放设备，请先连接耳机或音箱，再刷新设备。");

    private void CopyConnection()
    {
        try
        {
            string ip = (addresses.SelectedItem as LocalAddress)?.Address ?? throw new InvalidOperationException("没有找到本机 IPv4 地址。");
            using var validate = new AudioProtocol(receiverCode.Text);
            Clipboard.SetText(new ConnectionInfo(LocalNetwork.ParseV4(ip), (int)port.Value, receiverCode.Text).Serialize());
            Log("连接信息已复制。到 SUPERU 的发送页点击“粘贴连接信息”。");
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void PasteConnection()
    {
        try
        {
            ConnectionInfo info = ConnectionInfo.Parse(Clipboard.GetText());
            chosenReceiver = info.Address.ToString(); receivers.Text = chosenReceiver; port.Value = info.Port; sendCode.Text = info.Code;
            Log("已填写接收 IP、端口和连接码。");
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void OpenBluetooth()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "AudioPlaybackConnector64.exe");
            if (!File.Exists(path))
            {
                using var picker = new OpenFileDialog { Title = "选择原版 AudioPlaybackConnector64.exe", Filter = "AudioPlaybackConnector|AudioPlaybackConnector64.exe|应用程序|*.exe" };
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                path = picker.FileName;
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            Log("已启动原版蓝牙接收程序，请从系统托盘选择手机。");
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void SavePreferences()
    {
        preferences.TargetIp = receivers.Text.Trim(); preferences.Port = (int)port.Value;
        preferences.BufferMs = (int)bufferMs.Value; preferences.ReceiverCode = receiverCode.Text.Trim();
        preferences.SenderCode = sendCode.Text.Trim();
        preferences.SourceDeviceId = (sourceDevices.SelectedItem as AudioDevice)?.Id ?? "";
        preferences.OutputDeviceId = (outputDevices.SelectedItem as AudioDevice)?.Id ?? "";
        preferences.AllowedSender = allowedSender.Text.Trim();
        try { preferences.Save(); } catch (Exception ex) { Log("设置未保存：" + ex.Message); }
    }

    private void Log(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        if (log.TextLength > 24000) log.Text = log.Text[^12000..];
        log.AppendText(line + Environment.NewLine);
        try
        {
            Directory.CreateDirectory(Preferences.DirectoryPath);
            string file = Path.Combine(Preferences.DirectoryPath, "app.log");
            if (File.Exists(file) && new FileInfo(file).Length > 1024 * 1024) File.Move(file, file + ".previous", true);
            File.AppendAllText(file, line + Environment.NewLine);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private void ShowError(Exception ex)
    {
        Log("错误：" + ex.Message);
        status.Text = "未能启动 / 已停止";
        MessageBox.Show(this, ex.Message + "\n\n可查看“说明与日志”排查。", "局域网音频", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void RestoreWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }

    private async void OnClosing(object? senderObject, FormClosingEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true;
        if (closing || busy) return;
        closing = true;
        timer.Stop(); SavePreferences();
        discovery?.Dispose(); discovery = null;
        await StopAsync();
        tray.Visible = false; tray.Dispose(); timer.Dispose();
        allowClose = true;
        // Always leave the original FormClosing callback before closing again;
        // stopping an idle engine can complete synchronously.
        BeginInvoke(new Action(Close));
    }
}
