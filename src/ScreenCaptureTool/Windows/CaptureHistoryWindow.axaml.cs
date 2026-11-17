using System;



using System.Collections.Generic;



using System.Collections.ObjectModel;



using System.ComponentModel;



using System.Diagnostics;



using System.Globalization;



using System.IO;



using System.Linq;



using System.Runtime.CompilerServices;



using System.Threading.Tasks;



using Avalonia;



using Avalonia.Controls;



using Avalonia.Controls.ApplicationLifetimes;



using Avalonia.Controls.Documents;



using Avalonia.Input;



using Avalonia.Interactivity;



using Avalonia.Media;



using Avalonia.Media.Imaging;



using Avalonia.Platform.Storage;



using Avalonia.Styling;



using Avalonia.Threading;



using Avalonia.VisualTree;



using ScreenCaptureTool.Core;



using ScreenCaptureTool.Core.History;



using ScreenCaptureTool.Services;







namespace ScreenCaptureTool.Windows;







/// <summary>



/// 缩略图卡片的视图项。



///



/// 只暴露布尔/字符串状态，**不持有任何 <c>IBrush</c>**：选中态由 XAML 的



/// <c>Classes.selected="{Binding IsSelected}"</c> 驱动（设计规范 §2 C# 无画刷铁律）。



/// </summary>



public sealed class HistoryCardItem : INotifyPropertyChanged



{



    private bool _isSelected;



    private Bitmap? _thumbnail;







    public event PropertyChangedEventHandler? PropertyChanged;







    public HistoryCardItem(CaptureHistoryItem source)



    {



        Source = source;



    }







    /// <summary>底层数据记录。</summary>



    public CaptureHistoryItem Source { get; }







    public string Id => Source.Id;







    /// <summary>时间显示：今天的省略日期，其余带月日。</summary>



    public string TimeText



    {



        get



        {



            DateTime t = Source.CapturedAt;



            if (t.Date == DateTime.Today) return "今天 " + t.ToString("HH:mm:ss", CultureInfo.InvariantCulture);



            if (t.Date == DateTime.Today.AddDays(-1)) return "昨天 " + t.ToString("HH:mm:ss", CultureInfo.InvariantCulture);



            return t.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);



        }



    }







    public string SizeText => Source.Width + "×" + Source.Height;







    public string SourceText => Source.SourceDisplayName;







    /// <summary>



    /// 卡片第三行：可搜文本摘要。只取首行（一行宽、末尾由 XAML 省略号截断，命中位置在行外时



    /// 看不到高亮是设计稿 §06 已认下的代价）。没有文字的记录回「尚未识别」占住这一行 ——



    /// 留空会让同屏卡片有的三行有的两行，网格看着就是坏的。



    /// </summary>



    public string SummaryText



    {



        get



        {



            string text = Source.Text ?? "";



            if (string.IsNullOrWhiteSpace(text)) return "尚未识别";



            int cut = text.IndexOfAny(LineBreakChars);



            return (cut >= 0 ? text.Substring(0, cut) : text).Trim();



        }



    }







    private static readonly char[] LineBreakChars = { '\r', '\n' };







    /// <summary>缩略图位图（懒加载，加载失败保持 null 由 XAML 退化为占位图标）。</summary>



    public Bitmap? Thumbnail



    {



        get => _thumbnail;



        set



        {



            if (ReferenceEquals(_thumbnail, value)) return;



            _thumbnail = value;



            Raise();



            Raise(nameof(HasThumbnail));



        }



    }







    public bool HasThumbnail => _thumbnail != null;







    public void NotifySourceUpdated()



    {



        Raise(nameof(SizeText));



        Raise(nameof(SummaryText));



        Raise(nameof(SourceText));



    }







    public bool IsSelected



    {



        get => _isSelected;



        set



        {



            if (_isSelected == value) return;



            _isSelected = value;



            Raise();



        }



    }







    private void Raise([CallerMemberName] string? name = null)



    {



        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? ""));



    }



}







/// <summary>



/// 截图历史窗口（工具型窗口，骨架照设计规范 §7.1 / §7.2：36 标题栏 + 46 工具栏 + 正文 + 30 状态条）。



///



/// 静态单例（<see cref="ShowHistory"/>）：入口有托盘菜单与设置页两处，静态入口最省事，



/// 与 <see cref="OcrWindow.ShowResult"/> 同款模式。



///



/// 搜索是一个框吃两种查询（<see cref="HistoryQuery"/>）：时间表达式与关键词，不拆框、不加模式开关。



///



/// 刻意不做的一件事（AGENTS.md §8 不过度设计）：



/// - **网格/列表双视图**：同一批数据的两种排布属重复功能，缩略图是本窗主角，只保留网格。



/// </summary>



public partial class CaptureHistoryWindow : Window



{



    private static CaptureHistoryWindow? _instance;







    /// <summary>全部记录（倒序，最新在前）。</summary>



    private readonly List<HistoryCardItem> _allItems = new List<HistoryCardItem>();







    /// <summary>当前筛选后展示的记录，绑到 ItemsControl。</summary>



    private readonly ObservableCollection<HistoryCardItem> _visibleItems = new ObservableCollection<HistoryCardItem>();







    private HistoryCardItem? _selected;



    private Bitmap? _previewBitmap;



    private int _toastToken;







    /// <summary>搜索框当前查询（未输入时为 <see cref="HistoryQuery.Empty"/>，不过滤）。</summary>



    private HistoryQuery _query = HistoryQuery.Empty;







    /// <summary>上一次输入是否被判成时间表达式 —— 只在"由非时间翻成时间"的那一刻弹一次确认。</summary>



    private bool _lastQueryIsTime;







    /// <summary>上一次的高亮词。与命中集一起决定要不要重建卡片容器（重建会冲掉滚动位置）。</summary>



    private string? _lastHighlightTerm;







    /// <summary>自动刷新的去抖闸：一次截图可能连着产出多条记录，合并为一次列表合并。</summary>



    private bool _refreshQueued;



    private DispatcherTimer? _saveSizeDebounceTimer;







    /// <summary>



    /// 本窗口是否已关闭。异步装载用它判断"还要不要继续"，<b>不能用 <c>_instance == null</c></b>：



    /// 单例是在构造函数<b>返回之后</b>才赋值的（<c>ShowHistoryCore</c> 里 <c>_instance = new ...</c>），



    /// 而装载已经前移到构造期（首帧前就绪），于是构造期发起的那一批会被这道守卫整批误判成"窗口已关"。



    /// </summary>



    private bool _isClosed;







    public CaptureHistoryWindow()



