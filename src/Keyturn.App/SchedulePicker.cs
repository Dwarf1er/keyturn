using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Keyturn;

namespace Keyturn.App;

/// <summary>Replaces free-text schedules: "Every N minutes/hours/days" or "Daily at HH:mm", with a live summary.</summary>
public sealed class SchedulePicker : UserControl
{
    static readonly string[] Units = ["minutes", "hours", "days"];

    readonly ToggleButton _every = new() { Content = "Every", Classes = { "seg" } };
    readonly ToggleButton _daily = new() { Content = "Daily at", Classes = { "seg" } };
    readonly NumericUpDown _count = new() { Minimum = 1, Maximum = 999, Value = 1, FormatString = "0", Width = 120, Classes = { "field" } };
    readonly ComboBox _unit = new() { ItemsSource = Units, SelectedIndex = 1, Width = 130, Classes = { "field" } };
    static readonly bool Use24Hour = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('H');
    readonly ComboBox _hour = new() { Width = 84, Classes = { "field" } };
    readonly ComboBox _minute = new() { Width = 84, Classes = { "field" } };
    readonly ComboBox _ampm = new() { ItemsSource = new[] { "AM", "PM" }, Width = 84, Classes = { "field" } };
    readonly StackPanel _everyRow = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    readonly StackPanel _dailyRow = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    readonly TextBlock _summary = new() { Classes = { "muted", "small" } };
    bool _loading;

    public event EventHandler? Changed;

    public SchedulePicker()
    {
        _everyRow.Children.Add(_count);
        _everyRow.Children.Add(_unit);
        _hour.ItemsSource = Use24Hour ? Enumerable.Range(0, 24).Select(h => $"{h:00}").ToArray() : Enumerable.Range(1, 12).Select(h => h.ToString()).ToArray();
        SetMinutes(0);
        _dailyRow.Children.Add(_hour);
        _dailyRow.Children.Add(new TextBlock { Text = ":", Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center });
        _dailyRow.Children.Add(_minute);
        if (!Use24Hour) _dailyRow.Children.Add(_ampm);
        SetTime(new TimeSpan(6, 0, 0));

        var track = new Border
        {
            Classes = { "seg-track" },
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { _every, _daily } },
        };

        Content = new StackPanel { Spacing = 10, Children = { track, _everyRow, _dailyRow, _summary } };

        _every.Click += (_, _) => SetMode(daily: false);
        _daily.Click += (_, _) => SetMode(daily: true);
        _count.ValueChanged += (_, _) => Raise();
        _unit.SelectionChanged += (_, _) => Raise();
        _hour.SelectionChanged += (_, _) => Raise();
        _minute.SelectionChanged += (_, _) => Raise();
        _ampm.SelectionChanged += (_, _) => Raise();
        SetMode(daily: true);
    }

    /// <summary>The schedule text understood by <see cref="Keyturn.Schedule.Parse"/>.</summary>
    public string Value => _daily.IsChecked == true
        ? $"daily@{Time:hh\\:mm}"
        : $"{(int)(_count.Value ?? 1)}{"mhd"[Math.Max(0, _unit.SelectedIndex)]}";

    public void Load(string schedule)
    {
        _loading = true;
        var parsed = Schedule.Parse(schedule);
        if (parsed.DailyAt is { } t)
        {
            SetTime(t);
            SetMode(daily: true);
        }
        else
        {
            var i = parsed.Interval!.Value;
            (_count.Value, _unit.SelectedIndex) =
                i.TotalDays >= 1 && i.TotalDays % 1 == 0 ? ((decimal)i.TotalDays, 2) :
                i.TotalHours >= 1 && i.TotalHours % 1 == 0 ? ((decimal)i.TotalHours, 1) :
                ((decimal)i.TotalMinutes, 0);
            SetMode(daily: false);
        }
        _loading = false;
        UpdateSummary();
    }

    TimeSpan Time
    {
        get
        {
            var hour = _hour.SelectedIndex < 0 ? 0 : _hour.SelectedIndex;
            var minute = int.Parse((string?)_minute.SelectedItem ?? "0", CultureInfo.InvariantCulture);
            if (!Use24Hour)
            {
                hour = (hour + 1) % 12; // items are 1..12; 12 maps to 0
                if (_ampm.SelectedIndex == 1) hour += 12;
            }
            return new TimeSpan(hour, minute, 0);
        }
    }

    void SetTime(TimeSpan t)
    {
        SetMinutes(t.Minutes);
        _minute.SelectedItem = $"{t.Minutes:00}";
        if (Use24Hour) _hour.SelectedIndex = t.Hours;
        else
        {
            _hour.SelectedIndex = (t.Hours % 12 == 0 ? 12 : t.Hours % 12) - 1;
            _ampm.SelectedIndex = t.Hours >= 12 ? 1 : 0;
        }
    }

    /// <summary>Five-minute steps, plus the exact minute when a stored schedule uses an unusual one.</summary>
    void SetMinutes(int current)
    {
        var minutes = Enumerable.Range(0, 12).Select(i => i * 5).Append(current).Distinct().Order().Select(m => $"{m:00}").ToArray();
        _minute.ItemsSource = minutes;
        _minute.SelectedItem = $"{current:00}";
    }

    void SetMode(bool daily)
    {
        _daily.IsChecked = daily;
        _every.IsChecked = !daily;
        _everyRow.IsVisible = !daily;
        _dailyRow.IsVisible = daily;
        Raise();
    }

    void Raise()
    {
        UpdateSummary();
        if (!_loading) Changed?.Invoke(this, EventArgs.Empty);
    }

    void UpdateSummary() =>
        _summary.Text = Schedule.TryParse(Value, out var s) ? $"{Humanize.Schedule(s)}: you'll be asked for a new value" : "";
}
