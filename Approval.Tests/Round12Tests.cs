using Approval.Data;
using Approval.Data.Entities;
using NewLife.Cube.Entity;
using Xunit;
using XCode.Membership;

namespace Approval.Tests;

/// <summary>
/// 第 12 轮：委托代理。委托关系用框架内置的 PrincipalAgent，审批侧按流程或分类限定范围。
/// 任务归属不变，待办查询展开、办理鉴权放行、历史标注代办人、成功一次扣减一次可用次数。
/// </summary>
[Collection("approval")]
public class Round12Tests
{
    private const String Schema = """{"fields":[{"key":"studentUserId"},{"key":"counselorUserId"},{"key":"reason"}]}""";

    public Round12Tests(ApprovalWorld world) => _ = world;

    /// <summary>按流程范围委托：范围内待办展开、范围外不展开，代理人可以办理并留下代办痕迹。</summary>
    [Fact]
    public void Delegate_by_process_scope_expands_and_allows_handle()
    {
        var mark = Mark();
        var role = Role.Add("委托-" + mark, false, "切片");
        var approver = MakeUser("d12-ap-" + mark, "原审批人", role.ID, 0);
        var agent = MakeUser("d12-ag-" + mark, "代理人", role.ID, 0);
        var stranger = MakeUser("d12-st-" + mark, "无关人", role.ID, 0);
        var student = MakeUser("d12-stu-" + mark, "学生乙", role.ID, 0);
        var counselor = MakeUser("d12-co-" + mark, "辅导员", role.ID, 0);

        var processA = Publish(mark + "-a", "流程甲", Chain(approver.ID));
        var processB = Publish(mark + "-b", "流程乙", Chain(approver.ID));

        // 只委托流程甲
        var delegation = ApprovalAgent.Add(approver.ID, agent.ID, -1, DateTime.MinValue,
            new List<(AgentScope, Int32)> { (AgentScope.Process, processA.Id) }, "休假一周", null);
        Assert.True(delegation.Enable);
        Assert.Equal(-1, delegation.Times);
        Assert.Single(ApprovalAgentScope.FindAllByAgentId(delegation.Id));

        var instA = Start(processA, student, counselor, "d12-a-" + mark);
        var instB = Start(processB, student, counselor, "d12-b-" + mark);
        var taskA = ApprovalTask.FindPending(instA.Id).Single();
        var taskB = ApprovalTask.FindPending(instB.Id).Single();
        Assert.Equal(approver.ID, taskA.AssigneeId);
        Assert.Equal(approver.ID, taskB.AssigneeId);

        // 委托范围外的流程乙待办不进代理人待办；委托人自己两头都看得到
        var agentInbox = ApprovalTask.Inbox(agent.ID);
        Assert.Contains(agentInbox, t => t.Id == taskA.Id);
        Assert.DoesNotContain(agentInbox, t => t.Id == taskB.Id);
        Assert.Contains(ApprovalTask.Inbox(approver.ID), t => t.Id == taskA.Id);
        Assert.Contains(ApprovalTask.Inbox(approver.ID), t => t.Id == taskB.Id);

        // 无关人办不了；委托范围外的任务代理人也办不了
        Throws(4031, () => ApprovalInstance.Agree(taskA.Id, stranger.ID, null, "d12-str-" + mark, instA.Version, "127.0.0.1"));
        Throws(4031, () => ApprovalInstance.Agree(taskB.Id, agent.ID, null, "d12-out-" + mark, instB.Version, "127.0.0.1"));

        // 委托范围内，代理人同意。历史操作人是代理人，意见带代办标注
        var done = ApprovalInstance.Agree(taskA.Id, agent.ID, "同意办理", "d12-ag-" + mark, instA.Version, "127.0.0.1");
        Assert.Equal(InstanceStatus.Approved, done.Status);
        var history = ApprovalHistory.FindAllByInstanceId(done.Id).First(e => e.Action == "agree");
        Assert.Equal(agent.ID, history.OperatorId);
        Assert.Contains("同意办理", history.Comment);
        Assert.Contains("代原审批人办理", history.Comment);

        // 任务归属没变：委托人的已办里有它，代理人的已办展开里也有它
        Assert.Contains(ApprovalTask.Done(approver.ID), t => t.Id == taskA.Id);
        Assert.Contains(ApprovalTask.Done(agent.ID), t => t.Id == taskA.Id);
        Assert.DoesNotContain(ApprovalTask.Done(agent.ID), t => t.Id == taskB.Id);
    }

