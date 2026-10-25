using System.Numerics;

namespace UnityHps;

public static class Math3
{
    public const float Degrees = 180 / MathF.PI;
    public static Vector3 V(float[] a, int offset = 0) => new(a[offset], a[offset + 1], a[offset + 2]);
    public static Quaternion Q(float[] a, int offset = 0)
    {
        var q = new Quaternion(a[offset], a[offset + 1], a[offset + 2], a[offset + 3]);
        if (!float.IsFinite(q.LengthSquared()) || q.LengthSquared() < 1e-12f)
            throw new InvalidDataException("Non-finite or zero quaternion.");
        return Quaternion.Normalize(q);
    }
    public static float[] A(Vector3 v) => [v.X, v.Y, v.Z];
    public static float[] A(Quaternion q) => [q.X, q.Y, q.Z, q.W];
    public static Vector3 Rotate(Quaternion q, Vector3 v) => Vector3.Transform(v, q);
    public static Vector3 Log(Quaternion q)
    {
        q = Quaternion.Normalize(q);
        if (q.W < 0) q = -q;
        float length = MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z);
        if (length < 1e-8f) return new Vector3(q.X, q.Y, q.Z) * 2;
        return new Vector3(q.X, q.Y, q.Z) * (2 * MathF.Atan2(length, q.W) / length);
    }
    public static float Angle(Quaternion a, Quaternion b) => Log(Quaternion.Inverse(a) * b).Length() * Degrees;
    public static Quaternion Exp(Vector3 v)
    {
        float length = v.Length();
        return length < 1e-8f ? Quaternion.Normalize(new Quaternion(v * 0.5f, 1)) : Quaternion.CreateFromAxisAngle(v / length, length);
    }
    public static Quaternion FromTo(Vector3 a, Vector3 b)
    {
        if (a.LengthSquared() < 1e-16f || b.LengthSquared() < 1e-16f)
            return Quaternion.Identity;
        double ax = a.X, ay = a.Y, az = a.Z, bx = b.X, by = b.Y, bz = b.Z;
        double length = Math.Sqrt((ax * ax + ay * ay + az * az) * (bx * bx + by * by + bz * bz));
        double x = (ay * bz - az * by) / length, y = (az * bx - ax * bz) / length,
            z = (ax * by - ay * bx) / length;
        double dot = Math.Clamp((ax * bx + ay * by + az * bz) / length, -1, 1);
        if (dot >= 0)
            return Quaternion.Normalize(new Quaternion((float)x, (float)y, (float)z, (float)(1 + dot)));
        double sine = Math.Sqrt(x * x + y * y + z * z);
        // Use a stable half-angle near 180 degrees rather than 1+dot.
        // Double intermediates also retain the cross-product axis when nearly
        // opposite float vectors would lose it to cancellation.
        if (sine > 1e-12)
        {
            double twiceSineHalf = Math.Sqrt(2 * (1 - dot));
            double scale = twiceSineHalf / (2 * sine);
            return Quaternion.Normalize(new Quaternion((float)(x * scale), (float)(y * scale),
                (float)(z * scale), (float)(sine / twiceSineHalf)));
        }
        if (dot < 0)
        {
            a = Vector3.Normalize(a);
            Vector3 axis = Vector3.Cross(a, MathF.Abs(a.X) < 0.8f ? Vector3.UnitX : Vector3.UnitY);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }
        return Quaternion.Identity;
    }
    public static Quaternion LookRotation(Vector3 forward, Vector3 up)
    {
        forward = Vector3.Normalize(forward);
        Vector3 right = Vector3.Normalize(Vector3.Cross(up, forward));
        up = Vector3.Cross(forward, right);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(
            right.X, right.Y, right.Z, 0, up.X, up.Y, up.Z, 0,
            forward.X, forward.Y, forward.Z, 0, 0, 0, 0, 1)));
    }
    internal static void Put(float[] output, int offset, Vector3 v)
    { output[offset] = v.X; output[offset + 1] = v.Y; output[offset + 2] = v.Z; }
    internal static void Put(float[] output, int offset, Quaternion q)
    { output[offset] = q.X; output[offset + 1] = q.Y; output[offset + 2] = q.Z; output[offset + 3] = q.W; }
}
