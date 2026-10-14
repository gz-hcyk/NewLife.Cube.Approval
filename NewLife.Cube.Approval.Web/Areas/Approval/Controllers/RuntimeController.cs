using System.ComponentModel;
using Approval.Data;
using Approval.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using NewLife;
using NewLife.Cube;
using XCode.Membership;

namespace Approval.Web.Areas.Approval.Controllers;

/// <summary>审批运行。发起、同意、驳回都调用实例上的业务方法。</summary>
/// <remarks>
/// 授权模型（三层，别再给办理动作加回权限位）：
/// 1. 办理类（发起/草稿/撤回/同意/驳回/转办/加签/待办/已办/抄送/查看/检索）只校验登录，
///    真实边界在实体层——办理人、发起人、参与人、委托关系，越权由 ApprovalException 4031 把守；
/// 2. 「选人」32768 是特权位：Candidates 返回全量用户目录，数据面不限，按需授予；
/// 3. 「监控」16384 是特权位：可见范围超出参与关系，按需授予。
/// 设计类端点（ApprovalAdmin 表单/流程）不在此列，仍按菜单位授权。
/// </remarks>
[DisplayName("审批办理")]
[Menu(10, true, Icon = "task", Mode = MenuModes.Admin | MenuModes.Tenant)]
[ApprovalArea]
public class RuntimeController : ControllerBaseX
{
    /// <summary>当前登录人。发起页用它锁定本人发起的学生。</summary>
    [DisplayName("当前用户")]
    [HttpGet]
    public ActionResult Me()
    {
        try
        {
            var user = Current();
            var dept = Department.FindByID(user.DepartmentID);
            var display = UserDisplay.Name(user);
            return Json(0, "ok", new
            {
                id = user.ID,
                name = user.Name,
                displayName = display,
                departmentId = user.DepartmentID,
                departmentName = dept?.Name,
            }, null);
        }
        catch (ApprovalException ex)
        {
            return Json(ex.Code, ex.Message, null, null);
        }
    }

    /// <summary>可选用户。本人锁定、代发起选学生、转办选接任人都用这份名单。</summary>
    /// <remarks>
    /// 唯一数据面不限办理人的端点（返回全量启用用户目录），因此独立高位 32768「选人」按需授权，
    /// 不随办理类动作默认放行；与查看单据（128）脱钩。监控（16384）同为特权位。
    /// </remarks>
    [EntityAuthorize((PermissionFlags)32768)]
    [DisplayName("选人")]
    [HttpGet]
    public ActionResult Candidates()
    {
        try
        {
            _ = Current();
            var list = XCode.Membership.User.FindAll()
                .Where(user => user != null && user.Enable)
                .Select(user => new
                {
                    id = user.ID,
                    name = user.Name,
                    displayName = user.DisplayName.IsNullOrEmpty() ? user.Name : user.DisplayName,
                    departmentId = user.DepartmentID,
                });
            return Json(0, "ok", list, null);
        }
        catch (ApprovalException ex)
        {
            return Json(ex.Code, ex.Message, null, null);
        }
    }

    /// <summary>可以发起的已发布流程。</summary>
    [DisplayName("可发起流程")]
    [HttpGet]
    public ActionResult Processes()
    {
        try
        {
            _ = Current();
            var list = ApprovalProcess.FindAll()
                .Where(process => process.Enable && process.PublishedVersionId > 0)
                .Select(process => new
                {
                    id = process.Id,
                    code = process.Code,
                    name = process.Name,
                    formId = process.FormId,
                });
            return Json(0, "ok", list, null);
        }
        catch (ApprovalException ex)
        {
            return Json(ex.Code, ex.Message, null, null);
        }
    }

