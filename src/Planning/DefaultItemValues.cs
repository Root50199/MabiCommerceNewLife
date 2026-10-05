using System.IO;
using System.Text.Json;

namespace MabiCommerceNewLife;

public sealed class DefaultItemValues
{
    public Dictionary<int, decimal> BarterMaterialValuesById { get; set; } = [];
    public Dictionary<string, int> GuaranteeLetterMarketValue { get; set; } = [];

    public static DefaultItemValues Load(string path)
    {
        var values = JsonSerializer.Deserialize<DefaultItemValues>(File.ReadAllText(path))
            ?? throw new InvalidDataException("The default item values file is empty.");
        if (values.BarterMaterialValuesById is null || values.GuaranteeLetterMarketValue is null ||
            values.BarterMaterialValuesById.Any(pair => pair.Key <= 0 || pair.Value <= 0) ||
            values.GuaranteeLetterMarketValue.Any(pair =>
                !Enum.TryParse<GuaranteeLetterKind>(pair.Key, out var kind) ||
                !Enum.IsDefined(kind) || pair.Value <= 0))
            throw new InvalidDataException("The default item values file contains invalid prices or identifiers.");
        return values;
    }
}
