using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ScreenCaptureTool.Core.Capture
{
    /// <summary>水印落点档（2026-09-20 的「水印标注」第一轮口径，档名与顺序照用户原话八条）。</summary>
    public enum WatermarkPosition
    {
        /// <summary>平铺：满屏 45° 斜排、砖式错半格，疏密由「大小」与「间距」决定。</summary>
        Tile,
        BottomRight,
        BottomLeft,
        TopRight,
        TopLeft,
        TopCenter,
        BottomCenter,
        Center,
    }

    /// <summary>
    /// 一次盖章所需的全部参数。值来自 <c>AppSettings</c> 的那五个记忆键，
    /// 开关（<c>WatermarkEnabled</c>）不落盘、不进这一份：关掉时压根不会走到绘制。
    /// </summary>
    public sealed class WatermarkConfig
    {
        /// <summary>水印文字。空串＝不盖章也不提示（第一轮拍板第 3 条）。</summary>
        public string Text = string.Empty;

        public WatermarkPosition Position = WatermarkPosition.Tile;

        /// <summary>不透明度 5-100（%）。</summary>
        public int OpacityPercent = DefaultOpacity;

        /// <summary>字高 12-80，单位是<b>成品图像像素</b>（不是屏幕 DIP）。</summary>
        public int SizePx = DefaultSize;

        /// <summary>平铺间距 100-1000（%），只作用在平铺档。</summary>
        public int SpacingPercent = DefaultSpacing;

        /// <summary>字色 ARGB，沿用全工具共用的那枚标注色，不为水印另开一份颜色设置。</summary>
        public int ColorArgb = unchecked((int)0xFFFFFFFF);

        /// <summary>内容非空才盖（空串时参数照样能调，只是成品上没有字）。</summary>
        public bool HasContent => !string.IsNullOrEmpty(Text);

        #region 量程与默认值（参数条、设置页、两侧绘制共用这一份）

        public const int MinOpacity = 5;
        public const int MaxOpacity = 100;
        public const int DefaultOpacity = 30;

        public const int MinSize = 12;
        public const int MaxSize = 80;
        public const int DefaultSize = 24;

        public const int MinSpacing = 100;
        public const int MaxSpacing = 1000;
        public const int SpacingStep = 10;
        public const int DefaultSpacing = 300;

        /// <summary>字体族与字重：水印没有字体入口（参数只有不透明度/大小/间距三枚），故钉死。</summary>
        public const string FontFamilyName = "Microsoft YaHei UI";
        public const bool FontBold = true;

        #endregion
    }

    /// <summary>
    /// 水印的<b>唯一口径</b>：落点、平铺排布、成品绘制都只在这里算一次。
    ///
    /// 与 <see cref="SelectionEffects"/> 同族但更早一步：水印盖在<b>底图之上、所有标注之下</b>，
    /// 且发生在圆角/描边/阴影那圈边距展开<b>之前</b>，所以本类的一切坐标都以「内容（选区）矩形」为基准，
    /// 内容尺寸就是位图尺寸，不涉及任何边距。
    ///
    /// 三条渲染不变量：
    /// - <b>字高＝大小</b>：文字盒高度直接取 <c>SizePx</c>，不随字体行高浮动，预览与成品同一口径。
    /// - <b>内缩＝一倍字高</b>：角落/边居中档水印盒到选区边缘的距离＝<c>SizePx</c>（设计稿 §四），
    ///   不给第四个参数。
    /// - <b>平铺角度固定 −45°</b>（左下→右上），不给参数。⚠ 两侧符号已核对过：GDI+ 的
    ///   <c>RotateTransform(-45f)</c> 与预览侧 <see cref="RotationMatrix"/>（y 轴向下的行向量矩阵）
    ///   同向，改任何一侧的符号都会出现"预览与成品水印方向相反"这种只能肉眼发现的错。
    ///
    /// ⚠ 预览（Avalonia）与成品（GDI+）的<b>文字测量</b>必然各测一次（两套排版引擎，"© 内部资料"这类
    ///   短串实测差 1-2 像素），本类因此把「量到的宽度」做成入参而不是内部常量——落点算式仍然只有一份，
    ///   漂移只可能来自字形宽度本身，与序号/箭头那条"双渲染器共用度量口径"同源，勿试图用补偿常数硬凑。
    /// </summary>
    public static class WatermarkEffect
    {
        /// <summary>平铺斜排角度（度，负值＝屏幕上逆时针＝左下到右上）。预览与成品共用。</summary>
        public const float TileAngleDegrees = -45f;

        /// <summary>砖式错位的行间隔（每隔一行右移半格）。</summary>
        const double BrickOffsetFactor = 0.5;

        /// <summary>平铺块数上限：8K 选区 + 100% 间距会算出上万块，夹住以防遮罩每帧重算时卡死。</summary>
        const int MaxTiles = 4000;

        /// <summary>档名（菜单条目与设置显示共用）。</summary>
        public static string DisplayName(WatermarkPosition position) => position switch
        {
            WatermarkPosition.Tile => "平铺",
            WatermarkPosition.BottomRight => "右下角",
            WatermarkPosition.BottomLeft => "左下角",
            WatermarkPosition.TopRight => "右上角",
            WatermarkPosition.TopLeft => "左上角",
            WatermarkPosition.TopCenter => "顶部居中",
            WatermarkPosition.BottomCenter => "底部居中",
            _ => "居中",
        };

        /// <summary>按枚举成员名解析落点档（设置项落盘的就是这个名字）。</summary>
        public static bool TryParsePosition(string? name, out WatermarkPosition position)
        {
            foreach (WatermarkPosition v in Enum.GetValues<WatermarkPosition>())
            {
                if (string.Equals(name, v.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    position = v;
                    return true;
                }
            }

            position = WatermarkPosition.Tile;
            return false;
        }

        /// <summary>内缩距离：恒为「大小」的一倍字高，不是可设档。</summary>
        public static float InsetOf(int sizePx) => sizePx;

        /// <summary>
        /// 非平铺档的文字盒左上角（内容像素坐标）。
        /// 横向：左档内缩、右档贴右边减字宽、居中档取中线；纵向同理。
        /// ⚠ 遮罩预览（Avalonia）与成品绘制（GDI+）两路都用这一个方法定落点，别在任何一侧另写一份。
        /// </summary>
        public static PointF AnchorOriginOf(WatermarkPosition position,
            float contentWidth, float contentHeight, float textWidth, float textHeight)
        {
            float inset = InsetOf((int)Math.Round(textHeight));

            float x = position switch
            {
                WatermarkPosition.TopLeft or WatermarkPosition.BottomLeft => inset,
                WatermarkPosition.TopRight or WatermarkPosition.BottomRight => contentWidth - inset - textWidth,
                _ => (contentWidth - textWidth) / 2f,
            };

            float y = position switch
            {
                WatermarkPosition.TopLeft or WatermarkPosition.TopRight or WatermarkPosition.TopCenter => inset,
                WatermarkPosition.BottomLeft or WatermarkPosition.BottomRight or WatermarkPosition.BottomCenter
                    => contentHeight - inset - textHeight,
                _ => (contentHeight - textHeight) / 2f,
            };

            return new PointF(x, y);
        }

        /// <summary>
        /// 平铺档的每一块<b>中心点</b>（内容像素坐标）。
        /// 排布在<b>旋转后的坐标系</b>里铺网格（这样"行"才是斜的那一行），再整体旋回内容坐标系：
        /// 水平间距＝字宽×间距%，垂直间距＝字高×间距%，奇数行右移半格。
        /// 旋转系里要覆盖的区域＝内容矩形转 ±45° 后的外接正方形，半边长 (W+H)/(2√2)。
        /// </summary>
        public static List<PointF> TileCenters(float contentWidth, float contentHeight,
            float textWidth, float textHeight, int spacingPercent)
        {
            var centers = new List<PointF>();
            if (contentWidth <= 0 || contentHeight <= 0 || textWidth <= 0 || textHeight <= 0) return centers;

            double gap = Math.Clamp(spacingPercent, WatermarkConfig.MinSpacing, WatermarkConfig.MaxSpacing) / 100.0;
            double cellW = Math.Max(1.0, textWidth * gap);
            double cellH = Math.Max(1.0, textHeight * gap);
            double half = (contentWidth + contentHeight) / (2.0 * Math.Sqrt(2.0));

            int rows = (int)Math.Ceiling(2.0 * half / cellH);
            // 多铺一列：奇数行整体右移半格，最右边那一格否则补不上
            int cols = (int)Math.Ceiling(2.0 * half / cellW) + 1;
            if ((long)rows * cols > MaxTiles)
            {
                // 夹总块数：按行数等比削列，保持铺满观感（极端小间距＋超大选区才会走到这里）
                cols = Math.Max(1, MaxTiles / Math.Max(1, rows));
            }

            double angle = TileAngleDegrees * Math.PI / 180.0;
            double cos = Math.Cos(angle), sin = Math.Sin(angle);
            double originX = contentWidth / 2.0, originY = contentHeight / 2.0;

            for (int row = 0; row < rows; row++)
            {
                double ry = -half + (row + 0.5) * cellH;
                double brick = row % 2 == 1 ? cellW * BrickOffsetFactor : 0.0;
                for (int col = 0; col < cols; col++)
                {
                    double rx = -half + (col + 0.5) * cellW + brick;
                    centers.Add(new PointF(
                        (float)(originX + rx * cos - ry * sin),
                        (float)(originY + rx * sin + ry * cos)));
                }
            }

            return centers;
        }

        /// <summary>
        /// 绕点 (cx,cy) 旋转 <paramref name="angleDegrees"/> 的仿射矩阵（<b>行向量</b>约定，y 轴向下，
        /// 正值＝屏幕上顺时针），供 Avalonia 预览侧 <c>DrawingContext.PushTransform</c> 使用。
        /// 手工展开而不走 <c>Matrix.CreateRotation</c>：那个重载的角度单位/绕点签名各版本不一致，
        /// 而这里的公式与 GDI+ 的"先平移到中心、再旋转"严格同解（本类头部那条不变量）。
        /// </summary>
        public static void RotationMatrix(double angleDegrees, double cx, double cy,
            out double m11, out double m12, out double m21, out double m22, out double offX, out double offY)
        {
            double a = angleDegrees * Math.PI / 180.0;
            double cos = Math.Cos(a), sin = Math.Sin(a);
            m11 = cos; m12 = sin;
            m21 = -sin; m22 = cos;
            offX = cx - cx * cos + cy * sin;
            offY = cy - cx * sin - cy * cos;
        }

        /// <summary>
        /// 把水印盖到成品位图上（内容尺寸＝位图尺寸，调用点必须排在标注烘焙<b>之前</b>、边距展开<b>之后无涉</b>）。
        /// 文字测量与绘制同用 <c>GenericTypographic</c>，两者才落在同一个盒子里。
        /// <paramref name="config"/> 传 null＝这一次不盖（未开启或内容为空），调用方不必自己在外面判空。
        /// </summary>
        public static void Apply(Bitmap bitmap, WatermarkConfig? config)
        {
            if (bitmap == null || config == null || !config.HasContent) return;

            using Graphics g = Graphics.FromImage(bitmap);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            Draw(g, bitmap.Width, bitmap.Height, config);
        }

        /// <summary>成品绘制本体（贴图宿主与非位图宿主可复用）。</summary>
        public static void Draw(Graphics g, float contentWidth, float contentHeight, WatermarkConfig config)
        {
            if (g == null || config == null || !config.HasContent) return;

            Color color = Color.FromArgb(config.ColorArgb);
            int alpha = (int)Math.Round(color.A * Math.Clamp(config.OpacityPercent,
                WatermarkConfig.MinOpacity, WatermarkConfig.MaxOpacity) / 100.0);
            using var brush = new SolidBrush(Color.FromArgb(Math.Clamp(alpha, 0, 255), color.R, color.G, color.B));

            var style = WatermarkConfig.FontBold ? FontStyle.Bold : FontStyle.Regular;
            using var font = new Font(WatermarkConfig.FontFamilyName, Math.Max(1f, config.SizePx), style, GraphicsUnit.Pixel);
            using var format = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Near,
                Trimming = StringTrimming.None,
                FormatFlags = StringFormatFlags.NoWrap,
            };

            float textWidth = g.MeasureString(config.Text, font, PointF.Empty, format).Width;
            float textHeight = config.SizePx;

            if (config.Position != WatermarkPosition.Tile)
            {
                PointF origin = AnchorOriginOf(config.Position, contentWidth, contentHeight, textWidth, textHeight);
                g.DrawString(config.Text, font, brush, origin, format);
                return;
            }

            foreach (PointF center in TileCenters(contentWidth, contentHeight, textWidth, textHeight, config.SpacingPercent))
            {
                GraphicsState state = g.Save();
                try
                {
                    g.TranslateTransform(center.X, center.Y);
                    g.RotateTransform(TileAngleDegrees);
                    g.DrawString(config.Text, font, brush, -textWidth / 2f, -textHeight / 2f, format);
                }
                finally
                {
                    g.Restore(state);
                }
            }
        }
    }
}