    /// <summary>发起页要的开始节点字段权限。</summary>
    [DisplayName("发起表单")]
    [HttpGet]
    public ActionResult StartForm(Int32 processId)
    {
        try
        {
            _ = Current();
            var process = ApprovalProcess.FindById(processId)
                ?? throw new ApprovalException(4041, "流程不存在");
            if (process.PublishedVersionId <= 0)
                throw new ApprovalException(4091, "流程尚未发布");
            var version = ApprovalProcessVersion.FindById(process.PublishedVersionId)
                ?? throw new ApprovalException(4041, "已发布的流程版本不存在");
            var graph = FlowGraph.Parse(version.Definition);
            var start = graph.Nodes.FirstOrDefault(e => e.Type == "start");
            var schema = version.FormVersionId > 0 ? ApprovalFormVersion.FindById(version.FormVersionId)?.Schema : null;
            return Json(0, "ok", new
            {
                processId = process.Id,
                name = process.Name,
                fields = FieldRules.Views(schema, start),
                picks = graph.Nodes
                    .Where(node => node.Assignee?.Type == "starterPick")
                    .Select(node => new { nodeKey = node.Key, name = node.Name }),
            }, null);
        }
        catch (ApprovalException ex)
        {
            return Json(ex.Code, ex.Message, null, null);
        }
    }

    /// <summary>按可检索字段的值查找实例。关键字按原文包含匹配，例如「回家」能命中「回家一天」。</summary>
    [DisplayName("检索")]
    [HttpGet]
    public ActionResult Search(String? field, String? keyword)
    {
        try
        {
            var user = Current();
            var monitorIds = MonitorIds(user);
            var rows = ApprovalFieldValue.FindMatches(field ?? "", keyword ?? "");
            var list = new List<Object>();
            foreach (var row in rows)
            {
                var inst = ApprovalInstance.FindById(row.InstanceId);
                if (inst == null || !ApprovalAccess.CanView(user, inst, monitorIds)) continue;
                var full = monitorIds.Contains(inst.Id);
                if (!full && ApprovalAccess.HiddenFrom(user, inst).Contains(row.FieldKey)) continue;
                list.Add(new
                {
                    instanceId = row.InstanceId.ToString(),
                    field = row.FieldKey,
                    value = row.FieldValue,
                    title = inst.Title,
                    status = (Int32)inst.Status,
                });
            }

            return Json(0, "ok", list, null);
        }
        catch (ApprovalException ex)
        {
            return Json(ex.Code, ex.Message, null, null);
        }
    }

    /// <summary>发起。本人发起与代发起进入同一条已发布流程。</summary>
    [DisplayName("发起")]
    [HttpPost]
    public ActionResult Start([FromBody] StartInput input)
    {
        try
        {
            var user = Current();
            var inst = ApprovalInstance.Start(new ApprovalInstance.StartArgs
            {
                ProcessId = input?.ProcessId ?? 0,
                OperatorUserId = user.ID,
                Proxy = input?.Proxy ?? false,
                SubjectUserId = input?.SubjectUserId ?? 0,
                Data = input?.Data,
                RequestId = input?.RequestId,
                ClientIp = UserHost,
                AssigneePicks = input?.AssigneePicks,
            });
            return Json(0, "ok", Describe(inst), null);
        }
        catch (ApprovalException ex)
        {
            return Json(ex.Code, ex.Message, null, null);
        }
    }

    /// <summary>同意。</summary>
    [DisplayName("同意")]
    [HttpPost]
    public ActionResult Agree([FromBody] HandleInput input) => Handle(input, true);

    /// <summary>驳回给发起人。</summary>
    [DisplayName("驳回")]
    [HttpPost]
    public ActionResult Reject([FromBody] HandleInput input) => Handle(input, false);

    /// <summary>转办给另一名魔方用户。</summary>
    [DisplayName("转办")]
    [HttpPost]
    public ActionResult Transfer([FromBody] TransferInput input)
    {
        try
        {
            var user = Current();
            if (input == null || !Int64.TryParse(input.TaskId, out var taskId) || taskId <= 0)
                throw new ApprovalException(4001, "缺少任务编号");
            var inst = ApprovalInstance.Transfer(taskId, user.ID, input.TargetUserId, input.Comment, input.RequestId ?? "", input.InstanceVersion, UserHost);
            return Json(0, "ok", Describe(inst), null);
        }
        catch (ApprovalException ex)
        {
            return Json(ex.Code, ex.Message, null, null);
        }
    }

