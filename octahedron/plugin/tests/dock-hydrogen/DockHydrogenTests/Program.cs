using System;
using ServerPlugin;

// Checks the bill for hydrogen at a station connector (ServerPlugin/DockHydrogenMath.cs). Exit code 0 = all pass.
// Usage: dotnet run -c Release --project tests/dock-hydrogen/DockHydrogenTests

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
var (liters, sc) = DockHydrogenMath.Bill(1_000_000, 3, 20, 1_000_000);
Check(Near(liters, 1_000_000) && sc == 150_000, $"a full small tank: {liters} L for {sc} SC");

// SE-1469: ice at 0.01 SC per kg, 10 SC a 1,000 kg lot: the full small tank's 50,000 kg are 500 SC
(liters, sc) = DockHydrogenMath.Bill(1_000_000, 0.01, 20, 1_000_000);
Check(Near(liters, 1_000_000) && sc == 500, $"cheap ice: {liters} L for {sc} SC");

// SE-1497: ice at 0.04 SC per kg sells for 41 SC a lot with the tax: the full small tank is 2,050 SC
(liters, sc) = DockHydrogenMath.Bill(1_000_000, 0.041, 20, 1_000_000);
Check(Near(liters, 1_000_000) && sc == 2_050, $"ice at 41 SC a t: {liters} L for {sc} SC");

// Rounded up to a whole SC: 30 L is 1.5 kg, 4.5 SC
(liters, sc) = DockHydrogenMath.Bill(30, 3, 20, 100);
Check(Near(liters, 30) && sc == 5, $"rounded up: {liters} L for {sc} SC");

// Never free: a few liters cost 1 SC
(liters, sc) = DockHydrogenMath.Bill(0.5, 1, 20, 100);
Check(Near(liters, 0.5) && sc == 1, $"at least 1 SC: {liters} L for {sc} SC");

// No float noise rounds a whole amount up: 20 L at 1 SC is exactly 1 SC
(liters, sc) = DockHydrogenMath.Bill(20, 1, 20, 100);
Check(sc == 1, $"exact amounts stay exact: {sc} SC");

// Short of money: what the balance buys, for all of it
(liters, sc) = DockHydrogenMath.Bill(1_000_000, 3, 20, 3_000);
Check(Near(liters, 20_000) && sc == 3_000, $"partial: {liters} L for {sc} SC");

// Nothing without money, a price or a need
Check(DockHydrogenMath.Bill(1000, 3, 20, 0) == (0, 0), "no money");
Check(DockHydrogenMath.Bill(1000, 0, 20, 100) == (0, 0), "no ice price");
Check(DockHydrogenMath.Bill(0, 3, 20, 100) == (0, 0), "full tanks");

// A balance too small for anything still buys its liters' worth
(liters, sc) = DockHydrogenMath.Bill(1000, 7, 20, 1);
Check(Near(liters, 20.0 / 7) && sc == 1, $"1 SC buys a little: {liters} L for {sc} SC");

Console.WriteLine($"{checks - failures}/{checks} dock hydrogen checks passed");
return failures == 0 ? 0 : 1;
