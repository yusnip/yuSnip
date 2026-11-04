using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Ai;

namespace ScreenCaptureTool.Windows
{
    /// <summary>
        /// AI 识图预设提示词编辑窗口：设置技能命令名与提示词内容。
        /// 「设为默认」在设置页列表项上操作，本窗口不再提供。
        /// 交互模式与 AiProviderEditWindow 保持一致（draft 克隆 + 保存确认）。
        /// </summary>
    public partial class AiPromptPresetEditWindow : Window
    {
        private readonly bool _isNew;
        private readonly AiPromptPreset _working;

        public AiPromptPreset ResultPreset { get; private set; }
        public bool IsConfirmed { get; private set; }

        public AiPromptPresetEditWindow(AiPromptPreset? presetToEdit = null)
        {
            InitializeComponent();

            _isNew = presetToEdit == null;
            _working = presetToEdit?.Clone() ?? new AiPromptPreset
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "新提示词",
                Content = "",
                IsDefault = false
            };
            ResultPreset = _working;

            TxtWindowTitle.Text = _isNew ? "添加提示词" : $"编辑提示词 - {_working.Name}";
            TxtName.Text = _working.Name;
            TxtContent.Text = _working.Content;

            AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        }

        private void OnWindowKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        }

        private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                try { BeginMoveDrag(e); } catch { }
            }
        }

        private void OnSaveClick(object? sender, RoutedEventArgs e)
        {
            string name = (TxtName.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                TxtName.Focus();
                return;
            }

            _working.Name = name;
            _working.Content = (TxtContent.Text ?? "").Trim();

            ResultPreset = _working;
            IsConfirmed = true;
            Close();
        }

        private void OnCancelClick(object? sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
