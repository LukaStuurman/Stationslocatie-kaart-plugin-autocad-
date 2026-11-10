using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;

namespace Stationslocaties;

internal readonly record struct PhotoFrame(double Left, double Bottom, double Width, double Height)
{
    internal static PhotoFrame Default => new(307.499828456, 219.528452085, 109.319524350, 74.374734319);
}

internal static class PhotoRaster
{

    internal static string Prepare(string source, string? drawing, PhotoFrame frame)
    {
        using var input = File.OpenRead(source);
        var name = "uitsnede-" + Convert.ToHexString(SHA256.HashData(input)) +
            FormattableString.Invariant($"-{frame.Width / frame.Height:F6}.png");
        input.Position = 0;

        if (!string.IsNullOrWhiteSpace(drawing) && Path.IsPathFullyQualified(drawing) && File.Exists(drawing))
        {
            var folder = Path.Combine(Path.GetDirectoryName(drawing)!, "Stationlocaties-fotos");
            try { return Save(input, folder, name, frame); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { input.Position = 0; }
        }

        return Save(input, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Stationlocaties", "Fotos"), name, frame);
    }

    private static string Save(Stream source, string folder, string name, PhotoFrame frame)
    {
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, name);
        if (File.Exists(target)) return target;

        using var photo = Image.FromStream(source);
        const int outputWidth = 2186;
        var outputHeight = Math.Clamp((int)Math.Round(outputWidth * frame.Height / frame.Width), 1, 6000);
        var scale = Math.Max(frame.Width / photo.Width, frame.Height / photo.Height);
        var width = (float)(frame.Width / scale);
        var height = (float)(frame.Height / scale);
        using var canvas = new Bitmap(outputWidth, outputHeight, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(canvas))
        using (var attributes = new ImageAttributes())
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            graphics.DrawImage(photo, new Rectangle(0, 0, outputWidth, outputHeight),
                (photo.Width - width) / 2, (photo.Height - height) / 2, width, height, GraphicsUnit.Pixel, attributes);
        }

        var pending = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = File.Create(pending)) canvas.Save(file, ImageFormat.Png);
            try { File.Move(pending, target); }
            catch (IOException) when (File.Exists(target)) { }
        }
        finally { File.Delete(pending); }
        return target;
    }
}

