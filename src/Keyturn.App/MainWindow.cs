using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Keyturn.App;

/// <summary>
/// Prompt mode: only secrets that need a new value, one Save for all of them. It is live: it adds secrets that expire
/// while it is open, drops ones rotated elsewhere (a tool, the CLI), keeps what you've typed, and closes itself when
/// nothing is left.
/// Manage mode: every secret with its schedule, plus create/remove, each card saving on its own.
/// </summary>
public sealed class MainWindow : Window
{
    readonly SecretVault _vault;
    readonly bool _manage;
    readonly string? _only;
    readonly SnoozeStore? _snooze;
    readonly Dictionary<string, Border> _promptCards = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, TextBlock> _pillTexts = new(StringComparer.OrdinalIgnoreCase);
    bool _closing;
    readonly StackPanel _cards = new() { Spacing = 12 };
    readonly TextBlock _subtitle = new() { Classes = { "muted" } };
    readonly TextBlock _error = new() { Classes = { "error" }, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    readonly Dictionary<string, TextBox> _values = new();
    readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    bool _expandedInitialised;
    Border? _newPanel;
    Button? _saveAll;

    public MainWindow(SecretVault vault, bool manage, string? only = null, bool respectSnooze = false)
    {
        _vault = vault;
        _manage = manage;
        _only = only;
        _snooze = respectSnooze ? new SnoozeStore(vault.ConfigDirectory) : null;

        Title = manage ? "Keyturn" : "Keyturn: secrets due";
        Width = manage ? 600 : 480;
        MaxHeight = 780;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = !manage; // a scheduled prompt should be seen, not buried
        CanResize = false;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Closed += (_, _) => _closing = true;

        var root = new DockPanel { Margin = new Thickness(24) };

        var header = BuildHeader();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        if (!manage)
        {
            var footer = BuildPromptFooter();
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);
        }

        DockPanel.SetDock(_error, Dock.Bottom);
        _error.Margin = new Thickness(0, 12, 0, 0);
        root.Children.Add(_error);

        root.Children.Add(new ScrollViewer
        {
            Content = _cards,
            Margin = new Thickness(0, 20, 0, 0),
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 0, 2),
        });

        Content = root;
        Rebuild();

