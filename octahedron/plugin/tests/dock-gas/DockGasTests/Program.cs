using System;
using ServerPlugin;

// Checks the bill for H2 and O2 at a station connector (ServerPlugin/DockGasMath.cs). Exit code 0 = all pass.
// Usage: dotnet run -c Release --project tests/dock-gas/DockGasTests

int checks = 0,
    failures = 0;

void Check(bool ok, string what)
{
    checks++;
    if (ok)
        return;
    failures++;
    Console.WriteLine("FAIL " + what);
}

bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;

// 1,000,000 L at 20 L per kg is 50,000 kg of ice; at 3 SC per kg 150,000 SC
var (liters, _, sc) = DockGasMath.Bill(1_000_000, 0, 20, 10, 3, 1_000_000);
Check(Near(liters, 1_000_000) && sc == 150_000, $"a full small tank: {liters} L for {sc} SC");

// SE-1469: ice at 0.01 SC per kg, 10 SC a 1,000 kg lot: the full small tank's 50,000 kg are 500 SC
(liters, _, sc) = DockGasMath.Bill(1_000_000, 0, 20, 10, 0.01, 1_000_000);
Check(Near(liters, 1_000_000) && sc == 500, $"cheap ice: {liters} L for {sc} SC");

// SE-1497: ice at 0.04 SC per kg sells for 41 SC a lot with the tax: the full small tank is 2,050 SC
(liters, _, sc) = DockGasMath.Bill(1_000_000, 0, 20, 10, 0.041, 1_000_000);
Check(Near(liters, 1_000_000) && sc == 2_050, $"ice at 41 SC a t: {liters} L for {sc} SC");

// Rounded up to a whole SC: 30 L is 1.5 kg, 4.5 SC
(liters, _, sc) = DockGasMath.Bill(30, 0, 20, 10, 3, 100);
Check(Near(liters, 30) && sc == 5, $"rounded up: {liters} L for {sc} SC");

// Never free: a few liters cost 1 SC
(liters, _, sc) = DockGasMath.Bill(0.5, 0, 20, 10, 1, 100);
Check(Near(liters, 0.5) && sc == 1, $"at least 1 SC: {liters} L for {sc} SC");

// No float noise rounds a whole amount up: 20 L at 1 SC is exactly 1 SC
(liters, _, sc) = DockGasMath.Bill(20, 0, 20, 10, 1, 100);
Check(sc == 1, $"exact amounts stay exact: {sc} SC");

// Short of money: what the balance buys, for all of it
(liters, _, sc) = DockGasMath.Bill(1_000_000, 0, 20, 10, 3, 3_000);
Check(Near(liters, 20_000) && sc == 3_000, $"partial: {liters} L for {sc} SC");

// Nothing without money, a price or a need
Check(DockGasMath.Bill(1000, 0, 20, 10, 3, 0) == (0, 0, 0), "no money");
Check(DockGasMath.Bill(1000, 0, 20, 10, 0, 100) == (0, 0, 0), "no ice price");
Check(DockGasMath.Bill(0, 0, 20, 10, 3, 100) == (0, 0, 0), "full tanks");

// A balance too small for anything still buys its liters' worth
(liters, _, sc) = DockGasMath.Bill(1000, 0, 20, 10, 7, 1);
Check(Near(liters, 20.0 / 7) && sc == 1, $"1 SC buys a little: {liters} L for {sc} SC");

// SE-1536: O2 comes from the same ice. Empty tanks of a large grid ship: 1,000,000 L of H2 is 50,000 kg
// of ice at 20 L per kg, 100,000 L of O2 is 10,000 kg at 10 L per kg. Both are filled for the larger,
// 50,000 kg at 3 SC per kg, billed once: 150,000 SC
var (h2, o2, gas) = DockGasMath.Bill(1_000_000, 100_000, 20, 10, 3, 1_000_000);
Check(
    Near(h2, 1_000_000) && Near(o2, 100_000) && gas == 150_000,
    $"H2 and O2, H2 needs more ice: {h2} L H2 + {o2} L O2 for {gas} SC"
);

// O2 needing the larger ice: 100,000 L of H2 is 5,000 kg, 200,000 L of O2 20,000 kg: 60,000 SC
(h2, o2, gas) = DockGasMath.Bill(100_000, 200_000, 20, 10, 3, 1_000_000);
Check(
    Near(h2, 100_000) && Near(o2, 200_000) && gas == 60_000,
    $"H2 and O2, O2 needs more ice: {h2} L H2 + {o2} L O2 for {gas} SC"
);

// Only O2 short: 200,000 L of O2 is 20,000 kg, 60,000 SC, no H2
(h2, o2, gas) = DockGasMath.Bill(0, 200_000, 20, 10, 3, 1_000_000);
Check(
    h2 == 0 && Near(o2, 200_000) && gas == 60_000,
    $"O2 only: {h2} L H2 + {o2} L O2 for {gas} SC"
);

// The generator's ratios count, whatever they are: 40 L of H2 and 5 L of O2 per kg
(h2, o2, gas) = DockGasMath.Bill(400_000, 100_000, 40, 5, 1, 1_000_000);
Check(
    Near(h2, 400_000) && Near(o2, 100_000) && gas == 20_000,
    $"other ratios: {h2} L H2 + {o2} L O2 for {gas} SC"
);

// Short of money: 3,000 SC buy 1,000 kg of ice, 20,000 L of H2 and 10,000 L of O2
(h2, o2, gas) = DockGasMath.Bill(1_000_000, 100_000, 20, 10, 3, 3_000);
Check(
    Near(h2, 20_000) && Near(o2, 10_000) && gas == 3_000,
    $"partial, both: {h2} L H2 + {o2} L O2 for {gas} SC"
);

// Short of money, one gas nearly full: it still gets all it lacks, the other what the ice makes
(h2, o2, gas) = DockGasMath.Bill(1_000_000, 5_000, 20, 10, 3, 3_000);
Check(
    Near(h2, 20_000) && Near(o2, 5_000) && gas == 3_000,
    $"partial, O2 nearly full: {h2} L H2 + {o2} L O2 for {gas} SC"
);

// SE-1523: a player's tanks are filled once 1 % short, a bot's service ship (tanks, ammo) only under 90 %
Check(DockGasMath.Due(98.9, 100, false), "player 98.9 %: fill");
Check(!DockGasMath.Due(99.5, 100, false), "player 99.5 %: no fill");
Check(!DockGasMath.Due(95, 100, true), "service ship 95 %: no fill");
Check(!DockGasMath.Due(0.9, 1, true), "service ship at 90 %: no fill");
Check(DockGasMath.Due(0.89, 1, true), "service ship 89 %: fill");
Check(DockGasMath.Due(26, 30, true), "26 of 30 magazines: top up");
Check(!DockGasMath.Due(27, 30, true), "27 of 30 magazines: no top-up");
Check(!DockGasMath.Due(0, 0, true), "nothing to fill");

Console.WriteLine($"{checks - failures}/{checks} dock gas checks passed");
return failures == 0 ? 0 : 1;