    {



        InitializeComponent();







        var settings = AppSettings.Current;



        if (settings.CaptureHistoryWindowWidth >= MinWidth) Width = settings.CaptureHistoryWindowWidth;



        else Width = 1000;



        if (settings.CaptureHistoryWindowHeight >= MinHeight) Height = settings.CaptureHistoryWindowHeight;



        else Height = 680;







        ItemsHistory.ItemsSource = _visibleItems;



        // 网格水平对齐自适应（2026-09-10 迭代 5）：内容距铺满不到一列宽 → 居中（零头对称分摊）；
        // 差得多（如搜索只剩一两张）→ 贴左，单张卡片悬在窗口正中很怪。垂直固定顶对齐（XAML）。
        // 三处触发覆盖缩放窗口 / 滚动条增减 / 条目增减；只写枚举且有差值守卫，无布局循环。
        ScrollCards.SizeChanged += (_, _) => UpdateGridHorizontalAlignment();

        ScrollCards.ScrollChanged += (_, _) => UpdateGridHorizontalAlignment();

        ItemsHistory.LayoutUpdated += (_, _) => UpdateGridHorizontalAlignment();







        RadFilterAll.IsCheckedChanged += OnFilterChanged;



        RadFilterCapture.IsCheckedChanged += OnFilterChanged;



        RadFilterSticker.IsCheckedChanged += OnFilterChanged;



        RadFilterLong.IsCheckedChanged += OnFilterChanged;







        // 窗口已打开时新截图要即时上榜：存储层落盘后回调，切回 UI 线程做增量合并



        CaptureHistoryStore.ItemRecorded += OnHistoryItemRecorded;



        CaptureHistoryStore.ItemUpdated += OnHistoryItemUpdated;







        // 主题切换时重刷 Logo（动态内容的重渲染走 PropertyChanged，窗口本身不写



        // RequestedThemeVariant——那会脱离 App 主题作用域，见设计规范 §8.5）



        PropertyChanged += (_, e) =>



        {



            if (e.Property == ActualThemeVariantProperty) UpdateAppIcons();



        };







        SizeChanged += OnWindowSizeChanged;



        KeyDown += OnWindowKeyDown;







        // 构造阶段预载尺寸、图标与历史列表：首帧前就绪，消除打开时的白框与卡顿



        EnsureWindowSizeWithinScreen();



        UpdateAppIcons();



        ReloadHistory();







        Closed += OnWindowClosed;



    }







    // -------------------- 静态入口 --------------------







    /// <summary>打开（或激活已打开的）截图历史窗口。可从任意线程调用。</summary>



    public static void ShowHistory()



    {



        if (!Dispatcher.UIThread.CheckAccess())



        {



            Dispatcher.UIThread.Post(ShowHistoryCore);



            return;



        }



        ShowHistoryCore();



    }







    private static void ShowHistoryCore()



    {



        try



        {



            if (_instance != null)



            {



                _instance.ReloadHistory();



                WindowPresentationHelper.BringToForeground(_instance);



                return;



            }



            _instance = new CaptureHistoryWindow();



            WindowPresentationHelper.ShowSmoothly(_instance);



        }



        catch (Exception ex)



        {



            AppLogger.Error("CaptureHistoryWindow.ShowHistory 失败", ex);



        }



    }







    // -------------------- 数据装载 --------------------







    /// <summary>重新读取索引并重建卡片列表（打开窗口、清空、删除、重复触发时调用）。</summary>



    private void ReloadHistory()



    {



        try



        {



            DisposeCardThumbnails();



            _allItems.Clear();



            _selected = null;







            IReadOnlyList<CaptureHistoryItem> items = CaptureHistoryStore.LoadAll();



            for (int i = 0; i < items.Count; i++) _allItems.Add(new HistoryCardItem(items[i]));







            ApplyFilter();



            UpdateDetailPane();



            UpdateStatusBar();



            _ = LoadThumbnailsAsync(_allItems.ToList());



        }



        catch (Exception ex)



        {



            AppLogger.Error("CaptureHistoryWindow.ReloadHistory 失败", ex);



        }



    }







    /// <summary>



    /// 存储层新增记录后的回调（在**后台线程**）。先显式切回 UI 线程再去抖——



    /// <c>DispatcherTimer.RunOnce</c> 挂的是"当前线程"的 dispatcher，直接在后台线程调用会让回调永不执行。



    /// </summary>



    private void OnHistoryItemRecorded(CaptureHistoryItem item)



    {



        Dispatcher.UIThread.Post(QueueMergeFromStore);



    }







    /// <summary>



    /// 存储层记录被二次编辑并更新后的回调（后台线程）。切回 UI 线程局部刷新该卡片的缩略图与尺寸。



    /// 贴图 OCR 与老历史补识别的回写也走这里（<c>CaptureHistoryStore.TrySetText</c>）。



    /// </summary>



    private void OnHistoryItemUpdated(CaptureHistoryItem item)



    {



        Dispatcher.UIThread.Post(() =>



        {



            if (_isClosed) return;



            HistoryCardItem? card = _allItems.FirstOrDefault(c => string.Equals(c.Id, item.Id, StringComparison.Ordinal));



            if (card != null)



            {



                card.Source.Width = item.Width;



                card.Source.Height = item.Height;



                card.Source.FileBytes = item.FileBytes;



                // 回写的主体就是识别文字：不带这一行，卡片第三行会永远停在"尚未识别"、详情栏也是空的



                card.Source.Text = item.Text;



                _ = ReloadSingleCardAsync(card);



                // 文字从无到有会让这条记录突然命中搜索词：不重跑筛选，命中数与列表都还是旧账



                ApplyFilter();



                if (_selected == null) UpdateDetailPane();   // 未选中时详情栏显示的是命中/已识别计数



                AppLogger.Info("CaptureHistoryWindow: 条目文字更新，已重跑筛选 id=" + item.Id



                    + " 字数=" + item.Text.Length + " 命中=" + _visibleItems.Count);



            }



            UpdateStatusBar();



        });



    }







    private async Task ReloadSingleCardAsync(HistoryCardItem card)



    {



        string path = CaptureHistoryStore.ResolveExistingThumbPath(card.Id) ?? "";



        Bitmap? bmp = null;



        if (!string.IsNullOrEmpty(path))



        {



            try



            {



                await Task.Run(() =>



                {



                    using var fs = File.OpenRead(path);



                    bmp = new Bitmap(fs);



                });



            }



            catch (Exception ex)



            {



                AppLogger.Warn("CaptureHistoryWindow: 重载卡片缩略图失败 " + card.Id + " — " + ex.GetType().Name);



            }



        }



        var oldThumb = card.Thumbnail;



        if (bmp != null)



        {



            card.Thumbnail = bmp;



            try { oldThumb?.Dispose(); } catch { }



        }



        card.NotifySourceUpdated();



        if (_selected == card)



        {



            UpdateDetailPane();



        }



    }







    /// <summary>去抖闸（UI 线程）：一次截图可能连着产出多条记录（如长截图），合并为一次刷新。</summary>



    private void QueueMergeFromStore()



    {



        if (_refreshQueued) return;



        _refreshQueued = true;



        DispatcherTimer.RunOnce(() =>



        {



            _refreshQueued = false;



            if (_isClosed) return; // 窗口已关



            MergeFromStore();



        }, TimeSpan.FromMilliseconds(250));



    }







    /// <summary>



    /// 与存储层增量合并（自动刷新专用）。刻意不走 <see cref="ReloadHistory"/>：



    /// 那会 Dispose 并重新解码全部缩略图，用户看到的是满屏卡片闪成占位图。



    /// 这里按 id 复用已有卡片，只为新增记录建卡并单张取图，同时清理已被裁剪/删除的记录。



    /// </summary>



    private void MergeFromStore()



