using System;

namespace ServerPlugin;

/// <summary>
/// A shipyard's build volume for repair, grind and hack slots (SE-1386): the largest cylinder in
/// the shipyard's free space around its center line, as long as the free space. The free space
/// is a box, and its longest side is the center line. A ship docked at the connectors outside
/// the frame, or a ship towing another in, stays out of it. Plain math, so the tests run it.
/// </summary>
public readonly struct BuildVolume
{
    public readonly double[] Center;

    // Unit vector along the center line
    public readonly double[] Axis;
    public readonly double HalfLength;
    public readonly double Radius;

    public BuildVolume(double[] center, double[] axis, double halfLength, double radius)
    {
        Center = center;
        Axis = axis;
        HalfLength = halfLength;
        Radius = radius;
    }

    /// <summary>The cylinder of a box: <paramref name="axes"/> are its three unit axes, <paramref name="halfExtents"/> its half sides along them.</summary>
    public static BuildVolume OfBox(double[] center, double[][] axes, double[] halfExtents)
    {
        var along = 0;
        for (var i = 1; i < 3; i++)
            if (halfExtents[i] > halfExtents[along])
                along = i;
        var radius = double.MaxValue;
        for (var i = 0; i < 3; i++)
            if (i != along)
                radius = Math.Min(radius, halfExtents[i]);
        return new BuildVolume(center, axes[along], halfExtents[along], radius);
    }

    public bool Contains(double x, double y, double z)
    {
        double dx = x - Center[0],
            dy = y - Center[1],
            dz = z - Center[2];
        var t = dx * Axis[0] + dy * Axis[1] + dz * Axis[2];
        if (Math.Abs(t) > HalfLength)
            return false;
        return dx * dx + dy * dy + dz * dz - t * t <= Radius * Radius;
    }
}
