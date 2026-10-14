using Approval.Data.Entities;
using NewLife;
using NewLife.Cube.Entity;
using XCode;
using XCode.Membership;

namespace Approval.Data;

/// <summary>
/// 委托代理解析。委托关系用魔方内置的 <see cref="PrincipalAgent"/>（Membership 库，框架启动自动建表），
/// 审批侧只做两件事：按流程或分类判定生效范围（<see cref="ApprovalAgentScope"/>，无范围行表示全部待办），
/// 在待办查询时展开、在办理鉴权时放行。任务归属不变，撤回、会签、依次审批等既有逻辑零改动。
/// </summary>
public static class ApprovalAgent
{
    /// <summary>
    /// 取该代理人名下全部有效委托。框架的 GetAllValidByAgentId 只按启用过滤，
    /// 次数与有效期由审批侧按字段语义补判：Times 为 0 表示已用完，-1 表示无限制；
    /// Expire 不设置（小于 1900 的哨兵值）表示无限制。
    /// </summary>
    public static List<PrincipalAgent> ValidAgents(Int32 agentUserId)
    {
        if (agentUserId <= 0) return new List<PrincipalAgent>();
        var now = DateTime.Now;
        return PrincipalAgent.FindAllByAgentId(agentUserId)
            .Where(e => e.Enable && e.Times != 0 && (e.Expire.Year < 1900 || e.Expire >= now))
            .ToList();
    }

    /// <summary>取该委托人委托出去的全部委托，含停用和已过期的。</summary>
    public static IList<PrincipalAgent> MyDelegations(Int32 principalUserId)
    {
        if (principalUserId <= 0) return [];
        return PrincipalAgent.FindAllByPrincipalId(principalUserId);
    }

    /// <summary>取别人委托给我的全部委托，含停用和已过期的。</summary>
    public static IList<PrincipalAgent> DelegationsToMe(Int32 agentUserId)
    {
        if (agentUserId <= 0) return [];
        return PrincipalAgent.FindAllByAgentId(agentUserId);
    }

    /// <summary>该流程是否落在委托范围内。没有范围行表示全部待办都委托。</summary>
    public static Boolean InScope(IList<ApprovalAgentScope> scopes, Int32 processId, Int32 categoryId)
    {
        if (scopes.Count == 0) return true;
        return scopes.Any(e =>
            (e.ScopeType == AgentScope.Process && e.ScopeId == processId) ||
            (e.ScopeType == AgentScope.Category && e.ScopeId == categoryId && categoryId > 0));
    }

    /// <summary>
    /// 该用户能否办理这个任务。办理人本人可以；否则须是任务处理人（委托人）名下的有效代理，
    /// 且任务的流程或分类落在委托范围内。
    /// </summary>
    public static Boolean CanHandle(Int32 operatorUserId, ApprovalTask task)
    {
        if (task == null) return false;
        if (task.AssigneeId == operatorUserId) return true;
        if (operatorUserId <= 0) return false;

        var process = ApprovalProcess.FindById(task.ProcessId);
        var categoryId = process?.CategoryId ?? 0;
        foreach (var agent in ValidAgents(operatorUserId))
        {
            if (agent.PrincipalId != task.AssigneeId) continue;
            var scopes = ApprovalAgentScope.FindAllByAgentId(agent.Id);
            if (InScope(scopes, task.ProcessId, categoryId)) return true;
        }
        return false;
    }

    /// <summary>取我可以在待办里代办的委托人。范围是否命中由调用方按任务判定。</summary>
    public static ISet<Int32> DelegablePrincipals(Int32 agentUserId)
    {
        var set = new HashSet<Int32>();
        foreach (var agent in ValidAgents(agentUserId)) set.Add(agent.PrincipalId);
        return set;
    }

    /// <summary>单条委托当前是否还有效。启用、次数未尽、有效期未过。</summary>
    private static Boolean IsValid(PrincipalAgent agent) =>
        agent.Enable && agent.Times != 0 && (agent.Expire.Year < 1900 || agent.Expire >= DateTime.Now);

    /// <summary>
    /// 代办成功后扣减一次可用次数。框架语义是一次有效；Times 为 -1 表示无限制，不扣。
    /// 扣到 0 之后委托自然失效，GetAllValidByAgentId 不再返回它。
    /// </summary>
    public static void Consume(ApprovalTask task, Int32 operatorUserId)
    {
        if (task == null || task.AssigneeId == operatorUserId) return;
        foreach (var agent in ValidAgents(operatorUserId))
        {
            if (agent.PrincipalId != task.AssigneeId) continue;
            if (agent.Times > 0)
            {
                agent.Times--;
                agent.Update();
            }
            return;
        }
    }

