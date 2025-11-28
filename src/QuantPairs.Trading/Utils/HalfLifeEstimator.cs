using System;
using System.Linq;

namespace QuantPairs.Analytics.Utils
{
    /// <summary>
    /// Estime le half-life d'une série stationnaire ε_t via AR(1): ε_t = φ ε_{t-1} + u_t.
    /// Half-life = -ln(2) / ln(φ). Retourne null si φ ∉ (0,1) ou si données insuffisantes.
    /// </summary>
    public static class HalfLifeEstimator
    {
        public static double? Estimate(double[] epsilon)
        {
            if (epsilon == null || epsilon.Length < 50) return null;

            // AR(1) OLS: y_t = a + φ x_t, avec y_t = ε_t, x_t = ε_{t-1}
            int n = epsilon.Length - 1;
            double[] x = new double[n];
            double[] y = new double[n];
            for (int t = 1; t < epsilon.Length; t++)
            {
                x[t - 1] = epsilon[t - 1];
                y[t - 1] = epsilon[t];
            }

            double meanX = x.Average();
            double meanY = y.Average();
            double sxx = 0.0, sxy = 0.0;
            for (int i = 0; i < n; i++)
            {
                double dx = x[i] - meanX;
                sxx += dx * dx;
                sxy += dx * (y[i] - meanY);
            }
            if (sxx <= 0) return null;

            double phi = sxy / sxx;            // pente OLS = φ
            if (phi <= 0.0 || phi >= 1.0) return null;

            double hl = -Math.Log(2.0) / Math.Log(phi);
            if (double.IsNaN(hl) || double.IsInfinity(hl)) return null;
            return hl;
        }
    }
}
