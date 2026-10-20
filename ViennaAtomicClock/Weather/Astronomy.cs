namespace ViennaAtomicClock.Weather;

/// <summary>
/// Sun and moon positions, rise/set times and moon phase (low-precision formulas after Meeus / SunCalc,
/// accurate to a few minutes – plenty for a weather page).
/// </summary>
public static class Astronomy
{
    private const double Rad = Math.PI / 180;
    private const double J1970 = 2440588;
    private const double J2000 = 2451545;
    private const double J0 = 0.0009;
    private const double Obliquity = Rad * 23.4397;
    public const double SynodicMonth = 29.530588853;

    public static AstronomyData ForDay(DateTimeOffset localMidnight, TimeZoneInfo zone, double latitude, double longitude)
    {
        // Noon of the local day picks the right solar cycle.
        var noon = localMidnight.AddHours(12);
        var sun = SunTimes(noon.UtcDateTime, latitude, longitude);
        var (rise, set) = MoonTimes(localMidnight.UtcDateTime, latitude, longitude);
        var (phase, fraction) = MoonIllumination(noon.UtcDateTime);

        return new AstronomyData
        {
            Sunrise = Local(sun.Sunrise, zone),
            Sunset = Local(sun.Sunset, zone),
            CivilDawn = Local(sun.Dawn, zone),
            CivilDusk = Local(sun.Dusk, zone),
            Moonrise = Local(rise, zone),
            Moonset = Local(set, zone),
            MoonPhase = phase,
            MoonIllumination = fraction,
            DaysToFullMoon = (0.5 - phase + 1) % 1 * SynodicMonth,
            DaysToNewMoon = (1 - phase) % 1 * SynodicMonth
        };
    }

