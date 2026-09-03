#nullable disable

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using ScreenCaptureTool.Core.Capture;
using ScreenCaptureTool.Core.Io;

namespace ScreenCaptureTool.Core
{
    /// <summary>
    /// 图像导出服务。阶段 2 搬迁自旧项目。
    ///
    /// 现状：仍使用 <see cref="System.Drawing.Bitmap"/>，依赖 <c>System.Drawing.Common 8.0.x</c>。
    /// 长期方向（阶段 8 优化）：可能迁到 SkiaSharp 编码，去掉 System.Drawing 依赖。
    /// 本阶段坚持纯搬迁，不动业务逻辑。
    /// </summary>
    public static class ImageExportService
    {
        public static void Save(Bitmap bitmap, string fileName)
        {
            Save(bitmap, fileName, metadata: null);
        }

        /// <summary>
        /// 保存位图到文件；当目标为 PNG 且提供了 <paramref name="metadata"/> 时，
        /// 在保存像素后追加一个 tEXt chunk 写入截图元数据（位置/尺寸/DPI/时间戳）。
        /// 非 PNG（如贴图另存为 JPG/BMP）或 metadata 为 null 时，仅保存像素。
        /// </summary>
        /// <param name="metadata">截图元数据；仅 PNG 格式下写入，可为 null。</param>
        public static void Save(Bitmap bitmap, string fileName, CaptureMetadata metadata)
        {
            using (PerformanceTimer.Measure("ImageExportService.Save"))
            {
                if (bitmap == null) throw new ArgumentNullException(nameof(bitmap));
                if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentException("File name is empty.", nameof(fileName));

                string directory = Path.GetDirectoryName(fileName);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);

                ImageFormat format = GetImageFormat(fileName);
                bitmap.Save(fileName, format);

                // 截图统一为 PNG，元数据随 PNG tEXt chunk 写入；贴图另存为 JPG/BMP 时无此 chunk。
                if (metadata != null && format.Equals(ImageFormat.Png))
                {
                    PngChunkWriter.WriteTextChunk(fileName, CaptureMetadata.ChunkKeyword, metadata.ToTextValue());
                }
            }
        }

        private static ImageFormat GetImageFormat(string fileName)
        {
            string ext = Path.GetExtension(fileName);
            if (string.Equals(ext, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ext, ".jpeg", StringComparison.OrdinalIgnoreCase))
            {
                return ImageFormat.Jpeg;
            }
            if (string.Equals(ext, ".bmp", StringComparison.OrdinalIgnoreCase))
            {
                return ImageFormat.Bmp;
            }
            return ImageFormat.Png;
        }
    }
}
