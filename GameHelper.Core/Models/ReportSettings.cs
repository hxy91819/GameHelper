namespace GameHelper.Core.Models;

/// <summary>Optional report commentary settings; absent means no network requests.</summary>
public sealed class ReportSettings
{
    public ReportLlmSettings? Llm { get; set; }
    public void Validate() => Llm?.Validate();
}
