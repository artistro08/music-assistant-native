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

            var dt = (double)(timeAdded - lastUpdate);
            lastUpdate = timeAdded;

            var updateStdDev        = maxError * MaxErrorScale;
            var measurementVariance = updateStdDev * updateStdDev;

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
            var predictedOffset = offset + drift * dt;
            var dtSquared       = dt * dt;
            var newDriftCovariance       = driftCovariance + dt * driftProcessVariance;
            var newOffsetDriftCovariance = offsetDriftCovariance + driftCovariance * dt;
            var newOffsetCovariance      = offsetCovariance + 2 * offsetDriftCovariance * dt + driftCovariance * dtSquared + dt * processVariance;

            // Innovation, with adaptive forgetting once there is history
            var residual = measurement - predictedOffset;
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
            var uncertainty = 1.0 / Math.Max(newOffsetCovariance + measurementVariance, 1e-9);
            var offsetGain  = newOffsetCovariance * uncertainty;
            var driftGain   = newOffsetDriftCovariance * uncertainty;

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

    /// <summary>T_server = T_client + offset + drift * (T_client - T_last_update).</summary>
    public long ComputeServerTime(long clientTime)
    {
        lock (gate)
        {
            var effectiveDrift = elementUseDrift ? elementDrift : 0.0;
            var dt = (double)(clientTime - elementLastUpdate);
            return clientTime + (long)Math.Round(elementOffset + effectiveDrift * dt);
        }
    }

    /// <summary>Inverse of ComputeServerTime.</summary>
    public long ComputeClientTime(long serverTime)
    {
        lock (gate)
        {
            var effectiveDrift = elementUseDrift ? elementDrift : 0.0;
            return (long)Math.Round((serverTime - elementOffset + effectiveDrift * elementLastUpdate) / (1.0 + effectiveDrift));
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
