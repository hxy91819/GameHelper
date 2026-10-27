namespace GameHelper.Core.Abstractions;

/// <summary>Manual HDR control for the primary source only, independent of automation display selection.</summary>
public interface IPrimaryDisplayHdrController
{
    TrayHdrState ReadPrimary();
    void TogglePrimary();
}
