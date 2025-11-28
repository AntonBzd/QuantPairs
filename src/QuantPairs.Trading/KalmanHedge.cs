using System;

namespace QuantPairs.Trading
{
    /// <summary>
    /// Kalman filter for time-varying hedge ratio β_t (random walk).
    /// Measurement model: y_t = alpha + β_t * x_t + ε_t, ε_t ~ N(0, R)
    /// State: β_t = β_{t-1} + w_t, w_t ~ N(0, Q)
    ///
    /// Use a fixed alpha (from train OLS) for stability; filter only β_t.
    /// Returns the filtered beta path and residuals ε_t.
    /// </summary>
    public static class KalmanHedge
    {
        public sealed record Result(double[] Beta, double[] Residuals);

        public static Result Run(
            double[] y,
            double[] x,
            double alpha,
            double betaInit,
            double pInit,
            double q, // process noise variance
            double r  // measurement noise variance
        )
        {
            int n = Math.Min(y.Length, x.Length);
            var beta = new double[n];
            var resid = new double[n];
            double beta_t = betaInit;
            double P = pInit;

            for (int t = 0; t < n; t++)
            {
                // Predict (β_t|t-1)
                double beta_pred = beta_t;
                double P_pred = P + q;

                // Measurement matrix H_t = x_t
                double H = x[t];

                // Innovation
                double yhat = alpha + beta_pred * H;
                double innov = y[t] - yhat;

                // Innovation covariance
                double S = H * P_pred * H + r;
                if (S <= 1e-16) S = 1e-16;

                // Kalman gain
                double K = (P_pred * H) / S;

                // Update (numerically stable form)
                beta_t = beta_pred + K * innov;
                P = Math.Max(1e-12, (1.0 - K * H) * P_pred);

                // Store
                beta[t] = beta_t;
                resid[t] = y[t] - (alpha + beta_t * x[t]);
            }

            return new Result(beta, resid);
        }
    }
}