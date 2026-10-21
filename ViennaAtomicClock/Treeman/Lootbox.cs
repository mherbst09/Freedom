using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace ViennaAtomicClock.Treeman;

/// <summary>What one box gave: the skin, whether it was a duplicate (refunded) and the reel for the animation.</summary>
public sealed record LootResult(SkinDefinition Skin, bool Duplicate, int Refund, IReadOnlyList<SkinDefinition> Reel, int WinnerIndex);

/// <summary>
/// Box rules without any UI: price, rarity roll with the configured weights, duplicate refund and saving.
/// The reel is filled with random skins and the won skin sits at <see cref="WinnerIndex"/>.
/// </summary>
public sealed class LootboxSystem
{
    public const int ReelLength = 64;
    public const int WinnerIndex = 56;

    private readonly LootSettings _settings;
    private readonly ISaveSystem _save;
    private readonly Random _random;

    public LootboxSystem(LootSettings settings, ISaveSystem save, Random? random = null)
    {
        _settings = settings;
        _save = save;
        _random = random ?? new Random();
    }

    public int Price => _settings.BoxPrice;
    public bool CanOpen => _save.Data.Coins >= Price;
    public long MissingCoins => Math.Max(0, Price - _save.Data.Coins);

    /// <summary>Chance of a rarity in percent.</summary>
    public double ChanceOf(SkinRarity rarity) => _settings.RarityWeights[(int)rarity] / _settings.RarityWeights.Sum() * 100;

    public SkinRarity RollRarity()
    {
        var weights = _settings.RarityWeights;
        var roll = _random.NextDouble() * weights.Sum();
        for (var i = 0; i < weights.Length; i++)
        {
            roll -= weights[i];
            if (roll < 0) return (SkinRarity)i;
        }
        return SkinRarity.Common;
    }

    public SkinDefinition RollSkin()
    {
        var pool = SkinCatalog.OfRarity(RollRarity()).ToList();
        return pool[_random.Next(pool.Count)];
    }

    /// <summary>Pays the price and draws a skin. Returns null without enough coins.</summary>
    public LootResult? Open()
    {
        if (!CanOpen) return null;
        _save.Data.Coins -= Price;
        var skin = RollSkin();
        var duplicate = _save.Data.OwnedSkins.Contains(skin.Id);
        var refund = duplicate ? _settings.DuplicateRefund[(int)skin.Rarity] : 0;
        if (duplicate) _save.Data.Coins += refund;
        else _save.Data.OwnedSkins.Add(skin.Id);
        _save.Save();
        var reel = new SkinDefinition[ReelLength];
        for (var i = 0; i < reel.Length; i++) reel[i] = i == WinnerIndex ? skin : RollSkin();
        return new LootResult(skin, duplicate, refund, reel, WinnerIndex);
    }

    public void Equip(string id)
    {
        if (!_save.Data.OwnedSkins.Contains(id)) return;
        _save.Data.EquippedSkin = id;
        _save.Save();
    }
}

/// <summary>Builds the shop and collection panels of Treeman and runs the box reel animation.</summary>
internal sealed class LootboxPanel
{
    private const double CardWidth = 132;
    private const double CardGap = 8;
    private const double Step = CardWidth + CardGap;
    private const double ViewWidth = 760;

    private readonly LootboxSystem _boxes;
    private readonly ISaveSystem _save;
    private readonly LootSettings _settings;
    private readonly Action<SoundEffect> _sound;
    private readonly Action _changed;
    private readonly Action _close;

    private readonly TranslateTransform _reelShift = new();
    private StackPanel? _reel;
    private StackPanel? _result;
    private TextBlock? _coins;
    private GameButton? _openButton;
    private LootResult? _spin;
    private double _spinTime;
    private double _target;
    private double _flash;
    private int _lastTick;
    private double _sinceTick;

    public LootboxPanel(LootboxSystem boxes, ISaveSystem save, LootSettings settings, Action<SoundEffect> sound, Action changed, Action close)
    {
        _boxes = boxes;
        _save = save;
        _settings = settings;
        _sound = sound;
        _changed = changed;
        _close = close;
    }

    public bool IsSpinning => _spin is not null && _spinTime < _settings.SpinSeconds;
    public bool IsAnimating => IsSpinning || _flash > 0;
    /// <summary>The last finished box (tests and the result line).</summary>
    public LootResult? LastResult { get; private set; }

