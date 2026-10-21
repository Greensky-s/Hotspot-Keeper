using Microsoft.Win32;
using QRCoder;
using System.Diagnostics;
using System.Threading;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using System.Windows.Forms;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace HotspotKeeper;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\HotspotKeeper2_SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("Hotspot Keeper가 이미 실행 중입니다. 알림 영역에서 열어 주세요.", "Hotspot Keeper");
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args.Any(x => x.Equals("--background", StringComparison.OrdinalIgnoreCase))));
    }
}

public sealed class MainForm : Form
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "HotspotKeeper";
    const string PrefKey = @"Software\HotspotKeeper"; // 2.0 settings migration / compatibility
    readonly string logFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HotspotKeeper", "events.log");
    readonly SemaphoreSlim gate = new(1, 1);
    readonly System.Windows.Forms.Timer watchdog = new();
    readonly System.Windows.Forms.Timer networkDebounce = new() { Interval = 4000 };
    readonly System.Windows.Forms.Timer countdown = new() { Interval = 1000 };
    readonly NotifyIcon tray;
    readonly Label state = new() { Font = new Font("Malgun Gothic", 15, FontStyle.Bold), ForeColor = Color.FromArgb(24, 125, 72), AutoSize = false, Size = new Size(490, 36) };
    readonly Label details = new() { AutoSize = false, ForeColor = Color.DimGray, Size = new Size(490, 26) };
    readonly Label last = new() { AutoSize = false, Size = new Size(350, 26) };
    readonly Label next = new() { AutoSize = false, Size = new Size(155, 32), Font = new Font("Malgun Gothic", 14, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter };
    readonly Label clients = new() { AutoSize = false, Size = new Size(155, 32), Font = new Font("Malgun Gothic", 14, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter };
    readonly CheckBox enabled = new() { Text = "핫스팟 자동 복구", AutoSize = true };
    readonly CheckBox startup = new() { Text = "Windows 로그인 시 자동 실행", AutoSize = true };
    readonly ComboBox frequency = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    readonly Button inspect = new() { Text = "↻  지금 점검 및 복구", Height = 44, Width = 700 };
    readonly TextBox ssid = new() { Width = 510, MaxLength = 32 };
    readonly TextBox password = new() { Width = 510, UseSystemPasswordChar = true, MaxLength = 63 };
    readonly CheckBox reveal = new() { Text = "비밀번호 표시", AutoSize = true };
    readonly Button apply = new() { Text = "SSID / 비밀번호 적용", Width = 200, Height = 36 };
    readonly Button showQr = new() { Text = "연결용 QR 표시", Width = 160, Height = 36 };
    readonly Button windowsSettings = new() { Text = "Windows 핫스팟 설정 열기", Width = 220, Height = 36 };
    readonly TextBox logView = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9) };
    readonly ComboBox retention = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly System.Windows.Forms.Timer logMaintenance = new() { Interval = 60 * 60 * 1000 };
    string lastDiagnosisCode = "OK";
    string lastDiagnosisDetail = "현재까지 진단할 오류가 없습니다.";
    DateTime lastDiagnosisAt = DateTime.MinValue;
    DateTime nextTick = DateTime.MinValue;
    DateTime lastTick = DateTime.MinValue;
    int failures = 0;
    DateTime retryAfter = DateTime.MinValue;
    DateTime lastReactiveCheck = DateTime.MinValue;
    string? previousStatusKey;
    const long MaxLogBytes = 1024 * 1024; // rotate at ~1 MiB
    const int MaxLogArchives = 3;
    bool closing = false;
    bool loading = true;
    bool suspendRecovery = false;
    readonly bool startHidden;
    int intervalMinutes => frequency.SelectedItem is int n ? n : 3;
    int RetentionDays => retention.SelectedItem is RetentionOption option ? option.Days : 30;
    private sealed record RetentionOption(int Days, string Label)
    {
        public override string ToString() => Label;
    }


    public MainForm(bool background)
    {
        startHidden = background;
        Text = "Hotspot Keeper 2.6";
        ClientSize = new Size(760, 646);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Malgun Gothic", 9.5f);
        BackColor = Color.FromArgb(248, 250, 253);
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        BuildShell();
        var menu = new ContextMenuStrip();
        menu.Items.Add("관리 창 열기", null, (_, _) => OpenWindow());
        menu.Items.Add("지금 점검 및 복구", null, async (_, _) => await CheckAsync(true));
        menu.Items.Add("완전히 종료", null, (_, _) => { closing = true; Close(); });
        tray = new NotifyIcon { Icon = Icon, Text = "Hotspot Keeper 2.6", Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => OpenWindow();
        retention.Items.AddRange(new object[] {
            new RetentionOption(7, "7일"), new RetentionOption(30, "30일"),
            new RetentionOption(90, "90일"), new RetentionOption(180, "180일"),
            new RetentionOption(0, "자동 삭제 안 함")
        });
        retention.SelectedIndex = 1;
        retention.SelectedIndexChanged += (_, _) => {
            if (!loading) { Persist(); PruneLogs(); RefreshLog(); }
        };
        logMaintenance.Tick += (_, _) => PruneLogs();
        logMaintenance.Start();
        frequency.Items.AddRange(new object[] { 1, 3, 5, 10 });
        frequency.Format += (_, e) => { if (e.ListItem is int i) e.Value = $"{i}분마다"; };
        frequency.FormattingEnabled = true;
        frequency.SelectedItem = 3;
        StyleAction(inspect, true);
        inspect.Click += async (_, _) => await CheckAsync(true);
        enabled.CheckedChanged += (_, _) => { if (!loading) { Persist(); ResetWatchdog(); } };
        frequency.SelectedIndexChanged += (_, _) => { if (!loading) { Persist(); ResetWatchdog(); } };
        startup.CheckedChanged += (_, _) => { if (!loading) SetStartup(startup.Checked); };
        reveal.CheckedChanged += (_, _) => password.UseSystemPasswordChar = !reveal.Checked;
        apply.Click += async (_, _) => await ApplyNetworkAsync();
        showQr.Click += (_, _) => ShowQrDialog();
        windowsSettings.Click += (_, _) => OpenWindowsSettings();
        watchdog.Tick += async (_, _) => await CheckAsync();
        networkDebounce.Tick += async (_, _) =>
        {
            networkDebounce.Stop();
            if (!enabled.Checked || suspendRecovery || IsDisposed) return;
            if ((DateTime.Now - lastReactiveCheck).TotalSeconds < 20) return;
            lastReactiveCheck = DateTime.Now;
            await CheckAsync();
        };
        // Windows may fire network events on a background thread; marshal to UI.
        NetworkInformation.NetworkStatusChanged += NetworkStatusChanged;
        countdown.Tick += (_, _) => next.Text = enabled.Checked && nextTick != DateTime.MinValue
            ? FormatRemainingTime((int)Math.Ceiling((nextTick - DateTime.Now).TotalSeconds)) : "중지됨";
        countdown.Start();
        Microsoft.Win32.SystemEvents.PowerModeChanged += PowerModeChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch += SessionSwitched;
        FormClosing += (_, e) => { if (!closing && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        FormClosed += (_, _) =>
        {
            SystemEvents.PowerModeChanged -= PowerModeChanged;
            SystemEvents.SessionSwitch -= SessionSwitched;
            NetworkInformation.NetworkStatusChanged -= NetworkStatusChanged;
            networkDebounce.Stop(); networkDebounce.Dispose();
            watchdog.Stop(); countdown.Stop(); logMaintenance.Stop(); logMaintenance.Dispose(); tray.Visible = false; tray.Dispose();
        };
        LoadPreferences();
        loading = false;
        ResetWatchdog();
        PruneLogs();
        RefreshLog();
        Shown += async (_, _) => { if (startHidden) Hide(); await CheckAsync(); await LoadNetworkAsync(); };
    }

    static string FormatRemainingTime(int seconds)
    {
        seconds = Math.Max(0, seconds);
        int minutes = seconds / 60;
        int remainingSeconds = seconds % 60;
        return minutes == 0 ? $"{remainingSeconds}초" : $"{minutes}분 {remainingSeconds}초";
    }

    readonly Panel viewHost = new() { Location = new Point(0, 115), Size = new Size(760, 531) };
    readonly Panel[] views = new Panel[3];
    readonly Button[] navButtons = new Button[3];
    static readonly Color Ink = Color.FromArgb(25, 36, 57);
    static readonly Color Muted = Color.FromArgb(100, 112, 133);
    static readonly Color Blue = Color.FromArgb(22, 86, 207);
    static readonly Color Pale = Color.FromArgb(243, 247, 253);

    void BuildShell()
    {
        var brand = new Label { Text = "Hotspot Keeper", Font = new Font("Malgun Gothic", 17, FontStyle.Bold), ForeColor = Ink, Location = new Point(66, 14), AutoSize = true };
        var subtitle = new Label { Text = "모바일 핫스팟 관리자 · v2.6", Font = new Font("Malgun Gothic", 9), ForeColor = Muted, Location = new Point(68, 48), AutoSize = true };
        var image = new PictureBox { Image = (Icon ?? SystemIcons.Application).ToBitmap(), Location = new Point(18, 14), Size = new Size(39,39), SizeMode = PictureBoxSizeMode.Zoom };
        var badge = L("●  자동 감시 중", 9, Color.FromArgb(27, 135, 86), 560, 26, 178, 29, true);
        Controls.AddRange(new Control[] { brand, subtitle, image, badge });
        var names = new[] { "대시보드", "네트워크 설정", "활동 기록" };
        for (int i = 0; i < 3; i++)
        {
            int idx = i;
            var button = new Button { Text = names[i], Location = new Point(18 + i * 244, 73), Size = new Size(236, 35), TabStop = false, Cursor = Cursors.Hand, FlatStyle = FlatStyle.Flat, Font = new Font("Malgun Gothic", 10f) };
            button.FlatAppearance.BorderSize = 0;
            button.Click += (_, _) => SwitchView(idx);
            navButtons[i] = button;
            Controls.Add(button);
        }
        views[0] = BuildDashboard();
        views[1] = BuildConfiguration();
        views[2] = BuildLog();
        foreach (var view in views) { view.Dock = DockStyle.Fill; viewHost.Controls.Add(view); }
        Controls.Add(viewHost);
        SwitchView(0);
    }

    void SwitchView(int index)
    {
        for (int i = 0; i < views.Length; i++)
        {
            views[i].Visible = i == index;
            navButtons[i].BackColor = i == index ? Color.FromArgb(231, 240, 255) : Color.FromArgb(244, 245, 247);
            navButtons[i].ForeColor = i == index ? Blue : Muted;
            navButtons[i].Font = new Font("Malgun Gothic", 10f, i == index ? FontStyle.Bold : FontStyle.Regular);
        }
        views[index].BringToFront();
        if (index == 2) RefreshLog();
    }

    static Label L(string text, float size, Color color, int x, int y, int width, int height, bool bold = false)
        => new() { Text = text, Font = new Font("Malgun Gothic", size, bold ? FontStyle.Bold : FontStyle.Regular), ForeColor = color, Location = new Point(x,y), Size = new Size(width,height), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };

    static Panel Card(int x, int y, int width, int height, Color? color = null)
        => new SoftCard { Location = new Point(x,y), Size = new Size(width,height), BackColor = color ?? Color.White };

    static void StyleAction(Button button, bool primary = false)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.FlatAppearance.BorderColor = Color.FromArgb(223, 228, 238);
        button.BackColor = primary ? Color.FromArgb(22, 33, 52) : Color.White;
        button.ForeColor = primary ? Color.White : Ink;
        button.Font = new Font("Malgun Gothic", 10, FontStyle.Bold);
        button.Cursor = Cursors.Hand;
    }

    Panel BuildDashboard()
    {
        var root = new Panel { BackColor = BackColor };
        var statusCard = Card(18, 4, 724, 104);
        statusCard.Controls.Add(L("현재 핫스팟 상태", 9, Muted, 20, 12, 260, 22, true));
        state.Location = new Point(20, 34);
        state.Size = new Size(645, 34);
        details.Location = new Point(20, 71);
        statusCard.Controls.AddRange(new Control[]{state, details});
        root.Controls.Add(statusCard);

        string[] headers={"연결 기기", "점검 주기", "다음 점검"};
        for (int i=0;i<3;i++)
        {
            var c=Card(18+i*244, 121, 236, 96, Pale);
            c.Controls.Add(L(headers[i],9,Muted,16,10,200,25));
            if (i==0) { clients.Location=new Point(16,37);clients.TextAlign=ContentAlignment.MiddleLeft; c.Controls.Add(clients); }
            if (i==1)
            {
                var minute = L("3분마다", 14, Ink, 16, 37, 180, 34, true);
                c.Controls.Add(minute);
                frequency.SelectedIndexChanged += (_,_) => minute.Text = $"{intervalMinutes}분마다";
                minute.Text = $"{intervalMinutes}분마다";
            }
            if (i==2) { next.Location=new Point(16,37);next.TextAlign=ContentAlignment.MiddleLeft;c.Controls.Add(next); }
            root.Controls.Add(c);
        }

        inspect.Location = new Point(18, 228);
        inspect.Size = new Size(724, 44);
        root.Controls.Add(inspect);
        root.Controls.Add(L("자동 관리 설정", 12, Ink, 20, 289, 300, 29, true));
        var settings=Card(18, 323, 724, 165);
        enabled.Location=new Point(19, 17); enabled.Size=new Size(310, 28);enabled.Font=new Font("Malgun Gothic",10);enabled.AutoSize=false;
        startup.Location=new Point(19, 65);startup.Size=new Size(340,28);startup.Font=new Font("Malgun Gothic",10);startup.AutoSize=false;
        settings.Controls.AddRange(new Control[] { enabled, startup });
        settings.Controls.Add(L("핫스팟이 꺼지면 다시 활성화합니다.",9,Muted,40,43,380,18));
        settings.Controls.Add(L("Windows 로그인 후 자동으로 실행합니다.",9,Muted,40,93,380,18));
        settings.Controls.Add(L("자동 점검 주기",9.5f,Ink,19,125,150,28,true));
        frequency.Location=new Point(510,125);frequency.Size=new Size(185,30);
        settings.Controls.Add(frequency);
        root.Controls.Add(settings);
        last.Location = new Point(20,498);last.Size=new Size(650,23);last.ForeColor=Muted;
        root.Controls.Add(last);
        return root;
    }

    Panel BuildConfiguration()
    {
        var root = new Panel { BackColor=BackColor };
        root.Controls.Add(L("핫스팟 네트워크 설정",15,Ink,25,12,690,38,true));
        root.Controls.Add(L("네트워크 이름과 비밀번호를 관리하고 휴대전화 연결용 QR을 생성합니다.",9.3f,Muted,25,50,700,24));
        var form=Card(18,87,724,307);
        form.Controls.Add(L("네트워크 이름 (SSID)",10,Ink,20,14,440,28,true));
        ssid.Location=new Point(20,46);ssid.Size=new Size(675,31);
        form.Controls.Add(ssid);
        form.Controls.Add(L("UTF-8 최대 32바이트",8.5f,Muted,20,79,440,21));
        form.Controls.Add(L("Wi-Fi 비밀번호",10,Ink,20,111,440,28,true));
        password.Location=new Point(20,143);password.Size=new Size(675,31);
        form.Controls.Add(password);
        reveal.Location=new Point(20,180); reveal.Size=new Size(180,25); reveal.AutoSize=false;
        form.Controls.Add(reveal);
        apply.Location=new Point(20,226);apply.Size=new Size(330,42);StyleAction(apply,true);
        showQr.Location=new Point(363,226);showQr.Size=new Size(332,42);StyleAction(showQr);
        form.Controls.AddRange(new Control[]{apply,showQr});
        root.Controls.Add(form);
        var note=Card(18,406,724,109,Pale);
        note.Controls.Add(L("안내",10,Ink,18,10,120,25,true));
        note.Controls.Add(L("설정 변경 시 기존 연결이 끊어질 수 있습니다. QR 코드에는 비밀번호가 포함됩니다.",9,Muted,18,36,682,22));
        note.Controls.Add(L("비밀번호는 앱 로그나 레지스트리에 저장하지 않습니다. Windows API 권한이 필요할 수 있습니다.",9,Muted,18,62,682,22));
        windowsSettings.Location=new Point(500,11);windowsSettings.Size=new Size(202,30);StyleAction(windowsSettings);
        // Separate settings button to avoid overlapping note title or text.
        root.Controls.Add(note);
        return root;
    }

    Panel BuildLog()
    {
        var root = new Panel { BackColor = BackColor };
        root.Controls.Add(L("활동 기록", 15, Ink, 25, 12, 500, 36, true));
        root.Controls.Add(L("복구 내역을 확인하고 보관 기간을 직접 관리할 수 있습니다.", 9, Muted, 25, 51, 690, 24));

        var refresh = new Button { Text = "기록 새로고침", Location = new Point(20, 89), Size = new Size(140, 38) };
        var open = new Button { Text = "로그 폴더 열기", Location = new Point(171, 89), Size = new Size(145, 38) };
        var diagnostic = new Button { Text = "문제 진단", Location = new Point(327, 89), Size = new Size(135, 38) };
        var delete = new Button { Text = "전체 기록 삭제", Location = new Point(582, 89), Size = new Size(156, 38) };
        StyleAction(refresh); StyleAction(open); StyleAction(diagnostic); StyleAction(delete);
        refresh.Click += (_, _) => { PruneLogs(); RefreshLog(); };
        open.Click += (_, _) => {
            Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.GetDirectoryName(logFile)}\"") { UseShellExecute = true });
        };
        diagnostic.Click += (_, _) => ShowDiagnosis();
        delete.Click += (_, _) => DeleteAllLogs();
        root.Controls.AddRange(new Control[] { refresh, open, diagnostic, delete });

        var settings = Card(18, 140, 724, 86, Pale);
        settings.Controls.Add(L("자동 보관 기간", 10, Ink, 18, 11, 180, 27, true));
        settings.Controls.Add(L("선택한 기간보다 오래된 기록을 정리합니다. 자동 삭제 안 함도 선택할 수 있습니다.", 8.5f, Muted, 18, 43, 470, 26));
        retention.Location = new Point(530, 23);
        retention.Size = new Size(170, 28);
        settings.Controls.Add(retention);
        root.Controls.Add(settings);

        var card = Card(18, 240, 724, 273);
        logView.Location = new Point(15, 15);
        logView.Size = new Size(694, 243);
        logView.Dock = DockStyle.None;
        logView.BackColor = Color.White;
        logView.BorderStyle = BorderStyle.None;
        card.Controls.Add(logView);
        root.Controls.Add(card);
        return root;
    }

    void OpenWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }

    void LoadPreferences()
    {
        try
        {
            using var prefs = Registry.CurrentUser.CreateSubKey(PrefKey);
            enabled.Checked = Convert.ToString(prefs?.GetValue("Enabled", 1)) == "1";
            int n = Convert.ToInt32(prefs?.GetValue("Interval", 3));
            frequency.SelectedItem = frequency.Items.Contains(n) ? n : 3;
            int savedRetention = Convert.ToInt32(prefs?.GetValue("LogRetentionDays", 30));
            retention.SelectedItem = retention.Items.Cast<RetentionOption>().FirstOrDefault(v => v.Days == savedRetention) ?? retention.Items[1];
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            startup.Checked = run?.GetValue(RunName) is string;
        }
        catch (Exception ex) { WriteLog("설정 읽기 실패: " + ex.Message); }
    }
    void Persist()
    {
        try
        {
            using var prefs = Registry.CurrentUser.CreateSubKey(PrefKey);
            prefs?.SetValue("Enabled", enabled.Checked ? 1 : 0);
            prefs?.SetValue("Interval", intervalMinutes);
            prefs?.SetValue("LogRetentionDays", RetentionDays);
        }
        catch (Exception ex) { WriteLog("설정 저장 실패: " + ex.Message); }
    }
    void SetStartup(bool value)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) run?.SetValue(RunName, $"\"{Application.ExecutablePath}\" --background");
            else run?.DeleteValue(RunName, false);
        }
        catch (Exception ex)
        {
            MessageBox.Show("자동 시작 설정 실패: " + ex.Message);
            loading = true; startup.Checked = !value; loading = false;
        }
    }
    void ResetWatchdog()
    {
        watchdog.Stop();
        if (!enabled.Checked) { nextTick = DateTime.MinValue; return; }
        watchdog.Interval = intervalMinutes * 60000;
        watchdog.Start();
        nextTick = DateTime.Now.AddMinutes(intervalMinutes);
    }
    void PowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) BeginInvoke(new Action(async () => { await Task.Delay(3500); await CheckAsync(); }));
    }
    void SessionSwitched(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionUnlock) BeginInvoke(new Action(async () => { await Task.Delay(1500); await CheckAsync(); }));
    }

    void NetworkStatusChanged(object sender)
    {
        try
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            BeginInvoke(new Action(() =>
            {
                if (!enabled.Checked || suspendRecovery) return;
                networkDebounce.Stop();
                networkDebounce.Start();
            }));
        }
        catch (InvalidOperationException) { /* handle was destroyed */ }
    }

    NetworkOperatorTetheringManager Manager()
    {
        var profile = NetworkInformation.GetInternetConnectionProfile()
            ?? throw new InvalidOperationException("인터넷 연결 프로필이 없습니다.");
        return NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
    }
    async Task CheckAsync(bool manual = false)
    {
        if (!await gate.WaitAsync(0)) return;
        inspect.Enabled = false;
        try
        {
            if (suspendRecovery) return;
            var manager = Manager();
            bool on = manager.TetheringOperationalState == TetheringOperationalState.On;
            if (on)
            {
                failures = 0;
                retryAfter = DateTime.MinValue;
                UpdateState("핫스팟 켜짐 · 정상", true, "normal");
                ClearDiagnosis();
                try { clients.Text = $"{manager.GetTetheringClients().Count}대"; }
                catch (UnauthorizedAccessException) { clients.Text = "권한 필요"; }
                catch { clients.Text = "조회 불가"; }
                return;
            }

            clients.Text = "0대";
            if (!manual && !enabled.Checked)
            {
                UpdateState("핫스팟 꺼짐 · 자동 복구 중지", false, "disabled");
                return;
            }
            if (!manual && DateTime.Now < retryAfter)
            {
                UpdateState("복구 대기 중 · 네트워크 확인", false, "backoff");
                return;
            }

            UpdateState("핫스팟 꺼짐 · 복구 시도 중", false, "recovering");
            var result = await manager.StartTetheringAsync();
            on = manager.TetheringOperationalState == TetheringOperationalState.On;
            if (on || result.Status == TetheringOperationStatus.Success)
            {
                failures = 0;
                retryAfter = DateTime.MinValue;
                UpdateState("핫스팟 복구 성공", true, "recovered", alwaysLog: true);
                ClearDiagnosis();
            }
            else
            {
                failures++;
                SetRetryDelay();
                RecordDiagnosis("TETHER_" + result.Status, "Windows 핫스팟 시작 요청이 실패했습니다.");
                UpdateState($"복구 실패: {result.Status} (연속 {failures}회)", false, "error", alwaysLog: true);
            }
        }
        catch (Exception ex)
        {
            failures++;
            SetRetryDelay();
            RecordDiagnosis(ClassifyException(ex), SafeDetail(ex));
            UpdateState("점검 실패: " + SafeDetail(ex), false, "error:" + ex.GetType().Name);
        }
        finally
        {
            inspect.Enabled = true;
            lastTick = DateTime.Now;
            last.Text = $"마지막 점검: {lastTick:yyyy-MM-dd HH:mm:ss}";
            // Restart the timer so the countdown always matches the next scheduled check.
            ResetWatchdog();
            gate.Release();
        }
    }
    void SetRetryDelay()
    {
        // 30s, 60s, 120s, ... capped at 10min. Manual checks bypass this delay.
        int delaySeconds = Math.Min(600, 30 * (1 << Math.Min(failures - 1, 5)));
        retryAfter = DateTime.Now.AddSeconds(delaySeconds);
    }
    void UpdateState(string message, bool ok, string statusKey, bool alwaysLog = false)
    {
        state.Text = message;
        state.ForeColor = ok ? Color.FromArgb(22, 126, 75) : Color.FromArgb(177, 77, 37);
        details.Text = ok ? "자동 감시 정상 동작 중" : "네트워크 / 장치 / 권한 상태를 확인해 주세요";
        // Normal checks do not append an identical line every minute.
        if (alwaysLog || previousStatusKey != statusKey) WriteLog(message);
        previousStatusKey = statusKey;
    }
    async Task LoadNetworkAsync()
    {
        try
        {
            var config = Manager().GetCurrentAccessPointConfiguration();
            ssid.Text = config.Ssid;
            password.Text = config.Passphrase;
        }
        catch (Exception ex)
        {
            RecordDiagnosis(ClassifyException(ex), SafeDetail(ex));
            WriteLog("핫스팟 구성 읽기 불가: " + SafeDetail(ex));
            // Do not put a fake/placeholder password into the field.
        }
        await Task.CompletedTask;
    }
    async Task ApplyNetworkAsync()
    {
        var name = ssid.Text;
        var pass = password.Text;
        if (string.IsNullOrWhiteSpace(name) || Encoding.UTF8.GetByteCount(name) > 32 || pass.Length < 8 || pass.Length > 63 || pass.Any(c => c < 33 || c > 126))
        {
            MessageBox.Show("SSID는 UTF-8 기준 1~32바이트, 비밀번호는 공백을 제외한 ASCII 8~63자여야 합니다.", "입력 확인");
            return;
        }
        if (MessageBox.Show("설정을 변경하면 기존 연결이 끊길 수 있습니다. 진행할까요?", "변경 확인", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        if (!await gate.WaitAsync(0)) { MessageBox.Show("다른 작업이 완료될 때까지 기다려주세요."); return; }
        suspendRecovery = true;
        apply.Enabled = false;
        try
        {
            var mgr = Manager();
            var config = new NetworkOperatorTetheringAccessPointConfiguration { Ssid = name, Passphrase = pass };
            await mgr.ConfigureAccessPointAsync(config);
            MessageBox.Show("핫스팟 네트워크 설정을 저장했습니다. 연결이 끊긴 기기는 새 정보로 재연결하세요.", "적용 완료");
            WriteLog("SSID/비밀번호 변경 완료 (비밀번호 기록하지 않음)");
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show("Windows의 wiFiControl 권한이 거부되었습니다. 일반 EXE는 이 기능을 사용할 수 없을 수 있습니다. Windows 설정에서 직접 변경해 주세요.", "Windows 권한 제한");
            RecordDiagnosis("ACCESS_DENIED", "Windows가 Wi-Fi 설정 변경 권한을 거부했습니다.");
            WriteLog("SSID/비밀번호 변경 거부: wiFiControl 권한 부족");
        }
        catch (Exception ex)
        {
            MessageBox.Show("변경 실패: " + ex.Message + "\nWindows 기본 핫스팟 설정에서 변경할 수 있습니다.", "오류");
            RecordDiagnosis(ClassifyException(ex), SafeDetail(ex));
            WriteLog("SSID/비밀번호 변경 실패: " + SafeDetail(ex));
        }
        finally { suspendRecovery = false; apply.Enabled = true; gate.Release(); }
    }
    void ShowQrDialog()
    {
        if (string.IsNullOrWhiteSpace(ssid.Text) || string.IsNullOrEmpty(password.Text))
        { MessageBox.Show("먼저 현재 SSID와 비밀번호를 확인하거나 입력해 주세요."); return; }
        if (MessageBox.Show("QR 코드에는 Wi-Fi 비밀번호가 포함됩니다. 화면에 표시할까요?", "비밀번호 공개 확인", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        static string EscapeWifi(string value) => value.Replace("\\", "\\\\").Replace(";", "\\;").Replace(":", "\\:").Replace(",", "\\,").Replace("\"", "\\\"");
        string payload = $"WIFI:T:WPA;S:{EscapeWifi(ssid.Text)};P:{EscapeWifi(password.Text)};;";
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
            using var code = new QRCode(data);
            using var bitmap = code.GetGraphic(10);
            using var dialog = new Form { Text = "Wi-Fi 연결 QR 코드", Width = 385, Height = 430, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
            var picture = new PictureBox { Image = new Bitmap(bitmap), SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill };
            dialog.Controls.Add(picture);
            dialog.Controls.Add(new Label { Text = "연결할 휴대전화 카메라로 스캔하세요. 다른 사람에게 QR을 공유하지 마세요.", Dock = DockStyle.Bottom, Height = 44, TextAlign = ContentAlignment.MiddleCenter });
            dialog.ShowDialog(this);
            picture.Image?.Dispose();
        }
        catch (Exception ex) { MessageBox.Show("QR 생성 실패: " + ex.Message); }
    }
    static void OpenWindowsSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:network-mobilehotspot") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show("Windows 설정 열기 실패: " + ex.Message); }
    }
    // Maintenance never deletes unrelated files; only events.log and its numbered archives.
    void DeleteAllLogs()
    {
        if (MessageBox.Show("활동 기록을 모두 삭제할까요? 이 작업은 취소할 수 없습니다.",
                "기록 전체 삭제", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try
        {
            foreach (var file in ManagedLogFiles()) if (File.Exists(file)) File.Delete(file);
            RefreshLog();
        }
        catch (Exception ex)
        {
            MessageBox.Show("기록 삭제에 실패했습니다: " + ex.Message, "삭제 실패");
        }
    }

    IEnumerable<string> ManagedLogFiles()
    {
        yield return logFile;
        for (int i = 1; i <= MaxLogArchives; i++) yield return logFile + "." + i;
    }

    void PruneLogs()
    {
        if (RetentionDays == 0) return;
        try
        {
            DateTime cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (string file in ManagedLogFiles())
            {
                if (!File.Exists(file)) continue;
                string[] lines = File.ReadAllLines(file, Encoding.UTF8);
                var kept = lines.Where(line => !TryLogTimestamp(line, out DateTime time) || time >= cutoff).ToArray();
                if (kept.Length == lines.Length) continue;
                if (kept.Length == 0) File.Delete(file);
                else
                {
                    string tmp = file + ".tmp";
                    try
                    {
                        File.WriteAllLines(tmp, kept, new UTF8Encoding(false));
                        File.Move(tmp, file, true);
                    }
                    finally { if (File.Exists(tmp)) File.Delete(tmp); }
                }
            }
        }
        catch (Exception ex) { Debug.WriteLine("로그 정리 실패: " + ex.Message); }
    }

    static bool TryLogTimestamp(string line, out DateTime time)
    {
        time = default;
        return line.Length >= 21 && line[0] == '[' && line[20] == ']' &&
            DateTime.TryParseExact(line.Substring(1, 19), "yyyy-MM-dd HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out time);
    }


    // Error explanations are intentionally conservative: they are suggestions, not certainty.
    static string ClassifyException(Exception ex)
    {
        if (ex is UnauthorizedAccessException) return "ACCESS_DENIED";
        if (ex is InvalidOperationException && ex.Message.Contains("인터넷 연결 프로필")) return "NO_CONNECTION";
        if (ex is System.Runtime.InteropServices.COMException) return "WINDOWS_API";
        return "UNEXPECTED_ERROR";
    }

    static string SafeDetail(Exception ex)
    {
        // Never include arbitrary exception messages in a sharable diagnostic report:
        // they may embed paths, SSIDs, or other environment details.
        return ex switch
        {
            UnauthorizedAccessException => "Windows 접근 권한이 거부되었습니다.",
            InvalidOperationException when ex.Message.Contains("인터넷 연결 프로필") => "인터넷 연결 프로필이 없습니다.",
            System.Runtime.InteropServices.COMException com => $"Windows API 오류 (HRESULT 0x{com.HResult:X8})",
            _ => $"{ex.GetType().Name} 오류 (HRESULT 0x{ex.HResult:X8})"
        };
    }

    void RecordDiagnosis(string code, string detail)
    {
        lastDiagnosisCode = code;
        lastDiagnosisDetail = detail;
        lastDiagnosisAt = DateTime.Now;
        WriteLog("[진단 " + code + "] " + detail);
    }

    void ClearDiagnosis()
    {
        lastDiagnosisCode = "OK";
        lastDiagnosisDetail = "현재 핫스팟 상태가 정상입니다.";
        lastDiagnosisAt = DateTime.Now;
    }

    static (string cause, string action) DiagnosisText(string code)
    {
        if (code == "OK") return ("현재 감지된 문제 없음", "핫스팟 상태가 정상이라면 추가 조치는 필요하지 않습니다.");
        if (code == "NO_CONNECTION") return ("인터넷 연결 프로필을 찾지 못했습니다.", "① PC의 인터넷 연결을 확인하세요.\n② Windows 네트워크 설정에서 공유할 연결을 선택하세요.\n③ 연결이 안정된 후 다시 점검해 보세요.");
        if (code == "ACCESS_DENIED") return ("Windows의 네트워크 제어 권한이 제한되었을 수 있습니다.", "① Windows 설정에서 모바일 핫스팟을 직접 켜 보세요.\n② 회사·학교 PC라면 관리자 정책을 확인하세요.\n③ 보안 기능을 해제하지 마세요.");
        if (code.StartsWith("TETHER_")) return ("Windows가 핫스팟 시작 요청에 성공하지 못했습니다.", "① Wi-Fi 어댑터 활성화 및 모바일 핫스팟 설정을 확인하세요.\n② 이미 다른 무선 공유 기능이 동작 중인지 확인하세요.\n③ 잠시 기다린 뒤 '지금 점검 및 복구'를 눌러 보세요.");
        if (code == "WINDOWS_API") return ("Windows 네트워크 API 호출에서 오류가 발생했습니다.", "① Windows 모바일 핫스팟을 수동으로 켤 수 있는지 확인하세요.\n② Wi-Fi 장치 드라이버를 점검하세요.\n③ 문제가 반복되면 오류 코드를 개발자에게 보내세요.");
        return ("프로그램 또는 Windows에서 예상하지 못한 오류가 발생했습니다.", "① 핫스팟 기능을 수동으로 확인하세요.\n② 프로그램을 다시 실행해 보세요.\n③ 오류가 반복되면 진단 보고서를 저장하여 개발자에게 전달하세요.");
    }

    string BuildDiagnosticReport()
    {
        var (cause, action) = DiagnosisText(lastDiagnosisCode);
        return $"Hotspot Keeper 진단 보고서\r\n버전: 2.6.0\r\nWindows: {Environment.OSVersion.Version}\r\n진단 시각: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n최근 문제 시각: {(lastDiagnosisAt == DateTime.MinValue ? "없음" : lastDiagnosisAt.ToString("yyyy-MM-dd HH:mm:ss"))}\r\n진단 코드: {lastDiagnosisCode}\r\n요약: {lastDiagnosisDetail}\r\n가능한 원인: {cause}\r\n권장 조치:\r\n{action}\r\n\r\n※ SSID, 비밀번호, 기기 식별자, 원본 로그는 포함하지 않았습니다.\r\n※ 이 진단은 가능한 원인을 안내하며 확정 진단이 아닙니다.\r\n";
    }

    void ShowDiagnosis()
    {
        var (cause, action) = DiagnosisText(lastDiagnosisCode);
        using var dialog = new Form
        {
            Text = "Hotspot Keeper · 문제 진단",
            ClientSize = new Size(580, 490),
            Font = new Font("Malgun Gothic", 9.5f),
            BackColor = Color.FromArgb(248, 250, 253),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            Icon = this.Icon
        };
        var title = new Label { Text = "문제 진단 및 해결 안내", Location = new Point(20, 18), Size = new Size(530, 36), Font = new Font("Malgun Gothic", 15, FontStyle.Bold) };
        var diagnosticText = new TextBox
        {
            Location = new Point(20, 68), Size = new Size(540, 320),
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
            Text = $"진단 코드: {lastDiagnosisCode}\r\n최근 기록: {lastDiagnosisDetail}\r\n\r\n가능한 원인\r\n{cause}\r\n\r\n사용자 권장 조치\r\n{action.Replace("\n", "\r\n")}",
        };
        var settingsButton = new Button { Text = "Windows 설정 열기", Location = new Point(20, 410), Size = new Size(170, 42) };
        var exportButton = new Button { Text = "진단 보고서 저장", Location = new Point(200, 410), Size = new Size(180, 42) };
        var closeButton = new Button { Text = "닫기", Location = new Point(450, 410), Size = new Size(110, 42) };
        StyleAction(settingsButton); StyleAction(exportButton); StyleAction(closeButton);
        settingsButton.Click += (_, _) => OpenWindowsSettings();
        closeButton.Click += (_, _) => dialog.Close();
        exportButton.Click += (_, _) =>
        {
            using var save = new SaveFileDialog { Filter = "텍스트 파일 (*.txt)|*.txt", FileName = $"HotspotKeeper_Diagnosis_{DateTime.Now:yyyyMMdd_HHmmss}.txt", DefaultExt = "txt" };
            if (save.ShowDialog(dialog) != DialogResult.OK) return;
            try { File.WriteAllText(save.FileName, BuildDiagnosticReport(), new UTF8Encoding(true)); MessageBox.Show(dialog, "진단 보고서를 저장했습니다. 공유 전에 내용을 확인해 주세요.", "완료"); }
            catch (Exception ex) { MessageBox.Show(dialog, "저장 실패: " + SafeDetail(ex), "오류"); }
        };
        dialog.Controls.AddRange(new Control[] { title, diagnosticText, settingsButton, exportButton, closeButton });
        dialog.ShowDialog(this);
    }

    void WriteLog(string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
            // Keep bounded logs; older archives remain in the same folder.
            if (File.Exists(logFile) && new FileInfo(logFile).Length >= MaxLogBytes)
            {
                var oldest = logFile + "." + MaxLogArchives;
                if (File.Exists(oldest)) File.Delete(oldest);
                for (int i = MaxLogArchives - 1; i >= 1; i--)
                {
                    string from = logFile + "." + i;
                    if (File.Exists(from)) File.Move(from, logFile + "." + (i + 1));
                }
                File.Move(logFile, logFile + ".1");
            }
            File.AppendAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { /* Logging should never stop monitoring. */ }
    }
    void RefreshLog()
    {
        try
        {
            if (File.Exists(logFile))
            {
                var lines = File.ReadAllLines(logFile);
                logView.Text = string.Join(Environment.NewLine, lines.TakeLast(200));
            }
            else logView.Text = "아직 활동 기록이 없습니다.";
        }
        catch (Exception ex) { logView.Text = "기록 읽기 실패: " + ex.Message; }
    }
}

internal sealed class SoftCard : Panel
{
    public SoftCard()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = new GraphicsPath();
        int d = 18;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        path.AddArc(r.X,r.Y,d,d,180,90);
        path.AddArc(r.Right-d,r.Y,d,d,270,90);
        path.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);
        path.AddArc(r.X,r.Bottom-d,d,d,90,90);
        path.CloseFigure();
        using var brush = new SolidBrush(BackColor);
        using var pen = new Pen(Color.FromArgb(225,231,241));
        e.Graphics.FillPath(brush,path);
        e.Graphics.DrawPath(pen,path);
        base.OnPaint(e);
    }
}
