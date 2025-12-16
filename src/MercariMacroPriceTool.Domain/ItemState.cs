namespace MercariMacroPriceTool.Domain;

/// <summary>
/// Items テーブル相当の永続化モデル。
/// </summary>
public class ItemState
{
    public string ItemId { get; set; } = string.Empty;
    public string ItemUrl { get; set; } = string.Empty;
    public string? Title { get; set; }
    public int BasePrice { get; set; }
    public int RunCount { get; set; }
    public string? LastRunDate { get; set; }
    public string? UpdatedAt { get; set; }
    public int LastDownAmount { get; set; }
    public string? LastDownAt { get; set; }
    public int? LastDownRatePercent { get; set; }
    public int? LastDownDailyDownYen { get; set; }
    public int? LastDownRunIndex { get; set; }
}
