using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Pa1.Core;

namespace Pa1.App
{
    public partial class MainWindow : Window
    {
        readonly AudioEngine _eng = new AudioEngine();
        readonly ObservableCollection<TrackRow> _rows = new ObservableCollection<TrackRow>();
        readonly PlaylistEngine _pl = new PlaylistEngine();
        string _page = "analyzer";
        string _lastDir;            /* 文件夹选择器起始目录（记住上次位置） */
        int _sortMode;              /* 0 导入序 · 1 标题A-Z · 2 标题Z-A · 3 专辑A-Z · 4 专辑Z-A */
        int _seqCounter;            /* 导入流水号（TrackRow.Seq） */
        bool _restorePending;       /* 曲库恢复读标签中：此刻落盘会把未恢复完的列表冲掉 */
        bool _scrubbing;
        bool _initDone;
        BitmapFrame _iconDark, _iconLight;   /* 主题窗口图标（pack Resource，缓存避免每次换肤重解码） */
        int _mixRate, _mixBits;
        DateTime _mixT = DateTime.MinValue;
        double[] _lo, _hi, _fc;
        int[] _loBin, _hiBin;
        bool[] _dead;
        float _meterDb = (float)AnalyzerMath.DbFloor;
        double[] _peakT = new double[AnalyzerMath.BandN];
        DateTime _frozenAt = DateTime.MinValue;
        bool _frozen;
        Brush _ledOn, _ledOff, _ledClipOn;

        public sealed class TrackRow
        {
            public TrackMeta Meta;
            public int Seq;                 /* 导入序（排序后可还原） */
            /* setter 必须存在：WPF 对只读属性的 TwoWay/OneWayToSource 绑定直接抛异常（点击表格单元格即触发）。
               此处写穿到 Meta；表格本身 IsReadOnly + OneWay 绑定，实际不会走到 set。 */
            public string Title { get { return Meta.Title; } set { Meta.Title = value ?? ""; } }
            public string Artist { get { return Meta.Artist; } set { Meta.Artist = value ?? ""; } }
            public string Album { get { return Meta.Album; } set { Meta.Album = value ?? ""; } }
            public string Spec { get; set; }
        }