    /// <summary>当前用户撤回后的草稿，用来重新提交。</summary>
    [DisplayName("我的草稿")]
    [HttpGet]
    public ActionResult Drafts()
    {
        try
        {
            var user = Current();
            var list = ApprovalInstance.FindAll(ApprovalInstance._.UserId == user.ID & ApprovalInstance._.Status == InstanceStatus.Draft);
            return Json(0, "ok", list.Select(inst => new
            {
                instanceId = inst.Id.ToString(),
                title = inst.Title,
                round = inst.Round,
                status = (Int32)inst.Status,
                version = inst.Version,
                processId = inst.ProcessId,
                subjectUserId = inst.SubjectUserId,
                subjectName = UserDisplay.Name(inst.SubjectUserId),
                proxyUserId = inst.ProxyUserId,
                proxyName = inst.ProxyUserId > 0 ? UserDisplay.Name(inst.ProxyUserId) : null,
                userId = inst.UserId,
            }), null);
        }
        catch (ApprovalException ex)
        {
            return Json(ex.Code, ex.Message, null, null);
        }
    }

    /// <summary>撤回后重新提交，轮次加一，流程版本不变。</summary>
    [DisplayName("重新提交")]
    [HttpPost]
    public ActionResult Resubmit([FromBody] ResubmitInput input)
    {
        try
        {
            var user = Current();
            if (input == null || !Int64.TryParse(input.InstanceId, out var instanceId) || instanceId <= 0)
                throw new ApprovalException(4001, "缺少实例编号");
            var inst = ApprovalInstance.Resubmit(instanceId, user.ID, input.Data, input.RequestId ?? "", input.InstanceVersion, UserHost, input.AssigneePicks);
            return Json(0, "ok", Describe(inst), null);
        }
        catch (ApprovalException ex)
        {
            return Json(ex.Code, ex.Message, null, null);
        }
    }

    /// <summary>发起人撤回。代发起时发起人是代发人。</summary>
    [DisplayName("撤回")]
    [HttpPost]
    public ActionResult Withdraw([FromBody] WithdrawInput input)
    {
        try
        {
            var user = Current();
            if (input == null || !Int64.TryParse(input.InstanceId, out var instanceId) || instanceId <= 0)
                throw new ApprovalException(4001, "缺少实例编号");
            var inst = ApprovalInstance.Withdraw(instanceId, user.ID, input.Reason, input.RequestId ?? "", input.InstanceVersion, UserHost);
            return Json(0, "ok", Describe(inst), null);
        }
        catch (ApprovalException ex)
        {
            return Json(ex.Code, ex.Message, null, null);
        }
    }

    /// <summary>向后加签一级。</summary>
    [DisplayName("加签")]
    [HttpPost]
    public ActionResult AddSign([FromBody] AddSignInput input)
    {
        try
        {
            var user = Current();
            if (input == null || !Int64.TryParse(input.TaskId, out var taskId) || taskId <= 0)
                throw new ApprovalException(4001, "缺少任务编号");
            var inst = ApprovalInstance.AddSign(taskId, user.ID, input.TargetUserId, input.Comment, input.RequestId ?? "", input.InstanceVersion, UserHost);
            return Json(0, "ok", Describe(inst), null);
        }
        catch (ApprovalException ex)
        {
            return Json(ex.Code, ex.Message, null, null);
        }
    }

    /// <summary>抄送标为已阅。</summary>
    [DisplayName("已阅")]
    [HttpPost]
    public ActionResult Read([FromBody] ReadInput input)
    {
        try
        {
            var user = Current();
            if (input == null || !Int64.TryParse(input.TaskId, out var taskId) || taskId <= 0)
                throw new ApprovalException(4001, "缺少任务编号");
            var inst = ApprovalInstance.Read(taskId, user.ID, input.RequestId ?? "", UserHost);
            return Json(0, "ok", Describe(inst), null);
        }
        catch (ApprovalException ex)
        {
            return Json(ex.Code, ex.Message, null, null);
        }
    }

