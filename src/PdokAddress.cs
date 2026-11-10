using System.Net.Http;
using System.Text.Json;
using Autodesk.AutoCAD.Geometry;

namespace Stationslocaties;

internal static class PdokAddress
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(5) };

    internal static (string? Address, string Message) Lookup(Point3d point)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
            point.X is < -10000 or > 300000 || point.Y is < 280000 or > 650000)
            return (null, "Adres opzoeken vereist RD-coördinaten in meters.");
        var url = FormattableString.Invariant(
            $"https://api.pdok.nl/bzk/locatieserver/search/v3_1/reverse?X={point.X:0.###}&Y={point.Y:0.###}&type=adres&rows=1&fl=weergavenaam&distance=1000");
        try
        {
            var json = Client.GetStringAsync(url).GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(json);
            var addresses = document.RootElement.GetProperty("response").GetProperty("docs");
            if (addresses.GetArrayLength() > 0 &&
                addresses[0].TryGetProperty("weergavenaam", out var name) &&
                name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 } address)
                return (address, "Dichtstbijzijnd BAG-adres volgens PDOK: " + address);
            return (null, "PDOK vond geen BAG-adres binnen 1 km.");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or
            JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return (null, "PDOK kon het adres niet ophalen. Vul het adres desgewenst zelf in.");
        }
    }
}
