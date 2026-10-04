using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace ScheduleWidget
{
    /// <summary>
    /// CSS-style cubic-bezier(x1, y1, x2, y2) timing curve for WPF animations: the curve starts at (0,0), ends at (1,1) and
    /// is shaped by the two control points. Time is the curve's x, progress its y. Used by the mini calendar's page flip
    /// (ease-out (0.22, 1, 0.36, 1) for the unfolding page and the day-card cascade, ease-in-out (0.45, 0, 0.55, 1) for the
    /// page lifting away).
    /// </summary>
    public sealed class CubicBezierEase : EasingFunctionBase
    {
        public static readonly DependencyProperty X1Property = DependencyProperty.Register(nameof(X1), typeof(double), typeof(CubicBezierEase), new PropertyMetadata(0.25));
        public static readonly DependencyProperty Y1Property = DependencyProperty.Register(nameof(Y1), typeof(double), typeof(CubicBezierEase), new PropertyMetadata(0.1));
        public static readonly DependencyProperty X2Property = DependencyProperty.Register(nameof(X2), typeof(double), typeof(CubicBezierEase), new PropertyMetadata(0.25));
        public static readonly DependencyProperty Y2Property = DependencyProperty.Register(nameof(Y2), typeof(double), typeof(CubicBezierEase), new PropertyMetadata(1.0));

        public double X1 { get => (double)GetValue(X1Property); set => SetValue(X1Property, value); }
        public double Y1 { get => (double)GetValue(Y1Property); set => SetValue(Y1Property, value); }
        public double X2 { get => (double)GetValue(X2Property); set => SetValue(X2Property, value); }
        public double Y2 { get => (double)GetValue(Y2Property); set => SetValue(Y2Property, value); }

        public CubicBezierEase() { EasingMode = EasingMode.EaseIn; } // EaseIn = the curve exactly as given (EaseOut would mirror it)

        public CubicBezierEase(double x1, double y1, double x2, double y2) : this()
        {
            X1 = x1; Y1 = y1; X2 = x2; Y2 = y2;
        }

        protected override double EaseInCore(double normalizedTime) => Value(X1, Y1, X2, Y2, normalizedTime);

        protected override Freezable CreateInstanceCore() => new CubicBezierEase();

        /// <summary>
        /// Progress (y) of cubic-bezier(x1, y1, x2, y2) at time x in [0, 1]. The curve's parameter for x is found with
        /// Newton-Raphson (fast, the usual case) and falls back to bisection where the slope is too flat for Newton.
        /// </summary>
        public static double Value(double x1, double y1, double x2, double y2, double x)
        {
            if (double.IsNaN(x) || x <= 0) return 0;
            if (x >= 1) return 1;
            // A curve with a missing / infinite control value (e.g. set from XAML) runs linearly instead of producing NaN.
            if (!IsFinite(x1) || !IsFinite(y1) || !IsFinite(x2) || !IsFinite(y2)) return x;
            x1 = Math.Max(0, Math.Min(1, x1)); // x must stay a function of time (control x in [0, 1], as in CSS)
            x2 = Math.Max(0, Math.Min(1, x2));
            // B(s) = ((a·s + b)·s + c)·s for each axis (P0 = 0, P3 = 1).
            double cx = 3 * x1, bx = 3 * (x2 - x1) - cx, ax = 1 - cx - bx;
            double cy = 3 * y1, by = 3 * (y2 - y1) - cy, ay = 1 - cy - by;
            double SampleX(double s) => ((ax * s + bx) * s + cx) * s;
            double SlopeX(double s) => (3 * ax * s + 2 * bx) * s + cx;

            double t = x;
            for (int i = 0; i < 8; i++)
            {
                double error = SampleX(t) - x;
                if (Math.Abs(error) < 1e-7) return ((ay * t + by) * t + cy) * t;
                double slope = SlopeX(t);
                if (Math.Abs(slope) < 1e-6) break;
                t -= error / slope;
            }
            double lo = 0, hi = 1;
            t = x;
            for (int i = 0; i < 60; i++)
            {
                double value = SampleX(t);
                if (Math.Abs(value - x) < 1e-7) break;
                if (value < x) lo = t; else hi = t;
                t = (lo + hi) / 2;
            }
            return ((ay * t + by) * t + cy) * t;
        }

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
