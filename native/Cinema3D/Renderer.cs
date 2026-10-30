using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Cinema3D;

/// <summary>
/// Direct3D 11 на выходе проектора. Цепочка обмена с 3 буферами и задержкой до 3 кадров:
/// видеокарта сама отдаёт готовые кадры по тактам проектора, даже если программа на миг задержалась.
/// </summary>
public sealed class Renderer : IDisposable
{
    const string Hlsl = @"
Texture2D tY : register(t0);
Texture2D tUV : register(t1);
Texture2D tRGB : register(t2);
SamplerState sm : register(s0);
cbuffer P : register(b0) { float4 rA; float4 rB; int mode; int src; float2 pad; };
struct V { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
float3 Src(float2 uv)
{
    if (src == 1) return tRGB.Sample(sm, uv).rgb;
    float y = (tY.Sample(sm, uv).r - 0.0625) * 1.1644;
    float2 c = tUV.Sample(sm, uv).rg - 0.5;
    return float3(y + 1.7927 * c.y, y - 0.2132 * c.x - 0.5329 * c.y, y + 2.1124 * c.x); // BT.709
}
float4 PS(V i) : SV_Target
{
    float2 uv = saturate(i.uv);
    float3 a = Src(rA.xy + uv * rA.zw);
    if (mode == 1)
    {
        float3 b = Src(rB.xy + uv * rB.zw); // red-cyan anaglyph, Dubois matrices
        float3 c = float3(dot(a, float3(0.456, 0.500, 0.176)) + dot(b, float3(-0.043, -0.088, -0.002)),
                          dot(a, float3(-0.040, -0.038, -0.016)) + dot(b, float3(0.378, 0.734, -0.018)),
                          dot(a, float3(-0.015, -0.021, -0.005)) + dot(b, float3(-0.072, -0.113, 1.226)));
        return float4(saturate(c), 1);
    }
    return float4(saturate(a), 1);
}
";

    // Исходники шейдеров — только ASCII: D3DCompiler получает длину строки в символах,
    // и любая кириллица обрезает конец шейдера.

    // вершины полноэкранного треугольника
    const string HlslVs = @"
struct V { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
V VS(uint id : SV_VertexID)
{
    V o; float2 p = float2((id << 1) & 2, id & 2);
    o.uv = p; o.pos = float4(p.x * 2 - 1, 1 - p.y * 2, 0, 1); return o;
}
";

    // надписи поверх картинки
    const string HlslOv = @"
Texture2D overlayTex : register(t3);
SamplerState sm : register(s0);
struct V { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
float4 PSOv(V i) : SV_Target { return overlayTex.Sample(sm, saturate(i.uv)); }
";

    [StructLayout(LayoutKind.Sequential)]
    struct Consts { public Vector4 A, B; public int Mode, Src; public float P0, P1; }

    readonly IDXGIFactory2 factory;
    readonly IDXGIAdapter1 adapter;
    readonly ID3D11Device device;
    readonly ID3D11DeviceContext ctx;
    readonly IDXGISwapChain1 swap;
    readonly ID3D11RenderTargetView rtv;
    readonly ID3D11VertexShader vs;
    readonly ID3D11PixelShader ps, psOv;
    readonly ID3D11SamplerState sampler;
    readonly ID3D11BlendState blend;
    readonly ID3D11Buffer cb;
    ID3D11Texture2D texY, texUV, texRGB, texOv;
    ID3D11ShaderResourceView srvY, srvUV, srvRGB, srvOv;
    int vidW, vidH, ovW, ovH;
    public int RgbW, RgbH;
    public readonly int Width, Height;
    public readonly string AdapterName;

    public Renderer(IntPtr hwnd, string screenDevice, int width, int height)
    {
        Width = width; Height = height;
        factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();

        // видеокарта, к которой подключён проектор
        for (uint i = 0; adapter == null && factory.EnumAdapters1(i, out var a).Success; i++)
        {
            for (uint j = 0; a.EnumOutputs(j, out var o).Success; j++)
            {
                bool match = string.Equals(o.Description.DeviceName, screenDevice, StringComparison.OrdinalIgnoreCase);
                o.Dispose();
                if (match) { adapter = a; break; }
            }
            if (adapter != a) a.Dispose();
        }
        if (adapter == null) factory.EnumAdapters1(0, out adapter).CheckError();
        AdapterName = adapter.Description1.Description;

        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 },
            out device, out ctx).CheckError();

