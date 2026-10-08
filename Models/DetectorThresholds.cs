namespace ConfidentialDataMasker.Models;

public sealed class DetectorThresholds
{

    public double StampAspectMin { get; set; } = 0.4;
    public double StampAspectMax { get; set; } = 2.5;

    public double SignatureAspectMin { get; set; } = 1.2;

    public int SignatureMinWidthPx { get; set; } = 36;

    public int SignatureMinHeightPx { get; set; } = 24;

    public int SignatureMinColumnThicknessBlocks { get; set; } = 2;

    public double SignatureMaxFillRatio { get; set; } = 0.6;
}
