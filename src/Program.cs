using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("BatteryLevel")]
[assembly: System.Reflection.AssemblyProduct("BatteryLevel 设备电量")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

namespace BatteryLevel
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);

        [STAThread]
        static void Main(string[] args)
        {
            var argLine = string.Join(" ", args).ToLowerInvariant();

            if (argLine.Contains("--sniff"))  // 诊断：抓取厂商通道原始数据，保存到 exe 同目录
            {
                MessageBox.Show("将只读监听接收器数据 60 秒。\n\n期间请正常使用鼠标/键盘，可以打开官方驱动查看电量，并记下显示的数值。\n点击“确定”开始。",
                    "设备电量 · 数据抓取");
                var lines = PassiveHid.Sniff(60);
                var file = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.ExecutablePath),
                    "sniff-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                System.IO.File.WriteAllLines(file, lines.ToArray(), Encoding.UTF8);
                MessageBox.Show("抓取完成，共 " + lines.Count + " 行。\n已保存到：\n" + file, "设备电量 · 数据抓取");
                return;
            }

            if (argLine.Contains("--dump"))   // 命令行诊断：输出当前读取到的电量
            {
                var all = BatteryReader.ReadAll();
                if (PassiveHid.ListenerCount > 0)   // 被动监听需要等接收器上报一次
                {
                    Thread.Sleep(12000);
                    all = BatteryReader.ReadAll();
                }
                Aliases.Apply(all);
                try { Console.OutputEncoding = Encoding.UTF8; } catch { }
                foreach (var d in all)
                    Console.WriteLine("{0}\t{1}\t{2}\t{3}{4}", d.Name, d.HasBattery ? d.Level + "%" : "-",
                        d.Kind, d.Connection, d.Charging ? "\tcharging" : "");
                if (argLine.Contains("--hid"))
                    foreach (var h in Hid.EnumerateUsb())
                        Console.WriteLine("HID {0:X4}:{1:X4} {2:X4}:{3:X4} in={4} out={5} feat={6} {7}",
                            h.Vid, h.Pid, h.UsagePage, h.Usage, h.InLen, h.OutLen, h.FeatLen, h.Container);
                return;
            }

            bool created;
            using (var mutex = new Mutex(true, "BatteryLevel.SingleInstance", out created))
            {
                if (!created)
                {
                    // 已在运行：通知已有实例弹出面板（并允许其抢占前台）
                    AllowSetForegroundWindow(-1);
                    IntPtr hwnd = FindWindow(null, PopupForm.WindowTitle);
                    if (hwnd != IntPtr.Zero) PostMessage(hwnd, PopupForm.WM_SHOWME, IntPtr.Zero, IntPtr.Zero);
                    return;
                }
                try { SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp(!argLine.Contains("--tray")));
            }
        }
    }

    sealed class TrayApp : ApplicationContext
    {
        const int RefreshIntervalMs = 60 * 1000;
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string AppName = "BatteryLevel";

        readonly NotifyIcon tray;
        readonly PopupForm popup;
        readonly System.Windows.Forms.Timer timer, debounce;
        readonly ToolStripMenuItem autoStartItem;
        readonly HashSet<string> lowNotified = new HashSet<string>();
        List<DeviceBattery> last = new List<DeviceBattery>();
        Icon currentIcon;
        bool refreshing, pending, rescanPending, showOnFirst;

        public TrayApp(bool showPanel)
        {
            showOnFirst = showPanel;
            popup = new PopupForm();
            var unused = popup.Handle;   // 创建句柄，以便接收设备变更广播
            popup.RefreshRequested += delegate { Refresh(); };
            popup.DevicesChanged += delegate { debounce.Stop(); debounce.Start(); };
            popup.ShowRequested += delegate { ShowPanel(); };
            popup.DeviceRenamed += delegate
            {
                Aliases.Apply(last);    // last 与面板持有的是同一个列表
                popup.Invalidate();
                UpdateTray();
            };
            popup.DeviceRemoved += delegate
            {
                HiddenDevices.Filter(last);
                popup.Relayout();       // 设备变少，面板随之缩短
                UpdateTray();
            };
            popup.RescanRequested += delegate { Refresh(true); };

            var menu = new ContextMenuStrip();
            menu.Items.Add("打开面板", null, delegate { ShowPanel(); });
            menu.Items.Add("立即刷新", null, delegate { Refresh(); });
            menu.Items.Add("重新获取设备", null, delegate { Refresh(true); });
            autoStartItem = new ToolStripMenuItem("开机自启动", null, delegate { ToggleAutoStart(); });
            autoStartItem.Checked = IsAutoStart();
            menu.Items.Add(autoStartItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { ExitThread(); });

            tray = new NotifyIcon();
            tray.ContextMenuStrip = menu;
            tray.Text = "设备电量";
            SetTrayIcon(-1);
            tray.Visible = true;
            tray.MouseClick += OnTrayClick;

            timer = new System.Windows.Forms.Timer();
            timer.Interval = RefreshIntervalMs;
            timer.Tick += delegate { Refresh(); };
            timer.Start();

            debounce = new System.Windows.Forms.Timer();
            debounce.Interval = 2500;   // 设备连接/断开后稍等电量属性写入
            debounce.Tick += delegate { debounce.Stop(); Refresh(); };

            SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
            SystemEvents.PowerModeChanged += OnPowerChanged;
            PassiveHid.Changed += OnPassiveChanged;

            Refresh();
        }

        void OnTrayClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (popup.Visible) return;   // 点击托盘会先让面板失焦隐藏
            if ((DateTime.Now - popup.LastHidden).TotalMilliseconds < 300) return;
            ShowPanel();
        }

        void ShowPanel()
        {
            popup.ShowPanel();
            Refresh();
        }

        void Refresh() { Refresh(false); }

        /// <summary>rescan=true：重新获取设备——恢复已删除的设备并清空识别缓存，再完整扫描一次。</summary>
        void Refresh(bool rescan)
        {
            if (rescan) { HiddenDevices.Clear(); rescanPending = true; }
            if (refreshing) { pending = true; return; }
            refreshing = true;
            bool reset = rescanPending;
            rescanPending = false;
            popup.SetScanning(reset);
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<DeviceBattery> list;
                try
                {
                    if (reset) BatteryReader.ResetCaches();
                    list = BatteryReader.ReadAll();
                }
                catch { list = new List<DeviceBattery>(); }
                try { popup.BeginInvoke(new Action(delegate { OnResult(list); })); } catch { }
            });
        }

        void OnResult(List<DeviceBattery> list)
        {
            refreshing = false;
            HiddenDevices.Filter(list);
            Aliases.Apply(list);
            last = list;
            popup.SetDevices(list);
            UpdateTray();
            CheckLowBattery();
            if (showOnFirst) { showOnFirst = false; popup.ShowPanel(); }
            if (pending) { pending = false; Refresh(); }
            else if (!popup.Visible) Memory.Trim();
        }

        void UpdateTray()
        {
            int min = -1;
            var sb = new StringBuilder();
            foreach (var d in last)
            {
                if (!d.HasBattery) continue;   // 读不到电量的设备不参与托盘图标与提示
                if (min < 0 || d.Level < min) min = d.Level;
                var line = d.Name + " " + d.Level + "%";
                if (sb.Length + line.Length + 1 > 63) break;   // NotifyIcon.Text 上限 63 字符
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(line);
            }
            tray.Text = sb.Length == 0 ? "设备电量：暂无设备" : sb.ToString();
            SetTrayIcon(min);
        }

        void SetTrayIcon(int level)
        {
            var old = currentIcon;
            currentIcon = IconFactory.CreateTrayIcon(level, SystemInformation.SmallIconSize.Width);
            tray.Icon = currentIcon;
            if (old != null) old.Dispose();
        }

        void CheckLowBattery()
        {
            foreach (var d in last)
            {
                if (d.IsSystem || d.Charging || !d.HasBattery) continue;   // 本机电池由系统自行提醒；充电中不提醒
                if (d.Level <= Theme.LowThreshold)
                {
                    if (lowNotified.Add(d.Id))
                        tray.ShowBalloonTip(5000, "电量低", d.Name + " 剩余 " + d.Level + "%，请及时充电", ToolTipIcon.Warning);
                }
                else if (d.Level > Theme.LowThreshold + 5) lowNotified.Remove(d.Id);
            }
        }

        void OnPreferenceChanged(object s, UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.General && e.Category != UserPreferenceCategory.VisualStyle) return;
            popup.BeginInvoke(new Action(delegate { popup.ApplyTheme(); UpdateTray(); }));
        }

        void OnPassiveChanged()
        {
            // 接收器上报了新电量（监听线程），切回界面线程刷新
            try { popup.BeginInvoke(new Action(delegate { debounce.Stop(); debounce.Start(); })); } catch { }
        }

        void OnPowerChanged(object s, PowerModeChangedEventArgs e)
        {
            popup.BeginInvoke(new Action(delegate { debounce.Stop(); debounce.Start(); }));
        }

        static bool IsAutoStart()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(AppName) != null;
            }
            catch { return false; }
        }

        void ToggleAutoStart()
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (IsAutoStart()) k.DeleteValue(AppName, false);
                    else k.SetValue(AppName, "\"" + Application.ExecutablePath + "\" --tray");
                }
            }
            catch (Exception ex) { MessageBox.Show("设置开机自启动失败：" + ex.Message, "设备电量"); }
            autoStartItem.Checked = IsAutoStart();
        }

        protected override void ExitThreadCore()
        {
            SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
            SystemEvents.PowerModeChanged -= OnPowerChanged;
            PassiveHid.Changed -= OnPassiveChanged;
            timer.Stop();
            tray.Visible = false;
            tray.Dispose();
            popup.Dispose();
            base.ExitThreadCore();
        }
    }

    static class Memory
    {
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr process);

        /// <summary>面板隐藏后归还工作集，常驻时只占很少物理内存。</summary>
        public static void Trim()
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                EmptyWorkingSet(GetCurrentProcess());
            }
            catch { }
        }
    }
}