    {



        try



        {



            IReadOnlyList<CaptureHistoryItem> items = CaptureHistoryStore.LoadAll();







            var known = new Dictionary<string, HistoryCardItem>(StringComparer.Ordinal);



            foreach (HistoryCardItem card in _allItems) known[card.Id] = card;







            var merged = new List<HistoryCardItem>(items.Count);



            var fresh = new List<HistoryCardItem>();



            for (int i = 0; i < items.Count; i++)



            {



                CaptureHistoryItem src = items[i];



                if (known.TryGetValue(src.Id, out HistoryCardItem? card))



                {



                    known.Remove(src.Id);



                    if (card.Source.Width != src.Width || card.Source.Height != src.Height || card.Source.FileBytes != src.FileBytes)



                    {



                        card.Source.Width = src.Width;



                        card.Source.Height = src.Height;



                        card.Source.FileBytes = src.FileBytes;



                        _ = ReloadSingleCardAsync(card);



                    }



                    merged.Add(card);



                    continue;



                }



                var created = new HistoryCardItem(src);



                merged.Add(created);



                fresh.Add(created);



            }







            // known 里剩下的即已消失的记录（超限裁剪或别处删除），必须释放其缩略图位图



            foreach (HistoryCardItem vanished in known.Values)



            {



                Bitmap? bmp = vanished.Thumbnail;



                vanished.Thumbnail = null;



                try { bmp?.Dispose(); } catch { }



            }







            _allItems.Clear();



            _allItems.AddRange(merged);







            if (_selected != null && !_allItems.Contains(_selected))



            {



                _selected.IsSelected = false;



                _selected = null;



            }







            ApplyFilter();



            if (_selected == null) UpdateDetailPane();



            UpdateStatusBar();



            if (fresh.Count > 0) _ = LoadThumbnailsAsync(fresh);



        }



        catch (Exception ex)



        {



            AppLogger.Error("CaptureHistoryWindow.MergeFromStore 失败", ex);



        }



    }







    /// <summary>



    /// 后台逐张读缩略图并回 UI 线程赋值。



    /// 320px JPEG 单张 20~40KB，200 张约 6MB，一次性全载可接受；



    /// 逐张 yield 是为了不让首屏卡在整批解码上。



    /// </summary>



    private async Task LoadThumbnailsAsync(List<HistoryCardItem> cards)



    {



        foreach (HistoryCardItem item in cards)



        {



            if (_isClosed) return; // 窗口已关（本批由构造函数发起，故不能问 _instance）



            // 回退解析：新记录在 thumbs/<类型>/ 下，子目录化之前的旧图仍在 thumbs/ 根



            string path = CaptureHistoryStore.ResolveExistingThumbPath(item.Id) ?? "";



            Bitmap? bmp = null;



            try



            {



                if (path.Length > 0)



                {



                    await Task.Run(() =>



                    {



                        using var fs = File.OpenRead(path);



                        bmp = new Bitmap(fs);



                    });



                }



            }



            catch (Exception ex)



            {



                AppLogger.Warn("CaptureHistoryWindow: 读取缩略图失败 " + item.Id + " — " + ex.GetType().Name);



                bmp = null;



            }



            if (bmp == null) continue;



            if (_isClosed)



            {



                // 解码这一拍期间窗口已关（OnWindowClosed 已经清过一遍卡片），再挂上去就没人释放了



                try { bmp.Dispose(); } catch { }



                return;



            }



            item.Thumbnail = bmp;



        }



    }







    /// <summary>按「来源分段 ＋ 搜索框」两个条件重建可见列表（两者是与的关系）。</summary>



    private void ApplyFilter()



    {



        var next = new List<HistoryCardItem>(_allItems.Count);



        for (int i = 0; i < _allItems.Count; i++)



        {



            HistoryCardItem card = _allItems[i];



            if (MatchesFilter(card.Source.Source) && _query.Matches(card.Source)) next.Add(card);



        }







        string? term = _query.HighlightTerm;



        bool termChanged = !string.Equals(term, _lastHighlightTerm, StringComparison.Ordinal);



        _lastHighlightTerm = term;







        // 只比数量会漏判：删掉一条又新增一条时新旧结果集一样长，内容却不同（必须逐位比）。



        // 比对通过后整批不动 —— 逐字输入时绝大多数按键不改变结果，而清空重填会让每张卡片容器



        // 重新实现，用户看到的是滚动位置跳回顶部。



        bool same = next.Count == _visibleItems.Count;



        if (same)



            for (int i = 0; i < next.Count; i++)



                if (!ReferenceEquals(next[i], _visibleItems[i])) { same = false; break; }







        if (!same || termChanged)



        {



            double keepOffset = ScrollCards.Offset.Y;



            _visibleItems.Clear();



            foreach (HistoryCardItem card in next) _visibleItems.Add(card);
            if (next.Count > 0)
            {
                string? firstPath = CaptureHistoryStore.ResolveExistingImagePath(next[0].Source.Id);
                HistoryImageViewerWindow.PreloadBoxes(firstPath);
            }



            RestoreScrollOffset(keepOffset);



        }







        UpdateEmptyState();



    }







    // -------------------- 网格自适应对齐 --------------------
    // （2026-09-10 同日迭代 5）水平对齐二态自适应：贴左 ↔ 居中。判据是"内容距铺满内容区还差多少"：
    // 差不到一列宽（192）说明网格已接近铺满，居中让零头对称分摊、左右留白相等（强迫症需求）；
    // 差得多（筛选/搜索后只剩一两张）说明这是"列表"不是"网格"，贴左才符合直觉。
    // 与第三次重构前被删掉的边距计算方案不同：这里只写枚举、不写几何，对齐方式不影响 WrapPanel
    // 的换行测量（约束宽度相同），读写不互相反馈，天然无"卡死在旧值"的时序竞态。


    /// <summary>
    /// 按内容宽度与内容区宽度的差距切换 ItemsHistory 水平对齐（≤ 一列宽 → 居中，否则贴左）。
    /// LayoutUpdated 高频触发，故方法内只做两次读 + 一次枚举比较，仅在值变化时写入。
    /// </summary>
    private void UpdateGridHorizontalAlignment()
    {
        if (_isClosed) return;

        try
        {
            double viewport = ScrollCards.Viewport.Width;
            if (viewport <= 0) viewport = ScrollCards.Bounds.Width;
            if (viewport <= 0) return;

            // Margin 16 把内容区左右各收 16，剩余空隙按内容区口径算
            var m = ItemsHistory.Margin;
            double avail = viewport - m.Left - m.Right;
            double gap = avail - ItemsHistory.Bounds.Width;

            var target = gap <= 192.0
                ? Avalonia.Layout.HorizontalAlignment.Center
                : Avalonia.Layout.HorizontalAlignment.Left;

            if (ItemsHistory.HorizontalAlignment != target)
                ItemsHistory.HorizontalAlignment = target;
        }
        catch { }
    }



    /// <summary>列表重建后把滚动位置按新的内容高度夹回来（等布局走完再量，否则拿到的是旧 Extent）。</summary>



    private void RestoreScrollOffset(double y)



    {



        if (y <= 0) return;



        Dispatcher.UIThread.Post(() =>



        {



            try



            {



                double max = Math.Max(0, ScrollCards.Extent.Height - ScrollCards.Viewport.Height);



                ScrollCards.Offset = new Vector(ScrollCards.Offset.X, Math.Min(y, max));



            }



            catch { }



        }, DispatcherPriority.Loaded);



    }







    private bool MatchesFilter(CaptureHistorySource source)



    {



        if (RadFilterSticker?.IsChecked == true) return source == CaptureHistorySource.Sticker;



        if (RadFilterLong?.IsChecked == true) return source == CaptureHistorySource.LongScroll;



        // 「截图」档 = 保存 + 剪贴板两类直出截图（贴图与长截图各有自己的档）



        if (RadFilterCapture?.IsChecked == true)



            return source == CaptureHistorySource.Save || source == CaptureHistorySource.Clipboard;



        return true;



    }







