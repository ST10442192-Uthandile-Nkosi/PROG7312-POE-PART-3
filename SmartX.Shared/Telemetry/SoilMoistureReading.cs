namespace SmartX.Shared.Telemetry;

/// <summary>
/// Identifies volumetric water content from the packet unit. Conductivity (mS/cm) and other
/// environmental readings are not soil moisture, even when the value is below the VWC band
/// or the display name mentions moisture.
/// </summary>
public static class SoilMoistureReading
{
    public static bool IsVolumetricWaterContent(string? unit) =>
        unit?.Contains("VWC", StringComparison.OrdinalIgnoreCase) == true;
}
