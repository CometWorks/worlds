using System;
using VRageMath;

namespace ServerPlugin;

// A shipyard's free build space, in world coordinates. Box holds it. Round: the space is the
// cylinder along Box's Z that fills its width, the round volume of a shipyard without an arm,
// the double helix one (SE-959).
internal readonly record struct BuildSpace(MyOrientedBoundingBoxD Box, bool Round)
{
    // Width, height, length
    public Vector3D Size => Box.HalfExtent * 2;

    private double Radius => Box.HalfExtent.X;

    public double Volume => Round ? Math.PI * Radius * Radius * Size.Z : Size.Volume;

    // Whether a ship of this size, width, height and length, fits along the space, centered
    public bool Fits(Vector3D size) =>
        Round
            ? size.Z <= Size.Z && Math.Sqrt(size.X * size.X + size.Y * size.Y) <= 2 * Radius
            : size.X <= Size.X && size.Y <= Size.Y && size.Z <= Size.Z;

    public bool Contains(Vector3D point) => Inside(point, 0);

    // Whether the box of cells, in the grid's local coordinates, reaches into the space. For a
    // round space this goes by the sphere around the box.
    public bool Intersects(BoundingBoxD cells, MatrixD gridWorld)
    {
        if (Round)
            return Inside(Vector3D.Transform(cells.Center, gridWorld), cells.HalfExtents.Length());
        var toGrid = MatrixD.Invert(gridWorld);
        var local = new MyOrientedBoundingBoxD(
            Vector3D.Transform(Box.Center, toGrid),
            Box.HalfExtent,
            Quaternion.CreateFromRotationMatrix(
                MatrixD.CreateFromQuaternion(Box.Orientation) * toGrid
            )
        );
        return local.Intersects(ref cells);
    }

    private bool Inside(Vector3D point, double margin)
    {
        var local = Vector3D.Transform(point - Box.Center, Quaternion.Conjugate(Box.Orientation));
        if (Math.Abs(local.Z) > Box.HalfExtent.Z + margin)
            return false;
        if (Round)
            return local.X * local.X + local.Y * local.Y <= (Radius + margin) * (Radius + margin);
        return Math.Abs(local.X) <= Box.HalfExtent.X + margin
            && Math.Abs(local.Y) <= Box.HalfExtent.Y + margin;
    }

    public override string ToString() =>
        Round
            ? $"{2 * Radius:0} m across (the width and height together) and {Size.Z:0} m long"
            : $"{Size.X:0} x {Size.Y:0} x {Size.Z:0} m";
}
