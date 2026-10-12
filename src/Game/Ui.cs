using Godot;
using RingDraft.Core;

namespace RingDraft;

internal static class Style
{
    public static readonly Color Panel = new(0.06f, 0.06f, 0.11f, 0.94f);
    public static readonly Color Dim = new(0f, 0f, 0f, 0.55f);
    public static readonly Color Accent = new(0.95f, 0.78f, 0.35f);
    public static readonly Color Muted = new(0.72f, 0.72f, 0.8f);

    public static StyleBoxFlat Box(Color bg, int radius = 8, int pad = 12) => new()
    {
        BgColor = bg,
        CornerRadiusTopLeft = radius, CornerRadiusTopRight = radius,
        CornerRadiusBottomLeft = radius, CornerRadiusBottomRight = radius,
        ContentMarginLeft = pad, ContentMarginRight = pad, ContentMarginTop = pad, ContentMarginBottom = pad,
    };

    public static Label Label(string text, int size = 18, Color? color = null)
    {
        var l = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        l.AddThemeFontSizeOverride("font_size", size);
        if (color is { } c) l.AddThemeColorOverride("font_color", c);
        return l;
    }

    public static Button Button(string text, Action onPress, string tooltip = "", bool disabled = false)
    {
        var b = new Button
        {
            Text = text,
            TooltipText = tooltip,
            Disabled = disabled,
            Alignment = HorizontalAlignment.Left,
            ClipText = true,
            CustomMinimumSize = new Vector2(0, 38),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        b.AddThemeFontSizeOverride("font_size", 17);
        b.Pressed += onPress;
        return b;
    }
}

/// <summary>
/// Sits over the owner's own selection screen while someone else picks. Swallows mouse input,
/// and offers "Choose myself" once the picker has taken too long.
/// </summary>
internal partial class Blocker : Control
{
    private Label _label = null!;
    private Button _takeOver = null!;

    public static Blocker Attach(Node host, string message, Action onTakeOver)
    {
        var b = new Blocker { Name = "RingDraftBlocker", MouseFilter = MouseFilterEnum.Stop, FocusMode = FocusModeEnum.All };
        b.Build(message, onTakeOver);
        host.AddChild(b);
        b.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        b.CallDeferred(Control.MethodName.GrabFocus);
        return b;
    }

    private void Build(string message, Action onTakeOver)
    {
        AddChild(new ColorRect { Color = Style.Dim, MouseFilter = MouseFilterEnum.Ignore, AnchorRight = 1, AnchorBottom = 1 });

        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore, AnchorRight = 1, AnchorBottom = 1 };
        AddChild(center);

        var panel = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        panel.AddThemeStyleboxOverride("panel", Style.Box(Style.Panel, 10, 18));
        center.AddChild(panel);

        var box = new VBoxContainer { CustomMinimumSize = new Vector2(420, 0) };
        box.AddThemeConstantOverride("separation", 12);
        panel.AddChild(box);

        _label = Style.Label(message, 22, Style.Accent);
        _label.HorizontalAlignment = HorizontalAlignment.Center;
        box.AddChild(_label);

        _takeOver = Style.Button("They're taking a while. Choose myself", onTakeOver);
        _takeOver.Alignment = HorizontalAlignment.Center;
        _takeOver.Visible = false;
        box.AddChild(_takeOver);
    }

    public void OfferTakeOver()
    {
        _takeOver.Visible = true;
        _takeOver.CallDeferred(Control.MethodName.GrabFocus);
    }

    /// <summary>Stop intercepting so a synthesized click can reach the screen underneath.</summary>
    public void Release()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
        QueueFree();
    }

    // Keyboard and controller input that reaches us while focused goes nowhere.
    public override void _GuiInput(InputEvent e)
    {
        if (e is not InputEventMouseMotion) AcceptEvent();
    }
}

