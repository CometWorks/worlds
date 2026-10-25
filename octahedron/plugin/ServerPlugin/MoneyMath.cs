using System.Collections.Generic;

namespace ServerPlugin;

// The daily settlement's arithmetic, without the game, so tests/money can check it alone.
// All of it is checked long math: an overflow throws before any balance changes.
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

    // One day's settlement. taxed: every taxed account with its balance and threshold.
    // recipients: the active players, each paid Share. What the split leaves goes to OCT.
    public static Settlement Settle(
        IEnumerable<(long Id, long Balance, long Threshold)> taxed,
        int basisPoints,
        long octBalance,
        long octCap,
        long subsidyPerPlayer,
        IReadOnlyList<long> recipients
    )
    {
        var settlement = new Settlement { Recipients = recipients };
        checked
        {
            foreach (var (id, balance, threshold) in taxed)
            {
                var tax = Tax(balance, threshold, basisPoints);
                if (tax <= 0)
                    continue;
                settlement.Taxes.Add((id, tax));
                settlement.TaxTotal += tax;
            }
            settlement.OctExcess = octBalance > octCap ? octBalance - octCap : 0;
            settlement.Subsidy = subsidyPerPlayer * recipients.Count;
            settlement.Pool = settlement.TaxTotal + settlement.OctExcess + settlement.Subsidy;
            settlement.Share = recipients.Count == 0 ? 0 : settlement.Pool / recipients.Count;
            settlement.ToOct = settlement.Pool - settlement.Share * recipients.Count;
        }
        return settlement;
    }
}

internal sealed class Settlement
{
    public readonly List<(long Id, long Amount)> Taxes = new();
    public IReadOnlyList<long> Recipients;
    public long TaxTotal;
    public long OctExcess;
    public long Subsidy;
    public long Pool;
    public long Share;
    public long ToOct;
}
