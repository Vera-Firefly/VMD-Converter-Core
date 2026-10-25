using System.Numerics;
using System.Text.Json;
using UnityHps;

namespace VeraFirefly.VmdConverter.Core.Hps;

/// <summary>
/// Per-bone residual correction on top of the muscle decode (route B of the V04
/// record). The residual log(inv(W_decoded) * W_reference) is modelled as a linear
/// function of the muscle vector; fitting it on a short reference clip removes the
/// systematic part of the decode error (measured: 90-96% of the arm error).
/// </summary>
internal sealed class ResidualModel
{
    private const string SolverVersion = "arm-geometry-torso-v1";
    private readonly Dictionary<int, float[][]> byBone = new();
    private readonly Dictionary<int, (float[] Lo, float[] Hi)> byBoneBox = new();
    private static readonly float boxMargin = ParseMargin();
    private static readonly float boxSigma = ParseSigma();
    private readonly int featureCount;
    // k-NN mode: one shared muscle table plus, per bone, the residual observed at each entry.
    private float[][] knnFrames = [];
    private readonly Dictionary<int, float[][]> knnByBone = new();
    private int knnK = 1;
    private float knnScale = 0.25f;
    // Solve evaluates frames concurrently. Keep the last-query shortcut and its neighbor
    // arrays on the calling thread, so another frame cannot replace them mid-bone.
    private sealed class KnnQueryCache
    {
        internal float[]? LastMuscles;
        internal readonly int[] Neighbors = new int[16];
        internal readonly float[] Weights = new float[16];
        internal int NeighborCount;
        internal float Damping = 1f;
    }

    private readonly System.Threading.ThreadLocal<KnnQueryCache> knnCache =
        new(() => new KnnQueryCache());

    private ResidualModel(int featureCount) => this.featureCount = featureCount;

    internal int BoneCount => byBone.Count + knnByBone.Count;
    internal int Features => featureCount;

