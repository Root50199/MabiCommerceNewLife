using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MabiCommerceNewLife;

public partial class CargoDispositionWindow : Window
{
    private readonly IReadOnlyDictionary<int, CargoDispositionDestination> _destinationsById;
    private readonly ObservableCollection<CargoDispositionPriceLine> _lines = [];

    public CargoDispositionResult? Result { get; private set; }

    public CargoDispositionWindow(
        string cargoModeName,
        IReadOnlyList<CargoDispositionDestination> destinations,
        int loadedGoodsCount,
        int loadedQuantity)
    {
        InitializeComponent();
        _destinationsById = destinations.ToDictionary(destination => destination.PostId);
        CargoDescriptionText.Text =
            $"{cargoModeName} cargo contains {loadedQuantity:N0} units across {loadedGoodsCount:N0} goods.";
        DestinationPicker.ItemsSource = destinations;
        CargoPricesGrid.ItemsSource = _lines;
        if (destinations.Count > 0)
            DestinationPicker.SelectedIndex = 0;
        else
        {
            SellCargoButton.IsEnabled = false;
            DispositionStatusText.Text = "No destination has a positive quote for every loaded good.";
            SaleTotalText.Text = "Sale: --";
        }
    }

    private void DestinationPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DestinationPicker.SelectedValue is not int destinationId ||
            !_destinationsById.TryGetValue(destinationId, out var destination))
            return;

        _lines.Clear();
        foreach (var line in destination.Lines)
            _lines.Add(line);
        var grossSale = destination.Lines.Sum(line => line.SaleValue);
        SaleTotalText.Text = $"Gross sale: {grossSale.ToString("N0", CultureInfo.CurrentCulture)} Ducats";
        SellCargoButton.IsEnabled = true;
    }

    private void SellCargo_Click(object sender, RoutedEventArgs e)
    {
        if (DestinationPicker.SelectedValue is not int destinationId ||
            !_destinationsById.ContainsKey(destinationId))
            return;
        Result = new CargoDispositionResult(CargoDispositionAction.Sell, destinationId);
        DialogResult = true;
    }

    private void ClearCargo_Click(object sender, RoutedEventArgs e)
    {
        Result = new CargoDispositionResult(CargoDispositionAction.Clear, null);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) DialogResult = false;
    }
}

public enum CargoDispositionAction
{
    Clear,
    Sell
}

public sealed record CargoDispositionResult(CargoDispositionAction Action, int? DestinationId);

public sealed record CargoDispositionDestination(
    int PostId,
    string Name,
    IReadOnlyList<CargoDispositionPriceLine> Lines);

public sealed record CargoDispositionPriceLine(
    string ProductName,
    int Quantity,
    decimal UnitPrice,
    bool IsManualPrice)
{
    public string PriceType => IsManualPrice ? "Entered" : "Estimate";
    public decimal SaleValue => Quantity * UnitPrice;
}
