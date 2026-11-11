using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Stationslocaties;

internal sealed record ClientProfile(Guid Id, string Name, string Address, string Phone, string Website, string Email, string Logo)
{
    public override string ToString() => Name;
}

internal static class ClientLibrary
{
    private const string BuiltInLogo = "@Timmermans";
    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stationlocaties", "Opdrachtgevers");
    private static string Catalog => Path.Combine(Folder, "Opdrachtgevers.json");

    internal static ClientProfile[] Read()
    {
        if (!File.Exists(Catalog))
            return [new(new Guid("799d03e4-9eab-4f3a-92ec-745741fe9cec"), "Timmermans Infra", "Rijksweg 55b 5391 LJ Nuland",
                "073 – 3030840", "https://www.timmermansinfra.nl/", "info@timmermansinfra.nl", BuiltInLogo)];
        return JsonSerializer.Deserialize<ClientProfile[]>(File.ReadAllText(Catalog)) ?? [];
    }

    internal static void Save(ClientProfile[] profiles)
    {
        Directory.CreateDirectory(Folder);
        var pending = Path.Combine(Folder, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(pending, JsonSerializer.Serialize(profiles));
            File.Move(pending, Catalog, overwrite: true);
        }
        finally { File.Delete(pending); }
    }

    internal static string ImportLogo(string path)
    {
        using var source = Image.FromFile(path);
        using var bitmap = new Bitmap(source);
        using var png = new MemoryStream();
        bitmap.Save(png, ImageFormat.Png);
        return StoreLogo(png.ToArray());
    }

    internal static string LogoPath(string logo)
    {
        if (logo.Length == 0) return "";
        if (logo != BuiltInLogo) return Path.Combine(Folder, logo);
        using var stream = typeof(ClientLibrary).Assembly.GetManifestResourceStream("Timmermans.png");
        if (stream == null) return "";
        using var data = new MemoryStream();
        stream.CopyTo(data);
        return Path.Combine(Folder, StoreLogo(data.ToArray()));
    }

    private static string StoreLogo(byte[] data)
    {
        Directory.CreateDirectory(Folder);
        var name = Convert.ToHexString(SHA256.HashData(data)) + ".png";
        var path = Path.Combine(Folder, name);
        if (!File.Exists(path))
        {
            var pending = Path.Combine(Folder, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllBytes(pending, data);
                try { File.Move(pending, path); }
                catch (IOException) when (File.Exists(path)) { }
            }
            finally { File.Delete(pending); }
        }
        return name;
    }
}