    private static DateTimeOffset? Local(DateTime? utc, TimeZoneInfo zone) =>
        utc is { } value ? TimeZoneInfo.ConvertTime(new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)), zone) : null;

    /// <summary>Altitude of the sun above the horizon in degrees.</summary>
    public static double SunAltitude(DateTime utc, double latitude, double longitude)
    {
        var lw = Rad * -longitude;
        var phi = Rad * latitude;
        var d = ToDays(utc);
        var (dec, ra) = SunCoords(d);
        var h = SiderealTime(d, lw) - ra;
        return Altitude(h, phi, dec) / Rad;
    }

    public static (DateTime? Sunrise, DateTime? Sunset, DateTime? Dawn, DateTime? Dusk) SunTimes(DateTime utc, double latitude, double longitude)
    {
        var lw = Rad * -longitude;
        var phi = Rad * latitude;
        var d = ToDays(utc);
        var n = Math.Round(d - J0 - lw / (2 * Math.PI));
        var ds = J0 + lw / (2 * Math.PI) + n;
        var m = SolarMeanAnomaly(ds);
        var l = EclipticLongitude(m);
        var dec = Declination(l, 0);
        var noon = J2000 + ds + 0.0053 * Math.Sin(m) - 0.0069 * Math.Sin(2 * l);

        (DateTime?, DateTime?) Pair(double angle)
        {
            var w = HourAngle(angle * Rad, phi, dec);
            if (double.IsNaN(w)) return (null, null);
            var a = J0 + (w + lw) / (2 * Math.PI) + n;
            var set = J2000 + a + 0.0053 * Math.Sin(m) - 0.0069 * Math.Sin(2 * l);
            var rise = noon - (set - noon);
            return (FromJulian(rise), FromJulian(set));
        }

        var (sunrise, sunset) = Pair(-0.833);
        var (dawn, dusk) = Pair(-6);
        return (sunrise, sunset, dawn, dusk);
    }

    public static double MoonAltitude(DateTime utc, double latitude, double longitude)
    {
        var lw = Rad * -longitude;
        var phi = Rad * latitude;
        var d = ToDays(utc);
        var (ra, dec, _) = MoonCoords(d);
        var h = SiderealTime(d, lw) - ra;
        var altitude = Altitude(h, phi, dec);
        return (altitude + Refraction(altitude)) / Rad;
    }

    /// <summary>Phase (0 new … 0.5 full … 1) and illuminated fraction.</summary>
    public static (double Phase, double Fraction) MoonIllumination(DateTime utc)
    {
        var d = ToDays(utc);
        var (sDec, sRa) = SunCoords(d);
        var (mRa, mDec, mDist) = MoonCoords(d);
        const double sunDistance = 149598000;
        var phi = Math.Acos(Math.Sin(sDec) * Math.Sin(mDec) + Math.Cos(sDec) * Math.Cos(mDec) * Math.Cos(sRa - mRa));
        var inc = Math.Atan2(sunDistance * Math.Sin(phi), mDist - sunDistance * Math.Cos(phi));
        var angle = Math.Atan2(Math.Cos(sDec) * Math.Sin(sRa - mRa),
            Math.Sin(sDec) * Math.Cos(mDec) - Math.Cos(sDec) * Math.Sin(mDec) * Math.Cos(sRa - mRa));
        var fraction = (1 + Math.Cos(inc)) / 2;
        var phase = 0.5 + 0.5 * inc * (angle < 0 ? -1 : 1) / Math.PI;
        return (phase, fraction);
    }

    /// <summary>Moon rise and set within the 24 hours after <paramref name="startUtc"/> (null when it does not happen).</summary>
    public static (DateTime? Rise, DateTime? Set) MoonTimes(DateTime startUtc, double latitude, double longitude)
    {
        const double hc = 0.133 * Rad;
        double H(double hours) => MoonAltitude(startUtc.AddHours(hours), latitude, longitude) * Rad - hc;

        var h0 = H(0);
        double? rise = null, set = null;
        for (var i = 1; i <= 24; i += 2)
        {
            var h1 = H(i);
            var h2 = H(i + 1);
            var a = (h0 + h2) / 2 - h1;
            var b = (h2 - h0) / 2;
            var xe = -b / (2 * a);
            var ye = (a * xe + b) * xe + h1;
            var d = b * b - 4 * a * h1;
            var roots = 0;
            double x1 = 0, x2 = 0;
            if (d >= 0)
            {
                var dx = Math.Sqrt(d) / (Math.Abs(a) * 2);
                x1 = xe - dx;
                x2 = xe + dx;
                if (Math.Abs(x1) <= 1) roots++;
                if (Math.Abs(x2) <= 1) roots++;
                if (x1 < -1) x1 = x2;
            }

            if (roots == 1)
            {
                if (h0 < 0) rise = i + x1;
                else set = i + x1;
            }
            else if (roots == 2)
            {
                rise = i + (ye < 0 ? x2 : x1);
                set = i + (ye < 0 ? x1 : x2);
            }

            if (rise is not null && set is not null) break;
            h0 = h2;
        }

        return (rise is { } r ? startUtc.AddHours(r) : null, set is { } s ? startUtc.AddHours(s) : null);
    }

    private static double ToDays(DateTime utc) =>
        (utc - DateTime.UnixEpoch).TotalDays - 0.5 + J1970 - J2000;

    private static DateTime FromJulian(double julian) =>
        DateTime.UnixEpoch.AddDays(julian + 0.5 - J1970);

    private static double RightAscension(double l, double b) =>
        Math.Atan2(Math.Sin(l) * Math.Cos(Obliquity) - Math.Tan(b) * Math.Sin(Obliquity), Math.Cos(l));

    private static double Declination(double l, double b) =>
        Math.Asin(Math.Sin(b) * Math.Cos(Obliquity) + Math.Cos(b) * Math.Sin(Obliquity) * Math.Sin(l));

    private static double Altitude(double h, double phi, double dec) =>
        Math.Asin(Math.Sin(phi) * Math.Sin(dec) + Math.Cos(phi) * Math.Cos(dec) * Math.Cos(h));

    private static double SiderealTime(double d, double lw) => Rad * (280.16 + 360.9856235 * d) - lw;

    private static double Refraction(double h)
    {
        if (h < 0) h = 0;
        return 0.0002967 / Math.Tan(h + 0.00312536 / (h + 0.08901179));
    }

    private static double SolarMeanAnomaly(double d) => Rad * (357.5291 + 0.98560028 * d);

    private static double EclipticLongitude(double m)
    {
        var c = Rad * (1.9148 * Math.Sin(m) + 0.02 * Math.Sin(2 * m) + 0.0003 * Math.Sin(3 * m));
        return m + c + Rad * 102.9372 + Math.PI;
    }

    private static (double Dec, double Ra) SunCoords(double d)
    {
        var l = EclipticLongitude(SolarMeanAnomaly(d));
        return (Declination(l, 0), RightAscension(l, 0));
    }

    private static double HourAngle(double h, double phi, double dec)
    {
        var cos = (Math.Sin(h) - Math.Sin(phi) * Math.Sin(dec)) / (Math.Cos(phi) * Math.Cos(dec));
        return cos is < -1 or > 1 ? double.NaN : Math.Acos(cos);
    }

    private static (double Ra, double Dec, double Distance) MoonCoords(double d)
    {
        var l = Rad * (218.316 + 13.176396 * d);
        var m = Rad * (134.963 + 13.064993 * d);
        var f = Rad * (93.272 + 13.229350 * d);
        var lon = l + Rad * 6.289 * Math.Sin(m);
        var lat = Rad * 5.128 * Math.Sin(f);
        var distance = 385001 - 20905 * Math.Cos(m);
        return (RightAscension(lon, lat), Declination(lon, lat), distance);
    }
}
