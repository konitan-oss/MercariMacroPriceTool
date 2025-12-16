using System;

namespace MercariMacroPriceTool.Automation;

/// <summary>
/// 中央集約した URL 定義。docs/SELECTORS.md に揃える。
/// </summary>
public static class AutomationEndpoints
{
    public const string HomeUrl = "https://jp.mercari.com/";
    public const string ListingsUrl = "https://jp.mercari.com/mypage/listings";

    public static Uri GetDefaultLanding() => new(ListingsUrl);
}
