using System;
using System.Collections.Generic;

namespace ScreenCaptureTool.Core.Ai
{
    /// <summary>
    /// 大模型协议类型
    /// </summary>
    public enum AiProtocolType
    {
        OpenAI,
        Anthropic,
        Claude = Anthropic
    }

    /// <summary>
    /// 模型信息
    /// </summary>
    public sealed class AiModelItem
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;

        public override string ToString() => string.IsNullOrEmpty(DisplayName) ? Id : DisplayName;
    }

    /// <summary>
    /// AI 连接与模型测试结果
    /// </summary>
    public sealed class AiConnectionTestResult
    {
        public bool IsSuccess { get; set; }
        public long LatencyMs { get; set; }
        public string Message { get; set; } = string.Empty;
        public List<AiModelItem> Models { get; set; } = new List<AiModelItem>();
    }

    /// <summary>
    /// AI 识图请求参数
    /// </summary>
    public sealed class AiVisionRequest
    {
        public AiProtocolType Protocol { get; set; } = AiProtocolType.OpenAI;
        public string BaseUrl { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public string ImageBase64 { get; set; } = string.Empty;
        public string ImageMimeType { get; set; } = "image/png";
        public int TimeoutSeconds { get; set; } = 60;
    }

    /// <summary>
    /// AI 识图预设提示词（支持多条独立管理，每条带技能命令名，可在 AI 输入框通过 @ 唤起）
    /// </summary>
    public sealed class AiPromptPreset
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        /// <summary>技能命令名（AI 输入框 @ 后输入该名称筛选并选中）。</summary>
        public string Name { get; set; } = "新提示词";
        /// <summary>提示词内容。</summary>
        public string Content { get; set; } = string.Empty;
        /// <summary>是否为「直接识图」使用的默认提示词（全局仅一条为 true）。</summary>
        public bool IsDefault { get; set; }

        public AiPromptPreset Clone()
        {
            return new AiPromptPreset
            {
                Id = this.Id,
                Name = this.Name ?? string.Empty,
                Content = this.Content ?? string.Empty,
                IsDefault = this.IsDefault
            };
        }
    }

    /// <summary>
    /// AI 服务商 API 配置项（支持多配置独立启停）
    /// </summary>
    public sealed class AiApiConfig
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "默认 API";
        public bool IsEnabled { get; set; } = true;
        public string Protocol { get; set; } = "OpenAI";
        public string BaseUrl { get; set; } = "https://api.openai.com/v1";
        public string ApiKey { get; set; } = string.Empty;
        public string DefaultModel { get; set; } = string.Empty;
        /// <summary>用户勾选启用、在识图与提问窗口下拉框中展示的模型列表。</summary>
        public List<string> CustomModels { get; set; } = new List<string>();
        /// <summary>从上游 API 拉取到的完整候选模型池缓存，供编辑窗口筛选与勾选。</summary>
        public List<string> CachedModels { get; set; } = new List<string>();
        public int TimeoutSeconds { get; set; } = 60;

        public string Model
        {
            get => DefaultModel;
            set => DefaultModel = value;
        }

        public List<string> AvailableModels
        {
            get => CustomModels;
            set => CustomModels = value;
        }

        public static AiApiConfig CreateDefaultOpenAi()
        {
            return new AiApiConfig
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "OpenAI",
                IsEnabled = true,
                Protocol = "OpenAI",
                BaseUrl = "https://api.openai.com/v1",
                ApiKey = string.Empty,
                DefaultModel = string.Empty,
                CustomModels = new List<string>(),
                CachedModels = new List<string>()
            };
        }

        public static AiApiConfig CreateDefaultAnthropic()
        {
            return new AiApiConfig
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "Anthropic",
                IsEnabled = true,
                Protocol = "Anthropic",
                BaseUrl = "https://api.anthropic.com/v1",
                ApiKey = string.Empty,
                DefaultModel = string.Empty,
                CustomModels = new List<string>(),
                CachedModels = new List<string>()
            };
        }

        public AiProtocolType GetProtocolType()
        {
            return (string.Equals(Protocol, "Anthropic", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Protocol, "Claude", StringComparison.OrdinalIgnoreCase))
                ? AiProtocolType.Anthropic
                : AiProtocolType.OpenAI;
        }

        public string GetEffectiveApiKey()
        {
            string stored = ApiKey ?? string.Empty;
            if (stored.StartsWith("dp:", StringComparison.Ordinal))
                return string.Empty;
            return stored;
        }

        public AiApiConfig Clone()
        {
            return new AiApiConfig
            {
                Id = this.Id,
                Name = this.Name ?? string.Empty,
                IsEnabled = this.IsEnabled,
                Protocol = this.Protocol ?? "OpenAI",
                BaseUrl = this.BaseUrl ?? string.Empty,
                ApiKey = this.ApiKey ?? string.Empty,
                DefaultModel = this.DefaultModel ?? string.Empty,
                CustomModels = new List<string>(this.CustomModels ?? new List<string>()),
                CachedModels = new List<string>(this.CachedModels ?? new List<string>()),
                TimeoutSeconds = this.TimeoutSeconds > 0 ? this.TimeoutSeconds : 60
            };
        }
    }

    /// <summary>
    /// 模型下拉选择条目类型
    /// </summary>
    public enum ModelDropDownItemType
    {
        Header,
        Model,
        Separator
    }

    /// <summary>
    /// AI 识图结果窗口模型下拉选择条目 ViewModel
    /// </summary>
    public sealed class AiModelDropDownItem
    {
        public ModelDropDownItemType Type { get; set; } = ModelDropDownItemType.Model;
        public string ConfigId { get; set; } = string.Empty;
        public string ConfigName { get; set; } = string.Empty;
        public string ModelId { get; set; } = string.Empty;
        public string DisplayText { get; set; } = string.Empty;

        public bool IsHeader => Type == ModelDropDownItemType.Header;
        public bool IsModel => Type == ModelDropDownItemType.Model;
        public bool IsSeparator => Type == ModelDropDownItemType.Separator;
        public bool IsSelectable => Type == ModelDropDownItemType.Model && !string.IsNullOrEmpty(ModelId);

        public static AiModelDropDownItem CreateHeader(string configName, string configId) =>
            new AiModelDropDownItem
            {
                Type = ModelDropDownItemType.Header,
                ConfigId = configId,
                ConfigName = configName,
                DisplayText = configName
            };

        public static AiModelDropDownItem CreateSeparator() =>
            new AiModelDropDownItem
            {
                Type = ModelDropDownItemType.Separator
            };

        public static AiModelDropDownItem CreateModel(string modelId, string configId, string configName, string? display = null) =>
            new AiModelDropDownItem
            {
                Type = ModelDropDownItemType.Model,
                ConfigId = configId,
                ConfigName = configName,
                ModelId = modelId,
                DisplayText = display ?? modelId
            };

        public override string ToString() => IsHeader ? $"-- {DisplayText} --" : (IsSeparator ? "---" : DisplayText);
    }

    /// <summary>
    /// 流式思考过程分离过滤器（零反射）：
    /// 自动识别并剥离正文中内联的 &lt;think&gt;...&lt;/think&gt; 标签，
    /// 将思考部分分流到 onThinkingChunk，将正文回答分流到 onContentChunk。
    /// 跨 chunk 状态保持，支持标签被拆分在多个 chunk 中的边界情况。
    /// </summary>
    public sealed class StreamingThinkingFilter
    {
        private readonly Action<string> _onContentChunk;
        private readonly Action<string> _onThinkingChunk;
        private readonly System.Text.StringBuilder _buffer = new System.Text.StringBuilder();
        private bool _isInsideThink;

        private const string StartTag = "<think>";
        private const string EndTag = "</think>";

        public StreamingThinkingFilter(Action<string> onContentChunk, Action<string> onThinkingChunk)
        {
            _onContentChunk = onContentChunk ?? (_ => { });
            _onThinkingChunk = onThinkingChunk ?? (_ => { });
        }

        public void Feed(string chunk)
        {
            if (string.IsNullOrEmpty(chunk)) return;

            _buffer.Append(chunk);
            ProcessBuffer();
        }

        public void Flush()
        {
            if (_buffer.Length > 0)
            {
                string remaining = _buffer.ToString();
                _buffer.Clear();
                if (_isInsideThink)
                {
                    _onThinkingChunk(remaining);
                }
                else
                {
                    _onContentChunk(remaining);
                }
            }
        }

        private void ProcessBuffer()
        {
            while (_buffer.Length > 0)
            {
                string current = _buffer.ToString();
                if (!_isInsideThink)
                {
                    int startIdx = current.IndexOf(StartTag, StringComparison.OrdinalIgnoreCase);
                    if (startIdx >= 0)
                    {
                        if (startIdx > 0)
                        {
                            string prefix = current.Substring(0, startIdx);
                            _onContentChunk(prefix);
                        }
                        _isInsideThink = true;
                        _buffer.Remove(0, startIdx + StartTag.Length);
                    }
                    else
                    {
                        int possiblePrefixLen = GetPossibleTagPrefixLength(current, StartTag);
                        if (possiblePrefixLen > 0)
                        {
                            int safeLen = current.Length - possiblePrefixLen;
                            if (safeLen > 0)
                            {
                                string safeText = current.Substring(0, safeLen);
                                _onContentChunk(safeText);
                                _buffer.Remove(0, safeLen);
                            }
                            break;
                        }
                        else
                        {
                            _onContentChunk(current);
                            _buffer.Clear();
                        }
                    }
                }
                else
                {
                    int endIdx = current.IndexOf(EndTag, StringComparison.OrdinalIgnoreCase);
                    if (endIdx >= 0)
                    {
                        if (endIdx > 0)
                        {
                            string thinkText = current.Substring(0, endIdx);
                            _onThinkingChunk(thinkText);
                        }
                        _isInsideThink = false;
                        _buffer.Remove(0, endIdx + EndTag.Length);
                    }
                    else
                    {
                        int possiblePrefixLen = GetPossibleTagPrefixLength(current, EndTag);
                        if (possiblePrefixLen > 0)
                        {
                            int safeLen = current.Length - possiblePrefixLen;
                            if (safeLen > 0)
                            {
                                string safeText = current.Substring(0, safeLen);
                                _onThinkingChunk(safeText);
                                _buffer.Remove(0, safeLen);
                            }
                            break;
                        }
                        else
                        {
                            _onThinkingChunk(current);
                            _buffer.Clear();
                        }
                    }
                }
            }
        }

        private static int GetPossibleTagPrefixLength(string text, string tag)
        {
            int maxCheck = Math.Min(text.Length, tag.Length - 1);
            for (int len = maxCheck; len > 0; len--)
            {
                string textSuffix = text.Substring(text.Length - len);
                string tagPrefix = tag.Substring(0, len);
                if (string.Equals(textSuffix, tagPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return len;
                }
            }
            return 0;
        }
    }

}
