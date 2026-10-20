using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace ViennaAtomicClock;

internal sealed record CaffeineProduct(
    string Id,
    string Brand,
    string Name,
    string Category,
    int ServingMl,
    int CaffeineMg,
    string Store,
    string Icon = "⚡",
    bool IsEstimate = false)
{
    public string ServingLabel => $"{ServingMl} ml · {CaffeineMg} mg";
    public string SearchText => $"{Brand} {Name} {Category} {Store}".ToLowerInvariant();
    public string AccuracyLabel => IsEstimate ? "ca." : "laut Produktangabe";
    public ImageSource Image => DrinkArt.For(Brand, Name, Category);
}

internal sealed class CaffeineEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Brand { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string Category { get; set; } = "";
    public int ServingMl { get; set; }
    public int CaffeineMg { get; set; }
    public DateTimeOffset ConsumedAt { get; set; }
    public string Icon { get; set; } = "☕";

    public string TimeLabel => ConsumedAt.ToLocalTime().ToString("HH:mm");
    public string AmountLabel => $"{CaffeineMg} mg";
    public string DetailLabel => ServingMl > 0 ? $"{Brand} · {ServingMl} ml" : Brand;
    [JsonIgnore] public ImageSource Image => DrinkArt.For(Brand, ProductName, Category);
}