    /// <summary>待办里这条任务属于谁代办。本人办理返回 0；代办返回委托人编号。</summary>
    public static Int32 AgentOf(Int32 viewerUserId, ApprovalTask task)
    {
        if (task == null || task.AssigneeId == viewerUserId) return 0;
        foreach (var agent in ValidAgents(viewerUserId))
        {
            if (agent.PrincipalId != task.AssigneeId) continue;
            var scopes = ApprovalAgentScope.FindAllByAgentId(agent.Id);
            var process = ApprovalProcess.FindById(task.ProcessId);
            if (InScope(scopes, task.ProcessId, process?.CategoryId ?? 0)) return agent.PrincipalId;
        }
        return 0;
    }

    /// <summary>新增委托。同一委托人对同一代理人只允许一条启用中的委托，重复须先停用旧的。</summary>
    public static PrincipalAgent Add(Int32 principalUserId, Int32 agentUserId, Int32 times, DateTime expire, IList<(AgentScope ScopeType, Int32 ScopeId)> scopes, String? remark, String? clientIp)
    {
        if (principalUserId <= 0 || agentUserId <= 0) throw new ApprovalException(4001, "请选择委托对象");
        if (principalUserId == agentUserId) throw new ApprovalException(4001, "不能委托给自己");
        if (times < -1) throw new ApprovalException(4001, "委托次数不合法");

        var principal = User.FindByID(principalUserId) ?? throw new ApprovalException(4041, "委托人不存在");
        if (!principal.Enable) throw new ApprovalException(4221, "委托人已停用");
        var agent = User.FindByID(agentUserId) ?? throw new ApprovalException(4041, "代理人不存在");
        if (!agent.Enable) throw new ApprovalException(4221, "代理人已停用");

        // 同一对委托人、代理人只保留一条还有效的委托（过期和用完的自动让位），避免范围互相覆盖
        foreach (var old in MyDelegations(principalUserId))
        {
            if (old.AgentId == agentUserId && IsValid(old))
                throw new ApprovalException(4091, "已存在对该代理人的有效委托，请先停用");
        }

        foreach (var (scopeType, scopeId) in scopes)
        {
            if (scopeId <= 0) throw new ApprovalException(4001, "请选择委托范围");
            if (scopeType == AgentScope.Process)
            {
                var p = ApprovalProcess.FindById(scopeId) ?? throw new ApprovalException(4041, "委托范围内的流程不存在");
                EnsureTenant(p);
            }
            else if (scopeType == AgentScope.Category)
            {
                var c = ApprovalCategory.FindById(scopeId) ?? throw new ApprovalException(4041, "委托范围内的分类不存在");
                EnsureTenant(c);
            }
            else throw new ApprovalException(4001, "未知的委托范围类型");
        }

        // PrincipalAgent 在 Membership 库、Scope 在 Approval 库，跨库不能同事务，失败时补偿删除
        var entity = new PrincipalAgent
        {
            PrincipalId = principalUserId,
            AgentId = agentUserId,
            Enable = true,
            Times = times,
            Expire = expire,
            Remark = remark,
        };
        entity.Insert();
        try
        {
            foreach (var (scopeType, scopeId) in scopes.Distinct())
            {
                new ApprovalAgentScope { AgentId = entity.Id, ScopeType = scopeType, ScopeId = scopeId }.Insert();
            }
        }
        catch
        {
            entity.Delete();
            throw;
        }
        return entity;
    }

    /// <summary>停用委托。范围行一并保留，再次启用时仍然有效。</summary>
    public static void Cancel(Int32 delegationId, Int32 operatorUserId)
    {
        var agent = PrincipalAgent.FindById(delegationId) ?? throw new ApprovalException(4041, "委托不存在");
        if (agent.PrincipalId != operatorUserId) throw new ApprovalException(4031, "只有委托人可以停用该委托");
        if (!agent.Enable) return;
        agent.Enable = false;
        agent.Update();
    }

    /// <summary>租户纵深防御。委托范围引用的审批数据必须落在当前租户内。影子期（无上下文）不拦。</summary>
    private static void EnsureTenant(ITenantScope entity)
    {
        var tenantId = TenantContext.CurrentId;
        if (tenantId <= 0) return;
        if (entity.TenantId != tenantId)
            throw new ApprovalException(4031, "委托范围指向了其他租户的数据");
    }
}
