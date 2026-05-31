using System;
using UnityEngine;

[CreateAssetMenu(fileName = "WeldingEvaluationConfig", menuName = "Welding/Evaluation Config")]
public class WeldingEvaluationConfig : ScriptableObject
{
    [Serializable]
    public class ElectrodeProfile
    {
        [Tooltip("e.g. E6013 3/32\"")]
        public string code;

        [Tooltip("Electrode diameter in mm")]
        public float diameterMm;

        [Tooltip("Time to consume a 25 cm rack at 100 % quality (seconds)")]
        public float baseConsumptionSeconds;

        [Tooltip("Minimum valid arc gap in mm")]
        public float arcMinMm;

        [Tooltip("Maximum valid arc gap in mm")]
        public float arcMaxMm;

        // ── Dynamic thresholds (vary per electrode) ──────────────────────────

        [Tooltip("Laser height uniformity tolerance in mm (criteria 2 and 5)")]
        public float uniformityToleranceMm;

        [Tooltip("Laser start-vs-end threshold for cylinder closure in mm (criterion 10)")]
        public float continuityClosureThresholdMm;

        // ── Electrode consumption (used by ElectrodeConsumer) ────────────────

        [Tooltip("Total visual electrode length in mm (capacity unit for the progress bar and 3-D scaling). " +
                 "Larger electrodes have more material so this should increase with diameter.")]
        public float totalLengthMm;

        [Tooltip("Rate at which the electrode is consumed while the arc is active (mm per second). " +
                 "Thicker electrodes melt faster in absolute mm/s but carry more material.")]
        public float consumptionRateMmPerSec;
    }

    // ── Electrode profiles (E6013 3/32\", 1/8\", 5/32\") ──────────────────────

    [Header("Electrode Profiles")]
    public ElectrodeProfile[] electrodes =
    {
        new ElectrodeProfile
        {
            code = "E6013 3/32\"", diameterMm = 2.4f, baseConsumptionSeconds = 47f,
            arcMinMm = 2f, arcMaxMm = 3f,
            uniformityToleranceMm = 1.5f, continuityClosureThresholdMm = 2f
        },
        new ElectrodeProfile
        {
            code = "E6013 1/8\"",  diameterMm = 3.2f, baseConsumptionSeconds = 56f,
            arcMinMm = 3f, arcMaxMm = 4f,
            uniformityToleranceMm = 2.0f, continuityClosureThresholdMm = 3f
        },
        new ElectrodeProfile
        {
            code = "E6013 5/32\"", diameterMm = 4.0f, baseConsumptionSeconds = 89f,
            arcMinMm = 4f, arcMaxMm = 5f,
            uniformityToleranceMm = 2.5f, continuityClosureThresholdMm = 4f
        }
    };

    // ── Fixed thresholds (same for all electrodes) ────────────────────────────

    [Header("Fixed Thresholds")]

    [Tooltip("Max yaw deviation from start for a straight bead (criterion 1)")]
    public float straightnessMaxDeviationDeg = 5f;

    [Tooltip("Target work angle for T-joint (criterion 3)")]
    public float workAngle45Deg = 45f;

    [Tooltip("Tolerance around 45° work angle (criterion 3)")]
    public float workAngle45ToleranceDeg = 5f;

    [Tooltip("Target electrode angle for wedge joint (criterion 4)")]
    public float wedgeAngleDeg = 13f;

    [Tooltip("Tolerance around 13° wedge angle (criterion 4)")]
    public float wedgeAngleToleranceDeg = 3f;

    [Tooltip("Gap time in ms that counts as a continuity pause (criteria 6 and 7)")]
    public float continuityPauseThresholdMs = 500f;

    [Tooltip("Number of interruptions allowed before criterion 7 fails")]
    public int maxInterruptionsBeforeFail = 2;

    [Tooltip("Score points deducted per pause for criterion 6 (continuity)")]
    public float c6PenaltyPerPause = 34f;

    [Tooltip("Score points deducted per extra interruption (beyond max) for criterion 7")]
    public float c7PenaltyPerInterruption = 33f;

    [Tooltip("Allowed closure error in degrees for 360° bead (criterion 8)")]
    public float circumferentialClosureToleranceDeg = 10f;

    [Tooltip("Max pitch variation across the full curve pass (criterion 9)")]
    public float curveAngleVariationMaxDeg = 5f;

    // ── P2-T Fillet bead thresholds ───────────────────────────────────────────

    [Header("P2-T Fillet Bead Thresholds")]

    [Tooltip("Average bead deviation from the seam guide for a 100% position score (world metres). " +
             "With lossyScale=15 on Figura T, 10 mm world ≈ 0.010 m.")]
    public float beadPositionPerfectM = 0.010f;

    [Tooltip("Average bead deviation from the seam at which the position score = 0%.")]
    public float beadPositionFailM    = 0.040f;

    [Tooltip("Seam coverage fraction for a 100% coverage score (0.80 = 80% of seam covered).")]
    public float beadCoverageFullFraction = 0.80f;

    [Tooltip("Seam coverage fraction below which the coverage score = 0%.")]
    public float beadCoverageMinFraction  = 0.30f;

    [Tooltip("Max perpendicular deviation from a straight line for 100% straightness score (world metres).")]
    public float beadStraightnessPerfectM = 0.010f;

    [Tooltip("Perpendicular deviation at which the straightness score = 0%.")]
    public float beadStraightnessFailM    = 0.040f;

    [Tooltip("Ideal travel speed in mm/s for a P2-T fillet bead.")]
    public float travelSpeedIdealMmPerSec      = 3.0f;

    [Tooltip("±Tolerance around the ideal speed before the score is reduced.")]
    public float travelSpeedToleranceMmPerSec  = 1.5f;

    [Tooltip("Speed error (beyond tolerance) that yields a 0% travel-speed score.")]
    public float travelSpeedFailMmPerSec       = 4.5f;

    // ── Helpers ───────────────────────────────────────────────────────────────

    public ElectrodeProfile GetElectrode(int index)
    {
        if (electrodes == null || electrodes.Length == 0) return null;
        return electrodes[Mathf.Clamp(index, 0, electrodes.Length - 1)];
    }

    /// <summary>
    /// Returns the expected electrode consumption time for the given quality.
    /// Formula derived from table: factor = 2 - quality/100; time = base / factor.
    /// 100% quality → factor 1.0 (full time). 0% quality → factor 2.0 (half time).
    /// </summary>
    public float ComputeConsumptionTime(int electrodeIndex, float qualityPct)
    {
        var e = GetElectrode(electrodeIndex);
        if (e == null) return 0f;
        var factor = 2f - Mathf.Clamp01(qualityPct / 100f);
        return e.baseConsumptionSeconds / factor;
    }
}
