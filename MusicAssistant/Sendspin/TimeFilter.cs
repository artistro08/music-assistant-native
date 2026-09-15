namespace MusicAssistant.Sendspin;

/// <summary>
/// Two-dimensional Kalman filter mapping this PC's clock to the server's.
///
/// Port of aiosendspin's time_sync.py (itself a port of the ESPHome
/// implementation), which the specification names as the reference. State is
/// [offset, drift]; each NTP-style measurement corrects it, with adaptive
/// forgetting so a network hiccup or a server clock step converges again quickly.
/// All times are microseconds.
/// </summary>
/// <remarks>
/// @link https://github.com/Sendspin/aiosendspin/blob/main/aiosendspin/client/time_sync.py
/// </remarks>
public sealed class TimeFilter
{
    private const double AdaptiveForgettingCutoff        = 3.0;
    private const double MaxErrorScale                   = 0.5;
    private const double DriftSignificanceThresholdSquared = 2.0 * 2.0;

    private readonly double processVariance;
    private readonly double driftProcessVariance;
    private readonly double forgetVarianceFactor;

    private readonly object gate = new();
    private long   lastUpdate;
    private int    count;
    private double offset;
    private double drift;
    private double offsetCovariance = double.PositiveInfinity;
    private double offsetDriftCovariance;
    private double driftCovariance;

    // Snapshot read by the conversion functions
    private long   elementLastUpdate;
    private double elementOffset;
    private double elementDrift;
    private bool   elementUseDrift;

    public TimeFilter(double processStdDev = 0.0, double forgetFactor = 2.0, double driftProcessStdDev = 1e-11)
    {
        processVariance      = processStdDev * processStdDev;
        driftProcessVariance = driftProcessStdDev * driftProcessStdDev;
        forgetVarianceFactor = forgetFactor * forgetFactor;
    }

    public int    Count          { get { lock (gate) return count; } }
    public bool   IsSynchronized { get { lock (gate) return count >= 2 && !double.IsInfinity(offsetCovariance); } }
    public double Offset         { get { lock (gate) return offset; } }
    public double Drift          { get { lock (gate) return drift; } }
    /// <summary>Standard deviation of the offset estimate, in microseconds.</summary>
    public double Error          { get { lock (gate) return Math.Sqrt(offsetCovariance); } }

    /// <param name="measurement">((T2 - T1) + (T3 - T4)) / 2</param>
    /// <param name="maxError">((T4 - T1) - (T3 - T2)) / 2, half the round trip</param>
    /// <param name="timeAdded">client time the sample was taken (T4)</param>
    public void Update(double measurement, double maxError, long timeAdded)
    {
        lock (gate)
        {
            if (timeAdded <= lastUpdate) return;

            double dt = (double)(timeAdded - lastUpdate);
            lastUpdate = timeAdded;

            double updateStdDev        = maxError * MaxErrorScale;
            double measurementVariance = updateStdDev * updateStdDev;

            if (count <= 0)
            {
                count++;
                offset           = measurement;
                offsetCovariance = measurementVariance;
                drift            = 0;
                Publish(false);
                return;
            }

            if (count == 1)
            {
                count++;
                drift            = (measurement - offset) / dt;
                offset           = measurement;
                driftCovariance  = (offsetCovariance + measurementVariance) / (dt * dt);
                offsetCovariance = measurementVariance;
                Publish(false);
                return;
            }

            // Predict
            double predictedOffset = offset + drift * dt;
            double dtSquared       = dt * dt;
            double newDriftCovariance       = driftCovariance + dt * driftProcessVariance;
            double newOffsetDriftCovariance = offsetDriftCovariance + driftCovariance * dt;
            double newOffsetCovariance      = offsetCovariance + 2 * offsetDriftCovariance * dt + driftCovariance * dtSquared + dt * processVariance;

            double residual = measurement - predictedOffset;

            // Adaptive forgetting once there is history
            if (count < 100)
            {
                count++;
            }
            else if (Math.Abs(residual) > maxError * AdaptiveForgettingCutoff)
            {
                newDriftCovariance       *= forgetVarianceFactor;
                newOffsetDriftCovariance *= forgetVarianceFactor;
                newOffsetCovariance      *= forgetVarianceFactor;
            }

            // Correct
            double uncertainty = 1.0 / Math.Max(newOffsetCovariance + measurementVariance, 1e-9);
            double offsetGain  = newOffsetCovariance * uncertainty;
            double driftGain   = newOffsetDriftCovariance * uncertainty;

            offset = predictedOffset + offsetGain * residual;
            drift += driftGain * residual;

            driftCovariance       = newDriftCovariance - driftGain * newOffsetDriftCovariance;
            offsetDriftCovariance = newOffsetDriftCovariance - driftGain * newOffsetCovariance;
            offsetCovariance      = newOffsetCovariance - offsetGain * newOffsetCovariance;

            Publish(drift * drift > DriftSignificanceThresholdSquared * driftCovariance);
        }
    }

    private void Publish(bool useDrift)
    {
        elementLastUpdate = lastUpdate;
        elementOffset     = offset;
        elementDrift      = drift;
        elementUseDrift   = useDrift;
    }

    // Real clocks drift far under this; a larger fitted value is relay-jitter noise. Both transforms use the
    // capped value AND anchor the drift term at the last update (drift * elapsed, never drift * absolute clock),
    // so offset and drift stay a consistent inverse pair regardless of the cap.
    private const double MaxDrift = 150e-6;

    private double EffectiveDrift => elementUseDrift ? Math.Clamp(elementDrift, -MaxDrift, MaxDrift) : 0.0;

    /// <summary>T_server = T_client + offset + drift * (T_client - T_last_update).</summary>
    public long ComputeServerTime(long clientTime)
    {
        lock (gate)
        {
            double dt = (double)(clientTime - elementLastUpdate);
            return clientTime + (long)Math.Round(elementOffset + EffectiveDrift * dt);
        }
    }

    /// <summary>Inverse of ComputeServerTime, with the drift term anchored at the last update.</summary>
    public long ComputeClientTime(long serverTime)
    {
        lock (gate)
        {
            double drift = EffectiveDrift;
            // Solve server = client + offset + drift*(client - lastUpdate) for client, expressed so the drift
            // term is drift*(elapsed) rather than drift*(absolute clock), which stays exact under the cap.
            double elapsed = serverTime - elementOffset - elementLastUpdate;
            return serverTime - (long)Math.Round(elementOffset + drift * elapsed);
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            count = 0;
            lastUpdate = 0;
            offset = drift = 0;
            offsetCovariance = double.PositiveInfinity;
            offsetDriftCovariance = driftCovariance = 0;
            Publish(false);
        }
    }
}
