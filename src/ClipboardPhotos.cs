using System.Drawing.Imaging;
using System.IO;

namespace Stationslocaties;

internal sealed class ClipboardPhotos : IDisposable
{
    private static readonly string Staging = Path.Combine(Path.GetTempPath(), "Stationlocaties-klembord");
    private readonly List<string> temporary = new();

    internal static bool HasPhoto()
    {
        try { return Clipboard.ContainsImage() || Clipboard.ContainsFileDropList(); }
        catch (System.Runtime.InteropServices.ExternalException) { return false; }
    }

    internal string? Paste(IWin32Window owner)
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                foreach (string? path in Clipboard.GetFileDropList())
                    if (path != null && File.Exists(path) && Path.GetExtension(path).ToLowerInvariant()
                        is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".tif" or ".tiff") return path;
            }

            using var image = Clipboard.GetImage();
            if (image == null)
            {
                MessageBox.Show(owner, "Kopieer eerst een foto of afbeeldingsbestand.", "Locatiefoto");
                return null;
            }

            Directory.CreateDirectory(Staging);
            var file = Path.Combine(Staging, Guid.NewGuid().ToString("N") + ".png");
            temporary.Add(file);
            image.Save(file, ImageFormat.Png);
            return file;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(owner, "De foto kon niet worden geplakt. " + ex.Message, "Locatiefoto");
            return null;
        }
    }

    internal static bool IsStaged(string file) => Path.IsPathFullyQualified(file) &&
        string.Equals(Path.GetDirectoryName(file), Staging, StringComparison.OrdinalIgnoreCase);

    internal static string Persist(string file, string? drawing)
    {
        if (!IsStaged(file)) return file;
        if (!string.IsNullOrWhiteSpace(drawing) && Path.IsPathFullyQualified(drawing) && File.Exists(drawing))
        {
            var folder = Path.Combine(Path.GetDirectoryName(drawing)!, "Stationlocaties-fotos");
            try { return CopyTo(folder, file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return CopyTo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Stationlocaties", "Fotos"), file);
    }

    private static string CopyTo(string folder, string file)
    {
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, Path.GetFileName(file));
        if (!File.Exists(target)) File.Copy(file, target);
        return target;
    }

    public void Dispose()
    {
        foreach (var file in temporary)
        {
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

