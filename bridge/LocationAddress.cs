using System.Text.Json;
using Windows.Devices.Geolocation;
using Windows.Services.Maps;

namespace OtzariaBridge;

/// <summary>#105: כתובת לפי מיקום דרך MapLocationFinder של Windows — ניסיון בלבד.
/// במחשבים רגילים השירות דורש לרוב מפתח שירות ונכשל, ולכן התשובה תמיד מצליחה עם found=false,
/// והתוסף נופל ל-OpenStreetMap. Windows אינו מחזיק נתוני רחובות/מקומות — אלה נשארים מ-OSM בתוסף.
/// POST /sys/location/address: { lat, lon }
///   → { found, source: "windows", town, district, region, country, street, number, postcode, display } או { found:false, reason }</summary>
internal static class LocationAddress
{
    public static void Register() => Server.Route("/sys/location/address", Get);

    private static async Task<object> Get(JsonElement b)
    {
        double lat = b.Num("lat") ?? double.NaN, lon = b.Num("lon") ?? double.NaN;
        if (double.IsNaN(lat) || double.IsNaN(lon) || Math.Abs(lat) > 90 || Math.Abs(lon) > 180)
            throw new BridgeException("bad_request", "חסרים lat/lon תקינים", 400);
        try
        {
            var point = new Geopoint(new BasicGeoposition { Latitude = lat, Longitude = lon });
            var task = MapLocationFinder.FindLocationsAtAsync(point).AsTask();
            if (await Task.WhenAny(task, Task.Delay(8000)) != task)
            {
                task.Observe("location address", quiet: true);
                return new { found = false, reason = "timeout" };
            }
            var r = task.Result;
            if (r.Status != MapLocationFinderStatus.Success || r.Locations.Count == 0)
                return new { found = false, reason = r.Status.ToString() };
            var a = r.Locations[0].Address;
            return new
            {
                found = true,
                source = "windows",
                town = a.Town,
                district = a.District,
                region = a.Region,
                country = a.Country,
                street = a.Street,
                number = a.StreetNumber,
                postcode = a.PostCode,
                display = r.Locations[0].DisplayName,
            };
        }
        catch (Exception ex)
        {
            Config.Log("location address: " + ex.GetType().Name + ": " + ex.Message);
            return new { found = false, reason = "unavailable" };
        }
    }
}