        var sd = new SwapChainDescription1
        {
            Width = (uint)width, Height = (uint)height, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), BufferUsage = Usage.RenderTargetOutput, BufferCount = 3,
            Scaling = Scaling.Stretch, SwapEffect = SwapEffect.FlipDiscard, AlphaMode = AlphaMode.Ignore,
        };
        swap = factory.CreateSwapChainForHwnd(device, hwnd, sd, new SwapChainFullscreenDescription { Windowed = true }, null);
        factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAll);
        using (var d1 = device.QueryInterface<IDXGIDevice1>()) d1.MaximumFrameLatency = 3;
        using (var bb = swap.GetBuffer<ID3D11Texture2D>(0)) rtv = device.CreateRenderTargetView(bb);

        var vsCode = Compiler.Compile(HlslVs, "VS", "c3d_vs.hlsl", "vs_4_0");
        var psCode = Compiler.Compile(Hlsl, "PS", "c3d_ps.hlsl", "ps_4_0");
        var ovCode = Compiler.Compile(HlslOv, "PSOv", "c3d_ov.hlsl", "ps_4_0");
        vs = device.CreateVertexShader(vsCode.Span);
        ps = device.CreatePixelShader(psCode.Span);
        psOv = device.CreatePixelShader(ovCode.Span);
        sampler = device.CreateSamplerState(SamplerDescription.LinearClamp);
        blend = device.CreateBlendState(BlendDescription.AlphaBlend);
        cb = device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<Consts>(), BindFlags.ConstantBuffer, ResourceUsage.Default));
    }

    ID3D11Texture2D Tex(Format f, int w, int h, out ID3D11ShaderResourceView srv)
    {
        var t = device.CreateTexture2D(new Texture2DDescription(f, (uint)w, (uint)h, 1, 1));
        srv = device.CreateShaderResourceView(t);
        return t;
    }

    /// <summary>Загрузить кадр видео NV12 (плоскость яркости + плоскость цвета).</summary>
    public unsafe void UploadNV12(byte[] data, int w, int h)
    {
        if (w != vidW || h != vidH)
        {
            srvY?.Dispose(); texY?.Dispose(); srvUV?.Dispose(); texUV?.Dispose();
            texY = Tex(Format.R8_UNorm, w, h, out srvY);
            texUV = Tex(Format.R8G8_UNorm, w / 2, h / 2, out srvUV);
            vidW = w; vidH = h;
        }
        fixed (byte* p = data)
        {
            ctx.UpdateSubresource(texY, 0, null, (IntPtr)p, (uint)w, 0);
            ctx.UpdateSubresource(texUV, 0, null, (IntPtr)(p + w * h), (uint)w, 0);
        }
    }

    /// <summary>Загрузить картинку BGRA (тестовая стереопара).</summary>
    public void UploadRgb(IntPtr bgra, int w, int h, int stride)
    {
        if (w != RgbW || h != RgbH) { srvRGB?.Dispose(); texRGB?.Dispose(); texRGB = Tex(Format.B8G8R8A8_UNorm, w, h, out srvRGB); RgbW = w; RgbH = h; }
        ctx.UpdateSubresource(texRGB, 0, null, bgra, (uint)stride, 0);
    }

    /// <summary>Загрузить надписи поверх картинки (BGRA с premultiplied alpha).</summary>
    public void UploadOverlay(IntPtr bgra, int w, int h, int stride)
    {
        if (w != ovW || h != ovH) { srvOv?.Dispose(); texOv?.Dispose(); texOv = Tex(Format.B8G8R8A8_UNorm, w, h, out srvOv); ovW = w; ovH = h; }
        ctx.UpdateSubresource(texOv, 0, null, bgra, (uint)stride, 0);
    }

    public bool HasVideo => vidW > 0;

    // ---------- захват монитора (видео из браузера) ----------
    IDXGIOutputDuplication dup;
    ID3D11Texture2D staging;
    string dupDevice;
    public long CapturedFrames;

    /// <summary>
    /// Начать захват монитора, на котором в браузере идёт видео. Монитор должен быть подключён
    /// к той же видеокарте, что и проектор. Возвращает текст ошибки или null.
    /// </summary>
    public string StartCapture(string screenDevice)
    {
        dupDevice = screenDevice;
        dup?.Dispose(); dup = null;
        for (uint j = 0; adapter.EnumOutputs(j, out var o).Success; j++)
        {
            using (o)
            {
                if (!string.Equals(o.Description.DeviceName, screenDevice, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using var o5 = o.QueryInterfaceOrNull<IDXGIOutput5>();
                    if (o5 != null) dup = o5.DuplicateOutput1(device, new[] { Format.B8G8R8A8_UNorm });
                    else { using var o1 = o.QueryInterface<IDXGIOutput1>(); dup = o1.DuplicateOutput(device); }
                }
                catch (Exception ex) { return "Не удалось начать захват экрана: " + ex.Message; }
            }
            break;
        }
        if (dup == null) return "Монитор для захвата не найден на видеокарте проектора (он должен быть подключён к той же видеокарте).";
        EnsureCaptureTex();
        return null;
    }

    void EnsureCaptureTex()
    {
        var m = dup.Description.ModeDescription;
        int w = (int)m.Width, h = (int)m.Height;
        if (w != RgbW || h != RgbH || texRGB == null)
        {
            srvRGB?.Dispose(); texRGB?.Dispose(); staging?.Dispose(); staging = null;
            texRGB = Tex(Format.B8G8R8A8_UNorm, w, h, out srvRGB);
            RgbW = w; RgbH = h;
        }
    }

    /// <summary>Взять новый кадр с монитора (если он изменился). true — картинка обновлена.</summary>
    public bool CaptureFrame()
    {
        if (dup == null) return false;
        var r = dup.AcquireNextFrame(0, out var info, out var res);
        if (r.Failure)
        {
            // монитор переключил режим, блокировка экрана и т.п. — начать захват заново
            if (r.Code != Vortice.DXGI.ResultCode.WaitTimeout.Code) { try { StartCapture(dupDevice); } catch { } }
            return false;
        }
        try
        {
            if (info.LastPresentTime == 0) return false; // изменился только курсор
            EnsureCaptureTex();
            using var t = res.QueryInterface<ID3D11Texture2D>();
            ctx.CopyResource(texRGB, t);
            CapturedFrames++;
            return true;
        }
        finally { res.Dispose(); dup.ReleaseFrame(); }
    }

    /// <summary>Отладка: сохранить последний захваченный кадр в PNG (как его видит программа).</summary>
    public unsafe void SaveCapture(string path)
    {
        if (texRGB == null) return;
        if (staging == null)
            staging = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)RgbW, (uint)RgbH, 1, 1,
                BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        ctx.CopyResource(staging, texRGB);
        var map = ctx.Map(staging, 0, MapMode.Read);
        try
        {
            using var bmp = new Bitmap(RgbW, RgbH, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, RgbW, RgbH), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            for (int y = 0; y < RgbH; y++)
                Buffer.MemoryCopy((byte*)map.DataPointer + (long)y * map.RowPitch, (byte*)bd.Scan0 + (long)y * bd.Stride, bd.Stride, RgbW * 4);
            bmp.UnlockBits(bd);
            bmp.Save(path);
        }
        finally { ctx.Unmap(staging, 0); }
    }

    /// <summary>
    /// Где на захваченном экране картинки глаз (доли 0..1) для каждого возможного формата
    /// и какой формат, похоже, у видео.
    /// </summary>
    public sealed class CaptureGeometry
    {
        public StereoLayout? Layout;
        public RectangleF Crop, SbsL, SbsR, OuL, OuR;
    }

    /// <summary>Один замер захваченного кадра: где есть картинка (строки/столбцы) и на какой формат похоже.</summary>
    public sealed class CaptureSample
    {
        public bool[] Rows, Cols;
        public int Sx, Sy, W, H;
        public StereoLayout? Layout;
        public bool Dark;
    }

    /// <summary>
    /// Замер кадра: строки и столбцы, где есть картинка (узкие полоски интерфейса сайта отброшены),
    /// и формат — сравнением картинок глаз в рамке hint (накопленной), а если её ещё нет — в рамке этого кадра.
    /// </summary>
    public unsafe CaptureSample SampleCapture(CaptureGeometry hint)
    {
        if (texRGB == null) return null;
        if (staging == null)
            staging = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)RgbW, (uint)RgbH, 1, 1,
                BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        ctx.CopyResource(staging, texRGB);
        var map = ctx.Map(staging, 0, MapMode.Read);
        try
        {
            byte* basePtr = (byte*)map.DataPointer;
            int pitch = (int)map.RowPitch;
            int Luma(int x, int y) { byte* p = basePtr + (long)y * pitch + x * 4; return (p[0] + 2 * p[1] + p[2]) >> 2; }

            // строка/столбец — картинка, если хотя бы 20% точек не чёрные (поля браузера и полосы фильма — чистый чёрный)
            int sx = Math.Max(1, RgbW / 320), sy = Math.Max(1, RgbH / 180);
            var rows = new bool[(RgbH + sy - 1) / sy];
            var cols = new bool[(RgbW + sx - 1) / sx];
            for (int y = 0; y < RgbH; y += sy)
            {
                int n = 0, tot = 0;
                for (int x = 0; x < RgbW; x += sx) { tot++; if (Luma(x, y) > 16) n++; }
                rows[y / sy] = n * 5 > tot;
            }
            for (int x = 0; x < RgbW; x += sx)
            {
                int n = 0, tot = 0;
                for (int y = 0; y < RgbH; y += sy) { tot++; if (Luma(x, y) > 16) n++; }
                cols[x / sx] = n * 5 > tot;
            }
            DropThin(rows); DropThin(cols);
            var s = new CaptureSample { Rows = rows, Cols = cols, Sx = sx, Sy = sy, W = RgbW, H = RgbH };

            var g = hint ?? Geometry(rows, cols, sx, sy, RgbW, RgbH);
            if (g == null) { s.Dark = true; return s; }

            // формат: у стереопары картинки глаз почти одинаковые (сравниваем с подбором сдвига ±3 точки)
            const int GW = 120, GH = 60;
            byte[] Grab(RectangleF r)
            {
                var a = new byte[GW * GH];
                for (int y = 0; y < GH; y++)
                    for (int x = 0; x < GW; x++)
                        a[y * GW + x] = (byte)Luma(Math.Clamp((int)((r.X + r.Width * x / GW) * RgbW), 0, RgbW - 1),
                                                   Math.Clamp((int)((r.Y + r.Height * y / GH) * RgbH), 0, RgbH - 1));
                return a;
            }
            static double Diff(byte[] a, byte[] b)
            {
                double best = double.MaxValue;
                for (int sh = -3; sh <= 3; sh++)
                {
                    long sum = 0; int cnt = 0;
                    for (int y = 0; y < GH; y++)
                        for (int x = 3; x < GW - 3; x++) { sum += Math.Abs(a[y * GW + x] - b[y * GW + x + sh]); cnt++; }
                    best = Math.Min(best, (double)sum / cnt);
                }
                return best;
            }
            var ouA = Grab(g.OuL);
            double mean = 0; foreach (var v in ouA) mean += v; mean /= ouA.Length;
            if (mean < 12) { s.Dark = true; return s; }                 // тёмная сцена: формат не судим
            double ou = Diff(ouA, Grab(g.OuR)), sbs = Diff(Grab(g.SbsL), Grab(g.SbsR));
            double best = Math.Min(sbs, ou), other = Math.Max(sbs, ou);
            if (best < 20 && best < other * 0.6) s.Layout = ou < sbs ? StereoLayout.Ou : StereoLayout.Sbs;
            else if (best >= 20) s.Layout = StereoLayout.Mono;
            return s;
        }
        finally { ctx.Unmap(staging, 0); }
    }

    /// <summary>Узкие полосы (строка прогресса, подписи, кнопки плеера сайта) — не видео: меньше 4% высоты/ширины.</summary>
    static void DropThin(bool[] act)
    {
        int min = Math.Max(2, act.Length / 25);
        for (int i = 0; i < act.Length;)
        {
            if (!act[i]) { i++; continue; }
            int j = i; while (j < act.Length && act[j]) j++;
            if (j - i < min) for (int k = i; k < j; k++) act[k] = false;
            i = j;
        }
    }

    /// <summary>
    /// Рамка видео и картинки глаз по строкам/столбцам, где есть картинка (обычно накопленным за минуту).
    /// У широкоформатного фильма между картинками глаз чёрная щель — её учитываем, чтобы глаза не разъехались.
    /// null — картинки нет.
    /// </summary>
    public static CaptureGeometry Geometry(bool[] rows, bool[] cols, int sx, int sy, int W, int H)
    {
        int fr = Array.IndexOf(rows, true), lr = Array.LastIndexOf(rows, true);
        int fc = Array.IndexOf(cols, true), lc = Array.LastIndexOf(cols, true);
        if (fr < 0 || fc < 0) return null;
        int y0 = fr * sy, y1 = Math.Min(H, (lr + 1) * sy), x0 = fc * sx, x1 = Math.Min(W, (lc + 1) * sx);
        if ((x1 - x0) * (double)(y1 - y0) < 0.15 * W * H) { x0 = 0; y0 = 0; x1 = W; y1 = H; }
        var c = new RectangleF((float)x0 / W, (float)y0 / H, (float)(x1 - x0) / W, (float)(y1 - y0) / H);
        var (hRows, _) = Split(rows, y0 / sy, Math.Min(rows.Length, (y1 + sy - 1) / sy));
        var (wCols, _) = Split(cols, x0 / sx, Math.Min(cols.Length, (x1 + sx - 1) / sx));
        float hEye = Math.Min(c.Height / 2, (float)hRows * sy / H);
        float wEye = Math.Min(c.Width / 2, (float)wCols * sx / W);
        return new CaptureGeometry
        {
            Crop = c,
            OuL = new RectangleF(c.Left, c.Top, c.Width, hEye),
            OuR = new RectangleF(c.Left, c.Bottom - hEye, c.Width, hEye),
            SbsL = new RectangleF(c.Left, c.Top, wEye, c.Height),
            SbsR = new RectangleF(c.Right - wEye, c.Top, wEye, c.Height),
        };
    }

    /// <summary>Поиск чёрной щели посередине (между картинками глаз); возвращает размер одной картинки.</summary>
    static (int a, int b) Split(bool[] act, int lo, int hi)
    {
        int mid = (lo + hi) / 2, span = hi - lo, best0 = -1, best1 = -1;
        for (int i = lo; i < hi;)
        {
            if (act[i]) { i++; continue; }
            int j = i; while (j < hi && !act[j]) j++;
            int center = (i + j) / 2;
            if (Math.Abs(center - mid) < span * 0.15 && j - i > best1 - best0) { best0 = i; best1 = j; }
            i = j;
        }
        if (best0 >= 0)
        {
            int h1 = best0 - lo, h2 = hi - best1;
            if (Math.Abs(h1 - h2) <= Math.Max(2, span / 10)) return (Math.Min(h1, h2), 0);
        }
        return (span / 2, 0);
    }

    /// <summary>Нарисовать кадр: rA — прямоугольник глаза в текстуре, mode 1 = анаглиф, src 1 = картинка BGRA.</summary>
    public unsafe void Draw(Vector4 rA, Vector4 rB, int mode, int src, float aspect, bool overlay, bool content)
    {
        ctx.OMSetRenderTargets(rtv);
        ctx.ClearRenderTargetView(rtv, new Color4(0, 0, 0, 1));
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(vs);
        ctx.PSSetSampler(0, sampler);
        if (content && aspect > 0)
        {
            float vw = Width, vh = Width / aspect;
            if (vh > Height) { vh = Height; vw = Height * aspect; }
            ctx.RSSetViewport(new Viewport((Width - vw) / 2, (Height - vh) / 2, vw, vh));
            var c = new Consts { A = rA, B = rB, Mode = mode, Src = src };
            ctx.UpdateSubresource(cb, 0, null, (IntPtr)(&c), 0, 0);
            ctx.PSSetConstantBuffer(0, cb);
            ctx.PSSetShader(ps);
            ctx.PSSetShaderResources(0, new[] { srvY, srvUV, srvRGB });
            ctx.OMSetBlendState(null);
            ctx.Draw(3, 0);
        }
        if (overlay && srvOv != null)
        {
            ctx.RSSetViewport(new Viewport(20, 20, ovW, ovH));
            ctx.PSSetShader(psOv);
            ctx.PSSetShaderResource(3, srvOv);
            ctx.OMSetBlendState(blend);
            ctx.Draw(3, 0);
        }
    }

    /// <summary>Поставить кадр в очередь на ближайший такт (блокируется, если очередь полна).</summary>
    public void Present() => swap.Present(1, PresentFlags.None);

    /// <summary>Статистика видеокарты: на каком такте экрана показан последний кадр.</summary>
    public bool TryStats(out FrameStatistics st)
    {
        try { return swap.GetFrameStatistics(out st).Success && st.PresentCount != 0; }
        catch { st = default; return false; }
    }

    public void Dispose()
    {
        dup?.Dispose(); staging?.Dispose();
        srvY?.Dispose(); texY?.Dispose(); srvUV?.Dispose(); texUV?.Dispose();
        srvRGB?.Dispose(); texRGB?.Dispose(); srvOv?.Dispose(); texOv?.Dispose();
        cb.Dispose(); blend.Dispose(); sampler.Dispose(); psOv.Dispose(); ps.Dispose(); vs.Dispose();
        rtv.Dispose(); swap.Dispose(); ctx.ClearState(); ctx.Dispose(); device.Dispose(); adapter.Dispose(); factory.Dispose();
    }
}