/// <summary>The picker's window: what you're choosing for your neighbour.</summary>
internal partial class PickerPanel : CanvasLayer
{
    private PanelContainer _panel = null!;
    private Label _title = null!;
    private Label _subtitle = null!;
    private Label _notice = null!;
    private VBoxContainer _list = null!;
    private HBoxContainer _footer = null!;
    private Label _deckLabel = null!;
    private Button _deckToggle = null!;
    private Button _minimize = null!;
    private Label _timer = null!;
    private bool _minimized;
    private bool _deckOpen;
    private string _ownerName = "";
    private double _shownAt;
    private int _timeout;

    /// <summary>The envelope currently rendered. A refreshed shop state is a new object, which triggers a re-render.</summary>
    public Envelope? Shown { get; private set; }

    public override void _Ready()
    {
        Layer = 99;

        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        AddChild(root);
        root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        _panel = new PanelContainer { Visible = false };
        _panel.AddThemeStyleboxOverride("panel", Style.Box(Style.Panel, 10, 16));
        _panel.AnchorLeft = 1; _panel.AnchorRight = 1; _panel.AnchorTop = 0.5f; _panel.AnchorBottom = 0.5f;
        _panel.OffsetLeft = -560; _panel.OffsetRight = -24; _panel.OffsetTop = -330; _panel.OffsetBottom = 330;
        root.AddChild(_panel);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        _panel.AddChild(box);

        var header = new HBoxContainer();
        box.AddChild(header);
        _title = Style.Label("", 22, Style.Accent);
        _title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        header.AddChild(_title);
        _minimize = new Button { Text = "–", CustomMinimumSize = new Vector2(36, 32), TooltipText = "Minimize" };
        _minimize.Pressed += ToggleMinimize;
        header.AddChild(_minimize);

        _subtitle = Style.Label("", 16, Style.Muted);
        box.AddChild(_subtitle);
        _notice = Style.Label("", 16, Style.Accent);
        box.AddChild(_notice);

        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        box.AddChild(scroll);
        _list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _list.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_list);

        _deckToggle = Style.Button("", () => { _deckOpen = !_deckOpen; ApplyMinimized(); });
        box.AddChild(_deckToggle);
        _deckLabel = Style.Label("", 15, Style.Muted);
        _deckLabel.Visible = false;
        box.AddChild(_deckLabel);

        _footer = new HBoxContainer();
        _footer.AddThemeConstantOverride("separation", 8);
        box.AddChild(_footer);