    /// <summary>Stable id of the rig a residual model was fitted for: bone count, bone names,
    /// parent indices, rest local transforms and Humanoid mapping.</summary>
    internal static string Fingerprint(Rig rig)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(Material(rig)));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>Exact bytes hashed by <see cref="Fingerprint"/>; exposed for debugging.</summary>
    internal static string Material(Rig rig)
    {
        var text = new System.Text.StringBuilder(rig.bones.Length * 96);
        text.Append(rig.bones.Length).Append('|');
        for (int i = 0; i < rig.bones.Length; i++)
        {
            var bone = rig.bones[i];
            text.Append(bone.name ?? string.Empty).Append(':')
                .Append(bone.parent).Append(':')
                .Append(bone.humanBone).Append(':');
            if (bone.rotation is { Length: >= 4 })
                for (int k = 0; k < 4; k++)
                    text.Append(k == 0 ? "" : ",").Append((int)MathF.Round(bone.rotation[k] * 10000f));
            text.Append(':');
            if (bone.position is { Length: >= 3 })
                for (int k = 0; k < 3; k++)
                    text.Append(k == 0 ? "" : ",").Append((int)MathF.Round(bone.position[k] * 10000f));
            text.Append(';');
        }
        return text.ToString();
    }

    internal static ResidualModel? Load(string path, Rig rig)
    {
        if (!File.Exists(path)) return null;
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("bones", out JsonElement list)) return null;
        string fingerprint = Fingerprint(rig);
        if (!document.RootElement.TryGetProperty("rig", out JsonElement rigInfo) ||
            !rigInfo.TryGetProperty("fingerprint", out JsonElement stored) ||
            stored.GetString() is not { Length: > 0 } expected)
            throw new InvalidDataException(
                "Residual model has no rig fingerprint; re-fit it with the current " +
                "tools/residual_model.py (see Motion/M5-per-avatar标定操作手册.md).");
        if (!string.Equals(expected, fingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Residual model was fitted for a different rig " +
                $"(fingerprint {expected} != {fingerprint}); re-fit it for this avatar " +
                "(see Motion/M5-per-avatar标定操作手册.md).");
        // The solver identity is either a legacy static tag or the sha256 prefix of the
        // UnityHps.Core.dll that produced the base animation. Two binaries can pass the legacy
        // tag while producing different muscle vectors (measured: max 0.77 on channel 92), and
        // pairing the wrong residual with a solver silently costs accuracy (0.59 -> 5.86 deg).
        if (!document.RootElement.TryGetProperty("solver", out JsonElement pipeline) ||
            pipeline.ValueKind != JsonValueKind.String || pipeline.GetString() is not { Length: > 0 } solver)
            throw new InvalidDataException(
                "Residual model has no solver identity; re-fit it with the current " +
                "tools/residual_model.py after regenerating the base animation and muscle CSV.");
        string actualSolver = SolverIdentity();
        if (solver.Length == 16 && IsHex(solver))
        {
            if (!string.Equals(solver, actualSolver, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Residual model was fitted against solver {solver} but the loaded solver is " +
                    $"{actualSolver}; rebuild the model with the shipped binary " +
                    "(tools/calibrate.ps1 records this automatically).");
        }
        else if (solver != SolverVersion)
        {
            throw new InvalidDataException(
                $"Residual model uses solver '{solver}', this build is '{SolverVersion}'; re-fit it.");
        }
        else
            Console.Error.WriteLine(
                $"Residual: model carries the legacy solver tag '{solver}' and was not bound to a " +
                $"binary; this build is {actualSolver}. Re-fit with tools/calibrate.ps1 to bind them.");
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < rig.bones.Length; i++) byName[rig.bones[i].name ?? string.Empty] = i;
        if (document.RootElement.TryGetProperty("kind", out JsonElement kind) &&
            kind.GetString() == "knn-residual")
            return LoadKnn(document.RootElement, byName);
        var parsed = new Dictionary<int, float[][]>();
        var boxes = new Dictionary<int, (float[] Lo, float[] Hi)>();
        int features = 0;
        foreach (JsonElement item in list.EnumerateArray())
        {
            string? name = item.GetProperty("bone").GetString();
            if (name == null || !byName.TryGetValue(name, out int bone)) continue;
            var rows = new float[3][];
            int axis = 0;
            foreach (JsonElement row in item.GetProperty("a").EnumerateArray())
            {
                var values = new float[row.GetArrayLength()];
                int i = 0;
                foreach (JsonElement value in row.EnumerateArray()) values[i++] = value.GetSingle();
                rows[axis++] = values;
                features = Math.Max(features, values.Length);
            }
            parsed[bone] = rows;
            if (item.TryGetProperty("lo", out JsonElement loArray) &&
                item.TryGetProperty("hi", out JsonElement hiArray) &&
                loArray.ValueKind == JsonValueKind.Array && hiArray.ValueKind == JsonValueKind.Array)
            {
                var lo = new float[loArray.GetArrayLength()];
                var hi = new float[hiArray.GetArrayLength()];
                int k = 0;
                foreach (JsonElement value in loArray.EnumerateArray()) lo[k++] = value.GetSingle();
                k = 0;
                foreach (JsonElement value in hiArray.EnumerateArray()) hi[k++] = value.GetSingle();
                if (lo.Length == hi.Length && lo.Length == features) boxes[bone] = (lo, hi);
            }
        }
        if (parsed.Count == 0) return null;
        var model = new ResidualModel(features);
        foreach (var pair in parsed) model.byBone[pair.Key] = pair.Value;
        foreach (var pair in boxes) model.byBoneBox[pair.Key] = pair.Value;
        return model;
    }

    /// <summary>k-NN residual table: a nearest-neighbour model of the same residual the linear
    /// model fits. On Kipfel both behave alike; on Flare the residual is strongly nonlinear and
    /// the linear model stalls at 3.3 deg while nearest-neighbour reaches 0.8 deg (held-out
    /// frames). See Motion/V05-Flare标定失败真因-模型类_V01.md.</summary>
    private static ResidualModel? LoadKnn(JsonElement root, Dictionary<string, int> byName)
    {
        if (!root.TryGetProperty("frames", out JsonElement framesElement) ||
            framesElement.ValueKind != JsonValueKind.Array)
            return null;
        var frames = new List<float[]>(framesElement.GetArrayLength());
        foreach (JsonElement row in framesElement.EnumerateArray())
        {
            var values = new float[row.GetArrayLength()];
            int i = 0;
            foreach (JsonElement value in row.EnumerateArray()) values[i++] = value.GetSingle();
            frames.Add(values);
        }
        if (frames.Count == 0) return null;
        int features = frames[0].Length;
        var model = new ResidualModel(features) { knnFrames = frames.ToArray() };
        if (root.TryGetProperty("k", out JsonElement kElement) && kElement.TryGetInt32(out int k))
            model.knnK = Math.Clamp(k, 1, 16);
        if (root.TryGetProperty("scale", out JsonElement scaleElement) && scaleElement.TryGetSingle(out float scale)
            && scale > 1e-4f)
            model.knnScale = scale;
        if (!root.TryGetProperty("bones", out JsonElement list)) return null;
        foreach (JsonElement item in list.EnumerateArray())
        {
            string? name = item.GetProperty("bone").GetString();
            if (name == null || !byName.TryGetValue(name, out int bone)) continue;
            if (!item.TryGetProperty("r", out JsonElement residualList)) continue;
            var residuals = new float[residualList.GetArrayLength()][];
            int index = 0;
            foreach (JsonElement row in residualList.EnumerateArray())
            {
                var values = new float[row.GetArrayLength()];
                int i = 0;
                foreach (JsonElement value in row.EnumerateArray()) values[i++] = value.GetSingle();
                residuals[index++] = values;
            }
            if (residuals.Length == model.knnFrames.Length) model.knnByBone[bone] = residuals;
        }
        return model.knnByBone.Count == 0 ? null : model;
    }

    /// <summary>Pick the k nearest calibration poses for this muscle vector (cached per frame).</summary>
    private bool Query(float[] muscles, KnnQueryCache cache)
    {
        if (ReferenceEquals(muscles, cache.LastMuscles))
            return cache.NeighborCount > 0 && cache.Damping > 1e-3f;
        cache.LastMuscles = muscles;
        cache.NeighborCount = 0;
        int count = knnFrames.Length;
        if (count == 0) return false;
        int k = Math.Min(knnK, count);
        Span<float> distance = stackalloc float[k];
        Span<int> index = stackalloc int[k];
        for (int i = 0; i < k; i++) { distance[i] = float.MaxValue; index[i] = -1; }
        for (int f = 0; f < count; f++)
        {
            float[] frame = knnFrames[f];
            int limit = Math.Min(frame.Length, muscles.Length);
            float sum = 0f;
            for (int m = 0; m < limit; m++)
            {
                float d = frame[m] - muscles[m];
                sum += d * d;
            }
            if (sum >= distance[k - 1]) continue;
            int slot = k - 1;
            while (slot > 0 && distance[slot - 1] > sum)
            {
                distance[slot] = distance[slot - 1];
                index[slot] = index[slot - 1];
                slot--;
            }
            distance[slot] = sum;
            index[slot] = f;
        }
        if (index[0] < 0) return false;
        // Far outside the calibrated region the correction is faded out instead of extrapolated.
        float nearest = MathF.Sqrt(distance[0]);
        float scaled = nearest / (2f * knnScale);
        cache.Damping = MathF.Exp(-scaled * scaled);
        if (cache.Damping < 1e-3f) return false;
        cache.NeighborCount = k;
        for (int i = 0; i < k && index[i] >= 0; i++)
        {
            cache.Neighbors[i] = index[i];
            cache.Weights[i] = 1f / MathF.Sqrt(distance[i] + 1e-6f);
        }
        return true;
    }

    private Quaternion? RotationKnn(int bone, float[] muscles)
    {
        KnnQueryCache cache = knnCache.Value!;
        if (!Query(muscles, cache)) return null;
        if (!knnByBone.TryGetValue(bone, out float[][]? residuals)) return null;
        float x = 0f, y = 0f, z = 0f, total = 0f;
        for (int i = 0; i < cache.NeighborCount; i++)
        {
            int index = cache.Neighbors[i];
            if (index < 0 || index >= residuals.Length) continue;
            float[] r = residuals[index];
            float w = cache.Weights[i];
            x += r[0] * w;
            y += r[1] * w;
            z += r[2] * w;
            total += w;
        }
        if (total <= 0f) return null;
        x = x / total * cache.Damping;
        y = y / total * cache.Damping;
        z = z / total * cache.Damping;
        float angle = MathF.Sqrt(x * x + y * y + z * z);
        if (angle < 1e-9f) return Quaternion.Identity;
        float half = angle * 0.5f;
        float s = MathF.Sin(half) / angle;
        return new Quaternion(x * s, y * s, z * s, MathF.Cos(half));
    }
    private static float ParseMargin()
    {
        string? text = Environment.GetEnvironmentVariable("VMDCC_RESIDUAL_MARGIN");
        return float.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float value) && value >= 0f
            ? value : 0.1f;
    }

    private static bool IsHex(string text)
    {
        foreach (char c in text)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    /// <summary>sha256 prefix of the UnityHps.Core.dll this process loaded.</summary>
    private static string SolverIdentity()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "UnityHps.Core.dll");
            if (!File.Exists(path)) return "unknown";
            byte[] hash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path));
            return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        }
        catch (IOException) { return "unknown"; }
        catch (UnauthorizedAccessException) { return "unknown"; }
    }

    private static float ParseSigma()
    {
        string? text = Environment.GetEnvironmentVariable("VMDCC_RESIDUAL_SIGMA");
        return float.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float value) && value > 0f
            ? value : 0.35f;
    }

    /// <summary>Residual rotation for one bone, or null when it has no model.</summary>
    internal Quaternion? Rotation(int bone, float[] muscles)
    {
        if (knnFrames.Length > 0) return RotationKnn(bone, muscles);
        if (!byBone.TryGetValue(bone, out float[][]? rows)) return null;
        float weight = 1f;
        // The fitted correction is only trustworthy near the pose range it was fitted on.
        // Far outside it the linear extrapolation is worse than no correction (46 deg on a fast
        // spin), so skip instead of clamp. A small margin (default 0.1 of the muscle range) keeps
        // near-boundary frames corrected: P95 6.51 -> 3.86 deg and G1a 4/8 -> 8/8 on Kipfel.
        if (byBoneBox.TryGetValue(bone, out var box))
        {
            // The box carries the intercept as element 0, so muscle m maps to box element m + 1.
            // Soft weight instead of a hard cut: a pose slightly outside the calibrated range still
            // gets a partial correction, and the weight fades to zero as the excess grows.
            int boxed = Math.Min(box.Lo.Length, box.Hi.Length) - 1;
            boxed = Math.Min(boxed, muscles.Length);
            float worst = 0f;
            for (int m = 0; m < boxed; m++)
            {
                float low = box.Lo[m + 1] - boxMargin;
                float high = box.Hi[m + 1] + boxMargin;
                float span = MathF.Max(high - low, 1e-3f);
                float excess = muscles[m] < low ? (low - muscles[m]) / span
                             : muscles[m] > high ? (muscles[m] - high) / span : 0f;
                if (excess > worst) worst = excess;
            }
            weight = MathF.Exp(-(worst / boxSigma) * (worst / boxSigma));
            if (weight < 1e-3f) return null;
        }
        float x = 0f, y = 0f, z = 0f;
        for (int axis = 0; axis < 3 && axis < rows.Length; axis++)
        {
            float[] weights = rows[axis];
            float sum = weights.Length > 0 ? weights[0] : 0f;
            int limit = Math.Min(muscles.Length, weights.Length - 1);
            for (int m = 0; m < limit; m++) sum += weights[m + 1] * muscles[m];
            if (axis == 0) x = sum; else if (axis == 1) y = sum; else z = sum;
        }
        x *= weight; y *= weight; z *= weight;
        float angle = MathF.Sqrt(x * x + y * y + z * z);
        if (angle < 1e-9f) return Quaternion.Identity;
        float half = angle * 0.5f;
        float s = MathF.Sin(half) / angle;
        return new Quaternion(x * s, y * s, z * s, MathF.Cos(half));
    }
}
