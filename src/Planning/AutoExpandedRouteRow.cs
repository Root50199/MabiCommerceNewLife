using System.ComponentModel;

namespace MabiCommerceNewLife;

public sealed class AutoExpandedRouteRow : INotifyPropertyChanged
{
    private string _profit;
    private string _profitPerMinute;
    private string _totalGold;
    private string _goldPerMinute;
    private string _rewardTooltip = string.Empty;

    public AutoExpandedRouteRow(string destination, string transport, string ducatCost, bool costIsGold, string profit,
        string profitPerMinute, string time, string totalGold, string goldPerMinute, string seasonalRankingPoints,
        string goodsToBuy, decimal? minutes, AutoPlanRow plannerRoute)
    {
        Destination = destination;
        Transport = transport;
        DucatCost = ducatCost;
        CostIsGold = costIsGold;
        _profit = profit;
        _profitPerMinute = profitPerMinute;
        Time = time;
        _totalGold = totalGold;
        _goldPerMinute = goldPerMinute;
        SeasonalRankingPoints = seasonalRankingPoints;
        GoodsToBuy = goodsToBuy;
        Minutes = minutes;
        PlannerRoute = plannerRoute;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Destination { get; }
    public string Transport { get; }
    public string DucatCost { get; }
    public bool CostIsGold { get; }
    public bool CostIsDucat => !CostIsGold;
    public bool HasMinutes => Minutes is > 0;
    public string Profit => _profit;
    public string ProfitPerMinute => _profitPerMinute;
    public string Time { get; }
    public string TotalGold => _totalGold;
    public string GoldPerMinute => _goldPerMinute;
    public string SeasonalRankingPoints { get; }
    public string GoodsToBuy { get; }
    public decimal? Minutes { get; }
    public AutoPlanRow PlannerRoute { get; }
    public string RewardTooltip => _rewardTooltip;

    public void SetReward(string profit, string profitPerMinute, string totalGold, string goldPerMinute, string rewardTooltip)
    {
        _profit = profit;
        _profitPerMinute = profitPerMinute;
        _totalGold = totalGold;
        _goldPerMinute = goldPerMinute;
        _rewardTooltip = rewardTooltip;
        foreach (var name in new[] { nameof(Profit), nameof(ProfitPerMinute), nameof(TotalGold), nameof(GoldPerMinute), nameof(RewardTooltip) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}