        _timer = Style.Label("", 14, Style.Muted);
        box.AddChild(_timer);
    }

    public override void _Process(double delta)
    {
        if (Shown is null || _timeout <= 0) return;
        double left = _timeout - (Time.GetTicksMsec() / 1000.0 - _shownAt);
        _timer.Text = left > 0 ? $"They can take the choice back in {Math.Ceiling(left)}s" : "They can take the choice back now";
    }

    public void ShowOffer(OfferMsg o, string ownerName, Action<int> onPick)
    {
        Begin(o, o.TimeoutSeconds, ownerName);
        _title.Text = o.Kind switch
        {
            DecisionKind.SmithUpgrade => $"Upgrade a card for {ownerName}",
            DecisionKind.GeneratedCardChoice => $"Choose {ownerName}'s card (mid-combat)",
            _ => $"Pick {ownerName}'s card reward",
        };
        _subtitle.Text = $"{o.Gold} gold" + (o.Relics.Count > 0 ? $" · Relics: {string.Join(", ", o.Relics)}" : "");

        for (int i = 0; i < o.Options.Count; i++)
        {
            int index = i;
            var c = o.Options[i];
            _list.AddChild(Style.Button(c.Title, () => onPick(index), c.Description));
        }
        if (o.CanSkip)
            _footer.AddChild(Style.Button("Skip", () => onPick(-1)));

        SetDeck(o.Deck);
        Reveal();
    }

    public void ShowShop(ShopStateMsg s, string ownerName, Action<ShopActionType, int, int> onAction)
    {
        Begin(s, s.TimeoutSeconds, ownerName);
        _title.Text = $"Shopping for {ownerName}";
        _subtitle.Text = $"{s.Gold} gold to spend (theirs)";
        _notice.Text = s.Notice;

        var removal = s.Items.FirstOrDefault(i => i.Type == ShopItemType.CardRemoval);
        foreach (var item in s.Items.Where(i => i.Type != ShopItemType.CardRemoval))
        {
            var it = item;
            string tag = it.Type switch
            {
                ShopItemType.ColorlessCard => "Colorless",
                ShopItemType.Relic => "Relic",
                ShopItemType.Potion => "Potion",
                _ => "Card",
            };
            _list.AddChild(Style.Button($"[{tag}] {it.Title} · {it.Cost}g", () => onAction(ShopActionType.Buy, it.Index, -1), it.Description, !it.Affordable));
        }

        if (removal is not null)
            _footer.AddChild(Style.Button($"Remove a card · {removal.Cost}g", () => ShowRemoval(s, removal, onAction), "", !removal.Affordable));
        _footer.AddChild(Style.Button("Done shopping", () => onAction(ShopActionType.Done, -1, -1)));

        SetDeck(s.Deck);
        Reveal();
    }

    /// <summary>Called after a shop action is sent, until the refreshed state arrives.</summary>
    public void SetBusy(string text)
    {
        _notice.Text = text;
        foreach (var child in _list.GetChildren().Concat(_footer.GetChildren()))
            if (child is Button b) b.Disabled = true;
    }

    public void HideAll()
    {
        Shown = null;
        if (_panel is not null) _panel.Visible = false;
    }

    private void ShowRemoval(ShopStateMsg s, ShopItemView removal, Action<ShopActionType, int, int> onAction)
    {
        ClearLists();
        _notice.Text = $"Which card should {_ownerName} lose? ({removal.Cost}g)";
        for (int i = 0; i < s.Deck.Count; i++)
        {
            int deckIndex = i;
            var c = s.Deck[i];
            _list.AddChild(Style.Button(c.Title, () => onAction(ShopActionType.Remove, removal.Index, deckIndex), c.Description));
        }
        _footer.AddChild(Style.Button("Back", () => { Shown = null; })); // forces a re-render of the shop
    }

    private void Begin(Envelope e, int timeout, string ownerName)
    {
        Shown = e;
        _ownerName = ownerName;
        _timeout = timeout;
        _shownAt = Time.GetTicksMsec() / 1000.0;
        _notice.Text = "";
        _timer.Text = "";
        ClearLists();
    }

    private void ClearLists()
    {
        foreach (var parent in new Node[] { _list, _footer })
            foreach (var child in parent.GetChildren())
            {
                parent.RemoveChild(child);
                child.QueueFree();
            }
    }

    private void SetDeck(IReadOnlyList<CardView> deck)
    {
        var grouped = deck.GroupBy(c => c.Title).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key);
        _deckLabel.Text = string.Join(", ", grouped);
        _deckToggle.Text = deck.Count > 0 ? $"Their deck ({deck.Count} cards)" : "Their deck (not available)";
        _deckToggle.Disabled = deck.Count == 0;
    }

    private void Reveal()
    {
        _panel.Visible = true;
        ApplyMinimized();
        if (!_minimized && _list.GetChildCount() > 0 && _list.GetChild(0) is Control first)
            first.CallDeferred(Control.MethodName.GrabFocus);
    }

    private void ToggleMinimize()
    {
        _minimized = !_minimized;
        ApplyMinimized();
    }

    private void ApplyMinimized()
    {
        _minimize.Text = _minimized ? "+" : "–";
        foreach (var child in ((Node)_title.GetParent()).GetParent().GetChildren())
            if (child is Control c && c != _title.GetParent()) c.Visible = !_minimized;
        _deckLabel.Visible = !_minimized && _deckOpen;
        _panel.OffsetTop = _minimized ? -40 : -330;
        _panel.OffsetBottom = _minimized ? 40 : 330;
    }
}
