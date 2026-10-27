namespace GameHelper.Core.Abstractions;

/// <summary>Primary-display HDR presentation and atomic manual changes protected by launch ownership.</summary>
public interface ITrayHdrControl
{
    TrayHdrState Read();
    void Toggle();
}
