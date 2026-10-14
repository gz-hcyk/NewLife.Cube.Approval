using Approval.Data;
using XCode;
using XCode.Membership;

namespace Approval.Data.Entities;

public partial class ApprovalTask : Entity<ApprovalTask>
{
    static ApprovalTask()
    {
        Meta.Interceptors.Add<TenantInterceptor>();
    }

    /// <summary>某实例当前待处理的审批任务。</summary>
    public static IList<ApprovalTask> FindPending(Int64 instanceId) =>
        FindAll(_.InstanceId == instanceId & _.Status == TaskStatus.Pending & _.Kind == TaskKind.Approve);

    /// <summary>
    /// 当前用户的待办：本人的，加上有效委托范围内委托人的。只看办理人，不受流程监控的数据范围影响。
    /// 抄送另见 <see cref="CcInbox"/>。
    /// </summary>
    public static IList<ApprovalTask> Inbox(Int32 userId)
    {
        var list = FindAll(_.AssigneeId == userId & _.Kind == TaskKind.Approve & _.Status == TaskStatus.Pending).ToList();
        list.AddRange(Delegated(userId, _.Kind == TaskKind.Approve & _.Status == TaskStatus.Pending));
        return list;
    }

    /// <summary>当前用户尚未阅读的抄送：本人的，加上有效委托范围内委托人的。</summary>
    public static IList<ApprovalTask> CcInbox(Int32 userId)
    {
        var list = FindAll(_.AssigneeId == userId & _.Kind == TaskKind.Cc & _.Status == TaskStatus.Pending).ToList();
        list.AddRange(Delegated(userId, _.Kind == TaskKind.Cc & _.Status == TaskStatus.Pending));
        return list;
    }

    /// <summary>当前用户的已办：已同意、已驳回、已转办。含委托范围内委托人的对应已办。</summary>
    public static IList<ApprovalTask> Done(Int32 userId)
    {
        var list = FindAll(_.AssigneeId == userId & _.Kind == TaskKind.Approve)
            .Where(t => t.Status is TaskStatus.Agreed or TaskStatus.Rejected or TaskStatus.Transferred)
            .ToList();
        var delegated = Delegated(userId, _.Kind == TaskKind.Approve);
        list.AddRange(delegated.Where(t => t.Status is TaskStatus.Agreed or TaskStatus.Rejected or TaskStatus.Transferred));
        return list;
    }

    /// <summary>委托人那边落在委托范围内的待办。办理人本人的任务不进来，避免重复。</summary>
    private static IList<ApprovalTask> Delegated(Int32 agentUserId, Expression exp)
    {
        var principals = ApprovalAgent.DelegablePrincipals(agentUserId);
        if (principals.Count == 0) return [];

        return FindAll(exp & _.AssigneeId.In(principals.ToArray()))
            .Where(t => ApprovalAgent.AgentOf(agentUserId, t) > 0)
            .ToList();
    }
}
