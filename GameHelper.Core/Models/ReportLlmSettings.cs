namespace GameHelper.Core.Models;

/// <summary>OpenAI-compatible endpoint root, for example https://example.com/v1.</summary>
public sealed class ReportLlmSettings
{
    public string? BaseUrl { get; set; }
    public string? Model { get; set; }
    public string? ApiKey { get; set; }
    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model);
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(BaseUrl) && string.IsNullOrWhiteSpace(Model) && string.IsNullOrWhiteSpace(ApiKey)) return;
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("report.llm.baseUrl 必须为无凭据、查询或片段的 HTTP(S) 地址。");
        if (ApiKey?.Any(char.IsControl) == true) throw new ArgumentException("report.llm.apiKey 不能包含控制字符。 ");
        if (string.IsNullOrWhiteSpace(Model)) throw new ArgumentException("report.llm.model 不能为空。");
    }
}
