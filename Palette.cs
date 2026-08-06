using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Vinlyo;

/// <summary>
/// Extraction de la couleur dominante d'une pochette.
///
/// L'objectif n'est pas la justesse colorimetrique mais la reconnaissance : on
/// cherche la teinte que l'oeil retient en regardant la pochette. Les pixels
/// quasi noirs, quasi blancs et gris sont donc ecartes, car ils dominent
/// souvent en nombre sans rien dire de l'identite visuelle du disque.
/// </summary>
public static class Palette
{
    /// <summary>
    /// Rend la teinte dominante (en degres, 0-360) et sa saturation moyenne,
    /// ou null si la pochette est trop neutre pour qu'une teinte ait un sens.
    /// </summary>
    public static (double Hue, double Saturation)? Dominant(byte[] data)
    {
        try
        {
            // Une vignette de 24 pixels de large suffit largement : on cherche
            // une tendance d'ensemble, pas un detail.
            using var stream = new MemoryStream(data);

            var source = new BitmapImage();
            source.BeginInit();
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            source.DecodePixelWidth = 24;
            source.StreamSource = stream;
            source.EndInit();

            // Le format d'origine varie selon l'encodeur de la pochette :
            // on normalise avant de lire les octets.
            var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            var width = bitmap.PixelWidth;
            var height = bitmap.PixelHeight;
            if (width == 0 || height == 0) return null;

            var stride = width * 4;
            var pixels = new byte[stride * height];
            bitmap.CopyPixels(pixels, stride, 0);

            // Les teintes sont des angles : en faire la moyenne arithmetique
            // donnerait n'importe quoi (350 deg et 10 deg donneraient 180).
            // On accumule donc des vecteurs unitaires, ponderes par la
            // saturation et par la vivacite du pixel.
            double sumX = 0, sumY = 0, sumSat = 0, weight = 0;

            for (var i = 0; i < pixels.Length; i += 4)
            {
                double b = pixels[i] / 255.0;
                double g = pixels[i + 1] / 255.0;
                double r = pixels[i + 2] / 255.0;

                var (h, s, v) = ToHsv(r, g, b);

                // Trop sombre, trop clair ou trop gris : sans identite.
                if (v < 0.15 || v > 0.97 || s < 0.18) continue;

                var w = s * v;
                var radians = h * Math.PI / 180.0;

                sumX += Math.Cos(radians) * w;
                sumY += Math.Sin(radians) * w;
                sumSat += s * w;
                weight += w;
            }

            if (weight < 0.5) return null;   // pochette monochrome ou noir et blanc

            var hue = Math.Atan2(sumY, sumX) * 180.0 / Math.PI;
            if (hue < 0) hue += 360;

            return (hue, Math.Clamp(sumSat / weight, 0, 1));
        }
        catch
        {
            return null;
        }
    }

    private static (double H, double S, double V) ToHsv(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        double h = 0;
        if (delta > 0.0001)
        {
            if (max == r) h = 60 * (((g - b) / delta) % 6);
            else if (max == g) h = 60 * (((b - r) / delta) + 2);
            else h = 60 * (((r - g) / delta) + 4);
        }
        if (h < 0) h += 360;

        var s = max <= 0.0001 ? 0 : delta / max;
        return (h, s, max);
    }

    /// <summary>Construit une couleur a partir d'une teinte TSV.</summary>
    public static Color FromHsv(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        value = Math.Clamp(value, 0, 1);

        var c = value * saturation;
        var x = c * (1 - Math.Abs((hue / 60 % 2) - 1));
        var m = value - c;

        double r, g, b;
        if (hue < 60) { r = c; g = x; b = 0; }
        else if (hue < 120) { r = x; g = c; b = 0; }
        else if (hue < 180) { r = 0; g = c; b = x; }
        else if (hue < 240) { r = 0; g = x; b = c; }
        else if (hue < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }

        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}
