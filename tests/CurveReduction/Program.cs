using System.Reflection;
using UnityEngine;
using VeraFirefly.VmdConverter.Core;

var method = typeof(VmdExternalOutputKind).Assembly.GetType("VeraFirefly.VmdConverter.Core.VmdCurveUtility", true)!
    .GetMethod("ReduceQuaternion", BindingFlags.Static | BindingFlags.NonPublic)!;
int[] Reduce(Quaternion[] values, float degrees) => (int[])method.Invoke(null,
    new object[] { new float[] { 0, .5f, 1 }, values, degrees })!;
Quaternion Z(double degrees) => new(0, 0, (float)Math.Sin(degrees * Math.PI / 360), (float)Math.Cos(degrees * Math.PI / 360));
void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS: " + name); }
Check(Reduce(new[] { Z(0), Z(.008), Z(0) }, .001f).SequenceEqual(new[] { 0, 1, 2 }),
    "sub-float-dot angular excursion is retained at the requested tolerance");
Check(Reduce(new[] { Z(0), Z(.0005), Z(0) }, .001f).SequenceEqual(new[] { 0, 2 }),
    "angular excursion below tolerance can be reduced");
var normalized = Z(15); var scaled = new Quaternion(normalized.x * 1.00001f, normalized.y * 1.00001f,
    normalized.z * 1.00001f, normalized.w * 1.00001f);
Check(Reduce(new[] { normalized, scaled, normalized }, .001f).Length == 2,
    "quaternion normalization roundoff does not invent angular error");
Check(Reduce(new[] { Z(0), Z(90), Z(180) }, 0).Length == 3,
    "zero tolerance keeps every quaternion key");