        if (!manage)
        {
            // Best effort to come to the front; the OS may still keep focus on whatever you were typing in.
            Opened += (_, _) => Activate();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (_, _) => ReconcilePrompt();
            Opened += (_, _) => timer.Start();
            Closed += (_, _) => timer.Stop();
        }
    }

    // ---- chrome ----------------------------------------------------------------------------

    Control BuildHeader()
    {
        var titleBlock = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Text = _manage ? "Keyturn" : "Refresh your secrets", Classes = { "title" } }, _subtitle },
        };
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Children = { AppLogo.Create(48), titleBlock } };
        if (!_manage) return left;

        var add = new Button
        {
            Classes = { "btn", "primary", "sm" },
            Content = Row(Icons.Create(Icons.Plus, 15, "white"), new TextBlock { Text = "New secret", VerticalAlignment = VerticalAlignment.Center }),
            VerticalAlignment = VerticalAlignment.Center,
        };
        add.Click += (_, _) => ToggleNewPanel();

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(left);
        Grid.SetColumn(add, 1);
        grid.Children.Add(add);
        return grid;
    }

    Control BuildPromptFooter()
    {
        _saveAll = new Button { Classes = { "btn", "primary" }, MinWidth = 96, Content = "Save", IsDefault = true };
        _saveAll.Click += (_, _) => SavePrompt();
        var later = new Button { Classes = { "btn", "ghost" }, Content = "Later" };
        later.Click += (_, _) => Close();

        var note = Row(Icons.Create(Icons.Lock, 14), new TextBlock { Text = "Stored in your system keychain", Classes = { "muted", "small" }, VerticalAlignment = VerticalAlignment.Center });
        note.VerticalAlignment = VerticalAlignment.Center;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { later, _saveAll } };
        var grid = new Grid { Margin = new Thickness(0, 20, 0, 0), ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(note);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);
        return grid;
    }

    // ---- content ---------------------------------------------------------------------------

    /// <summary>What the prompt should show now: due, not dismissed, and (for `--refresh`) the one secret asked about.</summary>
    IReadOnlyList<SecretStatus> PromptSecrets(bool consultStore) =>
        (consultStore ? _vault.Due() : _vault.Expired())
            .Where(s => _only is null || s.Definition.Name.Equals(_only, StringComparison.OrdinalIgnoreCase))
            .Where(s => _snooze is null || !_snooze.IsSnoozed(s.Definition))
            .ToList();

    void RebuildPrompt()
    {
        _cards.Children.Clear();
        _values.Clear();
        _promptCards.Clear();
        _pillTexts.Clear();
        _error.IsVisible = false;

        var shown = PromptSecrets(consultStore: true);
        UpdatePromptSubtitle(shown.Count);
        if (shown.Count == 0) _cards.Children.Add(EmptyState());
        foreach (var s in shown) AddPromptCard(s);
        Dispatcher.UIThread.Post(() => _values.Values.FirstOrDefault()?.Focus());
    }

    void UpdatePromptSubtitle(int count) => _subtitle.Text = $"{Humanize.Count(count, "secret needs", "secrets need")} a new value";

    void AddPromptCard(SecretStatus s)
    {
        var card = PromptCard(s);
        _promptCards[s.Definition.Name] = card;
        _cards.Children.Add(card);
    }

    /// <summary>The live part: bring the cards in line with what is due right now, without disturbing what you have typed.</summary>
    void ReconcilePrompt()
    {
        if (_closing) return;
        IReadOnlyList<SecretStatus> want;
        try { want = PromptSecrets(consultStore: false); }
        catch (Exception) { return; } // a half-written file or a transient error: try again on the next tick

        var wanted = want.Select(s => s.Definition.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _promptCards.Keys.Where(name => !wanted.Contains(name)).ToList())
        {
            _cards.Children.Remove(_promptCards[gone]); // rotated by a tool or the CLI, or snoozed
            _promptCards.Remove(gone);
            _values.Remove(gone);
            _pillTexts.Remove(gone);
        }

        var added = false;
        foreach (var s in want.Where(s => !_promptCards.ContainsKey(s.Definition.Name)))
        {
            AddPromptCard(s);
            added = true;
        }
        foreach (var s in want)
            if (_pillTexts.TryGetValue(s.Definition.Name, out var text)) text.Text = Humanize.Status(s, DateTimeOffset.Now);

        UpdatePromptSubtitle(want.Count);
        if (want.Count == 0)
        {
            _closing = true;
            Close(); // nothing left to ask for
        }
        else if (added)
        {
            Activate();
        }
    }

    void Rebuild()
    {
        if (!_manage) { RebuildPrompt(); return; }

        _cards.Children.Clear();
        _values.Clear();
        _error.IsVisible = false;

        var all = _vault.Statuses();
        var shown = all
            .Where(s => _manage || s.NeedsAttention)
            .Where(s => _manage || _only is null || s.Definition.Name.Equals(_only, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var due = all.Count(s => s.NeedsAttention);

        _subtitle.Text = _manage
            ? (all.Count == 0 ? "Rotating secrets in your system keychain" : $"{Humanize.Count(all.Count, "secret", "secrets")} · {(due == 0 ? "all up to date" : $"{due} need{(due == 1 ? "s" : "")} attention")}")
            : $"{Humanize.Count(shown.Count, "secret needs", "secrets need")} a new value";

        if (_manage && _newPanel is not null) _cards.Children.Add(_newPanel);
        if (shown.Count == 0 && _newPanel is not { IsVisible: true }) _cards.Children.Add(EmptyState());
        if (_manage && !_expandedInitialised)
        {
            // Open the first thing that needs attention so the window never starts as a wall of collapsed rows.
            _expandedInitialised = true;
            if (shown.FirstOrDefault(x => x.NeedsAttention) is { } first) _expanded.Add(first.Definition.Name);
        }
        foreach (var s in shown) _cards.Children.Add(_manage ? ManageCard(s) : PromptCard(s));

        Dispatcher.UIThread.Post(() => _values.Values.FirstOrDefault()?.Focus());
    }

    Control EmptyState()
    {
        var icon = new Border
        {
            Width = 52, Height = 52, CornerRadius = new CornerRadius(14), HorizontalAlignment = HorizontalAlignment.Center,
            Classes = { "seg-track" }, Child = Icons.Create(Icons.Shield, 24, "text"),
        };
        icon.Child!.HorizontalAlignment = HorizontalAlignment.Center;
        icon.Child.VerticalAlignment = VerticalAlignment.Center;
        return new StackPanel
        {
            Spacing = 6, Margin = new Thickness(0, 28),
            Children =
            {
                icon,
                new TextBlock { Text = "No secrets yet", Classes = { "name" }, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) },
                new TextBlock { Text = "Add one and Keyturn will ask for a new value whenever it expires.", Classes = { "muted" }, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap },
            },
        };
    }

    Border PromptCard(SecretStatus s)
    {
        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(NameLine(s));
        if (!string.IsNullOrWhiteSpace(s.Definition.Description))
            stack.Children.Add(new TextBlock { Text = s.Definition.Description, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, -6, 0, 0) });
        stack.Children.Add(ValueField(s, "Enter new value"));
        stack.Children.Add(Row(Icons.Create(Icons.Clock, 14), new TextBlock { Text = Humanize.Schedule(s.Definition.ParsedSchedule), Classes = { "muted", "small" }, VerticalAlignment = VerticalAlignment.Center }));
        return new Border { Classes = { "card" }, Child = stack };
    }

    Border ManageCard(SecretStatus s)
    {
        var name = s.Definition.Name;
        var schedule = new SchedulePicker();
        schedule.Load(s.Definition.Schedule);
        var value = ValueField(s, s.State == SecretState.Fresh ? "Enter a new value to rotate early" : "Enter new value");

        var save = new Button { Classes = { "btn", "primary", "sm" }, Content = "Save changes", MinWidth = 110 };
        save.Click += async (_, _) =>
        {
            if (!Try(() =>
            {
                if (schedule.Value != s.Definition.Schedule) _vault.Define(name, schedule.Value);
                if (_values[name].Text is { Length: > 0 } v) _vault.Set(name, v);
            })) return;
            await Flash(save, "Saved");
            Rebuild();
        };

        var remove = new Button { Classes = { "btn", "danger", "sm" } };
        void ResetRemove() { remove.Content = Row(Icons.Create(Icons.Trash, 15, "danger"), new TextBlock { Text = "Remove", VerticalAlignment = VerticalAlignment.Center }); }
        ResetRemove();
        remove.Click += async (_, _) =>
        {
            // Two-step confirm without a modal: the button asks once, then reverts if ignored.
            if (remove.Tag is not true)
            {
                remove.Tag = true;
                remove.Content = "Click again to confirm";
                await Task.Delay(3000);
                if (remove.Tag is true) { remove.Tag = null; ResetRemove(); }
                return;
            }
            if (Try(() => _vault.Remove(name))) Rebuild();
        };

        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        actions.Children.Add(remove);
        Grid.SetColumn(save, 2);
        actions.Children.Add(save);

        var details = new StackPanel
        {
            Spacing = 16,
            Margin = new Thickness(0, 16, 0, 0),
            IsVisible = _expanded.Contains(name),
            Children =
            {
                Field("Value", value),
                Field("Rotation", schedule),
                new Border { Classes = { "divider" } },
                actions,
            },
        };

        var chevron = new Border { Child = Icons.Create(_expanded.Contains(name) ? Icons.ChevronUp : Icons.ChevronDown, 18), VerticalAlignment = VerticalAlignment.Center };
        var summary = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = name, Classes = { "name" } },
                Row(Icons.Create(Icons.Clock, 13),
                    new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(s.Definition.Description)
                            ? Humanize.Schedule(s.Definition.ParsedSchedule)
                            : $"{Humanize.Schedule(s.Definition.ParsedSchedule)} · {s.Definition.Description}",
                        Classes = { "muted", "small" },
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = 330,
                    }),
            },
        };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
        header.Children.Add(summary);
        var pill = StatusPill(s);
        pill.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(pill, 1);
        header.Children.Add(pill);
        Grid.SetColumn(chevron, 2);
        header.Children.Add(chevron);
        header.PointerPressed += (_, _) =>
        {
            var open = !details.IsVisible;
            details.IsVisible = open;
            chevron.Child = Icons.Create(open ? Icons.ChevronUp : Icons.ChevronDown, 18);
            if (open) _expanded.Add(name); else _expanded.Remove(name);
            if (open) Dispatcher.UIThread.Post(() => value.Focus());
        };

        return new Border { Classes = { "card" }, Child = new StackPanel { Children = { header, details } } };
    }

    static Border StatusPill(SecretStatus s) => StatusPill(s, out _);

    static Border StatusPill(SecretStatus s, out TextBlock text)
    {
        text = new TextBlock { Text = Humanize.Status(s, DateTimeOffset.Now), VerticalAlignment = VerticalAlignment.Center };
        return new Border
        {
            Classes = { "pill", s.State switch { SecretState.Missing => "warn", SecretState.Stale => "bad", _ => "ok" } },
            Child = Row(new Ellipse { Classes = { "dot" } }, text),
        };
    }

    Control NameLine(SecretStatus s)
    {
        var pill = StatusPill(s, out var pillText);
        if (!_manage) _pillTexts[s.Definition.Name] = pillText; // kept current by the live refresh ("Expired 5m ago")
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(new TextBlock { Text = s.Definition.Name, Classes = { "name" }, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(pill, 1);
        grid.Children.Add(pill);
        return grid;
    }

    /// <summary>A masked input with a show/hide toggle, used for every secret value.</summary>
    static TextBox PasswordBox(string placeholder)
    {
        var box = new TextBox { Classes = { "field" }, PasswordChar = '•', Watermark = placeholder };
        var reveal = new ToggleButton { Classes = { "icon-btn" }, Content = Icons.Create(Icons.Eye, 17) };
        reveal.IsCheckedChanged += (_, _) =>
        {
            box.RevealPassword = reveal.IsChecked == true;
            reveal.Content = Icons.Create(box.RevealPassword ? Icons.EyeOff : Icons.Eye, 17);
        };
        box.InnerRightContent = reveal;
        return box;
    }

    TextBox ValueField(SecretStatus s, string placeholder)
    {
        var box = PasswordBox(placeholder);
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter && !_manage) SavePrompt(); };
        _values[s.Definition.Name] = box;
        return box;
    }

    // ---- new secret ------------------------------------------------------------------------

    void ToggleNewPanel()
    {
        if (_newPanel is { IsVisible: true }) { _newPanel.IsVisible = false; Rebuild(); return; }

        var name = new TextBox { Classes = { "field" }, Watermark = "e.g. vpn-password", MaxLength = 64 };
        var description = new TextBox { Classes = { "field" }, Watermark = "What is this for? (optional)" };
        var schedule = new SchedulePicker();
        var value = PasswordBox("Optional: set it now");
        var message = new TextBlock { Classes = { "error" }, IsVisible = false, TextWrapping = TextWrapping.Wrap };

        var cancel = new Button { Classes = { "btn", "ghost", "sm" }, Content = "Cancel" };
        var create = new Button { Classes = { "btn", "primary", "sm" }, Content = "Create secret", MinWidth = 110 };
        cancel.Click += (_, _) => { _newPanel!.IsVisible = false; Rebuild(); };
        create.Click += (_, _) =>
        {
            try
            {
                var def = _vault.Define(name.Text?.Trim() ?? "", schedule.Value, string.IsNullOrWhiteSpace(description.Text) ? null : description.Text.Trim());
                // Setting it here means it is never "missing", so nothing has to prompt for it.
                if (!string.IsNullOrEmpty(value.Text)) _vault.Set(def.Name, value.Text);
                _newPanel!.IsVisible = false;
                Rebuild();
            }
            catch (Exception ex)
            {
                // The secret may exist already (the value is what failed); creating again just retries the value.
                message.Text = ex.InnerException is { } inner ? $"{ex.Message} ({inner.Message})" : ex.Message;
                message.IsVisible = true;
            }
        };

        _newPanel = new Border
        {
            Classes = { "card" },
            Child = new StackPanel
            {
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = "New secret", Classes = { "name" } },
                    Field("Name", name), Field("Description", description), Field("Rotation", schedule),
                    Field("Value", new StackPanel
                    {
                        Spacing = 6,
                        Children = { value, new TextBlock { Text = "You can set it later. Keyturn waits 10 minutes before asking for a secret that has none.", Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap } },
                    }),
                    message,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, create } },
                },
            },
        };
        Rebuild();
        Dispatcher.UIThread.Post(() => name.Focus());
    }

    // ---- actions ---------------------------------------------------------------------------

    async void SavePrompt()
    {
        var entered = _values.Where(kv => !string.IsNullOrEmpty(kv.Value.Text)).ToList();
        if (entered.Count == 0)
        {
            ShowError("Enter a new value for at least one secret, or choose Later.");
            return;
        }
        if (!Try(() => { foreach (var (name, box) in entered) { _vault.Set(name, box.Text!); box.Text = ""; } })) return;

        await Flash(_saveAll!, "Saved");
        if (!_closing) { _closing = true; Close(); }
    }

    /// <summary>Briefly turns a button green with a checkmark so saving feels acknowledged.</summary>
    static async Task Flash(Button button, string text)
    {
        button.IsEnabled = false;
        button.Classes.Add("done");
        button.Content = Row(Icons.Create(Icons.Check, 16, "white"), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        await Task.Delay(750);
    }

    bool Try(Action action)
    {
        try
        {
            _error.IsVisible = false;
            action();
            return true;
        }
        catch (Exception ex)
        {
            ShowError(ex.InnerException is { } inner ? $"{ex.Message} ({inner.Message})" : ex.Message);
            return false;
        }
    }

    void ShowError(string message)
    {
        _error.Text = message;
        _error.IsVisible = true;
    }

    // ---- layout helpers --------------------------------------------------------------------

    static StackPanel Row(params Control[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        foreach (var c in children) { c.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(c); }
        return row;
    }

    static StackPanel Field(string label, Control input) =>
        new() { Spacing = 6, Children = { new TextBlock { Text = label, Classes = { "label" } }, input } };
}
