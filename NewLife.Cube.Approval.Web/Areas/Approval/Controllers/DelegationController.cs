using System.ComponentModel;
using Approval.Data;
using Approval.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using NewLife;
using NewLife.Cube;
using NewLife.Cube.Entity;
using XCode.Membership;

namespace Approval.Web.Areas.Approval.Controllers;

/// <summary>
/// 委托代理。委托关系用魔方内置的 PrincipalAgent（框架自带控制器管不到审批的范围语义），
/// 这里只做审批侧的范围选择、我的委托展示和停用。
/// </summary>
[DisplayName("委托代理")]
[Menu(15, true, Icon = "user-secret", Mode = MenuModes.Admin | MenuModes.Tenant)]
[ApprovalArea]
public class DelegationController : ControllerBaseX
{
    /// <summary>我的委托。我委托出去的，和别人委托给我的。含范围明细和是否正在生效。</summary>
    [EntityAuthorize((PermissionFlags)16)]
    [DisplayName("我的委托")]
    [HttpGet]
    public ActionResult List()
    {
        try
        {
            var user = Current();
            var now = DateTime.Now;
            var mine = ApprovalAgent.MyDelegations(user.ID).Select(e => Describe(e, now)).ToArray();
            var toMe = ApprovalAgent.DelegationsToMe(user.ID).Select(e => Describe(e, now)).ToArray();
            return Json(0, "ok", new { mine, toMe }, null);
        }
        catch (ApprovalException ex)
        {
            return Json(ex.Code, ex.Message, null, null);
        }
    }

    /// <summary>可选委托范围。流程与分类两份名单，新增委托页下拉用。</summary>
    [EntityAuthorize((PermissionFlags)16)]
    [DisplayName("委托范围")]
    [HttpGet]
    public ActionResult Options()
    {
        try
        {
            var processes = ApprovalProcess.FindAll()
                .Select(e => new { id = e.Id, name = e.Name, categoryId = e.CategoryId }).ToArray();
            var categories = ApprovalCategory.FindAll()
                .Select(e => new { id = e.Id, name = e.Name }).ToArray();
            return Json(0, "ok", new { processes, categories }, null);
        }
        catch (ApprovalException ex)
        {
            return Json(ex.Code, ex.Message, null, null);
        }
    }

    /// <summary>新增委托。范围为空表示全部待办；同一代理人只保留一条启用中的委托。</summary>
    [EntityAuthorize((PermissionFlags)16)]
    [DisplayName("新增委托")]
    [HttpPost]
    public ActionResult Add([FromBody] DelegationInput input)
    {
        try
        {
            var user = Current();
            if (input == null) throw new ApprovalException(4001, "缺少请求参数");
            var scopes = (input.Scopes ?? new List<DelegationScopeInput>())
                .Where(e => e.ScopeId > 0)
                .Select(e => ((AgentScope)e.ScopeType, e.ScopeId))
                .ToList();
            // 框架语义：不设置有效期表示无限制，这里用 DateTime.MinValue 表达
            var agent = ApprovalAgent.Add(user.ID, input.AgentUserId, input.Times, input.Expire ?? DateTime.MinValue, scopes, input.Remark, UserHost);
            return Json(0, "ok", new { id = agent.Id }, null);
        }
        catch (ApprovalException ex)
        {
            return Json(ex.Code, ex.Message, null, null);
        }
    }

    /// <summary>停用委托。只有委托人可以停用；停用后范围行保留，再次启用时仍然有效。</summary>
    [EntityAuthorize((PermissionFlags)16)]
    [DisplayName("停用委托")]
    [HttpPost]
    public ActionResult Cancel([FromBody] DelegationCancelInput input)
    {
        try
        {
            var user = Current();
            if (input == null || input.Id <= 0) throw new ApprovalException(4001, "缺少委托编号");
            ApprovalAgent.Cancel(input.Id, user.ID);
            return Json(0, "ok", null, null);
        }
        catch (ApprovalException ex)
        {
            return Json(ex.Code, ex.Message, null, null);
        }
    }

    private User Current()
    {
        var id = CurrentUser?.ID ?? 0;
        if (id <= 0) throw new ApprovalException(401, "没有登录或登录超时！");
        return XCode.Membership.User.FindByID(id) ?? throw new ApprovalException(401, "没有登录或登录超时！");
    }

    private static Object Describe(PrincipalAgent agent, DateTime now) => new
    {
        id = agent.Id,
        principalId = agent.PrincipalId,
        principalName = UserDisplay.Name(agent.PrincipalId),
        agentId = agent.AgentId,
        agentName = UserDisplay.Name(agent.AgentId),
        enable = agent.Enable,
        times = agent.Times,
        expire = agent.Expire.Year < 1900 ? "" : agent.Expire.ToString("yyyy-MM-dd HH:mm:ss"),
        // 与框架 GetAllValidByAgentId 同一套判定：启用、次数未尽、有效期未过
        valid = agent.Enable && agent.Times != 0 && (agent.Expire.Year < 1900 || agent.Expire >= now),
        remark = agent.Remark,
        scopes = ApprovalAgentScope.FindAllByAgentId(agent.Id).Select(e => new
        {
            scopeType = (Int32)e.ScopeType,
            scopeId = e.ScopeId,
            name = e.ScopeType == AgentScope.Process
                ? ApprovalProcess.FindById(e.ScopeId)?.Name
                : ApprovalCategory.FindById(e.ScopeId)?.Name,
        }).ToArray(),
    };
}
