using System;
using NumericsQuaternion = System.Numerics.Quaternion;

// This small compatibility layer allows the managed PMX/VMD parser and IK solver to run
// without loading Unity. The worker never creates a Unity Avatar or AnimationClip.
namespace UnityEngine
{
    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
    }

    public struct Vector4
    {
        public float x, y, z, w;
        public Vector4(float x, float y, float z, float w)
        { this.x = x; this.y = y; this.z = z; this.w = w; }
    }

    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a = 1f)
        { this.r = r; this.g = g; this.b = b; this.a = a; }
    }

    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z)
        { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => new(0f, 0f, 0f);
        public static Vector3 right => new(1f, 0f, 0f);
        public static Vector3 up => new(0f, 1f, 0f);
        public static Vector3 forward => new(0f, 0f, 1f);
        public float sqrMagnitude => x * x + y * y + z * z;
        public Vector3 normalized
        {
            get { float length = MathF.Sqrt(sqrMagnitude); return length > 1e-8f ? this / length : zero; }
        }
        public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator -(Vector3 a) => new(-a.x, -a.y, -a.z);
        public static Vector3 operator *(Vector3 a, float value) => new(a.x * value, a.y * value, a.z * value);
        public static Vector3 operator *(float value, Vector3 a) => a * value;
        public static Vector3 operator /(Vector3 a, float value) => new(a.x / value, a.y / value, a.z / value);
        public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static Vector3 Cross(Vector3 a, Vector3 b) =>
            new(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
        public static Vector3 Min(Vector3 a, Vector3 b) =>
            new(MathF.Min(a.x, b.x), MathF.Min(a.y, b.y), MathF.Min(a.z, b.z));
        public static Vector3 Max(Vector3 a, Vector3 b) =>
            new(MathF.Max(a.x, b.x), MathF.Max(a.y, b.y), MathF.Max(a.z, b.z));
    }

    public struct Quaternion
    {
        public float x, y, z, w;
        public Quaternion(float x, float y, float z, float w)
        { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static Quaternion identity => new(0f, 0f, 0f, 1f);
        public static Quaternion operator *(Quaternion a, Quaternion b)
        {
            NumericsQuaternion q = NumericsQuaternion.Multiply(ToNumerics(a), ToNumerics(b));
            return FromNumerics(q);
        }
        public static Vector3 operator *(Quaternion rotation, Vector3 point)
        {
            var q = ToNumerics(rotation);
            var transformed = System.Numerics.Vector3.Transform(
                new System.Numerics.Vector3(point.x, point.y, point.z), q);
            return new Vector3(transformed.X, transformed.Y, transformed.Z);
        }
        public static Quaternion Inverse(Quaternion value) =>
            FromNumerics(NumericsQuaternion.Inverse(ToNumerics(value)));
        public static Quaternion SlerpUnclamped(Quaternion a, Quaternion b, float t) =>
            FromNumerics(NumericsQuaternion.Slerp(ToNumerics(a), ToNumerics(b), t));
        public static Quaternion AngleAxis(float angle, Vector3 axis)
        {
            Vector3 unit = axis.normalized;
            if (unit.sqrMagnitude < 1e-12f) return identity;
            float half = angle * Mathf.Deg2Rad * 0.5f;
            float sin = MathF.Sin(half);
            return new Quaternion(unit.x * sin, unit.y * sin, unit.z * sin, MathF.Cos(half));
        }
        public static Quaternion Euler(float x, float y, float z) =>
            AngleAxis(y, Vector3.up) * AngleAxis(x, Vector3.right) * AngleAxis(z, Vector3.forward);
        public static Quaternion Euler(Vector3 angles) => Euler(angles.x, angles.y, angles.z);
        public static Quaternion LookRotation(Vector3 forward, Vector3 upwards)
        {
            Vector3 z = forward.normalized;
            if (z.sqrMagnitude < 1e-12f) return identity;
            Vector3 x = Vector3.Cross(upwards, z).normalized;
            if (x.sqrMagnitude < 1e-12f)
                x = Vector3.Cross(MathF.Abs(z.y) < 0.99f ? Vector3.up : Vector3.right, z).normalized;
            Vector3 y = Vector3.Cross(z, x);
            float m00 = x.x, m01 = y.x, m02 = z.x;
            float m10 = x.y, m11 = y.y, m12 = z.y;
            float m20 = x.z, m21 = y.z, m22 = z.z;
            float trace = m00 + m11 + m22;
            if (trace > 0f)
            {
                float s = MathF.Sqrt(trace + 1f) * 2f;
                return new Quaternion((m21 - m12) / s, (m02 - m20) / s,
                    (m10 - m01) / s, s * 0.25f);
            }
            if (m00 > m11 && m00 > m22)
            {
                float s = MathF.Sqrt(1f + m00 - m11 - m22) * 2f;
                return new Quaternion(s * 0.25f, (m01 + m10) / s,
                    (m02 + m20) / s, (m21 - m12) / s);
            }
            if (m11 > m22)
            {
                float s = MathF.Sqrt(1f + m11 - m00 - m22) * 2f;
                return new Quaternion((m01 + m10) / s, s * 0.25f,
                    (m12 + m21) / s, (m02 - m20) / s);
            }
            else
            {
                float s = MathF.Sqrt(1f + m22 - m00 - m11) * 2f;
                return new Quaternion((m02 + m20) / s, (m12 + m21) / s,
                    s * 0.25f, (m10 - m01) / s);
            }
        }
        public void ToAngleAxis(out float angle, out Vector3 axis)
        {
            NumericsQuaternion q = NumericsQuaternion.Normalize(ToNumerics(this));
            float w = Math.Clamp(q.W, -1f, 1f);
            angle = 2f * MathF.Acos(w) * Mathf.Rad2Deg;
            float sine = MathF.Sqrt(MathF.Max(0f, 1f - w * w));
            axis = sine > 1e-6f ? new Vector3(q.X / sine, q.Y / sine, q.Z / sine) : Vector3.right;
        }
        public Vector3 eulerAngles
        {
            get
            {
                Vector3 right = this * Vector3.right;
                Vector3 up = this * Vector3.up;
                Vector3 forward = this * Vector3.forward;
                float xAngle = MathF.Asin(Math.Clamp(-forward.y, -1f, 1f));
                float cosine = MathF.Cos(xAngle);
                float yAngle = MathF.Abs(cosine) > 1e-5f
                    ? MathF.Atan2(forward.x, forward.z)
                    : MathF.Atan2(-right.z, right.x);
                float zAngle = MathF.Abs(cosine) > 1e-5f
                    ? MathF.Atan2(right.y, up.y)
                    : 0f;
                return new Vector3(PositiveDegrees(xAngle), PositiveDegrees(yAngle),
                    PositiveDegrees(zAngle));
            }
        }
        private static float PositiveDegrees(float radians)
        {
            float degrees = radians * Mathf.Rad2Deg % 360f;
            return degrees < 0f ? degrees + 360f : degrees;
        }
        private static NumericsQuaternion ToNumerics(Quaternion value) =>
            new(value.x, value.y, value.z, value.w);
        private static Quaternion FromNumerics(NumericsQuaternion value) =>
            new(value.X, value.Y, value.Z, value.W);
    }

    public static class Mathf
    {
        public const float PI = MathF.PI;
        public const float Rad2Deg = 180f / PI;
        public const float Deg2Rad = PI / 180f;
        public static float Abs(float value) => MathF.Abs(value);
        public static float Acos(float value) => MathF.Acos(value);
        public static float Atan2(float y, float x) => MathF.Atan2(y, x);
        public static float Sqrt(float value) => MathF.Sqrt(value);
        public static float Sign(float value) => value >= 0f ? 1f : -1f;
        public static float Min(float a, float b) => MathF.Min(a, b);
        public static float Max(float a, float b) => MathF.Max(a, b);
        public static float Clamp(float value, float minimum, float maximum) =>
            Math.Clamp(value, minimum, maximum);
        public static int Clamp(int value, int minimum, int maximum) =>
            Math.Clamp(value, minimum, maximum);
        public static float Clamp01(float value) => Math.Clamp(value, 0f, 1f);
        public static float LerpUnclamped(float a, float b, float t) => a + (b - a) * t;
    }
}
