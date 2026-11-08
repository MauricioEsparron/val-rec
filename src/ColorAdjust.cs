using System.Globalization;

namespace ValRec;

/// <summary>
/// Ajustes de color con los mismos rangos y fórmulas que el filtro "Corrección de color" de OBS (v2):
/// gamma (g&lt;0 → -g+1, si no 1/(g+1)), contraste (c&lt;0 → 1/(-c+1), si no c+1, escala desde 0),
/// saturación (valor+1, pesos 0,299/0,587/0,114), rotación de tono en RGB y luminosidad como desplazamiento.
/// </summary>
public class ColorAdjust
{
    public double Gamma { get; set; }        // -3 .. 3
    public double Contrast { get; set; }     // -4 .. 4
    public double Brightness { get; set; }   // -1 .. 1
    public double Saturation { get; set; }   // -1 .. 5
    public double Hue { get; set; }          // -180 .. 180
    public double Sharpness { get; set; }    // 0 .. 3 (extra, no existe en el filtro de OBS; imita S_Sharpen)
    public double MotionBlur { get; set; }   // 0 .. 1 (imita RSMB «Blur Amount»: mezcla de fotogramas vecinos)

    /// <summary>Los valores de la «Corrección de color» del usuario en OBS.</summary>
    public static ColorAdjust Obs() => new() { Contrast = 0.04, Brightness = 0.01, Saturation = 0.94 };

    /// <summary>Equivalente aproximado a los presets de Premiere del usuario (Sapphire S_HueSatBright + S_Sharpen + RSMB).</summary>
    public static ColorAdjust ValEdit() => new() { Contrast = 0.10, Saturation = 0.30, Sharpness = 1.5, MotionBlur = 0.4 };

    static string F(double x) => x.ToString("0.#####", CultureInfo.InvariantCulture);

    /// <summary>Cadena de filtros de ffmpeg (sobre BGRA, sin "format" inicial ni final). Vacía si no cambia nada.</summary>
    public string ToFilter()
    {
        var parts = new List<string>();

        // 1) Gamma (por canal)
        double g = Gamma < 0 ? -Gamma + 1 : 1 / (Gamma + 1);
        if (Math.Abs(g - 1) > 1e-4)
        {
            var e = $"clip(pow(val/255,{F(g)})*255,0,255)";
            parts.Add($"lutrgb=r='{e}':g='{e}':b='{e}'");
        }

        // 2) Matriz de color = Tono · Saturación · Contraste
        double c = Contrast < 0 ? 1 / (-Contrast + 1) : Contrast + 1;
        double s = Saturation + 1;
        double[] w = { 0.299, 0.587, 0.114 };
        var m = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                m[i, j] = c * ((i == j ? s : 0) + (1 - s) * w[j]);

        if (Math.Abs(Hue) > 1e-4)
        {
            double th = Hue * Math.PI / 180, cs = Math.Cos(th), sn = Math.Sin(th);
            double a = (1 - cs) / 3, q = sn / Math.Sqrt(3);
            double[,] h =
            {
                { cs + a, a - q, a + q },
                { a + q, cs + a, a - q },
                { a - q, a + q, cs + a },
            };
            var r = new double[3, 3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    for (int k = 0; k < 3; k++) r[i, j] += h[i, k] * m[k, j];
            m = r;
        }

        bool identity = true;
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                if (Math.Abs(m[i, j] - (i == j ? 1 : 0)) > 1e-4) identity = false;
        if (!identity)
        {
            string[] ch = { "r", "g", "b" };
            var terms = new List<string>();
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++) terms.Add($"{ch[i]}{ch[j]}={F(m[i, j])}");
            parts.Add("colorchannelmixer=" + string.Join(":", terms));
        }

        // 3) Luminosidad (desplazamiento)
        if (Math.Abs(Brightness) > 1e-4)
        {
            var e = $"clip(val+{F(Brightness * 255)},0,255)";
            parts.Add($"lutrgb=r='{e}':g='{e}':b='{e}'");
        }

        // 4) Nitidez (extra)
        if (Sharpness > 1e-4) parts.Add($"unsharp=5:5:{F(Sharpness)}:5:5:{F(Sharpness * 0.5)}");

        return string.Join(",", parts);
    }

    /// <summary>
    /// Parte temporal (desenfoque de movimiento): va ANTES del color.
    /// Rápido: mezcla el fotograma con el anterior. Calidad: interpola el movimiento (x2) y promedia los
    /// subfotogramas para cubrir "Blur Amount" del obturador (parecido a RSMB, mucho más lento).
    /// </summary>
    public string TemporalFilter(int fps, bool quality = false)
    {
        if (MotionBlur <= 1e-4) return "";
        if (!quality) return $"fps={fps},tmix=frames=2:weights='{F(MotionBlur)} 1'";
        const int N = 2;
        int n = Math.Max(2, (int)Math.Round(MotionBlur * N) + 1);
        return $"fps={fps},minterpolate=fps={fps * N}:mi_mode=mci:mc_mode=obmc:me=hexbs,tmix=frames={n},fps={fps}";
    }

    public ColorAdjust Clone() => (ColorAdjust)MemberwiseClone();
}
