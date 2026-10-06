namespace SplitIt.Shared;

/// <summary>One line of a settle-up plan: <paramref name="From"/> pays <paramref name="To"/>.</summary>
internal sealed record Transfer(MemberId From, MemberId To, long AmountMinor);

/// <summary>
/// The settle-up procedure (spec §10): a pure function of the balances, with
/// shared-history scores only breaking ties. No I/O, no framework — and the most
/// heavily tested code in the project.
///
/// 1. Drop members with a zero balance.
/// 2. Exact matches: every debtor/creditor pair of equal magnitude is a candidate;
///    take them by score (highest first), then debtor, then creditor in member-added
///    order, skipping any whose debtor or creditor is already matched.
/// 3. Greedy, until nobody owes: of the creditors with the largest credit and the
///    debtors with the largest debt, pair the two with the highest score (ties:
///    creditor, then debtor, in member-added order) and transfer the smaller.
/// 4. List the plan by payer, then recipient, in member-added order.
///
/// At most n − 1 transfers for n members with a non-zero balance; every exact match
/// taken. A heuristic, not a proven minimum (spec §10).
/// </summary>
internal static class SettlementPlan
{
    /// <param name="balances">Every member's balance, in member-added order; they must sum to zero.</param>
    /// <param name="score">The shared-history score of a pair; symmetric.</param>
    public static IReadOnlyList<Transfer> Plan(
        IReadOnlyList<(MemberId Member, long Balance)> balances, Func<MemberId, MemberId, int> score)
    {
        if (balances.Aggregate(Int128.Zero, (sum, b) => sum + b.Balance) != 0)
            throw new ArgumentException("balances must sum to zero", nameof(balances));

        var order = balances.Select((b, i) => (b.Member, i)).ToDictionary(x => x.Member, x => x.i);
        var open = balances.Where(b => b.Balance != 0).ToDictionary(b => b.Member, b => b.Balance);
        var transfers = new List<Transfer>();

        void Pay(MemberId debtor, MemberId creditor, long amount)
        {
            transfers.Add(new Transfer(debtor, creditor, amount));
            foreach (var (member, change) in new[] { (debtor, amount), (creditor, -amount) })
                if ((open[member] += change) == 0)
                    open.Remove(member);
        }

        // Exact matches: each clears two people with one transfer.
        var candidates =
            from debtor in open.Keys.Where(m => open[m] < 0)
            from creditor in open.Keys.Where(m => open[m] > 0)
            where -open[debtor] == open[creditor]
            orderby score(debtor, creditor) descending, order[debtor], order[creditor]
            select (debtor, creditor);
        foreach (var (debtor, creditor) in candidates.ToList())
            if (open.ContainsKey(debtor) && open.ContainsKey(creditor))
                Pay(debtor, creditor, open[creditor]);

        // Greedy: the largest creditor with the largest debtor.
        while (open.Count > 0)
        {
            var credit = open.Values.Max();
            var debt = -open.Values.Min();
            var (debtor, creditor) = (
                from c in open.Keys.Where(m => open[m] == credit)
                from d in open.Keys.Where(m => open[m] == -debt)
                orderby score(d, c) descending, order[c], order[d]
                select (d, c)).First();
            Pay(debtor, creditor, Math.Min(credit, debt));
        }

        return transfers.OrderBy(t => order[t.From]).ThenBy(t => order[t.To]).ToList();
    }
}