    private void OnFilterChanged(object? sender, RoutedEventArgs e)



    {



        if (!IsLoaded) return;



        ApplyFilter();



        // 选中项被筛掉时清空详情



        if (_selected != null && !_visibleItems.Contains(_selected))



        {



            _selected.IsSelected = false;



            _selected = null;



            UpdateDetailPane();



        }



        UpdateStatusBar();



    }







    // -------------------- 搜索 --------------------







    /// <summary>搜索框每敲一个字都重解析（≤200 条内存比对，不需要去抖）。</summary>



    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)



    {



        if (!IsLoaded) return;



        string raw = TxtSearch?.Text ?? "";



        HistoryQueryMode before = _query.Mode;



        _query = HistoryQuery.Parse(raw);







        if (BtnSearchClear != null) BtnSearchClear.IsVisible = raw.Trim().Length > 0;







        ApplyFilter();



        if (_selected != null && !_visibleItems.Contains(_selected))



        {



            _selected.IsSelected = false;



            _selected = null;



        }



        if (_selected == null) UpdateDetailPane();   // 未选中时详情栏显示的是命中计数，得跟着条件走



        UpdateStatusBar();







        bool isTime = _query.IsTime;



        // 分支判定进日志：用户报"搜不到"时，这行能直接区分"判成关键词了"还是"确实没命中"



        if (_query.Mode != before)



            AppLogger.Info("CaptureHistoryWindow: 搜索分支 → " + _query.Mode



                           + " 命中 " + _visibleItems.Count + " 条");



        else if (isTime)



            AppLogger.Info("CaptureHistoryWindow: 时间查询命中 " + _visibleItems.Count + " 条");







        // 浮层确认只在"刚被判成时间表达式"的那一刻弹一次；关键词每敲一个字都弹就是噪音



        if (isTime && !_lastQueryIsTime) ShowToast("按时间命中 " + _visibleItems.Count + " 条");



        _lastQueryIsTime = isTime;



    }







    private static string DescribeQuery(HistoryQuery q)



    {



        switch (q.Mode)



        {



            case HistoryQueryMode.Time:



                string span = q.Start == DateTime.MinValue



                    ? "时刻前缀 " + (q.TimePrefix ?? "")



                    : q.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 起"



                      + (q.TimePrefix != null ? " ＋ 时刻前缀 " + q.TimePrefix : "");



                return "时间表达式（" + span + "）";



            case HistoryQueryMode.Source:



                return "类型词「" + q.Term + "」";



            case HistoryQueryMode.Keyword:



                return "关键词「" + q.Term + "」";



            default:



                return "已清空（不过滤）";



        }



    }







    private void OnSearchKeyDown(object? sender, KeyEventArgs e)



    {



        if (e.Key == Key.Enter || e.Key == Key.Return)



        {



            if (MoveSelection(e.KeyModifiers.HasFlag(KeyModifiers.Shift))) e.Handled = true;



            return;



        }



        // 框里有字：第一次 Esc 只清空并留在框里；已经空了：把焦点交回窗口，下一次 Esc 才关窗



        if (e.Key == Key.Escape)



        {



            if ((TxtSearch?.Text ?? "").Length > 0)



            {



                ClearSearch();



                TxtSearch?.Focus();



                e.Handled = true;



                return;



            }



            FocusManager?.ClearFocus();



        }



    }







    private void OnSearchClearClick(object? sender, RoutedEventArgs e)



    {



        ClearSearch();



        TxtSearch?.Focus();



    }







    /// <summary>清空搜索框。TextChanged 里会统一重解析与重建列表，这里只管写回文本（同值不触发事件）。</summary>



    private void ClearSearch()



    {



        if (TxtSearch != null) TxtSearch.Text = "";



    }







    /// <summary>Enter / Shift+Enter 在命中集内循环移动选中；命中集为空时不动。</summary>



    private bool MoveSelection(bool backward)



    {



        if (_visibleItems.Count == 0) return false;



        int index = _selected != null ? _visibleItems.IndexOf(_selected) : -1;



        int next = backward



            ? (index <= 0 ? _visibleItems.Count - 1 : index - 1)



            : (index + 1 >= _visibleItems.Count ? 0 : index + 1);



        HistoryCardItem target = _visibleItems[next];



        SelectItem(target);



        ScrollCardIntoView(target);



        return true;



    }







    /// <summary>卡片容器实现后补一次命中高亮（绑定塞不了 Run，只能在这里接）。</summary>



    private void OnCardSummaryLoaded(object? sender, RoutedEventArgs e)



    {



        if (sender is not TextBlock tb || tb.DataContext is not HistoryCardItem card) return;



        ApplyHighlight(tb, card.SummaryText);



    }







    /// <summary>



    /// 把命中词换成带底色的 Run。三处高亮（卡片文字行 / 详情栏文字块 / 空态回显词）共用这一份。



    ///



    /// 只标第一处命中：卡片那行只有一行宽，标多了整行都是底色反而看不出重点。



    /// 空格分词的每个词各标一处（整串去匹配原文，多词查询永远标不上）。



    /// </summary>



    private void ApplyHighlight(TextBlock target, string text)



    {



        string? term = _query.HighlightTerm;



        List<(int Start, int Length)> spans = string.IsNullOrEmpty(term) || text.Length == 0



            ? new List<(int, int)>()



            : CollectHighlightSpans(term!, text);



        IBrush? mark = spans.Count > 0 ? TryResolveMarkBrush() : null;



        if (mark == null)



        {



            target.Inlines = null;



            return;



        }







        var runs = new InlineCollection();



        int cursor = 0;



        for (int i = 0; i < spans.Count; i++)



        {



            (int start, int length) = spans[i];



            if (start > cursor) runs.Add(new Run(text.Substring(cursor, start - cursor)));



            runs.Add(new Run(text.Substring(start, length)) { Background = mark });



            cursor = start + length;



        }



        if (cursor < text.Length) runs.Add(new Run(text.Substring(cursor)));



        target.Inlines = runs;



    }







    /// <summary>



    /// 逐词取第一处命中，按位置排序并合掉重叠区间（两个词落在同一段文字上时不能叠两层底色）。



    ///



    /// 词是在**原样文本**上找的，而归一后的词可能与原文形态不同（全角「１４:０３」折成半角后原文里



    /// 找不到这一串）——那种情况这个词就是标不上。高亮只是辅助，命中判定用的是双侧归一的 SearchText。



    /// </summary>



    private static List<(int Start, int Length)> CollectHighlightSpans(string term, string text)



    {



        var found = new List<(int Start, int Length)>();



        int from = 0;



        while (from < term.Length)



        {



            int space = term.IndexOf(' ', from);



            int end = space < 0 ? term.Length : space;



            int length = end - from;



            if (length > 0)



            {



                int idx = text.IndexOf(term.AsSpan(from, length), StringComparison.OrdinalIgnoreCase);



                if (idx >= 0) found.Add((idx, length));



            }



            from = end + 1;



        }







        found.Sort((a, b) => a.Start.CompareTo(b.Start));



        var merged = new List<(int Start, int Length)>(found.Count);



        for (int i = 0; i < found.Count; i++)



        {



            if (merged.Count > 0)



            {



                (int start, int length) = merged[merged.Count - 1];



                int stop = start + length;



                if (found[i].Start < stop)



                {



                    merged[merged.Count - 1] = (start, Math.Max(stop, found[i].Start + found[i].Length) - start);



                    continue;



                }



            }



            merged.Add(found[i]);



        }



        return merged;



    }







    private IBrush? _markBrush;



    private ThemeVariant? _markBrushVariant;



    private bool _markBrushMissing;







    /// <summary>高亮底色走 Token（代码取色必须带 ActualThemeVariant，AGENTS.md §7）；取不到就不再重试，避免每张卡片刷一条日志。</summary>



    private IBrush? TryResolveMarkBrush()



    {



        if (_markBrushMissing) return null;



        try



        {



            if (_markBrush != null && Equals(_markBrushVariant, ActualThemeVariant)) return _markBrush;



            if (this.TryFindResource("SctSearchMarkBrush", ActualThemeVariant, out object? res) && res is IBrush brush)



            {



                _markBrush = brush;



                _markBrushVariant = ActualThemeVariant;



                return brush;



            }



        }



        catch (Exception ex)



        {



            AppLogger.Warn("CaptureHistoryWindow: 取命中高亮底色失败 — " + ex.GetType().Name);



            return null;



        }



        _markBrushMissing = true;



        AppLogger.Warn("CaptureHistoryWindow: 未找到 SctSearchMarkBrush，本轮不做命中高亮。");



        return null;



    }







    /// <summary>把某张卡片滚进可视区。网格不虚拟化，容器就在可视树里，按 DataContext 认人。</summary>



    private void ScrollCardIntoView(HistoryCardItem card)



    {



        try



        {



            Control? host = FindCardHost(ItemsHistory, card);



            if (host == null) return;



            Point? origin = host.TranslatePoint(new Point(0, 0), ScrollCards);



            if (origin == null) return;







            double top = origin.Value.Y;



            double bottom = top + host.Bounds.Height;



            double offset = ScrollCards.Offset.Y;



            double viewport = ScrollCards.Viewport.Height;



            double target = offset;



            if (top < offset + 8) target = top - 8;



            else if (bottom > offset + viewport - 8) target = bottom - viewport + 8;



            double max = Math.Max(0, ScrollCards.Extent.Height - viewport);



            target = Math.Clamp(target, 0, max);



            if (Math.Abs(target - offset) < 0.5) return;



            ScrollCards.Offset = new Vector(ScrollCards.Offset.X, target);



        }



        catch (Exception ex)



        {



            AppLogger.Warn("CaptureHistoryWindow: 滚动到命中卡片失败 — " + ex.GetType().Name);



        }



    }







    private static Control? FindCardHost(Visual root, HistoryCardItem card)



    {



        foreach (Visual child in root.GetVisualChildren())



        {



            if (child is Control c && c.Classes.Contains("historyCard") && ReferenceEquals(c.DataContext, card))



                return c;



            Control? deeper = FindCardHost(child, card);



            if (deeper != null) return deeper;



        }



        return null;



    }







    // -------------------- 选中与详情 --------------------







    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)



    {



        try



        {



            if (sender is not Control ctrl || ctrl.DataContext is not HistoryCardItem item) return;







            // 右键只做选中（菜单由框架弹出），左键双击 = 打开图片查看器（支持原图翻页、旋转与搜索词高亮标注）



            // 左键单击已选中的卡片 = 切换收起详情栏；右键仅保持选中，双击走下方查看器分支
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 1 && ReferenceEquals(_selected, item))
            {
                SelectItem(null);
            }
            else
            {
                SelectItem(item);
            }



            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 2)



            {



                int idx = _visibleItems.IndexOf(item);



                string? term = _query.HighlightTerm;

                if (string.IsNullOrWhiteSpace(term))

                {

                    string rawInput = TxtSearch?.Text?.Trim() ?? "";

                    if (rawInput.Length > 0) term = rawInput;

                }

                HistoryImageViewerWindow.ShowFor(_visibleItems, idx >= 0 ? idx : 0, term);



            }



        }



        catch (Exception ex)



        {



            AppLogger.Warn("CaptureHistoryWindow.OnCardPointerPressed 异常：" + ex.GetType().Name);



        }



    }







    private void SelectItem(HistoryCardItem? item)



    {



        if (_selected != null) _selected.IsSelected = false;



        _selected = item;



        if (_selected != null) _selected.IsSelected = true;



        UpdateDetailPane();



        UpdateStatusBar();

        // 预热缓存：选中卡片瞬间提前预取 OCR 识别框，用户双击打开图片查看器 0 毫秒秒出高亮
        if (item != null)
        {
            string? path = CaptureHistoryStore.ResolveExistingImagePath(item.Source.Id);
            HistoryImageViewerWindow.PreloadBoxes(path);
        }



    }







    /// <summary>刷新右侧详情栏（含大图预览与按钮可用态）。</summary>



    private void UpdateDetailPane()



    {



        try



        {



            bool has = _selected != null;

            // 详情栏默认收起，点选卡片后才展开（2026-09-10 需求：打开历史不再常驻右侧详情栏）。
            // 收起时 PnlDetailCounts / TxtDetailPlaceholder 一并隐藏——命中数在底部状态栏仍可见。
            GridMain.ColumnDefinitions = new ColumnDefinitions(has ? "*,1,300" : "*,0,0");
            SepDetail.IsVisible = has;
            PnlDetail.IsVisible = has;



            // 未选中：搜索中把"命中 / 已识别 / 未识别"搬进详情栏——搜不到时用户最需要知道的



            // 就是到底有多少条识别过，否则他会以为搜索坏了（设计稿 §06）



            PnlDetailCounts.IsVisible = !has && !_query.IsEmpty;



            TxtDetailPlaceholder.IsVisible = !has && _query.IsEmpty;



            ScrollDetail.IsVisible = has;







            // 上一张预览图必须显式释放：这是全图 PNG 解码结果，不释放会稳定漏内存



            ImgPreview.Source = null;



            try { _previewBitmap?.Dispose(); } catch { }



            _previewBitmap = null;







            if (!has || _selected == null)



            {



                if (PnlDetailCounts.IsVisible) UpdateDetailCounts();



                return;



            }







            CaptureHistoryItem src = _selected.Source;



            TxtDetailTime.Text = src.CapturedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);



            TxtDetailSize.Text = src.Width + " × " + src.Height + " px";



            TxtDetailSource.Text = src.SourceDisplayName;



            TxtDetailBytes.Text = FormatBytes(src.FileBytes);







            RowDetailPath.IsVisible = !string.IsNullOrWhiteSpace(src.SavedPath);



            TxtDetailPath.Text = src.SavedPath;







            string ocrText = (src.Text ?? "").Trim();



            TxtDetailText.IsVisible = ocrText.Length > 0;



            if (ocrText.Length > 0)



            {



                TxtDetailTextMeta.Text = ocrText.Length + " 字";



                TxtDetailText.Text = ocrText;



                ApplyHighlight(TxtDetailText, ocrText);



            }



            else



            {



                TxtDetailTextMeta.Text = "尚未识别";



                TxtDetailText.Text = "";



                TxtDetailText.Inlines = null;



            }







            string? full = CaptureHistoryStore.ResolveExistingImagePath(src.Id);



            if (full != null)



            {



                try



                {



                    using var fs = File.OpenRead(full);



                    _previewBitmap = new Bitmap(fs);



                    ImgPreview.Source = _previewBitmap;



                }



                catch (Exception ex)



                {



                    AppLogger.Warn("CaptureHistoryWindow: 加载预览图失败 — " + ex.GetType().Name);



                }



            }



        }



        catch (Exception ex)



        {



            AppLogger.Warn("CaptureHistoryWindow.UpdateDetailPane 异常：" + ex.GetType().Name);



        }



    }







    /// <summary>



    /// 详情栏的三个计数（搜索中且未选中时显示）。<c>已识别</c> 走的是「有没有可搜文本」——



    /// 用户搜不到东西时，最先要确认的就是历史里到底识别过多少条，否则会以为搜索坏了。



    /// </summary>



    private void UpdateDetailCounts()



    {



        int hit = _visibleItems.Count;



        int done = 0;



        for (int i = 0; i < hit; i++)



            if (!string.IsNullOrWhiteSpace(_visibleItems[i].Source.Text)) done++;



        TxtCountHit.Text = hit + " / " + _allItems.Count + " 条";



        TxtCountDone.Text = done + " 条";



        TxtCountPending.Text = (hit - done) + " 条";



    }







    private void UpdateEmptyState()



    {



        bool empty = _visibleItems.Count == 0;



        PnlEmptyState.IsVisible = empty;



        ScrollCards.IsVisible = !empty;



        if (!empty) return;







        bool searching = !_query.IsEmpty;



        if (BtnEmptyClearSearch != null) BtnEmptyClearSearch.IsVisible = searching;



        if (IconEmpty != null) IconEmpty.Data = Geometry.Parse(searching ? OcrIconGeometry.Search : OcrIconGeometry.TrayHistory);







        // 搜出来是空的：回显关键词（三处高亮之一）＋说明搜的是什么，并给一键清空



        if (searching)



        {



            string term = _query.Mode == HistoryQueryMode.Keyword



                ? _query.Term



                : (TxtSearch?.Text ?? "").Trim();



            TxtEmptyTitle.Text = "没有匹配的历史记录";



            string hint = term.Length > 0



                ? "「" + term + "」没有命中的记录。文字只搜识别出的正文与贴图源文本；时间请写成 2026-09-06 / 昨天 / 14:03 这类形式。"



                : "换一个关键词或时间表达式试试；关键词搜的是识别出的正文与贴图源文本。";



            // 日期形态与类型词优先于关键词是有意定的硬规则（否则搜 14:03 会命中一堆正文），



            // 但被这样判掉的用户看不到任何解释，只会以为搜索坏了。这里说明，不做自动回退——



            // 回退会让每敲一个字的结果集都抖一次。



            if (_query.Mode == HistoryQueryMode.Time || _query.Mode == HistoryQueryMode.Source)



                hint += "当前按" + (_query.Mode == HistoryQueryMode.Time ? "时间" : "类型")



                    + "理解；想搜识别文字请换个说法，或用「类型:截图」这种带前缀的写法明确指定分类。";



            TxtEmptyHint.Text = hint;



            ApplyHighlight(TxtEmptyHint, hint);



            BtnEmptyOpenSettings.IsVisible = false;



            return;



        }



        TxtEmptyHint.Inlines = null;







        // 一条记录都没有 vs 筛选后为空，文案不同；关闭档还要引导去设置



        if (_allItems.Count > 0)



        {



            TxtEmptyTitle.Text = "此分类下暂无记录";



            TxtEmptyHint.Text = "切换上方筛选查看其他来源的历史。";



            BtnEmptyOpenSettings.IsVisible = false;



            return;



        }







        string mode = AppSettings.NormalizeCaptureHistoryMode(AppSettings.Current.CaptureHistoryMode);



        bool off = string.Equals(mode, "Off", StringComparison.Ordinal);



        TxtEmptyTitle.Text = "暂无截图历史";



        TxtEmptyHint.Text = off



            ? "截图历史当前已关闭。可在「设置 → 通用与存储 → 截图历史」中开启收集。"



            : "还没有产出记录。下一次截图或贴图后就会出现在这里。";



        BtnEmptyOpenSettings.IsVisible = off;



    }







    private void UpdateStatusBar()



    {



        try



        {



            // 「共 N 张」始终是全部记录，命中数另起一段——搜索时把总数变成命中数会让用户以为记录被删了



            TxtStatusCount.Text = _allItems.Count.ToString(CultureInfo.InvariantCulture);



            bool searching = !_query.IsEmpty;



            PnlStatusHit.IsVisible = searching;



            if (searching) TxtStatusHit.Text = _visibleItems.Count.ToString(CultureInfo.InvariantCulture);







            long bytes = 0;



            for (int i = 0; i < _allItems.Count; i++) bytes += _allItems[i].Source.FileBytes;



            TxtStatusUsage.Text = "占用 " + FormatBytes(bytes);







            TxtStatusHint.Text = searching ? "Enter 跳到下一个命中 · Esc 清空搜索"



                : (_selected == null ? "" : "已选择 1 张");



        }



        catch { }



    }







    private static string FormatBytes(long bytes)



    {



        if (bytes <= 0) return "0 KB";



        if (bytes < 1024L * 1024L) return (bytes / 1024.0).ToString("F0", CultureInfo.InvariantCulture) + " KB";



        if (bytes < 1024L * 1024L * 1024L) return (bytes / 1024.0 / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " MB";



        return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("F2", CultureInfo.InvariantCulture) + " GB";



    }







    // -------------------- 操作：复制 / 贴图 / 另存 / 打开位置 / 删除 --------------------







    private void OnCopyClick(object? sender, RoutedEventArgs e) => CopyCurrent();



    private void OnCtxCopyClick(object? sender, RoutedEventArgs e) { SelectFromMenu(sender); CopyCurrent(); }







    private async void CopyCurrent()



    {



        if (_selected == null) return;



        try



        {



            string? full = CaptureHistoryStore.ResolveExistingImagePath(_selected.Id);



            if (full == null) { ShowToast("图片文件已丢失"); ReloadHistory(); return; }







            using var bmp = new System.Drawing.Bitmap(full);



            // 烘焙阴影带/圆角的历史图必须保 alpha 写剪贴板，否则那圈半透明阴影在接收端直接丢失



            if (!await ClipboardService.SetImageAutoAlphaAsync(bmp)) return;



            ShowToast("已复制到剪贴板");



        }



        catch (Exception ex)



        {



            AppLogger.Error("CaptureHistoryWindow: 复制历史图片失败", ex);



            ShowToast("复制失败");



        }



    }







    private void OnStickerClick(object? sender, RoutedEventArgs e) => StickerCurrent();



    private void OnCtxStickerClick(object? sender, RoutedEventArgs e) { SelectFromMenu(sender); StickerCurrent(); }







    private void StickerCurrent()



    {



        if (_selected == null) return;



        try



        {



            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)



            {



                var existing = desktop.Windows.OfType<StickerWindow>().FirstOrDefault(w => w.HistoryId == _selected.Id);



                if (existing != null)



                {



                    existing.Activate();



                    StickerFocusManager.SetFocus(existing);



                    ShowToast("已置顶显示");



                    return;



                }



            }







            string? full = CaptureHistoryStore.ResolveExistingImagePath(_selected.Id);



            if (full == null) { ShowToast("图片文件已丢失"); ReloadHistory(); return; }







            // 检查是否存在对应的干净底图与标注存档，供贴出后进入标注模式恢复可编辑图元



            string? cleanPath = CaptureHistoryStore.ResolveCleanImagePath(_selected.Id);



            System.Drawing.Bitmap? cleanBmp = CaptureHistoryStore.LoadBitmapDetached(cleanPath);







            var archive = StickerArchiveStore.TryRead(_selected.Id);







            // 位图所有权交给贴图窗口（它在 OnClosed 里释放），这里不能 using



            var bmp = CaptureHistoryStore.LoadBitmapDetached(full);



            if (bmp == null) { ShowToast("加载图片失败"); return; }







            var sticker = new StickerWindow(bmp,



                cleanAnnotationSource: cleanBmp,



                annotationShapes: archive?.Shapes,



                annotationTexts: archive?.Texts);







            sticker.SetHistoryId(_selected.Id);







            if (archive != null && (archive.Card.ContentRadiusPx > 0 || archive.Card.StrokeWidthPx > 0 || archive.Card.BakedShadow))



            {



                sticker.ApplyBakedCaptureEffects(new Core.Capture.SelectionEffects.CardGeometry



                {



                    ContentRadiusPx = (float)archive.Card.ContentRadiusPx,



                    StrokeWidthPx = (float)archive.Card.StrokeWidthPx,



                }, archive.Card.BakedShadow);



            }



            else



            {



                // 历史条目落盘只剩一张成品图、没记任何效果参数，外缘形状只能从像素回读。



                // 不调这一句有两个观感缺陷：圆角条目透出灰白棋盘格、方角窗体阴影从圆角后面漏出。



                // 勾过阴影的条目则两个阴影并存：图里那圈烘焙阴影（素材）下垫棋盘格承托，



                // 窗体阴影（UI 状态指示）的光晕包在棋盘格（位图）外沿（2026-09-07 用户拍板）。



                // 两者都是 0 / false（四角不透明的普通图）时不登记：那种图的棋盘格该照常显示。



                // ⚠ 回读只有"外沿总半径"一个数，拆不出内容半径 + 描边宽，只能按无描边登记（光晕基准线



                //    就落在这条总半径的等距线上）；自家截图流程走 CaptureSession 的 CardGeometryOf，不会退化到这里。



                float outerRadiusPx = Core.Capture.SelectionEffects.ProbeOuterRadiusPx(bmp);



                bool hasBakedShadow = Core.Capture.SelectionEffects.HasBakedShadowBand(bmp);



                if (outerRadiusPx > 0f || hasBakedShadow)



                {



                    sticker.ApplyBakedCaptureEffects(new Core.Capture.SelectionEffects.CardGeometry



                    {



                        ContentRadiusPx = outerRadiusPx,



                    }, hasBakedShadow);



                    AppLogger.Info($"CaptureHistoryWindow: 历史贴图 外沿圆角={outerRadiusPx:F0}px 描边=未知按0 自带阴影带={hasBakedShadow}");



                }



            }







            if (archive != null && (archive.State.Width > 0 || archive.State.Height > 0))



            {



                sticker.Position = new PixelPoint(archive.State.X, archive.State.Y);



            }







            sticker.Show();



            sticker.Activate();



            StickerFocusManager.SetFocus(sticker);







            if (archive != null)



            {



                Dispatcher.UIThread.Post(() => sticker.ApplyArchiveState(archive), DispatcherPriority.Loaded);



            }







            ShowToast("已贴到屏幕");



        }



        catch (Exception ex)



        {



            AppLogger.Error("CaptureHistoryWindow: 从历史创建贴图失败", ex);



            ShowToast("贴图失败");



        }



    }







    private void OnSaveAsClick(object? sender, RoutedEventArgs e) => _ = SaveAsCurrentAsync();



    private void OnCtxSaveAsClick(object? sender, RoutedEventArgs e) { SelectFromMenu(sender); _ = SaveAsCurrentAsync(); }







    private async Task SaveAsCurrentAsync()



    {



        if (_selected == null) return;



        try



        {



            string? full = CaptureHistoryStore.ResolveExistingImagePath(_selected.Id);



            if (full == null) { ShowToast("图片文件已丢失"); ReloadHistory(); return; }







            IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions



            {



                Title = "另存为图片",



                SuggestedFileName = "yuSnip_" + _selected.Source.CapturedAt.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".png",



                DefaultExtension = "png",



                FileTypeChoices = new[]



                {



                    new FilePickerFileType("PNG Image") { Patterns = new[] { "*.png" } },



                    new FilePickerFileType("JPEG Image") { Patterns = new[] { "*.jpg", "*.jpeg" } },



                    new FilePickerFileType("Bitmap Image") { Patterns = new[] { "*.bmp" } },



                },



            });







            string? dest = file?.TryGetLocalPath();



            if (string.IsNullOrWhiteSpace(dest)) return;







            // 目标就是 PNG 时直接拷文件，避免无意义的解码 + 重编码



            if (dest!.EndsWith(".png", StringComparison.OrdinalIgnoreCase))



            {



                File.Copy(full, dest, true);



            }



            else



            {



                using var bmp = new System.Drawing.Bitmap(full);



                ImageExportService.Save(bmp, dest);



            }



            AppLogger.Info("CaptureHistoryWindow: 历史图片已另存为 " + dest);



            ShowToast("已保存");



        }



        catch (Exception ex)



        {



            AppLogger.Error("CaptureHistoryWindow: 另存历史图片失败", ex);



            ShowToast("保存失败");



        }



    }







    private void OnCtxOpenLocationClick(object? sender, RoutedEventArgs e) { SelectFromMenu(sender); OpenLocationCurrent(); }







    private void OpenLocationCurrent()



    {



        if (_selected == null) return;



        try



        {



            string? path = ResolveRevealPath(_selected.Source);



            if (path == null)



            {



                ShowToast("图片文件已丢失");



                ReloadHistory();



                return;



            }



            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });



        }



        catch (Exception ex)



        {



            AppLogger.Warn("CaptureHistoryWindow: 打开文件位置失败 — " + ex.GetType().Name);



            ShowToast("无法打开文件位置");



        }



    }







    /// <summary>



    /// 「打开文件位置」实际要定位的文件。优先用户当初保存的原图；剪贴板与贴图两类记录



    /// 从不产生原图（SavedPath 恒为空），回退到历史目录里的全图副本——每条记录都有这份，



    /// 窗口的预览/复制/贴图/另存用的都是它（该副本按来源分类型子目录，回退解析已覆盖新旧位置）。



    /// 原图被用户删除或移动时同样走这条回退。两者都不可用时返回 null。



    /// </summary>



    private static string? ResolveRevealPath(CaptureHistoryItem src)



    {



        if (!string.IsNullOrWhiteSpace(src.SavedPath) && File.Exists(src.SavedPath)) return src.SavedPath;



        return CaptureHistoryStore.ResolveExistingImagePath(src.Id);



    }







    private void OnDeleteClick(object? sender, RoutedEventArgs e) => DeleteCurrent();



    private void OnCtxDeleteClick(object? sender, RoutedEventArgs e) { SelectFromMenu(sender); DeleteCurrent(); }







    private void DeleteCurrent()



    {



        if (_selected == null) return;



        try



        {



            string id = _selected.Id;



            if (!CaptureHistoryStore.Delete(id)) { ShowToast("删除失败"); return; }



            ReloadHistory();



            ShowToast("已删除");



        }



        catch (Exception ex)



        {



            AppLogger.Error("CaptureHistoryWindow: 删除历史失败", ex);



            ShowToast("删除失败");



        }



    }







    private void OnClearAllClick(object? sender, RoutedEventArgs e)



    {



        try



        {



            if (_allItems.Count == 0) { ShowToast("历史已是空的"); return; }



            CaptureHistoryStore.ClearAll();



            ReloadHistory();



            ShowToast("已清空截图历史");



        }



        catch (Exception ex)



        {



            AppLogger.Error("CaptureHistoryWindow: 清空历史失败", ex);



            ShowToast("清空失败");



        }



    }







    private void OnOpenSettingsClick(object? sender, RoutedEventArgs e)



    {



        try { (Avalonia.Application.Current as App)?.OpenSettingsWindow(); }



        catch (Exception ex) { AppLogger.Warn("CaptureHistoryWindow: 打开设置失败 — " + ex.GetType().Name); }



    }







    /// <summary>



    /// 右键菜单点击时把作用对象切到被右键的那张卡片。



    /// 卡片的 PointerPressed 已经先行选中过，这里是兜底（例如键盘唤出菜单）。



    /// </summary>



    private void SelectFromMenu(object? sender)



    {



        try



        {



            if (sender is MenuItem mi && mi.DataContext is HistoryCardItem item && !ReferenceEquals(item, _selected))



            {



                SelectItem(item);



            }



        }



        catch { }



    }







    // -------------------- 窗口级 --------------------







    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)



    {



        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);



    }







    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;







    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();







    private void OnWindowKeyDown(object? sender, KeyEventArgs e)



    {



        // 焦点在输入框里时一律放行给输入框：否则在搜索框内按 Delete 会直接删掉选中的历史记录



        bool typing = FocusManager?.GetFocusedElement() is TextBox;







        if (e.Key == Key.Escape)



        {



            // 搜索框自己有「先清空、再关窗」两段式逻辑，这里不抢它的第一次 Esc



            if (typing) return;



            if (!string.IsNullOrEmpty(TxtSearch?.Text)) { ClearSearch(); e.Handled = true; return; }



            Close();



            e.Handled = true;



            return;



        }



        if (typing) return;







        if (e.Key == Key.Delete && _selected != null) { DeleteCurrent(); e.Handled = true; return; }



        // Ctrl+F 或 / 聚焦搜索框（仅本窗内生效）



        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))



        {



            TxtSearch?.Focus();



            TxtSearch?.SelectAll();



            e.Handled = true;



            return;



        }



        if (e.Key == Key.OemQuestion && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))



        {



            TxtSearch?.Focus();



            e.Handled = true;



        }



    }







    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)



    {



        if (WindowState == WindowState.Normal)



        {



            ScheduleSaveWindowSize();



        }



    }







    private void ScheduleSaveWindowSize()



    {



        if (_saveSizeDebounceTimer == null)



        {



            _saveSizeDebounceTimer = new DispatcherTimer



            {



                Interval = TimeSpan.FromMilliseconds(500)



            };



            _saveSizeDebounceTimer.Tick += (s, e) =>



            {



                _saveSizeDebounceTimer?.Stop();



                SaveCurrentWindowSize();



            };



        }



        _saveSizeDebounceTimer.Stop();



        _saveSizeDebounceTimer.Start();



    }







    private void SaveCurrentWindowSize()



    {



        try



        {



            if (WindowState != WindowState.Normal) return;



            double w = Bounds.Width > 0 ? Bounds.Width : Width;



            double h = Bounds.Height > 0 ? Bounds.Height : Height;



            if (w >= MinWidth && h >= MinHeight)



            {



                var settings = AppSettings.Current;



                if (Math.Abs(settings.CaptureHistoryWindowWidth - w) > 1 || Math.Abs(settings.CaptureHistoryWindowHeight - h) > 1)



                {



                    settings.CaptureHistoryWindowWidth = Math.Round(w);



                    settings.CaptureHistoryWindowHeight = Math.Round(h);



                    settings.Save();



                }



            }



        }



        catch { }



    }







    private void EnsureWindowSizeWithinScreen()



    {



        try



        {



            var screen = Screens.Primary ?? Screens.All.FirstOrDefault();



            if (screen == null) return;







            double scale = screen.Scaling;



            if (scale <= 0) scale = 1.0;



            double waH = screen.WorkingArea.Height / scale;



            double waW = screen.WorkingArea.Width / scale;



            if (waH <= 0 || waW <= 0) return;







            if (Width > waW) Width = Math.Max(MinWidth, waW);



            if (Height > waH) Height = Math.Max(MinHeight, waH);



        }



        catch { }



    }







    private void OnWindowClosed(object? sender, EventArgs e)



    {



        _isClosed = true; // 先置位：在飞的异步装载下一拍就会看到，别等 DisposeCardThumbnails 之后再断



        _saveSizeDebounceTimer?.Stop();



        SaveCurrentWindowSize();







        if (ReferenceEquals(_instance, this)) _instance = null;







        CaptureHistoryStore.ItemRecorded -= OnHistoryItemRecorded;







        ImgPreview.Source = null;



        try { _previewBitmap?.Dispose(); } catch { }



        _previewBitmap = null;



        DisposeCardThumbnails();



    }







    /// <summary>释放全部已加载的缩略图位图（重载列表与关窗时都要调）。</summary>



    private void DisposeCardThumbnails()



    {



        for (int i = 0; i < _allItems.Count; i++)



        {



            Bitmap? bmp = _allItems[i].Thumbnail;



            _allItems[i].Thumbnail = null;



            try { bmp?.Dispose(); } catch { }



        }



        _visibleItems.Clear();



    }







    /// <summary>Logo 与窗口图标随主题切换（与 OCR / 设置窗口同一套资产与回退顺序）。</summary>



    private void UpdateAppIcons()



    {



        try



        {



            bool isDark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;



            Uri uri;



            if (isDark)



            {



                uri = Avalonia.Platform.AssetLoader.Exists(new Uri("avares://yuSnip/Assets/logo_dark.ico"))



                    ? new Uri("avares://yuSnip/Assets/logo_dark.ico")



                    : new Uri("avares://yuSnip/Assets/Black.ico");



            }



            else



            {



                uri = Avalonia.Platform.AssetLoader.Exists(new Uri("avares://yuSnip/Assets/logo_light.ico"))



                    ? new Uri("avares://yuSnip/Assets/logo_light.ico")



                    : new Uri("avares://yuSnip/Assets/White.ico");



            }







            Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(uri));



            if (ImgAppLogo != null)



            {



                ImgAppLogo.Source = new Bitmap(Avalonia.Platform.AssetLoader.Open(uri));



            }



        }



        catch (Exception ex)



        {



            AppLogger.Warn("CaptureHistoryWindow.UpdateAppIcons 失败：" + ex.GetType().Name);



        }



    }







    // -------------------- Toast --------------------







    private async void ShowToast(string message)



    {



        try



        {



            if (ToastPanel == null || TxtToast == null) return;



            TxtToast.Text = message;



            ToastPanel.IsVisible = true;



            ToastPanel.Opacity = 1.0;







            int token = ++_toastToken;



            await Task.Delay(1800);



            if (_toastToken == token)



            {



                ToastPanel.IsVisible = false;



                ToastPanel.Opacity = 0.0;



            }



        }



        catch { }



    }



}



