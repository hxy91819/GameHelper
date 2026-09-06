namespace GameHelper.Core.Abstractions;

public enum SyncOutcomeStatus
{
    /// <summary>已成功上传。</summary>
    Uploaded,

    /// <summary>本次无需上传（未到间隔/无新数据/内容未变化等），见 <see cref="SyncOutcome.Detail"/>。</summary>
    Skipped,

    /// <summary>上传失败，见 <see cref="SyncOutcome.Error"/>。</summary>
    Failed,

    /// <summary>渠道校验通过（不写入数据）。</summary>
    Validated
}
