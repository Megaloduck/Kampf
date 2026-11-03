namespace Kampf.Application.Embeddings;

/// <summary>Small vector helpers; embeddings are compared by cosine similarity.</summary>
public static class VectorMath
{
    /// <summary>Cosine similarity in [-1, 1]; 0 for mismatched or empty vectors.</summary>
    public static float Cosine(float[] a, float[] b)
    {
        if (a.Length == 0 || a.Length != b.Length)
            return 0f;

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        if (normA <= 1e-9 || normB <= 1e-9)
            return 0f;

        return (float)(dot / (Math.Sqrt(normA) * Math.Sqrt(normB)));
    }

    /// <summary>Scales a vector to unit length in place (a zero vector is left alone).</summary>
    public static void NormalizeInPlace(float[] vector)
    {
        double sum = 0;
        for (var i = 0; i < vector.Length; i++)
            sum += vector[i] * vector[i];

        if (sum <= 1e-9)
            return;

        var scale = (float)(1.0 / Math.Sqrt(sum));
        for (var i = 0; i < vector.Length; i++)
            vector[i] *= scale;
    }
}