    // ───────────────────────── shop ─────────────────────────

    public FrameworkElement BuildShop()
    {
        _spin = null;
        _flash = 0;
        var stack = new StackPanel { Width = ViewWidth };
        _coins = new TextBlock { FontFamily = GameUi.Font, FontSize = 18, Foreground = GameUi.Cream, VerticalAlignment = VerticalAlignment.Center };
        var coins = GameUi.Badge(new GameIcon(GameIconKind.Coin), _coins);
        coins.HorizontalAlignment = HorizontalAlignment.Center;
        coins.Margin = new Thickness(0, 0, 0, 14);
        stack.Children.Add(coins);

        // Reel with the gold marker in the middle, framed like a dark slot in the board.
        var window = new Grid { Height = 190, ClipToBounds = true };
        window.Children.Add(new Border { CornerRadius = new CornerRadius(12), Background = Freeze(new SolidColorBrush(Color.FromRgb(0x14, 0x16, 0x1C))), BorderBrush = GameUi.Ink, BorderThickness = new Thickness(4) });
        // A Canvas never clips the long strip to the window width; the window itself clips.
        _reel = new StackPanel { Orientation = Orientation.Horizontal, RenderTransform = _reelShift };
        Canvas.SetTop(_reel, 10);
        window.Children.Add(new Canvas { Children = { _reel } });
        var fade = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(230, 0x14, 0x16, 0x1C), 0), new(Color.FromArgb(0, 0x14, 0x16, 0x1C), 0.18),
            new(Color.FromArgb(0, 0x14, 0x16, 0x1C), 0.82), new(Color.FromArgb(230, 0x14, 0x16, 0x1C), 1)
        }, new Point(0, 0), new Point(1, 0));
        window.Children.Add(new Border { Background = Freeze(fade), IsHitTestVisible = false });
        window.Children.Add(new Border { Width = 3, Background = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xC2, 0x32))), HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false });
        window.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 0,0 L 18,0 L 9,12 Z"), Fill = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xC2, 0x32))),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false
        });
        window.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 0,12 L 18,12 L 9,0 Z"), Fill = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xC2, 0x32))),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false
        });
        stack.Children.Add(window);
        ShowIdleReel();

        _result = new StackPanel { Margin = new Thickness(0, 12, 0, 0), MinHeight = 64 };
        stack.Children.Add(_result);
        _result.Children.Add(Odds());

        _openButton = GameUi.Button("", GameButtonColor.Orange, OpenBox, _sound, GameIconKind.Box, 64);
        _openButton.Margin = new Thickness(0, 12, 0, 10);
        stack.Children.Add(_openButton);
        stack.Children.Add(GameUi.Button(Loc.T("tm.back"), GameButtonColor.Wood, _close, _sound));
        UpdateShopTexts();
        return stack;
    }

    private void UpdateShopTexts()
    {
        if (_coins is null || _openButton is null) return;
        _coins.Text = _save.Data.Coins.ToString("N0", Loc.Culture);
        AutomationProperties.SetName(_coins, Loc.F("tm.coins", _save.Data.Coins));
        var can = _boxes.CanOpen && !IsSpinning;
        _openButton.IsEnabled = can;
        _openButton.Text = _boxes.CanOpen || IsSpinning
            ? Loc.F("tm.box.open", _boxes.Price.ToString("N0", Loc.Culture))
            : Loc.F("tm.box.notEnough", _boxes.MissingCoins.ToString("N0", Loc.Culture));
    }

    private UIElement Odds()
    {
        var row = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var rarity in Enum.GetValues<SkinRarity>())
        {
            var dot = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = Freeze(new SolidColorBrush(SkinCatalog.RarityColor(rarity))), Margin = new Thickness(10, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(dot);
            var label = GameUi.Label($"{Loc.T("tm.rarity." + rarity.ToString().ToLowerInvariant())} {_boxes.ChanceOf(rarity).ToString("0.#", Loc.Culture)} %", 13, GameUi.CreamDim);
            row.Children.Add(label);
        }
        return GameUi.Inset(row);
    }

    private void ShowIdleReel()
    {
        if (_reel is null) return;
        _reel.Children.Clear();
        var random = new Random(4);
        // A calm preview of what the box contains.
        foreach (var skin in SkinCatalog.All.OrderBy(_ => random.Next()).Take(8)) _reel.Children.Add(Card(skin, locked: false));
        _reelShift.X = (ViewWidth - 8 * Step) / 2;
    }

    private void OpenBox()
    {
        if (IsSpinning || _reel is null || _result is null) return;
        var result = _boxes.Open();
        if (result is null)
        {
            UpdateShopTexts();
            return;
        }
        _spin = result;
        _spinTime = 0;
        _lastTick = 0;
        _reel.Children.Clear();
        foreach (var skin in result.Reel) _reel.Children.Add(Card(skin, locked: false));
        // Stop somewhere inside the winning card, like the real thing – not always dead centre.
        var jitter = (Random.Shared.NextDouble() - 0.5) * (CardWidth - 24);
        _target = result.WinnerIndex * Step + CardWidth / 2 + jitter - ViewWidth / 2;
        _reelShift.X = 0;
        _result.Children.Clear();
        _result.Children.Add(GameUi.Label(Loc.T("tm.box.skipHint"), 13, GameUi.CreamDim));
        UpdateShopTexts();
        _changed();
    }

    /// <summary>Space/Enter during the spin jumps to the end.</summary>
    public void Skip()
    {
        if (IsSpinning) _spinTime = _settings.SpinSeconds - 0.0001;
    }

    public void Update(double seconds)
    {
        if (_spin is null) return;
        if (_spinTime < _settings.SpinSeconds)
        {
            _spinTime = Math.Min(_settings.SpinSeconds, _spinTime + seconds);
            var t = _spinTime / _settings.SpinSeconds;
            var eased = 1 - Math.Pow(1 - t, 4); // fast start, long slow ending
            var offset = _target * eased;
            _reelShift.X = -offset;
            var passed = (int)((offset + ViewWidth / 2) / Step);
            _sinceTick += seconds;
            if (passed != _lastTick)
            {
                _lastTick = passed;
                if (_sinceTick >= 0.035) _sound(SoundEffect.CaseTick); // at full speed one click per frame is enough
                _sinceTick = 0;
            }
            if (_spinTime >= _settings.SpinSeconds) Reveal();
        }
        if (_flash > 0 && _reel is not null)
        {
            _flash = Math.Max(0, _flash - seconds);
            if (_reel.Children[_spin.WinnerIndex] is Border winner) winner.Opacity = 0.75 + 0.25 * Math.Cos(_flash * 18);
        }
    }

    private void Reveal()
    {
        if (_spin is null || _result is null || _reel is null) return;
        var won = _spin;
        LastResult = won;
        _flash = 1.2;
        _sound(won.Skin.Rarity >= SkinRarity.Epic ? SoundEffect.Highscore : SoundEffect.CaseReveal);
        // Dim the other cards, lift the winner.
        for (var i = 0; i < _reel.Children.Count; i++)
            if (_reel.Children[i] is FrameworkElement card) card.Opacity = i == won.WinnerIndex ? 1 : 0.35;
        if (_reel.Children[won.WinnerIndex] is Border winner)
        {
            winner.Effect = new DropShadowEffect { Color = SkinCatalog.RarityColor(won.Skin.Rarity), BlurRadius = 26, ShadowDepth = 0, Opacity = 1 };
            winner.BorderBrush = Freeze(new SolidColorBrush(SkinCatalog.RarityColor(won.Skin.Rarity)));
            winner.BorderThickness = new Thickness(3);
        }

        _result.Children.Clear();
        _result.Children.Add(new GameText(won.Skin.Name, 30, Freeze(new SolidColorBrush(SkinCatalog.RarityColor(won.Skin.Rarity)))) { HorizontalAlignment = HorizontalAlignment.Center });
        var status = won.Duplicate ? Loc.F("tm.box.duplicate", won.Refund.ToString("N0", Loc.Culture)) : "★ " + Loc.T("tm.box.new");
        _result.Children.Add(GameUi.Label($"{Loc.T("tm.rarity." + won.Skin.Rarity.ToString().ToLowerInvariant())} · {status}", 15, GameUi.Cream, bold: true));
        var equipped = _save.Data.EquippedSkin == won.Skin.Id;
        GameButton? equip = null;
        equip = GameUi.Button(Loc.T(equipped ? "tm.box.equipped" : "tm.box.equip"), GameButtonColor.Blue, () =>
        {
            _boxes.Equip(won.Skin.Id);
            _changed();
            equip!.Text = Loc.T("tm.box.equipped");
            equip.IsEnabled = false;
        }, _sound, GameIconKind.Shirt, 50);
        equip.IsEnabled = !equipped;
        equip.Margin = new Thickness(0, 10, 0, 0);
        _result.Children.Add(equip);
        Motion.PopIn(_result);
        UpdateShopTexts();
        if (_boxes.CanOpen && _openButton is not null) _openButton.Text = Loc.F("tm.box.again", _boxes.Price.ToString("N0", Loc.Culture));
        _changed();
    }

    // ───────────────────────── collection ─────────────────────────

    public FrameworkElement BuildCollection()
    {
        var stack = new StackPanel { Width = ViewWidth };
        var owned = _save.Data.OwnedSkins.Count;
        stack.Children.Add(GameUi.Label(Loc.F("tm.skins.count", owned, SkinCatalog.All.Count) + " · " + Loc.T("tm.skins.hint"), 14, GameUi.CreamDim));
        var grid = new WrapPanel { Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var skin in SkinCatalog.All)
        {
            var has = _save.Data.OwnedSkins.Contains(skin.Id);
            var card = Card(skin, locked: !has, small: true);
            if (has)
            {
                var id = skin.Id;
                var button = new Button { Content = card, Padding = new Thickness(0), Margin = new Thickness(4), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
                button.Template = CardButtonTemplate;
                AutomationProperties.SetName(button, skin.Name);
                button.Click += (_, _) =>
                {
                    _sound(SoundEffect.MenuSelect);
                    _boxes.Equip(id);
                    _changed();
                    if (button.Parent is WrapPanel panel)
                        foreach (var child in panel.Children.OfType<Button>())
                            if (child.Content is Border b) MarkEquipped(b, ReferenceEquals(child, button));
                };
                MarkEquipped(card, _save.Data.EquippedSkin == id);
                grid.Children.Add(button);
            }
            else
            {
                card.Margin = new Thickness(4);
                grid.Children.Add(card);
            }
        }
        var scroller = GameUi.Inset(new ScrollViewer { Content = grid, MaxHeight = 430, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false }, new Thickness(6));
        scroller.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(scroller);
        var back = GameUi.Button(Loc.T("tm.back"), GameButtonColor.Green, _close, _sound);
        back.Margin = new Thickness(0, 14, 0, 0);
        stack.Children.Add(back);
        return stack;
    }

    private static readonly ControlTemplate CardButtonTemplate = MakeCardTemplate();

    private static ControlTemplate MakeCardTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        template.VisualTree = presenter;
        template.Seal();
        return template;
    }

    private static void MarkEquipped(Border card, bool equipped)
    {
        card.BorderBrush = equipped ? Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xC2, 0x32))) : Brushes.Transparent;
        card.BorderThickness = new Thickness(equipped ? 3 : 0);
    }

    // ───────────────────────── shared ─────────────────────────

    private static Border Card(SkinDefinition skin, bool locked, bool small = false)
    {
        var rarity = SkinCatalog.RarityColor(skin.Rarity);
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
        var preview = new SkinPreview(skin, locked) { Margin = new Thickness(8, 8, 8, 2) };
        grid.Children.Add(preview);
        var name = new TextBlock
        {
            Text = locked ? "???" : skin.Name, FontSize = small ? 11.5 : 12.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 4, 6)
        };
        Grid.SetRow(name, 1);
        grid.Children.Add(name);
        var bar = new Border { Background = Freeze(new SolidColorBrush(rarity)) };
        Grid.SetRow(bar, 2);
        grid.Children.Add(bar);
        var background = new LinearGradientBrush(Color.FromRgb(0x2A, 0x2E, 0x38), Color.FromArgb(255, (byte)(0x22 + rarity.R / 6), (byte)(0x24 + rarity.G / 6), (byte)(0x2C + rarity.B / 6)), 90);
        return new Border
        {
            Width = small ? 112 : CardWidth, Height = small ? 150 : 170, Margin = small ? new Thickness(0) : new Thickness(0, 0, CardGap, 0),
            CornerRadius = new CornerRadius(6), Background = Freeze(background), Child = grid, ClipToBounds = true
        };
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