    /// <summary>流程监控。按当前用户的数据范围过滤，不改变待办的办理人过滤。</summary>
    [EntityAuthorize((PermissionFlags)16384)]
    [DisplayName("监控")]
    [HttpGet]
    public ActionResult Monitor()
    {
        try
        {
            var user = Current();
            return Json(0, "ok", ApprovalInstance.Monitor(user).Select(inst => new
            {
                instanceId = inst.Id.ToString(),
                no = inst.No,
                title = inst.Title,
                status = (Int32)inst.Status,
                userId = inst.UserId,
                departmentId = inst.DepartmentId,
            }), null);
        }
        catch (ApprovalException ex)
        {
            return Json(ex.Code, ex.Message, null, null);
        }
    }

    /// <summary>当前用户的待办。</summary>
    [DisplayName("待办")]
    [HttpGet]
    public ActionResult Inbox()
    {
        try
        {
            var user = Current();
            return Json(0, "ok", ApprovalTask.Inbox(user.ID).Select(t => DescribeTask(t, user.ID)), null);
        }
        catch (ApprovalException ex)
        {
            return Json(ex.Code, ex.Message, null, null);
        }
    }

    /// <summary>当前用户的已办。</summary>
    [DisplayName("已办")]
    [HttpGet]
    public ActionResult Done()
    {
        try
        {
            var user = Current();
            return Json(0, "ok", ApprovalTask.Done(user.ID).Select(t => DescribeTask(t, user.ID)), null);
        }
        catch (ApprovalException ex)
        {
            return Json(ex.Code, ex.Message, null, null);
        }
    }

    /// <summary>当前用户尚未阅读的抄送。</summary>
    [DisplayName("抄送")]
    [HttpGet]
    public ActionResult Cc()
    {
        try
        {
            var user = Current();
            return Json(0, "ok", ApprovalTask.CcInbox(user.ID).Select(t => DescribeTask(t, user.ID)), null);
        }
        catch (ApprovalException ex)
        {
            return Json(ex.Code, ex.Message, null, null);
        }
    }

    /// <summary>查看实例、待办和轨迹。不是参与人、也不在本人监控范围内时拒绝。</summary>
    [DisplayName("查看单据")]
    [HttpGet]
    public ActionResult View(Int64 instanceId)
    {
        try
        {
            var user = Current();
            var inst = ApprovalInstance.FindById(instanceId) ?? throw new ApprovalException(4041, "审批单不存在");
            var monitorIds = MonitorIds(user);
            if (!ApprovalAccess.CanView(user, inst, monitorIds))
                throw new ApprovalException(4031, "无权查看该审批单");
            var raw = ApprovalFormData.FindById(inst.Id)?.Data;
            var form = ApprovalAccess.RedactForm(user, inst, raw, monitorIds);
            return Json(0, "ok", Describe(inst, form), null);
        }
        catch (ApprovalException ex)
        {
            return Json(ex.Code, ex.Message, null, null);
        }
    }

    /// <summary>有监控权限时返回数据范围内的实例编号，否则为空集。</summary>
    private static HashSet<Int64> MonitorIds(User user)
    {
        if (!HasMonitor(user)) return [];
        return ApprovalInstance.Monitor(user).Select(inst => inst.Id).ToHashSet();
    }

    private static Boolean HasMonitor(User user)
    {
        var menu = XCode.Membership.Menu.FindAll().FirstOrDefault(item =>
            (item.FullName ?? "").Contains("Runtime", StringComparison.OrdinalIgnoreCase)
            || (item.Url ?? "").Contains("Runtime", StringComparison.OrdinalIgnoreCase));
        return menu != null && user.Has(menu, (PermissionFlags)16384);
    }

