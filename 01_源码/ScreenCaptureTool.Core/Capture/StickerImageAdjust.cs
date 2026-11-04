using System;
using System.Drawing;
using System.Drawing.Imaging;

namespace ScreenCaptureTool.Core.Capture
{
    /// <summary>
    /// 贴图「图像处理」的状态（2026-09-29）：旋转（0/90/180/270，顺时针）、水平/垂直翻转、灰度、反色、亮度。
    ///
    /// <para><b>非破坏式</b>：贴图保留一份原图，显示图恒由"原图 + 本状态"重新导出（<see cref="StickerImageAdjust.Apply"/>）。
    /// 于是"重置旋转与翻转""重置所有图像处理"都是精确回到原图，连续旋转几十次也不会累计插值损失。</para>
    /// </summary>
    public readonly record struct StickerImageAdjustState(
        int RotationDeg = 0,
        bool FlipH = false,
        bool FlipV = false,
        bool Grayscale = false,
        bool Invert = false,
        int BrightnessPct = 0)
    {
        public static StickerImageAdjustState Identity => default;

        public bool IsIdentity => RotationDeg == 0 && !FlipH && !FlipV && !Grayscale && !Invert && BrightnessPct == 0;

        /// <summary>几何部分（旋转/翻转）是否为单位变换。</summary>
        public bool IsGeometryIdentity => RotationDeg == 0 && !FlipH && !FlipV;

        /// <summary>旋转后宽高是否互换。</summary>
        public bool SwapsAxes => RotationDeg == 90 || RotationDeg == 270;

        public StickerImageAdjustState RotateClockwise() => this with { RotationDeg = (RotationDeg + 90) % 360 };
        public StickerImageAdjustState RotateCounterClockwise() => this with { RotationDeg = (RotationDeg + 270) % 360 };

        /// <summary>"水平翻转"按<b>屏幕上看到的</b>方向翻：旋转 90/270 之后，屏幕水平轴对应原图的垂直轴。</summary>
        public StickerImageAdjustState ToggleFlipScreenH() => SwapsAxes ? this with { FlipV = !FlipV } : this with { FlipH = !FlipH };

        /// <summary>"垂直翻转"同理按屏幕方向翻。</summary>
        public StickerImageAdjustState ToggleFlipScreenV() => SwapsAxes ? this with { FlipH = !FlipH } : this with { FlipV = !FlipV };

        public StickerImageAdjustState ResetGeometry() => this with { RotationDeg = 0, FlipH = false, FlipV = false };
    }

    /// <summary>贴图图像处理的纯像素实现（GDI+ 位图进、新位图出；输入不修改）。</summary>
    public static class StickerImageAdjust
    {
        /// <summary>亮度可选档（百分比，0＝原样）。菜单与实现共用这一份，保证"当前档"判定一致。</summary>
        public static readonly int[] BrightnessPresets = { -50, -30, -15, 0, 15, 30, 50 };

        /// <summary>按状态从原图导出显示图。单位状态返回原图的独立拷贝（调用方可放心持有/释放）。</summary>
        public static Bitmap Apply(Bitmap original, StickerImageAdjustState st)
        {
            if (original == null) throw new ArgumentNullException(nameof(original));

            // 1) 几何：先翻转（原图坐标系）再旋转，与状态机里"翻转按屏幕方向记账"的换算互逆
            var bmp = new Bitmap(original.Width, original.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.DrawImage(original, new Rectangle(0, 0, original.Width, original.Height),
                    0, 0, original.Width, original.Height, GraphicsUnit.Pixel);
            }
            RotateFlipType rft = ToRotateFlip(st);
            if (rft != RotateFlipType.RotateNoneFlipNone) bmp.RotateFlip(rft);

            // 2) 颜色：逐像素，alpha 保留（圆角/透明区不受影响）
            if (st.Grayscale || st.Invert || st.BrightnessPct != 0)
                ApplyColor(bmp, st.Grayscale, st.Invert, st.BrightnessPct);
            return bmp;
        }

        /// <summary>GDI+ 的 RotateFlip 是"先旋转后翻转"。本状态语义是"原图先翻转、再顺时针旋转 R"，
        /// 等价于 旋转 R 后再按旋转后的轴翻转：水平翻转在旋转 90/270 后落到另一轴。</summary>
        private static RotateFlipType ToRotateFlip(StickerImageAdjustState st)
        {
            bool fx = st.FlipH, fy = st.FlipV;
            if (st.SwapsAxes) (fx, fy) = (fy, fx);
            int r = ((st.RotationDeg % 360) + 360) % 360;
            // RotateFlipType：RotateXFlipX 顺时针旋转 X 度后水平翻转；XY 翻转 ≡ 旋转 180
            if (fx && fy) { r = (r + 180) % 360; fx = fy = false; }
            return (r, fx, fy) switch
            {
                (0, false, false) => RotateFlipType.RotateNoneFlipNone,
                (90, false, false) => RotateFlipType.Rotate90FlipNone,
                (180, false, false) => RotateFlipType.Rotate180FlipNone,
                (270, false, false) => RotateFlipType.Rotate270FlipNone,
                (0, true, false) => RotateFlipType.RotateNoneFlipX,
                (90, true, false) => RotateFlipType.Rotate90FlipX,
                (180, true, false) => RotateFlipType.Rotate180FlipX,
                (270, true, false) => RotateFlipType.Rotate270FlipX,
                (0, false, true) => RotateFlipType.RotateNoneFlipY,
                (90, false, true) => RotateFlipType.Rotate90FlipY,
                (180, false, true) => RotateFlipType.Rotate180FlipY,
                (270, false, true) => RotateFlipType.Rotate270FlipY,
                _ => RotateFlipType.RotateNoneFlipNone,
            };
        }

        private static unsafe void ApplyColor(Bitmap bmp, bool gray, bool invert, int brightnessPct)
        {
            // 亮度按"向白/向黑线性混合"做：+50% ＝ 向白走一半，−50% ＝ 向黑走一半；饱和度与色相不变，
            // 不会像乘法那样把亮部直接顶成死白。
            double k = Math.Clamp(brightnessPct / 100.0, -1.0, 1.0);
            var lut = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                double v = k >= 0 ? i + (255 - i) * k : i * (1 + k);
                lut[i] = (byte)Math.Clamp((int)Math.Round(v), 0, 255);
            }

            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < bmp.Height; y++)
                {
                    byte* row = (byte*)data.Scan0 + (long)y * data.Stride;
                    for (int x = 0; x < bmp.Width; x++, row += 4)
                    {
                        int b = row[0], g = row[1], r = row[2];
                        if (gray)
                        {
                            // Rec.709 亮度
                            int l = (r * 2126 + g * 7152 + b * 722 + 5000) / 10000;
                            r = g = b = l;
                        }
                        if (invert) { r = 255 - r; g = 255 - g; b = 255 - b; }
                        row[0] = lut[b]; row[1] = lut[g]; row[2] = lut[r];
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }
    }
}
