using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Recording;

namespace ScreenCaptureTool.Controls
{
    /// <summary>
    /// 录屏前置参数条：画质下拉 / 录制延迟下拉 / 声音面板 / 键鼠操作面板 / 开始录制。
    /// 条上没有取消出口——Esc 由宿主 <c>CaptureWindow.TryCancelRecordByKeyboard</c> 直接收条。
    /// 全条只挂 <c>Themes/Controls.axaml</c> 里的全局类，本控件不自带皮（2026-09-21 UI 统一化）。
    /// 键鼠操作那一格（连同下挂面板与两枚勾选）在录制中的控制条上另有一份：开录之后还要能改。
    ///
    /// <para>图标钮下挂的是 <c>Popup</c> ＋ <c>Border.textPopupPanel</c> 面板，与水印参数条同一配方：
    /// 刻意不开 Popup 自带的轻点关闭（那一下会透传给全屏遮罩＝挪选区／另起新框），收面板改走窗口根的
    /// Tunnel 预捕获。</para>
    /// </summary>
    public partial class RecordOptionsControl : UserControl
    {
        /// <summary>点了「开始录制」（或 Enter/Space）。参数已由本次点击冻结进 <see cref="LastBuilt"/>。</summary>
        public event Action? StartRequested;

        /// <summary>默认录制输出格式（MP4），录制完成后可在预览贴图窗口自由转存为 GIF/WebP。</summary>
        public RecordFormat Format { get; private set; } = RecordFormat.Mp4;

        /// <summary>开始那一刻冻结的完整参数（Region 由上层补）。</summary>
        public RecordingOptions? LastBuilt { get; private set; }

        /// <summary>当前选中的画质预设 Id（内置档＝RecordQuality 成员名，自建档＝u…）。</summary>
        private string _selectedPresetId = nameof(RecordQuality.Standard);
        private int _countdownSec = RecordCountdown.DefaultSec;

        /// <summary>面板打开期间挂在窗口根上的"点外面即收"处理器（与水印／笔迹菜单同一配方）。
        /// 两条面板共用同一个宿主钩子与同一个处理器，处理器内按<b>当前开着的那条</b>决定收谁。</summary>
        private InputElement? _dismissHost;

        public RecordOptionsControl()
        {
            InitializeComponent();
            var tools = (StackPanel)Content!;
            var map = new System.Collections.Generic.Dictionary<string, Control[]>
            {
                ["quality"] = [BtnQuality], ["countdown"] = [BtnCountdown],
                ["audio"] = [(Control)BtnAudio.Parent!], ["keymouse"] = [(Control)BtnKeyMouse.Parent!], ["start"] = [BtnStart]
            };
            new Services.ToolbarLayoutView(tools, ToolbarScene.RecordPreparation, map).Apply(AppSettings.Current);

            WireDismiss(AudioPopup);
            WireDismiss(KeyMousePopup);
            DetachedFromVisualTree += (_, _) => DetachDismissHook();

            BuildCountdownMenu();
            BuildQualityMenu();
            SyncQuality();
            SyncCountdown();

            // 设置页改了预设（改参数、改显隐、新建删除）⇒ 参数条若正开着就就地重铺，不必重开截图
            RecordPresets.Changed += OnPresetsChanged;
            DetachedFromVisualTree += (_, _) => RecordPresets.Changed -= OnPresetsChanged;
        }

