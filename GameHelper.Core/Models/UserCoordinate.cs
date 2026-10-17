namespace GameHelper.Core.Models;

/// <summary>A geographic coordinate pair used for solar sunrise/sunset computation.</summary>
public readonly record struct UserCoordinate(double Latitude, double Longitude);