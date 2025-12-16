namespace MercariMacroPriceTool.Automation;

/// <summary>
/// listings ページから取得する1件分の情報。
/// </summary>
public class ListingItem
{
    public string ItemId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int Price { get; set; }
    public string ItemUrl { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;
    public bool IsPaused { get; set; }
    public string? RawText { get; set; }
}
