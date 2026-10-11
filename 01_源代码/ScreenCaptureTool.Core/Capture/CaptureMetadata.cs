using System;
using System.Globalization;
using System.Text;

namespace ScreenCaptureTool.Core.Capture
{
    /// <summary>
    /// 一次截图的元数据快照：截图区域（屏幕物理像素）、截屏 DPI 缩放、捕获时间。
    /// 用于写入 PNG tEXt chunk，将来「从文件打开为贴图」时读取即可恢复原尺寸与原位置。
    /// </summary>
    /// <remarks>
    /// 文本序列化格式（ASCII 安全，紧凑易检视）：
    /// <code>v=1;x=100;y=200;w=800;h=600;dpi=1.5;ts=2026-06-14T08:30:00Z</code>
    /// 字段语义见 <see cref="TextVersion"/>；版本号预留以便将来加字段不破坏旧解析。
    /// </remarks>
    public sealed record CaptureMetadata(int X, int Y, int Width, int Height, double DpiScale, DateTime CapturedAtUtc)
    {
        /// <summary>当前文本格式的版本号；将来加字段时递增，读取端按版本分支解析。</summary>
        public const int TextVersion = 1;

        /// <summary>写入 PNG tEXt chunk 的关键字（keyword）。</summary>
        public const string ChunkKeyword = "ScreenCapture.Info";

        /// <summary>把元数据序列化为 tEXt chunk 的 value 文本（不含 keyword 与分隔符）。</summary>
        public string ToTextValue()
        {
            // InvariantCulture 保证小数点和日期格式跨区域一致；Z 后缀明确标注 UTC。
            return string.Create(CultureInfo.InvariantCulture,
                $"v={TextVersion};x={X};y={Y};w={Width};h={Height};dpi={DpiScale:0.######};ts={CapturedAtUtc:yyyy-MM-ddTHH:mm:ssZ}");
        }

        /// <summary>
        /// 构造 tEXt chunk 的 data 段字节数据：<keyword>\0<value>。
        /// keyword 与 value 全部为 ASCII，符合 PNG tEXt 规范（Latin-1 + 无 Null）。
        /// </summary>
        public byte[] EncodeChunkData()
        {
            string text = ToTextValue();
            // keyword(ASCII) + 0x00 分隔符 + value(ASCII)
            return Encoding.ASCII.GetBytes(ChunkKeyword + "\0" + text);
        }
    }
}
