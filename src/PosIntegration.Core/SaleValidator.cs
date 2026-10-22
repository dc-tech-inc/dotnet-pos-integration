namespace DcTech.PosIntegration;

public static class SaleValidator
{
    /// <summary>Returns null when the sale is acceptable, otherwise the reason it is rejected.</summary>
    public static string? Validate(Sale sale)
    {
        if (string.IsNullOrWhiteSpace(sale.SaleId)) return "missing sale id";
        if (sale.Lines.Count == 0) return "sale has no lines";
        foreach (var line in sale.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.Sku)) return "line without SKU";
            if (line.Quantity <= 0) return $"non-positive quantity for {line.Sku}";
        }
        return null;
    }

    /// <summary>Merges repeated SKUs and sorts by SKU. A fixed lock order keeps concurrent sales from deadlocking on each other.</summary>
    public static IReadOnlyList<SaleLine> Normalize(IEnumerable<SaleLine> lines) =>
        lines.GroupBy(l => l.Sku, StringComparer.Ordinal)
             .OrderBy(g => g.Key, StringComparer.Ordinal)
             .Select(g => new SaleLine(g.Key, g.Sum(l => l.Quantity)))
             .ToList();
}
