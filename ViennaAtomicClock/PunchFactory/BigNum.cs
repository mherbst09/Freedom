using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ViennaAtomicClock.PunchFactory;

/// <summary>
/// Number for idle-game amounts far beyond <see cref="double"/>: a mantissa in [1, 10) and a 64-bit power of ten.
/// Precision is that of a double (≈15 digits), which is all an incremental game needs; the range is practically unlimited.
/// </summary>
[JsonConverter(typeof(BigNumJsonConverter))]
public readonly struct BigNum : IComparable<BigNum>, IEquatable<BigNum>
{
    public static readonly BigNum Zero = new(0, 0);
    public static readonly BigNum One = new(1, 0);

    public BigNum(double mantissa, long exponent)
    {
        if (mantissa == 0 || double.IsNaN(mantissa))
        {
            Mantissa = 0;
            Exponent = 0;
            return;
        }
        if (double.IsInfinity(mantissa)) mantissa = Math.Sign(mantissa) * 9.999999999999;
        var shift = (long)Math.Floor(Math.Log10(Math.Abs(mantissa)));
        Mantissa = mantissa / Math.Pow(10, shift);
        Exponent = exponent + shift;
        // Rounding can leave 10.0 or 0.9999…
        if (Math.Abs(Mantissa) >= 10) { Mantissa /= 10; Exponent++; }
        else if (Math.Abs(Mantissa) < 1) { Mantissa *= 10; Exponent--; }
    }

    public double Mantissa { get; }
    public long Exponent { get; }
    public bool IsZero => Mantissa == 0;
    public bool IsNegative => Mantissa < 0;

    public static implicit operator BigNum(double value) => FromDouble(value);
    public static implicit operator BigNum(long value) => FromDouble(value);
    public static implicit operator BigNum(int value) => FromDouble(value);

    public static BigNum FromDouble(double value) => double.IsNaN(value) || value == 0 ? Zero : new BigNum(value, 0);

    /// <summary>The value as double (±∞ beyond 1e308).</summary>
    public double ToDouble()
    {
        if (IsZero) return 0;
        if (Exponent > 308) return Mantissa > 0 ? double.PositiveInfinity : double.NegativeInfinity;
        if (Exponent < -323) return 0;
        return Mantissa * Math.Pow(10, Exponent);
    }

    /// <summary>log10 of the absolute value (−∞ for zero).</summary>
    public double Log10 => IsZero ? double.NegativeInfinity : Exponent + Math.Log10(Math.Abs(Mantissa));

    public static BigNum operator +(BigNum a, BigNum b)
    {
        if (a.IsZero) return b;
        if (b.IsZero) return a;
        var big = a.Exponent >= b.Exponent ? a : b;
        var small = a.Exponent >= b.Exponent ? b : a;
        var diff = big.Exponent - small.Exponent;
        if (diff > 17) return big; // the smaller value is below double precision
        return new BigNum(big.Mantissa + small.Mantissa * Math.Pow(10, -diff), big.Exponent);
    }

    public static BigNum operator -(BigNum a) => new(-a.Mantissa, a.Exponent);
    public static BigNum operator -(BigNum a, BigNum b) => a + -b;
    public static BigNum operator *(BigNum a, BigNum b) => a.IsZero || b.IsZero ? Zero : new BigNum(a.Mantissa * b.Mantissa, a.Exponent + b.Exponent);
    public static BigNum operator *(BigNum a, double b) => a * FromDouble(b);
    public static BigNum operator /(BigNum a, BigNum b)
    {
        if (b.IsZero) throw new DivideByZeroException();
        return a.IsZero ? Zero : new BigNum(a.Mantissa / b.Mantissa, a.Exponent - b.Exponent);
    }
    public static BigNum operator /(BigNum a, double b) => a / FromDouble(b);

    /// <summary>a^b for real exponents (works for huge results).</summary>
    public static BigNum Pow(BigNum value, double power)
    {
        if (value.IsZero) return power == 0 ? One : Zero;
        if (value.IsNegative) throw new ArgumentOutOfRangeException(nameof(value), "Negative base.");
        var log = value.Log10 * power;
        return FromLog10(log);
    }

    public static BigNum FromLog10(double log)
    {
        if (double.IsNegativeInfinity(log)) return Zero;
        var exponent = (long)Math.Floor(log);
        return new BigNum(Math.Pow(10, log - exponent), exponent);
    }

    public static BigNum Max(BigNum a, BigNum b) => a >= b ? a : b;
    public static BigNum Min(BigNum a, BigNum b) => a <= b ? a : b;
    public BigNum Floor() => Exponent >= 15 || IsZero ? this : FromDouble(Math.Floor(ToDouble()));

    public int CompareTo(BigNum other)
    {
        if (IsNegative != other.IsNegative) return IsNegative ? -1 : 1;
        if (IsZero || other.IsZero) return Mantissa.CompareTo(other.Mantissa);
        var sign = IsNegative ? -1 : 1;
        if (Exponent != other.Exponent) return Exponent.CompareTo(other.Exponent) * sign;
        return Mantissa.CompareTo(other.Mantissa);
    }

    public static bool operator <(BigNum a, BigNum b) => a.CompareTo(b) < 0;
    public static bool operator >(BigNum a, BigNum b) => a.CompareTo(b) > 0;
    public static bool operator <=(BigNum a, BigNum b) => a.CompareTo(b) <= 0;
    public static bool operator >=(BigNum a, BigNum b) => a.CompareTo(b) >= 0;
    public static bool operator ==(BigNum a, BigNum b) => a.Equals(b);
    public static bool operator !=(BigNum a, BigNum b) => !a.Equals(b);
    public bool Equals(BigNum other) => Exponent == other.Exponent && Math.Abs(Mantissa - other.Mantissa) < 1e-12;
    public override bool Equals(object? obj) => obj is BigNum other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Math.Round(Mantissa, 10), Exponent);

    private static readonly string[] Suffixes =
    {
        "", "K", "M", "B", "T", "Qa", "Qi", "Sx", "Sp", "Oc", "No", "Dc", "UDc", "DDc", "TDc", "QaDc", "QiDc", "SxDc", "SpDc", "OcDc", "NoDc", "Vg"
    };

    /// <summary>Readable short form: 999, 1K, 12.5K, 3.21M … 1.23Vg, then letter pairs (aa, ab …), finally 1.23e1234.</summary>
    public string Format(CultureInfo? culture = null, int digits = 3)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (IsZero) return "0";
        if (IsNegative) return "-" + (-this).Format(culture, digits);
        if (Exponent < 3)
        {
            var value = ToDouble();
            return value < 10 && value % 1 != 0 ? value.ToString("0.#", culture) : Math.Floor(value).ToString("0", culture);
        }
        var group = Exponent / 3;
        var scaled = Mantissa * Math.Pow(10, Exponent % 3);
        var decimals = Math.Max(0, digits - 1 - (int)(Exponent % 3));
        var number = Math.Floor(scaled * Math.Pow(10, decimals)) / Math.Pow(10, decimals); // never round up past the shown value
        var text = number.ToString("0." + new string('#', Math.Max(1, decimals)), culture);
        if (group < Suffixes.Length) return text + Suffixes[group];
        var letters = group - Suffixes.Length;
        if (letters < 26 * 26) return text + (char)('a' + letters / 26) + (char)('a' + letters % 26);
        return Mantissa.ToString("0.##", culture) + "e" + Exponent.ToString(CultureInfo.InvariantCulture);
    }

    public override string ToString() => Format(CultureInfo.InvariantCulture);

    /// <summary>Exact storage form "mantissa|exponent".</summary>
    public string Serialize() => Mantissa.ToString("R", CultureInfo.InvariantCulture) + "|" + Exponent.ToString(CultureInfo.InvariantCulture);

    public static bool TryParse(string? text, out BigNum value)
    {
        value = Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('|');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var m) && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var e)
            && double.IsFinite(m))
        {
            value = new BigNum(m, e);
            return true;
        }
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain) && double.IsFinite(plain))
        {
            value = FromDouble(plain);
            return true;
        }
        return false;
    }
}

internal sealed class BigNumJsonConverter : JsonConverter<BigNum>
{
    public override BigNum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number) return BigNum.FromDouble(reader.GetDouble());
        return BigNum.TryParse(reader.GetString(), out var value) ? value : throw new JsonException("Invalid number.");
    }

    public override void Write(Utf8JsonWriter writer, BigNum value, JsonSerializerOptions options) => writer.WriteStringValue(value.Serialize());
}