    /// <summary>按分类范围委托：分类下所有流程的待办都转交。</summary>
    [Fact]
    public void Delegate_by_category_scope_covers_all_processes()
    {
        var mark = Mark();
        var role = Role.Add("委托分类-" + mark, false, "切片");
        var approver = MakeUser("d12c-ap-" + mark, "原审批人", role.ID, 0);
        var agent = MakeUser("d12c-ag-" + mark, "代理人", role.ID, 0);
        var student = MakeUser("d12c-stu-" + mark, "学生乙", role.ID, 0);
        var counselor = MakeUser("d12c-co-" + mark, "辅导员", role.ID, 0);

        var category = new ApprovalCategory { Code = "d12c-cat-" + mark, Name = "学工-" + mark, Enable = true };
        category.Insert();

        var processA = Publish(mark + "c-a", "分类流程甲", Chain(approver.ID));
        var processB = Publish(mark + "c-b", "分类流程乙", Chain(approver.ID));
        processA.CategoryId = category.Id;
        processA.Update();
        processB.CategoryId = category.Id;
        processB.Update();

        ApprovalAgent.Add(approver.ID, agent.ID, 2, DateTime.MinValue,
            new List<(AgentScope, Int32)> { (AgentScope.Category, category.Id) }, null, null);

        var instA = Start(processA, student, counselor, "d12c-a-" + mark);
        var instB = Start(processB, student, counselor, "d12c-b-" + mark);
        var taskA = ApprovalTask.FindPending(instA.Id).Single();
        var taskB = ApprovalTask.FindPending(instB.Id).Single();

        var inbox = ApprovalTask.Inbox(agent.ID);
        Assert.Contains(inbox, t => t.Id == taskA.Id);
        Assert.Contains(inbox, t => t.Id == taskB.Id);
    }

    /// <summary>一次委托一次办理：Times 用尽后待办不再展开，委托失效。</summary>
    [Fact]
    public void Times_are_consumed_once_per_handle()
    {
        var mark = Mark();
        var role = Role.Add("委托次数-" + mark, false, "切片");
        var approver = MakeUser("d12t-ap-" + mark, "原审批人", role.ID, 0);
        var agent = MakeUser("d12t-ag-" + mark, "代理人", role.ID, 0);
        var student = MakeUser("d12t-stu-" + mark, "学生乙", role.ID, 0);
        var counselor = MakeUser("d12t-co-" + mark, "辅导员", role.ID, 0);

        var process = Publish(mark + "t", "次数流程", Chain(approver.ID));
        var delegation = ApprovalAgent.Add(approver.ID, agent.ID, 1, DateTime.MinValue,
            new List<(AgentScope, Int32)> { (AgentScope.Process, process.Id) }, null, null);

        var inst1 = Start(process, student, counselor, "d12t-1-" + mark);
        var inst2 = Start(process, student, counselor, "d12t-2-" + mark);
        var task1 = ApprovalTask.FindPending(inst1.Id).Single();
        var task2 = ApprovalTask.FindPending(inst2.Id).Single();

        Assert.Contains(ApprovalTask.Inbox(agent.ID), t => t.Id == task1.Id);
        ApprovalInstance.Agree(task1.Id, agent.ID, null, "d12t-h1-" + mark, inst1.Version, "127.0.0.1");

        // 一次有效：扣减后框架侧不再认为有效，待办不再展开，第二单办不了
        var reloaded = PrincipalAgent.FindById(delegation.Id)!;
        Assert.Equal(0, reloaded.Times);
        Assert.Empty(ApprovalAgent.ValidAgents(agent.ID));
        Assert.DoesNotContain(ApprovalTask.Inbox(agent.ID), t => t.Id == task2.Id);
        Throws(4031, () => ApprovalInstance.Agree(task2.Id, agent.ID, null, "d12t-h2-" + mark, inst2.Version, "127.0.0.1"));

        // 委托人自己仍然可以办理
        var ok = ApprovalInstance.Agree(task2.Id, approver.ID, "本人办", "d12t-h3-" + mark, inst2.Version, "127.0.0.1");
        Assert.Equal(InstanceStatus.Approved, ok.Status);
    }