internal static class CaffeineCatalog
{
    public static IReadOnlyList<CaffeineProduct> Products { get; } = new List<CaffeineProduct>
    {
        // Österreichische Handelsmarken
        E("kong-classic", "KONG STRONG", "Energy Drink Classic", 250, 80, "Lidl"),
        E("kong-zero", "KONG STRONG", "Energy Drink Sugarfree", 250, 80, "Lidl"),
        E("kong-wild", "KONG STRONG", "Wild Power", 250, 80, "Lidl"),
        E("kong-cola", "KONG STRONG", "Cola Energy", 250, 80, "Lidl"),
        E("flying-classic", "FLYING POWER", "Energy Drink Regular", 250, 80, "HOFER"),
        E("flying-zero", "FLYING POWER", "Energy Drink Sugarfree", 250, 80, "HOFER"),
        E("sbudget-classic", "S-BUDGET", "Energy Drink Classic", 250, 80, "SPAR / INTERSPAR"),
        E("sbudget-zero", "S-BUDGET", "Energy Drink Sugarfree", 250, 80, "SPAR / INTERSPAR"),
        E("clever-classic", "CLEVER", "Energy Drink Classic", 250, 80, "BILLA / PENNY"),
        E("clever-zero", "CLEVER", "Energy Drink Sugarfree", 250, 80, "BILLA / PENNY"),
        E("clever-kiwi", "CLEVER", "Energy Drink Kiwi", 250, 80, "BILLA / PENNY"),

        // Energy-Drink-Marken, die im österreichischen Handel verbreitet sind
        E("redbull-250", "RED BULL", "Energy Drink", 250, 80, "Österreichischer Handel"),
        E("redbull-sugarfree", "RED BULL", "Sugarfree", 250, 80, "Österreichischer Handel"),
        E("redbull-zero", "RED BULL", "Zero", 250, 80, "Österreichischer Handel"),
        E("redbull-edition", "RED BULL", "Edition", 250, 80, "Österreichischer Handel"),
        E("redbull-355", "RED BULL", "Energy Drink groß", 355, 114, "Österreichischer Handel"),
        E("redbull-473", "RED BULL", "Energy Drink XL", 473, 151, "Österreichischer Handel"),
        E("monster-original", "MONSTER", "Original", 500, 160, "Österreichischer Handel"),
        E("monster-zero", "MONSTER", "Zero Sugar", 500, 160, "Österreichischer Handel"),
        E("monster-ultra", "MONSTER", "Ultra White", 500, 150, "Österreichischer Handel"),
        E("monster-mango", "MONSTER", "Mango Loco", 500, 160, "Österreichischer Handel"),
        E("monster-doctor", "MONSTER", "The Doctor", 500, 160, "Österreichischer Handel"),
        E("monster-pipeline", "MONSTER", "Pipeline Punch", 500, 160, "Österreichischer Handel"),
        E("rockstar-original", "ROCKSTAR", "Original", 500, 160, "Österreichischer Handel"),
        E("rockstar-zero", "ROCKSTAR", "Zero Sugar", 500, 160, "Österreichischer Handel"),
        E("effect-330", "EFFECT", "Energy Drink", 330, 106, "Österreichischer Handel"),
        E("effect-zero", "EFFECT", "Zero Sugar", 330, 106, "Österreichischer Handel"),
        E("hell-classic", "HELL", "Classic", 250, 80, "BILLA / Tankstelle"),
        E("hell-zero", "HELL", "Zero", 250, 80, "Österreichischer Handel"),
        E("28black", "28 BLACK", "Açaí", 250, 80, "Österreichischer Handel"),
        E("powerhorse", "POWER HORSE", "Energy Drink", 250, 80, "Österreichischer Handel"),
        E("burn", "BURN", "Original", 250, 80, "Österreichischer Handel"),

        // Kaffeezubereitungen – Werte schwanken je nach Bohne und Zubereitung.
        K("espresso", "KAFFEE", "Espresso", 60, 80),
        K("double-espresso", "KAFFEE", "Doppelter Espresso", 120, 160),
        K("verlaengerter", "KAFFEE", "Verlängerter", 200, 90),
        K("filter", "KAFFEE", "Filterkaffee", 200, 90),
        K("melange", "KAFFEE", "Wiener Melange", 200, 80),
        K("cappuccino", "KAFFEE", "Cappuccino", 180, 80),
        K("latte", "KAFFEE", "Caffè Latte", 250, 80),
        K("flatwhite", "KAFFEE", "Flat White", 180, 130),
        K("coldbrew", "KAFFEE", "Cold Brew", 250, 150),
        K("instant", "KAFFEE", "Instantkaffee", 200, 65),
        K("decaf", "KAFFEE", "Entkoffeinierter Kaffee", 200, 3),

        // Trinkkaffee / gekühlte Produkte
        I("emmi-latte", "EMMI", "Caffè Latte", 230, 80, "BILLA / SPAR / HOFER"),
        I("emmi-macchiato", "EMMI", "Caffè Latte Macchiato", 230, 80, "BILLA / SPAR / HOFER"),
        I("emmi-double", "EMMI", "Caffè Latte Double Zero", 230, 120, "BILLA / SPAR"),
        I("nescafe-xpress", "NESCAFÉ", "Xpress", 250, 75, "Österreichischer Handel"),
        I("starbucks-latte", "STARBUCKS", "Caffè Latte", 220, 90, "BILLA / SPAR"),
        I("starbucks-doubleshot", "STARBUCKS", "Doubleshot Espresso", 200, 120, "BILLA / SPAR"),

        // Weitere typische Koffeinquellen
        new("black-tea", "TEE", "Schwarztee", "Tee", 220, 50, "Österreichischer Handel", "◌", true),
        new("green-tea", "TEE", "Grüntee", "Tee", 220, 35, "Österreichischer Handel", "◌", true),
        new("cola", "COLA", "Cola", "Softdrink", 355, 40, "Österreichischer Handel", "●", true),
        new("mate", "MATE", "Mate-Limonade", "Softdrink", 330, 66, "Österreichischer Handel", "●", true)
    };

    private static CaffeineProduct E(string id, string brand, string name, int ml, int mg, string store) =>
        new(id, brand, name, "Energy", ml, mg, store, "⚡");

    private static CaffeineProduct K(string id, string brand, string name, int ml, int mg) =>
        new(id, brand, name, "Kaffee", ml, mg, "Café / zuhause", "☕", true);

    private static CaffeineProduct I(string id, string brand, string name, int ml, int mg, string store) =>
        new(id, brand, name, "Eiskaffee", ml, mg, store, "▤", true);
}

internal sealed class CaffeineStore
{
    public List<CaffeineEntry> Entries { get; set; } = new();

    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ViennaAtomicClock",
        "caffeine-log.json");

    public static CaffeineStore Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return new CaffeineStore();
            var result = JsonSerializer.Deserialize<CaffeineStore>(File.ReadAllText(StorePath)) ?? new CaffeineStore();
            // Keep one year of data so the file never grows without bounds.
            result.Entries.RemoveAll(e => e.ConsumedAt < DateTimeOffset.Now.AddYears(-1));
            return result;
        }
        catch
        {
            return new CaffeineStore();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Tracking remains usable for the current session in a read-only profile.
        }
    }
}