        private void OnPresetsChanged()
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                BuildQualityMenu();
                SetQualityById(_selectedPresetId);
            });
        }

        /// <summary>设置页只借用控件外观，离开原宿主后不再订阅预设变化。</summary>
        internal void StopPresetUpdates() => RecordPresets.Changed -= OnPresetsChanged;

        private MenuFlyout QualityMenu => (MenuFlyout)BtnQuality.Flyout!;

        /// <summary>画质条目按 <see cref="RecordPresets.VisibleList"/> 现铺（设置页勾了「显示」的那些，顺序同设置页）。</summary>
        private void BuildQualityMenu()
        {
            QualityMenu.Items.Clear();
            MenuItem? first = null;
            foreach (var p in RecordPresets.VisibleList())
            {
                var item = new MenuItem { Header = p.Name, Tag = p.Id };
                ToolTip.SetPlacement(item, PlacementMode.Right);
                ToolTip.SetHorizontalOffset(item, 6);
                ToolTip.SetShowDelay(item, 150);
                ToolTip.SetTip(item, p.Name + "：" + p.Summary());
                item.Click += OnQualityItemClick;
                QualityMenu.Items.Add(item);
                first ??= item;
            }
            // 画质菜单＝纯文字短档名 → 窄条 + 居中（§6.12 唯一挂类入口，presenter 由框架现造、外部拿不到）
            if (first != null)
                MenuFlyoutPresenterStyle.Attach(first,
                    MenuFlyoutPresenterStyle.Compact, MenuFlyoutPresenterStyle.Centered);
        }

        /// <summary>
        /// 录制延迟菜单本体。flyout 是独立名称作用域，XAML 上写 <c>x:Name</c> 不生成字段（CS0103），
        /// 只能从宿主钮的 <see cref="FlyoutBase"/> 取回来。
        /// </summary>
        private MenuFlyout CountdownMenu => (MenuFlyout)BtnCountdown.Flyout!;

        /// <summary>
        /// 录制延迟条目按 <see cref="RecordCountdown.Seconds"/> 现铺（不在 XAML 里重抄一遍档位 ⇒
        /// 加一档只改 Core 那张表，条上与设置页同时跟上）。
        /// </summary>
        private void BuildCountdownMenu()
        {
            MenuItem? first = null;
            foreach (int sec in RecordCountdown.Seconds)
            {
                var item = new MenuItem { Header = RecordCountdown.Label(sec), Tag = sec };
                item.Click += OnCountdownItemClick;
                CountdownMenu.Items.Add(item);
                first ??= item;
            }

            if (first != null)
                MenuFlyoutPresenterStyle.Attach(first,
                    MenuFlyoutPresenterStyle.Compact, MenuFlyoutPresenterStyle.Centered);
        }

        /// <summary>
        /// 铺"本次开条"的初值：读<b>上次使用</b>（<c>RecLast*</c>），没记过才回落到设置页的<b>默认值</b>。
        /// 两族键的分工见 <c>AppSettings</c> 里那两段族注释（2026-09-24 用户要求"修改跟设置里面要区别开来"）。
        /// <para>⚠ 这里的赋值会触发四枚勾选的 Checked/Unchecked，而那几个处理器是"选择即落盘"——
        /// 所以落盘必须先比对再写（<see cref="SaveLast"/>），否则每次弹条都白盘一遍设置。</para>
        /// <para>末尾那行日志是取证：记忆里外两层，"读到的"和"铺上的"必须都记下来，
        /// 才分得开"没读到"与"读到没铺上"（本次排查就卡在只能看到现象）。</para>
        /// </summary>
        public void ApplyDefaults()
        {
            var s = AppSettings.Current;
            int countdown = AppSettings.Pick(s.RecLastCountdownSec, s.RecCountdownSec);
            AppLogger.Info($"录制参数条：上次使用 画质={s.RecLastQuality}|{s.RecQuality} " +
                           $"延迟={s.RecLastCountdownSec?.ToString() ?? "未记录"}|{s.RecCountdownSec} " +
                           $"系统声={s.RecLastSystemAudio?.ToString() ?? "未记录"}|{s.RecSystemAudio} " +
                           $"麦克风={s.RecLastMicAudio?.ToString() ?? "未记录"}|{s.RecMicAudio} " +
                           $"键帽={s.RecLastVisKeycap?.ToString() ?? "未记录"}|{s.RecVisKeycap} " +
                           $"涟漪={s.RecLastVisRipple?.ToString() ?? "未记录"}|{s.RecVisRipple} " +
                           $"→ 铺上 延迟={countdown} 秒");

            SetQualityById(AppSettings.Pick(s.RecLastQuality, s.RecQuality));
            SetCountdown(countdown);
            ChkSystemAudio.IsChecked = AppSettings.Pick(s.RecLastSystemAudio, s.RecSystemAudio);
            ChkMicAudio.IsChecked = AppSettings.Pick(s.RecLastMicAudio, s.RecMicAudio);
            ChkKeycapPre.IsChecked = AppSettings.Pick(s.RecLastVisKeycap, s.RecVisKeycap);
            ChkRipplePre.IsChecked = AppSettings.Pick(s.RecLastVisRipple, s.RecVisRipple);
        }

        /// <summary>
        /// 键鼠两路勾选（不进 <see cref="BuildOptions"/>：起跑那拍 <c>RecordFlow</c> 直接读这一对键的
        /// "上次使用"值）。落盘已由 <see cref="OnFxCheckChanged"/> 在勾选那一刻做完，这里只是给宿主读数用。
        /// </summary>
        public bool KeycapChecked => ChkKeycapPre.IsChecked == true;

        /// <summary>见 <see cref="KeycapChecked"/>。</summary>
        public bool RippleChecked => ChkRipplePre.IsChecked == true;

        /// <summary>把当前选项汇成录制参数（不含 Region）；同时冻结 <see cref="Format"/> 与 <see cref="LastBuilt"/>。</summary>
        public RecordingOptions BuildOptions()
        {
            var s = AppSettings.Current;
            bool sys = ChkSystemAudio.IsChecked == true;
            bool mic = ChkMicAudio.IsChecked == true;
            AudioLayout audio = (sys, mic) switch
            {
                (true, true) => s.RecAudioMix == "Split" ? AudioLayout.Split : AudioLayout.Mixed,
                (true, false) => AudioLayout.SystemOnly,
                (false, true) => AudioLayout.MicOnly,
                _ => AudioLayout.None,
            };

            var opt = new RecordingOptions
            {
                Engine = RecordEngines.Normalize(s.RecEngine),
                Quality = Enum.TryParse<RecordQuality>(_selectedPresetId, out var q) ? q : RecordQuality.Standard,
                PresetId = _selectedPresetId,
                Audio = audio,
                MicVolume01 = Math.Clamp(s.RecMicVolume, 20, 100) / 100f,
                // 光标入画读设置项。原先这里写死 true ⇒ 设置页「鼠标可视化」那三档里的"关"
                // 只关得掉涟漪、关不掉光标，用户看到的"设了不生效"正是这一条（2026-09-22 修）。
                DrawCursor = s.RecVisCursor,
                CountdownSec = _countdownSec,
                ManualFfmpegPath = string.IsNullOrWhiteSpace(s.RecFfmpegPath) ? null : s.RecFfmpegPath,
                CaptureSource = RecordCaptureSources.Normalize(s.RecCaptureSource),
            };
            Format = RecordFormat.Mp4;
            LastBuilt = opt;
            return opt;
        }

        /// <summary>收起下挂面板（宿主切工具、收起参数条时调用）。两条一起收——它们不可能同时开着，
        /// 但漏收一条会留下"切了工具面板还浮着"的鬼影。</summary>
        public void ClosePopups()
        {
            AudioPopup.IsOpen = false;
            KeyMousePopup.IsOpen = false;
        }

        /// <summary>Enter/Space 快捷开始（CaptureWindow 按键路由入口）。</summary>
        public bool TryStartByKeyboard()
        {
            if (!IsVisible) return false;
            Start();
            return true;
        }

        private void OnQualityItemClick(object? sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { Tag: string tagStr })
            {
                SetQualityById(tagStr);
                SaveLast(AppSettings.Current.RecLastQuality, _selectedPresetId,
                    v => AppSettings.Current.RecLastQuality = v);
            }
        }

        /// <summary>按 Id 选档。记住的档已被删除或设成隐藏 ⇒ 回落到标准档，标准档也隐藏了就取下拉第一档
        /// （不偷偷用一个菜单里看不到的档）。</summary>
        private void SetQualityById(string? id)
        {
            var visible = RecordPresets.VisibleList();
            var hit = visible.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
                      ?? visible.FirstOrDefault(p => p.Id == nameof(RecordQuality.Standard))
                      ?? visible[0];
            _selectedPresetId = hit.Id;
            SyncQuality();
        }

        /// <summary>把当前画质铺到按钮文字与菜单当前档：当前档只挂 <c>.current</c>（染品牌紫），不拼 ✓。</summary>
        private void SyncQuality()
        {
            var p = RecordPresets.Resolve(_selectedPresetId);
            TxtQuality.Text = p.Name;
            ToolTip.SetTip(BtnQuality, $"录制画质（当前：{p.Name}）\n{p.Summary()}\n点击切换画质档位；档位可在「设置 → 交互与行为 → 录屏」里编辑与新建");
            foreach (object? obj in QualityMenu.Items)
            {
                if (obj is MenuItem { Tag: string id } item) SetCurrent(item, id == _selectedPresetId);
            }
        }

        private static void SetCurrent(MenuItem item, bool current) => item.Classes.Set("current", current);

        private void OnCountdownItemClick(object? sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { Tag: int sec })
            {
                SetCountdown(sec);
                SaveLast(AppSettings.Current.RecLastCountdownSec, (int?)sec,
                    v => AppSettings.Current.RecLastCountdownSec = v);
            }
        }

        private void SetCountdown(int sec)
        {
            _countdownSec = RecordCountdown.Normalize(sec);
            SyncCountdown();
        }

        /// <summary>延迟当前档回显到按钮文字与菜单条目（当前档只挂 <c>.current</c> 染品牌紫，不拼 ✓）。</summary>
        private void SyncCountdown()
        {
            TxtCountdown.Text = RecordCountdown.Label(_countdownSec);
            foreach (object? obj in CountdownMenu.Items)
            {
                if (obj is MenuItem { Tag: int sec } item)
                {
                    SetCurrent(item, sec == _countdownSec);
                }
            }
        }

        private void OnAudioClick(object? sender, RoutedEventArgs e)
        {
            KeyMousePopup.IsOpen = false;
            AudioPopup.IsOpen = !AudioPopup.IsOpen;
        }

        private void OnKeyMouseClick(object? sender, RoutedEventArgs e)
        {
            AudioPopup.IsOpen = false;
            KeyMousePopup.IsOpen = !KeyMousePopup.IsOpen;
        }

        /// <summary>
        /// 两条面板开关期间挂/摘窗口根的"点外面即收"钩子。⚠ 钩子是**共用**的，判据必须是"任一还开着"
        /// 而不是"刚关的这条已关"——两条各挂一份 <c>WireDismiss</c>，若按后者，先开A再开B（A自动收）时 B 一关
        /// 就会连带摘掉钩子，此时若 A 又被重新打开则钩子已不在，面板点外面收不掉。
        /// </summary>
        private void WireDismiss(Popup popup)
        {
            popup.PropertyChanged += (_, e) =>
            {
                if (e.Property != Popup.IsOpenProperty) return;
                if (AnyPopupOpen) AttachDismissHook();
                else DetachDismissHook();
            };
        }

        private bool AnyPopupOpen => AudioPopup.IsOpen || KeyMousePopup.IsOpen;

        private void AttachDismissHook()
        {
            if (_dismissHost != null || TopLevel.GetTopLevel(this) is not { } host) return;
            _dismissHost = host;
            host.AddHandler(InputElement.PointerPressedEvent, OnDismissPressed, RoutingStrategies.Tunnel);
        }

        private void DetachDismissHook()
        {
            _dismissHost?.RemoveHandler(InputElement.PointerPressedEvent, OnDismissPressed);
            _dismissHost = null;
        }

        /// <summary>
        /// 窗口根 Tunnel 按下（有面板开着才挂）：按在面板或那枚开关钮上＝自己人，不动；按在别处＝收起**当前开着的那条**面板，
        /// 但**不吞事件**（与 <c>WatermarkOptionsControl.OnDismissPressed</c> 同口径：吞掉会改动画布起笔语义）。
        /// 判据按几何／祖先链而非命中，且要把面板那枚 Border 自身也算进去——按在面板内边距那圈空位上时
        /// 命中的就是它，而它不在自己的祖先链里。
        /// ⚠ 开关钮算"自己人"：否则按下这一下先收面板，紧接着的 Click 读到 <c>IsOpen==false</c> 又把它弹回来，
        ///    表现为"那枚按钮点两下也关不掉面板"。
        /// ⚠ 两条面板共用本处理器与同一个宿主钩子：钩子是"任一开着即挂"，故这里必须按各自的开合状态分派，
        ///    且两条的开关钮／面板 Border 都要进"自己人"白名单——漏一条，那条就会被另一条的判定误收。
        /// </summary>
        private void OnDismissPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.Source is not Visual source) return;

            if (AudioPopup.IsOpen && !IsOwn(source, BtnAudio) && !IsOwn(source, AudioPanel))
                AudioPopup.IsOpen = false;

            if (KeyMousePopup.IsOpen && !IsOwn(source, BtnKeyMouse) && !IsOwn(source, KeyMousePanel))
                KeyMousePopup.IsOpen = false;
        }

        private static bool IsOwn(Visual source, Visual ancestor) =>
            ReferenceEquals(source, ancestor)
            || source.GetVisualAncestors().Any(a => ReferenceEquals(a, ancestor));

        private void Start()
        {
            ClosePopups();
            BuildOptions();
            StartRequested?.Invoke();
        }

        /// <summary>
        /// 声音两枚勾选（系统声音／麦克风）：改动即记为"上次使用"。
        /// 2026-09-24 之前这一族六项只在点「开始录制」时统一落盘（<c>PersistChoices</c>），
        /// 于是"改了档位→按 Esc 或右键退出"这一路整段丢失——用户报的"上次设置的东西下次又是默认"
        /// 至少包含这一类。现在六项一律选择即落盘，<see cref="Start"/> 里不再补写。
        /// </summary>
        private void OnAudioCheckChanged(object? sender, RoutedEventArgs e)
        {
            bool on = (sender as CheckBox)?.IsChecked == true;
            if (ReferenceEquals(sender, ChkSystemAudio))
                SaveLast(AppSettings.Current.RecLastSystemAudio, (bool?)on, v => AppSettings.Current.RecLastSystemAudio = v);
            else if (ReferenceEquals(sender, ChkMicAudio))
                SaveLast(AppSettings.Current.RecLastMicAudio, (bool?)on, v => AppSettings.Current.RecLastMicAudio = v);
        }

        /// <summary>键鼠两枚勾选（按键显示／点击涟漪）：同 <see cref="OnAudioCheckChanged"/>。
        /// ⚠ 起跑时 <c>RecordFlow</c> 读的是同一对键（经 <c>AppSettings.Pick</c> 回落），
        /// 所以这里落盘即等于"本次录制生效"，不需要再走 <c>BuildOptions</c>。</summary>
        private void OnFxCheckChanged(object? sender, RoutedEventArgs e)
        {
            bool on = (sender as CheckBox)?.IsChecked == true;
            if (ReferenceEquals(sender, ChkKeycapPre))
                SaveLast(AppSettings.Current.RecLastVisKeycap, (bool?)on, v => AppSettings.Current.RecLastVisKeycap = v);
            else if (ReferenceEquals(sender, ChkRipplePre))
                SaveLast(AppSettings.Current.RecLastVisRipple, (bool?)on, v => AppSettings.Current.RecLastVisRipple = v);
        }

        /// <summary>「上次使用」落盘的唯一入口：与已记录的值相同就直接返回——<see cref="ApplyDefaults"/>
        /// 铺初值同样会触发这些事件，不比对就是每次弹条都白盘一遍整份 ini。
        /// 失败只记日志：存不下设置不该拦着用户改参数。</summary>
        private static void SaveLast<T>(T recorded, T value, Action<T> assign)
        {
            if (Equals(recorded, value)) return;
            try
            {
                assign(value);
                AppSettings.Current.Save();
            }
            catch (Exception ex)
            {
                AppLogger.Warn("录制参数条：保存参数选择失败：" + ex.Message);
            }
        }

        private void OnStartClick(object? sender, RoutedEventArgs e) => Start();
    }
}