    /// <summary>过期委托和停用委托都不再生效。</summary>
    [Fact]
    public void Expired_and_canceled_delegations_do_not_apply()
    {
        var mark = Mark();
        var role = Role.Add("委托失效-" + mark, false, "切片");
        var approver = MakeUser("d12x-ap-" + mark, "原审批人", role.ID, 0);
        var agent = MakeUser("d12x-ag-" + mark, "代理人", role.ID, 0);
        var student = MakeUser("d12x-stu-" + mark, "学生乙", role.ID, 0);
        var counselor = MakeUser("d12x-co-" + mark, "辅导员", role.ID, 0);

        var process = Publish(mark + "x", "失效流程", Chain(approver.ID));

        // 已过期的委托
        var expired = ApprovalAgent.Add(approver.ID, agent.ID, 5, DateTime.Now.AddMinutes(-1),
            new List<(AgentScope, Int32)> { (AgentScope.Process, process.Id) }, null, null);
        Assert.Empty(ApprovalAgent.ValidAgents(agent.ID));

        // 改成长期有效后手动停用
        var live = ApprovalAgent.Add(approver.ID, agent.ID, 5, DateTime.MinValue,
            new List<(AgentScope, Int32)> { (AgentScope.Process, process.Id) }, null, null);
        Assert.Single(ApprovalAgent.ValidAgents(agent.ID));

        ApprovalAgent.Cancel(live.Id, approver.ID);
        Assert.Empty(ApprovalAgent.ValidAgents(agent.ID));
        Assert.False(PrincipalAgent.FindById(live.Id)!.Enable);
        // 范围行保留，再次启用时仍然有效
        Assert.Single(ApprovalAgentScope.FindAllByAgentId(live.Id));

        var inst = Start(process, student, counselor, "d12x-i-" + mark);
        var task = ApprovalTask.FindPending(inst.Id).Single();
        Assert.DoesNotContain(ApprovalTask.Inbox(agent.ID), t => t.Id == task.Id);
        Throws(4031, () => ApprovalInstance.Agree(task.Id, agent.ID, null, "d12x-h-" + mark, inst.Version, "127.0.0.1"));

        // 只有委托人能停用
        Throws(4031, () => ApprovalAgent.Cancel(expired.Id, agent.ID));
    }

    /// <summary>抄送任务同样随委托转交；无范围行表示全部待办。</summary>
    [Fact]
    public void Cc_and_blank_scope_delegate_everything()
    {
        var mark = Mark();
        var role = Role.Add("委托抄送-" + mark, false, "切片");
        var approver = MakeUser("d12k-ap-" + mark, "抄送人", role.ID, 0);
        var agent = MakeUser("d12k-ag-" + mark, "代理人", role.ID, 0);
        var student = MakeUser("d12k-stu-" + mark, "学生乙", role.ID, 0);
        var counselor = MakeUser("d12k-co-" + mark, "辅导员", role.ID, 0);

        var process = Publish(mark + "k", "抄送流程", ChainWithCc(approver.ID));
        // 不传范围：全部待办都委托
        ApprovalAgent.Add(approver.ID, agent.ID, -1, DateTime.MinValue, new List<(AgentScope, Int32)>(), null, null);

        var inst = Start(process, student, counselor, "d12k-i-" + mark);
        // 审批节点是发起人本人，先同意才会产生抄送
        var approveTask = ApprovalTask.FindPending(inst.Id).Single();
        ApprovalInstance.Agree(approveTask.Id, student.ID, null, "d12k-ap-" + mark, inst.Version, "127.0.0.1");

        var cc = ApprovalTask.CcInbox(approver.ID).Single();
        Assert.Equal(TaskKind.Cc, cc.Kind);
        Assert.Contains(ApprovalTask.CcInbox(agent.ID), t => t.Id == cc.Id);
    }

