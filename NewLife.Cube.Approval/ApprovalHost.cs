namespace Approval.Data;

/// <summary>
/// 业务宿主回写自己的单据。核心包没有请假单或其他业务表。
/// 同意、驳回、撤回、重新提交之后触发，包括用同一请求号重放的那一次。
/// </summary>
public static class ApprovalHost
{
    /// <summary>实例编号。订阅方按这个编号把审批状态抄回自己的业务单。</summary>
    public static event Action<Int64>? InstanceChanged;

    /// <summary>通知已注册的业务宿主。没有订阅时什么都不做。</summary>
    /// <param name="instanceId">审批实例编号。</param>
    internal static void Notify(Int64 instanceId) => InstanceChanged?.Invoke(instanceId);
}
