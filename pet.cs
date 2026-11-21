// ============================================================================
// 3D 桌面宠物 —— 纯 WPF（Viewport3D / Direct3D），不依赖任何游戏引擎
// 编译：Windows 自带 csc.exe（无需安装任何东西）
// 交互：左键拖动 | 双击跳跃 | 右键打开豆包 | Esc 退出
// 行为：待机浮动 + 眨眼 → 随机闲逛 → 撞屏幕边缘停止 → 继续待机
// API ：内嵌本地 HTTP 服务（TcpListener），可供豆包等外部程序控制：
//       GET  http://127.0.0.1:8787/status
//       POST http://127.0.0.1:8787/jump
//       POST http://127.0.0.1:8787/walk?dir=1&seconds=5
//       POST http://127.0.0.1:8787/idle
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using SD = System.Drawing;
using WF = System.Windows.Forms;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace DesktopPet
{
    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Log.Write("unhandled: " + e.ExceptionObject);
            };

            // P0-3：单实例守卫，第二个实例静默退出（退出码 0）
            bool createdNew;
            Mutex mutex = null;
            try
            {
                mutex = new Mutex(true, "Global\\DesktopPetWPF", out createdNew);
            }
            catch
            {
                createdNew = true; // Global 权限不足时退化为本地实例
            }
            if (mutex != null && !createdNew)
            {
                SecondInstanceNotify();
                Log.Write("second instance exit");
                return;
            }

            Log.Write("startup");

            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += (s, e) =>
            {
                Log.Write("UI unhandled: " + e.Exception);
                e.Handled = true;
            };
            app.Run(new PetWindow());

            try
            {
                if (mutex != null)
                {
                    mutex.ReleaseMutex();
                    mutex.Dispose();
                }
            }
            catch { }

            Log.Write("exit");
        }

        // 可选：给已运行的实例发一次气泡
        static void SecondInstanceNotify()
        {
            try
            {
                using (var wc = new WebClient())
                {
                    wc.UploadString("http://127.0.0.1:8787/pet/chat",
                        "application/json", "{\"text\":\"我已经在这儿啦\"}");
                }
            }
            catch { }
        }
    }

    // ============================================================================
    // P0-2 开机自启：HKCU\...\Run，值名 DesktopPet，路径带引号；取消时删除值
    // ============================================================================
    public static class AutoStart
    {
        const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        const string ValueName = "DesktopPet";

        public static bool IsEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    return key != null && key.GetValue(ValueName) != null;
                }
            }
            catch { return false; }
        }

        public static void Set(bool on)
        {
            try
            {
                if (on)
                {
                    string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                    if (string.IsNullOrEmpty(exe)) return;
                    using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                    {
                        key.SetValue(ValueName, "\"" + exe + "\"");
                    }
                }
                else
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                    {
                        if (key != null && key.GetValue(ValueName) != null)
                            key.DeleteValue(ValueName, false);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("autostart set: " + ex.Message);
            }
        }
    }

    public class PetWindow : Window
    {
        // ---------------- 行为参数 ----------------
        const double WalkSpeed = 60.0;   // 行走速度（像素/秒）
        const double IdleMin = 2.0, IdleMax = 6.0;   // 待机随机时长（秒）
        const double WalkMin = 3.0, WalkMax = 8.0;   // 行走随机时长（秒）
        const double BlinkCycle = 3.4;   // 眨眼周期（秒）
        const double BlinkLen = 0.13;    // 眨眼时长（秒）

        enum State { Idle, Walk, Happy, Shy, Curious, Sleepy, Doze }

        readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        readonly Random _rand = new Random();
        State _state = State.Idle;
        double _clock;            // 累计时间（秒）
        double _stateTimer;       // 当前状态剩余时间
        double _dir = 1.0;        // 行走方向：1 右，-1 左
        bool _dragging;
        Point _dragMouse, _dragWin;
        double _downCX, _downCY;    // FIX-CLICK-2 P0-1：按下时刻物理像素光标（GetCursorPos 原值），单击判定唯一基准
        bool _dragPending;          // FIX-CLICK-2 P0-2：按下未确认拖动——期间窗口一动不动（修"点一下抖一下"+假位移）
        double _downClock;        // 按下时刻（_clock，秒）——区分纯点击与拖动
        double _wakeSuppressUntil;// 菜单「睡眠」后 0.8s 内抑制鼠标路径唤醒，防止菜单冒泡把它点醒
        DispatcherTimer _dragGuard; // P0-1 兜底：按下 500ms 后仍未松手且位移<5 DIP → 视为单击

        // 弹珠模式：拖拽时记录速度，甩到边界就脱手弹出去，之后在四壁之间来回弹到停下
        bool _ballFlight;
        double _ballVX, _ballVY;      // 自由飞行速度（DIP/秒）
        double _dragVX, _dragVY;      // 拖动速度（DIP/秒，指数平滑）
        double _dragLastX, _dragLastY, _dragLastT;
        const double BallLaunchSpeed = 200.0;   // 低于这个速度撞边界就还是老实贴住
        const double BallThrowSpeed = 350.0;    // 松手甩出去的阈值（比撞墙阈值高些，免得随手放它就跑）
        const double BallMaxSpeed = 1600.0;     // 初速上限，防止手快甩出光速
        const double BallWallDamp = 0.80;       // 撞墙损失 20%
        const double BallFriction = 700.0;      // 每秒减速 DIP/s²
        const double BallStopSpeed = 55.0;      // 低于这个速度就停

        // 变换节点
        TranslateTransform3D _hop;   // 脚本驱动的浮动/小跳
        TranslateTransform3D _jump;  // 双击跳跃（动画驱动）
        RotateTransform3D _yaw;      // 朝向
        ScaleTransform3D[] _eyeScales;
        ScaleTransform3D[] _blushScales;
        // FIX-GROUND P0-1/P0-2：影子随高度收缩变淡 + 落地果冻形变（欠阻尼弹簧标量）
        ScaleTransform3D _shadowScale;
        System.Windows.Shapes.Rectangle _shadow2D;          // FIX-SHADOW2D：贴地 2D 软影（3D 盘片被球体遮挡，实际看不见）
        ScaleTransform _shadow2DScale;
        RadialGradientBrush _shadowGrad;                    // 单张径向渐变影子，替代原三层硬边叠片（会看出三环台阶）
        readonly double[] _shadowStopA = { 120, 66, 26, 0 }; // 四个色标的基浓度（0.00/0.38/0.72/1.00）
        double _bgLum = 1.0;                                // 脚下桌面亮度：0=黑 1=白，决定影子投暗影还是投光晕
        double _bgSampleT;
        RadialGradientBrush _floorPoolGrad;                  // 光照池（也要跟着明暗换色，否则深色壁纸上整块地面消失）
        readonly System.Collections.Generic.List<SolidColorBrush> _floorLineBrushes
            = new System.Collections.Generic.List<SolidColorBrush>();   // 每根网格线的画刷，同上
        ScaleTransform3D _squashScale;
        ScaleTransform3D _faceSquashScale;   // 五官组：触地下压时随身体一起变形（Center=球底，ScaleY 同时产生下压位移）
        double _squash, _squashV;
        double _prevH;            // 上一帧离地高度，用于求竖直速度
        double _prevVy;           // 上一帧竖直速度——触地是瞬时过零，两帧跨过触地点时
                                  // 单帧 vy 可能掉进 (-1,-0.5] 死区，用前后两帧最大速度兜底
        double _lastImpact = -10; // 上次触地冲击时刻（0.15s 最小间隔闸门）
        double _turnUntil = -1;   // FIX-TURN P0-3：转圈收尾前点击只唤醒不改朝向，防"反向转一圈"。
                                  // 存 Environment.TickCount64 毫秒——_clock 用标称 16ms 累计，DispatcherTimer
                                  // 只会迟到不会提前，负载下 _clock 恒慢于墙钟，用它算窗口会被拉长（实测 1.33s 仍抑制）
        double _dragStartTop;     // 按下时窗口 Top（松手下落用）
        const double SquashK = 180;
        static readonly double SquashC = 0.55 * 2 * Math.Sqrt(180);   // ≈0.55 倍临界阻尼，抖两下
        // 冲击标定：规格值 mult=1.6/cap=0.28 在 K=180 下峰值仅 ~0.009，达不到验收 0.08~0.12；
        // 实测标定 0.7/3.0 → 行走峰值 ~0.028（≤0.03）、bounce ~0.095（区间内）
        const double SquashImpulseMult = 0.7;
        const double SquashImpulseCap = 3.0;
        double _shadowAppliedH = -1;
        // 果冻二期（路线A）：body 网格下半部逐顶点形变——底部贴地压扁+横向鼓出，脸区域零位移。
        // 注意：这是非线性形变，仿射 Transform3D 做不到；整组 _squashScale 因此保持恒等不再写入。
        MeshGeometry3D _bodyMesh;
        Point3D[] _bodyBase;
        double _bodySquashApplied;
        ControlServer _server;
        Border _bubble;
        TextBlock _bubbleText;
        DispatcherTimer _bubbleTimer;

        // P0-1 托盘与菜单
        WF.NotifyIcon _tray;
        SD.Icon _trayIcon;
        IntPtr _hicon;
        bool _followMouse = true;
        System.Windows.Controls.MenuItem _miFollow;

        // P0-4 瞳孔跟随
        DispatcherTimer _pupilTimer;
        TranslateTransform3D _pupilOffset;
        Vector _pupilCur, _pupilTarget;
        double _nearBoost = 1.0;
        double _lastPupilLog;

        // P0-5 嘴
        ScaleTransform3D _mouthScale;
        DispatcherTimer _mouthTimer;
        bool _mouthOpen;

        // P0-6 情绪状态机
        string _mood = "neutral";
        double _moodEyes = 1.0;        // 眼睛开合系数
        double _moodFloatFreq = 2.0;   // 浮动频率
        double _moodFloatAmp = 0.04;   // 浮动幅度
        double _lastInteract;          // 上次交互时刻（秒）
        double _zzzTimer;              // Doze 冒 Zzz 气泡计时
        // 睡眠口水珠（仅 doze 显示）
        ScaleTransform3D _droolScale;
        TranslateTransform3D _droolBob;
        double _droolVis;
        const double DozeAfterSeconds = 300;   // 5 分钟无交互进入 Doze
        // 闲逛侧身角：原来 ±90° 会把眼/腮红/嘴压到球的轮廓线上（薄片感），收窄到 ±35°
        const double WalkYawDeg = 35;
        // 姿态接管的朝向上限：比走路的 35 更保守。走路时侧身是"走过去"的合理姿态，
        // 但正面看你时脸一旦转过去，五官就压到球轮廓上露馅了（实测 -35 左眼已贴边，-24 安全）
        const double PoseYawDeg = 24;

        // ================= POSE 通道：AI 连续姿态注入 =================
        // NaN = 这一项不接管，回落到原有的情绪/闲逛逻辑；一旦写入就逐帧缓动靠近，永不瞬跳。
        // 这是给外部 AI 用的"关节角度"，不是"选动画"——原有状态机全部保留，只是被覆盖。
        double _poseEye = double.NaN;       // 眼睛开合 0.05~1.4（眨眼仍由 Blink 覆盖）
        double _poseBlush = double.NaN;     // 腮红 0~2.5
        double _poseYaw = double.NaN;       // 朝向角 -180~180（一经接管，AnimateYaw 被逐帧掐掉）
        double _poseGazeX = double.NaN;     // 瞳孔注视 -1~1（接管时绕过鼠标跟随）
        double _poseGazeY = double.NaN;
        double _poseBreathAmp = double.NaN; // 呼吸幅度 0~0.2
        double _poseBreathFreq = double.NaN;// 呼吸频率 0.2~6
        double _poseHold;                    // 剩余接管秒数，归零自动归还控制权
        int _lastPoseTick;                   // 上一次姿态节拍的墙钟毫秒（.NET Framework 无 TickCount64，int 回绕用 unchecked 差值抵消）
        double _moodBlush = 1.0;             // 情绪里的腮红基准，姿态释放时还原用
        // 平滑后的当前值（起始值 = 中性姿态，避免接管瞬间跳变）
        double _curPoseEye = 1.0;
        double _curPoseBlush = 1.0;
        double _curPoseYaw;
        double _curPoseBreathAmp = 0.04;
        double _curPoseBreathFreq = 2.0;

        // 移动钉住 + 暂停闲逛
        double _pinTimer;              // /pet/move 后钉住秒数（期间不闲逛）
        bool _paused;                  // /pet/pause：彻底暂停闲逛

        // FIX-ORB：悬浮魔法球——一只会飘的青色光球；闲逛时豆仔去追它，追到就闪开
        bool _orbEnabled = true;                        // 右键菜单可关
        bool _hasWalkTarget;                            // 兼容字段：追球玩法撤回后恒为 false
        Window _orbWindow;
        System.Windows.Shapes.Ellipse _orbGlow;
        System.Windows.Shapes.Ellipse _orbCore;
        double _orbX = double.NaN, _orbY = double.NaN;  // 球心屏幕坐标（DIP），NaN = 还没初始化
        double _orbTX, _orbTY;                          // 缓动目标

        double _orbBob;                                 // 浮动相位
        double _orbDY;                                  // 渲染层额外垂直偏移（浮动/贴地跳）
        double _orbTheta;                               // 绕圈角度
        double _playT;                                  // 下次"扑球"倒计时
        Grid _orbBurst;                                 // 被抓空时喷出来的一圈小星点
        ScaleTransform _orbBurstScale;


        public PetWindow()
        {
            AllowsTransparency = true;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            Width = 320;
            Height = 320;
            Title = "豆仔 · 3D Desktop Pet";

            LoadSettings();   // 先把上次选的轮盘样式读回来，再建界面

            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 40;
            Top = area.Bottom - Height;

            Content = BuildLayout();

            MouseLeftButtonDown += OnDown;
            PreviewMouseLeftButtonUp += OnDragEnd;   // FIX-CLICK-WAKE P0-1：预览隧道，抢在子元素吞 up 之前
            LostMouseCapture += OnLostCapture;       // 捕获丢失兜底，绝不允许卡在 dragging
            MouseMove += OnMove;
            // P0-1：右键改走轮盘菜单（12 格环）；万一轮盘构建失败自动退回列表菜单
            MouseRightButtonDown += OnRadialDown;
            MouseWheel += OnRadialWheel;
            MouseRightButtonUp += OnRadialUp;
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) Close();              // Esc：退出
            };

            _timer.Tick += Tick;

            // FIX-CLICK-WAKE P0-1：单击兜底定时器（up 被吞时 500ms 后判单击）
            _dragGuard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _dragGuard.Tick += delegate
            {
                try
                {
                    _dragGuard.Stop();
                    if (!_dragging && !_dragPending) return;   // P0-2 后"按住不动"停在 pending 态，同样要兜底
                    double d = ClickDistDip();                 // P0-3：与 up 路径共用同一套位移判定
                    // 注意：guard 本身就在按下 500ms 时触发，IsClick 的 ms<400 恒 false——
                    // 这里只判距离（阈值与 IsClick 一致 8 DIP），时间条件由"guard 已等 500ms"隐含。
                    // 否则规格 §一 的"长按 700ms 醒"路径会死掉
                    if (d >= 0 && d < 8.0)
                    {
                        Log.Write("drag guard: no up in 500ms, dist=" +
                                  Math.Round(d, 1) + " -> treat as click");
                        EndDrag();
                    }
                    else
                    {
                        Log.Write("drag guard: real drag in progress, void");
                    }
                }
                catch (Exception ex) { Log.Write("drag guard: " + ex.Message); }
            };
            _timer.Start();
            EnterIdle();

            // P0-4 瞳孔跟随定时器（25fps，跟随关闭时暂停）
            _pupilTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _pupilTimer.Tick += PupilTick;
            if (_followMouse) _pupilTimer.Start();

            // 启动本地控制 API（供豆包等外部程序调用）
            _server = new ControlServer(this);
            _server.Start();

            CreateTray();
            BuildPetMenu();
            BuildOrb();   // FIX-ORB：悬浮魔法球

            // 开场白：上场 0.8s 后冒一次"豆仔来啦～"，之后不再自动说话
            var greet = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            greet.Tick += delegate
            {
                try
                {
                    greet.Stop();
                    ShowBubble("豆仔来啦～");
                }
                catch (Exception ex) { Log.Write("greet: " + ex.Message); }
            };
            greet.Start();
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                CloseRadial();                // 轮盘开着就一起收掉，别留一个空壳 popup
                if (_orbWindow != null)
                {
                    _orbWindow.Close();       // FIX-ORB：主人关了，魔法球也得跟着关，否则会剩一只光球在桌面上
                    _orbWindow = null;
                }
                if (_tray != null)
                {
                    _tray.Visible = false;
                    _tray.Dispose();
                    _tray = null;
                }
                if (_trayIcon != null)
                {
                    _trayIcon.Dispose();
                    _trayIcon = null;
                }
                if (_hicon != IntPtr.Zero)
                {
                    DestroyIcon(_hicon);
                    _hicon = IntPtr.Zero;
                }
            }
            catch (Exception ex) { Log.Write("tray dispose: " + ex.Message); }
            base.OnClosed(e);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr hIcon);

        // FIX-ORB：魔法球窗口要"鼠标穿透"——透明窗口里的 Transparent 背景仍然吃点击，
        // 必须靠 WS_EX_TRANSPARENT 才能让鼠标穿过光球落到下面的桌面/窗口
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        // 轮盘弹窗要压在豆仔身上（豆仔主窗是 Topmost，普通 popup 会被它盖住）
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        // ================= P0-1 托盘 =================

        void CreateTray()
        {
            try
            {
                // 代码现画 32×32 蓝白圆球图标，不落磁盘
                using (var bmp = new SD.Bitmap(32, 32))
                {
                    using (var g = SD.Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
                        g.Clear(SD.Color.Transparent);
                        using (var body = new SD.SolidBrush(SD.Color.FromArgb(255, 126, 200, 255)))
                            g.FillEllipse(body, 2, 3, 28, 26);
                        using (var hi = new SD.SolidBrush(SD.Color.FromArgb(230, 255, 255, 255)))
                            g.FillEllipse(hi, 8, 8, 7, 6);
                        using (var pen = new SD.Pen(SD.Color.FromArgb(255, 37, 43, 56), 2))
                            g.DrawEllipse(pen, 2, 3, 28, 26);
                    }
                    _hicon = bmp.GetHicon();
                    _trayIcon = SD.Icon.FromHandle(_hicon);
                }

                _tray = new WF.NotifyIcon
                {
                    Icon = _trayIcon,
                    Text = "豆仔 · 桌面宠物",
                    Visible = true
                };
                _tray.DoubleClick += delegate { ShowPet(); };

                var menu = new WF.ContextMenuStrip();
                var miShow = new WF.ToolStripMenuItem("显示/隐藏");
                miShow.Click += delegate { ToggleVisible(); };
                var miAuto = new WF.ToolStripMenuItem("开机自启");
                miAuto.CheckOnClick = true;
                miAuto.Checked = AutoStart.IsEnabled();
                miAuto.Click += delegate
                {
                    AutoStart.Set(miAuto.Checked);
                    Log.Write("autostart(tray)=" + miAuto.Checked);
                };
                var miExit = new WF.ToolStripMenuItem("退出");
                miExit.Click += delegate { Close(); };
                menu.Items.Add(miShow);
                menu.Items.Add(miAuto);
                menu.Items.Add(new WF.ToolStripSeparator());
                menu.Items.Add(miExit);
                _tray.ContextMenuStrip = menu;
                menu.Opening += delegate { miAuto.Checked = AutoStart.IsEnabled(); };
            }
            catch (Exception ex)
            {
                Log.Write("tray init: " + ex.Message);
            }
        }

        void ShowPet()
        {
            try
            {
                ApiSetVisible(true);
                Activate();
            }
            catch (Exception ex) { Log.Write("show pet: " + ex.Message); }
        }

        void ToggleVisible()
        {
            ApiSetVisible(Visibility != Visibility.Visible);
        }

        // ================= P0-1 宠物右键菜单 =================

        void BuildPetMenu()
        {
            try
            {
                var menu = new ContextMenu();

                var miSpeak = new System.Windows.Controls.MenuItem { Header = "说句话…" };
                miSpeak.Click += delegate { ShowChatWindow(); };
                menu.Items.Add(miSpeak);

                var miAct = new System.Windows.Controls.MenuItem { Header = "动作" };
                AddActionItem(miAct, "弹跳", "bounce");
                AddActionItem(miAct, "摇头", "shake");
                AddActionItem(miAct, "转圈", "turn");
                AddActionItem(miAct, "害羞", "blush");
                var miDoze = new System.Windows.Controls.MenuItem { Header = "睡眠" };
                miDoze.Click += delegate { EnterDoze(); };
                miAct.Items.Add(miDoze);
                var miWake = new System.Windows.Controls.MenuItem { Header = "唤醒" };
                miWake.Click += delegate { Wake(); SetMood("neutral"); };
                miAct.Items.Add(miWake);
                menu.Items.Add(miAct);

                _miFollow = new System.Windows.Controls.MenuItem
                {
                    Header = "跟着鼠标",
                    IsCheckable = true,
                    IsChecked = _followMouse
                };
                _miFollow.Click += delegate
                {
                    _followMouse = _miFollow.IsChecked;
                    Log.Write("follow mouse=" + _followMouse);
                    OnFollowChanged();
                };
                menu.Items.Add(_miFollow);

                var miPause = new System.Windows.Controls.MenuItem
                {
                    Header = "暂停闲逛",
                    IsCheckable = true,
                    IsChecked = _paused
                };
                miPause.Click += delegate
                {
                    ApiPause(miPause.IsChecked);
                };
                menu.Items.Add(miPause);
                menu.Opened += delegate { miPause.IsChecked = _paused; };

                // FIX-ORB：魔法球开关（关掉就只剩普通闲逛）
                var miOrb = new System.Windows.Controls.MenuItem
                {
                    Header = "魔法球（小伙伴）",
                    IsCheckable = true,
                    IsChecked = _orbEnabled
                };
                miOrb.Click += delegate
                {
                    _orbEnabled = miOrb.IsChecked;
                    if (!_orbEnabled && _state == State.Walk && _hasWalkTarget) { _hasWalkTarget = false; EnterIdle(); }
                    Log.Write("orb=" + _orbEnabled);
                };
                menu.Items.Add(miOrb);
                menu.Opened += delegate { miOrb.IsChecked = _orbEnabled; };

                var miHide = new System.Windows.Controls.MenuItem { Header = "隐藏" };
                miHide.Click += delegate { ApiSetVisible(false); };
                menu.Items.Add(miHide);

                var miAuto = new System.Windows.Controls.MenuItem
                {
                    Header = "开机自启",
                    IsCheckable = true,
                    IsChecked = AutoStart.IsEnabled()
                };
                miAuto.Click += delegate
                {
                    AutoStart.Set(miAuto.IsChecked);
                    Log.Write("autostart=" + miAuto.IsChecked);
                };
                menu.Items.Add(miAuto);
                menu.Opened += delegate { miAuto.IsChecked = AutoStart.IsEnabled(); };

                var miExit = new System.Windows.Controls.MenuItem { Header = "退出" };
                miExit.Click += delegate { Close(); };
                menu.Items.Add(miExit);

                var miUrl = new System.Windows.Controls.MenuItem { Header = "打开豆包网页" };
                miUrl.Click += delegate { OpenUrl(); };
                menu.Items.Add(miUrl);

                ContextMenu = null;              // 右键改走轮盘菜单；列表菜单留着做兜底
                _menuFallback = menu;
            }
            catch (Exception ex)
            {
                Log.Write("menu init: " + ex.Message);
            }
        }

        void AddActionItem(System.Windows.Controls.MenuItem parent, string label, string action)
        {
            var mi = new System.Windows.Controls.MenuItem { Header = label };
            mi.Click += delegate { ApiAction(action); };
            parent.Items.Add(mi);
        }

        void TryOpenMenu()
        {
            try
            {
                if (_menuFallback != null)
                {
                    _menuFallback.PlacementTarget = this;
                    _menuFallback.IsOpen = true;
                }
            }
            catch (Exception ex) { Log.Write("menu open: " + ex.Message); }
        }

        // ================= 轮盘菜单（右键） =================
        // 12 格环：右键按下即弹出；按住拖到格子松手 = 直接选中（快）；只点一下不拖 = 保持展开，
        // 移到格子左键点一下选中；点中心/圈外、或再点右键 = 取消。
        // 命中判定在宠物窗口里用全局光标算（鼠标已被 Capture，popup 自己不收消息）
        class RadialItem
        {
            public string Label;
            public Action Run;
            public Func<bool> IsOn;          // 开关项的当前状态，null = 普通项
            public Func<string> DynLabel;    // 动态文字（睡眠/唤醒）
            public System.Windows.Shapes.Path Shape;
            public TextBlock Text;
            public TextBlock Tick;
            public Brush[] Norm = new Brush[StyleCount];   // 每套样式一份常态填充
            public Brush[] Hot = new Brush[StyleCount];    // 每套样式一份悬停填充
        }

        // 画布比环大一圈：第 6 套「星环」要在环外铺星云（环外半径 122，星云到 176）
        const double RadialSize = 360;
        const double RadialOuter = 134;
        const double RadialInner = 70;
        const int RadialCount = 12;
        const int StyleCount = 6;            // 0 彩格 / 1 金色法阵 / 2 星空 / 3 霓虹 / 4 星云 / 5 星体轨道
        const int HitChip = -2;              // 右下角"样式"按钮的命中码
        const double ChipOffX = 112;         // 相对圆心的偏移（DIP）
        const double ChipOffY = 112;
        // 「星体轨道」模式下轨道下方很挤，把「样式」按钮挪到右上角空区
        double ChipHitX() { return _radialStyle == 5 ? 116.0 : ChipOffX; }
        double ChipHitY() { return _radialStyle == 5 ? -134.0 : ChipOffY; }

        int _radialStyle = 5;                // 默认走新的「星体轨道」菜单（旧皮肤作为遗留保留）
        List<UIElement>[] _decos = new List<UIElement>[StyleCount];
        System.Windows.Shapes.Ellipse _radialRing;
        System.Windows.Shapes.Path _radialBase;
        Border _radialChip;
        TextBlock _radialChipText;
        Brush[] _textNorm = new Brush[StyleCount];
        Brush[] _textHot = new Brush[StyleCount];
        Brush[] _baseFill = new Brush[StyleCount];
        Brush[] _chipBorder = new Brush[StyleCount];
        static readonly string[] StyleNames = new string[] { "彩格", "法阵", "星空", "霓虹", "星云", "星体" };

        RotateTransform _nebulaSpin;         // 星云环的缓慢自转
        int _underIndex = -1;                // 环带装饰插到扇区之下、文字之上的位置（否则会盖住文字）

        System.Windows.Controls.Primitives.Popup _radial;
        Canvas _radialCanvas;
        List<RadialItem> _radialItems;
        bool _radialOpen;
        int _radialHover = -1;
        double _radialMoveMax;
        Point _radialDownScreen;
        double _radialCx, _radialCy;
        ContextMenu _menuFallback;

        // 霓虹流动：一个定时器推相位，12 格按"离亮头多远"调亮度，一道光顺时针扫过整圈
        DispatcherTimer _flowTimer;
        double _flowPhase;

        // 12 格各自的底色（跟着功能区分配：动作类偏暖，系统类偏冷，隐藏灰、退出红）
        static readonly Color[] RadialColors = new Color[]
        {
            Color.FromArgb(0xFF, 0x4E, 0x9E, 0xDC),   // 0 说话  蓝
            Color.FromArgb(0xFF, 0x33, 0xA8, 0xA0),   // 1 弹跳  青
            Color.FromArgb(0xFF, 0x4F, 0xAE, 0x5F),   // 2 摇头  绿
            Color.FromArgb(0xFF, 0x92, 0xC2, 0x3F),   // 3 转圈  草绿
            Color.FromArgb(0xFF, 0xDF, 0xC1, 0x3F),   // 4 害羞  黄
            Color.FromArgb(0xFF, 0xDF, 0x8A, 0x42),   // 5 睡眠  橙
            Color.FromArgb(0xFF, 0xD6, 0x5C, 0x7C),   // 6 魔法球 玫红
            Color.FromArgb(0xFF, 0xB0, 0x53, 0xBF),   // 7 暂停  紫
            Color.FromArgb(0xFF, 0x6F, 0x6D, 0xD6),   // 8 跟随  靛蓝
            Color.FromArgb(0xFF, 0x4A, 0x7F, 0xD6),   // 9 自启  宝蓝
            Color.FromArgb(0xFF, 0x6E, 0x7C, 0x8C),   // 10 隐藏 石板灰
            Color.FromArgb(0xFF, 0xC8, 0x43, 0x3F)    // 11 退出 红
        };

        // 星空样式：深蓝紫星云里的一圈冷色扇区，偶尔掺两颗暖星
        static readonly Color[] CosmicColors = new Color[]
        {
            Color.FromArgb(0xFF, 0x33, 0x5C, 0xC4),   // 0 说话  宝蓝
            Color.FromArgb(0xFF, 0x27, 0x4B, 0x9E),   // 1 弹跳  深蓝
            Color.FromArgb(0xFF, 0x2C, 0x3D, 0x88),   // 2 摇头  靛
            Color.FromArgb(0xFF, 0x4A, 0x35, 0x96),   // 3 转圈  紫
            Color.FromArgb(0xFF, 0x6B, 0x3C, 0x9B),   // 4 害羞  紫红
            Color.FromArgb(0xFF, 0x7E, 0x3F, 0x8A),   // 5 睡眠  梅红
            Color.FromArgb(0xFF, 0x9C, 0x55, 0x4E),   // 6 魔法球 暖棕红
            Color.FromArgb(0xFF, 0xB0, 0x7A, 0x3E),   // 7 暂停  金褐
            Color.FromArgb(0xFF, 0x3E, 0x6C, 0xB2),   // 8 跟随  湖蓝
            Color.FromArgb(0xFF, 0x2B, 0x61, 0xA8),   // 9 自启  深湖蓝
            Color.FromArgb(0xFF, 0x2A, 0x36, 0x5E),   // 10 隐藏 夜灰蓝
            Color.FromArgb(0xFF, 0x7C, 0x35, 0x58)    // 11 退出 暗酒红
        };

        // 霓虹样式：常态全透明，悬停浮一层青或粉的光
        static readonly Color[] NeonColors = new Color[]
        {
            Color.FromArgb(0xFF, 0x3F, 0xD8, 0xFF),   // 0 说话  青
            Color.FromArgb(0xFF, 0xFF, 0x58, 0xC8),   // 1 弹跳  粉
            Color.FromArgb(0xFF, 0x3F, 0xD8, 0xFF),   // 2 摇头  青
            Color.FromArgb(0xFF, 0xFF, 0x58, 0xC8),   // 3 转圈  粉
            Color.FromArgb(0xFF, 0x3F, 0xD8, 0xFF),   // 4 害羞  青
            Color.FromArgb(0xFF, 0xFF, 0x58, 0xC8),   // 5 睡眠  粉
            Color.FromArgb(0xFF, 0x3F, 0xD8, 0xFF),   // 6 魔法球 青
            Color.FromArgb(0xFF, 0xFF, 0x58, 0xC8),   // 7 暂停  粉
            Color.FromArgb(0xFF, 0x3F, 0xD8, 0xFF),   // 8 跟随  青
            Color.FromArgb(0xFF, 0xFF, 0x58, 0xC8),   // 9 自启  粉
            Color.FromArgb(0xFF, 0x3F, 0xD8, 0xFF),   // 10 隐藏 青
            Color.FromArgb(0xFF, 0xFF, 0x58, 0xC8)    // 11 退出 粉
        };

        void BuildRadial()
        {
            for (int s = 0; s < StyleCount; s++) _decos[s] = new List<UIElement>();
            _textNorm[0] = Brushes.White;
            _textHot[0] = Brushes.White;
            _textNorm[1] = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xE6, 0xAE));
            _textHot[1] = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFD, 0xF2));
            _textNorm[2] = new SolidColorBrush(Color.FromArgb(0xFF, 0xE6, 0xF1, 0xFF));
            _textHot[2] = Brushes.White;
            _textNorm[3] = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xDD, 0xF5));
            _textHot[3] = Brushes.White;
            _textNorm[4] = new SolidColorBrush(Color.FromArgb(0xFF, 0xE8, 0xEE, 0xFF));
            _textHot[4] = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFA, 0xDC));
            _textNorm[5] = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xF3, 0xDC));
            _textHot[5] = Brushes.White;
            _baseFill[0] = new SolidColorBrush(Color.FromArgb(0x8C, 0x33, 0x3B, 0x46));
            _baseFill[1] = new SolidColorBrush(Color.FromArgb(0xF8, 0x11, 0x0E, 0x07));
            var cosmicBg = new LinearGradientBrush(
                Color.FromArgb(0xF8, 0x12, 0x16, 0x3C), Color.FromArgb(0xF8, 0x28, 0x11, 0x38), new Point(0, 0), new Point(1, 1));
            _baseFill[2] = cosmicBg;
            _baseFill[3] = new SolidColorBrush(Color.FromArgb(0xF0, 0x07, 0x05, 0x0D));
            var nebulaBg = new LinearGradientBrush(
                Color.FromArgb(0xF2, 0x0A, 0x10, 0x30), Color.FromArgb(0xF2, 0x2A, 0x0F, 0x3A), new Point(0, 0), new Point(1, 1));
            _baseFill[4] = nebulaBg;
            // 星环：垫底色用深海军蓝，环缝半透明，星点能透过来
            // 星环：底盘交给程序化星图贴图，这里保持透明
            _baseFill[5] = Brushes.Transparent;
            _chipBorder[0] = new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF));
            _chipBorder[1] = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xCE, 0x7A));
            _chipBorder[2] = new SolidColorBrush(Color.FromArgb(0xC0, 0x9F, 0xD8, 0xFF));
            _chipBorder[3] = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0x58, 0xC8));
            _chipBorder[4] = new SolidColorBrush(Color.FromArgb(0xC0, 0xA9, 0xC4, 0xFF));
            _chipBorder[5] = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xD9, 0x96));

            _radialCanvas = new Canvas { Width = RadialSize, Height = RadialSize, IsHitTestVisible = false, Background = Brushes.Transparent };
            double cx = RadialSize / 2.0, cy = RadialSize / 2.0;

            var ring = new System.Windows.Shapes.Ellipse
            {
                Width = (RadialOuter + 4) * 2,
                Height = (RadialOuter + 4) * 2,
                Stroke = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = 1.4,
                Fill = null
            };
            Canvas.SetLeft(ring, cx - RadialOuter - 4);
            Canvas.SetTop(ring, cy - RadialOuter - 4);
            _radialCanvas.Children.Add(ring);
            _radialRing = ring;

            // 底盘：扇区之间留了缝，没有这层深色垫底，缝里会露出下面的桌面文字（很脏）
            var baseGeo = new PathGeometry { FillRule = FillRule.EvenOdd };
            var fOut = new PathFigure { StartPoint = new Point(cx - RadialOuter, cy), IsClosed = true };
            fOut.Segments.Add(new ArcSegment { Point = new Point(cx + RadialOuter, cy), Size = new Size(RadialOuter, RadialOuter), SweepDirection = SweepDirection.Clockwise });
            fOut.Segments.Add(new ArcSegment { Point = new Point(cx - RadialOuter, cy), Size = new Size(RadialOuter, RadialOuter), SweepDirection = SweepDirection.Clockwise });
            var fIn = new PathFigure { StartPoint = new Point(cx - RadialInner, cy), IsClosed = true };
            fIn.Segments.Add(new ArcSegment { Point = new Point(cx + RadialInner, cy), Size = new Size(RadialInner, RadialInner), SweepDirection = SweepDirection.Counterclockwise });
            fIn.Segments.Add(new ArcSegment { Point = new Point(cx - RadialInner, cy), Size = new Size(RadialInner, RadialInner), SweepDirection = SweepDirection.Counterclockwise });
            baseGeo.Figures.Add(fOut);
            baseGeo.Figures.Add(fIn);
            var baseDonut = new System.Windows.Shapes.Path { Data = baseGeo, Fill = new SolidColorBrush(Color.FromArgb(0x8C, 0x33, 0x3B, 0x46)) };
            _radialCanvas.Children.Add(baseDonut);
            _radialBase = baseDonut;
            _underIndex = _radialCanvas.Children.Count;   // 之后插进来的环带画在这层：压住底盘、却被扇区和文字压住

            // 中间不再垫底、也不再写"取消"：圆洞里只留豆仔的脸（点洞中心 = 取消）

            _radialItems = new List<RadialItem>();
            AddRadialItem(0, "说话", delegate { ShowChatWindow(); }, null, null);
            AddRadialItem(1, "弹跳", delegate { ApiAction("bounce"); }, null, null);
            AddRadialItem(2, "摇头", delegate { ApiAction("shake"); }, null, null);
            AddRadialItem(3, "转圈", delegate { ApiAction("turn"); }, null, null);
            AddRadialItem(4, "害羞", delegate { ApiAction("blush"); }, null, null);
            AddRadialItem(5, "睡眠", delegate { ToggleDoze(); }, null, delegate { return IsDoze() ? "唤醒" : "睡眠"; });
            AddRadialItem(6, "魔法球", delegate { ToggleOrb(); }, delegate { return _orbEnabled; }, null);
            AddRadialItem(7, "暂停", delegate { ApiPause(!_paused); }, delegate { return _paused; }, null);
            AddRadialItem(8, "跟随", delegate { ToggleFollowMouse(); }, delegate { return _followMouse; }, null);
            AddRadialItem(9, "自启", delegate { ToggleAutoStart(); }, delegate { return AutoStart.IsEnabled(); }, null);
            AddRadialItem(10, "隐藏", delegate { ApiSetVisible(false); }, null, null);
            AddRadialItem(11, "退出", delegate { Close(); }, null, null);

            BuildChip(cx, cy);
            BuildGoldDeco(cx, cy);
            BuildCosmicDeco(cx, cy);
            BuildNeonDeco(cx, cy);
            BuildNebulaDeco(cx, cy);
            BuildOrbitDeco(cx, cy);
            ApplyRadialStyle();

            _radial = new System.Windows.Controls.Primitives.Popup
            {
                Child = _radialCanvas,
                AllowsTransparency = true,
                StaysOpen = true,
                IsHitTestVisible = false,
                PlacementTarget = this,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Relative
            };
        }

        void AddRadialItem(int index, string label, Action run, Func<bool> isOn, Func<string> dyn)
        {
            double cx = RadialSize / 2.0, cy = RadialSize / 2.0;
            double mid = -90.0 + index * (360.0 / RadialCount);
            double half = (360.0 / RadialCount) / 2.0;
            double gap = 0.9;   // 扇区之间留一点缝，视觉上更清楚
            double a0 = (mid - half + gap) * Math.PI / 180.0;
            double a1 = (mid + half - gap) * Math.PI / 180.0;

            var p0 = new Point(cx + RadialOuter * Math.Cos(a0), cy + RadialOuter * Math.Sin(a0));
            var p1 = new Point(cx + RadialOuter * Math.Cos(a1), cy + RadialOuter * Math.Sin(a1));
            var p2 = new Point(cx + RadialInner * Math.Cos(a1), cy + RadialInner * Math.Sin(a1));
            var p3 = new Point(cx + RadialInner * Math.Cos(a0), cy + RadialInner * Math.Sin(a0));

            var fig = new PathFigure { StartPoint = p0, IsClosed = true, IsFilled = true };
            fig.Segments.Add(new ArcSegment { Point = p1, Size = new Size(RadialOuter, RadialOuter), SweepDirection = SweepDirection.Clockwise });
            fig.Segments.Add(new LineSegment { Point = p2 });
            fig.Segments.Add(new ArcSegment { Point = p3, Size = new Size(RadialInner, RadialInner), SweepDirection = SweepDirection.Counterclockwise });
            var geo = new PathGeometry();
            geo.Figures.Add(fig);

            // 每套样式一张色表：彩格用满色，法阵不上色只留金雾，星空用星云冷色，霓虹悬停才透光
            Color baseCol = RadialColors[index % RadialColors.Length];
            Color cosCol = CosmicColors[index % CosmicColors.Length];
            Color neonCol = NeonColors[index % NeonColors.Length];
            var norm = new Brush[StyleCount];
            var hot = new Brush[StyleCount];
            norm[0] = new SolidColorBrush(Color.FromArgb(0xDE, baseCol.R, baseCol.G, baseCol.B));
            hot[0] = new SolidColorBrush(Color.FromArgb(0xFF,
                (byte)(baseCol.R + (255 - baseCol.R) * 0.22),
                (byte)(baseCol.G + (255 - baseCol.G) * 0.22),
                (byte)(baseCol.B + (255 - baseCol.B) * 0.22)));
            norm[1] = new SolidColorBrush(Color.FromArgb(0x00, 0xFF, 0xD9, 0x7A));
            hot[1] = new SolidColorBrush(Color.FromArgb(0xA6, 0xFF, 0xD0, 0x6A));
            norm[2] = new SolidColorBrush(Color.FromArgb(0xE0,
                (byte)(cosCol.R + (255 - cosCol.R) * 0.10),
                (byte)(cosCol.G + (255 - cosCol.G) * 0.10),
                (byte)(cosCol.B + (255 - cosCol.B) * 0.10)));
            hot[2] = new SolidColorBrush(Color.FromArgb(0xFF,
                (byte)(cosCol.R + (255 - cosCol.R) * 0.40),
                (byte)(cosCol.G + (255 - cosCol.G) * 0.40),
                (byte)(cosCol.B + (255 - cosCol.B) * 0.40)));
            norm[3] = new SolidColorBrush(Color.FromArgb(0x00, neonCol.R, neonCol.G, neonCol.B));
            hot[3] = new SolidColorBrush(Color.FromArgb(0x6F, neonCol.R, neonCol.G, neonCol.B));
            // 星云：扇区常态全透明，只让 Ellipse 渐变环露出来；悬停浮一层淡金
            norm[4] = new SolidColorBrush(Color.FromArgb(0x00, 0xFF, 0xF0, 0xC8));
            hot[4] = new SolidColorBrush(Color.FromArgb(0x5E, 0xFF, 0xF0, 0xC8));
            // 星环：扇区常态全透明（露出金色环带），悬停/流光是一层暖金色光
            norm[5] = new SolidColorBrush(Color.FromArgb(0x00, 0xFF, 0xE0, 0xA8));
            hot[5] = new SolidColorBrush(Color.FromArgb(0x4A, 0xFF, 0xF4, 0xDC));

            var shape = new System.Windows.Shapes.Path
            {
                Data = geo,
                Fill = norm[1],
                Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = 1
            };
            _radialCanvas.Children.Add(shape);

            double r = (RadialOuter + RadialInner) / 2.0;
            var text = new TextBlock
            {
                Text = label,
                FontSize = 12,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
                Width = 56,
                TextAlignment = TextAlignment.Center
            };
            Canvas.SetLeft(text, cx + r * Math.Cos(mid * Math.PI / 180.0) - 28);
            Canvas.SetTop(text, cy + r * Math.Sin(mid * Math.PI / 180.0) - 9);
            _radialCanvas.Children.Add(text);

            var tick = new TextBlock
            {
                Text = "●",
                FontSize = 8,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
                Width = 10,
                TextAlignment = TextAlignment.Center,
                Visibility = Visibility.Hidden
            };
            double rt = RadialInner + 7;
            Canvas.SetLeft(tick, cx + rt * Math.Cos(mid * Math.PI / 180.0) - 5);
            Canvas.SetTop(tick, cy + rt * Math.Sin(mid * Math.PI / 180.0) - 5);
            _radialCanvas.Children.Add(tick);

            _radialItems.Add(new RadialItem { Label = label, Run = run, IsOn = isOn, DynLabel = dyn, Shape = shape, Text = text, Tick = tick, Norm = norm, Hot = hot });
        }

        bool IsDoze() { return _state == State.Doze; }

        void BuildChip(double cx, double cy)
        {
            _radialChipText = new TextBlock
            {
                Text = "样式",
                FontSize = 10,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _radialChip = new Border
            {
                Width = 42,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.FromArgb(0xB4, 0x1A, 0x18, 0x14)),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)),
                Child = _radialChipText
            };
            Canvas.SetLeft(_radialChip, cx + ChipOffX - 21);
            Canvas.SetTop(_radialChip, cy + ChipOffY - 10);
            _radialCanvas.Children.Add(_radialChip);
        }

        // 装饰层统一登记到某一套样式的列表里，切换样式时成组显隐
        void AddDeco(int style, UIElement el)
        {
            _radialCanvas.Children.Add(el);
            _decos[style].Add(el);
        }

        UIElement MakeRing(int style, double cx, double cy, double r, double thick, byte alpha, byte rr, byte gg, byte bb)
        {
            var e = new System.Windows.Shapes.Ellipse
            {
                Width = r * 2,
                Height = r * 2,
                Fill = null,
                Stroke = new SolidColorBrush(Color.FromArgb(alpha, rr, gg, bb)),
                StrokeThickness = thick
            };
            Canvas.SetLeft(e, cx - r);
            Canvas.SetTop(e, cy - r);
            AddDeco(style, e);
            return e;
        }

        // 四角星芒（星空样式的星星）：8 个点，外圈尖、内圈收
        UIElement MakeSparkle(int style, double cx, double cy, double x, double y, double size, Color col)
        {
            var fig = new PathFigure { IsClosed = true, IsFilled = true };
            for (int k = 0; k < 8; k++)
            {
                double ang = (-90.0 + k * 45.0) * Math.PI / 180.0;
                double rr = (k % 2 == 0) ? size : size * 0.18;
                var pt = new Point(x + rr * Math.Cos(ang), y + rr * Math.Sin(ang));
                if (k == 0) fig.StartPoint = pt;
                else fig.Segments.Add(new LineSegment { Point = pt });
            }
            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            var star = new System.Windows.Shapes.Path
            {
                Data = geo,
                Fill = new SolidColorBrush(col),
                IsHitTestVisible = false
            };
            AddDeco(style, star);
            return star;
        }

        // 霓虹样式的发光弧
        void AddArc(int style, double cx, double cy, double r, double fromDeg, double toDeg, double thick, byte alpha, byte rr, byte gg, byte bb)
        {
            double a0 = fromDeg * Math.PI / 180.0, a1 = toDeg * Math.PI / 180.0;
            var p0 = new Point(cx + r * Math.Cos(a0), cy + r * Math.Sin(a0));
            var p1 = new Point(cx + r * Math.Cos(a1), cy + r * Math.Sin(a1));
            var fig = new PathFigure { StartPoint = p0, IsClosed = false };
            fig.Segments.Add(new ArcSegment
            {
                Point = p1,
                Size = new Size(r, r),
                SweepDirection = SweepDirection.Clockwise
            });
            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            var arc = new System.Windows.Shapes.Path
            {
                Data = geo,
                Fill = null,
                Stroke = new SolidColorBrush(Color.FromArgb(alpha, rr, gg, bb)),
                StrokeThickness = thick,
                IsHitTestVisible = false
            };
            AddDeco(style, arc);
        }

        void BuildGoldDeco(double cx, double cy)
        {
            MakeRing(1, cx, cy, RadialOuter + 18, 7, 0x2E, 0xFF, 0xC0, 0x50);
            MakeRing(1, cx, cy, RadialOuter + 11, 3.5, 0x58, 0xFF, 0xD2, 0x7C);
            MakeRing(1, cx, cy, RadialOuter + 2.5, 2, 0xEE, 0xFF, 0xDE, 0xA4);
            MakeRing(1, cx, cy, RadialInner - 3, 1.5, 0xBE, 0xFF, 0xDE, 0xA4);

            for (int i = 0; i < 48; i++)
            {
                double a = i * 7.5 * Math.PI / 180.0;
                double r1 = RadialOuter + 4.5;
                double r2 = (i % 6 == 0) ? RadialOuter + 12 : RadialOuter + 8;
                var ln = new System.Windows.Shapes.Line
                {
                    X1 = cx + r1 * Math.Cos(a), Y1 = cy + r1 * Math.Sin(a),
                    X2 = cx + r2 * Math.Cos(a), Y2 = cy + r2 * Math.Sin(a),
                    Stroke = new SolidColorBrush(Color.FromArgb(0x9A, 0xFF, 0xD8, 0x8E)),
                    StrokeThickness = 1,
                    IsHitTestVisible = false
                };
                AddDeco(1, ln);
            }

            for (int i = 0; i < 8; i++)
            {
                double a = i * 45.0 * Math.PI / 180.0;
                double nx = Math.Cos(a), ny = Math.Sin(a);
                double px = -ny, py = nx;
                var fig = new PathFigure
                {
                    StartPoint = new Point(cx + (RadialOuter + 4) * nx + 4.5 * px, cy + (RadialOuter + 4) * ny + 4.5 * py),
                    IsClosed = true
                };
                fig.Segments.Add(new LineSegment { Point = new Point(cx + (RadialOuter + 21) * nx, cy + (RadialOuter + 21) * ny) });
                fig.Segments.Add(new LineSegment { Point = new Point(cx + (RadialOuter + 4) * nx - 4.5 * px, cy + (RadialOuter + 4) * ny - 4.5 * py) });
                var geo = new PathGeometry();
                geo.Figures.Add(fig);
                AddDeco(1, new System.Windows.Shapes.Path
                {
                    Data = geo,
                    Fill = new SolidColorBrush(Color.FromArgb(0xA8, 0xFF, 0xCE, 0x66)),
                    IsHitTestVisible = false
                });
            }
        }

        // 星空样式：一圈幽蓝外晕 + 两道细环 + 撒在圈外的金色星芒
        void BuildCosmicDeco(double cx, double cy)
        {
            MakeRing(2, cx, cy, RadialOuter + 22, 10, 0x30, 0x7A, 0x6B, 0xFF);
            MakeRing(2, cx, cy, RadialOuter + 9, 1.2, 0x48, 0x9F, 0xD8, 0xFF);
            MakeRing(2, cx, cy, RadialOuter + 2.5, 1.5, 0xD8, 0xCF, 0xE8, 0xFF);
            MakeRing(2, cx, cy, RadialInner - 4, 1.2, 0x8A, 0x9F, 0xD8, 0xFF);

            // 星芒位置固定写死，避免每次重编译都换一套，方便对比
            double[][] stars = new double[][]
            {
                new double[] { -104, 6.5 }, new double[] { -76, 4.2 }, new double[] { -52, 7.5 },
                new double[] { -28, 3.6 }, new double[] { 0, 6.0 }, new double[] { 24, 4.4 },
                new double[] { 48, 7.0 }, new double[] { 72, 4.0 }, new double[] { 96, 6.2 },
                new double[] { 128, 4.6 }, new double[] { 156, 6.8 }, new double[] { 182, 4.2 },
                new double[] { 210, 5.6 }, new double[] { 238, 3.8 }
            };
            for (int i = 0; i < stars.Length; i++)
            {
                double a = stars[i][0] * Math.PI / 180.0;
                double dist = RadialOuter + (i % 3 == 0 ? 15 : (i % 3 == 1 ? 8 : 20));
                Color c = (i % 4 == 0)
                    ? Color.FromArgb(0xCC, 0x9F, 0xD8, 0xFF)
                    : Color.FromArgb(0xE0, 0xFF, 0xD9, 0x8A);
                MakeSparkle(2, cx, cy, cx + dist * Math.Cos(a), cy + dist * Math.Sin(a), stars[i][1], c);
            }
        }

        // 霓虹样式：上青下粉两段发光弧 + 内圈细环 + 一圈粉刻度
        void BuildNeonDeco(double cx, double cy)
        {
            AddArc(3, cx, cy, RadialOuter + 4, -168, -16, 10, 0x2A, 0x3F, 0xD8, 0xFF);
            AddArc(3, cx, cy, RadialOuter + 4, -168, -16, 3, 0xEE, 0x3F, 0xE8, 0xFF);
            AddArc(3, cx, cy, RadialOuter + 4, 16, 168, 10, 0x2A, 0xFF, 0x58, 0xC8);
            AddArc(3, cx, cy, RadialOuter + 4, 16, 168, 3, 0xEE, 0xFF, 0x58, 0xC8);
            MakeRing(3, cx, cy, RadialInner - 4, 1.4, 0x78, 0x3F, 0xD8, 0xFF);

            for (int i = 0; i < 36; i++)
            {
                double a = i * 10.0 * Math.PI / 180.0;
                var ln = new System.Windows.Shapes.Line
                {
                    X1 = cx + (RadialOuter + 1) * Math.Cos(a), Y1 = cy + (RadialOuter + 1) * Math.Sin(a),
                    X2 = cx + (RadialOuter - 7) * Math.Cos(a), Y2 = cy + (RadialOuter - 7) * Math.Sin(a),
                    Stroke = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0x58, 0xC8)),
                    StrokeThickness = 1,
                    IsHitTestVisible = false
                };
                AddDeco(3, ln);
            }
        }

        // 环带类装饰要压在底盘之上、扇区与文字之下，否则渐变环会把 12 个标签糊掉
        void AddDecoUnder(int style, UIElement el)
        {
            if (_underIndex >= 0 && _underIndex <= _radialCanvas.Children.Count)
            {
                _radialCanvas.Children.Insert(_underIndex, el);
                _underIndex++;
            }
            else _radialCanvas.Children.Add(el);
            _decos[style].Add(el);
        }

        // 样式 4「星云」：Ellipse 环带（渐变描边 = 星云色）+ 缓慢自转，外面撒金色/冰蓝星芒，再挂一颗带环的小行星
        void BuildNebulaDeco(double cx, double cy)
        {
            double rIn = RadialInner, rOut = RadialOuter;

            // WPF 没有角度渐变（conic），这里用 180 片 2° 的细扇区拼成一圈、逐片插值颜色 = 真·绕圈星云
            Color[] cols = new Color[]
            {
                Color.FromArgb(0xFF, 0x16, 0x30, 0x6E), Color.FromArgb(0xFF, 0x1D, 0x49, 0xB8),
                Color.FromArgb(0xFF, 0x2E, 0x6B, 0xE0), Color.FromArgb(0xFF, 0x4A, 0x55, 0xD0),
                Color.FromArgb(0xFF, 0x7B, 0x46, 0xC8), Color.FromArgb(0xFF, 0xB8, 0x4B, 0xC0),
                Color.FromArgb(0xFF, 0xD6, 0x55, 0xA0), Color.FromArgb(0xFF, 0xB8, 0x5C, 0x86),
                Color.FromArgb(0xFF, 0x3E, 0x5A, 0xAE), Color.FromArgb(0xFF, 0x16, 0x30, 0x6E)
            };
            double[] poss = new double[] { 0, 0.10, 0.22, 0.34, 0.46, 0.58, 0.68, 0.76, 0.86, 1.0 };

            var bandCanvas = new Canvas { Width = RadialSize, Height = RadialSize, IsHitTestVisible = false };
            int n = 180;
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)n;
                Color c = ConicColor(cols, poss, t);
                // 明暗起伏：让相邻几片有亮有暗，看起来像星云而不是一条死渐变
                double k = 0.92 + 0.08 * Math.Sin(i * 0.30) + 0.04 * Math.Sin(i * 1.1 + 0.8);
                c = Color.FromArgb(0xFF,
                    (byte)Math.Min(255, (int)(c.R * k)),
                    (byte)Math.Min(255, (int)(c.G * k)),
                    (byte)Math.Min(255, (int)(c.B * k)));
                double a0 = (i * 360.0 / n - 0.8) * Math.PI / 180.0;
                double a1 = ((i + 1) * 360.0 / n + 0.8) * Math.PI / 180.0;
                var p0 = new Point(cx + rOut * Math.Cos(a0), cy + rOut * Math.Sin(a0));
                var p1 = new Point(cx + rOut * Math.Cos(a1), cy + rOut * Math.Sin(a1));
                var p2 = new Point(cx + rIn * Math.Cos(a1), cy + rIn * Math.Sin(a1));
                var p3 = new Point(cx + rIn * Math.Cos(a0), cy + rIn * Math.Sin(a0));
                var fig = new PathFigure { StartPoint = p0, IsClosed = true, IsFilled = true };
                fig.Segments.Add(new ArcSegment { Point = p1, Size = new Size(rOut, rOut), SweepDirection = SweepDirection.Clockwise });
                fig.Segments.Add(new LineSegment { Point = p2 });
                fig.Segments.Add(new ArcSegment { Point = p3, Size = new Size(rIn, rIn), SweepDirection = SweepDirection.Counterclockwise });
                var geo = new PathGeometry();
                geo.Figures.Add(fig);
                bandCanvas.Children.Add(new System.Windows.Shapes.Path
                {
                    Data = geo,
                    Fill = new SolidColorBrush(c),
                    IsHitTestVisible = false
                });
            }
            _nebulaSpin = new RotateTransform(0);
            bandCanvas.RenderTransform = _nebulaSpin;
            bandCanvas.RenderTransformOrigin = new Point(0.5, 0.5);
            AddDecoUnder(4, bandCanvas);

            MakeRing(4, cx, cy, RadialOuter + 13, 10, 0x2A, 0x6A, 0x7C, 0xFF);   // 外层幽蓝光晕
            MakeRing(4, cx, cy, RadialOuter + 5, 1.6, 0x6A, 0xA9, 0xC4, 0xFF);  // 细亮环
            MakeRing(4, cx, cy, RadialInner - 4, 1.4, 0x84, 0xC9, 0x7B, 0xE0);  // 内圈淡紫

            // 圈外一圈固定位置的星芒，金的为主、掺几颗冰蓝的
            double[] sAng = new double[] { -142, -118, -96, -62, -28, -6, 14, 34, 58, 78, 102, 122, 146, 168, -166 };
            for (int i = 0; i < sAng.Length; i++)
            {
                double a = sAng[i] * Math.PI / 180.0;
                double rr = RadialOuter + (i % 3 == 0 ? 21 : (i % 3 == 1 ? 11 : 27));
                Color sc = (i % 4 == 0)
                    ? Color.FromArgb(0xFF, 0x8F, 0xF0, 0xFF)
                    : Color.FromArgb(0xFF, 0xFF, 0xD2, 0x4D);
                MakeSparkle(4, cx, cy, cx + rr * Math.Cos(a), cy + rr * Math.Sin(a), i % 3 == 0 ? 8.5 : 5.5, sc);
            }

            // 左上一颗带土星环的小行星
            double sa = -38.0 * Math.PI / 180.0;
            double sr = RadialOuter + 15;
            double px = cx + sr * Math.Cos(sa), py = cy + sr * Math.Sin(sa);
            var planet = new System.Windows.Shapes.Ellipse
            {
                Width = 14,
                Height = 14,
                Fill = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xD9, 0x8A)),
                Stroke = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xF3, 0xC4)),
                StrokeThickness = 0.8,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(planet, px - 7);
            Canvas.SetTop(planet, py - 7);
            AddDeco(4, planet);

            var pring = new System.Windows.Shapes.Ellipse
            {
                Width = 30,
                Height = 9,
                Fill = null,
                Stroke = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xE0, 0x9A)),
                StrokeThickness = 1.6,
                IsHitTestVisible = false
            };
            pring.RenderTransform = new RotateTransform(-18);
            pring.RenderTransformOrigin = new Point(0.5, 0.5);
            Canvas.SetLeft(pring, px - 15);
            Canvas.SetTop(pring, py - 4.5);
            AddDeco(4, pring);
        }

        // 按位置在一组停靠点上插值颜色（给"角度渐变"用）
        Color ConicColor(Color[] cols, double[] poss, double t)
        {
            t = ((t % 1.0) + 1.0) % 1.0;
            for (int i = 1; i < poss.Length; i++)
            {
                if (t <= poss[i])
                {
                    double f = (t - poss[i - 1]) / Math.Max(1e-6, poss[i] - poss[i - 1]);
                    Color a = cols[i - 1], b = cols[i];
                    return Color.FromArgb(
                        (byte)(a.A + (b.A - a.A) * f),
                        (byte)(a.R + (b.R - a.R) * f),
                        (byte)(a.G + (b.G - a.G) * f),
                        (byte)(a.B + (b.B - a.B) * f));
                }
            }
            return cols[cols.Length - 1];
        }

        // ================= 样式 5「星体轨道」=================
        // 菜单不再是环形扇区：12 颗半透明星体沿着一条倾斜的椭圆轨道绕着豆仔转。
        //   · 鼠标滚轮 / 左右拖动  →  轨道转动，选中那颗转到正前方（最大最亮）
        //   · 转到球背面的星体会被豆仔挡住  →  立体纵深
        //   · 界面上不写文字，选中项的名字由豆仔用说话气泡念出来
        //   · 右键拖完松手 / 左键单击  →  执行当前选中项
        // 星体照着参考图做：冰蓝通透、左上高光、边缘亮弧、右下反光、底下一点软影。

        const int OrbSlots = RadialCount;
        const double OrbRx = 150.0;              // 轨道长半径（DIP）
        const double OrbRy = 52.0;               // 轨道短半径（越小越"扁"、越贴脸）
        const double OrbTilt = -0.14;            // 轨道整体旋转（弧度）
        const double OrbOffY = 44.0;             // 轨道中心整体下移：后半圈正好从豆仔眼睛后面穿过
        const double OrbStepDeg = 360.0 / OrbSlots;
        const double BallOffY = -15.0;           // 豆仔球心在窗口中心上方 15 DIP
        const double BallVisR = 78.0;            // 豆仔球体视觉半径

        List<Canvas> _orbBodies = new List<Canvas>();
        List<ScaleTransform> _orbScale = new List<ScaleTransform>();
        List<System.Windows.Shapes.Ellipse> _orbSelGlow = new List<System.Windows.Shapes.Ellipse>();
        List<System.Windows.Shapes.Ellipse> _orbStateDot = new List<System.Windows.Shapes.Ellipse>();
        List<System.Windows.Shapes.Ellipse> _orbSelRing = new List<System.Windows.Shapes.Ellipse>();
        List<System.Windows.Shapes.Path> _orbBodyEls = new List<System.Windows.Shapes.Path>();
        List<System.Windows.Shapes.Path> _orbRimEls = new List<System.Windows.Shapes.Path>();
        List<Brush> _orbBrNorm = new List<Brush>();
        List<Brush> _orbBrOn = new List<Brush>();
        List<Brush> _orbRimNorm = new List<Brush>();
        static readonly Brush _orbRimOn = new SolidColorBrush(Color.FromArgb(0xB4, 0xFF, 0xF0, 0xC0));
        List<double> _orbPhase = new List<double>();
        List<double> _orbSizeVar = new List<double>();
        readonly List<UIElement> _orbSparkles = new List<UIElement>();
        readonly List<double> _orbSparkleBase = new List<double>();
        System.Windows.Shapes.Path _orbPathLine;
        double _orbAngle;                        // 当前旋转角（度）
        double _orbTarget;                       // 目标旋转角（度）
        int _orbIndex;                           // 当前选中项
        double _orbDragSlots;                    // 拖动累计（slot）
        int _orbDragStart = -1;                  // 拖动起始选中项
        string _orbSpoken;                       // 上次念过的名字（避免重复）

        static Color Col(byte a, int rgb)
        {
            return Color.FromArgb(a, (byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
        }

        // 轨道上的点：先画一个压扁的椭圆，再整体旋转出倾角
        static void OrbPos(double phi, out double x, out double y)
        {
            double ex = OrbRx * Math.Cos(phi);
            double ey = OrbRy * Math.Sin(phi);
            double c = Math.Cos(OrbTilt), s = Math.Sin(OrbTilt);
            x = ex * c - ey * s;
            y = ex * s + ey * c + OrbOffY;
        }

        static int OrbNorm(int i)
        {
            i %= OrbSlots;
            if (i < 0) i += OrbSlots;
            return i;
        }

        // ---- 水彩配色：12 颗星体一人一色 ----
        static readonly int[] VividPalette = new int[]
        {
            0xFF7A68, 0xFFA23A, 0xFFD23F, 0x8FD14F, 0x2FC39A, 0x35B5E8,
            0x5B7FEF, 0x8A6BFF, 0xC86BEA, 0xF0639E, 0xFF8A5C, 0x2ED3C6
        };

        static Color VividColor(int i)
        {
            int n = VividPalette.Length;
            return Col(0xFF, VividPalette[((i % n) + n) % n]);
        }

        static Color WithAlpha(Color c, byte a) { return Color.FromArgb(a, c.R, c.G, c.B); }

        static Color MixColor(Color a, Color b, double k)
        {
            if (k <= 0) return a;
            if (k >= 1) return b;
            return Color.FromArgb(
                (byte)(a.A + (b.A - a.A) * k),
                (byte)(a.R + (b.R - a.R) * k),
                (byte)(a.G + (b.G - a.G) * k),
                (byte)(a.B + (b.B - a.B) * k));
        }

        static Color ShadeColor(Color c, double k)
        {
            return Color.FromArgb(c.A,
                (byte)Math.Min(255, c.R * k),
                (byte)Math.Min(255, c.G * k),
                (byte)Math.Min(255, c.B * k));
        }

        // 一团水彩：圆球边缘按正弦微扰，像手绘颜料边
        static Geometry BlobGeo(double cx, double cy, double r, double phase)
        {
            var fig = new PathFigure { IsClosed = true, IsFilled = true };
            int n = 40;
            for (int k = 0; k < n; k++)
            {
                double a = k * Math.PI * 2.0 / n;
                double rr = r * (1.0 + 0.020 * Math.Sin(3.0 * a + phase) + 0.013 * Math.Sin(5.0 * a + phase * 1.7));
                var pt = new Point(cx + rr * Math.Cos(a), cy + rr * Math.Sin(a));
                if (k == 0) fig.StartPoint = pt;
                else fig.Segments.Add(new LineSegment { Point = pt });
            }
            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            return geo;
        }

        // 水彩球：淡 → 饱和 → 深，高光不再惨白
        static RadialGradientBrush BubbleBrush(Color baseCol)
        {
            var br = new RadialGradientBrush { Center = new Point(0.38, 0.30), GradientOrigin = new Point(0.38, 0.30) };
            br.GradientStops.Add(new GradientStop(WithAlpha(MixColor(baseCol, Colors.White, 0.66), 0xEE), 0.0));
            br.GradientStops.Add(new GradientStop(WithAlpha(MixColor(baseCol, Colors.White, 0.28), 0xEE), 0.28));
            br.GradientStops.Add(new GradientStop(WithAlpha(baseCol, 0xEE), 0.64));
            br.GradientStops.Add(new GradientStop(WithAlpha(ShadeColor(baseCol, 0.58), 0xF0), 1.0));
            br.Freeze();
            return br;
        }

        // 一条水彩色的轨道彩带
        static LinearGradientBrush WaterColorStrip(byte alpha)
        {
            var br = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            double[] pos = { 0.0, 0.16, 0.32, 0.48, 0.64, 0.80, 1.0 };
            for (int k = 0; k < pos.Length; k++)
                br.GradientStops.Add(new GradientStop(WithAlpha(VividColor(k * 2), alpha), pos[k]));
            br.Freeze();
            return br;
        }

        static System.Windows.Shapes.Path MakeSparkleEl(double x, double y, double size, Color col)
        {
            var fig = new PathFigure { IsClosed = true, IsFilled = true };
            for (int k = 0; k < 8; k++)
            {
                double ang = (-90.0 + k * 45.0) * Math.PI / 180.0;
                double rr = (k % 2 == 0) ? size : size * 0.16;
                var pt = new Point(x + rr * Math.Cos(ang), y + rr * Math.Sin(ang));
                if (k == 0) fig.StartPoint = pt;
                else fig.Segments.Add(new LineSegment { Point = pt });
            }
            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            return new System.Windows.Shapes.Path
            {
                Data = geo,
                Fill = new SolidColorBrush(col),
                IsHitTestVisible = false
            };
        }

        // 把轨道线按"前半圈 / 后半圈"分开画，后半圈明显更淡（绕到豆仔背后）
        bool OrbitBehindPet(double x, double y, double margin)
        {
            double dx = x, dy = y - BallOffY;
            return Math.Sqrt(dx * dx + dy * dy) < BallVisR + margin;
        }

        // 轨道线：后半圈在豆仔轮廓内断开（被豆仔挡住），前半圈整段画
        void AddOrbitArc(double cx, double cy, bool front, double thick, Brush brush, double opacity)
        {
            var geo = new PathGeometry();
            PathFigure fig = null;
            int n = 72;
            for (int k = 0; k <= n; k++)
            {
                double phi = (front ? 0.0 : Math.PI) + Math.PI * k / n;
                double x, y;
                OrbPos(phi, out x, out y);
                bool hidden = !front && OrbitBehindPet(x, y, 2.0);
                if (hidden) { fig = null; continue; }
                var pt = new Point(cx + x, cy + y);
                if (fig == null)
                {
                    fig = new PathFigure { IsClosed = false, StartPoint = pt };
                    geo.Figures.Add(fig);
                }
                else fig.Segments.Add(new LineSegment { Point = pt });
            }
            if (geo.Figures.Count == 0) return;
            var el = new System.Windows.Shapes.Path
            {
                Data = geo,
                Stroke = brush,
                StrokeThickness = thick,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Fill = null,
                Opacity = opacity,
                IsHitTestVisible = false
            };
            AddDecoUnder(5, el);
        }

        void BuildOrbitDeco(double cx, double cy)
        {
            // 轨道：后半圈很淡（只在豆仔头后若隐若现），前半圈亮一些
            AddOrbitArc(cx, cy, false, 2.4, WaterColorStrip(0x80), 0.28);    // 后半圈：很淡的水彩带
            AddOrbitArc(cx, cy, true, 13.0, WaterColorStrip(0x58), 0.26);    // 前半圈：柔光晕
            AddOrbitArc(cx, cy, true, 3.6, WaterColorStrip(0xB8), 0.92);     // 前半圈：水彩彩带
            var geoFront = new PathGeometry();
            var figFront = new PathFigure { IsClosed = false };
            for (int k = 0; k <= 72; k++)
            {
                double phi = Math.PI * k / 72.0;
                double x, y;
                OrbPos(phi, out x, out y);
                var pt = new Point(cx + x, cy + y);
                if (k == 0) figFront.StartPoint = pt;
                else figFront.Segments.Add(new LineSegment { Point = pt });
            }
            geoFront.Figures.Add(figFront);
            _orbPathLine = new System.Windows.Shapes.Path
            {
                Data = geoFront,
                Stroke = new SolidColorBrush(Col(0x70, 0xFFFFFF)),
                StrokeThickness = 1.0,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Fill = null,
                IsHitTestVisible = false
            };
            AddDecoUnder(5, _orbPathLine);

            // 12 颗星体
            for (int i = 0; i < OrbSlots; i++)
            {
                var root = new Canvas { Width = 64, Height = 64, IsHitTestVisible = false };

                // 底影
                var sh = new System.Windows.Shapes.Ellipse { Width = 32, Height = 11 };
                var shBr = new RadialGradientBrush();
                shBr.GradientStops.Add(new GradientStop(Col(0x66, 0x08213C), 0.0));
                shBr.GradientStops.Add(new GradientStop(Col(0x00, 0x08213C), 1.0));
                shBr.Freeze();
                sh.Fill = shBr;
                Canvas.SetLeft(sh, 16);
                Canvas.SetTop(sh, 42);
                root.Children.Add(sh);

                // 泡体：左上亮、边缘深的冰蓝球
                // 外圈暗边（让气泡在白底上也有轮廓）
                // ---- 水彩星体：一颗一个颜色，高光收敛 ----
                Color baseCol = VividColor(i);
                Color deepCol = ShadeColor(baseCol, 0.58);
                Color softCol = MixColor(baseCol, Colors.White, 0.78);
                Color mixCol = MixColor(baseCol, VividColor(i + 5), 0.55);

                // 水彩晕：颜料在纸面上化开的一圈
                var wash = new System.Windows.Shapes.Ellipse { Width = 62, Height = 62 };
                var washBr = new RadialGradientBrush { Center = new Point(0.5, 0.5), GradientOrigin = new Point(0.44, 0.42) };
                washBr.GradientStops.Add(new GradientStop(WithAlpha(baseCol, 0x00), 0.0));
                washBr.GradientStops.Add(new GradientStop(WithAlpha(baseCol, 0x2C), 0.60));
                washBr.GradientStops.Add(new GradientStop(WithAlpha(deepCol, 0x16), 0.82));
                washBr.GradientStops.Add(new GradientStop(WithAlpha(deepCol, 0x00), 1.0));
                washBr.Freeze();
                wash.Fill = washBr;
                Canvas.SetLeft(wash, 1);
                Canvas.SetTop(wash, -1);
                root.Children.Add(wash);

                // 外圈：同色系深色软边（不再用深蓝描边）
                var halo = new System.Windows.Shapes.Ellipse
                {
                    Width = 46, Height = 46, Fill = null,
                    Stroke = new SolidColorBrush(WithAlpha(deepCol, 0x3E)), StrokeThickness = 2.6
                };
                Canvas.SetLeft(halo, 9);
                Canvas.SetTop(halo, 7);
                root.Children.Add(halo);

                // 主体：手绘感的圆形 + 水彩渐变
                var bodyGeo = BlobGeo(32.0, 29.5, 20.6, i * 1.7);
                var brN = BubbleBrush(baseCol);
                var brOn = BubbleBrush(Col(0xFF, 0xFFC359));
                var body = new System.Windows.Shapes.Path { Data = bodyGeo, Fill = brN, IsHitTestVisible = false };
                root.Children.Add(body);

                // 第二层颜料（相邻色混进来，像水彩叠色）
                var tint = new System.Windows.Shapes.Ellipse { Width = 21, Height = 14 };
                var tintBr = new RadialGradientBrush();
                tintBr.GradientStops.Add(new GradientStop(WithAlpha(mixCol, 0x4E), 0.0));
                tintBr.GradientStops.Add(new GradientStop(WithAlpha(mixCol, 0x00), 1.0));
                tintBr.Freeze();
                tint.Fill = tintBr;
                tint.RenderTransform = new RotateTransform(32);
                tint.RenderTransformOrigin = new Point(0.5, 0.5);
                Canvas.SetLeft(tint, 24);
                Canvas.SetTop(tint, 26);
                root.Children.Add(tint);

                // 边缘：淡色软描边（比原来的白色亮弧弱很多）
                var rim = new System.Windows.Shapes.Path
                {
                    Data = bodyGeo, Fill = null,
                    Stroke = new SolidColorBrush(WithAlpha(MixColor(baseCol, Colors.White, 0.80), 0x8C)),
                    StrokeThickness = 1.0, IsHitTestVisible = false
                };
                var rimNorm = rim.Stroke;
                root.Children.Add(rim);

                // 高光：压小压淡（原来太大太亮）
                var hi = new System.Windows.Shapes.Ellipse
                {
                    Width = 10.5, Height = 6.0,
                    Fill = new SolidColorBrush(WithAlpha(softCol, 0x8E))
                };
                hi.RenderTransform = new RotateTransform(-26);
                hi.RenderTransformOrigin = new Point(0.5, 0.5);
                Canvas.SetLeft(hi, 18.5);
                Canvas.SetTop(hi, 17);
                root.Children.Add(hi);

                // 右下反光：也压淡
                var ca = new System.Windows.Shapes.Ellipse { Width = 13, Height = 7, Opacity = 0.26 };
                var caBr = new RadialGradientBrush();
                caBr.GradientStops.Add(new GradientStop(WithAlpha(softCol, 0xCC), 0.0));
                caBr.GradientStops.Add(new GradientStop(WithAlpha(softCol, 0x00), 1.0));
                caBr.Freeze();
                ca.Fill = caBr;
                ca.RenderTransform = new RotateTransform(24);
                ca.RenderTransformOrigin = new Point(0.5, 0.5);
                Canvas.SetLeft(ca, 27);
                Canvas.SetTop(ca, 38);
                root.Children.Add(ca);

                var gl = new System.Windows.Shapes.Ellipse { Width = 72, Height = 72, Opacity = 0.0 };
                var glBr = new RadialGradientBrush();
                var glCol = MixColor(baseCol, Colors.White, 0.45);
                glBr.GradientStops.Add(new GradientStop(WithAlpha(glCol, 0x00), 0.50));
                glBr.GradientStops.Add(new GradientStop(WithAlpha(glCol, 0x38), 0.78));
                glBr.GradientStops.Add(new GradientStop(WithAlpha(glCol, 0x00), 1.0));
                glBr.Freeze();
                gl.Fill = glBr;
                Canvas.SetLeft(gl, -4);
                Canvas.SetTop(gl, -4);
                root.Children.Add(gl);

                // 选中环：选中那颗才亮
                var ring = new System.Windows.Shapes.Ellipse
                {
                    Width = 50, Height = 50, Fill = null, Opacity = 0.0,
                    Stroke = new SolidColorBrush(Col(0xE8, 0xF4FDFF)), StrokeThickness = 1.8
                };
                Canvas.SetLeft(ring, 7);
                Canvas.SetTop(ring, 5);
                root.Children.Add(ring);

                // 开关状态点
                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = 5, Height = 5,
                    Fill = new SolidColorBrush(Col(0xFF, 0xFFE9A8)),
                    Visibility = Visibility.Collapsed
                };
                Canvas.SetLeft(dot, 44);
                Canvas.SetTop(dot, 36);
                root.Children.Add(dot);

                var st = new ScaleTransform(1, 1);
                root.RenderTransform = st;
                root.RenderTransformOrigin = new Point(0.5, 0.5);
                AddDecoUnder(5, root);
                _orbBodies.Add(root);
                _orbScale.Add(st);
                _orbSelGlow.Add(gl);
                _orbSelRing.Add(ring);
                _orbBodyEls.Add(body);
                _orbRimEls.Add(rim);
                _orbBrNorm.Add(brN);
                _orbBrOn.Add(brOn);
                _orbRimNorm.Add(rimNorm);
                _orbStateDot.Add(dot);
                _orbPhase.Add(i * 0.63);
                _orbSizeVar.Add(0.90 + 0.16 * Math.Abs(Math.Sin(i * 2.7 + 0.4)));   // 大小不一，像图里那样
            }

            // 水彩飞溅：轨道周围几粒半透明颜料点，像笔尖甩出来的
            var splat = new Random(1314);
            for (int i = 0; i < 16; i++)
            {
                double a = splat.NextDouble() * Math.PI * 2.0;
                double x, y;
                OrbPos(a, out x, out y);
                double jx = (splat.NextDouble() - 0.5) * 26.0;
                double jy = (splat.NextDouble() - 0.5) * 20.0;
                double rr = 1.4 + splat.NextDouble() * 2.6;
                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = rr * 2, Height = rr * 2,
                    Fill = new SolidColorBrush(WithAlpha(VividColor(i), 0x66)),
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(dot, cx + x + jx - rr);
                Canvas.SetTop(dot, cy + y + jy - rr);
                AddDecoUnder(5, dot);
            }
            for (int i = 0; i < 5; i++)
            {
                double a = splat.NextDouble() * Math.PI * 2.0;
                double x, y;
                OrbPos(a, out x, out y);
                double rr = 5.0 + splat.NextDouble() * 5.0;
                var blob = new System.Windows.Shapes.Ellipse { Width = rr * 2, Height = rr * 1.6, Opacity = 0.20 };
                var blobBr = new RadialGradientBrush();
                var bc = VividColor(i * 3 + 1);
                blobBr.GradientStops.Add(new GradientStop(WithAlpha(bc, 0x88), 0.0));
                blobBr.GradientStops.Add(new GradientStop(WithAlpha(bc, 0x00), 1.0));
                blobBr.Freeze();
                blob.Fill = blobBr;
                Canvas.SetLeft(blob, cx + x - rr);
                Canvas.SetTop(blob, cy + y - rr * 0.8);
                AddDecoUnder(5, blob);
            }

            // 轨道外几颗小星芒
            var rnd = new Random(520);
            for (int i = 0; i < 12; i++)
            {
                double a = rnd.NextDouble() * Math.PI * 2.0;
                double rr = 150.0 + rnd.NextDouble() * 25.0;
                var sp = MakeSparkleEl(cx + rr * Math.Cos(a), cy + rr * Math.Sin(a) * 0.92,
                                       2.6 + rnd.NextDouble() * 3.2, Col(0xCC, 0xE8F8FF));
                AddDecoUnder(5, sp);
                _orbSparkles.Add(sp);
                _orbSparkleBase.Add(0.25 + rnd.NextDouble() * 0.6);
            }
        }

        // 每帧：轨道缓动、星体定位/缩放/遮挡、星芒呼吸
        void OrbitTick(bool near)
        {
            // 角度差折到 (-180, 180]，永远走最短路径
            double dd = _orbTarget - _orbAngle;
            dd -= 360.0 * Math.Floor((dd + 180.0) / 360.0);
            if (Math.Abs(dd) > 0.05) _orbAngle += dd * (near ? 0.30 : 0.24);
            else _orbAngle += dd;
            if (_orbAngle > 720.0) { _orbAngle -= 720.0; _orbTarget -= 720.0; }
            else if (_orbAngle < -720.0) { _orbAngle += 720.0; _orbTarget += 720.0; }

            double cx = RadialSize / 2.0, cy = RadialSize / 2.0;
            for (int i = 0; i < _orbBodies.Count; i++)
            {
                double phi = (90.0 + i * OrbStepDeg + _orbAngle) * Math.PI / 180.0;
                double x, y;
                OrbPos(phi, out x, out y);
                double depth = Math.Sin(phi);                       // 1 = 正前方，-1 = 正后方
                double f = (depth + 1.0) * 0.5;
                double bob = Math.Sin(_flowPhase * 1.25 + _orbPhase[i]) * 2.2 * f;
                bool sel = (i == _orbIndex);
                double scl = (0.34 + 0.88 * Math.Pow(f, 0.90)) * _orbSizeVar[i] * (sel ? 1.34 : 1.0);
                _orbScale[i].ScaleX = scl;
                _orbScale[i].ScaleY = scl;

                var root = _orbBodies[i];
                Canvas.SetLeft(root, cx + x - 32.0);
                Canvas.SetTop(root, cy + y + bob - 32.0 - (sel ? 5.0 : 0.0));

                // 转到球背面 → 被豆仔挡住（留出星体自身半径，避免压在球边上）
                double bdx = x, bdy = y + bob - BallOffY;
                double bodyR = 21.0 * scl;
                bool behind = depth < 0.02 && Math.Sqrt(bdx * bdx + bdy * bdy) < BallVisR + bodyR * 0.95;
                root.Opacity = behind ? 0.0 : (0.04 + 0.96 * Math.Pow(f, 1.15));
                Panel.SetZIndex(root, 200 + (int)(depth * 100.0));
                _orbSelGlow[i].Opacity = sel && !behind ? (0.82 + 0.18 * Math.Sin(_flowPhase * 2.2)) : 0.0;
                _orbSelRing[i].Opacity = sel && !behind ? (0.55 + 0.35 * Math.Sin(_flowPhase * 2.2)) : 0.0;
            }
            for (int i = 0; i < _orbSparkles.Count; i++)
                _orbSparkles[i].Opacity = _orbSparkleBase[i] * (0.35 + 0.65 * (0.5 + 0.5 * Math.Sin(_flowPhase * 1.6 + i * 1.3)));
            if (_orbPathLine != null) _orbPathLine.Opacity = 0.55 + 0.25 * Math.Sin(_flowPhase * 1.1);
        }

        void OrbReset()
        {
            _orbIndex = OrbNorm(_orbIndex);
            _orbDragSlots = 0;
            _orbDragStart = -1;
            _orbTarget = -_orbIndex * OrbStepDeg;
            _orbAngle = _orbTarget;
            _orbSpoken = null;
            SpeakOrb();
        }

        // 让豆仔用说话气泡念出当前选中项
        void SpeakOrb()
        {
            if (_radialItems == null || _orbIndex < 0 || _orbIndex >= _radialItems.Count) return;
            var it = _radialItems[_orbIndex];
            string name = it.DynLabel != null ? it.DynLabel() : it.Label;
            if (name == _orbSpoken) return;
            _orbSpoken = name;
            try { ShowBubble(name); } catch { }
        }

        void OrbitRotate(int dir)
        {
            _orbIndex = OrbNorm(_orbIndex + dir);
            _orbTarget = -_orbIndex * OrbStepDeg;
            Log.Write("orbit rotate -> " + _orbIndex + " " + _radialItems[_orbIndex].Label);
            SpeakOrb();
        }

        // 左右拖动：轨道跟手转，选中项实时切换
        void OrbitDragTick(Point p)
        {
            if (_orbDragStart < 0) _orbDragStart = _orbIndex;
            _orbDragSlots = (p.X - _radialDownScreen.X) / 62.0;
            int ni = OrbNorm(_orbDragStart + (int)Math.Round(_orbDragSlots));
            if (ni != _orbIndex) { _orbIndex = ni; SpeakOrb(); }
            _orbTarget = -(_orbDragStart + _orbDragSlots) * OrbStepDeg;
        }

        // 松手：吸附到最近一格；确实拖动过就直接执行
        void OrbitRelease()
        {
            int start = _orbDragStart < 0 ? _orbIndex : _orbDragStart;
            bool dragged = Math.Abs(_orbDragSlots) > 0.18;
            _orbIndex = OrbNorm(start + (int)Math.Round(_orbDragSlots));
            _orbDragSlots = 0;
            _orbDragStart = -1;
            _orbTarget = -_orbIndex * OrbStepDeg;
            if (dragged) RunRadialItem();
            else SpeakOrb();
        }

        // 已开启的开关项：星体换成暖金色
        void OrbSyncStateDots()
        {
            if (_radialItems == null) return;
            for (int i = 0; i < _orbStateDot.Count && i < _radialItems.Count; i++)
            {
                var it = _radialItems[i];
                bool on = (it.IsOn != null && it.IsOn());
                _orbStateDot[i].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                if (i < _orbBodyEls.Count)
                {
                    _orbBodyEls[i].Fill = on ? _orbBrOn[i] : _orbBrNorm[i];
                    _orbRimEls[i].Stroke = on ? _orbRimOn : _orbRimNorm[i];
                }
            }
        }

        void ApplyRadialStyle()
        {
            if (_radialBase != null) _radialBase.Fill = _baseFill[_radialStyle];
            if (_radialRing != null)
                _radialRing.Stroke = _radialStyle == 0
                    ? new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF))
                    : Brushes.Transparent;
            for (int s = 0; s < StyleCount; s++)
            {
                var vis = (s == _radialStyle) ? Visibility.Visible : Visibility.Collapsed;
                for (int i = 0; i < _decos[s].Count; i++) _decos[s][i].Visibility = vis;
            }
            if (_radialChip != null)
            {
                double cxc = RadialSize / 2.0, cyc = RadialSize / 2.0;
                Canvas.SetLeft(_radialChip, cxc + ChipHitX() - 21);
                Canvas.SetTop(_radialChip, cyc + ChipHitY() - 10);
                _radialChip.BorderBrush = _chipBorder[_radialStyle];
                _radialChipText.Foreground = _textNorm[_radialStyle];
                _radialChip.Background = _radialStyle == 0
                    ? new SolidColorBrush(Color.FromArgb(0xB4, 0x1A, 0x18, 0x14))
                    : (_radialStyle == 5
                        ? new SolidColorBrush(Color.FromArgb(0xD2, 0x14, 0x1C, 0x46))
                        : new SolidColorBrush(Color.FromArgb(0xC8, 0x12, 0x10, 0x14)));
            }
            if (_radialItems != null)
            {
                var tickBrush = _textNorm[_radialStyle];
                bool orbit = (_radialStyle == 5);
                for (int i = 0; i < _radialItems.Count; i++)
                {
                    var it = _radialItems[i];
                    it.Tick.Foreground = tickBrush;
                    it.Text.Visibility = orbit ? Visibility.Collapsed : Visibility.Visible;
                    it.Shape.Visibility = orbit ? Visibility.Collapsed : Visibility.Visible;
                    if (orbit) it.Tick.Visibility = Visibility.Hidden;
                    else if (it.IsOn != null) it.Tick.Visibility = it.IsOn() ? Visibility.Visible : Visibility.Hidden;
                }
            }
            ApplyRadialHover();
        }

        void ToggleRadialStyle()
        {
            _radialStyle = (_radialStyle + 1) % StyleCount;
            ApplyRadialStyle();
            if (_radialStyle == 5 && _radialOpen) { OrbSyncStateDots(); OrbReset(); }
            SaveSettings();
            Log.Write("radial style=" + _radialStyle + " " + StyleNames[_radialStyle]);
        }

        // 配置只存轮盘样式这一项，换成 key=value 续着写就行
        static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DouzaiDesktopPet", "config.ini");

        void LoadSettings()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return;
                foreach (var raw in File.ReadAllLines(ConfigPath))
                {
                    var line = raw.Trim();
                    if (line.StartsWith("style="))
                    {
                        int v;
                        if (int.TryParse(line.Substring(6).Trim(), out v) && v >= 0 && v < StyleCount)
                            _radialStyle = v;
                    }
                    else if (line.StartsWith("ai_url=")) _aiUrl = line.Substring(7).Trim();
                    else if (line.StartsWith("ai_key=")) _aiKey = line.Substring(7).Trim();
                    else if (line.StartsWith("ai_model=")) _aiModel = line.Substring(9).Trim();
                    else if (line.StartsWith("ai_prompt=")) _aiPrompt = line.Substring(10).Trim();
                }
                Log.Write("config load ai=" + (IsAiConfigured() ? (_aiModel + " @ " + _aiUrl) : "(not configured)"));
                Log.Write("config load style=" + _radialStyle + " " + StyleNames[_radialStyle]);
            }
            catch (Exception ex) { Log.Write("config load: " + ex.Message); }
        }

        void SaveSettings()
        {
            try
            {
                string dir = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllLines(ConfigPath, new string[]
                {
                    "# 豆仔配置：删掉这个文件就回到默认（星体轨道）",
                    "style=" + _radialStyle,
                    "ai_url=" + (_aiUrl ?? ""),
                    "ai_key=" + (_aiKey ?? ""),
                    "ai_model=" + (_aiModel ?? ""),
                    "ai_prompt=" + ((_aiPrompt ?? "").Replace("\r", " ").Replace("\n", " "))
                }, Encoding.UTF8);
            }
            catch (Exception ex) { Log.Write("config save: " + ex.Message); }
        }

        void ToggleDoze()
        {
            if (IsDoze()) { Wake(); SetMood("neutral"); }
            else EnterDoze();
            Log.Write("radial doze=" + IsDoze());
        }

        void ToggleOrb()
        {
            _orbEnabled = !_orbEnabled;
            if (!_orbEnabled && _state == State.Walk && _hasWalkTarget) { _hasWalkTarget = false; EnterIdle(); }
            Log.Write("orb=" + _orbEnabled);
        }

        void ToggleFollowMouse()
        {
            _followMouse = !_followMouse;
            Log.Write("follow mouse=" + _followMouse);
            OnFollowChanged();
        }

        void ToggleAutoStart()
        {
            AutoStart.Set(!AutoStart.IsEnabled());
            Log.Write("autostart=" + AutoStart.IsEnabled());
        }

        Point CursorDip()
        {
            NativePoint cp;
            if (!GetCursorPos(out cp)) return new Point(_radialCx, _radialCy);
            var src = PresentationSource.FromVisual(this);
            double s = src != null ? src.CompositionTarget.TransformToDevice.M11 : 1.0;
            return new Point(cp.X / s, cp.Y / s);
        }

        void ShowRadialMenu()
        {
            if (_radial == null) BuildRadial();
            _radial.IsOpen = false;              // 先关一次，保证每次都是干净的新位置

            // 开关项状态刷新
            for (int i = 0; i < _radialItems.Count; i++)
            {
                var it = _radialItems[i];
                if (it.DynLabel != null) it.Text.Text = it.DynLabel();
                if (it.IsOn != null) it.Tick.Visibility = it.IsOn() ? Visibility.Visible : Visibility.Hidden;
            }

            var area = WorkArea();
            double wantX = Left + Width / 2.0;
            double wantY = Top + 160.0;
            _radialCx = Math.Min(Math.Max(wantX, area.Left + RadialOuter + 6), area.Right - RadialOuter - 6);
            _radialCy = Math.Min(Math.Max(wantY, area.Top + RadialOuter + 6), area.Bottom - RadialOuter - 6);

            _radial.PlacementTarget = this;
            _radial.Placement = System.Windows.Controls.Primitives.PlacementMode.Relative;
            _radial.HorizontalOffset = _radialCx - Left - RadialSize / 2.0;
            _radial.VerticalOffset = _radialCy - Top - RadialSize / 2.0;
            _radial.IsOpen = true;

            // popup 默认在 Topmost 主窗之下 → 手动提成置顶，否则豆仔会盖住轮盘
            try
            {
                var hs = PresentationSource.FromVisual(_radialCanvas) as System.Windows.Interop.HwndSource;
                if (hs != null) SetWindowPos(hs.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0013);   // TOPMOST|NOMOVE|NOSIZE|NOACTIVATE
            }
            catch (Exception ex) { Log.Write("radial topmost: " + ex.Message); }

            _radialOpen = true;
            _radialHover = -1;
            _radialMoveMax = 0;
            _radialDownScreen = CursorDip();
            ApplyRadialHover();
            if (_radialStyle == 5) { OrbSyncStateDots(); OrbReset(); }
            StartFlow();
            CaptureMouse();
            Log.Write("radial open at " + (int)_radialCx + "," + (int)_radialCy);
        }

        void CloseRadial()
        {
            if (!_radialOpen) return;
            _radialOpen = false;
            _radialHover = -1;
            try { if (_radial != null) _radial.IsOpen = false; } catch { }
            if (IsMouseCaptured) ReleaseMouseCapture();
            StopFlow();
            Log.Write("radial close");
        }

        int HitRadial()
        {
            var p = CursorDip();
            double dx = p.X - _radialCx, dy = p.Y - _radialCy;
            if (Math.Abs(dx - ChipHitX()) < 23 && Math.Abs(dy - ChipHitY()) < 12) return HitChip;   // 「样式」按钮
            if (_radialStyle == 5) return -1;                                                  // 星体轨道：不按角度取扇区
            double r = Math.Sqrt(dx * dx + dy * dy);
            if (r < RadialInner || r > RadialOuter) return -1;
            double ang = Math.Atan2(dy, dx) * 180.0 / Math.PI;      // 0 = 正右
            double idx = Math.Round((ang + 90.0) / (360.0 / RadialCount));
            int i = ((int)idx) % RadialCount;
            if (i < 0) i += RadialCount;
            return i;
        }

        void UpdateRadialHover()
        {
            if (!_radialOpen) return;
            var p = CursorDip();
            double d = Math.Sqrt((p.X - _radialDownScreen.X) * (p.X - _radialDownScreen.X) + (p.Y - _radialDownScreen.Y) * (p.Y - _radialDownScreen.Y));
            if (d > _radialMoveMax) _radialMoveMax = d;
            if (_radialStyle == 5)
            {
                // 星体轨道：只有按住鼠标拖动才转轨道（否则移动鼠标去点「样式」会误转）
                int h0 = HitRadial();
                if (h0 != _radialHover) { _radialHover = h0; ApplyRadialHover(); }
                if (Mouse.LeftButton == MouseButtonState.Pressed || Mouse.RightButton == MouseButtonState.Pressed)
                    OrbitDragTick(p);
                return;
            }
            int h = HitRadial();
            if (h != _radialHover) { _radialHover = h; ApplyRadialHover(); }
        }

        void ApplyRadialHover()
        {
            if (_radialItems == null) return;
            int s = _radialStyle;
            // 星体轨道：没有扇区高亮，只保留右下角「样式」按钮的悬停反馈
            if (s == 5)
            {
                if (_radialChip != null)
                {
                    bool hot0 = (_radialHover == HitChip);
                    _radialChip.Opacity = hot0 ? 1.0 : 0.92;
                    _radialChip.BorderThickness = new Thickness(hot0 ? 2 : 1);
                    _radialChipText.FontWeight = hot0 ? FontWeights.Bold : FontWeights.Normal;
                }
                return;
            }
            Color strokeN, strokeH;
            if (s == 1) { strokeN = Color.FromArgb(0x14, 0xFF, 0xD0, 0x80); strokeH = Color.FromArgb(0xFF, 0xFF, 0xE2, 0xAC); }
            else if (s == 2) { strokeN = Color.FromArgb(0x22, 0x9F, 0xD8, 0xFF); strokeH = Color.FromArgb(0xFF, 0xCF, 0xEF, 0xFF); }
            else if (s == 3) { strokeN = Color.FromArgb(0x18, 0xFF, 0x58, 0xC8); strokeH = Color.FromArgb(0xFF, 0x9F, 0xF0, 0xFF); }
            else { strokeN = Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF); strokeH = Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF); }
            var strokeNorm = new SolidColorBrush(strokeN);
            var strokeHot = new SolidColorBrush(strokeH);
            for (int i = 0; i < _radialItems.Count; i++)
            {
                var it = _radialItems[i];
                bool hot = (i == _radialHover);
                it.Shape.Fill = hot ? it.Hot[s] : it.Norm[s];
                it.Shape.Stroke = hot ? strokeHot : strokeNorm;
                it.Shape.StrokeThickness = hot ? 2 : 1;
                it.Text.Foreground = hot ? _textHot[s] : _textNorm[s];
                it.Text.FontWeight = hot ? FontWeights.Bold : FontWeights.SemiBold;
            }
            if (_radialChip != null)
            {
                bool chipHot = (_radialHover == HitChip);
                _radialChip.Opacity = chipHot ? 1.0 : 0.92;
                _radialChip.BorderThickness = new Thickness(chipHot ? 2 : 1);
                _radialChipText.FontWeight = chipHot ? FontWeights.Bold : FontWeights.Normal;
            }
        }

        // —— 霓虹流动 ——
        // 不开 Effect、不做逐帧重建画笔：只改 12 个已有 SolidColorBrush 的 Opacity，
        // 一道"彗星"光贴着环顺时针跑，后面拖 4 格的尾巴。轮盘一关就停，闲时零开销。
        void StartFlow()
        {
            if (_flowTimer == null)
            {
                _flowTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
                _flowTimer.Tick += delegate
                {
                    try { FlowTick(); }
                    catch (Exception ex) { Log.Write("flow: " + ex.Message); StopFlow(); }
                };
            }
            _flowPhase = 0;
            
            _flowTimer.Start();
        }

        void StopFlow()
        {
            
            if (_flowTimer != null) _flowTimer.Stop();
            // 把被流动压暗的画笔还回满亮度，下次打开不至于残留半透明
            if (_radialItems != null)
            {
                for (int i = 0; i < _radialItems.Count; i++)
                {
                    var it = _radialItems[i];
                    for (int s = 0; s < StyleCount; s++)
                    {
                        if (it.Norm[s] != null) it.Norm[s].Opacity = 1.0;
                        if (it.Hot[s] != null) it.Hot[s].Opacity = 1.0;
                    }
                }
            }
        }

        void FlowTick()
        {
            if (!_radialOpen || _radialItems == null) { StopFlow(); return; }
            var p = CursorDip();
            // 鼠标停在轮盘范围内 → 流动提速（0.3 → 0.6 格/帧），移开恢复
            bool near = Math.Abs(p.X - _radialCx) < RadialOuter + 14 && Math.Abs(p.Y - _radialCy) < RadialOuter + 14;
            _flowPhase = (_flowPhase + (near ? 0.4 : 0.2)) % RadialCount;   // 0.2 格/帧 ≈ 3.6 秒转一圈
            int s = _radialStyle;
            if (s == 5) { OrbitTick(near); return; }          // 星环：星尘绕环 + 星芒呼吸，扇区不参与流光
            if (s == 4 && _nebulaSpin != null)
                _nebulaSpin.Angle = (_nebulaSpin.Angle + (near ? 1.0 : 0.5)) % 360;   // 星云慢转：约 43 秒一圈，悬停快一倍
            for (int i = 0; i < _radialItems.Count; i++)
            {
                var it = _radialItems[i];
                if (i == _radialHover) { it.Shape.Fill = it.Hot[s]; it.Hot[s].Opacity = 1.0; continue; }
                if (s == 1 || s == 4) { it.Shape.Fill = it.Norm[s]; it.Norm[s].Opacity = 1.0; continue; }  // 法阵/星云：扇区不吃流动色
                double d = i - _flowPhase;
                d = ((d % RadialCount) + RadialCount) % RadialCount;
                double w = d < 4 ? (1.0 - d / 4.0) : 0.0;   // 头亮，尾四格渐弱
                w = w * w;                                   // 平方一下，亮头更集中
                if (s == 0 || s == 2)                        // 彩格/星空：底色保持满色，亮头用"提亮版"盖上去，不往黑里压
                {
                    if (d < 3) { it.Shape.Fill = it.Hot[s]; it.Hot[s].Opacity = 0.30 + 0.70 * w; }
                    else { it.Shape.Fill = it.Norm[s]; it.Norm[s].Opacity = 1.0; }
                }
                else                                          // 法阵/霓虹：底色是透明的，用金色/霓虹光当亮头
                {
                    it.Shape.Fill = it.Hot[s];
                    it.Hot[s].Opacity = 0.12 + 0.88 * w;
                }
            }
        }

        void RunRadialItem()
        {
            int h = (_radialStyle == 5) ? _orbIndex : _radialHover;
            CloseRadial();
            if (h < 0 || _radialItems == null || h >= _radialItems.Count) return;
            try
            {
                Log.Write("radial run " + _radialItems[h].Label);
                _radialItems[h].Run();
            }
            catch (Exception ex) { Log.Write("radial run: " + ex.Message); }
        }

        void OnRadialDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (_radialOpen) { CloseRadial(); e.Handled = true; return; }
                ShowRadialMenu();
                e.Handled = true;
            }
            catch (Exception ex)
            {
                Log.Write("radial down: " + ex.Message);
                CloseRadial();
                TryOpenMenu();                    // 轮盘挂了就退回列表菜单，别让右键失灵
            }
        }

        void OnRadialUp(object sender, MouseButtonEventArgs e)
        {
            if (!_radialOpen) return;
            UpdateRadialHover();
            if (_radialHover == HitChip) { ToggleRadialStyle(); e.Handled = true; return; }
            if (_radialStyle == 5) { OrbitRelease(); e.Handled = true; return; }
            if (_radialHover >= 0 && _radialMoveMax > 10) RunRadialItem();
            else if (_radialMoveMax <= 10) { /* 只点了一下：保持展开，等左键点选 */ }
            else CloseRadial();
            e.Handled = true;
        }

        // 星体轨道：滚轮左右转菜单
        void OnRadialWheel(object sender, MouseWheelEventArgs e)
        {
            if (!_radialOpen || _radialStyle != 5) return;
            OrbitRotate(e.Delta > 0 ? -1 : 1);
            e.Handled = true;
        }

        // P0-4 跟随开关变化：关闭时暂停定时器（CPU 归零）并让瞳孔回正
        void OnFollowChanged()
        {
            try
            {
                if (_pupilTimer == null) return;
                if (_followMouse)
                {
                    _pupilTimer.Start();
                }
                else
                {
                    _pupilTimer.Stop();
                    _pupilCur = new Vector(0, 0);
                    _pupilTarget = new Vector(0, 0);
                    _nearBoost = 1.0;
                    if (_pupilOffset != null)
                    {
                        _pupilOffset.OffsetX = 0;
                        _pupilOffset.OffsetY = 0;
                    }
                    SetEyes(1.0);
                }
            }
            catch (Exception ex) { Log.Write("follow change: " + ex.Message); }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct NativePoint
        {
            public int X;
            public int Y;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool GetCursorPos(out NativePoint p);

        // P0-4 瞳孔跟随主循环（25fps）
        void PupilTick(object sender, EventArgs e)
        {
            try
            {
                NativePoint p;
                if (!GetCursorPos(out p)) return;

                // DPI 换算：GetCursorPos 是物理像素，Left/Top 是 DIP（125% 缩放必须转换）
                Vector cursorDip;
                var src = PresentationSource.FromVisual(this);
                if (src != null)
                {
                    var pt = src.CompositionTarget.TransformFromDevice.Transform(
                        new Point(p.X, p.Y));
                    cursorDip = new Vector(pt.X, pt.Y);
                }
                else
                {
                    cursorDip = new Vector(p.X, p.Y);
                }

                // 宠物头心（窗口内近似）
                var head = new Point(Left + Width * 0.5, Top + Height * 0.456);
                var v = cursorDip - new Vector(head.X, head.Y);
                double dist = v.Length;

                const double PupilMax = 0.075;  // 3D 单位最大偏移
                if (dist > 900 || dist < 0.001)
                {
                    _pupilTarget = new Vector(0, 0);   // 远处缓慢回正
                    _nearBoost = 1.0;
                }
                else
                {
                    double mag = Math.Min(dist / 200.0, 1.0) * PupilMax;
                    v.Normalize();
                    _pupilTarget = new Vector(v.X * mag, v.Y * mag);
                    _nearBoost = dist < 60 ? 1.15 : 1.0;   // 靠近 60px 内眼睛放大
                }

                // 姿态接管时，注视方向由外部给，绕过鼠标跟随
                if (!double.IsNaN(_poseGazeX))
                    _pupilTarget = new Vector(_poseGazeX * PupilMax, _poseGazeY * PupilMax);

                // 缓动：每帧向目标插值 20%，不瞬移
                _pupilCur += (_pupilTarget - _pupilCur) * 0.2;
                _pupilOffset.OffsetX = _pupilCur.X;
                _pupilOffset.OffsetY = -_pupilCur.Y;   // 屏幕 Y 向下，3D Y 向上

                // 验收证据：节流记录瞳孔偏移（2 秒一次）
                if (_clock - _lastPupilLog > 2.0)
                {
                    _lastPupilLog = _clock;
                    Log.Write("pupil dx=" + _pupilCur.X.ToString("F3") +
                              " dy=" + _pupilCur.Y.ToString("F3") +
                              " dist=" + dist.ToString("F0") +
                              " boost=" + _nearBoost.ToString("F2"));
                }
            }
            catch (Exception ex)
            {
                Log.Write("pupil tick: " + ex.Message);
            }
        }

        // ================= P0-5 说话与嘴部动作 =================

        void StartTalking()
        {
            try
            {
                if (_mouthTimer != null && _mouthScale != null)
                {
                    if (_mood == "doze")
                    {
                        Log.Write("mouth open skip (doze)");
                        return;                 // 睡着时任何调用都不张合嘴，免得像吧唧嘴
                    }
                    _mouthTimer.Stop();
                    _mouthOpen = false;
                    _mouthTimer.Start();
                    Log.Write("mouth open (talking)");
                }
            }
            catch (Exception ex) { Log.Write("start talking: " + ex.Message); }
        }

        void StopTalking()
        {
            try
            {
                if (_mouthTimer != null)
                    _mouthTimer.Stop();
                if (_mouthScale != null)
                    _mouthScale.ScaleY = 0.4;   // 闭嘴
                Log.Write("mouth close");
            }
            catch (Exception ex) { Log.Write("stop talking: " + ex.Message); }
        }

        // ================= AI 聊天（OpenAI 兼容接口）=================
        // 右键菜单「说话」→ 打开聊天窗：消息发到配置的模型接口，回复显示在窗口里，
        // 同时让豆仔用说话气泡念出前 60 个字。接口地址 / Key / 模型 / 人设都在
        // 「设置」面板里填，存在 %APPDATA%\DouzaiDesktopPet\config.ini（本机明文）。
        // 没配置接口时会自动退回原来的「让豆仔说话」模式，功能不会坏。

        const string DefaultAiPrompt = "你是桌面宠物「豆仔」，一只圆滚滚的蓝色小球。说话简短、可爱、口语化，多用语气词；回答控制在 80 字以内，不要用 Markdown。";
        string _aiUrl = "", _aiKey = "", _aiModel = "", _aiPrompt = "";
        Window _chatWin;
        StackPanel _chatList;
        ScrollViewer _chatScroll;
        TextBox _chatInput;
        Button _chatSend;
        TextBlock _chatHint;
        bool _chatBusy;
        readonly List<string[]> _chatHistory = new List<string[]>();

        static string Shorten(string s, int max)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            if (s.Length <= max) return s;
            return s.Substring(0, max) + "…";
        }

        static string JsonStr(string s)
        {
            if (s == null) s = "";
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        // 从响应 JSON 里抠出第一个指定字段的字符串值（够用就行，不引第三方 JSON 库）
        static string ExtractJsonField(string json, string field)
        {
            if (string.IsNullOrEmpty(json)) return null;
            string pat = "\"" + field + "\"";
            int i = json.IndexOf(pat, StringComparison.Ordinal);
            if (i < 0) return null;
            i += pat.Length;
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t' || json[i] == '\r' || json[i] == '\n')) i++;
            if (i >= json.Length || json[i] != ':') return null;
            i++;
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t' || json[i] == '\r' || json[i] == '\n')) i++;
            if (i >= json.Length) return null;
            if (json[i] == '"')
            {
                i++;
                var sb = new StringBuilder();
                while (i < json.Length)
                {
                    char c = json[i++];
                    if (c == '"') break;
                    if (c == '\\' && i < json.Length)
                    {
                        char esc = json[i++];
                        switch (esc)
                        {
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'u':
                                if (i + 4 <= json.Length)
                                {
                                    int code;
                                    if (int.TryParse(json.Substring(i, 4), System.Globalization.NumberStyles.HexNumber, null, out code))
                                    {
                                        sb.Append((char)code);
                                        i += 4;
                                    }
                                }
                                break;
                            default: sb.Append(esc); break;
                        }
                    }
                    else sb.Append(c);
                }
                return sb.ToString();
            }
            int start = i;
            while (i < json.Length && json[i] != ',' && json[i] != '}' && json[i] != ']') i++;
            return json.Substring(start, i - start).Trim();
        }

        static string NormalizeChatUrl(string url)
        {
            url = (url ?? "").Trim();
            if (url.Length == 0) return url;
            url = url.TrimEnd('/');
            if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return url;
            return url + "/chat/completions";
        }

        // 后台线程里跑：POST /chat/completions，返回助手正文
        string CallAi(List<string[]> msgs, string url, string key, string model, string prompt)
        {
            var sb = new StringBuilder();
            sb.Append("{\"model\":").Append(JsonStr(model)).Append(",\"messages\":[");
            bool first = true;
            if (!string.IsNullOrEmpty(prompt))
            {
                sb.Append("{\"role\":\"system\",\"content\":").Append(JsonStr(prompt)).Append('}');
                first = false;
            }
            for (int i = 0; i < msgs.Count; i++)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"role\":").Append(JsonStr(msgs[i][0])).Append(",\"content\":").Append(JsonStr(msgs[i][1])).Append('}');
            }
            sb.Append("],\"temperature\":0.8,\"stream\":false}");
            byte[] data = Encoding.UTF8.GetBytes(sb.ToString());

            try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; } catch { }
            var req = (HttpWebRequest)WebRequest.Create(NormalizeChatUrl(url));
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Accept = "application/json";
            req.Timeout = 30000;
            req.ReadWriteTimeout = 30000;
            if (!string.IsNullOrEmpty(key)) req.Headers["Authorization"] = "Bearer " + key;
            req.ContentLength = data.Length;
            using (var rs = req.GetRequestStream()) rs.Write(data, 0, data.Length);

            string text;
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    text = sr.ReadToEnd();
            }
            catch (WebException wex)
            {
                string detail = "";
                try
                {
                    if (wex.Response != null)
                        using (var sr = new StreamReader(wex.Response.GetResponseStream(), Encoding.UTF8))
                            detail = sr.ReadToEnd();
                }
                catch { }
                Log.Write("ai http: " + wex.Message + " " + Shorten(detail, 200));
                throw new Exception(Shorten(detail.Length > 0 ? detail : wex.Message, 160));
            }

            string content = ExtractJsonField(text, "content");
            if (content == null)
            {
                string em = ExtractJsonField(text, "message");
                throw new Exception(Shorten(em != null ? em : text, 160));
            }
            content = content.Trim();
            if (content.Length == 0) content = "（模型没说话）";
            return content;
        }

        void AddChatBubble(string text, bool fromUser, bool system)
        {
            if (_chatList == null) return;
            var tb = new TextBlock
            {
                Text = text,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = system
                    ? new SolidColorBrush(Color.FromArgb(0xFF, 0x9A, 0xA6, 0xC0))
                    : (fromUser ? Brushes.White : new SolidColorBrush(Color.FromArgb(0xFF, 0x1E, 0x28, 0x38)))
            };
            var border = new Border
            {
                Background = system
                    ? new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF))
                    : (fromUser ? new SolidColorBrush(Color.FromArgb(0xFF, 0x4A, 0x7F, 0xD6))
                                : new SolidColorBrush(Color.FromArgb(0xFF, 0xEF, 0xF4, 0xFD))),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(9, 6, 9, 6),
                Margin = new Thickness(6, 4, 6, 4),
                HorizontalAlignment = fromUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                MaxWidth = 300,
                Child = tb
            };
            _chatList.Children.Add(border);
            try { _chatScroll.ScrollToEnd(); } catch { }
        }

        void UpdateChatHint()
        {
            if (_chatHint == null) return;
            string host = "未配置";
            try
            {
                if (!string.IsNullOrEmpty(_aiUrl))
                    host = new Uri(NormalizeChatUrl(_aiUrl)).Host;
            }
            catch { host = _aiUrl; }
            _chatHint.Text = "模型：" + (string.IsNullOrEmpty(_aiModel) ? "未填" : _aiModel) + "  ·  接口：" + host
                           + (string.IsNullOrEmpty(_aiUrl) ? "（点「设置」配置，或先当传话筒用）" : "");
        }

        void ClearChat()
        {
            _chatHistory.Clear();
            if (_chatList != null) _chatList.Children.Clear();
            AddChatBubble(IsAiConfigured() ? "清空啦，想聊点什么？" : "清空啦～先点「设置」填模型接口，我们就能聊天了。", false, true);
        }

        bool IsAiConfigured() { return !string.IsNullOrEmpty(_aiUrl) && !string.IsNullOrEmpty(_aiModel); }

        void SendChat()
        {
            if (_chatBusy || _chatInput == null) return;
            string text = (_chatInput.Text ?? "").Trim();
            if (text.Length == 0) return;
            if (text.Length > 2000) text = text.Substring(0, 2000);
            _chatInput.Clear();
            AddChatBubble(text, true, false);
            _chatHistory.Add(new string[] { "user", text });

            // 没配接口：退回「让豆仔念出来」
            if (!IsAiConfigured())
            {
                AddChatBubble("（还没配置模型接口：点右上角「设置」填 接口地址 / API Key / 模型名。）", false, true);
                ShowBubble(Shorten(text, 60), true, 4.0);
                return;
            }

            string url = _aiUrl, key = _aiKey, model = _aiModel, prompt = _aiPrompt;
            var msgs = new List<string[]>();
            int from = Math.Max(0, _chatHistory.Count - 12);
            for (int i = from; i < _chatHistory.Count; i++)
                msgs.Add(new string[] { _chatHistory[i][0], _chatHistory[i][1] });

            _chatBusy = true;
            if (_chatSend != null) _chatSend.IsEnabled = false;
            ShowBubble("豆仔想想…", true, 40.0);
            Log.Write("ai ask model=" + model);

            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                string reply = null, err = null;
                try { reply = CallAi(msgs, url, key, model, prompt); }
                catch (Exception ex) { err = ex.Message; }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    _chatBusy = false;
                    if (_chatSend != null) _chatSend.IsEnabled = true;
                    if (reply != null)
                    {
                        _chatHistory.Add(new string[] { "assistant", reply });
                        AddChatBubble(reply, false, false);
                        ShowBubble(Shorten(reply, 60), true, 6.0);
                        Log.Write("ai reply " + reply.Length + " chars");
                    }
                    else
                    {
                        AddChatBubble("（连接失败：" + (err ?? "未知错误") + "）", false, true);
                        ShowBubble("唔…连不上模型", true, 4.0);
                        Log.Write("ai error: " + err);
                    }
                }));
            });
        }

        void ShowAiSettings()
        {
            try
            {
                var dlg = new Window
                {
                    Title = "模型接口设置",
                    Width = 450,
                    Height = 440,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Topmost = true,
                    ResizeMode = ResizeMode.NoResize
                };
                var sp = new StackPanel { Margin = new Thickness(16) };
                sp.Children.Add(new TextBlock { Text = "OpenAI 兼容接口", FontWeight = FontWeights.Bold, FontSize = 14, Margin = new Thickness(0, 0, 0, 10) });
                sp.Children.Add(new TextBlock { Text = "接口地址（base，例：https://api.deepseek.com/v1 或 https://ark.cn-beijing.volces.com/api/v3）", Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap });
                var tbUrl = new TextBox { Text = _aiUrl, Margin = new Thickness(0, 3, 0, 8) };
                sp.Children.Add(tbUrl);
                sp.Children.Add(new TextBlock { Text = "API Key（只存在本机 config.ini，可留空）", Foreground = Brushes.Gray });
                var tbKey = new TextBox { Text = _aiKey, Margin = new Thickness(0, 3, 0, 8) };
                sp.Children.Add(tbKey);
                sp.Children.Add(new TextBlock { Text = "模型名（例：deepseek-chat / gpt-4o-mini / qwen2.5 / 豆包推理接入点 ep-xxx）", Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap });
                var tbModel = new TextBox { Text = _aiModel, Margin = new Thickness(0, 3, 0, 8) };
                sp.Children.Add(tbModel);
                sp.Children.Add(new TextBlock { Text = "人设提示词（可选）", Foreground = Brushes.Gray });
                var tbPrompt = new TextBox
                {
                    Text = string.IsNullOrEmpty(_aiPrompt) ? DefaultAiPrompt : _aiPrompt,
                    AcceptsReturn = true,
                    Height = 74,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Margin = new Thickness(0, 3, 0, 12)
                };
                sp.Children.Add(tbPrompt);
                var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                var btnSave = new Button { Content = "保存", Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
                var btnCancel = new Button { Content = "取消", Width = 80, IsCancel = true };
                row.Children.Add(btnSave);
                row.Children.Add(btnCancel);
                sp.Children.Add(row);
                dlg.Content = sp;
                btnSave.Click += delegate
                {
                    _aiUrl = (tbUrl.Text ?? "").Trim();
                    _aiKey = (tbKey.Text ?? "").Trim();
                    _aiModel = (tbModel.Text ?? "").Trim();
                    _aiPrompt = (tbPrompt.Text ?? "").Trim();
                    SaveSettings();
                    UpdateChatHint();
                    Log.Write("ai settings saved url=" + _aiUrl + " model=" + _aiModel);
                    dlg.Close();
                };
                dlg.ShowDialog();
            }
            catch (Exception ex) { Log.Write("ai settings: " + ex.Message); }
        }

        void ShowChatWindow()
        {
            try
            {
                if (_chatWin != null)
                {
                    if (!_chatWin.IsVisible) _chatWin.Show();
                    _chatWin.Activate();
                    if (_chatInput != null) _chatInput.Focus();
                    return;
                }

                var win = new Window
                {
                    Title = "豆仔 · AI 聊天",
                    Width = 390,
                    Height = 540,
                    MinWidth = 330,
                    MinHeight = 380,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Topmost = true,
                    ResizeMode = ResizeMode.CanResizeWithGrip,
                    Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x15, 0x1A, 0x2B))
                };
                var root = new Grid();
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var header = new Grid { Margin = new Thickness(12, 10, 12, 4) };
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var title = new TextBlock
                {
                    Text = "豆仔 · AI 聊天",
                    Foreground = Brushes.White,
                    FontSize = 15,
                    FontWeight = FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(title, 0);
                header.Children.Add(title);
                var btnSet = new Button { Content = "设置", Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(4, 0, 0, 0) };
                btnSet.Click += delegate { ShowAiSettings(); };
                Grid.SetColumn(btnSet, 1);
                header.Children.Add(btnSet);
                var btnClear = new Button { Content = "清空", Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(4, 0, 0, 0) };
                btnClear.Click += delegate { ClearChat(); };
                Grid.SetColumn(btnClear, 2);
                header.Children.Add(btnClear);
                root.Children.Add(header);
                Grid.SetRow(header, 0);

                _chatHint = new TextBlock
                {
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x8C, 0x98, 0xB0)),
                    Margin = new Thickness(14, 0, 14, 6),
                    TextWrapping = TextWrapping.Wrap
                };
                root.Children.Add(_chatHint);
                Grid.SetRow(_chatHint, 1);

                _chatScroll = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Margin = new Thickness(10, 2, 10, 6),
                    Padding = new Thickness(2),
                    Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x10, 0x14, 0x22))
                };
                _chatList = new StackPanel();
                _chatScroll.Content = _chatList;
                root.Children.Add(_chatScroll);
                Grid.SetRow(_chatScroll, 2);

                var inputBar = new DockPanel { Margin = new Thickness(10, 4, 10, 12), LastChildFill = true };
                _chatSend = new Button { Content = "发送", Width = 66, Margin = new Thickness(6, 0, 0, 0) };
                _chatSend.Click += delegate { SendChat(); };
                DockPanel.SetDock(_chatSend, Dock.Right);
                _chatInput = new TextBox { FontSize = 13, Padding = new Thickness(6, 4, 6, 4) };
                _chatInput.KeyDown += delegate(object s2, KeyEventArgs e2)
                {
                    if (e2.Key == Key.Enter || e2.Key == Key.Return) { SendChat(); e2.Handled = true; }
                };
                inputBar.Children.Add(_chatSend);
                inputBar.Children.Add(_chatInput);
                root.Children.Add(inputBar);
                Grid.SetRow(inputBar, 3);

                win.Content = root;
                win.Closed += delegate
                {
                    _chatWin = null;
                    _chatList = null;
                    _chatScroll = null;
                    _chatInput = null;
                    _chatSend = null;
                    _chatHint = null;
                };
                win.PreviewKeyDown += delegate(object s2, KeyEventArgs e2)
                {
                    // 回车发送（窗口级兜底：输入框失焦也能发）
                    if (e2.Key == Key.Enter || e2.Key == Key.Return)
                    {
                        SendChat();
                        e2.Handled = true;
                        return;
                    }
                    if (e2.Key == Key.O && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
                    {
                        ShowAiSettings();
                        e2.Handled = true;
                    }
                    else if (e2.Key == Key.Escape)
                    {
                        win.Close();
                        e2.Handled = true;
                    }
                };
                win.Loaded += delegate { try { _chatInput.Focus(); } catch { } };

                _chatWin = win;
                UpdateChatHint();
                for (int i = 0; i < _chatHistory.Count; i++)
                    AddChatBubble(_chatHistory[i][1], _chatHistory[i][0] == "user", false);
                if (_chatHistory.Count == 0)
                {
                    if (IsAiConfigured()) AddChatBubble("你好呀，我是豆仔～想聊点什么？", false, false);
                    else AddChatBubble("你好呀，我是豆仔～先点右上角「设置」填一下模型接口（OpenAI 兼容），我们就能聊天啦。Ctrl+O 也能打开设置。", false, true);
                }
                win.Show();
            }
            catch (Exception ex) { Log.Write("chat window: " + ex.Message); }
        }

        // ================= 外部控制 API =================

        public void ApiJump()
        {
            Jump();
        }

        public void ApiIdle()
        {
            EnterIdle();
        }

        public void ApiWalk(double dir, double seconds)
        {
            _state = State.Walk;
            _dir = dir >= 0 ? 1.0 : -1.0;
            _stateTimer = Math.Max(0.5, seconds);
            AnimateYaw(_dir > 0 ? WalkYawDeg : -WalkYawDeg);
        }

        public string ApiStatus()
        {
            string stateName;
            switch (_state)
            {
                case State.Walk: stateName = "walk"; break;
                case State.Doze: stateName = "doze"; break;
                case State.Happy: stateName = "happy"; break;
                case State.Shy: stateName = "shy"; break;
                case State.Curious: stateName = "curious"; break;
                case State.Sleepy: stateName = "sleepy"; break;
                default: stateName = "idle"; break;
            }
            return "{\"state\":\"" + stateName +
                   "\",\"x\":" + (int)Left +
                   ",\"y\":" + (int)Top +
                   ",\"port\":" + _server.Port +
                   ",\"visible\":" + (Visibility == Visibility.Visible ? "true" : "false") +
                   ",\"mood\":\"" + _mood + "\"" +
                   ",\"pose\":" + (_poseHold > 0 ? "true" : "false") +
                   ",\"floor\":" + _floorParallax.ToString("F3") +
                   ",\"bg\":" + _bgLum.ToString("F2") + "}";
        }

        // ================= P0-6 情绪状态机 =================

        // mood: happy|shy|curious|sleepy|doze|neutral，非法返回 false
        public bool SetMood(string mood)
        {
            switch ((mood ?? "").ToLowerInvariant())
            {
                case "neutral":
                    ApplyMood("neutral", 1.0, 1.0, 2.0, 0.04, 0);
                    if (_state == State.Doze) _state = State.Idle;
                    break;
                case "happy":
                    ApplyMood("happy", 1.0, 1.5, 3.0, 0.06, 0);
                    if (_state == State.Doze) _state = State.Idle;
                    break;
                case "shy":
                    ApplyMood("shy", 0.85, 2.0, 1.6, 0.03, 0);
                    if (_state == State.Doze) _state = State.Idle;
                    break;
                case "curious":
                    ApplyMood("curious", 1.15, 1.0, 2.2, 0.04, 12);
                    if (_state == State.Doze) _state = State.Idle;
                    break;
                case "sleepy":
                    ApplyMood("sleepy", 0.5, 1.0, 1.2, 0.03, 0);
                    if (_state == State.Doze) _state = State.Idle;
                    break;
                case "doze":
                    ApplyMood("doze", 0.35, 1.0, 0.8, 0.02, 0);
                    _state = State.Doze;
                    _zzzTimer = 7.0;              // 先让"豆仔困啦"挂一会儿，之后才轮到 Zzz
                    ShowBubble("豆仔困啦", false);   // 睡着不动嘴
                    break;
                default:
                    return false;
            }
            return true;
        }

        void ApplyMood(string mood, double eyes, double blush, double freq, double amp, double tilt)
        {
            _mood = mood;
            _moodEyes = eyes;
            _moodBlush = blush;
            _moodFloatFreq = freq;
            _moodFloatAmp = amp;
            SetBlush(blush);
            if (_state != State.Walk)
            {
                if (tilt > 0)
                    AnimateYaw(tilt);
                else if (_mood != "doze" || tilt == 0)
                    AnimateYaw(0);
            }
            Log.Write("mood=" + mood + " eyes=" + eyes + " blush=" + blush);
        }

        // ================= POSE 通道实现 =================

        // 外部传进来的一次姿态指令：NaN 表示该项不接管
        public sealed class PoseCmd
        {
            public double Eye = double.NaN;
            public double Blush = double.NaN;
            public double Yaw = double.NaN;
            public double GazeX = double.NaN;
            public double GazeY = double.NaN;
            public double BreathAmp = double.NaN;
            public double BreathFreq = double.NaN;
            public double Hold = 6.0;    // 接管时长（秒），到点自动归还控制权
            public bool Clear;           // true = 立刻放开全部接管
        }

        static double PoseClamp(double v, double lo, double hi)
        {
            if (double.IsNaN(v)) return double.NaN;
            return v < lo ? lo : (v > hi ? hi : v);
        }

        // 所有 Api* 都要求 UI 线程；外部一律用 BeginInvoke 投过来
        public void ApiPose(PoseCmd p)
        {
            try
            {
                if (p == null) return;
                if (p.Clear)
                {
                    ReleasePose("clear");
                    return;
                }
                _poseEye = PoseClamp(p.Eye, 0.05, 1.4);
                _poseBlush = PoseClamp(p.Blush, 0.0, 2.5);
                _poseYaw = PoseClamp(p.Yaw, -PoseYawDeg, PoseYawDeg);   // 超过这个角五官会贴到球轮廓上
                _poseGazeX = PoseClamp(p.GazeX, -1.0, 1.0);
                _poseGazeY = PoseClamp(p.GazeY, -1.0, 1.0);
                _poseBreathAmp = PoseClamp(p.BreathAmp, 0.0, 0.20);
                _poseBreathFreq = PoseClamp(p.BreathFreq, 0.2, 6.0);
                _poseHold = p.Hold <= 0 ? 6.0 : Math.Min(p.Hold, 60.0);
                if (_state == State.Walk) EnterIdle();   // 接管期间不许再走来走去
                Log.Write("pose eye=" + _poseEye.ToString("F2") + " blush=" + _poseBlush.ToString("F2") +
                          " yaw=" + _poseYaw.ToString("F0") + " gaze=" + _poseGazeX.ToString("F2") + "," +
                          _poseGazeY.ToString("F2") + " hold=" + _poseHold.ToString("F1"));
            }
            catch (Exception ex) { Log.Write("api pose: " + ex.Message); }
        }

        void ReleasePose(string why)
        {
            _poseEye = _poseBlush = _poseYaw = _poseGazeX = _poseGazeY = double.NaN;
            _poseBreathAmp = _poseBreathFreq = double.NaN;
            _poseHold = 0;
            if (_blushScales != null) SetBlush(_moodBlush);
            // 朝向必须显式收回：否则松手后她会一直歪着头（原逻辑只在状态切换时才重置 yaw）
            if (_state == State.Walk) AnimateYaw(_dir > 0 ? WalkYawDeg : -WalkYawDeg);
            else AnimateYaw(0);
            Log.Write("pose release (" + why + ")");
        }

        // 每帧调用：先衰减接管计时，再把目标值往当前值拉（缓动，绝不瞬跳）
        void TickPose(double dt)
        {
            int now = Environment.TickCount;
            double wdt = _lastPoseTick == 0 ? dt : unchecked(now - _lastPoseTick) * 0.001;   // 墙钟增量
            _lastPoseTick = now;
            if (wdt < 0 || wdt > 1.0) wdt = dt;                                      // 卡顿/休眠时兜底
            if (_poseHold > 0)
            {
                _poseHold -= wdt;
                if (_poseHold <= 0) { ReleasePose("timeout"); return; }
            }
            if (!double.IsNaN(_poseEye)) _curPoseEye += (_poseEye - _curPoseEye) * 0.18;
            else _curPoseEye = _moodEyes;

            if (!double.IsNaN(_poseBlush))
            {
                _curPoseBlush += (_poseBlush - _curPoseBlush) * 0.18;
                SetBlush(_curPoseBlush);
            }
            else _curPoseBlush = _moodBlush;

            if (!double.IsNaN(_poseYaw))
            {
                double delta = _poseYaw - _curPoseYaw;
                delta = ((delta + 180.0) % 360.0 + 360.0) % 360.0 - 180.0;   // 走最短路径
                _curPoseYaw += delta * 0.18;
                var rot = (AxisAngleRotation3D)_yaw.Rotation;
                rot.BeginAnimation(AxisAngleRotation3D.AngleProperty, null);  // 掐掉正在跑的 AnimateYaw
                rot.Angle = _curPoseYaw;
            }
            else _curPoseYaw = ((AxisAngleRotation3D)_yaw.Rotation).Angle;

            if (!double.IsNaN(_poseBreathAmp)) _curPoseBreathAmp += (_poseBreathAmp - _curPoseBreathAmp) * 0.12;
            else _curPoseBreathAmp = _moodFloatAmp;
            if (!double.IsNaN(_poseBreathFreq)) _curPoseBreathFreq += (_poseBreathFreq - _curPoseBreathFreq) * 0.12;
            else _curPoseBreathFreq = _moodFloatFreq;
        }

        void SetBlush(double factor)
        {
            if (_blushScales == null) return;
            foreach (var s in _blushScales)
            {
                s.ScaleX = factor;
                s.ScaleY = factor;
                s.ScaleZ = factor;
            }
        }

        // P0-6 任何交互/调用唤醒（Doze → Idle）
        public void Wake()
        {
            _lastInteract = _clock;
            if (_state == State.Doze)
            {
                _state = State.Idle;
                SetMood("neutral");
                Log.Write("wake up");
                ShowBubble("豆仔醒啦");
            }
        }

        // ---- 说话气泡 ----
        public void ApiChat(string text)
        {
            ShowBubble(text);
        }

        // speak=false：只出气泡不动嘴（睡眠时的 Zzz 用这条，睡着不该吧唧嘴）
        void ShowBubble(string text, bool speak = true) { ShowBubble(text, speak, 3.0); }

        void ShowBubble(string text, bool speak, double seconds)
        {
            _bubbleText.Text = string.IsNullOrEmpty(text) ? "..." : text;
            _bubble.Visibility = Visibility.Visible;
            _bubble.Opacity = 1;
            _bubbleTimer.Stop();
            _bubbleTimer.Interval = TimeSpan.FromSeconds(Math.Max(1.0, seconds));
            _bubbleTimer.Start();
            if (speak) StartTalking();    // P0-5 气泡期间嘴张合
        }

        // ---- 动作枚举：idle / bounce / shake / turn / blush ----
        public bool ApiAction(string action)
        {
            switch ((action ?? "").ToLowerInvariant())
            {
                case "idle": EnterIdle(); return true;
                case "bounce":
                case "jump": Jump(); return true;
                case "shake": Shake(); return true;
                case "turn": Turn(); return true;
                case "blush": Blush(); return true;
                default: return false;
            }
        }

        // 摇头晃脑
        void Shake()
        {
            EnterIdle();
            var kf = new DoubleAnimationUsingKeyFrames();
            double[] angles = { 0, 28, -28, 18, -18, 0 };
            for (int i = 0; i < angles.Length; i++)
            {
                kf.KeyFrames.Add(new LinearDoubleKeyFrame(
                    angles[i], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(i * 140))));
            }
            _yaw.Rotation.BeginAnimation(AxisAngleRotation3D.AngleProperty, kf);
        }

        // 转一圈（FIX-TURN P0-1）：从当前基准角出发转 360°，FillBehavior.Stop 不把动画值钉在 360——
        // 旧实现 HoldEnd 钉 360 后，单击 AnimateYaw(0) 会取 360 当起点反向转一整圈。
        // 900ms / EaseInOut 保持不变
        void Turn()
        {
            EnterIdle();
            double start = ((AxisAngleRotation3D)_yaw.Rotation).Angle % 360.0;      // 基准角（正常为 0）
            var a = new DoubleAnimation(start, start + 360, TimeSpan.FromMilliseconds(900))
                    { FillBehavior = FillBehavior.Stop };     // 关键：转完不保留 360
            a.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
            a.Completed += delegate
            {
                try
                {
                    _yaw.Rotation.BeginAnimation(AxisAngleRotation3D.AngleProperty, null);
                    ((AxisAngleRotation3D)_yaw.Rotation).Angle = start;   // 360 ≡ start，视觉零跳变
                    Log.Write("turn done, yaw reset to " + start);
                }
                catch (Exception ex) { Log.Write("turn done: " + ex.Message); }
            };
            _yaw.Rotation.BeginAnimation(AxisAngleRotation3D.AngleProperty, a);
            _turnUntil = Environment.TickCount + 950.0;        // 转圈收尾前点击不抢朝向（P0-3，墙钟毫秒；
                                                               // .NET Framework 无 TickCount64，int 毫秒 24.9 天回绕对宠物进程无实际影响）
            Log.Write("turn start deg=" + Math.Round(start, 1));
        }

        // 害羞（腮红放大收缩）
        void Blush()
        {
            EnterIdle();
            foreach (var s in _blushScales)
            {
                Pulse(s, 1.9, 900);
            }
        }

        static void Pulse(ScaleTransform3D s, double peak, double ms)
        {
            var props = new[]
            {
                ScaleTransform3D.ScaleXProperty,
                ScaleTransform3D.ScaleYProperty,
                ScaleTransform3D.ScaleZProperty
            };
            foreach (var prop in props)
            {
                // 基准值先归 1：动画结束后移除动画，属性回到可自由设置的状态
                s.BeginAnimation(prop, null);
                if (prop == ScaleTransform3D.ScaleXProperty) s.ScaleX = 1;
                else if (prop == ScaleTransform3D.ScaleYProperty) s.ScaleY = 1;
                else s.ScaleZ = 1;

                var kf = new DoubleAnimationUsingKeyFrames();
                kf.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                var up = new EasingDoubleKeyFrame(peak,
                    KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms * 0.4)));
                up.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut };
                kf.KeyFrames.Add(up);
                var down = new EasingDoubleKeyFrame(1,
                    KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)));
                down.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
                kf.KeyFrames.Add(down);
                var capturedProp = prop;
                kf.Completed += delegate { s.BeginAnimation(capturedProp, null); };
                s.BeginAnimation(prop, kf);
            }
        }

        // ---- 显隐 ----
        public void ApiSetVisible(bool show)
        {
            Visibility = show ? Visibility.Visible : Visibility.Hidden;
            try
            {
                if (_orbWindow != null)
                    _orbWindow.Visibility = show && _orbEnabled ? Visibility.Visible : Visibility.Hidden;
            }
            catch (Exception ex) { Log.Write("orb visible: " + ex.Message); }
        }

        // ================= P0-7 多显示器工作区 =================

        // 宠物中心点所在屏幕的工作区（换算为 DIP 坐标）
        // ================= FIX-ORB 悬浮魔法球 =================

        // 独立一个置顶透明小窗：光球可以飘到桌面上任何地方，豆仔的窗口再去追
        void BuildOrb()
        {
            try
            {
                var g = new Grid();

                _orbGlow = new System.Windows.Shapes.Ellipse { Width = 112, Height = 112 };
                var glowBrush = new RadialGradientBrush();
                glowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0xAA, 0x8C, 0xE0, 0xFF), 0.00));
                glowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x5A, 0x5E, 0xC4, 0xFF), 0.45));
                glowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x33, 0x88, 0xDD), 1.00));
                _orbGlow.Fill = glowBrush;
                _orbGlow.HorizontalAlignment = HorizontalAlignment.Center;
                _orbGlow.VerticalAlignment = VerticalAlignment.Center;

                _orbCore = new System.Windows.Shapes.Ellipse { Width = 32, Height = 32 };
                var coreBrush = new RadialGradientBrush();
                coreBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 0.00));
                coreBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0xF2, 0xC8, 0xF4, 0xFF), 0.42));
                coreBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x70, 0x7E, 0xD8, 0xFF), 0.82));
                coreBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x4E, 0xA8, 0xFF), 1.00));
                _orbCore.Fill = coreBrush;
                _orbCore.HorizontalAlignment = HorizontalAlignment.Center;
                _orbCore.VerticalAlignment = VerticalAlignment.Center;

                g.Children.Add(_orbGlow);
                g.Children.Add(_orbCore);

                // 扑空时喷一圈小星点：8 颗，绕中心 42px，整体缩放 + 淡出
                _orbBurst = new Grid { Width = 120, Height = 120, Opacity = 0.0, IsHitTestVisible = false };
                _orbBurst.HorizontalAlignment = HorizontalAlignment.Center;
                _orbBurst.VerticalAlignment = VerticalAlignment.Center;
                _orbBurstScale = new ScaleTransform(1, 1);
                _orbBurst.RenderTransformOrigin = new Point(0.5, 0.5);
                _orbBurst.RenderTransform = _orbBurstScale;
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI / 4.0;
                    var dot = new System.Windows.Shapes.Ellipse { Width = 6, Height = 6 };
                    dot.Fill = new SolidColorBrush(Color.FromArgb(0xE6, 0xDA, 0xF6, 0xFF));
                    dot.HorizontalAlignment = HorizontalAlignment.Left;
                    dot.VerticalAlignment = VerticalAlignment.Top;
                    dot.Margin = new Thickness(60 + Math.Cos(a) * 42 - 3, 60 + Math.Sin(a) * 42 - 3, 0, 0);
                    _orbBurst.Children.Add(dot);
                }
                g.Children.Add(_orbBurst);

                var w = new Window
                {
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Background = Brushes.Transparent,
                    Topmost = true,
                    ShowInTaskbar = false,
                    ResizeMode = ResizeMode.NoResize,
                    Width = 120,
                    Height = 120,
                    Focusable = false,
                    IsHitTestVisible = false,   // 鼠标穿透，不挡桌面操作
                    Title = "豆仔·魔法球"
                };
                w.Content = g;
                _orbWindow = w;
                // 注意：构造期本窗口还没 Show，设 Owner 会抛"无法将 Owner 属性设置为之前未显示的 Window"，
                // 所以不用 Owner，改成在主窗口关闭时手动关掉球
                w.Show();

                // 鼠标穿透 + 不进 Alt-Tab：WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
                try
                {
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                    int ex = GetWindowLong(hwnd, -20);
                    SetWindowLong(hwnd, -20, ex | 0x20 | 0x80 | 0x08000000);
                }
                catch (Exception ex2) { Log.Write("orb clickthrough: " + ex2.Message); }

                _orbX = Left + Width * 0.5 + 150;
                _orbY = Top + 60;
                _orbTX = _orbX;
                _orbTY = _orbY;
                _orbTheta = Math.PI * 1.4;                        // 从侧后方起转，别一开机就绕到脸前
                _playT = 10.0 + _rand.NextDouble() * 8.0;          // 第一次扑球延后，开机别立刻扑
                ApplyOrbPos();

                // 认主：Owner 只能在两个窗口都显示之后再设（否则抛"之前未显示的 Window"）。
                // 设了之后球永远在豆仔之上——绕到后脑勺时不会被球挡住消失，看起来才像"绕圈"
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    try { _orbWindow.Owner = this; }
                    catch (Exception ex) { Log.Write("orb owner: " + ex.Message); }
                }));
                Log.Write("orb on");
            }
            catch (Exception ex) { Log.Write("orb init: " + ex.Message); }
        }

        void ApplyOrbPos()
        {
            if (_orbWindow == null) return;
            _orbWindow.Left = _orbX - 60;
            _orbWindow.Top = _orbY + _orbDY - 60;
        }

        // 魔法球 = 跟着豆仔的小伙伴：
        //   · 豆仔走路时 → 落在它身后贴地一跳一跳地跟（像大人带小孩）
        //   · 豆仔待机时 → 沿着椭圆轨道在它身前身后绕圈
        //   · 偶尔球绕到脸前，豆仔扑一下扑空 → 喷一圈星点 + 开心两秒
        void TickOrb(double dt)
        {
            if (_orbWindow == null) return;

            if (!_orbEnabled || Visibility != Visibility.Visible)
            {
                if (_orbWindow.Visibility != Visibility.Hidden) _orbWindow.Visibility = Visibility.Hidden;
                return;
            }
            if (_orbWindow.Visibility != Visibility.Visible) _orbWindow.Visibility = Visibility.Visible;

            var area = WorkArea();
            double cx = Left + Width * 0.5;
            double cy = Top + Height * 0.42;
            double groundY = Top + Height * 0.685;      // 球底那一行，球贴在这个高度跳

            if (double.IsNaN(_orbX))
            {
                _orbX = cx - 120; _orbY = groundY; _orbTX = _orbX; _orbTY = _orbY;
            }

            _orbBob += dt;
            if (_playT > 0) _playT -= dt;

            if (_state == State.Walk)
            {
                // 跟在后下方，贴地一跳一跳
                _orbTX = cx - _dir * 105;
                _orbTY = groundY;
                _orbDY = -Math.Abs(Math.Sin(_orbBob * 3.4)) * 17.0;
            }
            else
            {
                // 身前身后绕圈（椭圆：横向 128、纵向 52，慢速 0.62rad/s ≈ 10 秒一圈）
                _orbTheta += dt * 0.52;
                _orbTX = cx + Math.Cos(_orbTheta) * 128;
                _orbTY = cy + Math.Sin(_orbTheta) * 52;
                _orbDY = Math.Sin(_orbBob * 1.7) * 6.0;

                // 偶尔绕到脸前时扑一下：14~24 秒一次，别太频繁
                if (_playT <= 0 && _state == State.Idle && !_paused && Math.Cos(_orbTheta) > 0.65)
                {
                    _playT = 14.0 + _rand.NextDouble() * 10.0;
                    _orbTX += 55;            // 球顺轨道再往前滑一点，扑空感更强
                    OrbBurst();
                    PlayPounce();
                    Log.Write("orb play");
                }
            }

            _orbTX = Math.Max(area.Left + 55, Math.Min(area.Right - 55, _orbTX));
            _orbTY = Math.Max(area.Top + 55, Math.Min(area.Bottom - 55, _orbTY));

            double k = Math.Min(1.0, dt * (_state == State.Walk ? 5.0 : 6.0));
            _orbX += (_orbTX - _orbX) * k;
            _orbY += (_orbTY - _orbY) * k;
            if (_orbGlow != null) _orbGlow.Opacity = 0.72 + 0.28 * Math.Sin(_orbBob * 2.3);
            ApplyOrbPos();
        }

        // 喷一圈小星点：整体放大 0.35→1.5，同时淡出
        void OrbBurst()
        {
            try
            {
                if (_orbBurst == null) return;
                _orbBurst.BeginAnimation(OpacityProperty, null);
                _orbBurstScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _orbBurstScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                _orbBurst.Opacity = 1.0;
                _orbBurstScale.ScaleX = 0.35;
                _orbBurstScale.ScaleY = 0.35;
                var fa = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(560));
                var sa = new DoubleAnimation(0.35, 1.55, TimeSpan.FromMilliseconds(560));
                _orbBurst.BeginAnimation(OpacityProperty, fa);
                _orbBurstScale.BeginAnimation(ScaleTransform.ScaleXProperty, sa);
                _orbBurstScale.BeginAnimation(ScaleTransform.ScaleYProperty, sa);
            }
            catch (Exception ex) { Log.Write("orb burst: " + ex.Message); }
        }

        // 扑空：跳一下 + 开心两秒半（复用现有 jump 动画和情绪系统）
        void PlayPounce()
        {
            try
            {
                if (!_orbEnabled) return;
                ApiAction("jump");
                SetMood("happy");
                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2600) };
                t.Tick += delegate
                {
                    try
                    {
                        t.Stop();
                        if (_mood == "happy") SetMood("neutral");
                    }
                    catch (Exception ex) { Log.Write("orb mood back: " + ex.Message); }
                };
                t.Start();
            }
            catch (Exception ex) { Log.Write("orb pounce: " + ex.Message); }
        }

        // 宠物中心点所在屏幕的工作区（换算为 DIP 坐标）
        Rect WorkArea()
        {
            try
            {
                var src = PresentationSource.FromVisual(this);
                if (src == null)
                {
                    var wa0 = SystemParameters.WorkArea;
                    return new Rect(wa0.Left, wa0.Top, wa0.Width, wa0.Height);
                }
                var toDev = src.CompositionTarget.TransformToDevice;
                var fromDev = src.CompositionTarget.TransformFromDevice;
                var center = toDev.Transform(new Point(Left + Width * 0.5, Top + Height * 0.5));
                var screen = WF.Screen.FromPoint(new SD.Point((int)center.X, (int)center.Y));
                var tl = fromDev.Transform(new Point(screen.WorkingArea.Left, screen.WorkingArea.Top));
                var br = fromDev.Transform(new Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom));
                return new Rect(tl, br);
            }
            catch
            {
                var wa = SystemParameters.WorkArea;
                return new Rect(wa.Left, wa.Top, wa.Width, wa.Height);
            }
        }

        // 贴边吸附：不允许半只球悬在屏幕外；显示器切换/拔插后自动纠正
        void ClampToWorkArea()
        {
            try
            {
                var wa = WorkArea();
                double x = Math.Max(wa.Left, Math.Min(wa.Right - Width, Left));
                double y = Math.Max(wa.Top, Math.Min(wa.Bottom - Height, Top));
                if (x != Left) Left = x;
                if (y != Top) Top = y;
            }
            catch (Exception ex) { Log.Write("clamp: " + ex.Message); }
        }

        // ---- 平滑移动到屏幕坐标 ----
        public void ApiMoveTo(double x, double y)
        {
            var area = WorkArea();
            x = Math.Max(area.Left, Math.Min(area.Right - Width, x));
            y = Math.Max(area.Top, Math.Min(area.Bottom - Height, y));

            // 执行前强制切 idle 并暂停闲逛，完成后 2 秒"钉住"再恢复
            if (_state == State.Walk)
                EnterIdle();
            _pinTimer = 2.0;

            var ax = new DoubleAnimation(x, TimeSpan.FromMilliseconds(600));
            ax.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
            var ay = new DoubleAnimation(y, TimeSpan.FromMilliseconds(600));
            ay.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut };

            // 关键：动画结束后立即移除动画（FillBehavior.HoldEnd 会一直"持有"
            // Left/Top 属性，导致之后的拖动和行走赋值全部失效）
            double fx = x, fy = y;
            ax.Completed += delegate
            {
                BeginAnimation(Window.LeftProperty, null);
                BeginAnimation(Window.TopProperty, null);
                Left = fx;
                Top = fy;
            };
            BeginAnimation(Window.LeftProperty, ax);
            BeginAnimation(Window.TopProperty, ay);
        }

        // 暂停/恢复闲逛（手动 /walk 等显式指令不受影响）
        public void ApiPause(bool on)
        {
            _paused = on;
            Log.Write("pause=" + on);
            if (on && _state == State.Walk)
                EnterIdle();   // 立即停下
        }

        // ================= 场景搭建 =================

        // 3D 视口 + 说话气泡叠层
        UIElement BuildLayout()
        {
            var grid = new Grid();
            grid.Children.Add(BuildScene());

            // FIX-SHADOW2D：3D 影子盘受相机俯角 14° 和球体遮挡影响，浓的那圈全藏在球背后，
            // 投影出来只剩全透明的尾巴，等于没有影子。改成贴在视口底部的一层 2D 径向渐变软影，
            // 任何壁纸、任何跳跃高度都能稳定"踩在地上"，也不再有 3D 盘片的透视错位
            _shadow2D = new System.Windows.Shapes.Rectangle
            {
                Width = 210, Height = 64,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 67),   // 让渐变最浓的一行正好压在球底（实测球底在窗口内 221DIP 处）
                IsHitTestVisible = false,
                Opacity = 0.0
            };
            var shBrush = new RadialGradientBrush();
            shBrush.GradientOrigin = new Point(0.5, 0.5);
            shBrush.Center = new Point(0.5, 0.5);
            shBrush.RadiusX = 0.5;
            shBrush.RadiusY = 0.5;
            shBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0xCC, 0x0A, 0x12, 0x1C), 0.00));
            shBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x6A, 0x0A, 0x12, 0x1C), 0.50));
            shBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x14, 0x0A, 0x12, 0x1C), 0.80));
            shBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x0A, 0x12, 0x1C), 1.00));
            _shadow2D.Fill = shBrush;
            _shadow2DScale = new ScaleTransform(1, 1);
            _shadow2D.RenderTransformOrigin = new Point(0.5, 0.5);
            _shadow2D.RenderTransform = _shadow2DScale;
            grid.Children.Insert(0, _shadow2D);   // 插到最底层，保证在 3D 视口后面

            _bubble = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(235, 0xFF, 0xFF, 0xFF)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 6, 10, 6),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 34, 0, 0),   // 场景下移 26px 后，气泡同步下压，仍贴在球顶上方
                MaxWidth = 280,
                Visibility = Visibility.Collapsed
            };
            _bubbleText = new TextBlock
            {
                Foreground = Brushes.Black,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap
            };
            _bubble.Child = _bubbleText;
            grid.Children.Add(_bubble);

            _bubbleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _bubbleTimer.Tick += delegate
            {
                _bubbleTimer.Stop();
                _bubble.Visibility = Visibility.Collapsed;
                StopTalking();                     // P0-5 气泡消失后闭嘴
            };

            // P0-5 嘴张合节奏（约 120ms 一次开合）
            _mouthTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _mouthTimer.Tick += delegate
            {
                _mouthOpen = !_mouthOpen;
                if (_mouthScale != null)
                    _mouthScale.ScaleY = _mouthOpen ? 0.9 : 0.25;
            };

            return grid;
        }

        Viewport3D BuildScene()
        {
            var viewport = new Viewport3D();

            viewport.Camera = new PerspectiveCamera
            {
                Position = new Point3D(0, 1.8, 5.0),
                LookDirection = new Vector3D(0, -0.25, -1),
                FieldOfView = 45
            };

            var content = new Model3DGroup();
            // 整体下移：原来球顶距窗口顶只剩 59px，跳跃峰值 87px 会被窗口切掉，先让出 26px 空间
            content.Transform = new TranslateTransform3D(0, -0.267, 0);
            // 环境光 0x86/0x8E/0x9C：回到"淡蓝"基调，但比原 0x90 略低留一点体积
            content.Children.Add(new AmbientLight(Color.FromRgb(0x88, 0x8C, 0x94)));
            var sun = new DirectionalLight();
            sun.Color = Color.FromRgb(0xF4, 0xF7, 0xFA);        // 略压白，防顶部冲成纯白
            sun.Direction = new Vector3D(-0.42, -1.05, -1.45);   // 往正面偏：最亮点落在脸中央，顶部轮廓不吃到最亮
            content.Children.Add(sun);
            // 左下角一盏很弱的冷补光，只把暗部边缘托起来一点，不冲淡主光
            var fill = new DirectionalLight();
            fill.Color = Color.FromRgb(0x40, 0x52, 0x66);
            fill.Direction = new Vector3D(0.85, 0.55, -0.35);
            content.Children.Add(fill);

            // 地面软阴影：一整张 quad + 径向渐变（真渐隐，中心浓→边缘化到 0）
            // 原来三层同心硬边盘叠出来的假模糊，投影后能看出三环台阶；现在一张图搞定，也不再需要错开 y 防 Z-fighting
            _shadowGrad = new RadialGradientBrush();
            _shadowGrad.GradientOrigin = new Point(0.5, 0.5);
            _shadowGrad.Center = new Point(0.5, 0.5);
            _shadowGrad.RadiusX = 0.5;
            _shadowGrad.RadiusY = 0.5;
            double[] stopPos = { 0.00, 0.30, 0.55, 1.00 };
            for (int i = 0; i < stopPos.Length; i++)
                _shadowGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0x12, 0x1C, 0x2A), stopPos[i]));

            var sMesh = new MeshGeometry3D();
            double sr = 2.60;                                   // 球半径是 1.0，影子盘必须明显大于球，否则浓的部分全被球挡住只剩全透明尾巴
            double sy = -0.022;
            sMesh.Positions.Add(new Point3D(-sr, sy, sr));
            sMesh.Positions.Add(new Point3D(sr, sy, sr));
            sMesh.Positions.Add(new Point3D(sr, sy, -sr));
            sMesh.Positions.Add(new Point3D(-sr, sy, -sr));
            sMesh.TextureCoordinates.Add(new Point(0, 0));
            sMesh.TextureCoordinates.Add(new Point(1, 0));
            sMesh.TextureCoordinates.Add(new Point(1, 1));
            sMesh.TextureCoordinates.Add(new Point(0, 1));
            sMesh.TriangleIndices.Add(0); sMesh.TriangleIndices.Add(1); sMesh.TriangleIndices.Add(2);
            sMesh.TriangleIndices.Add(0); sMesh.TriangleIndices.Add(2); sMesh.TriangleIndices.Add(3);
            _shadowScale = new ScaleTransform3D();             // Center 默认原点，quad 已居中于原点
            var shModel = new GeometryModel3D(sMesh, new DiffuseMaterial(_shadowGrad));
            shModel.Transform = _shadowScale;
            // FIX-SHADOW2D：保留几何体但不再入场，影子交给 2D 软影渲染
            // content.Children.Add(shModel);
            RefreshShadow(0);

            // FIX-SHADOW2D：伪 3D 地面（光池）也撤掉——它在 3D 层叠在 2D 影子上，把影子洗白了；用户也反馈这块效果不好
            // content.Children.Add(BuildFloor());
            content.Children.Add(BuildPet());

            var visual = new ModelVisual3D { Content = content };
            viewport.Children.Add(visual);
            return viewport;
        }

        // ============ 伪 3D 地面 ============
        // 球的脚底是 y=0（球体半径 1、整体上移 1），影子在 y≈-0.02。
        // 地面压到 -0.035，影子才浮在地面上而不是被吞掉。
        // 纵深靠两件事：透视收束（近大远小）+ 远处实心化到全透明，看不到边就不像一块板子。
        double _prevLeftParallax = double.NaN;
        Model3DGroup BuildFloor()
        {
            var group = new Model3DGroup();
            const double yF = -0.035;

            // 光池（替代整片地面）：只在她脚下铺一小块椭圆，四周全化掉
            // 之前试过铺满 28 单位的整片地面，结果是窗口边缘被切出一道硬边、整块变灰，立刻露馅像"桌上的板子"
            var mesh = new MeshGeometry3D();
            mesh.Positions.Add(new Point3D(-2.2, yF, 0.55));
            mesh.Positions.Add(new Point3D(2.2, yF, 0.55));
            mesh.Positions.Add(new Point3D(2.2, yF, -2.2));
            mesh.Positions.Add(new Point3D(-2.2, yF, -2.2));
            mesh.TextureCoordinates.Add(new Point(0, 0));
            mesh.TextureCoordinates.Add(new Point(1, 0));
            mesh.TextureCoordinates.Add(new Point(1, 1));
            mesh.TextureCoordinates.Add(new Point(0, 1));
            mesh.TriangleIndices.Add(0); mesh.TriangleIndices.Add(1); mesh.TriangleIndices.Add(2);
            mesh.TriangleIndices.Add(0); mesh.TriangleIndices.Add(2); mesh.TriangleIndices.Add(3);

            var grad = new RadialGradientBrush();
            grad.GradientOrigin = new Point(0.5, 0.42);
            grad.Center = new Point(0.5, 0.42);
            grad.RadiusX = 0.50;
            grad.RadiusY = 0.55;
            grad.GradientStops.Add(new GradientStop(Color.FromArgb(0x34, 0x10, 0x1C, 0x30), 0.00));
            grad.GradientStops.Add(new GradientStop(Color.FromArgb(0x16, 0x10, 0x1C, 0x30), 0.38));
            grad.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x10, 0x1C, 0x30), 0.80));
            _floorPoolGrad = grad;
            group.Children.Add(new GeometryModel3D(mesh, new DiffuseMaterial(grad)));

            // 透视网格已移除：窗口是全透明的，这些细线会直接横穿后面的其它窗口内容，
            // 看起来像屏幕划痕而不是地面；地面的"站住感"改由贴地的接触阴影承担。

            // 镜头滞后：窗口移动时地面反向轻移，模拟相机比主体慢半拍
            _floorShift = new TranslateTransform3D();
            group.Transform = _floorShift;
            return group;
        }

        TranslateTransform3D _floorShift;
        double _floorParallax;

        // 镜头滞后：窗口横移时地面先反向甩一点，随后慢慢回正——这是"相机比主体慢半拍"的错觉
        void TickParallax()
        {
            if (_floorShift == null) return;
            double dx = double.IsNaN(_prevLeftParallax) ? 0 : Left - _prevLeftParallax;
            _prevLeftParallax = Left;
            if (dx > 60 || dx < -60) dx = 0;          // 拖动瞬移/切屏时别把地面甩出去
            double target = -dx * 0.034;              // 实测：0.006→2px、0.022→5.4px（都太轻），0.034 约 8px
            if (target > 0.55) target = 0.55;
            if (target < -0.55) target = -0.55;
            _floorParallax += (target - _floorParallax) * 0.12;
            _floorShift.OffsetX = _floorParallax;
        }

        // 影子浓淡 = 基浓度 × 离地衰减 × 背景明暗；浅桌面投冷调暗影，深桌面改投淡光晕（纯黑影子在黑底上等于没有）
        void RefreshShadow(double h)
        {
            if (_shadowGrad == null) return;
            double heightK = Math.Max(0.0, 1.0 - 0.6 * h);
            double litK = Math.Min(1.0, Math.Max(0.0, (_bgLum - 0.35) / 0.40));   // 0=深底 1=浅底
            double opacity = (0.95 - 0.42 * litK) * heightK;                       // 浅底阴影重、深底轻（深底留深色影子本来就是隐形，宁可淡也别发白）
            for (int i = 0; i < _shadowGrad.GradientStops.Count && i < _shadowStopA.Length; i++)
            {
                byte a = (byte)(_shadowStopA[i] * opacity);
                byte r = 0x0E;   // 影子永远是"偏冷的深灰蓝"，不再跟背景明暗翻成亮蓝白
                byte g = 0x16;
                byte b = 0x22;
                _shadowGrad.GradientStops[i].Color = Color.FromArgb(a, r, g, b);
            }

            // 地面跟着一起换：浅底用深灰线，深底用浅灰线，否则深色壁纸上网格和光池等于隐形，"站不住"
            for (int i = 0; i < _floorLineBrushes.Count; i++)
            {
                var old = _floorLineBrushes[i].Color;
                _floorLineBrushes[i].Color = Color.FromArgb(old.A,
                    (byte)(0xE2 + (0x18 - 0xE2) * litK),
                    (byte)(0xEA + (0x2A - 0xEA) * litK),
                    (byte)(0xF6 + (0x40 - 0xF6) * litK));
            }
            if (_floorPoolGrad != null)
            {
                for (int i = 0; i < _floorPoolGrad.GradientStops.Count; i++)
                {
                    var old = _floorPoolGrad.GradientStops[i].Color;
                    _floorPoolGrad.GradientStops[i].Color = Color.FromArgb(old.A,
                        (byte)(0xE6 + (0x10 - 0xE6) * litK),
                        (byte)(0xEE + (0x1C - 0xEE) * litK),
                        (byte)(0xFF + (0x30 - 0xFF) * litK));
                }
            }

            // 2D 软影：跳得越高摊得越大、越淡；浅桌面加浓（深桌面加深反正也看不见）
            if (_shadow2D != null && _shadow2DScale != null)
            {
                _shadow2DScale.ScaleX = 1.0 + 0.22 * h;
                _shadow2DScale.ScaleY = 1.0 + 0.10 * h;
                _shadow2D.Opacity = (0.80 + 0.20 * litK) * heightK;
            }
        }

        // 每秒抄一次脚下的桌面颜色：抄点特意放在影子外侧，否则会把影子本身抄进去，越抄越黑
        void TickShadowBg(double dt)
        {
            _bgSampleT -= dt;
            if (_bgSampleT > 0) return;
            _bgSampleT = 1.2;
            try
            {
                var sp = PointToScreen(new Point(Width * 0.18, Height * 0.90));
                int px = (int)Math.Round(sp.X), py = (int)Math.Round(sp.Y);
                double sum = 0; int n = 0;
                using (var bmp = new System.Drawing.Bitmap(7, 7))
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(px - 3, py - 3, 0, 0, new System.Drawing.Size(7, 7));
                    for (int yy = 0; yy < 7; yy++)
                        for (int xx = 0; xx < 7; xx++)
                        {
                            var c = bmp.GetPixel(xx, yy);
                            sum += (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
                            n++;
                        }
                }
                if (n > 0)
                {
                    _bgLum += (sum / n - _bgLum) * 0.5;      // 平滑，背景有动画时影子不跟着抖；0.5 约 3 秒收敛
                    RefreshShadow(_shadowAppliedH > 0 ? _shadowAppliedH : 0);
                }
            }
            catch { }
        }

        // 一条贴在地面上的细长四边形（width 是它的横向粗细，world 单位）
        void AddLine(Model3DGroup parent, Point3D a, Point3D b, double width, byte alpha)
        {
            var dx = b.X - a.X; var dz = b.Z - a.Z;
            double len = Math.Sqrt(dx * dx + dz * dz);
            if (len < 1e-6) return;
            double nx = -dz / len * width * 0.5, nz = dx / len * width * 0.5;
            double yy = a.Y + 0.0015;                 // 统一抬一点，防与地面 Z-fighting
            var m = new MeshGeometry3D();
            m.Positions.Add(new Point3D(a.X - nx, yy, a.Z - nz));
            m.Positions.Add(new Point3D(a.X + nx, yy, a.Z + nz));
            m.Positions.Add(new Point3D(b.X + nx, yy, b.Z + nz));
            m.Positions.Add(new Point3D(b.X - nx, yy, b.Z - nz));
            m.TriangleIndices.Add(0); m.TriangleIndices.Add(1); m.TriangleIndices.Add(2);
            m.TriangleIndices.Add(0); m.TriangleIndices.Add(2); m.TriangleIndices.Add(3);
            var brush = new SolidColorBrush(Color.FromArgb(alpha, 0x18, 0x2A, 0x40));
            _floorLineBrushes.Add(brush);
            parent.Children.Add(new GeometryModel3D(m, new DiffuseMaterial(brush)));
        }

        Model3DGroup BuildPet()
        {
            // 球体：漫反射 + 一层柔和高光（原来纯 diffuse 没有亮斑，像哑光塑料片）
            var bodyDiffuse = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0x8A, 0xD0, 0xFF)));
            var bodyMat = new MaterialGroup();
            bodyMat.Children.Add(bodyDiffuse);
            bodyMat.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), 90.0));
            var darkMat = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0x25, 0x2B, 0x38)));
            var whiteMat = new DiffuseMaterial(new SolidColorBrush(Colors.White));

            var pet = new Model3DGroup();

            // 身体（果冻史莱姆造型）；网格引用留存用于下半部逐顶点果冻形变
            _bodyMesh = Sphere(1.0, 24, 32);
            _bodyBase = new Point3D[_bodyMesh.Positions.Count];
            _bodyMesh.Positions.CopyTo(_bodyBase, 0);
            var body = new GeometryModel3D(_bodyMesh, bodyMat);
            body.Transform = new TranslateTransform3D(0, 1.0, 0);
            pet.Children.Add(body);

            // 五官容器：触地下压时整组变形（下压位移 + 轻微压扁）
            var face = new Model3DGroup();

            // 双眼（可缩放实现眨眼）+ 眼睛高光；双眼共享 _pupilOffset 实现瞳孔跟随
            double[] eyeX = { -0.32, 0.32 };
            _pupilOffset = new TranslateTransform3D();
            for (int i = 0; i < 2; i++)
            {
                var scale = new ScaleTransform3D
                {
                    CenterX = eyeX[i],
                    CenterY = 1.45,
                    CenterZ = 0.86
                };
                _eyeScales = Append(_eyeScales, scale);

                var eye = new GeometryModel3D(Sphere(0.13, 10, 14), darkMat);
                var eyeGroup = new Transform3DGroup();
                eyeGroup.Children.Add(new TranslateTransform3D(eyeX[i], 1.45, 0.86));
                eyeGroup.Children.Add(_pupilOffset);
                eyeGroup.Children.Add(scale);
                eye.Transform = eyeGroup;
                face.Children.Add(eye);

                var shine = new GeometryModel3D(Sphere(0.034, 8, 10), whiteMat);
                // 高光跟随瞳孔：定位 → 共享 _pupilOffset → 共享 scale（链尾，
                // 眨眼时以眼心为中心压扁，高光随瞳孔一起被压扁贴到眼皮）
                // 高光半径/位置调小内收（0.045→0.034，上移量 0.07→0.047）：原来太大又贴着眼球上边缘，
                // 瞳孔朝右偏移时白点会被轮廓切掉，看着像粘在眼角的一颗珠子
                var shineGroup = new Transform3DGroup();
                shineGroup.Children.Add(new TranslateTransform3D(eyeX[i] - 0.028, 1.497, 0.95));
                shineGroup.Children.Add(_pupilOffset);
                shineGroup.Children.Add(scale);
                shine.Transform = shineGroup;
                face.Children.Add(shine);
            }

            // P0-5 嘴（气泡说话时按 120ms 节奏张合）
            _mouthScale = new ScaleTransform3D
            {
                CenterX = 0,
                CenterY = 1.02,
                CenterZ = 0.90,
                ScaleY = 0.4,
                ScaleZ = 0.35
            };
            var mouth = new GeometryModel3D(Sphere(0.10, 8, 12), darkMat);
            var mouthGroup = new Transform3DGroup();
            mouthGroup.Children.Add(new TranslateTransform3D(0, 1.02, 0.90));
            mouthGroup.Children.Add(_mouthScale);
            mouth.Transform = mouthGroup;
            face.Children.Add(mouth);

            // 睡眠口水珠：挂在嘴角右下方，只有 doze 时淡入（_droolScale 0 = 隐藏）
            // 材质给一点高光，看着像湿的水珠而不是一个粉色球
            var droolMat = new MaterialGroup();
            droolMat.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(255, 0xD9, 0xEE, 0xFF))));
            droolMat.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)), 90.0));
            droolMat.Children.Add(new EmissiveMaterial(new SolidColorBrush(Color.FromArgb(0x48, 0xE8, 0xF7, 0xFF))));
            _droolScale = new ScaleTransform3D(0, 0, 0);
            _droolBob = new TranslateTransform3D();
            // 三颗球串成水滴：脖子细、下坠大，整体挪到嘴角外侧，不压着嘴
            double[] droolR = { 0.019, 0.030, 0.042 };
            double[] droolX = { 0.112, 0.120, 0.126 };
            double[] droolY = { 1.004, 0.968, 0.928 };
            for (int i = 0; i < 3; i++)
            {
                var d = new GeometryModel3D(Sphere(droolR[i], 10, 14), droolMat);
                var tg = new Transform3DGroup();
                tg.Children.Add(new TranslateTransform3D(droolX[i], droolY[i], 0.918));
                tg.Children.Add(_droolBob);
                tg.Children.Add(_droolScale);
                d.Transform = tg;
                face.Children.Add(d);
            }

            // 腮红（害羞时可放大）
            // 腮红：环境光调低后原 160 alpha 会发闷，提到 195 保住粉感
            var blushMat = new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(195, 0xFF, 0xA3, 0xBA)));
            double[] blushX = { -0.62, 0.62 };
            for (int i = 0; i < 2; i++)
            {
                var scale = new ScaleTransform3D
                {
                    CenterX = blushX[i],
                    CenterY = 1.12,
                    CenterZ = 0.78
                };
                _blushScales = Append(_blushScales, scale);

                var blush = new GeometryModel3D(Sphere(0.09, 8, 10), blushMat);
                var blushGroup = new Transform3DGroup();
                blushGroup.Children.Add(new TranslateTransform3D(blushX[i], 1.12, 0.78));
                blushGroup.Children.Add(scale);
                blush.Transform = blushGroup;
                face.Children.Add(blush);
            }

            // 五官组变换：Scale Center=球底(0,0,0)——ScaleY<1 时五官整体下压（位移=高度×压缩比）
            var faceT = new Transform3DGroup();
            _faceSquashScale = new ScaleTransform3D();
            faceT.Children.Add(_faceSquashScale);
            face.Transform = faceT;
            pet.Children.Add(face);

            // 整体变换：落地压扁 → 跳跃 → 浮动 → 朝向（scale 在链首=最后作用于装配好的整体，
            // Center (0,0,0)=球底，"从脚底往上塌"）
            var group = new Transform3DGroup();
            _squashScale = new ScaleTransform3D();
            group.Children.Add(_squashScale);
            _jump = new TranslateTransform3D();
            _hop = new TranslateTransform3D();
            var rotation = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0);
            _yaw = new RotateTransform3D(rotation, new Point3D(0, 1, 0));
            group.Children.Add(_jump);
            group.Children.Add(_hop);
            group.Children.Add(_yaw);
            pet.Transform = group;

            return pet;
        }

        static T[] Append<T>(T[] arr, T item)
        {
            var n = new T[arr == null ? 1 : arr.Length + 1];
            if (arr != null) Array.Copy(arr, n, arr.Length);
            n[n.Length - 1] = item;
            return n;
        }

        // ================= 几何体生成 =================

        static MeshGeometry3D Sphere(double radius, int stacks, int slices)
        {
            var mesh = new MeshGeometry3D();
            for (int i = 0; i <= stacks; i++)
            {
                double phi = Math.PI * i / stacks;
                for (int j = 0; j <= slices; j++)
                {
                    double theta = 2 * Math.PI * j / slices;
                    double x = Math.Sin(phi) * Math.Cos(theta);
                    double y = Math.Cos(phi);
                    double z = Math.Sin(phi) * Math.Sin(theta);
                    mesh.Positions.Add(new Point3D(radius * x, radius * y, radius * z));
                    mesh.Normals.Add(new Vector3D(x, y, z));
                }
            }
            for (int i = 0; i < stacks; i++)
            {
                for (int j = 0; j < slices; j++)
                {
                    int a = i * (slices + 1) + j;
                    int b = a + slices + 1;
                    mesh.TriangleIndices.Add(a);
                    mesh.TriangleIndices.Add(b);
                    mesh.TriangleIndices.Add(a + 1);
                    mesh.TriangleIndices.Add(b);
                    mesh.TriangleIndices.Add(b + 1);
                    mesh.TriangleIndices.Add(a + 1);
                }
            }
            return mesh;
        }

        static MeshGeometry3D MakeDisc(double radius, int seg)
        {
            var mesh = new MeshGeometry3D();
            mesh.Positions.Add(new Point3D(0, 0, 0));
            mesh.Normals.Add(new Vector3D(0, 1, 0));
            for (int i = 0; i <= seg; i++)
            {
                double a = 2 * Math.PI * i / seg;
                mesh.Positions.Add(new Point3D(radius * Math.Cos(a), 0, radius * Math.Sin(a)));
                mesh.Normals.Add(new Vector3D(0, 1, 0));
            }
            for (int i = 1; i <= seg; i++)
            {
                mesh.TriangleIndices.Add(0);
                mesh.TriangleIndices.Add(i + 1);
                mesh.TriangleIndices.Add(i);
            }
            return mesh;
        }

        // ================= 主循环（状态机） =================

        void Tick(object sender, EventArgs e)
        {
            try
            {
                double dt = _timer.Interval.TotalSeconds;
                _clock += dt;
                _stateTimer -= dt;
                if (_pinTimer > 0) _pinTimer -= dt;   // 移动后钉住计时
                TickPose(dt);                          // 姿态接管层：衰减计时 + 缓动

                // FIX-GROUND：离地高度 / 落地弹簧形变 / 影子收缩
                double h = Math.Max(0, _hop.OffsetY + _jump.OffsetY);
                double vy = (h - _prevH) / dt;
                _squashV += (-SquashK * _squash - SquashC * _squashV) * dt;   // 弹簧积分
                _squash += _squashV * dt;
                if (_squash < 0) { _squash = 0; if (_squashV < 0) _squashV = 0; }   // 只压不拉
                if (_squash > 0.12) _squash = 0.12;                                  // 上限 12%
                // 触地检测：贴地(h<0.03)且明显下坠。速度取本帧与上一帧的较大值——
                // BounceEase 触地是瞬时过零，若两帧恰好跨过触地点且渲染节拍不稳，
                // 单帧 vy 可能落进 (-1,-0.5] 死区而漏判；前后帧取 max 消除该窗口。
                // 高处缓降(_prevH>0.08)兜底拖高松手这类小落差；闲置呼吸斜率≤0.08/s 绝无可能误触。
                // 0.15s 闸门防行走小跳连环触发
                double impactSpeed = Math.Max(Math.Abs(vy), Math.Abs(_prevVy));
                if (h < 0.03 && impactSpeed > 0.5 &&
                    (impactSpeed > 1.0 || _prevH > 0.08) &&
                    _clock - _lastImpact > 0.15)
                {
                    _squashV += Math.Min(impactSpeed * SquashImpulseMult, SquashImpulseCap);
                    _lastImpact = _clock;
                    Log.Write("land vy=" + Math.Round(vy, 2) + " squashV=" + Math.Round(_squashV, 2));
                }
                _prevVy = vy;
                _prevH = h;
                if (Math.Abs(_squash - _bodySquashApplied) > 0.0025)
                {
                    _bodySquashApplied = _squash;
                    ApplyBodySquash();               // 路线A：下半部逐顶点果冻形变
                    // 五官随压扁一起下压变形：ScaleY=1-0.6s（Center=球底 → 眼(1.45)下压 ~0.87s·单位 ≈ 7px），
                    // 横向轻微外扩近体积守恒；与身体上部表面位移(≈0.4s)方向一致，眼睛不会从眼窝里脱出
                    _faceSquashScale.ScaleY = 1.0 - _squash * 0.6;
                    _faceSquashScale.ScaleX = 1.0 + _squash * 0.15;
                    _faceSquashScale.ScaleZ = 1.0 + _squash * 0.15;
                }
                // 影子随离地高度收缩变淡（挂在 content，不跟 pet 组跳）
                if (Math.Abs(h - _shadowAppliedH) > 0.015)
                {
                    _shadowAppliedH = h;
                    double ss = 1.0 - 0.35 * h;
                    _shadowScale.ScaleX = ss;
                    _shadowScale.ScaleZ = ss;
                    RefreshShadow(h);
                }
                TickShadowBg(dt);

                // 弹珠模式：自由飞行（撞壁反弹 + 摩擦减速）；飞行期间不让它自己走
                if (_ballFlight) TickBallFlight(dt);

                if (_state == State.Walk)
                {
                    _hop.OffsetY = Math.Abs(Math.Sin(_clock * 9.0)) * 0.14;  // 行走小跳
                    SetEyes(1.0);
                    var area = WorkArea();
                    double nx = Left + _dir * WalkSpeed * dt;
                    if (nx < area.Left || nx + Width > area.Right || _stateTimer <= 0)
                    {
                        _hasWalkTarget = false;
                        EnterIdle();
                    }
                    else
                    {
                        Left = nx;
                    }
                }
                else
                {
                    // 非行走状态：情绪姿态浮动（待机/情绪/打盹共用）
                    // FIX-GROUND P0-5：非负呼吸曲线（0→amp→0，两端导数为 0），负半周会把球底压进地里
                    // 呼吸曲线改用姿态层给的频率/幅度（没接管时就是情绪值）
                    _hop.OffsetY = (1 - Math.Cos(_clock * _curPoseBreathFreq)) * 0.5 * _curPoseBreathAmp;
                    if (_hop.OffsetY < 0) _hop.OffsetY = 0;
                    double eyeNow = Blink(_clock) ? 0.15 : (double.IsNaN(_poseEye) ? _moodEyes : _curPoseEye);
                    SetEyes(eyeNow);

                    // P0-6 Doze：偶尔冒 Zzz 气泡
                    if (_state == State.Doze)
                    {
                        _zzzTimer -= dt;
                        if (_zzzTimer <= 0)
                        {
                            _zzzTimer = 6.0 + _rand.NextDouble() * 4.0;
                            ShowBubble("Zzz", false);   // 只出气泡，不动嘴
                        }
                    }

                    // P0-6 长时间无交互 → Doze
                    if (_state == State.Idle && _clock - _lastInteract > DozeAfterSeconds)
                    {
                        Log.Write("doze (inactive " + DozeAfterSeconds + "s)");
                        SetMood("doze");
                    }

                    // 待机时随机闲逛计时（钉住期/暂停期不闲逛）
                    if (_state == State.Idle && _stateTimer <= 0 &&
                        _pinTimer <= 0 && !_paused && !_ballFlight && _poseHold <= 0)
                        EnterWalk();
                }

                // 睡眠口水珠：doze 淡入（0→1）、醒后缩回；出现时随呼吸轻轻上下
                double droolTarget = (_mood == "doze") ? 1.0 : 0.0;
                _droolVis += (droolTarget - _droolVis) * 0.18;
                if (_droolScale != null)
                {
                    _droolScale.ScaleX = _droolVis;
                    _droolScale.ScaleY = _droolVis;
                    _droolScale.ScaleZ = _droolVis;
                }
                if (_droolBob != null)
                    _droolBob.OffsetY = _droolVis > 0.02 ? Math.Sin(_clock * 1.5) * 0.012 : 0;

                // P0-7 每帧吸附到当前屏幕工作区（显示器切换/拔插自动纠正）
                ClampToWorkArea();
                TickParallax();
                TickOrb(dt);          // FIX-ORB：魔法球自己飘 / 被追时闪开
            }
            catch (Exception ex)
            {
                Log.Write("tick: " + ex.Message);
            }
        }

        bool Blink(double t)
        {
            return (t % BlinkCycle) < BlinkLen;
        }

        void EnterIdle()
        {
            _state = State.Idle;
            _stateTimer = IdleMin + _rand.NextDouble() * (IdleMax - IdleMin);
            AnimateYaw(0);
        }

        void EnterWalk()
        {
            _state = State.Walk;
            _stateTimer = WalkMin + _rand.NextDouble() * (WalkMax - WalkMin);
            _dir = _rand.Next(2) == 0 ? 1.0 : -1.0;
            AnimateYaw(_dir > 0 ? WalkYawDeg : -WalkYawDeg);
            _hasWalkTarget = false;      // 现在魔法球是"跟着走"，不再有追球目标
        }

        // FIX-TURN P0-2：一律走最短路径——delta 折进 (-180,180]，起点取与 target 等价且离当前最近的角。
        // 校验：cur=360,target=0 → delta 0 → 不动；cur=200,target=0 → 从 -160 → 0（顺向最短，不倒转一整圈）
        void AnimateYaw(double target)
        {
            double cur = ((AxisAngleRotation3D)_yaw.Rotation).Angle;
            double delta = target - cur;
            delta = ((delta + 180.0) % 360.0 + 360.0) % 360.0 - 180.0;  // 折到 (-180, 180]
            double from = target - delta;                               // 与 target 等价、离当前最近的起点
            var a = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(300));
            a.EasingFunction = new QuadraticEase();
            _yaw.Rotation.BeginAnimation(AxisAngleRotation3D.AngleProperty, a);
        }

        void SetEyes(double scaleY)
        {
            if (_eyeScales == null) return;
            foreach (var s in _eyeScales)
            {
                s.ScaleY = scaleY * _nearBoost;   // P0-4 靠近时眼睛放大一点点
                s.ScaleX = _nearBoost;
            }
        }

        // ================= 交互 =================

        // 路线A：按 _bodySquashApplied 对 body 网格做下半部加权形变（t = 1-wy/2：顶 0 → 底 1）。
        // 纵向：y' = y - s·2.0·t^1.5·wy（底 wy=0 位移恒 0 贴地钉死；赤道下沉 ~0.7s ≈ 5px；顶部 ~1px）
        // 横向：轻微径向鼓出 s·1.2·t² + 底部加性摊开 s·2.2·t²（果冻"摊脚"，s=0.095 时脚圈 +0.2 units ≈ 7px/侧）
        // 五官（眼 1.45 / 嘴 1.2 / 腮红）所在高度表面位移 <0.5px，脸绝不变形。
        // 法线按纵压/横鼓近似逆缩放后归一（s≤0.12 误差不可见）。825 顶点每次 ~微秒级。
        void ApplyBodySquash()
        {
            try
            {
                double s = _bodySquashApplied;
                var pos = _bodyMesh.Positions;
                var nor = _bodyMesh.Normals;
                for (int i = 0; i < _bodyBase.Length; i++)
                {
                    Point3D p = _bodyBase[i];
                    double wy = p.Y + 1.0;
                    double t = 1.0 - wy * 0.5;
                    if (t < 0) t = 0;
                    double wv = t * Math.Sqrt(t);
                    double fy = 1.0 - s * 2.0 * wv;
                    if (fy < 0.2) fy = 0.2;
                    double inv = Math.Sqrt(p.X * p.X + p.Z * p.Z);
                    double bulge = 1.0 + s * 1.2 * t * t;
                    double spread = s * 2.2 * t * t;
                    double nx, nz;
                    if (inv > 1e-6)
                    {
                        double k = (inv * bulge + spread) / inv;
                        nx = p.X * k;
                        nz = p.Z * k;
                    }
                    else { nx = 0; nz = 0; }
                    pos[i] = new Point3D(nx, p.Y - s * 2.0 * wv * wy, nz);
                    double fx = inv > 1e-6 ? (inv * bulge + spread) / inv : 1.0;
                    double nlx = p.X / fx, nly = p.Y / fy, nlz = p.Z / fx;
                    double len = Math.Sqrt(nlx * nlx + nly * nly + nlz * nlz);
                    if (len < 1e-9) len = 1;
                    nor[i] = new Vector3D(nlx / len, nly / len, nlz / len);
                }
            }
            catch (Exception ex) { Log.Write("body squash: " + ex.Message); }
        }

        void OnDown(object sender, MouseButtonEventArgs e)
        {
            // 轮盘展开时左键 = 选中/取消，不能落进拖动逻辑
            if (_radialOpen)
            {
                UpdateRadialHover();
                if (_radialHover == HitChip) ToggleRadialStyle();
                else if (_radialStyle == 5)
                {
                    var cp = CursorDip();
                    double ox = cp.X - _radialCx, oy = cp.Y - _radialCy;
                    if (Math.Sqrt(ox * ox + oy * oy) > RadialOuter + 30) CloseRadial();
                    else RunRadialItem();
                }
                else if (_radialHover >= 0) RunRadialItem();
                else CloseRadial();
                e.Handled = true;
                return;
            }
            var dp = e.GetPosition(this);
            Log.Write("mouse down at (" + (int)dp.X + "," + (int)dp.Y + ") clickCount=" + e.ClickCount);
            if (e.ClickCount == 2)
            {
                Jump();
                _dragPending = false;             // 双击不走拖动/单击判定，清掉候选防止泄漏
                _dragGuard.Stop();
                e.Handled = true;
                return;
            }
            // FIX-CLICK-2 P0-2：按下只记录候选，不立即开拖——窗口必须一动不动，
            // 修掉"按下瞬间窗口被拽 1~2px"（既是观感问题也是单击位移失真的源头）
            //
            // ⚠ 顺序关键：CaptureMouse() 会同步泵出队列里挂起的 mouse-move（OnMove 重入）！
            // 实测：远距离移动光标后立刻按下，move 事件还在队列里，若 _downCX 尚未赋值（=0），
            // 重入的 OnMove 会以 dpx=1156 误 promotion、以 (0,0) 为基准把窗口瞬移出屏。
            // 因此基准读取必须放在 CaptureMouse 之前，pending 置位放最后。
            _dragging = false;
            NativePoint cp0;
            if (GetCursorPos(out cp0)) { _downCX = cp0.X; _downCY = cp0.Y; }   // P0-1：物理像素基准，先于一切
            _dragWin = new Point(Left, Top);      // 窗口基准只给拖动用，不参与单击判定
            _dragMouse = new Point(_downCX, _downCY);   // P0-1：物理像素，弃用 PointToScreen
            _downClock = _clock;                  // 记录按下时刻
            _dragStartTop = Top;                  // 松手下落用
            CaptureMouse();                       // 快速拖动不丢事件（可能同步重入 OnMove：此时基准已就绪，dpx=0 安全）
            _dragPending = true;
            _dragMouse = new Point(cp0.X, cp0.Y); // P0-1：也用物理像素，弃用 PointToScreen（窗口跳位污染基准）
            _dragVX = 0; _dragVY = 0; _dragLastT = 0;   // 弹珠模式：每次按下重置速度基准
            _ballFlight = false; _ballVX = 0; _ballVY = 0;   // 飞行中再被抓住 → 立刻刹停，交回鼠标
            if (_state == State.Walk)
            {
                _state = State.Idle;              // 抓起时打断行走，避免 Tick 抢位置
                _stateTimer = IdleMin;
            }
            _dragGuard.Stop();
            _dragGuard.Start();                   // P0-1：500ms 后仍未松手且没拖动 → 兜底判单击
        }

        void OnDragEnd(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging && !_dragPending) return;   // 菜单等子元素的 up 不参与判定；pending 也要收尾判单击
            EndDrag();
        }

        void OnLostCapture(object sender, MouseEventArgs e)
        {
            if (_dragging || _dragPending)   // 捕获被抢/释放 → 立刻收尾，绝不允许卡在 dragging/pending
            {
                Log.Write("lost mouse capture -> end drag");
                EndDrag();
            }
        }

        // FIX-CLICK-2 P0-3：单击位移唯一判定入口——按下/当前物理像素差换算 DIP；up 路径与 guard 兜底共用。
        // P0-1：基准是 GetCursorPos 原值（_downCX/_downCY），与窗口跳位、PointToScreen 彻底脱钩
        double ClickDistDip()
        {
            NativePoint cp;
            if (!GetCursorPos(out cp)) return -1;
            var src = PresentationSource.FromVisual(this);
            double scale = src != null ? src.CompositionTarget.TransformToDevice.M11 : 1.0;
            return Math.Sqrt(Math.Pow(cp.X - _downCX, 2) + Math.Pow(cp.Y - _downCY, 2)) / scale;
        }

        // P0-3：阈值 5→8 DIP（本机空点抖动实测 1.6~2 DIP，125% 缩放下留足余量）
        bool IsClick() { double d = ClickDistDip(); return d >= 0 && d < 8.0 && (_clock - _downClock) * 1000 < 400; }

        // 进入弹珠模式：脱离鼠标，带着当前速度自由飞，Ticker 里每帧撞壁反弹 + 摩擦减速
        void StartBallFlight(double vx, double vy)
        {
            // 初速封顶：手快甩出的瞬时速度能到 3000+ DIP/s，不封顶会飞得太夸张
            double sp0 = Math.Sqrt(vx * vx + vy * vy);
            if (sp0 > BallMaxSpeed)
            {
                vx = vx / sp0 * BallMaxSpeed;
                vy = vy / sp0 * BallMaxSpeed;
            }
            _ballFlight = true;
            _ballVX = vx;
            _ballVY = vy;
            _dragPending = false;
            _dragging = false;
            if (_dragGuard != null) _dragGuard.Stop();
            if (IsMouseCaptured) ReleaseMouseCapture();
            _state = State.Idle;
            _stateTimer = IdleMin + _rand.NextDouble() * (IdleMax - IdleMin);
            _pinTimer = 0;
            Log.Write("ball launch vx=" + Math.Round(vx) + " vy=" + Math.Round(vy));
        }

        // 每帧飞行积分：四壁反弹（衰减 20%）+ 摩擦减速 + 撞壁给一点果冻形变
        void TickBallFlight(double dt)
        {
            var wa = WorkArea();
            double nx = Left + _ballVX * dt;
            double ny = Top + _ballVY * dt;
            bool bounced = false;
            if (nx < wa.Left) { nx = wa.Left; _ballVX = -_ballVX * BallWallDamp; bounced = true; }
            else if (nx + Width > wa.Right) { nx = wa.Right - Width; _ballVX = -_ballVX * BallWallDamp; bounced = true; }
            if (ny < wa.Top) { ny = wa.Top; _ballVY = -_ballVY * BallWallDamp; bounced = true; }
            else if (ny + Height > wa.Bottom) { ny = wa.Bottom - Height; _ballVY = -_ballVY * BallWallDamp; bounced = true; }
            Left = nx;
            Top = ny;
            if (bounced)
            {
                _squashV += 0.35;             // 复用落地那套弹簧，撞墙也"咚"一下
                if (_squashV > 0.9) _squashV = 0.9;
                _lastImpact = _clock;
                Log.Write("ball wall vx=" + Math.Round(_ballVX) + " vy=" + Math.Round(_ballVY));
            }
            double sp = Math.Sqrt(_ballVX * _ballVX + _ballVY * _ballVY);
            double f = sp > 0 ? Math.Max(0.0, sp - BallFriction * dt) / sp : 0.0;
            _ballVX *= f;
            _ballVY *= f;
            sp = Math.Sqrt(_ballVX * _ballVX + _ballVY * _ballVY);
            if (sp < BallStopSpeed)
            {
                _ballFlight = false;
                _ballVX = _ballVY = 0;
                _pinTimer = 1.0;
                EnterIdle();
                Log.Write("ball stop");
            }
        }

        // 统一收尾：单击判定（IsClick → 转正），否则视为拖动钉住
        void EndDrag()
        {
            _dragGuard.Stop();
            _dragPending = false;
            _dragging = false;
            double distDip, ms; bool click;
            try
            {
                distDip = ClickDistDip();               // P0-1：取坐标必须在释放捕获之前——释放会引起一次坐标刷新
                ms = (_clock - _downClock) * 1000.0;
                click = IsClick();
            }
            catch (Exception ex) { Log.Write("click calc: " + ex.GetType().Name + " " + ex.Message); distDip = -1; ms = 0; click = false; }
            if (IsMouseCaptured) ReleaseMouseCapture(); // P0-1：放最后
            try
            {
                Log.Write("mouse up distDIP=" + Math.Round(distDip, 1) +
                          " ms=" + (int)ms + " -> click=" + click);
                if (click)
                    FaceFront();
                else
                {
                    _pinTimer = 1.5;   // P0-4：真拖动放下后钉住 1.5s，避免立刻走开
                    // 弹珠模式：松手时速度够大就直接扔出去（后面撞壁会自己弹），否则走原来的"自由落体"
                    if (Math.Abs(_dragVX) > BallThrowSpeed || Math.Abs(_dragVY) > BallThrowSpeed)
                    {
                        StartBallFlight(_dragVX, _dragVY);
                        _pinTimer = 1.5;
                        return;
                    }

                    // FIX-GROUND P0-3 松手下落：离地高度 = 3D 高度 + 拖起时窗口上移量换算；
                    // h0 < 0.02（贴地松手）直接跳过
                    try
                    {
                        double h0 = Math.Max(0, _hop.OffsetY + _jump.OffsetY)
                            + Math.Max(0, _dragStartTop - Top) * 2.0 / Math.Max(1.0, Height);
                        if (h0 >= 0.02)
                        {
                            int fallMs = (int)Math.Max(120, Math.Min(350, h0 * 250));
                            _jump.BeginAnimation(TranslateTransform3D.OffsetYProperty, null);
                            _jump.OffsetY = 0;
                            var fa = new DoubleAnimation(h0, 0, TimeSpan.FromMilliseconds(fallMs))
                            {
                                FillBehavior = FillBehavior.Stop
                            };
                            fa.Completed += delegate
                            {
                                try
                                {
                                    _jump.BeginAnimation(TranslateTransform3D.OffsetYProperty, null);
                                    _jump.OffsetY = 0;
                                }
                                catch { }
                            };
                            _jump.BeginAnimation(TranslateTransform3D.OffsetYProperty, fa);
                            Log.Write("drag drop h0=" + Math.Round(h0, 2) + " ms=" + ms);
                        }
                    }
                    catch (Exception ex) { Log.Write("drag drop: " + ex.Message); }
                }
            }
            catch (Exception ex) { Log.Write("drag end: " + ex.Message); }
        }

        // 单击转正：面向镜头（yaw -> 0），1.5 秒内不重新闲逛；顺带唤醒
        public void FaceFront()
        {
            FaceFrontCore(true);        // 鼠标路径：受 _wakeSuppressUntil 抑制
        }

        // fromMouse=false 供 /pet/face、MCP pet_face 调用，不受抑制
        public void FaceFrontCore(bool fromMouse)
        {
            try
            {
                if (fromMouse && _clock < _wakeSuppressUntil) return;
                if (_state == State.Walk)
                {
                    _state = State.Idle;
                    _stateTimer = IdleMin;
                }
                if (_state == State.Doze)
                    Wake();             // 睡眠态点击必醒：Wake 内部 SetMood("neutral") 复位闭眼/Zzz
                // FIX-TURN P0-3：转圈途中点击只唤醒，朝向交给转圈自己收尾——
                // 此时强行 AnimateYaw(0) 会从中间角度（如 200°）倒回，又是反向转
                if (Environment.TickCount >= _turnUntil)
                {
                    Log.Write("click yaw base=" + Math.Round(((AxisAngleRotation3D)_yaw.Rotation).Angle, 1));
                    AnimateYaw(0);      // 复用现有 300ms 二次缓动（最短路径）
                }
                _pinTimer = 1.5;        // 避免刚转正就又侧身走开
                Log.Write("click -> face front (yaw -> 0)");
            }
            catch (Exception ex) { Log.Write("face front: " + ex.Message); }
        }

        // 菜单「睡眠」：随时可睡，不必等 5 分钟自然休眠
        public void EnterDoze()
        {
            _lastInteract = _clock;
            if (_state == State.Walk) EnterIdle();
            SetMood("doze");
            _pinTimer = 0;
            _wakeSuppressUntil = _clock + 0.8;   // 菜单 up 冒泡窗口，别把它自己点醒
            Log.Write("menu -> doze");
        }

        void OpenUrl()
        {
            try
            {
                System.Diagnostics.Process.Start("https://www.doubao.com");
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("无法打开浏览器: " + ex.Message);
            }
        }

        void OnMove(object sender, MouseEventArgs e)
        {
            // 轮盘展开期间：鼠标被 Capture 到这里，只负责高亮跟随，不碰拖动状态
            if (_radialOpen)
            {
                UpdateRadialHover();
                return;
            }
            // 松键清理：只在按键确实松开时复位（CaptureMouse 会合成一次 OnMove——
            // 若在"按键未按下"判断之外再判 !_dragging，pending 态会在按下期间被误清，单击判定全灭）
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _dragPending = false;
                _dragging = false;
                if (IsMouseCaptured) ReleaseMouseCapture();
                return;
            }
            // FIX-CLICK-2 P0-2：延迟拖动——按下后首次移动超过 4 DIP 才真正开拖；
            // 抖动期间窗口一动不动（按下到松手 Left/Top 必须零变化）
            if (_dragPending)
            {
                NativePoint cm;
                if (GetCursorPos(out cm))
                {
                    var src0 = PresentationSource.FromVisual(this);
                    double scale0 = src0 != null ? src0.CompositionTarget.TransformToDevice.M11 : 1.0;
                    double dpx = Math.Sqrt(Math.Pow(cm.X - _downCX, 2) + Math.Pow(cm.Y - _downCY, 2));
                    if (dpx > 4.0 * scale0)
                    {
                        _dragging = true;
                        _dragPending = false;
                        _dragWin = new Point(Left, Top);
                        // 基准用"按下时刻"光标位而非当前位置：WPF 会合并快速移动成一次 OnMove，
                        // 若以当前位置为基准，单次跳变的拖动位移恒为零（窗口不跟手）。
                        // pending 期间窗口纹丝不动 → (按下窗口位, 按下光标位) 是自洽基准
                        _dragMouse = new Point(_downCX, _downCY);
                    }
                }
                if (_dragPending) return;   // 仍处候选态：忽略抖动，绝不能落进后面的清理/拖动分支
            }
            if (!_dragging) return;
            // P0-1：拖动也用真实屏幕坐标——捕获状态下 e.GetPosition 可能携带陈旧位置
            //（合成 move 会按陈旧基准让窗口瞬移，本轮"点击后窗口跳 625px、后续点击全落空"的元凶）。
            // promotion 判定用 GetCursorPos 实时值，天然不受合成事件影响
            NativePoint cm2;
            if (!GetCursorPos(out cm2)) return;
            var src1 = PresentationSource.FromVisual(this);
            double scale1 = src1 != null ? src1.CompositionTarget.TransformToDevice.M11 : 1.0;
            Left = _dragWin.X + (cm2.X - _dragMouse.X) / scale1;
            Top = _dragWin.Y + (cm2.Y - _dragMouse.Y) / scale1;

            // 记录拖动速度（指数平滑），供"甩到边界就弹出去"用
            if (_dragLastT > 0)
            {
                double ddt = _clock - _dragLastT;
                if (ddt > 0.002 && ddt < 0.35)
                {
                    _dragVX = _dragVX * 0.6 + ((Left - _dragLastX) / ddt) * 0.4;
                    _dragVY = _dragVY * 0.6 + ((Top - _dragLastY) / ddt) * 0.4;
                }
            }
            _dragLastX = Left; _dragLastY = Top; _dragLastT = _clock;

            // 撞到工作区边界：速度够大就脱手当弹珠弹出去；速度小就照旧让 tick 里的吸附贴住
            var wa = WorkArea();
            bool hitWall = false;
            if (Left <= wa.Left && _dragVX < -BallLaunchSpeed) { Left = wa.Left; hitWall = true; }
            else if (Left + Width >= wa.Right && _dragVX > BallLaunchSpeed) { Left = wa.Right - Width; hitWall = true; }
            if (Top <= wa.Top && _dragVY < -BallLaunchSpeed) { Top = wa.Top; hitWall = true; }
            else if (Top + Height >= wa.Bottom && _dragVY > BallLaunchSpeed) { Top = wa.Bottom - Height; hitWall = true; }
            if (hitWall)
            {
                double nvx = _dragVX, nvy = _dragVY;
                if (Left <= wa.Left && nvx < 0) nvx = -nvx * BallWallDamp;
                else if (Left + Width >= wa.Right && nvx > 0) nvx = -nvx * BallWallDamp;
                if (Top <= wa.Top && nvy < 0) nvy = -nvy * BallWallDamp;
                else if (Top + Height >= wa.Bottom && nvy > 0) nvy = -nvy * BallWallDamp;
                StartBallFlight(nvx, nvy);
            }
        }

        void Jump()
        {
            if (_state == State.Walk) EnterIdle();

            // 跳到顶时球顶已贴到窗口上沿，气泡没地方放会压到脸上：跳跃期间藏起气泡，落地淡回
            bool hideBubble = _bubble != null && _bubble.Visibility == Visibility.Visible;
            if (hideBubble) _bubble.Opacity = 0;

            var kf = new DoubleAnimationUsingKeyFrames();

            // 放慢弹跳：起跳 300ms、下落回弹 800ms（原 200/520），触地间隔随比例放大仍 > 0.15s 闸门
            var up = new EasingDoubleKeyFrame(0.72, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300)));
            up.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            kf.KeyFrames.Add(up);

            var down = new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(800)));
            down.EasingFunction = new BounceEase { Bounces = 2, Bounciness = 2, EasingMode = EasingMode.EaseOut };
            kf.KeyFrames.Add(down);

            if (hideBubble)
            {
                kf.Completed += delegate
                {
                    // 气泡还在有效期内就淡回来，已经过期则保持隐藏
                    if (_bubble.Visibility == Visibility.Visible) _bubble.Opacity = 1;
                };
            }

            _jump.BeginAnimation(TranslateTransform3D.OffsetYProperty, kf);
        }
    }

    // ============================================================================
    // 本地控制服务：迷你 HTTP 服务器（TcpListener 实现，无任何外部依赖）
    // 供豆包等外部程序通过 http://127.0.0.1:8787 控制桌宠
    // ============================================================================
    public class ControlServer
    {
        const int DefaultPort = 8787;

        readonly PetWindow _pet;
        TcpListener _listener;

        public int Port { get; private set; }

        public ControlServer(PetWindow pet)
        {
            _pet = pet;
            Port = -1;
        }

        public void Start()
        {
            // 端口被占用时依次向后尝试
            for (int p = DefaultPort; p < DefaultPort + 10; p++)
            {
                try
                {
                    _listener = new TcpListener(IPAddress.Loopback, p);
                    _listener.Start();
                    Port = p;
                    _listener.BeginAcceptTcpClient(OnAccept, null);
                    return;
                }
                catch { }
            }
        }

        void OnAccept(IAsyncResult ar)
        {
            TcpClient client;
            try { client = _listener.EndAcceptTcpClient(ar); }
            catch { return; }
            try { _listener.BeginAcceptTcpClient(OnAccept, null); }
            catch { }

            // 安全：仅接受本机回环连接，拒绝外部 IP
            var remote = client.Client.RemoteEndPoint as IPEndPoint;
            if (remote == null || !IPAddress.IsLoopback(remote.Address))
            {
                client.Close();
                return;
            }

            try { Handle(client); }
            catch (Exception ex) { Log.Write("handle: " + ex.Message); }
        }

        void Handle(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                stream.ReadTimeout = 3000;
                string request = ReadAll(stream);
                string[] lines = request.Replace("\r\n", "\n").Split('\n');
                if (lines.Length == 0) return;
                var seg = lines[0].Split(' ');
                if (seg.Length < 2) return;

                string method = seg[0];
                string fullPath = seg[1];
                string path = fullPath;
                string query = "";
                int q = fullPath.IndexOf('?');
                if (q >= 0)
                {
                    path = fullPath.Substring(0, q);
                    query = fullPath.Substring(q + 1);
                }

                // 请求体（JSON）
                string body = "";
                int hIdx = request.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (hIdx >= 0 && request.Length > hIdx + 4)
                    body = request.Substring(hIdx + 4);

                // CORS 预检
                if (method == "OPTIONS")
                {
                    WriteResponse(stream, 204, "");
                    return;
                }

                // P0-8：API 请求行入日志
                Log.Write(method + " " + fullPath);

                // P0-6：任何 API 调用视为交互，唤醒
                _pet.Dispatcher.BeginInvoke(new Action(_pet.Wake));

                // MCP（豆包等自定义连接器走 JSON-RPC），与 REST 共存自动识别
                if (body.Contains("\"jsonrpc\""))
                {
                    McpHandle(stream, body);
                    return;
                }

                int code = 200;
                string data;
                switch (path)
                {
                    case "/":
                        data = "{\"endpoints\":[\"GET /pet/port\",\"GET /status\",\"POST /pet/chat\",\"POST /pet/action\",\"POST /pet/visible\",\"POST /pet/move\",\"POST /pet/mood\",\"POST /pet/pose\",\"POST /pet/pause\",\"POST /pet/face\",\"POST /jump\",\"POST /walk?dir=1&seconds=5\",\"POST /idle\"]}";
                        break;

                    case "/pet/port":
                        data = "{\"port\":" + Port + "}";
                        break;

                    case "/status":
                        data = (string)_pet.Dispatcher.Invoke(new Func<string>(_pet.ApiStatus));
                        break;

                    case "/pet/chat":
                        if (method != "POST") { code = 405; data = "{}"; break; }
                        {
                            string text = ExtractString(body, "text");
                            if (text.Length == 0) { code = 400; data = "{}"; break; }
                            string t = text;
                            _pet.Dispatcher.BeginInvoke(new Action(delegate { _pet.ApiChat(t); }));
                            data = "{}";
                        }
                        break;

                    case "/pet/action":
                        if (method != "POST") { code = 405; data = "{}"; break; }
                        {
                            string act = ExtractString(body, "action");
                            if (!(bool)_pet.Dispatcher.Invoke(new Func<string, bool>(_pet.ApiAction), act))
                            {
                                code = 400;
                                data = "{}";
                                break;
                            }
                            data = "{\"action\":\"" + act.ToLowerInvariant() + "\"}";
                        }
                        break;

                    case "/pet/visible":
                        if (method != "POST") { code = 405; data = "{}"; break; }
                        {
                            bool show = ExtractBool(body, "show", true);
                            bool s = show;
                            _pet.Dispatcher.BeginInvoke(new Action(delegate { _pet.ApiSetVisible(s); }));
                            data = "{\"show\":" + (show ? "true" : "false") + "}";
                        }
                        break;

                    case "/pet/move":
                        if (method != "POST") { code = 405; data = "{}"; break; }
                        {
                            double mx, my;
                            if (!TryExtractNumber(body, "x", out mx) || !TryExtractNumber(body, "y", out my))
                            {
                                code = 400;
                                data = "{}";
                                break;
                            }
                            double dx = mx, dy = my;
                            _pet.Dispatcher.BeginInvoke(new Action(delegate { _pet.ApiMoveTo(dx, dy); }));
                            data = "{\"x\":" + mx + ",\"y\":" + my + "}";
                        }
                        break;

                    case "/pet/mood":
                        if (method != "POST") { code = 405; data = "{}"; break; }
                        {
                            string mood = ExtractString(body, "mood");
                            bool ok = (bool)_pet.Dispatcher.Invoke(
                                new Func<string, bool>(_pet.SetMood), mood);
                            if (!ok)
                            {
                                // 规范要求：非法 mood 返回 {"code":1,"msg":"unknown mood"}，不许崩
                                WriteResponse(stream, 200,
                                    "{\"code\":1,\"msg\":\"unknown mood\",\"data\":{}}");
                                return;
                            }
                            data = "{\"mood\":\"" + mood.ToLowerInvariant() + "\"}";
                        }
                        break;

                    case "/pet/pose":
                        if (method != "POST") { code = 405; data = "{}"; break; }
                        {
                            var cmd = new PetWindow.PoseCmd();
                            double v;
                            if (TryExtractNumber(body, "eye", out v)) cmd.Eye = v;
                            if (TryExtractNumber(body, "blush", out v)) cmd.Blush = v;
                            if (TryExtractNumber(body, "yaw", out v)) cmd.Yaw = v;
                            if (TryExtractNumber(body, "gazeX", out v)) cmd.GazeX = v;
                            if (TryExtractNumber(body, "gazeY", out v)) cmd.GazeY = v;
                            if (TryExtractNumber(body, "breathAmp", out v)) cmd.BreathAmp = v;
                            if (TryExtractNumber(body, "breathFreq", out v)) cmd.BreathFreq = v;
                            if (TryExtractNumber(body, "hold", out v)) cmd.Hold = v;
                            cmd.Clear = ExtractBool(body, "clear", false);
                            var c2 = cmd;
                            _pet.Dispatcher.BeginInvoke(new Action(delegate { _pet.ApiPose(c2); }));
                            data = "{\"pose\":\"ok\"}";
                        }
                        break;

                    case "/pet/pause":
                        if (method != "POST") { code = 405; data = "{}"; break; }
                        {
                            bool on = ExtractBool(body, "on", false);
                            _pet.Dispatcher.Invoke(new Action<bool>(_pet.ApiPause), on);
                            data = "{\"pause\":" + (on ? "true" : "false") + "}";
                        }
                        break;

                    case "/pet/face":
                        if (method != "POST") { code = 405; data = "{}"; break; }
                        _pet.Dispatcher.Invoke(new Action(() => _pet.FaceFrontCore(false)));
                        data = "{\"face\":\"front\"}";
                        break;

                    case "/jump":
                        if (method != "POST") { code = 405; data = "{}"; break; }
                        _pet.Dispatcher.BeginInvoke(new Action(_pet.ApiJump));
                        data = "{\"action\":\"jump\"}";
                        break;

                    case "/walk":
                        if (method != "POST") { code = 405; data = "{}"; break; }
                        {
                            double dir = GetQueryDouble(query, "dir", 1.0);
                            double seconds = GetQueryDouble(query, "seconds", 5.0);
                            double d = dir, sec = seconds;
                            _pet.Dispatcher.BeginInvoke(new Action(delegate { _pet.ApiWalk(d, sec); }));
                            data = "{\"action\":\"walk\",\"dir\":" + (dir >= 0 ? 1 : -1) + ",\"seconds\":" + seconds + "}";
                        }
                        break;

                    case "/idle":
                        if (method != "POST") { code = 405; data = "{}"; break; }
                        _pet.Dispatcher.BeginInvoke(new Action(_pet.ApiIdle));
                        data = "{\"action\":\"idle\"}";
                        break;

                    default:
                        code = 404;
                        data = "{}";
                        break;
                }

                string json = code == 200
                    ? "{\"code\":0,\"msg\":\"ok\",\"data\":" + data + "}"
                    : "{\"code\":" + (code == 400 ? 400 : code == 404 ? 404 : code == 405 ? 405 : 1) +
                      ",\"msg\":\"" + (code == 400 ? "bad request" : code == 404 ? "not found" : "method not allowed") +
                      "\",\"data\":" + data + "}";
                WriteResponse(stream, code, json);
            }
        }

        // ================= MCP（豆包自定义连接器）============================
        // 豆包自定义连接器走 MCP 协议（JSON-RPC over HTTP）。
        // 同一端口自动识别：请求体含 "jsonrpc" 即按 MCP 处理，否则走 REST。

        void McpHandle(NetworkStream stream, string body)
        {
            string rpcId = ExtractRawValue(body, "id");
            string rpcMethod = ExtractString(body, "method");

            // P0-8 / P0-6：记录 + 唤醒
            Log.Write("mcp " + rpcMethod);
            _pet.Dispatcher.BeginInvoke(new Action(_pet.Wake));

            // 通知类请求（无 id）不需要响应
            if (rpcId.Length == 0)
            {
                WriteResponse(stream, 202, "");
                return;
            }

            string result;
            bool isError = false;
            switch (rpcMethod)
            {
                case "initialize":
                    result = "{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{\"tools\":{}}," +
                             "\"serverInfo\":{\"name\":\"desktop-pet\",\"version\":\"1.1.0\"}}";
                    break;

                case "ping":
                    result = "{}";
                    break;

                case "tools/list":
                    result = McpToolsList();
                    break;

                case "tools/call":
                    result = McpToolsCall(body, out isError);
                    break;

                default:
                    WriteRpcError(stream, rpcId, -32601, "method not found: " + rpcMethod);
                    return;
            }

            string payload = "{\"jsonrpc\":\"2.0\",\"id\":" + rpcId +
                             ",\"result\":" + result + "}";
            WriteResponse(stream, 200, payload);
        }

        string McpToolsList()
        {
            return "{\"tools\":[" +
                McpTool("pet_chat", "让桌宠头顶弹出文字气泡（3秒后消失）",
                    "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\",\"description\":\"气泡显示的文字\"}},\"required\":[\"text\"]}") + "," +
                McpTool("pet_action", "触发桌宠动作动画",
                    "{\"type\":\"object\",\"properties\":{\"action\":{\"type\":\"string\",\"enum\":[\"idle\",\"bounce\",\"shake\",\"turn\",\"blush\"],\"description\":\"idle=待机,bounce=弹跳,shake=摇头,turn=转圈,blush=害羞\"}},\"required\":[\"action\"]}") + "," +
                McpTool("pet_visible", "显示或隐藏桌宠",
                    "{\"type\":\"object\",\"properties\":{\"show\":{\"type\":\"boolean\",\"description\":\"true显示,false隐藏\"}},\"required\":[\"show\"]}") + "," +
                McpTool("pet_move", "平滑移动桌宠到屏幕坐标（自动限制不出屏幕）",
                    "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\",\"description\":\"屏幕X坐标\"},\"y\":{\"type\":\"number\",\"description\":\"屏幕Y坐标\"}},\"required\":[\"x\",\"y\"]}") + "," +
                McpTool("pet_mood", "设置桌宠情绪（影响眼睛/腮红/浮动姿态）",
                    "{\"type\":\"object\",\"properties\":{\"mood\":{\"type\":\"string\",\"enum\":[\"happy\",\"shy\",\"curious\",\"sleepy\",\"doze\",\"neutral\"],\"description\":\"happy开心,shy害羞,curious好奇,sleepy犯困,doze打盹,neutral平常\"}},\"required\":[\"mood\"]}") + "," +
                McpTool("pet_pause", "暂停/恢复桌宠自动闲逛（移动等手动指令不受影响）",
                    "{\"type\":\"object\",\"properties\":{\"on\":{\"type\":\"boolean\",\"description\":\"true=暂停闲逛，false=恢复\"}},\"required\":[\"on\"]}") + "," +
                McpTool("pet_face", "让桌宠转身面向镜头（yaw 归零），随后 1.5 秒内不闲逛",
                    "{\"type\":\"object\",\"properties\":{}}") + "," +
                McpTool("pet_status", "查询桌宠状态（待机/行走、屏幕坐标、是否可见）",
                    "{\"type\":\"object\",\"properties\":{}}") + "," +
                McpTool("pet_pose", "连续姿态注入：直接给眼睛开合/腮红/朝向/注视/呼吸（不填的项不改），hold 秒后自动还原。比 pet_mood 精细，用来做「看这边」「歪头」「睁大眼睛」这类活细节",
                    "{\"type\":\"object\",\"properties\":{\"eye\":{\"type\":\"number\",\"description\":\"眼睛开合 0.05~1.4，1=正常\"},\"blush\":{\"type\":\"number\",\"description\":\"腮红 0~2.5，1=情绪默认\"},\"yaw\":{\"type\":\"number\",\"description\":\"朝向角 -24~24（超过五官会贴到球轮廓上），0=正面\"},\"gazeX\":{\"type\":\"number\",\"description\":\"瞳孔左右 -1~1\"},\"gazeY\":{\"type\":\"number\",\"description\":\"瞳孔上下 -1~1\"},\"breathAmp\":{\"type\":\"number\",\"description\":\"呼吸幅度 0~0.2\"},\"breathFreq\":{\"type\":\"number\",\"description\":\"呼吸频率 0.2~6\"},\"hold\":{\"type\":\"number\",\"description\":\"接管秒数，默认6，上限60\"},\"clear\":{\"type\":\"boolean\",\"description\":\"true=立刻放开接管\"}}}") +
                "]}";
        }

        static string McpTool(string name, string desc, string schema)
        {
            return "{\"name\":\"" + name + "\",\"description\":\"" + desc +
                   "\",\"inputSchema\":" + schema + "}";
        }

        string McpToolsCall(string body, out bool isError)
        {
            isError = false;
            string name = ExtractString(body, "name");

            // 参数在 params.arguments 内，把搜索范围切过去
            int ai = body.IndexOf("\"arguments\"", StringComparison.Ordinal);
            string args = ai >= 0 ? body.Substring(ai) : body;

            string text;
            switch (name)
            {
                case "pet_chat":
                    {
                        string t = ExtractString(args, "text");
                        if (t.Length == 0)
                        {
                            isError = true;
                            return McpText("缺少 text 参数");
                        }
                        _pet.Dispatcher.BeginInvoke(new Action(delegate { _pet.ApiChat(t); }));
                        text = "桌宠正在说：" + t;
                        break;
                    }
                case "pet_action":
                    {
                        string act = ExtractString(args, "action");
                        if (!(bool)_pet.Dispatcher.Invoke(new Func<string, bool>(_pet.ApiAction), act))
                        {
                            isError = true;
                            return McpText("未知动作：" + act);
                        }
                        text = "动作已执行：" + act.ToLowerInvariant();
                        break;
                    }
                case "pet_visible":
                    {
                        bool show = ExtractBool(args, "show", true);
                        bool s = show;
                        _pet.Dispatcher.BeginInvoke(new Action(delegate { _pet.ApiSetVisible(s); }));
                        text = show ? "桌宠已显示" : "桌宠已隐藏";
                        break;
                    }
                case "pet_move":
                    {
                        double mx, my;
                        if (!TryExtractNumber(args, "x", out mx) || !TryExtractNumber(args, "y", out my))
                        {
                            isError = true;
                            return McpText("缺少 x/y 参数");
                        }
                        double dx = mx, dy = my;
                        _pet.Dispatcher.BeginInvoke(new Action(delegate { _pet.ApiMoveTo(dx, dy); }));
                        text = "桌宠正在移动到 (" + mx + ", " + my + ")";
                        break;
                    }
                case "pet_mood":
                    {
                        string mood = ExtractString(args, "mood");
                        if (!(bool)_pet.Dispatcher.Invoke(new Func<string, bool>(_pet.SetMood), mood))
                        {
                            isError = true;
                            return McpText("unknown mood: " + mood);
                        }
                        text = "情绪已切换：" + mood.ToLowerInvariant();
                        break;
                    }
                case "pet_pause":
                    {
                        bool on = ExtractBool(args, "on", false);
                        _pet.Dispatcher.Invoke(new Action<bool>(_pet.ApiPause), on);
                        text = on ? "闲逛已暂停，它会老实待着" : "闲逛已恢复";
                        break;
                    }
                case "pet_face":
                    _pet.Dispatcher.Invoke(new Action(() => _pet.FaceFrontCore(false)));
                    text = "已转身面向镜头";
                    break;
                case "pet_pose":
                    {
                        var cmd = new PetWindow.PoseCmd();
                        double v;
                        if (TryExtractNumber(args, "eye", out v)) cmd.Eye = v;
                        if (TryExtractNumber(args, "blush", out v)) cmd.Blush = v;
                        if (TryExtractNumber(args, "yaw", out v)) cmd.Yaw = v;
                        if (TryExtractNumber(args, "gazeX", out v)) cmd.GazeX = v;
                        if (TryExtractNumber(args, "gazeY", out v)) cmd.GazeY = v;
                        if (TryExtractNumber(args, "breathAmp", out v)) cmd.BreathAmp = v;
                        if (TryExtractNumber(args, "breathFreq", out v)) cmd.BreathFreq = v;
                        if (TryExtractNumber(args, "hold", out v)) cmd.Hold = v;
                        cmd.Clear = ExtractBool(args, "clear", false);
                        var c2 = cmd;
                        _pet.Dispatcher.Invoke(new Action(delegate { _pet.ApiPose(c2); }));
                        text = cmd.Clear ? "已放开姿态接管" : "姿态已注入，hold=" + cmd.Hold + "s";
                        break;
                    }
                case "pet_status":
                    text = (string)_pet.Dispatcher.Invoke(new Func<string>(_pet.ApiStatus));
                    break;
                default:
                    isError = true;
                    return McpText("未知工具：" + name);
            }
            return McpText(text);
        }

        static string McpText(string text)
        {
            return "{\"content\":[{\"type\":\"text\",\"text\":\"" + EscapeJson(text) + "\"}],\"isError\":false}";
        }

        static void WriteRpcError(NetworkStream stream, string rpcId, int code, string message)
        {
            var payload = "{\"jsonrpc\":\"2.0\",\"id\":" + rpcId +
                          ",\"error\":{\"code\":" + code + ",\"message\":\"" + message + "\"}}";
            WriteResponse(stream, 200, payload);
        }

        static string EscapeJson(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }


        // ================= 极简 JSON 解析（值提取） =================

        static string ExtractString(string json, string key)
        {
            if (json == null) return "";
            var m = FindValueStart(json, key);
            if (m < 0 || json[m] != '"') return "";
            var sb = new StringBuilder();
            int i = m + 1;
            while (i < json.Length)
            {
                char c = json[i];
                if (c == '\\' && i + 1 < json.Length)
                {
                    char n = json[i + 1];
                    if (n == 'n') sb.Append('\n');
                    else if (n == 't') sb.Append('\t');
                    else sb.Append(n);
                    i += 2;
                    continue;
                }
                if (c == '"') break;
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        static bool TryExtractNumber(string json, string key, out double value)
        {
            value = 0;
            var m = FindValueStart(json, key);
            if (m < 0 || m >= json.Length) return false;
            int end = m;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-' || json[end] == '+' ||
                   json[end] == '.' || json[end] == 'e' || json[end] == 'E'))
                end++;
            return double.TryParse(json.Substring(m, end - m),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        static bool ExtractBool(string json, string key, bool fallback)
        {
            var m = FindValueStart(json, key);
            if (m < 0) return fallback;
            if (json.Substring(m).StartsWith("false", StringComparison.OrdinalIgnoreCase)) return false;
            if (json.Substring(m).StartsWith("true", StringComparison.OrdinalIgnoreCase)) return true;
            return fallback;
        }

        // 返回 "key" 对应值的原始文本（字符串含引号原样保留，数字/布尔取到值结尾）
        static string ExtractRawValue(string json, string key)
        {
            if (json == null) return "";
            int m = FindValueStart(json, key);
            if (m < 0 || m >= json.Length) return "";
            if (json[m] == '"')
            {
                int i = m + 1;
                while (i < json.Length)
                {
                    if (json[i] == '\\') { i += 2; continue; }
                    if (json[i] == '"') return json.Substring(m, i - m + 1);
                    i++;
                }
                return "";
            }
            int end = m;
            while (end < json.Length && json[end] != ',' && json[end] != '}' && json[end] != ' ')
                end++;
            return json.Substring(m, end - m);
        }

        // 返回 "key" 对应值的起始下标
        static int FindValueStart(string json, string key)
        {
            string pattern = "\"" + key + "\"";
            int k = json.IndexOf(pattern, StringComparison.Ordinal);
            if (k < 0) return -1;
            int i = k + pattern.Length;
            while (i < json.Length && (json[i] == ' ' || json[i] == ':')) i++;
            return i;
        }


        static double GetQueryDouble(string query, string key, double fallback)
        {
            foreach (var kv in query.Split('&'))
            {
                int eq = kv.IndexOf('=');
                if (eq <= 0) continue;
                if (string.Equals(kv.Substring(0, eq), key, StringComparison.OrdinalIgnoreCase))
                {
                    double v;
                    if (double.TryParse(kv.Substring(eq + 1), out v)) return v;
                }
            }
            return fallback;
        }

        static string ReadAll(NetworkStream s)
        {
            var ms = new MemoryStream();
            var buf = new byte[4096];
            while (true)
            {
                int n;
                try { n = s.Read(buf, 0, buf.Length); }
                catch { break; }
                if (n <= 0) break;
                ms.Write(buf, 0, n);

                string soFar = Encoding.UTF8.GetString(ms.ToArray());
                int idx = soFar.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (idx >= 0)
                {
                    int contentLen = 0;
                    var headerBlock = soFar.Substring(0, idx);
                    var headerLines = headerBlock.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in headerLines)
                    {
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            int.TryParse(line.Substring(15).Trim(), out contentLen);
                    }
                    if (ms.Length >= idx + 4 + contentLen) break;
                }
                if (ms.Length > 65536) break;
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        static void WriteResponse(NetworkStream s, int code, string body)
        {
            string reason;
            switch (code)
            {
                case 200: reason = "OK"; break;
                case 204: reason = "No Content"; break;
                case 400: reason = "Bad Request"; break;
                case 404: reason = "Not Found"; break;
                case 405: reason = "Method Not Allowed"; break;
                default: reason = "OK"; break;
            }
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(code).Append(' ').Append(reason).Append("\r\n");
            sb.Append("Content-Type: application/json; charset=utf-8\r\n");
            sb.Append("Access-Control-Allow-Origin: *\r\n");
            sb.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
            sb.Append("Access-Control-Allow-Headers: Content-Type\r\n");
            var payload = Encoding.UTF8.GetBytes(body);
            if (code != 204)
                sb.Append("Content-Length: ").Append(payload.Length).Append("\r\n");
            sb.Append("Connection: close\r\n\r\n");

            var head = Encoding.UTF8.GetBytes(sb.ToString());
            s.Write(head, 0, head.Length);
            if (payload.Length > 0) s.Write(payload, 0, payload.Length);
            s.Flush();
        }
    }

    // ============================================================================
    // P0-8 日志：exe 同目录 pet.log（追加写，UTF-8），超 2MB 截断重建；
    // 日志失败绝不影响主流程。
    // ============================================================================
    public static class Log
    {
        static readonly object _lock = new object();
        const long MaxBytes = 2 * 1024 * 1024;

        public static void Write(string msg)
        {
            try
            {
                lock (_lock)
                {
                    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pet.log");
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > MaxBytes)
                        fi.Delete();
                    File.AppendAllText(
                        path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + msg + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