        public MainWindow()
        {
            /* 设置先于 InitializeComponent：XAML 属性赋值会触发 Vol_ValueChanged→Persist，
               若此时字典未加载，会把用户设置覆盖成默认值（回忆失效的根因） */
            Settings.Load();

            InitializeComponent();

            /* 透明无边框窗口的最大化需要约束到工作区（否则盖住任务栏）；
               并禁用 Win11 DWM 自动圆角——否则组合器的圆弧裁剪会盖掉 squircle 连续曲率角 */
            SourceInitialized += (ss, ee) =>
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                var src = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
                if (src != null) src.AddHook(MaxSizeHook);
                int pref = 1;   /* DWMWCP_DONOTROUND */
                int hr = DwmSetWindowAttribute(hwnd, 33 /*DWMWA_WINDOW_CORNER_PREFERENCE*/, ref pref, 4);
                QueueDisplayUpdate();
                try
                {
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "gsharpminor-crash.log"),
                        DateTime.Now + " [dwm] corner-pref hr=0x" + hr.ToString("X8") + Environment.NewLine);
                }
                catch { }
            };

            DpiChanged += (ss, ee) => QueueDisplayUpdate();

            /* 排序下拉用字符串 Items（与 DevCombo 设备列表同路） */
            SortCombo.ItemsSource = new[]
                { "Sort · Import", "Sort · Title A-Z", "Sort · Title Z-A", "Sort · Album A-Z", "Sort · Album Z-A" };

            /* 命令行自动化通道：--page=library --theme=light --add=<文件> --exclusive/--shared --dsd=dop|native|pcm --play --no-save */
            bool cliPlay = false;
            foreach (var a in Environment.GetCommandLineArgs())
            {
                if (a == "--no-save") Settings.NoSave = true;
                else if (a == "--exclusive") _cliExclusive = true;
                else if (a == "--shared") _cliShared = true;
                else if (a == "--play") cliPlay = true;
            }

            /* 恢复窗口记忆（高 DPI 下按工作区钳制，避免传输台被裁出屏） */
            var wa = SystemParameters.WorkArea;
            if (Settings.GetDouble("win.left", -1) >= 0)
            {
                Left = Settings.GetDouble("win.left", Left);
                Top = Settings.GetDouble("win.top", Top);
                Width = Settings.GetDouble("win.w", Width);
                Height = Settings.GetDouble("win.h", Height);
            }
            /* 启动时也使用相同的内容下限。 */
            double minW, minH;
            ComputeMinSize(wa.Width, wa.Height, out minW, out minH);
            if (Height > wa.Height) Height = wa.Height - 16;
            if (Width > wa.Width) Width = wa.Width - 16;
            if (Width < minW) Width = minW;
            if (Height < minH) Height = minH;
            if (Left + Width > wa.Right) Left = Math.Max(wa.Left, wa.Right - Width);
            if (Top + Height > wa.Bottom) Top = Math.Max(wa.Top, wa.Bottom - Height);

            _lo = new double[AnalyzerMath.BandN];
            _hi = new double[AnalyzerMath.BandN];
            _fc = new double[AnalyzerMath.BandN];
            AnalyzerMath.BuildBands(_lo, _hi, _fc);

            _eng.StateChanged += () => Dispatcher.BeginInvoke(new Action(SyncUI));
            /* 曲目自然播完 → 自动切下一曲（此前事件链断了：PlaybackStopped 只刷 UI 不推进） */
            _eng.TrackEnded += () => Dispatcher.BeginInvoke(new Action(OnTrackEnded));
            _eng.DevSvc.Changed += () => Dispatcher.BeginInvoke(new Action(() => { RefreshDevList(); SyncUI(); }));

            /* vsync 对齐的渲染循环：≥60fps 且随显示器刷新率自适应（60/75/120/144Hz）。
               DispatcherTimer 会被 Windows 15.6ms 定时器粒度锁死在 ~32fps，弃用。 */
            CompositionTarget.Rendering += OnRenderingTick;

            RefreshDevList();

            /* 恢复主题/音量/独占/播放模式/页签（在 CLI 覆盖前） */
            Theme.Set(Settings.Get("theme", "dark"));
            if (_cliExclusive) _eng.DevSvc.ExclusiveOn = true;
            else if (_cliShared) _eng.DevSvc.ExclusiveOn = false;
            else _eng.DevSvc.ExclusiveOn = Settings.Get("exclusive", "0") == "1";
            /* 恢复 DSD 输出模式（dop/native/pcm，非法值回 dop） */
            string dsdMode = Settings.Get("dsd.mode", "dop");
            _eng.DsdMode = dsdMode == "native" || dsdMode == "pcm" ? dsdMode : "dop";
            /* 恢复响度归一化（norm.on / norm.target） */
            _eng.NormOn = Settings.Get("norm.on", "0") == "1";
            double nt = Settings.GetDouble("norm.target", -18);
            if (nt >= -40 && nt <= -5) _eng.NormTarget = nt;
            _pl.ModeIdx = Settings.GetInt("mode", 0);
            if (_pl.ModeIdx < 0 || _pl.ModeIdx >= PlayMode.All.Length) _pl.ModeIdx = 0;
            /* 恢复频谱量程（只接受 -60/-72/-96 三档） */
            double rf = Settings.GetDouble("dbfloor", -96);
            AnalyzerMath.DbFloor = (rf == -60 || rf == -72) ? rf : -96;
            /* 恢复排序档位（0 导入序 … 4 专辑 Z-A） */
            _sortMode = Settings.GetInt("sort", 0);
            if (_sortMode < 0 || _sortMode > 4) _sortMode = 0;
            if (SortCombo != null) SortCombo.SelectedIndex = _sortMode;
            UpdateThemeBrushes();
            SetPage(Settings.Get("page", "analyzer"));

            VolSlider.Value = Settings.GetDouble("vol", 80);   /* 触发 Vol_ValueChanged */
            SyncUI();

            int cliAdded = 0, cliSkipped = 0;   /* --add 全程累计（多条 --add 不逐条覆盖统计） */
            foreach (var a in Environment.GetCommandLineArgs())
            {
                try
                {
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "gsharpminor-crash.log"),
                        DateTime.Now + " [cli] " + a + Environment.NewLine);
                }
                catch { }
                if (a == "--page=library") SetPage("library");
                else if (a == "--page=analyzer") SetPage("analyzer");
                else if (a == "--theme=light") { Theme.Set("light"); UpdateThemeBrushes(); SyncUI(); SpecView.InvalidateVisual(); }
                else if (a == "--theme=dark") { Theme.Set("dark"); UpdateThemeBrushes(); SyncUI(); SpecView.InvalidateVisual(); }
                else if (a.StartsWith("--vol="))
                {
                    double v;
                    if (double.TryParse(a.Substring(6), out v))
                    {
                        VolSlider.Value = Math.Max(0, Math.Min(100, v));
                        _eng.SetVolume((float)(VolSlider.Value / 100.0));
                    }
                }
                else if (a.StartsWith("--sort="))
                {
                    string sv = a.Substring(7).ToLowerInvariant();
                    _sortMode = sv == "title-az" ? 1 : sv == "title-za" ? 2
                              : sv == "album-az" ? 3 : sv == "album-za" ? 4 : 0;
                    if (SortCombo != null) SortCombo.SelectedIndex = _sortMode;
                }
                else if (a == "--norm") _eng.SetNorm(true);
                else if (a.StartsWith("--norm-target="))
                {
                    double nt2;
                    if (double.TryParse(a.Substring(14), NumberStyles.Float, CultureInfo.InvariantCulture, out nt2)
                        && nt2 >= -40 && nt2 <= -5) _eng.NormTarget = nt2;
                }
                else if (a.StartsWith("--dsd="))
                {
                    string dv = a.Substring(6).ToLowerInvariant();
                    _eng.SetDsdMode(dv == "native" ? "native" : dv == "pcm" ? "pcm" : "dop");
                }
                else if (a.StartsWith("--add="))
                {
                    string p = a.Substring(6);
                    var batch = new List<string>();
                    try
                    {
                        if (Directory.Exists(p)) ScanAudioDir(p, batch);   /* CLI 亦可整目录批量 */
                        else if (File.Exists(p)) batch.Add(p);
                    }
                    catch { }
                    foreach (var f in batch)
                    {
                        try
                        {
                            var meta = TagReader.Read(f);
                            if (meta.Spec != null)
                            {
                                if (AddTrackIfNew(meta, f)) cliAdded++;
                                else cliSkipped++;   /* 去重校验：已有/批内重复 */
                            }
                        }
                        catch { }
                    }
                    TrackList.ItemsSource = _rows;
                    PlStats.Text = _rows.Count + " TRACKS"
                        + (cliAdded > 0 ? " · +" + cliAdded + " ADDED" : "")
                        + (cliSkipped > 0 ? " · " + cliSkipped + " SKIPPED" : "");
                    try
                    {
                        File.AppendAllText(Path.Combine(Path.GetTempPath(), "gsharpminor-crash.log"),
                            DateTime.Now + " [add-cli] dir=" + p + " batch=" + batch.Count
                            + " total +" + cliAdded + " added, " + cliSkipped + " skipped, total=" + _rows.Count
                            + Environment.NewLine);
                    }
                    catch { }
                }
            }
            ApplySort(_sortMode);          /* CLI --add 同步路径：立即按档重排 */
            /* 曲库记忆：重启自动恢复上次导入（--no-save 测试通道不恢复，防污染） */
            if (!Settings.NoSave) RestoreTracks();
            /* CLI --add 同步路径也要落盘（GUI 走 AddFilesAsync 完成回调）；
               恢复在途时不存——完成回调会合并后统一存 */
            if (!Settings.NoSave && !_restorePending && _rows.Count > 0) SaveTracks();
            if (cliPlay && _rows.Count > 0) PlayIndex(0);
            _initDone = true;
            Persist();   /* 构造完成即固化一次（覆盖 taskkill 等非优雅退出） */
        }

        bool _cliExclusive;
        bool _cliShared;

        void Persist()
        {
            if (!_initDone) return;   /* 构造期间（XAML 属性事件）不落盘，防默认值覆盖用户设置 */
            if (VolSlider == null || _eng == null || _pl == null) return;
            Settings.Set("theme", Theme.Cur.Name);
            Settings.Set("dbfloor", AnalyzerMath.DbFloor.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Settings.Set("exclusive", _eng.DevSvc.ExclusiveOn ? "1" : "0");
            Settings.Set("dsd.mode", _eng.DsdMode);
            Settings.Set("norm.on", _eng.NormOn ? "1" : "0");
            Settings.Set("norm.target", _eng.NormTarget.ToString(CultureInfo.InvariantCulture));
            Settings.Set("mode", _pl.ModeIdx.ToString());
            Settings.Set("page", _page);
            Settings.Set("vol", ((int)VolSlider.Value).ToString());
            /* 窗口几何：WPF 在 Show 前 Left/Top 为 NaN，勿落盘污染 */
            if (!double.IsNaN(Left) && !double.IsNaN(Top) && !double.IsNaN(Width) && !double.IsNaN(Height))
            {
                Settings.Set("win.left", ((int)Left).ToString());
                Settings.Set("win.top", ((int)Top).ToString());
                Settings.Set("win.w", ((int)Width).ToString());
                Settings.Set("win.h", ((int)Height).ToString());
            }
            Settings.Save();
        }

        void UpdateThemeBrushes()
        {
            var T = Theme.Cur;
            SetRes("BgBrush", T.WBg);
            SetRes("PanelBrush", T.WPanel);
            SetRes("Panel2Brush", T.WPanel2);
            SetRes("LineBrush", T.WLine);
            SetRes("TextBrush", T.WText);
            SetRes("MutedBrush", T.WMuted);
            SetRes("WinBtnBrush", T.WWnd);
            SetRes("TopBarBrush", T.WTopBar);
            SetRes("PhosBrush", T.WPhos);
            SetRes("PhosDimBrush", T.WPhosDim);
            SetRes("AmberBrush", T.WAmber);
            /* 滚动条 thumb：Light=CLIP 琥珀、Dark=原磷光绿（用户指定，Dark 不改色） */
            SetRes("ScrollThumbBrush", ReferenceEquals(T, ThemePal.Dark) ? T.WPhosDim : T.WAmber);
            SetRes("TrackBgBrush", T.WTrackBg);
            SetRes("HoverBrush", Color.FromArgb(26, T.WPhos.R, T.WPhos.G, T.WPhos.B));
            SetRes("SelBrush", Color.FromArgb(76, T.WPhos.R, T.WPhos.G, T.WPhos.B));
            _ledOn = new SolidColorBrush(T.WPhos);
            _ledOff = new SolidColorBrush(T.WTrackBg);
            _ledClipOn = new SolidColorBrush(T.WAmber);
            /* 辉光效果：换新实例（Freezable 可能被渲染管线冻结，改 Color 会抛只读异常） */
            BrandGlow = NewGlow(BrandGlow, 22, 0.5, T.WPhos);
            BrandText.Effect = BrandGlow;
            GlowSig = NewGlow(GlowSig, 10, GlowSig != null ? GlowSig.Opacity : 0, T.WPhos);
            LedSignal.Effect = GlowSig;
            GlowClip = NewGlow(GlowClip, 10, GlowClip != null ? GlowClip.Opacity : 0, T.WAmber);
            LedClip.Effect = GlowClip;
            /* 电平表渐变：磷光绿 → 琥珀（高电平警示） */
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            g.GradientStops.Add(new GradientStop(T.WPhosDim, 0));
            g.GradientStops.Add(new GradientStop(T.WPhos, 0.72));
            g.GradientStops.Add(new GradientStop(T.WAmber, 1));
            MeterFill.Fill = g;
            /* 窗口图标（任务栏/Alt-Tab）随主题：Dark=黑金谱号 / Light=米绿谱号 */
            if (_iconDark == null)
            {
                _iconDark = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/icon-dark.png"));
                _iconLight = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/icon-light.png"));
            }
            Icon = ReferenceEquals(T, ThemePal.Dark) ? _iconDark : _iconLight;
        }

        void SetRes(string key, Color c)
        {
            /* 画刷可能被样式/渲染管线冻结（Pen、模板封存）——改 Color 会抛「只读状态」异常。
               直接换新实例；XAML 均为 DynamicResource，会自动重解析到新画刷。 */
            Resources[key] = new SolidColorBrush(c);
        }

        static DropShadowEffect NewGlow(DropShadowEffect old, double blur, double opacity, Color c)
        {
            return new DropShadowEffect { BlurRadius = blur, ShadowDepth = 0, Opacity = opacity, Color = c };
        }

        void RefreshDevList()
        {
            DevCombo.ItemsSource = _eng.DevSvc.Devices.Select(d => d.Label).ToList();
            DevCombo.SelectedIndex = Math.Max(0, _eng.DevSvc.Devices.IndexOf(_eng.DevSvc.Selected));
        }

        /* ════════ 分析节拍（vsync 驱动，帧率随显示器刷新率自适应） ════════ */
        TimeSpan _lastRT = TimeSpan.MinValue;
        double _frameDt = 0.016;
        int _tickCount;
        DateTime _beatStart = DateTime.MinValue;
        int _beatTicks;

        void OnRenderingTick(object sender, EventArgs e)
        {
            var rt = (e as RenderingEventArgs)?.RenderingTime ?? TimeSpan.FromTicks(DateTime.Now.Ticks);
            if (_beatStart == DateTime.MinValue) _beatStart = DateTime.Now;
            double dt = _lastRT == TimeSpan.MinValue ? 0.016 : (rt - _lastRT).TotalSeconds;
            _lastRT = rt;
            if (dt <= 0 || dt > 0.25) dt = 0.016;
            _frameDt = dt;
            Tick();
        }

        void Tick()
        {
            try
            {
                TickCore();
                _beatTicks++;
                if (++_tickCount % 200 == 0)   /* 每 ~5s 心跳（含实测 FPS） */
                {
                    double fps = 0;
                    if (_beatStart != DateTime.MinValue)
                    {
                        double sec = (DateTime.Now - _beatStart).TotalSeconds;
                        if (sec > 0.1) fps = _beatTicks / sec;
                    }
                    _beatStart = DateTime.Now;
                  _beatStart = DateTime.Now;
                _beatTicks = 0;
                /* 内存/GC 诊断：验证是否存在「越用越卡」的泄漏（堆/Gen2/LOH 应平稳） */
                long gcMb, wsMb; int g0, g1, g2;
                try
                {
                    gcMb = GC.GetTotalMemory(false) >> 20;
                    g0 = GC.CollectionCount(0); g1 = GC.CollectionCount(1); g2 = GC.CollectionCount(2);
                    wsMb = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 >> 20;
                }
                catch { gcMb = wsMb = -1; g0 = g1 = g2 = -1; }
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "gsharpminor-crash.log"),
                    DateTime.Now + " [beat] ticks=" + _tickCount + " fps=" + fps.ToString("0.0")
                    + " mem=" + gcMb + "M ws=" + wsMb + "M g0=" + g0 + " g1=" + g1 + " g2=" + g2 + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                try
                {
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "gsharpminor-crash.log"),
                        DateTime.Now + " [tick] " + ex + Environment.NewLine);
                }
                catch { }
            }
        }

        void TickCore()
        {
            var now = DateTime.UtcNow;
            double dt = _frameDt;   /* vsync 帧距（RenderingTime 测得），平滑常数随刷新率自适应 */
            if (dt <= 0 || dt > 0.25) dt = 0.016;

            /* Analyze 已在后台线程跑（AudioEngine.AnalyzeLoop）——UI 只读结果，不再做 FFT */
            var fd = _eng.FreqDb;

            /* 混音格式缓存 2s（GetMixFormat 是 COM 调用，不该每帧走） */
            if (_mixRate == 0 || (now - _mixT).TotalSeconds > 2)
            {
                _eng.DevSvc.GetMixFormat(out _mixRate, out _mixBits);
                _mixT = now;
            }
            int devRate = _mixRate, devBits = _mixBits;
            double binHz = (_eng.FftRate > 0 ? _eng.FftRate : devRate) / (double)_eng.FftSize;
            if (_loBin == null)
            {
                _loBin = new int[AnalyzerMath.BandN];
                _hiBin = new int[AnalyzerMath.BandN];
                _dead = new bool[AnalyzerMath.BandN];
            }
            double nyqBin = fd.Length - 1;
            for (int i = 0; i < AnalyzerMath.BandN; i++)
            {
                _dead[i] = _lo[i] / binHz >= nyqBin;
                _loBin[i] = Math.Max(1, (int)(_lo[i] / binHz));
                _hiBin[i] = (int)Math.Min(nyqBin, _hi[i] / binHz);
            }

            /* 电平表 */
            double rmsDb = 20 * Math.Log10(_eng.Rms + 1e-9);
            double kMA = 1 - Math.Pow(1 - 0.50, dt * 60), kMR = 1 - Math.Pow(1 - 0.10, dt * 60);
            _meterDb += (float)((rmsDb - _meterDb) * (rmsDb > _meterDb ? kMA : kMR));
            double lv = Math.Max(0, Math.Min(1, (_meterDb + 54) / 54));
            MeterFill.Width = lv * MeterTrack.ActualWidth;
            bool sig = _eng.Rms > 0.012, clip = _eng.Clipped;
            LedSignal.Fill = sig ? _ledOn : _ledOff;
            LedClip.Fill = clip ? _ledClipOn : _ledOff;
            GlowSig.Opacity = sig ? 0.85 : 0.0;
            GlowClip.Opacity = clip ? 0.85 : 0.0;

            /* 频段平滑 + 峰值保持 */
            double kAtk = 1 - Math.Pow(1 - 0.55, dt * 60), kRel = 1 - Math.Pow(1 - 0.12, dt * 60);
            double nowMs = now.Subtract(new DateTime(1970, 1, 1)).TotalMilliseconds;
            var disp = SpecView.Disp;
            var peak = SpecView.Peak;
            for (int i = 0; i < AnalyzerMath.BandN; i++)
            {
                double target = _dead[i] ? AnalyzerMath.DbFloor
                    : Math.Max(AnalyzerMath.DbFloor, Math.Min(AnalyzerMath.DbCeil,
                        AnalyzerMath.BandValue(fd, _loBin[i], _hiBin[i], "max", _eng.CalOffset)));
                disp[i] += (float)((target - disp[i]) * (target > disp[i] ? kAtk : kRel));
                if (disp[i] >= peak[i]) { peak[i] = disp[i]; _peakT[i] = nowMs; }
                else if (nowMs - _peakT[i] > 5000)
                    peak[i] = Math.Max(disp[i], peak[i] - 20 * (float)dt);
            }
            SpecView.HasContent = _eng.IsLoaded || _eng.Rms > 0.001;
            SpecView.InvalidateVisual();

            /* 进度：几何每帧更新；文本只在内容变化时写（每帧拼串 + TextBlock 重排曾是 GC/渲染churn） */
            if (!_scrubbing && _eng.IsLoaded)
            {
                double pos = _eng.Position, len = _eng.Length;
                double ratio = len > 0 ? pos / len : 0;
                ScrubHead.X1 = ScrubHead.X2 = ratio * ScrubTrack.ActualWidth;
                ScrubFill.Width = ratio * ScrubTrack.ActualWidth;
                string ts = Fmt(pos), tl = Fmt(len);
                if (ts != _lastTimeNow) { TimeNow.Text = ts; _lastTimeNow = ts; }
                if (tl != _lastTimeTotal) { TimeTotal.Text = tl; _lastTimeTotal = tl; }
                if (ts != _lastSubT || tl != _lastSubL)
                {
                    NpSub.Text = MetaText() + " · " + ts + " / " + tl;
                    _lastSubT = ts; _lastSubL = tl;
                }
            }
        }

        string _lastTimeNow, _lastTimeTotal, _lastSubT, _lastSubL;

        static string Fmt(double sec)
        {
            if (double.IsNaN(sec) || double.IsInfinity(sec) || sec < 0) sec = 0;
            return ((int)sec / 60) + ":" + ((int)sec % 60).ToString("D2");
        }

        string MetaText()
        {
            if (_eng.Meta == null) return "NO DISC";
            var m = _eng.Meta;
            var sp = m.Spec != null ? FormatProbe.SpecLabel(m.Spec) : "";
            return (m.Artist + " — " + m.Title + "  ·  " + sp).Trim(' ', '·');
        }

        /* ════════ UI 同步 ════════ */
        void SyncUI()
        {
            var T = Theme.Cur;
            PageA.Background = Brushes.Transparent;
            NpName.Text = _eng.Meta != null ? _eng.Meta.Title : "— NO DISC —";
            NpNameB.Text = NpName.Text;
            NpSub.Text = MetaText();
            bool playing = _eng.IsPlaying;
            BtnPlay.Content = playing ? "❚❚ Pause" : "▶ Play";
            BtnPlayLibrary.Content = playing ? "❚❚" : "▶";

            string planLbl = _eng.Plan != null ? _eng.Plan.Label : "—";
            string excl = _eng.DevSvc.ExclusiveOn ? "EXCL" : "SHARED";
            string devName = _eng.DevSvc.Selected != null ? _eng.DevSvc.Selected.Label : "DEFAULT";
            if (_eng.DevSvc.ExclusiveOn && _eng.Error != null && _eng.Error.StartsWith("EXCLUSIVE FAILED"))
                excl = "EXCL · FALLBACK";
            ChainReadout.Text = "OUT · " + excl + " · " + planLbl + " · DEV " + devName;
            if (_eng.NormOn && _eng.NormSource == "N/A")
                ChainReadout.Text += " · NORM N/A (bitstream)";
            else if (_eng.NormOn && _eng.NormSource != "-")
                ChainReadout.Text += " · NORM " + _eng.NormGainDb.ToString("+0.0;-0.0;0.0")
                    + "dB " + _eng.NormSource;

            BtnExclusive.Content = _eng.DevSvc.ExclusiveOn ? "Exclusive · ON" : "Exclusive · OFF";
            BtnDsdOut.Content = _eng.DsdMode == "native" ? "DSD · Native"
                              : _eng.DsdMode == "pcm" ? "DSD · PCM" : "DSD · DoP";
            BtnNorm.Content = _eng.NormOn ? "NORM · ON" : "NORM · OFF";
            /* 主题字样：显示当前主题；Light=CLIP 琥珀色，Dark=SIG 磷光绿 */
            bool isDark = ReferenceEquals(T, ThemePal.Dark);
            BtnTheme.Content = isDark ? "Dark" : "Light";
            BtnTheme.SetResourceReference(ForegroundProperty, isDark ? "PhosBrush" : "AmberBrush");
            BtnMode.Content = _pl.Mode.Label;
            BtnRange.Content = "Range · " + (int)AnalyzerMath.DbFloor + "dB";

            if (_eng.Error != null) ChainReadout.Text += "  ·  " + _eng.Error;
        }

        void SetPage(string p)
        {
            _page = p;
            PageA.Visibility = p == "analyzer" ? Visibility.Visible : Visibility.Collapsed;
            PageB.Visibility = p == "library" ? Visibility.Visible : Visibility.Collapsed;
            /* SetResourceReference 走动态资源：换肤时随 PhosBrush/MutedBrush 自动更新
               （旧实现 Brush() 一次性快照 → 切主题后页签选中色停留在旧主题） */
            TabAnalyzer.SetResourceReference(ForegroundProperty, p == "analyzer" ? "PhosBrush" : "MutedBrush");
            TabLibrary.SetResourceReference(ForegroundProperty, p == "library" ? "PhosBrush" : "MutedBrush");
            Persist();
        }

        /* ════════ 交互 ════════ */
        void Tab_Click(object s, RoutedEventArgs e) { SetPage(s == TabAnalyzer ? "analyzer" : "library"); }

        void BtnTheme_Click(object s, RoutedEventArgs e)
        {
            Theme.Toggle();
            UpdateThemeBrushes();
            SyncUI();
            SpecView.InvalidateVisual();
            Persist();
        }

        /* ════════ 频谱量程：-60 / -72 / -96 三档循环（-96 = 16bit 理论底噪） ════════ */
        static readonly double[] DbRanges = { -60, -72, -96 };

        void BtnRange_Click(object s, RoutedEventArgs e)
        {
            int idx = System.Array.IndexOf(DbRanges, AnalyzerMath.DbFloor);
            AnalyzerMath.DbFloor = DbRanges[(idx + 1) % DbRanges.Length];
            /* 抬档（-96→-60）时把低于新下限的柱条/峰值立即钳下来，防画到基线以下 */
            float f = (float)AnalyzerMath.DbFloor;
            var d = SpecView.Disp; var pk = SpecView.Peak;
            for (int i = 0; i < d.Length; i++) { if (d[i] < f) d[i] = f; if (pk[i] < f) pk[i] = f; }
            SpecView.InvalidateVisual();
            SyncUI();
            Persist();
        }

        void BtnPlay_Click(object s, RoutedEventArgs e) { if (_loading) return; _eng.Toggle(); SyncUI(); }
        void BtnStop_Click(object s, RoutedEventArgs e) { if (_loading) return; _eng.Stop(); _eng.Position = 0; SyncUI(); }

        void BtnPrev_Click(object s, RoutedEventArgs e) { Step(-1, false); }
        void BtnNext_Click(object s, RoutedEventArgs e) { Step(1, false); }

        void OnTrackEnded()
        {
            if (!_initDone || _rows.Count == 0) return;
            Step(1, true);   /* 自动推进：PickNext 按播放模式给下一曲（顺序到尾返回 -1 → 停） */
        }

        void Step(int dir, bool auto)
        {
            if (_loading) return;
            int idx = _pl.PickNext(dir, auto);
            if (idx >= 0 && idx < _rows.Count) PlayIndex(idx);
            else { _eng.Stop(); SyncUI(); }
        }

        void PlayIndex(int idx)
        {
            if (idx < 0 || idx >= _rows.Count) return;
            _pl.CurIdx = idx;
            var meta = _rows[idx].Meta;
            string path = FindPath(meta);
            RefreshNowPlaying();
            int gen = ++_loadGen;
            _loading = true;
            NpSub.Text = "LOADING · " + meta.Title + " …";
            /* 解码（尤其 DSD 全量内存解码）严禁在 UI 线程执行——大曲目直接「未响应」 */
            System.Threading.Tasks.Task.Run(() =>
            {
                try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "gsharpminor-crash.log"), DateTime.Now + " [load] begin gen=" + gen + " title=" + meta.Title + Environment.NewLine); } catch { }
                bool ok = false;
                try
                {
                    lock (_engGate) { ok = _eng.LoadFile(path); if (ok) _eng.Play(); }
                }
                catch (Exception ex) { _eng.Error = ex.Message; }
                try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "gsharpminor-crash.log"), DateTime.Now + " [load] end gen=" + gen + " ok=" + ok + Environment.NewLine); } catch { }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (gen == _loadGen) _loading = false;   /* 过期任务不解除忙态 */
                    if (gen != _loadGen) return;             /* 已被更新的加载请求取代 */
                    SyncUI();
                }));
            });
        }

        int _loadGen;
        volatile bool _loading;
        readonly object _engGate = new object();

        string FindPath(TrackMeta m) { return _paths.ContainsKey(m) ? _paths[m] : ""; }
        readonly Dictionary<TrackMeta, string> _paths = new Dictionary<TrackMeta, string>();
        readonly HashSet<string> _pathKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase); /* 规范化路径键：添加去重校验 */

        /// <summary>入库一条曲目并登记路径键；键已存在（重复添加）则拒绝并返回 false。</summary>
        bool AddTrackIfNew(TrackMeta meta, string path)
        {
            string k = AddGuard.Key(path);
            if (k.Length == 0 || !_pathKeys.Add(k)) return false;
            _paths[meta] = path;
            _rows.Add(new TrackRow { Meta = meta, Seq = _seqCounter++, Spec = FormatProbe.SpecLabel(meta.Spec) });
            _pl.Tracks.Add(meta);
            return true;
        }

        void RefreshNowPlaying()
        {
            if (_pl.CurIdx >= 0 && _pl.CurIdx < _rows.Count)
            {
                NpName.Text = _rows[_pl.CurIdx].Title;
                NpNameB.Text = NpName.Text;
                TrackList.SelectedIndex = _pl.CurIdx;
            }
        }

        void BtnMode_Click(object s, RoutedEventArgs e)
        {
            _pl.CycleMode();
            BtnMode.Content = _pl.Mode.Label;
            BtnMode.ToolTip = _pl.Mode.Title;
            Persist();
        }

        void BtnExclusive_Click(object s, RoutedEventArgs e)
        {
            if (_loading) return;
            _eng.SetExclusive(!_eng.DevSvc.ExclusiveOn);
            SyncUI();
            Persist();
        }

        /// <summary>DSD 输出模式循环：DoP → Native → PCM（带进度重载当前曲目，由 SetDsdMode 完成）。</summary>
        void BtnDsdOut_Click(object s, RoutedEventArgs e)
        {
            if (_loading) return;
            string next = _eng.DsdMode == "dop" ? "native" : _eng.DsdMode == "native" ? "pcm" : "dop";
            _eng.SetDsdMode(next);
            SyncUI();
            Persist();
        }

        /// <summary>响度归一化开关（即时生效不重载；增益来源 TAG/SCAN/CACHE 显示于 OUT 读数）。</summary>
        void BtnNorm_Click(object s, RoutedEventArgs e)
        {
            _eng.SetNorm(!_eng.NormOn);
            SyncUI();
            Persist();
        }

        void DevCombo_SelectionChanged(object s, SelectionChangedEventArgs e)
        {
            if (DevCombo.SelectedIndex < 0) return;
            var devs = _eng.DevSvc.Devices;
            if (DevCombo.SelectedIndex < devs.Count)
            {
                _eng.DevSvc.Selected = devs[DevCombo.SelectedIndex];
                if (!_loading && _eng.IsLoaded) { _eng.Stop(); _eng.Play(); }
                SyncUI();
            }
        }

        void BtnAdd_Click(object s, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Multiselect = true,
                Filter = "Audio|*.wav;*.flac;*.dsf;*.dff;*.mp3;*.ogg;*.opus;*.aiff;*.aif;*.m4a|All|*.*",
            };
            if (dlg.ShowDialog() != true) return;
            AddFilesAsync(dlg.FileNames);
        }

        void BtnAddFolder_Click(object s, RoutedEventArgs e)
        {
            var dirs = FolderPick.PickFolders(_lastDir);
            if (dirs == null || dirs.Count == 0) return;
            _lastDir = dirs[dirs.Count - 1];   /* 记住最后选中的目录作下次起始位 */
            BtnAdd.Content = "Scanning…";
            AddFilesAsync(dirs);               /* 目录在后台线程递归展开，大文件夹不卡 UI */
        }

        /* ════════ 自绘标题栏：窗口控制 + 拖动/双击最大化 ════════ */

        /* WM_GETMINMAXINFO：透明无边框窗口默认最大化到整个屏幕（盖住任务栏），
           手工把最大化矩形钳到显示器工作区；并按当前显示器工作区写入
           ptMinTrackSize ——handled=true 会跳过 WPF 对 MinWidth/MinHeight 的
           默认处理，此前下限形同虚设（窗口可拖到 585px 宽，页签/排序列/页脚被裁）。 */
        IntPtr MaxSizeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_GETMINMAXINFO = 0x0024;
            const int WM_DISPLAYCHANGE = 0x007E;
            const int WM_DPICHANGED = 0x02E0;
            if (msg == WM_DISPLAYCHANGE || msg == WM_DPICHANGED)
            {
                QueueDisplayUpdate(); // DPI 消息交给 WPF，待其处理建议矩形后再校正。
            }
            else if (msg == WM_GETMINMAXINFO)
            {
                var mmi = (MINMAXINFO)System.Runtime.InteropServices.Marshal.PtrToStructure(
                    lParam, typeof(MINMAXINFO));
                IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (mon != IntPtr.Zero)
                {
                    var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(MONITORINFO)) };
                    if (GetMonitorInfo(mon, ref mi))
                    {
                        mmi.ptMaxPosition.x = mi.rcWork.left - mi.rcMonitor.left;
                        mmi.ptMaxPosition.y = mi.rcWork.top - mi.rcMonitor.top;
                        mmi.ptMaxSize.x = mi.rcWork.right - mi.rcWork.left;
                        mmi.ptMaxSize.y = mi.rcWork.bottom - mi.rcWork.top;
                        /* 最小拖放尺寸 = 内容下限（DIP）× 当前窗口 DPI（ptMinTrackSize 是物理像素） */
                        double dpi = DpiOf(hwnd);
                        double minW, minH;
                        ComputeMinSize((mi.rcWork.right - mi.rcWork.left) / dpi,
                                       (mi.rcWork.bottom - mi.rcWork.top) / dpi, out minW, out minH);
                        mmi.ptMinTrackSize.x = (int)Math.Round(minW * dpi);
                        mmi.ptMinTrackSize.y = (int)Math.Round(minH * dpi);
                        System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, true);
                        handled = true;
                    }
                }
            }
            return IntPtr.Zero;
        }

        // DIP 已包含系统缩放。更大的像素工作区不应抬高用户可拖动的下限。
        internal static void ComputeMinSize(double workW, double workH, out double w, out double h)
        {
            w = Math.Min(900, Math.Max(1, workW - 16));
            h = Math.Min(620, Math.Max(1, workH - 16));
        }

        double DpiOf(IntPtr hwnd)
        {
            try
            {
                uint d = GetDpiForWindow(hwnd);
                if (d > 0) return d / 96.0;
            }
            catch { }
            return 1.0;
        }

        bool _displayUpdateQueued;
        bool _fitOnRestore;

        void QueueDisplayUpdate()
        {
            if (_displayUpdateQueued) return;
            _displayUpdateQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                _displayUpdateQueued = false;
                ApplyDisplayBounds();
            }));
        }

        void ApplyDisplayBounds()
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            if (WindowState == WindowState.Minimized)
            {
                _fitOnRestore = true;
                return;
            }

            var mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(MONITORINFO)) };
            if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref mi)) return;
            var area = mi.rcWork;
            int width = area.right - area.left, height = area.bottom - area.top;
            if (width <= 0 || height <= 0) return;
            double dpi = DpiOf(hwnd);
            double minW, minH;
            ComputeMinSize(width / dpi, height / dpi, out minW, out minH);
            MinWidth = minW;
            MinHeight = minH;

            RECT current;
            if (!GetWindowRect(hwnd, out current)) return;
            var target = area;
            _fitOnRestore = WindowState == WindowState.Maximized;
            if (!_fitOnRestore)
                target = FitNormalBounds(current, area, (int)Math.Round(minW * dpi), (int)Math.Round(minH * dpi));

            if (current.left != target.left || current.top != target.top ||
                current.right != target.right || current.bottom != target.bottom)
                SetWindowPos(hwnd, IntPtr.Zero, target.left, target.top,
                    target.right - target.left, target.bottom - target.top, SWP_NOACTIVATE | SWP_NOZORDER);
        }

        // 全程使用屏幕物理像素，支持位于主屏左侧或上方的显示器。
        static RECT FitNormalBounds(RECT current, RECT area, int minWidth, int minHeight)
        {
            int width = Math.Min(area.right - area.left, Math.Max(minWidth, current.right - current.left));
            int height = Math.Min(area.bottom - area.top, Math.Max(minHeight, current.bottom - current.top));
            int left = Math.Max(area.left, Math.Min(current.left, area.right - width));
            int top = Math.Max(area.top, Math.Min(current.top, area.bottom - height));
            return new RECT { left = left, top = top, right = left + width, bottom = top + height };
        }

        const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct POINTAPI { public int x, y; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct MINMAXINFO
        {
            public POINTAPI ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
        }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct RECT { public int left, top, right, bottom; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor, rcWork;
            public int dwFlags;
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool GetMonitorInfo(IntPtr hmon, ref MONITORINFO mi);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern uint GetDpiForWindow(IntPtr hwnd);
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
        const uint MONITOR_DEFAULTTONEAREST = 2;

        void BtnMinimize_Click(object s, RoutedEventArgs e) { WindowState = WindowState.Minimized; }
        void BtnClose_Click(object s, RoutedEventArgs e) { Close(); }
        void BtnMaximize_Click(object s, RoutedEventArgs e) { ToggleMaximize(); }

        void ToggleMaximize()
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        void Win_StateChanged(object s, EventArgs e)
        {
            if (_fitOnRestore && WindowState != WindowState.Minimized) QueueDisplayUpdate();
            if (BtnMaximize == null) return;
            bool max = WindowState == WindowState.Maximized;
            BtnMaximize.Content = max ? "❐" : "□";
            BtnMaximize.ToolTip = max ? "还原" : "最大化";
            /* 最大化填满工作区 → 圆角归零（保留会露出屏幕外的角） */
            if (WinBg != null) WinBg.CornerRadius = new CornerRadius(max ? 0 : 10);
        }

        void TitleBar_MouseLeftButtonDown(object s, MouseButtonEventArgs e)
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            /* 页签/主题/窗口按钮自身处理点击，不能被标题栏拖动截获 */
            var d = e.OriginalSource as DependencyObject;
            while (d != null && d != TitleBar)
            {
                if (d is ButtonBase) return;
                d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
            }
            e.Handled = true;
            if (e.ClickCount == 2) { ToggleMaximize(); return; }
            if (WindowState == WindowState.Maximized) return;
            try { DragMove(); } catch (InvalidOperationException) { }
        }

        /* 拖放：文件与文件夹皆可（文件夹递归扫音频） */
        void Win_DragOver(object s, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        void Win_Drop(object s, DragEventArgs e)
        {
            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths == null || paths.Length == 0) return;
            AddFilesAsync(paths);   /* 文件与文件夹皆可（目录后台递归展开） */
        }

        static readonly HashSet<string> AudioExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".wav", ".flac", ".dsf", ".dff", ".mp3", ".ogg", ".opus", ".aiff", ".aif", ".m4a" };

        void ScanAudioDir(string dir, List<string> acc)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                    if (AudioExt.Contains(Path.GetExtension(f))) acc.Add(f);
                foreach (var sub in Directory.EnumerateDirectories(dir))
                    ScanAudioDir(sub, acc);
            }
            catch { }   /* 无权限/网络盘子目录跳过，不整批失败 */
        }

        /* ════════ 排序：歌名/专辑 × 正倒序（A-Z / Z-A 四档分开）+ 导入序还原 ════════ */
        void SortCombo_SelectionChanged(object s, SelectionChangedEventArgs e)
        {
            if (SortCombo == null) return;
            _sortMode = Math.Max(0, SortCombo.SelectedIndex);
            if (!_initDone) return;
            ApplySort(_sortMode);
            SaveTracks();
        }

        void ApplySort(int mode)
        {
            if (_rows.Count < 2) return;
            var cur = (_pl.CurIdx >= 0 && _pl.CurIdx < _rows.Count) ? _rows[_pl.CurIdx] : null;
            var indexed = _rows.Select((r, i) => new { r, i }).ToList();
            List<TrackRow> ordered;
            switch (mode)
            {
                case 1:  /* 歌名 A-Z */
                    ordered = indexed.OrderBy(x => x.r.Title ?? "", StringComparer.CurrentCultureIgnoreCase)
                                     .ThenBy(x => x.i).Select(x => x.r).ToList(); break;
                case 2:  /* 歌名 Z-A */
                    ordered = indexed.OrderByDescending(x => x.r.Title ?? "", StringComparer.CurrentCultureIgnoreCase)
                                     .ThenBy(x => x.i).Select(x => x.r).ToList(); break;
                case 3:  /* 专辑 A-Z：专辑升序；专辑内按曲目序号 1→N（无序号排专辑尾） */
                    ordered = indexed.OrderBy(x => x.r.Album ?? "", StringComparer.CurrentCultureIgnoreCase)
                                     .ThenBy(x => x.r.Meta.TrackNo > 0 ? 0 : 1)
                                     .ThenBy(x => x.r.Meta.TrackNo)
                                     .ThenBy(x => x.i).Select(x => x.r).ToList(); break;
                case 4:  /* 专辑 Z-A：专辑降序；专辑内按曲目序号 N→1（无序号仍排专辑尾） */
                    ordered = indexed.OrderByDescending(x => x.r.Album ?? "", StringComparer.CurrentCultureIgnoreCase)
                                     .ThenBy(x => x.r.Meta.TrackNo > 0 ? 0 : 1)
                                     .ThenByDescending(x => x.r.Meta.TrackNo)
                                     .ThenBy(x => x.i).Select(x => x.r).ToList(); break;
                default: /* 导入序 */
                    ordered = indexed.OrderBy(x => x.r.Seq).ThenBy(x => x.i).Select(x => x.r).ToList(); break;
            }
            for (int i = 0; i < ordered.Count; i++)
            {
                int j = _rows.IndexOf(ordered[i]);
                if (j != i) _rows.Move(j, i);
            }
            /* _pl.Tracks 与 _rows 同行同步重排（PickNext 按同一索引空间推进） */
            var tracks = _pl.Tracks;
            for (int i = 0; i < ordered.Count; i++) tracks[i] = ordered[i].Meta;
            if (cur != null) _pl.CurIdx = _rows.IndexOf(cur);
            _pl.InvalidateShuffle();
            TrackList.SelectedIndex = _pl.CurIdx;
        }

        /* ════════ 曲库记忆：重启免重新导入（%APPDATA% settings.txt 的 tracks 键，| 分隔） ════════ */
        void SaveTracks()
        {
            if (Settings.NoSave) return;
            var parts = new List<string>();
            foreach (var r in _rows)
            {
                string p = FindPath(r.Meta);
                if (!string.IsNullOrEmpty(p)) parts.Add(p);
            }
            Settings.Set("tracks", string.Join("|", parts));
            Settings.Set("sort", _sortMode.ToString());
            Settings.Save();
        }

        void RestoreTracks()
        {
            string blob = Settings.Get("tracks", "");
            if (string.IsNullOrEmpty(blob)) return;
            var files = new List<string>();
            foreach (var p in blob.Split('|'))
                if (p.Length > 0 && File.Exists(p)) files.Add(p);   /* 已删除/移走的文件静默跳过 */
            if (files.Count == 0) return;
            _restorePending = true;
            AddFilesAsync(files);
        }

        /// <summary>批量添加：files 可混文件与文件夹（目录在后台递归展开，大文件夹不卡 UI）。
        /// 标签读取在线程池；入库前做去重校验（AddGuard）：与曲库已有/批内重复的路径直接跳过。</summary>
        void AddFilesAsync(IList<string> inputs, Action afterDone = null)
        {
            if (inputs == null || inputs.Count == 0) return;
            BtnAdd.IsEnabled = false;
            BtnAdd.Content = "Reading…";
            var existing = new List<string>(_pathKeys);   /* 路径键快照供后台过滤（UI 线程外不碰 _pathKeys） */
            System.Threading.Tasks.Task.Run(() =>
            {
                var files = new List<string>();
                foreach (var p in inputs)
                {
                    try
                    {
                        if (Directory.Exists(p)) ScanAudioDir(p, files);
                        else if (File.Exists(p)) files.Add(p);
                    }
                    catch { }
                }
                int skipExist, skipDup;
                var fresh = AddGuard.FilterNew(files, existing, out skipExist, out skipDup);
                var pairs = new List<KeyValuePair<TrackMeta, string>>();
                foreach (var f in fresh)
                {
                    try
                    {
                        var meta = TagReader.Read(f);
                        if (meta.Spec != null) pairs.Add(new KeyValuePair<TrackMeta, string>(meta, f));
                    }
                    catch { }
                }
                int notAudio = fresh.Count - pairs.Count;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    int added = 0, skipped = skipExist + skipDup;
                    var addedPairs = new List<KeyValuePair<TrackMeta, string>>();
                    foreach (var kv in pairs)
                        if (AddTrackIfNew(kv.Key, kv.Value)) { added++; addedPairs.Add(kv); }
                        else skipped++;                      /* 并发兜底：本批内路径键已登记 */
                    TrackList.ItemsSource = _rows;
                    if (files.Count == 0)
                        PlStats.Text = "NO AUDIO IN FOLDER";
                    else if (_restorePending)
                        PlStats.Text = _rows.Count + " TRACKS";   /* 曲库恢复：静默恢复，不刷添加统计 */
                    else
                    {
                        string msg = _rows.Count + " TRACKS";
                        if (added > 0) msg += " · +" + added + " ADDED";
                        if (skipped > 0) msg += " · " + skipped + " SKIPPED";
                        if (notAudio > 0) msg += " · " + notAudio + " NOT AUDIO";
                        PlStats.Text = msg;
                    }
                    try
                    {
                        File.AppendAllText(Path.Combine(Path.GetTempPath(), "gsharpminor-crash.log"),
                            DateTime.Now + " [add] inputs=" + inputs.Count + " files=" + files.Count
                            + " fresh=" + fresh.Count + " +" + added + " added, " + skipped + " skipped ("
                            + skipExist + " exist + " + skipDup + " batch), " + notAudio + " not-audio, total=" + _rows.Count
                            + Environment.NewLine);
                    }
                    catch { }
                    _pl.InvalidateShuffle();
                    ApplySort(_sortMode);   /* 新增后按当前档位重排（导入序档按 Seq 稳定） */
                    /* 响度缓存预热：无 RG 标签的曲目排队后台扫描（串行，播前大概率已就绪） */
                    foreach (var kv in addedPairs)
                        if (float.IsNaN(kv.Key.RgGainDb))
                            LoudnessScanner.Request(kv.Value);
                    _restorePending = false;
                    SaveTracks();
                    BtnAdd.IsEnabled = true;
                    BtnAdd.Content = "+ Add Files";
                    if (afterDone != null) afterDone();
                }));
            });
        }

        void TrackList_MouseDoubleClick(object s, MouseButtonEventArgs e)
        {
            /* 按点击的行内容解析索引（不信任 SelectedIndex：视图错位时会播错曲目） */
            var row = ItemsControl.ContainerFromElement(TrackList, e.OriginalSource as DependencyObject) as DataGridRow;
            var tr = row != null ? row.Item as TrackRow : null;
            int idx = tr != null ? _rows.IndexOf(tr) : TrackList.SelectedIndex;
            if (idx >= 0) PlayIndex(idx);
        }

        /* 滚轮整行吸附：拖动走像素滚动（连续），滚轮手动换算成整行步进并吸附到
           行边界——用户要的组合行为（滚轮吸附/拖动连续）。每格 = WheelScrollLines 行。
           Windows 方向：Delta>0（向上滚）→ 偏移减小（看前面的内容）。 */
        void TrackList_PreviewMouseWheel(object s, MouseWheelEventArgs e)
        {
            var sv = TrackListScroll();
            if (sv == null) return;
            int rows = (int)Math.Round(e.Delta / 120.0 * SystemParameters.WheelScrollLines);
            if (rows == 0) return;
            double rowH = TrackList.RowHeight > 0 ? TrackList.RowHeight : 36;
            double max = sv.ScrollableHeight;
            double target = (Math.Round(sv.VerticalOffset / rowH) - rows) * rowH;
            if (target < 0) target = 0;
            if (target > max) target = max;
            sv.ScrollToVerticalOffset(target);
            e.Handled = true;
        }

        ScrollViewer TrackListScroll()
        {
            if (_listSv != null) return _listSv;
            DependencyObject d = TrackList;
            while (d != null)
            {
                d = VisualTreeHelper.GetChild(d, 0);
                if (d is ScrollViewer sv) { _listSv = sv; break; }
            }
            return _listSv;
        }
        ScrollViewer _listSv;

        /* 进度条拖动：MouseDown 捕获鼠标（拖出轨道/窗口仍收得到 MouseUp，防 seek 丢失 +
           _scrubbing 粘住）；松手时按最终位置 seek；捕获被外部夺走则只解除拖动态不 seek。 */
        void Scrub_MouseLeftButtonDown(object s, MouseButtonEventArgs e)
        {
            if (!_eng.IsLoaded) return;
            _scrubbing = true;
            ScrubTrack.CaptureMouse();
            ScrubVisual(e.GetPosition(ScrubTrack).X);
        }

        void Scrub_MouseLeftButtonUp(object s, MouseButtonEventArgs e)
        {
            ScrubTrack.ReleaseMouseCapture();
            if (!_eng.IsLoaded) { _scrubbing = false; return; }
            var p = e.GetPosition(ScrubTrack);
            double ratio = Math.Max(0, Math.Min(1, p.X / ScrubTrack.ActualWidth));
            _eng.Position = ratio * _eng.Length;
            _scrubbing = false;
        }

        void Scrub_LostMouseCapture(object s, MouseEventArgs e)
        {
            _scrubbing = false;   /* Alt-Tab 等外部夺捕获：进度头恢复跟随实际位置 */
        }

        void Scrub_MouseMove(object s, MouseEventArgs e)
        {
            if (_scrubbing && _eng.IsLoaded) ScrubVisual(e.GetPosition(ScrubTrack).X);
        }

        void ScrubVisual(double x)
        {
            double w = ScrubTrack.ActualWidth;
            if (w <= 0) return;
            double ratio = Math.Max(0, Math.Min(1, x / w));
            ScrubHead.X1 = ScrubHead.X2 = ratio * w;
            ScrubFill.Width = ratio * w;
            if (_eng.IsLoaded) TimeNow.Text = Fmt(ratio * _eng.Length);
        }

        void Vol_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_eng == null) return;
            _eng.SetVolume((float)(e.NewValue / 100.0));
            if (VolSlider != null) Persist();
        }

        /* 隧道阶段拦截：防 DataGrid/ComboBox 吃掉 T/1/2 等快捷键（单一处理路径防双触发） */
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (HandleKey(e)) { e.Handled = true; return; }
            base.OnPreviewKeyDown(e);
        }

        bool HandleKey(KeyEventArgs e)
        {
            Key k = e.Key == Key.System ? e.SystemKey : e.Key;
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "gsharpminor-crash.log"),
                    DateTime.Now + " [key] " + k + Environment.NewLine);
            }
            catch { }
            if (k == Key.D1) { SetPage("analyzer"); return true; }
            if (k == Key.D2) { SetPage("library"); return true; }
            if (k == Key.T) { BtnTheme_Click(null, null); return true; }
            if (k == Key.R) { BtnRange_Click(null, null); return true; }
            if (k == Key.D) { BtnDsdOut_Click(null, null); return true; }
            if (k == Key.N) { BtnNorm_Click(null, null); return true; }
            if (k == Key.Space) { if (!_loading) { _eng.Toggle(); SyncUI(); } return true; }
            if (k == Key.Left) { if (!_loading) _eng.Position = Math.Max(0, _eng.Position - 5); return true; }
            if (k == Key.Right) { if (!_loading) _eng.Position = Math.Min(_eng.Length, _eng.Position + 5); return true; }
            if (k == Key.F) { _frozen = !_frozen; SpecView.SetFrozen(_frozen); return true; }
            return false;
        }

        protected override void OnClosed(EventArgs e)
        {
            try { Persist(); } catch { }
            try { if (!_restorePending) SaveTracks(); } catch { }
            CompositionTarget.Rendering -= OnRenderingTick;
            _eng.Dispose();
            base.OnClosed(e);
        }
    }
}
