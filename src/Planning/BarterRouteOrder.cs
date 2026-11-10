namespace MabiCommerceNewLife;

public static class BarterRouteOrder
{
    public static int SelectStartPostId(
        int firstPostId,
        int secondPostId,
        int destinationPostId,
        Func<int, int, DateTimeOffset, decimal?> legMinutes,
        DateTimeOffset departureTime,
        int? fixedStartPostId = null)
    {
        if (fixedStartPostId == firstPostId || fixedStartPostId == secondPostId)
            return fixedStartPostId.Value;

        var firstOrderMinutes = RouteMinutes(firstPostId, secondPostId);
        var secondOrderMinutes = RouteMinutes(secondPostId, firstPostId);
        return secondOrderMinutes is decimal secondMinutes &&
               (firstOrderMinutes is null || secondMinutes < firstOrderMinutes)
            ? secondPostId
            : firstPostId;

        decimal? RouteMinutes(int startPostId, int nextPostId)
        {
            var firstLeg = legMinutes(startPostId, nextPostId, departureTime);
            if (firstLeg is not > 0) return null;
            var secondDepartureTime = departureTime.AddMinutes((double)firstLeg.Value);
            var secondLeg = legMinutes(nextPostId, destinationPostId, secondDepartureTime);
            return firstLeg is > 0 && secondLeg is > 0 ? firstLeg.Value + secondLeg.Value : null;
        }
    }
}
