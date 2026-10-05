using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MabiCommerceNewLife;

public partial class OcrPriceReviewWindow : Window
{
    private readonly ObservableCollection<PriceListOcrCandidate> _candidates;

    public IReadOnlyList<PriceListOcrCandidate> AcceptedCandidates => _candidates
        .Where(candidate => candidate.Include && candidate.Price > 0)
        .ToList();

    public decimal? AcceptedSourcePrice
    {
        get
        {
            if (ApplySourcePriceCheck.IsChecked != true ||
                !decimal.TryParse(SourcePriceInput.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var price) || price <= 0)
                return null;
            return price;
        }
    }

    public OcrPriceReviewWindow(string productName, string sourcePostName,
        IEnumerable<PriceListOcrCandidate> candidates, float meanConfidence, OcrSourcePrice? sourcePrice = null,
        bool showSourcePrice = true)
    {
        InitializeComponent();
        _candidates = new ObservableCollection<PriceListOcrCandidate>(candidates);
        OcrCandidatesGrid.ItemsSource = _candidates;
        OcrConfidenceText.Text = $"{productName} from {sourcePostName} · OCR confidence {meanConfidence:N1}% · review before applying.";
        SourcePriceInput.Text = sourcePrice?.Price.ToString("N0", CultureInfo.CurrentCulture) ?? string.Empty;
        SourcePriceText.Text = sourcePrice?.SourceText ?? "Source price not recognized";
        ApplySourcePriceCheck.IsEnabled = showSourcePrice && sourcePrice is not null;
        ApplySourcePriceCheck.IsChecked = showSourcePrice && sourcePrice is not null;
        SourcePricePanel.Visibility = showSourcePrice ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        OcrCandidatesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        OcrCandidatesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (AcceptedCandidates.Count == 0)
        {
            ReviewStatusText.Text = "Select at least one valid price.";
            return;
        }
        if (ApplySourcePriceCheck.IsChecked == true && AcceptedSourcePrice is null)
        {
            ReviewStatusText.Text = "Enter a valid source price or uncheck its update.";
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) DialogResult = false;
    }
}