    private ActionResult Handle(HandleInput? input, Boolean agree)
    {
        try
        {
            var user = Current();
            if (input == null || !Int64.TryParse(input.TaskId, out var taskId) || taskId <= 0)
                throw new ApprovalException(4001, "缺少任务编号");
            var inst = agree
                ? ApprovalInstance.Agree(taskId, user.ID, input.Comment, input.RequestId ?? "", input.InstanceVersion, UserHost, input.Data)
                : ApprovalInstance.Reject(taskId, user.ID, input.Comment, input.RequestId ?? "", input.InstanceVersion, UserHost, input.Data);
            return Json(0, "ok", Describe(inst), null);
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

    private Object Describe(ApprovalInstance inst)
    {
        var user = Current();
        var monitorIds = MonitorIds(user);
        var raw = ApprovalFormData.FindById(inst.Id)?.Data;
        return Describe(inst, ApprovalAccess.RedactForm(user, inst, raw, monitorIds));
    }

    private static Object Describe(ApprovalInstance inst, String? formData)
    {
        var tasks = ApprovalTask.FindAll(ApprovalTask._.InstanceId == inst.Id);
        var history = ApprovalHistory.FindAllByInstanceId(inst.Id);
        var version = ApprovalProcessVersion.FindById(inst.ProcessVersionId);
        return new
        {
            instanceId = inst.Id.ToString(),
            no = inst.No,
            title = inst.Title,
            status = (Int32)inst.Status,
            version = inst.Version,
            userId = inst.UserId,
            userName = UserDisplay.Name(inst.UserId),
            subjectUserId = inst.SubjectUserId,
            subjectName = UserDisplay.Name(inst.SubjectUserId),
            proxyUserId = inst.ProxyUserId,
            proxyName = inst.ProxyUserId > 0 ? UserDisplay.Name(inst.ProxyUserId) : null,
            counselorUserId = inst.CounselorUserId,
            departmentId = inst.DepartmentId,
            round = inst.Round,
            processId = inst.ProcessId,
            processVersionId = inst.ProcessVersionId,
            formData,
            definition = version?.Definition ?? "",
            nodeFields = NodeFields(inst, version),
            tasks = tasks.Select(DescribeTask),
            history = history.Select(h => new
            {
                action = h.Action,
                actionName = h.ActionName,
                comment = h.Comment,
                operatorId = h.OperatorId,
                operatorName = UserDisplay.Name(h.OperatorId),
                fromStatus = h.FromStatus,
                toStatus = h.ToStatus,
                round = h.Round,
                nodeKey = h.NodeKey ?? "",
                nodeName = h.NodeName ?? "",
                createTime = h.CreateTime.Year < 1900 ? "" : h.CreateTime.ToString("yyyy-MM-dd HH:mm:ss"),
            }),
        };
    }

    private static Object NodeFields(ApprovalInstance inst, ApprovalProcessVersion? version = null)
    {
        version ??= ApprovalProcessVersion.FindById(inst.ProcessVersionId);
        var graph = version == null || version.Definition.IsNullOrEmpty() ? new FlowGraph() : FlowGraph.Parse(version.Definition);
        var schema = inst.FormVersionId > 0 ? ApprovalFormVersion.FindById(inst.FormVersionId)?.Schema : null;
        return graph.Nodes.Select(node => new
        {
            nodeKey = node.Key,
            fields = FieldRules.Views(schema, node),
        });
    }

    private static Object DescribeTask(ApprovalTask t, Int32 viewerUserId)
    {
        var agentOf = ApprovalAgent.AgentOf(viewerUserId, t);
        return new
        {
            id = t.Id.ToString(),
            instanceId = t.InstanceId.ToString(),
            assigneeId = t.AssigneeId,
            assigneeName = UserDisplay.Name(t.AssigneeId),
            // 代办视图：大于 0 表示这是委托人转交代办的待办
            agentOf,
            agentOfName = agentOf > 0 ? UserDisplay.Name(agentOf) : null,
            nodeKey = t.NodeKey,
            nodeName = t.NodeName,
            status = (Int32)t.Status,
            mode = (Int32)t.Mode,
            title = t.Title,
            kind = (Int32)t.Kind,
            seq = t.Seq,
            source = (Int32)t.Source,
            createTime = t.CreateTime.Year < 1900 ? "" : t.CreateTime.ToString("yyyy-MM-dd HH:mm:ss"),
        };
    }
}