    /// <summary>重复委托、委托自己、停用用户都不允许。</summary>
    [Fact]
    public void Add_rejects_duplicate_self_and_disabled()
    {
        var mark = Mark();
        var role = Role.Add("委托校验-" + mark, false, "切片");
        var approver = MakeUser("d12v-ap-" + mark, "委托人", role.ID, 0);
        var agent = MakeUser("d12v-ag-" + mark, "代理人", role.ID, 0);
        var stopped = MakeUser("d12v-off-" + mark, "停用代理", role.ID, 0);
        stopped.Enable = false;
        stopped.Update();

        Throws(4001, () => ApprovalAgent.Add(approver.ID, approver.ID, -1, DateTime.MinValue, new List<(AgentScope, Int32)>(), null, null));
        Throws(4221, () => ApprovalAgent.Add(approver.ID, stopped.ID, -1, DateTime.MinValue, new List<(AgentScope, Int32)>(), null, null));

        ApprovalAgent.Add(approver.ID, agent.ID, -1, DateTime.MinValue, new List<(AgentScope, Int32)>(), null, null);
        Throws(4091, () => ApprovalAgent.Add(approver.ID, agent.ID, -1, DateTime.MinValue, new List<(AgentScope, Int32)>(), null, null));

        // 另一个代理人可以同时存在，互不影响
        var other = MakeUser("d12v-ag2-" + mark, "代理人乙", role.ID, 0);
        ApprovalAgent.Add(approver.ID, other.ID, -1, DateTime.MinValue, new List<(AgentScope, Int32)>(), null, null);
        Assert.Empty(ApprovalAgent.ValidAgents(approver.ID));
        Assert.Single(ApprovalAgent.ValidAgents(agent.ID));
        Assert.Single(ApprovalAgent.ValidAgents(other.ID));
        // 委托人名下有两条委托记录
        Assert.Equal(2, ApprovalAgent.MyDelegations(approver.ID).Count);
    }

    private static ApprovalInstance Start(ApprovalProcess process, User student, User counselor, String requestId) =>
        ApprovalInstance.Start(new ApprovalInstance.StartArgs
        {
            ProcessId = process.Id,
            OperatorUserId = student.ID,
            Proxy = false,
            Data = Form(student.ID, counselor.ID),
            RequestId = requestId,
            ClientIp = "127.0.0.1",
        });

    private static String Form(Int32 studentId, Int32 counselorId) =>
        "{\"studentUserId\":" + studentId + ",\"counselorUserId\":" + counselorId + ",\"reason\":\"回家\"}";

    private static ApprovalProcess Publish(String mark, String name, String definition)
    {
        var form = new ApprovalFormDefinition { Code = mark + "-form", Name = name + "表单", Enable = true };
        form.Insert();
        form.SaveDraft(Schema);
        form.Publish(0);

        var process = new ApprovalProcess { Code = mark, Name = name, FormId = form.Id, Enable = true };
        process.Insert();
        process.SaveDraft(definition);
        process.Publish(0);
        return ApprovalProcess.FindById(process.Id)!;
    }

    private static String Chain(Int32 approverId) => """
    {"nodes":[
    {"key":"s","type":"start","name":"开始"},
    {"key":"a","type":"approve","name":"审批","mode":"any","assignee":{"type":"user","userIds":[0]}},
    {"key":"e","type":"end","name":"结束"}],
    "edges":[{"key":"e1","from":"s","to":"a"},{"key":"e2","from":"a","to":"e"}]}
    """.Replace("[0]", "[" + approverId + "]");

    private static String ChainWithCc(Int32 ccUserId) => """
    {"nodes":[
    {"key":"s","type":"start","name":"开始"},
    {"key":"a","type":"approve","name":"审批","mode":"any","assignee":{"type":"applicant"}},
    {"key":"c","type":"cc","name":"抄送","assignee":{"type":"user","userIds":[0]}},
    {"key":"e","type":"end","name":"结束"}],
    "edges":[{"key":"e1","from":"s","to":"a"},{"key":"e2","from":"a","to":"c"},{"key":"e3","from":"c","to":"e"}]}
    """.Replace("[0]", "[" + ccUserId + "]");

    private static User MakeUser(String name, String display, Int32 roleId, Int32 departmentId)
    {
        var user = User.Add(name, "pass1234", roleId, display);
        user.DepartmentID = departmentId;
        user.Enable = true;
        user.Update();
        return User.FindByID(user.ID)!;
    }

    private static String Mark() => Guid.NewGuid().ToString("N")[..8];

    private static void Throws(Int32 code, Action action)
    {
        var ex = Assert.Throws<ApprovalException>(action);
        Assert.Equal(code, ex.Code);
    }
}
