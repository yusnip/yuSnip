using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Ai;

namespace ScreenCaptureTool.Windows
{
    /// <summary>
    /// 可勾选的模型列表项视图模型（供 AiProviderEditWindow 强类型绑定与实时联动）
    /// </summary>
    public sealed class AiSelectableModelItem : INotifyPropertyChanged
    {
        private bool _isChecked;
        private bool _isDefault;

        public string ModelId { get; set; } = string.Empty;
        public string DisplayName => ModelId;

        public Action? OnCheckChanged { get; set; }

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked != value)
                {
                    _isChecked = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                    OnCheckChanged?.Invoke();
                }
            }
        }

        public bool IsDefault
        {
            get => _isDefault;
            set
            {
                if (_isDefault != value)
                {
                    _isDefault = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDefault)));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public partial class AiProviderEditWindow : Window
    {
        private readonly bool _isNew;
        private readonly AiApiConfig _workingConfig;
        private bool _isSyncingModel;

        private readonly List<AiSelectableModelItem> _allModelItems = new();
        private List<AiSelectableModelItem> _currentFilteredItems = new();

        /// <summary>
        /// 常用多模态视觉模型的智能推断关键字清单
        /// </summary>
        private static readonly string[] VisionKeywords = new[]
        {
            "vision", "vl", "4o", "4.5", "sonnet", "opus", "gemini-1.5", "gemini-2",
            "qvq", "glm-4v", "pixtral", "omni", "multimodal", "claude-3-5", "claude-3-7",
            "internvl", "minicpm", "deepseek-vl"
        };

        public AiApiConfig ResultConfig { get; private set; }
        public bool IsConfirmed { get; private set; }

        public AiProviderEditWindow(AiApiConfig? configToEdit = null)
        {
            InitializeComponent();

            _isNew = configToEdit == null;
            _workingConfig = configToEdit?.Clone() ?? new AiApiConfig
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "新服务商",
                Protocol = "OpenAI",
                BaseUrl = "https://api.openai.com/v1",
                ApiKey = "",
                DefaultModel = "",
                IsEnabled = true,
                CustomModels = new List<string>(),
                CachedModels = new List<string>()
            };

            ResultConfig = _workingConfig;

            TxtWindowTitle.Text = _isNew ? "添加 API 服务商" : $"编辑 API 服务商 - {_workingConfig.Name}";
            LoadConfigData();

            AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        }

        private void LoadConfigData()
        {
            TxtName.Text = _workingConfig.Name;

            if (_workingConfig.IsEnabled)
            {
                RadEnable.IsChecked = true;
            }
            else
            {
                RadDisable.IsChecked = true;
            }

            bool isAnthropic = string.Equals(_workingConfig.Protocol, "Anthropic", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(_workingConfig.Protocol, "Claude", StringComparison.OrdinalIgnoreCase);
            if (isAnthropic)
            {
                RadAnthropic.IsChecked = true;
            }
            else
            {
                RadOpenAi.IsChecked = true;
            }

            RadAnthropic.IsCheckedChanged += (_, _) =>
            {
                if (RadAnthropic.IsChecked == true)
                {
                    _workingConfig.Protocol = "Anthropic";
                    if (string.IsNullOrWhiteSpace(TxtBaseUrl.Text) || TxtBaseUrl.Text == "https://api.openai.com/v1")
                    {
                        TxtBaseUrl.Text = "https://api.anthropic.com/v1";
                    }
                }
                else
                {
                    _workingConfig.Protocol = "OpenAI";
                    if (string.IsNullOrWhiteSpace(TxtBaseUrl.Text) || TxtBaseUrl.Text == "https://api.anthropic.com/v1")
                    {
                        TxtBaseUrl.Text = "https://api.openai.com/v1";
                    }
                }
            };

            TxtBaseUrl.Text = _workingConfig.BaseUrl;
            TxtApiKey.Text = _workingConfig.ApiKey;
            TxtSpecifiedModel.Text = _workingConfig.Model;

            // 载入已有模型池
            _workingConfig.CachedModels ??= new List<string>();
            _workingConfig.CustomModels ??= new List<string>();

            // 兼容性处理：若 CachedModels 为空但 CustomModels 有值，以 CustomModels 作为初始候选池
            var pool = new List<string>(_workingConfig.CachedModels);
            if (pool.Count == 0 && _workingConfig.CustomModels.Count > 0)
            {
                pool.AddRange(_workingConfig.CustomModels);
            }

            string specified = (_workingConfig.Model ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(specified) && !pool.Any(m => string.Equals(m, specified, StringComparison.OrdinalIgnoreCase)))
            {
                pool.Insert(0, specified);
            }

            PopulateModelItems(pool, _workingConfig.CustomModels, specified);

            TxtSpecifiedModel.TextChanged += (_, _) =>
            {
                if (_isSyncingModel) return;
                string val = (TxtSpecifiedModel.Text ?? "").Trim();
                _workingConfig.Model = val;

                foreach (var item in _allModelItems)
                {
                    bool isDefault = !string.IsNullOrWhiteSpace(val) && string.Equals(item.ModelId, val, StringComparison.OrdinalIgnoreCase);
                    item.IsDefault = isDefault;
                    if (isDefault && !item.IsChecked)
                    {
                        item.IsChecked = true;
                    }
                }
                UpdateModelStats();
            };
        }

        private void PopulateModelItems(List<string> pool, List<string> enabledList, string? defaultModel)
        {
            _allModelItems.Clear();
            var enabledSet = new HashSet<string>(enabledList ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(defaultModel))
            {
                enabledSet.Add(defaultModel);
            }

            foreach (var id in pool.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct())
            {
                bool isDefault = !string.IsNullOrWhiteSpace(defaultModel) && string.Equals(id, defaultModel, StringComparison.OrdinalIgnoreCase);
                bool isChecked = enabledSet.Contains(id) || isDefault;

                _allModelItems.Add(new AiSelectableModelItem
                {
                    ModelId = id,
                    IsChecked = isChecked,
                    IsDefault = isDefault,
                    OnCheckChanged = UpdateModelStats
                });
            }

            PnlModelManagement.IsVisible = _allModelItems.Count > 0;
            ApplyModelFilter();
        }

        private void ApplyModelFilter()
        {
            string kw = (TxtModelSearch.Text ?? "").Trim();
            if (string.IsNullOrEmpty(kw))
            {
                _currentFilteredItems = _allModelItems.ToList();
            }
            else
            {
                _currentFilteredItems = _allModelItems
                    .Where(m => m.ModelId.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
            }

            LstModels.ItemsSource = _currentFilteredItems;
            TxtEmptyModels.IsVisible = _currentFilteredItems.Count == 0 && _allModelItems.Count > 0;
            UpdateModelStats();
        }

        private void UpdateModelStats()
        {
            int checkedCount = _allModelItems.Count(m => m.IsChecked);
            int totalCount = _allModelItems.Count;
            int filteredCount = _currentFilteredItems.Count;

            string kw = (TxtModelSearch.Text ?? "").Trim();
            if (string.IsNullOrEmpty(kw))
            {
                TxtModelStats.Text = $"已启用 {checkedCount} / {totalCount} 个模型（仅勾选的模型会展示在识图对话下拉框中）";
            }
            else
            {
                int filteredChecked = _currentFilteredItems.Count(m => m.IsChecked);
                TxtModelStats.Text = $"当前筛选出 {filteredCount} 个模型（其中已启用 {filteredChecked} 个）| 全量已启用 {checkedCount} / {totalCount} 个";
            }
        }

        private void OnModelSearchTextChanged(object? sender, TextChangedEventArgs e)
        {
            ApplyModelFilter();
        }

        private void OnFilterVisionClick(object? sender, RoutedEventArgs e)
        {
            string currentDefault = (TxtSpecifiedModel.Text ?? "").Trim();
            int matched = 0;

            foreach (var item in _allModelItems)
            {
                bool isVision = VisionKeywords.Any(k => item.ModelId.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
                                || string.Equals(item.ModelId, currentDefault, StringComparison.OrdinalIgnoreCase);
                item.IsChecked = isVision;
                if (isVision) matched++;
            }

            UpdateModelStats();
            TxtStatus.Text = $"视觉推荐：已为您筛选勾选 {matched} 个识别到的视觉/多模态模型。";
        }

        private void OnSelectAllClick(object? sender, RoutedEventArgs e)
        {
            foreach (var item in _currentFilteredItems)
            {
                item.IsChecked = true;
            }
            UpdateModelStats();
        }

        private void OnClearAllClick(object? sender, RoutedEventArgs e)
        {
            string currentDefault = (TxtSpecifiedModel.Text ?? "").Trim();
            foreach (var item in _currentFilteredItems)
            {
                if (!string.Equals(item.ModelId, currentDefault, StringComparison.OrdinalIgnoreCase))
                {
                    item.IsChecked = false;
                }
            }
            UpdateModelStats();
        }

        private void OnSetAsDefaultClick(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string modelId && !string.IsNullOrWhiteSpace(modelId))
            {
                _isSyncingModel = true;
                TxtSpecifiedModel.Text = modelId;
                _workingConfig.Model = modelId;
                _isSyncingModel = false;

                foreach (var item in _allModelItems)
                {
                    bool isTarget = string.Equals(item.ModelId, modelId, StringComparison.OrdinalIgnoreCase);
                    item.IsDefault = isTarget;
                    if (isTarget && !item.IsChecked)
                    {
                        item.IsChecked = true;
                    }
                }
                UpdateModelStats();
            }
        }

        private void OnTogglePasswordVisibility(object? sender, RoutedEventArgs e)
        {
            bool isHidden = TxtApiKey.PasswordChar == '*';
            TxtApiKey.PasswordChar = isHidden ? '\0' : '*';
            if (EyeIcon != null) EyeIcon.IsVisible = !isHidden;
            if (EyeOffIcon != null) EyeOffIcon.IsVisible = isHidden;
        }

        private async void OnFetchModelsClick(object? sender, RoutedEventArgs e)
        {
            await RunTestOrFetchAsync(fetchOnly: true);
        }

        private async void OnTestConnectionClick(object? sender, RoutedEventArgs e)
        {
            await RunTestOrFetchAsync(fetchOnly: false);
        }

        private async Task RunTestOrFetchAsync(bool fetchOnly)
        {
            var proto = RadAnthropic.IsChecked == true ? AiProtocolType.Anthropic : AiProtocolType.OpenAI;
            string bUrl = (TxtBaseUrl.Text ?? "").Trim();
            string aKey = (TxtApiKey.Text ?? "").Trim();

            BtnTestConnection.IsEnabled = false;
            BtnFetchModels.IsEnabled = false;
            TxtStatus.Text = fetchOnly ? "正在拉取上游模型列表…" : "正在连接上游服务，验证地址与密钥…";

            try
            {
                var res = await AiVisionService.Instance.TestConnectionAndFetchModelsAsync(proto, bUrl, aKey);
                if (res.IsSuccess)
                {
                    if (res.Models != null && res.Models.Count > 0)
                    {
                        var modelIds = res.Models
                            .Select(m => m.Id)
                            .Where(id => !string.IsNullOrWhiteSpace(id))
                            .Distinct()
                            .ToList();

                        // 记录已有勾选项
                        var existingChecked = _allModelItems.Where(m => m.IsChecked).Select(m => m.ModelId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        string curDefault = (TxtSpecifiedModel.Text ?? "").Trim();
                        if (!string.IsNullOrWhiteSpace(curDefault))
                        {
                            existingChecked.Add(curDefault);
                        }

                        bool isFirstTime = existingChecked.Count <= 1;

                        _allModelItems.Clear();
                        int visionCount = 0;

                        foreach (var id in modelIds)
                        {
                            bool isDefault = string.Equals(id, curDefault, StringComparison.OrdinalIgnoreCase);
                            bool isVision = VisionKeywords.Any(k => id.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
                            bool isChecked;

                            if (!isFirstTime)
                            {
                                isChecked = existingChecked.Contains(id) || isDefault;
                            }
                            else
                            {
                                // 首次拉取：若数量不多（<=8）全选，否则智能预勾选视觉模型及当前默认模型
                                isChecked = modelIds.Count <= 8 || isDefault || isVision;
                            }

                            if (isChecked) visionCount++;

                            _allModelItems.Add(new AiSelectableModelItem
                            {
                                ModelId = id,
                                IsChecked = isChecked,
                                IsDefault = isDefault,
                                OnCheckChanged = UpdateModelStats
                            });
                        }

                        // 若未指定默认模型，自动挑选首个已勾选模型作为默认
                        if (string.IsNullOrWhiteSpace(curDefault) && _allModelItems.Count > 0)
                        {
                            var target = _allModelItems.FirstOrDefault(m => m.IsChecked) ?? _allModelItems[0];
                            target.IsChecked = true;
                            target.IsDefault = true;
                            _isSyncingModel = true;
                            TxtSpecifiedModel.Text = target.ModelId;
                            _workingConfig.Model = target.ModelId;
                            _isSyncingModel = false;
                        }

                        PnlModelManagement.IsVisible = true;
                        ApplyModelFilter();

                        if (fetchOnly)
                        {
                            TxtStatus.Text = modelIds.Count > 8 && isFirstTime
                                ? $"成功：{res.Message}（共拉取 {modelIds.Count} 个模型，已智能为您预勾选 {visionCount} 个视觉模型，可按需勾选或过滤）"
                                : $"成功：{res.Message}（共拉取 {modelIds.Count} 个模型，已载入勾选列表）";
                        }
                        else
                        {
                            TxtStatus.Text = $"成功：连接成功（耗时 {res.LatencyMs}ms），地址与密钥可用，共 {modelIds.Count} 个模型";
                        }
                    }
                    else
                    {
                        TxtStatus.Text = fetchOnly
                            ? $"成功：{res.Message}，但上游未返回任何模型"
                            : $"成功：连接成功（耗时 {res.LatencyMs}ms），地址与密钥可用";
                    }
                }
                else
                {
                    TxtStatus.Text = $"失败：{res.Message}";
                }
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"失败：发生异常: {ex.Message}";
            }
            finally
            {
                BtnTestConnection.IsEnabled = true;
                BtnFetchModels.IsEnabled = true;
            }
        }

        private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 1)
            {
                try { BeginMoveDrag(e); } catch { }
            }
        }

        private void OnWindowKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                OnCancelClick(sender, e);
            }
            else if (e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Control) != 0)
            {
                e.Handled = true;
                OnSaveClick(sender, e);
            }
        }

        private void OnCancelClick(object? sender, RoutedEventArgs e)
        {
            IsConfirmed = false;
            Close();
        }

        private void OnSaveClick(object? sender, RoutedEventArgs e)
        {
            string name = (TxtName.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "API 服务商";
            }

            string specified = (TxtSpecifiedModel.Text ?? "").Trim();
            var enabledModels = _allModelItems.Where(m => m.IsChecked).Select(m => m.ModelId).Distinct().ToList();

            // 确保指定的默认模型必然包含在已启用模型中
            if (!string.IsNullOrWhiteSpace(specified) && !enabledModels.Any(m => string.Equals(m, specified, StringComparison.OrdinalIgnoreCase)))
            {
                enabledModels.Insert(0, specified);
            }

            _workingConfig.Name = name;
            _workingConfig.IsEnabled = RadEnable.IsChecked == true;
            _workingConfig.Protocol = RadAnthropic.IsChecked == true ? "Anthropic" : "OpenAI";
            _workingConfig.BaseUrl = (TxtBaseUrl.Text ?? "").Trim();
            _workingConfig.ApiKey = (TxtApiKey.Text ?? "").Trim();
            _workingConfig.Model = specified;
            _workingConfig.CustomModels = enabledModels;
            _workingConfig.CachedModels = _allModelItems.Select(m => m.ModelId).Distinct().ToList();
            if (_workingConfig.CachedModels.Count == 0 && enabledModels.Count > 0)
            {
                _workingConfig.CachedModels = new List<string>(enabledModels);
            }

            ResultConfig = _workingConfig;
            IsConfirmed = true;
            Close();
        }
    }
}

