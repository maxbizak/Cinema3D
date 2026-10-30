using System.Numerics;
using System.Text.RegularExpressions;

namespace Cinema3D;

/// <summary>Как уложены кадры глаз в файле.</summary>
public enum StereoLayout { Auto, Sbs, Ou, Mono }

/// <summary>Что выводить на проектор.</summary>
public enum OutMode { Dlp, Mono2D, Anaglyph }

public static class Stereo
{
    static readonly Regex OuRx = new(@"(^|[^a-z])(h-?ou|half-?ou|ou|tab|h-?tab|top-?bottom|over-?under|abl|tb|3d-?ou)([^a-z]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex SbsRx = new(@"(^|[^a-z])(h-?sbs|half-?sbs|f-?sbs|full-?sbs|sbs|side-?by-?side|lr|3d-?sbs)([^a-z]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Формат по метке в названии файла (SBS, HOU, TAB…), null — метки нет.</summary>
    public static StereoLayout? DetectByName(string name) =>
        OuRx.IsMatch(name) ? StereoLayout.Ou : SbsRx.IsMatch(name) ? StereoLayout.Sbs : null;

    /// <summary>Формат по имени файла, иначе по пропорциям кадра (dw — ширина с учётом SAR).</summary>
    public static StereoLayout Detect(string name, double dw, double h)
    {
        if (DetectByName(name) is StereoLayout byName) return byName;
        if (dw > 0 && h > 0)
        {
            if (dw / h >= 3) return StereoLayout.Sbs;
            if (h / dw >= 0.9) return StereoLayout.Ou;
        }
        return StereoLayout.Sbs; // самый частый формат скачиваемых 3D-фильмов (Half-SBS)
    }

    /// <summary>
    /// Формат по одному кадру в оттенках серого (w×h): у стереопары половины почти одинаковые.
    /// Сравниваем левую с правой и верхнюю с нижней (с подбором сдвига ±3 пикселя).
    /// </summary>
    public static StereoLayout? DetectByHalves(byte[] px, int w, int h, out string scores)
    {
        double sbs = HalfDiff(px, w, h, true), ou = HalfDiff(px, w, h, false);
        scores = $"sbs={sbs:F1} ou={ou:F1}";
        double best = Math.Min(sbs, ou), other = Math.Max(sbs, ou);
        if (best < 20 && best < other * 0.6) return ou < sbs ? StereoLayout.Ou : StereoLayout.Sbs;
        if (best >= 20) return StereoLayout.Mono;
        return null;
    }

    public static double HalfDiff(byte[] p, int w, int h, bool sideBySide)
    {
        double best = double.MaxValue;
        for (int s = -3; s <= 3; s++)
        {
            long sum = 0; int cnt = 0;
            if (sideBySide)
            {
                int half = w / 2;
                for (int y = 0; y < h; y++)
                    for (int x = 3; x < half - 3; x++) { sum += Math.Abs(p[y * w + x] - p[y * w + half + x + s]); cnt++; }
            }
            else
            {
                int half = h / 2;
                for (int y = 0; y < half; y++)
                    for (int x = 3; x < w - 3; x++) { sum += Math.Abs(p[y * w + x] - p[(y + half) * w + x + s]); cnt++; }
            }
            best = Math.Min(best, (double)sum / cnt);
        }
        return best;
    }

    public static string Name(StereoLayout l) => l switch
    {
        StereoLayout.Sbs => "SBS (рядом)", StereoLayout.Ou => "OU (сверху/снизу)", StereoLayout.Mono => "2D", _ => "Авто"
    };

    public static string Name(OutMode m) => m switch
    {
        OutMode.Dlp => "DLP 3D 120 Гц", OutMode.Mono2D => "2D", _ => "Анаглиф"
    };

    /// <summary>
    /// Прямоугольники левого и правого глаза в текстуре (x, y, w, h в долях 0..1)
    /// и пропорции картинки одного глаза. dw — ширина кадра с учётом SAR.
    /// </summary>
    public static (Vector4 L, Vector4 R, float Aspect) Eyes(StereoLayout l, double dw, double h)
    {
        if (l == StereoLayout.Sbs)
        {
            double full = dw / 2 / h; // Full-SBS 3840x1080 -> 16:9 на глаз; Half-SBS -> растянуть до кадра
            return (new(0, 0, .5f, 1), new(.5f, 0, .5f, 1), (float)(full >= 1.2 ? full : dw / h));
        }
        if (l == StereoLayout.Ou)
        {
            double full = dw / (h / 2);
            return (new(0, 0, 1, .5f), new(0, .5f, 1, .5f), (float)(full <= 2.5 && dw / h < 1.2 ? full : dw / h));
        }
        return (new(0, 0, 1, 1), new(0, 0, 1, 1), (float)(dw / h));
    }
}
