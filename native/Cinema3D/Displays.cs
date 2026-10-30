using System.Runtime.InteropServices;

namespace Cinema3D;

public record DisplayInfo(Screen Screen, int Hz)
{
    public string Device => Screen.DeviceName;
    public override string ToString() =>
        $"{Screen.DeviceName.Replace(@"\\.\", "")} — {Screen.Bounds.Width}×{Screen.Bounds.Height} @ {Hz} Гц{(Screen.Primary ? " (основной)" : "")}";
}

/// <summary>Экраны Windows и их текущая частота обновления.</summary>
public static class Displays
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettings(string name, int mode, ref DEVMODE dm);

    public static int Hz(string device)
    {
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        return EnumDisplaySettings(device, -1, ref dm) ? dm.dmDisplayFrequency : 0;
    }

    public static List<DisplayInfo> All() => Screen.AllScreens.Select(s => new DisplayInfo(s, Hz(s.DeviceName))).ToList();

    public static bool Is120(DisplayInfo d) => d.Hz is >= 100 and <= 130;

    /// <summary>
    /// Проектор — экран с частотой ~120 Гц. Windows может переименовать экраны (DISPLAY1 ↔ DISPLAY2)
    /// после перезагрузки или переподключения, поэтому сохранённое имя берём, только если у этого
    /// экрана сейчас действительно ~120 Гц.
    /// </summary>
    public static DisplayInfo PickProjector(string saved)
    {
        var all = All();
        return all.FirstOrDefault(d => d.Device == saved && Is120(d))
            ?? all.FirstOrDefault(d => Is120(d) && !d.Screen.Primary)
            ?? all.FirstOrDefault(Is120)
            ?? all.FirstOrDefault(d => d.Device == saved)
            ?? all.FirstOrDefault(d => !d.Screen.Primary)
            ?? all[0];
    }
}
