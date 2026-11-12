using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace MabiCommerceNewLife;

public sealed class ChecklistOverlaySettings : HoverOverlaySettings
{
    public bool ClickThrough { get; set; }
}

public partial class ChecklistWindow : Window
{
    private readonly HoverOverlayChrome _chrome;
    private readonly HashSet<string> _checked;
    private IReadOnlyList<DepartureChecklistItem> _items = [];
    private ChecklistRow[] _rows = [];
    private bool _updating;
    private readonly Window _checkInputs;
    private readonly Canvas _inputCanvas = new();
    private readonly List<(CheckBox Source, CheckBox Input)> _inputChecks = [];
    private bool _clickThrough;
    private bool _syncingInputs;
    private bool _closing;

    public ChecklistWindow(ChecklistOverlaySettings settings, Action saveSettings, HashSet<string> checkedItems)
    {
        InitializeComponent();
        _checked = checkedItems;
        _clickThrough = settings.ClickThrough;
        _checkInputs = new Window
        {
            Title = "Checklist checkboxes", WindowStyle = WindowStyle.None, AllowsTransparency = true,
            Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            ShowActivated = false, Topmost = true, Content = _inputCanvas,
            UseLayoutRounding = true, SnapsToDevicePixels = true
        };
        _checkInputs.SourceInitialized += (_, _) => NativeWindowStyles.Add(_checkInputs, NativeWindowStyles.ToolWindow);
        _checkInputs.MouseEnter += (_, _) => NativeDrag.Activate(new WindowInteropHelper(_checkInputs).Handle);
        _chrome = new HoverOverlayChrome(this, BodyBackground, settings, saveSettings, "CHECKLIST",
            settings.ClickThrough, value =>
            {
                settings.ClickThrough = _clickThrough = value;
                SyncCheckboxInputs();
            });
        LayoutUpdated += (_, _) => SyncCheckboxInputs();
        LocationChanged += (_, _) => SyncCheckboxInputs();
        IsVisibleChanged += (_, _) => SyncCheckboxInputs();
        Loaded += (_, _) => SyncCheckboxInputs();
        BodyBackground.IsVisibleChanged += (_, _) => SyncCheckboxInputs();
        Closing += (_, _) => _closing = true;
        Closed += (_, _) =>
        {
            if (Application.Current.Windows.Cast<Window>().Contains(_checkInputs)) _checkInputs.Close();
        };
    }

    // A separate alpha-transparent window keeps only the checkbox squares interactive across processes.
    private void SyncCheckboxInputs()
    {
        if (_syncingInputs || _closing) return;
        _syncingInputs = true;
        try { UpdateCheckboxInputs(); }
        finally { _syncingInputs = false; }
    }

    private void UpdateCheckboxInputs()
    {
        if (!IsLoaded || !IsVisible || !BodyBackground.IsVisible || !_clickThrough)
        {
            _checkInputs.Hide();
            foreach (var (source, _) in _inputChecks) source.Visibility = Visibility.Visible;
            return;
        }
        var sources = VisualChildren<CheckBox>(ChecklistItems).ToArray();
        if (!_inputChecks.Select(pair => pair.Source).SequenceEqual(sources))
        {
            _inputCanvas.Children.Clear();
            _inputChecks.Clear();
            foreach (var source in sources)
            {
                var input = new CheckBox { DataContext = source.DataContext, Width = 14, Height = 14 };
                input.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty, new Binding("Text"));
                input.SetBinding(CheckBox.IsCheckedProperty, new Binding("IsChecked") { Mode = BindingMode.TwoWay });
                input.Checked += ChecklistItem_Changed;
                input.Unchecked += ChecklistItem_Changed;
                _inputCanvas.Children.Add(input);
                _inputChecks.Add((source, input));
            }
        }
        _checkInputs.Left = Left;
        _checkInputs.Top = Top;
        _checkInputs.Width = ActualWidth;
        _checkInputs.Height = ActualHeight;
        var viewport = ChecklistScroll.TransformToAncestor(this).TransformBounds(new Rect(ChecklistScroll.RenderSize));
        foreach (var (source, input) in _inputChecks)
        {
            source.Visibility = Visibility.Hidden;
            var bounds = source.TransformToAncestor(this).TransformBounds(new Rect(source.RenderSize));
            Canvas.SetLeft(input, bounds.Left);
            Canvas.SetTop(input, bounds.Top);
            input.Visibility = viewport.Contains(bounds) ? Visibility.Visible : Visibility.Hidden;
        }
        if (!_checkInputs.IsVisible)
        {
            _checkInputs.Owner = this;
            _checkInputs.Show();
            TopmostGuard.Watch(_checkInputs);
        }
    }

    private static IEnumerable<T> VisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in VisualChildren<T>(child)) yield return descendant;
        }
    }

    private void ChecklistText_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBlock { DataContext: ChecklistRow row }) return;
        var checkbox = VisualChildren<CheckBox>(ChecklistItems).Single(check => ReferenceEquals(check.DataContext, row));
        checkbox.IsChecked = !row.IsChecked;
        e.Handled = true;
    }

    public void ShowChecklist(IReadOnlyList<DepartureChecklistItem> items, bool relevant)
    {
        ScopeText.Text = relevant ? "Active route preparations" : "General commerce preparations";
        if (_items.SequenceEqual(items)) return;
        _items = items;
        _updating = true;
        try
        {
            _rows = items.Select(item => new ChecklistRow(item, _checked.Contains(item.Key))).ToArray();
            UpdateParentChecks();
            ChecklistItems.ItemsSource = _rows;
        }
        finally { _updating = false; }
    }

    public void ResetChecks()
    {
        _checked.Clear();
        var items = _items;
        _items = [];
        ShowChecklist(items, ScopeText.Text == "Active route preparations");
    }

    public void ResetPosition() => _chrome.ResetPosition();

    internal Window CreateTutorialPreview() => _chrome.CreateTutorialPreview();

    private void ChecklistItem_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating || sender is not CheckBox { DataContext: ChecklistRow row } checkbox) return;
        _updating = true;
        try
        {
            SetChecked(row, checkbox.IsChecked == true);
            foreach (var child in _rows.Where(child => child.ParentKey == row.Key))
                SetChecked(child, row.IsChecked);
            UpdateParentChecks();
        }
        finally { _updating = false; }
    }

    private void SetChecked(ChecklistRow row, bool value)
    {
        row.IsChecked = value;
        if (value) _checked.Add(row.Key);
        else _checked.Remove(row.Key);
    }

    private void UpdateParentChecks()
    {
        foreach (var group in _rows.Where(row => row.ParentKey is not null).GroupBy(row => row.ParentKey))
            SetChecked(_rows.Single(row => row.Key == group.Key), group.All(row => row.IsChecked));
    }

    private sealed class ChecklistRow(DepartureChecklistItem item, bool isChecked) : INotifyPropertyChanged
    {
        private bool _isChecked = isChecked;
        public string Key => item.Key;
        public string Text => item.Text;
        public string? ParentKey => item.ParentKey;
        public Thickness Indent => new(ParentKey is null ? 2 : 20, 4, 2, 4);
        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value) return;
                _isChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
