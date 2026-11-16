namespace PeakPill.UI;

/// <summary>
/// A damped spring like SwiftUI's <c>.spring(response:dampingFraction:)</c>. Changing <see cref="Target"/> mid-flight
/// keeps the current speed, so an interrupted animation never jerks – it just bends towards the new goal.
/// </summary>
public sealed class Spring
{
    /// <summary>The island's feel (approved 2026-11-14): 0.50 s per swing, damping 0.70 (about 5 % overshoot).</summary>
    public const double IslandResponse = 0.50, IslandDamping = 0.70;

    public double Value, Velocity, Target;
    public double Response, Damping;
    private readonly double _eps;

    public Spring(double value, double response = IslandResponse, double damping = IslandDamping, double epsilon = 0.05)
    {
        Value = Target = value;
        Response = response;
        Damping = damping;
        _eps = epsilon;
    }

    public bool Settled => Math.Abs(Value - Target) < _eps && Math.Abs(Velocity) < _eps * 4;

    public void Snap(double v) { Value = Target = v; Velocity = 0; }

    /// <summary>Advances the spring by <paramref name="dt"/> seconds (fixed 4 ms sub-steps keep it stable).</summary>
    public void Step(double dt)
    {
        if (Settled) { Value = Target; Velocity = 0; return; }
        double k = Math.Pow(2 * Math.PI / Response, 2), c = 4 * Math.PI * Damping / Response;
        int n = Math.Max(1, (int)Math.Ceiling(dt / 0.004));
        double h = dt / n;
        for (int i = 0; i < n; i++)
        {
            double a = -k * (Value - Target) - c * Velocity;
            Velocity += a * h;
            Value += Velocity * h;
        }
    }
}
