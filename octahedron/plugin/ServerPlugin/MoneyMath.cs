using System.Collections.Generic;

namespace ServerPlugin;

// The arithmetic of the daily wealth tax and the hourly payout, without the game, so tests/money
// can check it alone. All of it is checked long math: an overflow throws before any balance changes.
internal static class MoneyMath
{
    // What a balance pays: basisPoints / 10,000 of the part above the threshold, rounded down
    public static long Tax(long balance, long threshold, int basisPoints)
    {
        if (basisPoints <= 0 || balance <= threshold)
            return 0;
        var excess = checked(balance - threshold);
        return checked(excess / 10_000 * basisPoints + excess % 10_000 * basisPoints / 10_000);
    }

    // The day's tax of every taxed account, given with its balance and threshold
    public static (List<(long Id, long Amount)> Taxes, long Total) Taxes(
        IEnumerable<(long Id, long Balance, long Threshold)> taxed,
        int basisPoints
    )
    {
        var taxes = new List<(long Id, long Amount)>();
        long total = 0;
        foreach (var (id, balance, threshold) in taxed)
        {
            var tax = Tax(balance, threshold, basisPoints);
            if (tax <= 0)
                continue;
            taxes.Add((id, tax));
            total = checked(total + tax);
        }
        return (taxes, total);
    }

    // One hourly payout. held: the tax collected and not paid out yet, released evenly over the
    // hours left until the next tax day (all of it in the last hour). The subsidy is a 24th of the
    // daily amount per active player. Each recipient gets Share; what the split leaves stays held.
    // Nobody active: nothing is paid, the tax stays held and OCT keeps its excess.
    public static Payout Pay(
        long held,
        int hoursLeft,
        long octBalance,
        long octCap,
        long dailySubsidyPerPlayer,
        IReadOnlyList<long> recipients
    )
    {
        var payout = new Payout { Recipients = recipients };
        if (recipients.Count == 0)
            return payout;
        checked
        {
            payout.Tax = hoursLeft <= 1 ? held : held / hoursLeft;
            payout.OctExcess = octBalance > octCap ? octBalance - octCap : 0;
            payout.Subsidy =
                dailySubsidyPerPlayer / 24 * recipients.Count
                + dailySubsidyPerPlayer % 24 * recipients.Count / 24;
            payout.Pool = payout.Tax + payout.OctExcess + payout.Subsidy;
            payout.Share = payout.Pool / recipients.Count;
            payout.Rest = payout.Pool - payout.Share * recipients.Count;
        }
        return payout;
    }

    // Hangar's fee for a ship brought to another station: per10Pcu SC per 10 PCU, rounded up
    public static long RetrieveFee(int pcu, long per10Pcu) =>
        pcu <= 0 || per10Pcu <= 0 ? 0 : checked((pcu * per10Pcu + 9) / 10);
}

internal sealed class Payout
{
    public IReadOnlyList<long> Recipients = [];
    public long Tax;
    public long OctExcess;
    public long Subsidy;
    public long Pool;
    public long Share;
    public long Rest;
}
