using DoomLauncher.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DoomLauncher.WinUI;

public sealed record ServerTableState(string Search, bool PopulatedOnly, bool HideFull, ServerSortColumn Sort, bool Descending, string? SelectedAddress);

public sealed partial class MultiplayerServerTable : UserControl
{
    // One column definition drives headers and every virtualized row.
    private static readonly (ServerSortColumn Sort, string Label, double Width, bool Star)[] Columns =
    [
        (ServerSortColumn.Name, "ServerName", 3, true), (ServerSortColumn.Players, "Players", 76, false),
        (ServerSortColumn.GameMode, "GameMode", 140, false), (ServerSortColumn.Map, "Map", 85, false),
        (ServerSortColumn.Iwad, "IWAD", 108, false), (ServerSortColumn.Version, "EngineVersion", 105, false),
        (ServerSortColumn.Ping, "Ping", 70, false), (ServerSortColumn.Password, "Password", 70, false),
        (ServerSortColumn.Address, "Address", 155, false),
    ];
    private IReadOnlyList<MultiplayerServer> _servers = [];
    private UiText? _strings;
    private bool _updating;
    private ServerSortColumn _sort = ServerSortColumn.Players;
    private bool _descending = true;
    public event EventHandler? SelectedServerChanged;
    public MultiplayerServer? SelectedServer => ServerList.SelectedItem as MultiplayerServer;
    internal ListView Rows => ServerList;
    internal string FilterText { get => Search.Text; set => Search.Text = value; }
    internal IReadOnlyList<Button> HeaderButtons => Headers.Children.OfType<Button>().ToArray();

    public MultiplayerServerTable() { InitializeComponent(); SetColumns(Headers); }
    public void Configure(UiText strings, ServerTableState? previous = null)
    {
        _strings = strings; DataContext = strings; Headers.Children.Clear();
        foreach (var (column, label, _, _) in Columns)
        {
            var button = new Button { Tag = column, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = column is ServerSortColumn.Players or ServerSortColumn.Ping ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(0, 5, 0, 5) };
            ToolTipService.SetToolTip(button, strings["SortServers"]); Grid.SetColumn(button, Headers.Children.Count);
            button.Click += (_, _) => SortBy(column); Headers.Children.Add(button);
        }
        if (previous is not null)
        {
            _updating = true; Search.Text = previous.Search; PopulatedOnly.IsChecked = previous.PopulatedOnly; HideFull.IsChecked = previous.HideFull;
            _sort = previous.Sort; _descending = previous.Descending; _updating = false;
        }
        Apply();
    }
    public ServerTableState CaptureState() => new(Search.Text, PopulatedOnly.IsChecked == true, HideFull.IsChecked == true, _sort, _descending, SelectedServer?.AddressText);
    public void SetServers(IReadOnlyList<MultiplayerServer> servers, string? selectedAddress = null)
    { _servers = servers; Apply(selectedAddress); }
    internal void SelectAddress(string address) => ServerList.SelectedItem = ServerList.Items.OfType<MultiplayerServer>().FirstOrDefault(server => server.AddressText == address);
    internal void SortBy(ServerSortColumn column)
    { _descending = _sort == column ? !_descending : column == ServerSortColumn.Players; _sort = column; Apply(); }
    private static void SetColumns(Grid grid)
    {
        grid.ColumnDefinitions.Clear();
        foreach (var (_, _, width, star) in Columns) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, star ? GridUnitType.Star : GridUnitType.Pixel) });
    }
    private void ServerRow_Loaded(object sender, RoutedEventArgs args)
    {
        var grid = (Grid)sender; SetColumns(grid); UpdatePasswordLabel(grid);
    }
    private void ServerRow_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args) => UpdatePasswordLabel((Grid)sender);
    private void UpdatePasswordLabel(Grid grid)
    {
        if (_strings is not null && grid.DataContext is MultiplayerServer server)
        {
            var icon = grid.Children.OfType<FontIcon>().Single();
            var description = _strings[server.PasswordRequired ? "PasswordProtected" : "NoPasswordRequired"];
            ToolTipService.SetToolTip(icon, description); Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(icon, description);
        }
    }
    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs args) => TableContent.Width = Math.Max(1160, args.NewSize.Width);
    private void Filter_Changed(object sender, RoutedEventArgs args) { if (!_updating && _strings is not null) Apply(); }
    private void Selection_Changed(object sender, SelectionChangedEventArgs args) { if (!_updating) SelectedServerChanged?.Invoke(this, EventArgs.Empty); }
    private void Apply(string? address = null)
    {
        if (_strings is null) return;
        var prior = SelectedServer; address ??= prior?.AddressText;
        var rows = MultiplayerBrowserQuery.Apply(_servers, Search.Text, PopulatedOnly.IsChecked == true, HideFull.IsChecked == true, _sort, _descending);
        _updating = true; ServerList.ItemsSource = rows; ServerList.SelectedItem = rows.FirstOrDefault(server => server.AddressText == address); _updating = false;
        EmptyMessage.Text = _servers.Count == 0 ? _strings["ServerBrowserEmpty"] : _strings["NoMatchingServers"];
        EmptyMessage.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Summary.Text = string.Format(_strings["ServerCount"], rows.Count, _servers.Count);
        foreach (var button in Headers.Children.OfType<Button>())
        {
            var column = (ServerSortColumn)button.Tag; var definition = Columns.First(item => item.Sort == column);
            var label = _strings[definition.Label] + (_sort == column ? _descending ? " ▼" : " ▲" : "");
            button.Content = label; Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
        }
        if (prior != SelectedServer) SelectedServerChanged?.Invoke(this, EventArgs.Empty);
    }
}
