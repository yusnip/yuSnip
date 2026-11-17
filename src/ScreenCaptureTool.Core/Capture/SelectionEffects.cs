using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ScreenCaptureTool.Core.Capture
{
    /// <summary>
    /// 截图选区的圆角、描边与投影（纯位图后处理，不含 UI）。
    ///
    /// 这里是三项效果的<b>唯一口径</b>：最终成品位图在这里画，遮罩里的实时预览
    /// （<c>SelectionLayer</c>）也引用同一组常量换算 DIP，两边看到的必须是同一个东西。
    ///
    /// 两条不变量：
    /// - 内容<b>逐像素不缩放</b>。描边与阴影都是往画布外面扩出来的边距（四周等量），不是把图缩小后
    ///   塞进原尺寸里；成品相对选区到底长出一圈多少，只由 <see cref="MarginOf"/> 一处说了算。
    /// - 最外层轮廓之外都是<b>真透明</b>（alpha=0），不填任何底色。最外层轮廓 = 描边外沿（未描边时
    ///   即内容轮廓），阴影再往这条轮廓之外扩散。丢 alpha 的出口（JPG/BMP、只认 CF_DIB 的老应用）
    ///   由各自的导出/剪贴板环节决定怎么兜底，这里不预填。
    /// - <b>卡片外沿只有一条线</b>：内容轮廓沿各自外法线外扩描边宽得到的真等距线（见 <see cref="BuildStrokeRing"/>
    ///   与 <see cref="CardGeometry"/>）。成品阴影的距离场、遮罩预览、贴图窗体阴影三处都按"到内容轮廓的距离
    ///   再减描边宽"算同一条线。⚠ 禁止再用"矩形与半径同步外扩描边宽"的同族缩放当基准——它在 45° 方向多出
    ///   0.2312×描边宽，圆角与描边同时开时会在四个角各切出一片透明楔形（阴影与卡片断开）。
    /// </summary>
    public static class SelectionEffects
    {
        /// <summary>圆角滑杆参数上限（UI 滑杆量程 0-50）。</summary>
        public const int MaxRadiusParam = 50;

        /// <summary>成品圆角物理像素上限（对应参数 50 时达到 200 物理像素）。</summary>
        public const int MaxRadiusPx = 200;

        /// <summary>参数档位到物理像素的倍率（50 档 = 200px）。</summary>
        public const int ParamToPxScale = 4;

        /// <summary>把滑杆参数档位（0-50）换算成实际生效的物理像素半径（0-200px）。</summary>
        public static int ParamToRadiusPx(int param) => Math.Clamp(param, 0, MaxRadiusParam) * ParamToPxScale;

        /// <summary>把物理像素半径反向折算为滑杆参数档位（0-50）。</summary>
        public static int RadiusPxToParam(int radiusPx) => Math.Clamp((int)Math.Round((float)radiusPx / ParamToPxScale), 0, MaxRadiusParam);

        /// <summary>把滑杆参数档位钳到合法区间 0-50。</summary>
        public static int NormalizeParam(int param) => Math.Clamp(param, 0, MaxRadiusParam);

        /// <summary>描边滑杆量程（UI 滑杆 0-20）。0 = 不描边。</summary>
        public const int MaxStrokeParam = 20;

        /// <summary>
        /// 描边档位 → 物理像素宽度（<b>1:1</b>，与圆角的 ×4 不同：描边本来就按像素直观）。
        /// 该宽度全部长在内容边界<b>以外</b>（不遮挡截图内容），故成品尺寸随它一起变大。
        /// </summary>
        public static int ParamToStrokePx(int param) => NormalizeStrokeParam(param);

        /// <summary>把描边档位钳到合法区间 0-20（旧配置/手改 INI 的兜底）。</summary>
        public static int NormalizeStrokeParam(int param) => Math.Clamp(param, 0, MaxStrokeParam);

        /// <summary>
        /// 描边内沿相对内容边界往<b>里</b>多盖的物理像素数。0 = 严格不内缩。
        /// 沿革：成品端已改为实心底衬方案（<see cref="DrawStrokeUnderlay"/>），交界处由内容覆盖单次栅格化，
        /// 严禁把成品端改回内缩或外环；本常量仅供预览端（<c>SelectionLayer</c>）保持环构造并传递 0 偏移。
        /// </summary>
        public const int StrokeInnerBleedPx = 0;

        /// <summary>
        /// 描边<b>默认</b>颜色（ARGB，不透明品牌紫 <c>#4219F2</c>，与遮罩上元素检测框／参考线所用同色）。
        /// 实际取色由调用方传入（设置项 <c>AppSettings.CaptureStrokeColor</c>，入口是参数条上的色块＋取色器）；
        /// 成品（GDI+）与遮罩预览（Avalonia）拿到的是<b>同一个 int</b>，两边只做类型拆分、不各存一份色值。
        /// </summary>
        public const int DefaultStrokeArgb = unchecked((int)0xFF4219F2);

        /// <summary>
        /// 阴影扩散半径（物理像素）。<b>四周等量、无偏移</b>——带纵向偏移时底边比另外三边厚一截，
        /// 用户要的是一张四周对称的柔和投影。
        /// </summary>
        public const int ShadowBlurPx = 36;

        /// <summary>
        /// 画布边距（物理像素，四周等量）。路径四周外扩 0.5px 后，形状边界比边距近 0.5px，
        /// 加上像素中心 0.5px 偏移，实际最大距离 = 边距 − 1；故边距取扩散半径 + 1 才能完整包住阴影。
        /// 勾上阴影后成品尺寸 = 选区尺寸 + 2 × 该值。
        /// </summary>
        public const int ShadowMarginPx = ShadowBlurPx + 1;

        /// <summary>阴影峰值不透明度（0-255，贴边处最深，约 17%）。用户要求在第一版 88 的基础上再降一半。</summary>
        public const int ShadowPeakAlpha = 44;

        /// <summary>高斯衰减系数：越大收得越快、光晕越紧。</summary>
        private const float GaussianK = 4.2f;

        /// <summary>
        /// 超椭圆圆角指数（n=5：连续曲率过渡，角点曲率平滑归零，视觉上比圆弧更"柔"）。
        /// 用户从「截图圆角边缘-柔化方案对比.html」三轮挑选后定稿：连续曲率 + 硬边。
        /// </summary>
        private const float CurvatureExponent = 5f;

        /// <summary>
        /// 阴影往形状<b>内侧</b>多写的像素数。填充路径比内容外扩了 0.5px，纹理在 <c>WrapMode.Clamp</c>
        /// 下不外扩，于是圆弧处的反走样像素只被内容盖住一部分；内侧不留投影打底，四角就会沿弧线
        /// 露出一圈比阴影更亮的细缝（浅色桌面上尤其明显）。直边处的覆盖是整像素判定，不受影响。
        /// </summary>
        private const int ShadowInnerBleedPx = 1;

        /// <summary>
        /// 阴影浓度曲线。<paramref name="distancePx"/> = 到<b>卡片外沿</b>（内容轮廓外扩描边宽的真等距线；未描边时即内容轮廓）的距离（物理像素），返回 0~1 的相对浓度。
        ///
        /// 成品逐像素写 alpha、遮罩预览逐圈描边<b>都走这一条曲线</b>，两边看到的必须是同一个阴影；
        /// 任何"把数值抄一份到绘制端"的写法都会在缩放屏下出现预览与成品不一致。
        /// </summary>
        public static float ShadowFalloff(float distancePx)
        {
            if (distancePx <= 0f) return 1f;
            if (distancePx >= ShadowBlurPx) return 0f;
            float t = distancePx / ShadowBlurPx;
            return MathF.Exp(-GaussianK * t * t);
        }

        /// <summary>把任意输入半径钳到合法区间（旧配置/手改 INI 的兜底）。</summary>
        public static int NormalizeRadius(int radiusPx) => Math.Clamp(radiusPx, 0, MaxRadiusPx);

        /// <summary>
        /// 实际生效半径：不超过短边的一半，否则圆角会自己咬到一起。
        /// </summary>
        public static float EffectiveRadius(int radiusPx, int contentWidth, int contentHeight)
        {
            float half = Math.Min(contentWidth, contentHeight) / 2f;
            return Math.Min(NormalizeRadius(radiusPx), half);
        }

        /// <summary>
        /// 描边外扩占用的画布边距（物理像素，四周等量）。色带整体长在内容轮廓以外，
        /// 再加 1px 是为了让它自己的抗锯齿外沿也落在画布内——边距取整，外沿正好压在画布边上会被裁掉半圈。
        /// </summary>
        public static int StrokeMarginOf(int strokePx) => NormalizeStrokeParam(strokePx) > 0 ? strokePx + 1 : 0;

        /// <summary>
        /// 成品相对选区要加的画布边距（四周等量）：描边外扩与阴影扩散各自贡献，直接相加。
        /// 阴影的扩散基准是<b>描边外沿</b>那条轮廓（见 <see cref="Create"/>），故两者同时开启时
        /// 边距 = 描边边距 + 阴影边距，成品尺寸 = 选区尺寸 + 2 × 该值。
        /// </summary>
        public static int MarginOf(bool shadow, int strokePx) => (shadow ? ShadowMarginPx : 0) + StrokeMarginOf(strokePx);

        /// <summary>
        /// 贴图窗体阴影画光晕所需的<b>卡片几何</b>（全部位图物理像素）：内容轮廓圆角 + 描边宽度。
        /// 光晕最内一圈必须压在<b>卡片外沿</b>上，而卡片外沿 = 内容轮廓沿各自外法线外扩
        /// <see cref="StrokeWidthPx"/> 的<b>真等距线</b>（与描边色带的外沿同一条线，见 <see cref="BuildStrokeRing"/>）。
        ///
        /// ⚠ 一个"外沿半径"表达不了这条线：同族缩放（矩形与半径同步外扩描边宽）在 45° 方向比真等距线
        /// 多出 0.2312×描边宽（超椭圆 45° 的径长是 1.2312r，直边段才是 1:1），圆角与描边同时开时
        /// 这点差就是一条肉眼可见的缝（2026-09-06 用户实测：贴图角部缺阴影、保存的图角部断开）。
        /// </summary>
        public readonly struct CardGeometry
        {
            /// <summary>内容轮廓圆角半径（含 <see cref="Create"/> 的抗锯齿 pad）；0＝直角。</summary>
            public float ContentRadiusPx { get; init; }

            /// <summary>描边宽度，即卡片外沿相对内容轮廓的外扩量；0＝无描边，外沿就是内容轮廓。</summary>
            public float StrokeWidthPx { get; init; }

            /// <summary>
            /// 位图<b>画布边缘</b>到<b>内容轮廓</b>的透明边距，与 <see cref="StrokeMarginOf"/> 同一算式。
            /// 贴图窗口的阴影层铺在整张位图的显示矩形上，故必须先按这个量往里收，才收在内容轮廓上。
            /// 注意只含描边边距、<b>不含</b>阴影带宽：外圈带烘焙阴影的贴图，窗体光晕的基准是
            /// 棋盘格背景板（＝整张位图）的外沿，不往卡片里收（2026-09-07 用户拍板的并存口径）。
            /// </summary>
            public float ContentInsetPx => StrokeWidthPx > 0f ? StrokeWidthPx + 1f : 0f;
        }

        /// <summary>
        /// 按三项效果的<b>唯一口径</b>算出 <see cref="CardGeometry"/>。贴图窗口要让窗体阴影贴合卡片边缘
        /// 时<b>只能问这里要数</b>：内容半径已被 <see cref="EffectiveRadius"/> 夹到短边一半、抗锯齿 pad
        /// 又随描边开关切换，调用方各自抄算式必然分叉（画布边距同理见 <see cref="MarginOf"/>）。
        /// </summary>
        public static CardGeometry CardGeometryOf(int radiusPx, int strokePx, int contentWidth, int contentHeight)
        {
            int stroke = ParamToStrokePx(strokePx);
            float r = EffectiveRadius(radiusPx, contentWidth, contentHeight);
            // pad 规则与 Create 一致：描边底衬已吸收交界抗锯齿，只有不描边时才外扩 0.5px 防白边。
            float contentRadius = r > 0.5f ? r + (stroke > 0 ? 0f : 0.5f) : r;
            return new CardGeometry { ContentRadiusPx = contentRadius, StrokeWidthPx = stroke };
        }

        /// <summary>
        /// 一张<b>外来</b>位图外圈是否真有一圈半透明阴影带（即成品烘焙过投影）。判据取<b>四边中点</b>
        /// 而不是四角：圆角只削掉四个角楔形，四边中段必然不透明；只有烘过扩散阴影，四边中段才会是半透明的。
        /// 异形／圆形素材四边也空 → 判为"带阴影"同样正确（贴图窗会在其背后垫棋盘格承托，同样适用）。
        ///
        /// 只服务"无从得知参数"的图（剪贴板 F3、截图历史）；自家截图流程一律走 <see cref="CardGeometryOf"/>。
        /// </summary>
        public static bool HasBakedShadowBand(Bitmap bitmap)
        {
            if (bitmap.Width < 4 || bitmap.Height < 4) return false;
            try
            {
                int w = bitmap.Width - 1, h = bitmap.Height - 1;
                return bitmap.GetPixel(w / 2, 0).A < 32
                    || bitmap.GetPixel(w / 2, h).A < 32
                    || bitmap.GetPixel(0, h / 2).A < 32
                    || bitmap.GetPixel(w, h / 2).A < 32;
            }
            catch
            {
                return false; // 已释放的位图（"Parameter is not valid"）等，按"没有阴影带"处理
            }
        }

        /// <summary>
        /// 从像素侧回读一张<b>外来</b>位图的卡片外沿圆角半径（物理像素），口径同 <see cref="CardGeometry"/>
        /// 里那条外沿（含描边外扩，不含阴影扩散）。落盘只剩 PNG、没记效果参数的场合（截图历史）只能这样反推。
        ///
        /// 依据是"四周边距等量"这条不变量：自上而下找到第一行出现不透明像素的行 y₀（卡片最顶那一行），
        /// 该行第一个不透明像素正压在卡片顶边与圆角轮廓的切点上，切点水平位移恰等于外沿半径，
        /// 而画布边距（阴影 37 ＋ 描边宽 ＋1）在 <c>x₀ − y₀</c> 里被抵掉。故带不带阴影边距、边距多大都
        /// 不影响结果，直角图自然得 0。圆角与阴影都是抗锯齿过渡，判"不透明"取 alpha ≥ 250，
        /// 切点那一格只覆盖半格、量出来会多 1~2px —— 这点量级落在光晕里看不出来。
        /// 扫描限制在左上角一小窗（外沿半径与边距的理论上限），全不透明的图第一格即命中；
        /// 认不出形状（整块透明、异常位图）返回 0＝按直角处理。
        ///
        /// ⚠ 这条路上<b>拆不出</b>「内容半径 + 描边宽」（两个未知数只有一个可测量），所以只能当
        /// <see cref="CardGeometry.StrokeWidthPx"/> = 0 用，即把回读到的总半径整个当成内容圆角。
        /// 自家截图流程一律走 <see cref="CardGeometryOf"/>，不要退化到这里。
        /// </summary>
        public static float ProbeOuterRadiusPx(Bitmap bitmap)
        {
            if (bitmap.Width < 4 || bitmap.Height < 4) return 0f;
            int limit = Math.Min(Math.Min(bitmap.Width, bitmap.Height) / 2, MaxRadiusPx + ShadowMarginPx + MaxStrokeParam + 2);
            try
            {
                for (int y = 0; y <= limit; y++)
                {
                    for (int x = 0; x <= limit; x++)
                    {
                        if (bitmap.GetPixel(x, y).A >= OpaqueAlphaThreshold) return Math.Max(0, x - y);
                    }
                }
                return 0f;
            }
            catch
            {
                return 0f;
            }
        }

        /// <summary>
        /// 判"完全不透明"的 alpha 阈值。轮廓反推（<see cref="ProbeOuterRadiusPx"/>）、外壳探针
        /// （<see cref="ProbeShellMarginPx"/>）与外壳回填（<see cref="WrapContentIntoShell"/>）三处
        /// 共用这一个数，对同一条抗锯齿过渡带的归属判定才不会分叉。
        /// </summary>
        public const int OpaqueAlphaThreshold = 250;

        /// <summary>
        /// 外壳探针的深度上限（物理像素）。自家成品的壳宽是确定的（<see cref="ShadowMarginPx"/>
        /// ＋描边边距），多出来的余量只为量得动**别的应用**复制来的阴影带——那一圈有多宽不受我们约束。
        /// </summary>
        public const int ShellProbeCapPx = ShadowMarginPx + MaxStrokeParam + 60;

        /// <summary>
        /// 从像素侧量一张位图外圈那层<b>半透明壳</b>有多宽（自家成品的烘焙阴影带、外来素材自带的
        /// 阴影边、抠图留下的透明边）：四条边各自往里扫到第一个含完全不透明像素的行/列，取四者的
        /// <b>最小值</b>。四边等量是自家成品的不变量；外来素材不守这个规矩时宁可少裁——多出来那几边
        /// 仍由 <see cref="WrapContentIntoShell"/> 的 alpha 判据兜住，不会漏进外来图。
        ///
        /// 任何一条边在 <see cref="ShellProbeCapPx"/> 内都找不到不透明像素（异形素材、比探针还宽的
        /// 阴影带）就整张返回 0＝不裁，宁可把整幅交出去，回填时照样把透明区还原回来。
        /// ⚠ 只量得到<b>半透明</b>的那圈：不透明的描边环会被当成内容起点，量不出它有多厚。
        /// </summary>
        public static int ProbeShellMarginPx(Bitmap bitmap)
        {
            if (bitmap == null || bitmap.Width < 4 || bitmap.Height < 4) return 0;
            int w = bitmap.Width, h = bitmap.Height;
            int cap = Math.Min(ShellProbeCapPx, Math.Min(w, h) / 3);
            if (cap <= 0) return 0;

            try
            {
                int top = ProbeShellBand(bitmap, new Rectangle(0, 0, w, cap), true, false);
                int bottom = ProbeShellBand(bitmap, new Rectangle(0, h - cap, w, cap), true, true);
                int left = ProbeShellBand(bitmap, new Rectangle(0, 0, cap, h), false, false);
                int right = ProbeShellBand(bitmap, new Rectangle(w - cap, 0, cap, h), false, true);
                if (top < 0 || bottom < 0 || left < 0 || right < 0) return 0;
                return Math.Min(Math.Min(top, bottom), Math.Min(left, right));
            }
            catch
            {
                return 0; // 已释放/格式异常的位图按"没有壳"处理，与 HasBakedShadowBand 同一兜底
            }
        }

        /// <summary>
        /// 扫一条边带，返回"离边界最近的一条含不透明像素的行（或列）"到边界的距离；整带都没有返回 -1。
        /// </summary>
        private static int ProbeShellBand(Bitmap target, Rectangle band, bool scanRows, bool fromEnd)
        {
            BitmapData data = target.LockBits(band, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                // ⚠ 子矩形锁的 Stride 是整幅行距、Scan0 指向带的左上角，必须逐行拷（同 WriteShadowBand）
                int stride = data.Stride, rowBytes = band.Width * 4;
                var buf = new byte[rowBytes * band.Height];
                for (int row = 0; row < band.Height; row++)
                    Marshal.Copy(IntPtr.Add(data.Scan0, row * stride), buf, row * rowBytes, rowBytes);

                int lines = scanRows ? band.Height : band.Width;
                int samples = scanRows ? band.Width : band.Height;
                for (int k = 0; k < lines; k++)
                {
                    int line = fromEnd ? lines - 1 - k : k;
                    for (int t = 0; t < samples; t++)
                    {
                        int px = scanRows ? line * band.Width + t : t * band.Width + line;
                        if (buf[px * 4 + 3] >= OpaqueAlphaThreshold) return k;
                    }
                }
                return -1;
            }
            finally
            {
                target.UnlockBits(data);
            }
        }

        /// <summary>
        /// 一张位图的<b>内容矩形</b>＝画布去掉外圈那层"壳"（阴影带＋描边环＋圆角缺口）之后剩下的区域。
        ///
        /// 用途只有一个：决定把这张图<b>交给外部处理（产商图片翻译）时裁到哪</b>。产商只认不透明图，
        /// 透明区会被它填成白/黑纯色，回传又是不带 alpha 的 JPG，于是"圆角＋阴影"的素材翻译完就成了
        /// 一张直角白底图（2026-10-01 用户报）。回来那一路由 <see cref="WrapContentIntoShell"/> 负责。
        ///
        /// 边距一律<b>只从这一处</b>要数，调用端不得重抄几何算式：先用 <see cref="ProbeShellMarginPx"/>
        /// 从像素量出半透明那圈壳有多厚，再按 <see cref="CardGeometry.StrokeWidthPx"/> 补上不透明的描边环；
        /// 无从得知参数的外来素材 StrokeWidthPx 恒为 0，只剩探针，量不到就按整幅处理（少裁无害）。
        /// </summary>
        public static Rectangle ContentRectOf(Bitmap bitmap, CardGeometry card)
        {
            if (bitmap == null) throw new ArgumentNullException(nameof(bitmap));
            int w = bitmap.Width, h = bitmap.Height;
            if (w < 4 || h < 4) return new Rectangle(0, 0, Math.Max(0, w), Math.Max(0, h));

            int probed = ProbeShellMarginPx(bitmap);
            // 探针量到壳时，描边环那一段还得按参数加回来（它是<b>不透明</b>像素，探针只会停在它外沿）；
            // 减掉 ContentInsetPx 里那 1px 是刻意的偏内：宁可让最外一圈描边像素跟着内容一起交出去
            // （产商不重画无文字区，回来还是那个色），也不要在内侧削掉一行真内容留下没翻译的发丝线。
            int inset = probed > 0
                ? probed + (int)card.StrokeWidthPx
                : (int)Math.Round(card.ContentInsetPx);
            if (inset <= 0 || inset * 2 >= Math.Min(w, h)) return new Rectangle(0, 0, w, h);
            return new Rectangle(inset, inset, w - inset * 2, h - inset * 2);
        }

        /// <summary>
        /// 把外部处理（产商图片翻译）回传的<b>内容图</b>套回原位图的外壳：
        /// <list type="bullet">
        /// <item><description>内容矩形<b>以外</b>的像素一个字节都不碰——那圈烘焙阴影带、描边环与圆角缺口
        /// 保持逐字节原样（产商把它们填成的纯色被整个丢掉，而不是"照它的样子重画一遍阴影"）；</description></item>
        /// <item><description>内容矩形<b>以内</b>：alpha ≥ <see cref="OpaqueAlphaThreshold"/> 的像素取回传图的
        /// RGB、alpha 仍取原图；比这更透明的像素（圆角弧上那一两像素的抗锯齿过渡、异形素材的边缘）整像素
        /// 保留原图，免得原地的内容色被换成产商为透明区编出来的白/黑，在弧上留一条淡边。</description></item>
        /// </list>
        /// 回传图尺寸与内容矩形不等时（门面为压产商体积上限会等比降采样）先放大回内容矩形，
        /// 于是成品画布与位图尺寸恒等于原图——贴图窗口的位置、缩放与窗体阴影几何都不必跟着变。
        /// ⚠ 只写不透明的像素，因此与 LockBits 返回的是否预乘数据无关（alpha=255 时两种表示相同）。
        /// </summary>
        /// <returns>新位图（调用方负责释放）；参数越界或内容矩形为空时返回 <b>null</b>，调用方沿用回传图。</returns>
        public static Bitmap? WrapContentIntoShell(Bitmap shell, Bitmap content, Rectangle contentRect)
        {
            if (shell == null) throw new ArgumentNullException(nameof(shell));
            if (content == null) throw new ArgumentNullException(nameof(content));

            int w = shell.Width, h = shell.Height;
            if (contentRect.Width <= 0 || contentRect.Height <= 0) return null;
            if (contentRect.X < 0 || contentRect.Y < 0 || contentRect.Right > w || contentRect.Bottom > h) return null;

            var result = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            Bitmap? scaled = null;
            try
            {
                // ① 壳整幅原样搬到新位图：此后只改内容矩形内的不透明像素，半透明区一律不过手。
                // ⚠ 必须逐字节拷，不能用 Graphics.DrawImageUnscaled：32bppArgb 在 GDI+ 里是**预乘**存储，
                // 描边环／圆角弧上那些半透明像素过一遍绘制再读回会 ±1（2026-10-01 像素自检实测 1180 个像素偏 1），
                // "外圈一个字节都不碰"这条承诺就兑现不了。
                var whole = new Rectangle(0, 0, w, h);
                BitmapData shellData = shell.LockBits(whole, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                BitmapData resultData = result.LockBits(whole, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
                try
                {
                    int lineBytes = w * 4;
                    var line = new byte[lineBytes];
                    for (int row = 0; row < h; row++)
                    {
                        Marshal.Copy(IntPtr.Add(shellData.Scan0, row * shellData.Stride), line, 0, lineBytes);
                        Marshal.Copy(line, 0, IntPtr.Add(resultData.Scan0, row * resultData.Stride), lineBytes);
                    }
                }
                finally
                {
                    shell.UnlockBits(shellData);
                    result.UnlockBits(resultData);
                }

                // ② 回传图归一到内容矩形的尺寸
                Bitmap body = content;
                if (content.Width != contentRect.Width || content.Height != contentRect.Height)
                {
                    scaled = new Bitmap(contentRect.Width, contentRect.Height, PixelFormat.Format32bppArgb);
                    using (var gs = Graphics.FromImage(scaled))
                    {
                        gs.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        gs.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        gs.CompositingMode = CompositingMode.SourceCopy;
                        gs.DrawImage(content, new Rectangle(0, 0, contentRect.Width, contentRect.Height));
                    }
                    body = scaled;
                }

                // ③ 只在内容矩形内、且只替换不透明像素的 RGB（alpha 恒取原图）
                var rect = new Rectangle(contentRect.X, contentRect.Y, contentRect.Width, contentRect.Height);
                BitmapData dst = result.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
                BitmapData src = body.LockBits(new Rectangle(0, 0, body.Width, body.Height),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int rowBytes = rect.Width * 4;
                    var drow = new byte[rowBytes];
                    var srow = new byte[rowBytes];
                    for (int row = 0; row < rect.Height; row++)
                    {
                        IntPtr dLine = IntPtr.Add(dst.Scan0, row * dst.Stride);
                        Marshal.Copy(dLine, drow, 0, rowBytes);
                        Marshal.Copy(IntPtr.Add(src.Scan0, row * src.Stride), srow, 0, rowBytes);

                        bool changed = false;
                        for (int col = 0; col < rect.Width; col++)
                        {
                            int off = col * 4;
                            if (drow[off + 3] < OpaqueAlphaThreshold) continue; // 壳的一部分，原样留着
                            drow[off] = srow[off];
                            drow[off + 1] = srow[off + 1];
                            drow[off + 2] = srow[off + 2];
                            changed = true;
                        }
                        if (changed) Marshal.Copy(drow, 0, dLine, rowBytes);
                    }
                }
                finally
                {
                    result.UnlockBits(dst);
                    body.UnlockBits(src);
                }

                return result;
            }
            catch
            {
                result.Dispose();
                return null;
            }
            finally
            {
                scaled?.Dispose();
            }
        }

        /// <summary>
        /// 生成"圆角 + 描边 + 可选投影"的新位图。
        /// </summary>
        /// <param name="source">成品位图（32bppArgb，内容不透明）。</param>
        /// <param name="radiusPx">圆角半径（物理像素）。</param>
        /// <param name="shadow">是否画投影。</param>
        /// <param name="strokePx">描边宽度（物理像素），整体画在内容边界<b>以外</b>，故成品四周各长出
        /// <see cref="StrokeMarginOf"/> 那么多。0 = 不描边。</param>
        /// <param name="strokeArgb">描边颜色（ARGB）。仅在 <paramref name="strokePx"/> > 0 时参与绘制。</param>
        /// <returns>新位图（调用方负责释放）；三项都不需要时返回 <b>null</b>，调用方继续用原图。</returns>
        public static Bitmap? Create(Bitmap source, int radiusPx, bool shadow, int strokePx = 0, int strokeArgb = DefaultStrokeArgb)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            int radius = NormalizeRadius(radiusPx);
            int stroke = ParamToStrokePx(strokePx);
            if (radius <= 0 && !shadow && stroke <= 0) return null;

            int contentW = source.Width;
            int contentH = source.Height;
            if (contentW <= 0 || contentH <= 0) return null;

            int margin = MarginOf(shadow, stroke);
            var result = new Bitmap(contentW + margin * 2, contentH + margin * 2, PixelFormat.Format32bppArgb);

            // GDI+ 按"像素中心 ± 0.5"算覆盖率：边界正好压在整数坐标上时，最外一圈像素只被
            // 覆盖一半，成品四周会莫名多出一圈半透明（深色背景上看就是整张图发了白）。
            // 描边开启时：交界过渡由实心描边底衬吸收（alpha 恒 1），不再需要外扩，裁切边界严格对齐选区框；
            // 描边关闭时：保留四周外扩 0.5px 技巧，防仅圆角/仅阴影时最外一圈半透明发白。
            float pad = stroke > 0 ? 0f : 0.5f;
            var content = new RectangleF(margin - pad, margin - pad, contentW + pad * 2, contentH + pad * 2);
            float r = EffectiveRadius(radius, contentW, contentH);
            float pathRadius = r > 0.5f ? r + pad : r;

            // 阴影从**卡片外沿**往外投：整张卡片一起投影。若仍以内容轮廓为基准，内侧那几十像素
            // 会被不透明的描边盖掉，露出来的半截浓度曲线看着就是一条硬边带。
            // 这条外沿是内容轮廓沿各自外法线外扩描边宽的**真等距线**（描边色带画的就是它），实现上
            // 等价于"到内容轮廓的距离减描边宽"。⚠ 不要改回"矩形与半径同步外扩描边宽"的同族缩放：
            // 那在 45° 方向比真等距线多出 0.2312×描边宽，角部会切出一片纯透明楔形（阴影与卡片断开）、
            // 楔形外侧又突然从峰值浓度起跳，实测 20px 描边在角部留 3px 缝（2026-09-06 用户回报）。
            float grow = stroke;

            // 新建的位图缓冲区是未初始化的，先整幅清成真透明（此后只往里画，不再 Clear）
            using (var clear = Graphics.FromImage(result)) clear.Clear(Color.Transparent);

            if (shadow)
            {
                // 投影要逐像素写 alpha，必须趁还没有 Graphics 的时候做（两者不能同时持有位图）
                // 基准 = 内容轮廓（content / pathRadius）再往外挪 grow：贴图窗口的窗体阴影按
                // CardGeometryOf 算的是同样这两个数，两端必须是同一条线。
                WriteShadow(result, content, pathRadius, grow);
            }

            Graphics g = Graphics.FromImage(result);
            try
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.CompositingMode = CompositingMode.SourceOver;

                // 描边底衬：必须在内容之前画！
                // 实心填满外轮廓，内容随后画在它之上。交界处只有内容单次 AA 混合（内容色→描边色），
                // alpha 恒为 1，彻底解决"两次独立栅格化再叠合"导致的半透明谷与偏色。
                if (stroke > 0)
                {
                    DrawStrokeUnderlay(g, content, pathRadius, stroke, strokeArgb);
                }

                if (pathRadius > 0.5f)
                {
                    // 用 TextureBrush 填圆角路径而不是 SetClip：GDI+ 的裁剪区不做反走样，
                    // 直接裁出来的四角是锯齿；FillPath 走的是画刷覆盖率，边缘平滑。
                    using var path = new GraphicsPath();
                    AddRoundedRect(path, content, pathRadius);
                    using var brush = new TextureBrush(source, WrapMode.Clamp);
                    brush.TranslateTransform(margin, margin);
                    g.FillPath(brush, path);
                }
                else
                {
                    g.DrawImageUnscaled(source, margin, margin);
                }
            }
            finally
            {
                g.Dispose();
            }

            return result;
        }

        /// <summary>
        /// 距离→阴影 alpha 查找表（1/4 像素一档，长度 <c>ShadowBlurPx×4+1</c>）。
        /// 成品逐像素（<see cref="WriteShadow"/>）与遮罩预览的距离场位图<b>共用同一张表</b>，
        /// 两端对同一段距离曲线的量化分档才不会各算各的。
        /// </summary>
        public static byte[] BuildShadowAlphaLut()
        {
            var lut = new byte[ShadowBlurPx * 4 + 1];
            for (int i = 0; i < lut.Length; i++)
                lut[i] = (byte)Math.Round(ShadowPeakAlpha * ShadowFalloff(i / 4f));
            return lut;
        }

        /// <summary>
        /// 画投影：按「点到卡片外沿的精确距离」逐像素写 alpha。
        ///
        /// 为什么不再用画笔描圈叠：描圈是离散台阶，贴边处几圈叠加会先形成一段浓度平台、
        /// 外缘又收得突然，成品看上去就是一条硬边；改成距离场 + <see cref="ShadowFalloff"/>
        /// 连续曲线后没有台阶，而且四周天然等量（旧版带 8px 纵向偏移，底边比另外三边厚一截）。
        /// </summary>
        /// <param name="content">内容矩形（成品位图坐标系）。</param>
        /// <param name="radius">内容轮廓圆角半径。</param>
        /// <param name="grow">描边宽 = 卡片外沿相对内容轮廓的外扩量。凸集的等距线就是"到原边界的距离
        /// 减一个常量"，故距离场只需按内容轮廓算一次、末端统一减 grow，不必另造一条轮廓。</param>
        private static void WriteShadow(Bitmap target, RectangleF content, float radius, float grow)
        {
            // 核心矩形 = 四段圆弧的圆心所在矩形：点到它的"外部距离"再减半径，就是到内容轮廓的精确距离
            float coreL = content.X + radius, coreT = content.Y + radius;
            float coreR = content.Right - radius, coreB = content.Bottom - radius;

            var lut = BuildShadowAlphaLut();

            int margin = ShadowMarginPx;
            int w = target.Width, h = target.Height;
            // 左右两条带的宽度要盖到"角弧与顶/底直边的切点"（x = 画布边距 + 半径 + 描边宽）：
            // 上下两条带只管到 y = margin，切点以下那一片非零阴影只有左右带能算到。
            int sideW = Math.Min(margin + (int)Math.Ceiling(radius + grow) + ShadowInnerBleedPx, w);
            int band = margin + ShadowInnerBleedPx;

            // 只锁外圈四条带（左右两条额外含进四角那段圆弧区），不整幅锁：
            // 4K 选区整幅的临时缓冲是十几 MB，而内容区反正会被后面的 FillPath 盖满。
            // 每条带都往内容区多伸 ShadowInnerBleedPx，上下带与左右带因此在边缘那几行重叠——
            // 重叠无害：四条带算的是同一个全局距离场，同一像素写入的值完全相同。
            WriteShadowBand(target, new Rectangle(0, 0, w, band), coreL, coreT, coreR, coreB, radius, grow, lut);
            WriteShadowBand(target, new Rectangle(0, h - band, w, band), coreL, coreT, coreR, coreB, radius, grow, lut);
            WriteShadowBand(target, new Rectangle(0, margin, sideW, h - margin * 2), coreL, coreT, coreR, coreB, radius, grow, lut);
            WriteShadowBand(target, new Rectangle(w - sideW, margin, sideW, h - margin * 2), coreL, coreT, coreR, coreB, radius, grow, lut);
        }

        /// <summary>给一条带内的像素写阴影 alpha。带内不写的位置保持全 0（真透明）。</summary>
        private static void WriteShadowBand(Bitmap target, Rectangle area, float coreL, float coreT, float coreR,
            float coreB, float radius, float grow, byte[] lut)
        {
            if (area.Width <= 0 || area.Height <= 0) return;

            BitmapData data = target.LockBits(area, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                // ⚠ 子矩形锁返回的 Stride 是**整幅**行距、Scan0 指向带的左上角。
                // 按 stride*height 整块回写会把带外的整行一起覆盖成 0（实测左带被右带的拷贝抹掉），
                // 所以缓冲按带宽紧凑排列，再逐行拷回各自的行首。
                int stride = data.Stride, rowBytes = area.Width * 4;
                var buf = new byte[rowBytes * area.Height];

                // 每列的横向外部距离先算好，内层循环只剩一次比较/开方
                var ox = new float[area.Width];
                for (int c = 0; c < area.Width; c++)
                {
                    float px = area.X + c + 0.5f;
                    ox[c] = Math.Max(Math.Max(coreL - px, px - coreR), 0f);
                }

                for (int row = 0; row < area.Height; row++)
                {
                    float py = area.Y + row + 0.5f;
                    float oy = Math.Max(Math.Max(coreT - py, py - coreB), 0f);
                    int rowOff = row * rowBytes;

                    for (int col = 0; col < area.Width; col++)
                    {
                        // 直边段取较大的那个分量就够，只有落在角部象限里才需要算超椭圆距离
                        float d;
                        if (ox[col] > 0f && oy > 0f)
                        {
                            float px = area.X + col + 0.5f;
                            float qx = px < coreL ? coreL - px : px - coreR;
                            float qy = py < coreT ? coreT - py : py - coreB;
                            d = DistanceToSuperellipseArc(qx, qy, radius);
                        }
                        else
                        {
                            d = Math.Max(ox[col], oy) - radius;
                        }
                        d -= grow; // 换算成"到卡片外沿的距离"：描边把这条基准线整体往外挪了 grow
                        if (d < -ShadowInnerBleedPx) continue; // 再往里内容必然盖满，不必写

                        int index = (int)(d * 4f);
                        if (index < 0) index = 0;              // 内侧那一两像素按峰值浓度打底
                        if (index >= lut.Length) continue;     // 超出扩散半径，浓度已为 0
                        byte alpha = lut[index];
                        if (alpha == 0) continue;

                        int off = rowOff + col * 4;
                        buf[off] = 0;                          // B
                        buf[off + 1] = 0;                      // G
                        buf[off + 2] = 0;                      // R
                        buf[off + 3] = alpha;                  // A —— 纯黑阴影的预乘与非预乘表示完全相同
                    }
                }

                if (rowBytes == stride)
                {
                    Marshal.Copy(buf, 0, data.Scan0, buf.Length);
                }
                else
                {
                    for (int row = 0; row < area.Height; row++)
                        Marshal.Copy(buf, row * rowBytes, IntPtr.Add(data.Scan0, row * stride), rowBytes);
                }
            }
            finally
            {
                target.UnlockBits(data);
            }
        }

        /// <summary>
        /// 描边底衬：用描边色实心填充「内容轮廓外扩 <paramref name="strokePx"/>」的外轮廓多边形，
        /// 内容随后画在它之上——交界处只有内容边缘这一条 AA 混合（内容→描边色），
        /// alpha 恒为 1，彻底消除"两次独立栅格化再 SourceOver"在交界留下的半透明谷。
        /// ⚠ 防重走警告：严禁改回"先画内容后画描边环"——那正是连续三轮修不掉谷和偏色的根源。
        /// </summary>
        private static void DrawStrokeUnderlay(Graphics g, RectangleF content, float pathRadius, int strokePx, int strokeArgb)
        {
            BuildStrokeRing(content, pathRadius, 0f, strokePx, out PointF[] outer, out _);
            if (outer.Length < 3) return;

            using var path = new GraphicsPath();
            path.AddPolygon(outer);
            using var brush = new SolidBrush(Color.FromArgb(strokeArgb));
            g.FillPath(brush, path);
        }

        /// <summary>
        /// 构造描边色带的内外两条边界（同一顺时针遍历）：外沿 = 内容填充用的<b>那条</b>超椭圆轮廓
        /// 向外扩 <paramref name="outerExpand"/>，内沿 = 同一条轮廓向内缩 <paramref name="innerShrink"/>。
        /// 描边宽度就是 <paramref name="outerExpand"/>；<paramref name="innerShrink"/> 现恒为
        /// <see cref="StrokeInnerBleedPx"/> = 0（严格不内缩）。用色不在这里，由调用方各自取同一份设置值。
        /// ⚠ 成品端只用本方法取<b>外轮廓</b>做实心底衬（见 <see cref="DrawStrokeUnderlay"/>），
        /// 不再画内外双串的环——"先画内容后画描边环"会因两次独立栅格化在交界留出半透明谷，已禁止；
        /// 环形构造如今只服务遮罩预览端（<c>SelectionLayer</c>，那里的活体内容无法重画）。
        ///
        /// ⚠ 两个偏移量都必须由调用方按<b>自己所在的单位</b>换算好再传（成品传物理像素、遮罩预览传
        /// <c>像素 × DIP 系数</c>），这里不猜缩放，否则 150% 屏上预览会多算 1.5 倍。
        ///
        /// ⚠ 不要再回到"矩形内缩 t/2 ＋半径同步 −t/2 的中心线 ＋ 等宽画笔"：那个构造只对**圆弧**矩形成立。
        /// 超椭圆（n=5）角尖的曲率半径只有约 r/15.8，把参数 r 按比例缩小得到的弧不是原弧的等距线，
        /// 45° 方向上短掉约 0.12t —— 实测 20px 描边在圆角中段露出一条约 2px 的原图缝隙，
        /// 看上去就是"描边的圆角跟截图圆角没对上"。
        /// </summary>
        public static void BuildStrokeRing(RectangleF rect, float radius, float innerShrink, float outerExpand,
            out PointF[] outer, out PointF[] inner)
        {
            var baseOutline = BuildOutline(rect, radius);
            outer = OffsetOutlineConvex(baseOutline, -outerExpand);
            inner = OffsetOutlineConvex(baseOutline, innerShrink);
        }

        /// <summary>只需要卡片外沿的显示层复用同一真等距算法，不计算随后丢弃的内轮廓。</summary>
        public static PointF[] BuildStrokeOuterOutline(RectangleF rect, float radius, float outerExpand)
            => OffsetOutlineConvex(BuildOutline(rect, radius), -outerExpand);

        /// <summary>
        /// 把一条顺时针<b>凸</b>闭合多边形沿内法线做严格等距偏移（Minkowski 侵蚀 / 膨胀）
        /// <paramref name="d"/> 像素：<b>d &gt; 0 向内收、d &lt; 0 向外扩</b>。做法是逐条边把所在半平面
        /// 平移 d 后全部求交（Sutherland–Hodgman 依次裁剪），结果就是「离边界正好 |d| 像素」的那条线。
        /// 收缩到不存在时返回空串，调用方按"色带盖满整幅"处理。
        ///
        /// ⚠ 不要再回到"每个顶点沿相邻两边内角平分线推 d/cos(θ/2)"：那是"相邻两条内缩边的交点"，
        /// 只有当 d 不超过该处曲率半径时才成立。超椭圆（n=5）角尖的曲率半径只有约 r/15.8，远小于描边宽度，
        /// 那段扇形的内缩边彼此穿越，顶点会被推到形状中心附近——实测 r=4/t=20 时四角各留一个约 5px 的
        /// 原图口袋，半径被短边一半封顶的 40×40 上中央空腔约 10px。半平面求交会让那些"够不着"的边
        /// 自动退出结果，直边处仍是精确的 d、直角处自然收敛成尖角，无需任何特判。
        /// </summary>
        private static PointF[] OffsetOutlineConvex(PointF[] pts, float d)
        {
            var constraints = RemoveDegeneratePoints(pts);
            if (constraints.Length < 3) return Array.Empty<PointF>();

            // 外扩（d<0）时初始串必须比结果还大，否则原多边形整个落在外移后的半平面以内、求交等于没交
            var subject = d >= 0f ? constraints : BoundingBox(constraints, MathF.Abs(d));
            for (int i = 0; i < constraints.Length && subject.Length >= 3; i++)
            {
                // 约束恒定取自原始各边：内法线方向平移 d 后的半平面，全部求交
                PointF a = constraints[i];
                PointF b = constraints[(i + 1) % constraints.Length];
                PointF nrm = InwardNormal(b.X - a.X, b.Y - a.Y);
                if (nrm.X == 0f && nrm.Y == 0f) continue;
                subject = ClipHalfPlane(subject, a.X + nrm.X * d, a.Y + nrm.Y * d, nrm);
            }
            return subject.Length >= 3 ? subject : Array.Empty<PointF>();
        }

        /// <summary>裁掉 <paramref name="q"/> 点处法线 <paramref name="n"/> 反方向那一侧，保留 (p−q)·n ≥ 0 的部分。</summary>
        private static PointF[] ClipHalfPlane(PointF[] subject, float qx, float qy, PointF n)
        {
            int m = subject.Length;
            var list = new System.Collections.Generic.List<PointF>(m + 2);
            for (int i = 0; i < m; i++)
            {
                PointF cur = subject[i];
                PointF next = subject[(i + 1) % m];
                float dc = (cur.X - qx) * n.X + (cur.Y - qy) * n.Y;
                float dn = (next.X - qx) * n.X + (next.Y - qy) * n.Y;
                if (dc >= 0f) list.Add(cur);
                if ((dc >= 0f) != (dn >= 0f))
                {
                    // 两点在切割线两侧，交点参数 u ∈ (0,1)；异号故分母 dc−dn 不会为 0
                    float u = dc / (dc - dn);
                    list.Add(new PointF(cur.X + (next.X - cur.X) * u, cur.Y + (next.Y - cur.Y) * u));
                }
            }
            return RemoveDegeneratePoints(list.ToArray());
        }

        /// <summary>点集包围盒四周再放大 <paramref name="pad"/> 后的 4 个角点（顺时针，作外扩求交的初始串）。</summary>
        private static PointF[] BoundingBox(PointF[] pts, float pad)
        {
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var p in pts)
            {
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }
            minX -= pad; minY -= pad; maxX += pad; maxY += pad;
            return new[]
            {
                new PointF(minX, minY), new PointF(maxX, minY),
                new PointF(maxX, maxY), new PointF(minX, maxY),
            };
        }

        /// <summary>顺时针多边形（屏幕坐标 y 向下）某条边的单位内法线：方向 (dx,dy) → (-dy,dx)。</summary>
        private static PointF InwardNormal(float dx, float dy)
        {
            float len = MathF.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6f) return new PointF(0f, 0f);
            return new PointF(-dy / len, dx / len);
        }

        /// <summary>
        /// 抹掉连续重合点（含首尾重合）。半径被短边一半封顶时相邻两角的切点会正好重叠，
        /// 留下零长度边会让法线失去定义。
        /// </summary>
        private static PointF[] RemoveDegeneratePoints(PointF[] pts)
        {
            var list = new System.Collections.Generic.List<PointF>(pts.Length);
            foreach (var p in pts)
                if (list.Count == 0 || MathF.Abs(p.X - list[list.Count - 1].X) > 1e-4f
                    || MathF.Abs(p.Y - list[list.Count - 1].Y) > 1e-4f)
                    list.Add(p);
            if (list.Count > 1 && MathF.Abs(list[0].X - list[list.Count - 1].X) <= 1e-4f
                && MathF.Abs(list[0].Y - list[list.Count - 1].Y) <= 1e-4f)
                list.RemoveAt(list.Count - 1);
            return list.ToArray();
        }

        /// <summary>把超椭圆圆角矩形追加进 <see cref="GraphicsPath"/>。</summary>
        public static void AddRoundedRect(GraphicsPath path, RectangleF rect, float radius)
        {
            if (radius <= 0.5f)
            {
                path.AddRectangle(rect);
                return;
            }
            path.AddPolygon(BuildOutline(rect, radius));
        }

        /// <summary>
        /// 生成超椭圆圆角的一段弧线点（第一象限局部坐标：角心在 (0,0)，
        /// 弧从 (radius, 0) 到 (0, radius)）。调用方按四角做镜像/平移即可拼出完整轮廓。
        /// <paramref name="stepsPerCorner"/> 控制弧线细度，默认按半径自适应。
        /// </summary>
        public static PointF[] BuildCornerOutlinePoints(float radius, int stepsPerCorner = 0)
        {
            if (radius <= 0.5f) return Array.Empty<PointF>();
            if (stepsPerCorner <= 0)
                stepsPerCorner = Math.Max(8, (int)Math.Ceiling(radius / 2f));

            float n = CurvatureExponent;
            float twoOverN = 2f / n;
            var pts = new PointF[stepsPerCorner + 1];
            for (int i = 0; i <= stepsPerCorner; i++)
            {
                float t = (float)(Math.PI / 2 * i / stepsPerCorner);
                // ⚠ 端点处 cos/sin 会因浮点误差出现极小负值，而 pow(负, 分数) = NaN，
                // 会一路污染轮廓点与阴影距离场（(int)NaN 又恰好折成 0 → 取到峰值档）。先钳非负。
                float c = MathF.Max(0f, MathF.Cos(t)), s = MathF.Max(0f, MathF.Sin(t));
                pts[i] = new PointF(
                    radius * MathF.Pow(c, twoOverN),
                    radius * MathF.Pow(s, twoOverN));
            }
            return pts;
        }

        /// <summary>
        /// 返回超椭圆圆角矩形的完整轮廓点（闭合多边形，可直接喂给
        /// <c>GraphicsPath.AddPolygon</c> 或 <c>StreamGeometry</c>）。
        ///
        /// ⚠ 必须按顺时针依次拼「左上→右上→右下→左下」，且每段以该角**角心**为原点、按外凸方向
        /// 映射第一象限弧 <c>c[i]=(a,b)</c>（<c>c[0]=(r,0)</c> 切在一条边、<c>c[last]=(0,r)</c> 切在邻边）。
        /// 右上、左下两段要**逆序**发射，才能让相邻角的切点被直线边正确衔接——否则多边形自相交，
        /// <c>FillPath</c> 按填充规则几乎填不出内容（成品透明），遮罩挖洞与边框也会错乱。
        /// </summary>
        public static PointF[] BuildOutline(RectangleF rect, float radius)
        {
            if (radius <= 0.5f)
                return new[] {
                    new PointF(rect.X, rect.Y), new PointF(rect.Right, rect.Y),
                    new PointF(rect.Right, rect.Bottom), new PointF(rect.X, rect.Bottom) };

            var c = BuildCornerOutlinePoints(radius);
            int cn = c.Length;
            float r = radius;
            float right = rect.Right, bottom = rect.Bottom;
            var pts = new PointF[cn * 4];
            int idx = 0;

            // 左上：角心 (X+r, Y+r)，外凸 (-x,-y)，正序 左切点→顶切点
            for (int i = 0; i < cn; i++)
                pts[idx++] = new PointF(rect.X + r - c[i].X, rect.Y + r - c[i].Y);
            // 右上：角心 (Right-r, Y+r)，外凸 (+x,-y)，逆序 顶切点→右切点
            for (int i = cn - 1; i >= 0; i--)
                pts[idx++] = new PointF(right - r + c[i].X, rect.Y + r - c[i].Y);
            // 右下：角心 (Right-r, Bottom-r)，外凸 (+x,+y)，正序 右切点→底切点
            for (int i = 0; i < cn; i++)
                pts[idx++] = new PointF(right - r + c[i].X, bottom - r + c[i].Y);
            // 左下：角心 (X+r, Bottom-r)，外凸 (-x,+y)，逆序 底切点→左切点
            for (int i = cn - 1; i >= 0; i--)
                pts[idx++] = new PointF(rect.X + r - c[i].X, bottom - r + c[i].Y);
            return pts;
        }

        /// <summary>
        /// 返回完整轮廓点及每点的外法线（单位向量），供需要"沿边界等距外推"的调用方使用——
        /// 目前唯一消费方是贴图窗体阴影层 <c>StickerShadowLayer</c>（一圈圈偏移环构成高斯光晕）。
        /// 法线由超椭圆隐函数 F(u,v)=(u/r)^n+(v/r)^n−1 的梯度归一化得到：∇F ∝ (u^(n-1), v^(n-1))。
        /// 点的排布与 <see cref="BuildOutline"/> 完全同一顺时针遍历（右上、左下逆序），法线随点同号映射。
        /// </summary>
        public static void BuildOutlineWithNormals(RectangleF rect, float radius,
            out PointF[] points, out PointF[] normals)
        {
            if (radius <= 0.5f)
            {
                points = new[] {
                    new PointF(rect.X, rect.Y), new PointF(rect.Right, rect.Y),
                    new PointF(rect.Right, rect.Bottom), new PointF(rect.X, rect.Bottom) };
                normals = new[] {
                    new PointF(-0.7071f, -0.7071f), new PointF(0.7071f, -0.7071f),
                    new PointF(0.7071f, 0.7071f), new PointF(-0.7071f, 0.7071f) };
                return;
            }

            var c = BuildCornerOutlinePoints(radius);
            int cn = c.Length;
            float r = radius;
            float right = rect.Right, bottom = rect.Bottom;
            float nMinus1 = CurvatureExponent - 1f;

            // 第一象限弧点 (a,b) 的外法线 ∝ (a^(n-1), b^(n-1))，两端点自然退化为轴对齐方向
            var nl = new PointF[cn];
            for (int i = 0; i < cn; i++)
            {
                float gx = MathF.Pow(MathF.Max(c[i].X, 1e-6f), nMinus1);
                float gy = MathF.Pow(MathF.Max(c[i].Y, 1e-6f), nMinus1);
                float len = MathF.Sqrt(gx * gx + gy * gy);
                nl[i] = new PointF(gx / len, gy / len);
            }

            points = new PointF[cn * 4];
            normals = new PointF[cn * 4];
            int idx = 0;

            // 左上：外凸 (-x,-y)，正序
            for (int i = 0; i < cn; i++)
            {
                points[idx] = new PointF(rect.X + r - c[i].X, rect.Y + r - c[i].Y);
                normals[idx] = new PointF(-nl[i].X, -nl[i].Y);
                idx++;
            }
            // 右上：外凸 (+x,-y)，逆序
            for (int i = cn - 1; i >= 0; i--)
            {
                points[idx] = new PointF(right - r + c[i].X, rect.Y + r - c[i].Y);
                normals[idx] = new PointF(nl[i].X, -nl[i].Y);
                idx++;
            }
            // 右下：外凸 (+x,+y)，正序
            for (int i = 0; i < cn; i++)
            {
                points[idx] = new PointF(right - r + c[i].X, bottom - r + c[i].Y);
                normals[idx] = new PointF(nl[i].X, nl[i].Y);
                idx++;
            }
            // 左下：外凸 (-x,+y)，逆序
            for (int i = cn - 1; i >= 0; i--)
            {
                points[idx] = new PointF(rect.X + r - c[i].X, bottom - r + c[i].Y);
                normals[idx] = new PointF(-nl[i].X, nl[i].Y);
                idx++;
            }
        }

        /// <summary>
        /// 超椭圆弧在 45° 方向相对核心矩形角点的内缩量（= r·(1−2^(−1/n))，n 即 <see cref="CurvatureExponent"/>）。
        /// 圆角矩形的角部边界从"核心矩形角点"沿对角线往里退这么多——遮罩预览的四条阴影边带
        /// （<c>SelectionLayer.EnsureEffects</c>）必须按此内缩量加宽，否则角部弧线退进带内侧的
        /// 那片楔形阴影会整块漏掉（探针曾在角部测得与成品差 43/255，即此缺口）。
        /// </summary>
        public static float CornerRecedePx(float radius)
            => radius * (1f - MathF.Pow(2f, -1f / CurvatureExponent));

        /// <summary>
        /// 卡片外沿（= 内容轮廓外扩 <paramref name="grow"/> 的真等距线）在角部比直边段往<b>里</b>凹进多少。
        /// 遮罩预览的四条阴影边带必须按这个量往内侧多盖，否则四个角各漏一片楔形阴影。
        ///
        /// 两项相加：内容弧自己的 45° 内缩 <see cref="CornerRecedePx"/>；描边把整条线沿对角线往外推
        /// <paramref name="grow"/>，折算到纵向只剩 grow/√2，也就是比直边段（往外推 grow）少推
        /// grow·(1−1/√2)。成品端的四条带按 <c>边距 + 半径 + 描边宽</c>取宽，天然盖得住，不需要这个量。
        /// </summary>
        public static float ShadowBandCornerReachPx(float radius, float grow)
            => CornerRecedePx(radius) + grow * (1f - 1f / MathF.Sqrt(2f));

        /// <summary>
        /// 点到超椭圆圆角矩形边界的有符号距离（外正内负）。
        /// 直边段用常规矩形距离；角落象限内用二分法求到超椭圆弧线的最近距离。
        /// n>2 时 g(t) 单调递减，二分法保证收敛。
        /// </summary>
        public static float DistanceToRoundedRect(float px, float py, RectangleF rect, float radius)
        {
            if (radius <= 0.5f)
            {
                float dx = Math.Max(Math.Max(rect.X - px, px - rect.Right), 0f);
                float dy = Math.Max(Math.Max(rect.Y - py, py - rect.Bottom), 0f);
                return (dx > 0f && dy > 0f) ? MathF.Sqrt(dx * dx + dy * dy) : Math.Max(dx, dy);
            }

            float coreL = rect.X + radius, coreT = rect.Y + radius;
            float coreR = rect.Right - radius, coreB = rect.Bottom - radius;
            float ox = Math.Max(Math.Max(coreL - px, px - coreR), 0f);
            float oy = Math.Max(Math.Max(coreT - py, py - coreB), 0f);

            if (ox <= 0f || oy <= 0f)
                return Math.Max(ox, oy) - radius;

            // 角落象限：把查询折到第一象限局部坐标，求到弧线的有符号距离再减半径
            float qx, qy;
            if (px < coreL) { qx = coreL - px; } else { qx = px - coreR; }
            if (py < coreT) { qy = coreT - py; } else { qy = py - coreB; }
            return DistanceToSuperellipseArc(qx, qy, radius);
        }

        /// <summary>
        /// 第一象限点 (qx, qy) 到超椭圆弧 (r·cos^(2/n)t, r·sin^(2/n)t), t∈[0, π/2] 的有符号距离。
        /// 弧外为正、弧内为负。
        ///
        /// 几何性质：在第一象限，点到超椭圆弧的距离平方 D²(t) 是严格单峰函数（只有一个全局极小点）。
        /// 采用黄金分割极值搜索（Golden Section Search）：
        /// 1) 无需对隐函数/导数做符号二分（旧版导数判定误设 tCrit=0 导致角落全部算成到端点的大距离，进而丢失角落阴影）；
        /// 2) 包含两端点 (r,0) 与 (0,r) 候选，与水平/垂直直边的阴影距离严格 C⁰/C¹ 连续无缝拼接；
        /// 3) 迭代次数按<b>距离</b>精度定，不是按弧长精度：极小值附近 D² 对 t 是二阶小量，10 次迭代后
        ///    参数区间剩 0.618¹⁰·(π/2) ≈ 0.013 rad，即便 r=200 对应弧上位置偏差 ~1.6px，落到距离上
        ///    也只剩 (1.6px)²/(2ρ) ≈ 0.03px（ρ 为局部曲率半径）——遮罩预览逐档重栅格化时这里每档要
        ///    调用上千次，每次 18 个评估 × 2 个 MathF.Pow（无硬件加速），是重建成本的大头，能省则省。
        /// </summary>
        private static float DistanceToSuperellipseArc(float qx, float qy, float radius)
        {
            float r = radius;
            const float HalfPi = 1.5707963268f;
            const float InvPhi = 0.6180339887f; // (sqrt(5) - 1) / 2

            // 两端点候选：t=0 处切水平边 (r, 0)，t=π/2 处切垂直边 (0, r)
            float d0 = (qx - r) * (qx - r) + qy * qy;
            float d1 = qx * qx + (qy - r) * (qy - r);

            // 黄金分割搜索弧内极小距离
            float a = 0f, b = HalfPi;
            float c = b - InvPhi * (b - a);
            float d = a + InvPhi * (b - a);

            float fc = EvalSuperellipseD2(c, qx, qy, r);
            float fd = EvalSuperellipseD2(d, qx, qy, r);

            for (int i = 0; i < 10; i++)
            {
                if (fc < fd)
                {
                    b = d;
                    d = c;
                    fd = fc;
                    c = b - InvPhi * (b - a);
                    fc = EvalSuperellipseD2(c, qx, qy, r);
                }
                else
                {
                    a = c;
                    c = d;
                    fc = fd;
                    d = a + InvPhi * (b - a);
                    fd = EvalSuperellipseD2(d, qx, qy, r);
                }
            }

            float minD2 = MathF.Min(MathF.Min(d0, d1), MathF.Min(fc, fd));
            float dist = MathF.Sqrt(minD2);

            // 有符号：超椭圆弧内（(qx/r)^n + (qy/r)^n < 1）为负，弧外为正。
            // n=5 的整数次幂用乘法链而不是 MathF.Pow——这里每次调用都要算，Pow 无硬件加速。
            float ax = qx / r, ay = qy / r;
            float ax2 = ax * ax, ay2 = ay * ay;
            float f = ax2 * ax2 * ax + ay2 * ay2 * ay;
            float sign = f < 1f ? -1f : 1f;
            return sign * dist;
        }

        private static float EvalSuperellipseD2(float t, float qx, float qy, float r)
        {
            float cos = MathF.Max(0f, MathF.Cos(t));
            float sin = MathF.Max(0f, MathF.Sin(t));
            float twoOverN = 2f / CurvatureExponent;
            float x = r * MathF.Pow(cos, twoOverN);
            float y = r * MathF.Pow(sin, twoOverN);
            float dx = qx - x;
            float dy = qy - y;
            return dx * dx + dy * dy;
        }
    }
}
