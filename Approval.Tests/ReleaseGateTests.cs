using System.ComponentModel;
using Approval.Data;
using Approval.Data.Entities;
using Xunit;
using XCode.Membership;

namespace Approval.Tests;

/// <summary>上线门禁：请求号重放不能变成越权读取，非办理人不能转办或加签。</summary>
[Collection("approval")]
public class ReleaseGateTests
{
    private const String Schema = """{"fields":[{"key":"studentUserId"},{"key":"reason","search":true},{"key":"days"}]}""";

    public ReleaseGateTests(ApprovalWorld world) => _ = world;

    [Fact]
    [DisplayName("别人不能用发起请求号或网关请求号重放单据")]
    public void Stranger_cannot_replay_start_or_gateway_request()
    {
        var mark = Guid.NewGuid().ToString("N")[..8];
        var owner = UserOf("gate-owner-" + mark, "学生甲");
        var approver = UserOf("gate-appr-" + mark, "审批人");
        var stranger = UserOf("gate-str-" + mark, "旁观者");
        var process = Publish(mark, approver.ID);
        var requestId = "gate-start-" + mark;
        var secret = "gate-secret-" + mark;
        var inst = ApprovalInstance.Start(new ApprovalInstance.StartArgs
        {
            ProcessId = process.Id,
            OperatorUserId = owner.ID,
            Data = "{\"studentUserId\":" + owner.ID + ",\"reason\":\"" + secret + "\",\"days\":5}",
            RequestId = requestId,
        });
        Assert.Contains(ApprovalHistory.FindAllByInstanceId(inst.Id), row => row.RequestId == "choose-1-x" && row.OperatorId == owner.ID);

        var stolenStart = Assert.Throws<ApprovalException>(() => ApprovalInstance.Start(new ApprovalInstance.StartArgs
        {
            ProcessId = process.Id,
            OperatorUserId = stranger.ID,
            Data = "{\"studentUserId\":" + stranger.ID + ",\"reason\":\"偷看\"}",
            RequestId = requestId,
        }));
        Assert.Equal(4031, stolenStart.Code);
        var stolenChoose = Assert.Throws<ApprovalException>(() => ApprovalInstance.Start(new ApprovalInstance.StartArgs
        {
            ProcessId = process.Id,
            OperatorUserId = stranger.ID,
            RequestId = "choose-1-x",
        }));
        Assert.Equal(4031, stolenChoose.Code);
        Assert.DoesNotContain(secret, stolenChoose.Message);

        var again = ApprovalInstance.Start(new ApprovalInstance.StartArgs
        {
            ProcessId = process.Id,
            OperatorUserId = owner.ID,
            RequestId = requestId,
        });
        Assert.Equal(inst.Id, again.Id);
        Assert.Single(ApprovalInstance.FindAll(ApprovalInstance._.ProcessId == process.Id));
    }

    [Fact]
    [DisplayName("非办理人不能同意、转办、加签或撤回，本人重放同意仍成功")]
    public void Stranger_cannot_handle_and_owner_can_replay_agree()
    {
        var mark = Guid.NewGuid().ToString("N")[..8];
        var owner = UserOf("gate2-owner-" + mark, "学生甲");
        var approver = UserOf("gate2-appr-" + mark, "审批人");
        var stranger = UserOf("gate2-str-" + mark, "旁观者");
        var target = UserOf("gate2-to-" + mark, "接任人");
        var process = Publish(mark, approver.ID);
        var inst = ApprovalInstance.Start(new ApprovalInstance.StartArgs
        {
            ProcessId = process.Id,
            OperatorUserId = owner.ID,
            Data = "{\"studentUserId\":" + owner.ID + ",\"reason\":\"回家\",\"days\":5}",
            RequestId = "gate2-start-" + mark,
        });
        var task = ApprovalTask.FindPending(inst.Id).Single();
        var agreeId = "gate2-ag-" + mark;

        Assert.Equal(4031, Assert.Throws<ApprovalException>(() =>
            ApprovalInstance.Agree(task.Id, stranger.ID, "越权", "choose-1-x", inst.Version, "127.0.0.1")).Code);
        Assert.Equal(4031, Assert.Throws<ApprovalException>(() =>
            ApprovalInstance.Transfer(task.Id, stranger.ID, target.ID, "转走", "gate2-tr-" + mark, inst.Version, "127.0.0.1")).Code);
        Assert.Equal(4031, Assert.Throws<ApprovalException>(() =>
            ApprovalInstance.AddSign(task.Id, stranger.ID, target.ID, "加签", "gate2-add-" + mark, inst.Version, "127.0.0.1")).Code);
        Assert.Equal(4031, Assert.Throws<ApprovalException>(() =>
            ApprovalInstance.Withdraw(inst.Id, stranger.ID, "撤", "gate2-wd-" + mark, inst.Version, "127.0.0.1")).Code);
        Assert.Equal(Approval.Data.Entities.TaskStatus.Pending, ApprovalTask.FindById(task.Id)!.Status);

        inst = ApprovalInstance.Agree(task.Id, approver.ID, "同意", agreeId, inst.Version, "127.0.0.1", "{\"days\":4}");
        Assert.Equal(InstanceStatus.Approved, inst.Status);
        var history = ApprovalHistory.FindAllByInstanceId(inst.Id).Count;
        var hits = 0;
        Action<Int64> onChanged = id =>
        {
            if (id == inst.Id) hits++;
        };
        ApprovalHost.InstanceChanged += onChanged;
        try
        {
            var replay = ApprovalInstance.Agree(task.Id, approver.ID, "同意", agreeId, inst.Version, "127.0.0.1");
            Assert.Equal(inst.Id, replay.Id);
            Assert.Equal(InstanceStatus.Approved, replay.Status);
            Assert.Equal(1, hits);
        }
        finally
        {
            ApprovalHost.InstanceChanged -= onChanged;
        }

        Assert.Equal(history, ApprovalHistory.FindAllByInstanceId(inst.Id).Count);
    }

    private static ApprovalProcess Publish(String mark, Int32 approverId)
    {
        var form = new ApprovalFormDefinition { Code = "gate-" + mark, Name = "门禁表单", Enable = true };
        form.Insert();
        form.SaveDraft(Schema);
        form.Publish(0);
        var process = new ApprovalProcess { Code = "gatep-" + mark, Name = "门禁流程", FormId = form.Id, Enable = true };
        process.Insert();
        process.SaveDraft(
            "{\"nodes\":[" +
            "{\"key\":\"s\",\"type\":\"start\",\"name\":\"开始\",\"fields\":[{\"key\":\"studentUserId\",\"access\":\"readonly\"},{\"key\":\"reason\",\"access\":\"editable\"},{\"key\":\"days\",\"access\":\"editable\"}]}," +
            "{\"key\":\"x\",\"type\":\"exclusive\",\"name\":\"天数\"}," +
            "{\"key\":\"a\",\"type\":\"approve\",\"name\":\"审批\",\"mode\":\"any\",\"assignee\":{\"type\":\"user\",\"userIds\":[" + approverId + "]},\"fields\":[{\"key\":\"days\",\"access\":\"editable\"},{\"key\":\"reason\",\"access\":\"readonly\"}]}," +
            "{\"key\":\"e\",\"type\":\"end\",\"name\":\"结束\"}]," +
            "\"edges\":[" +
            "{\"key\":\"e1\",\"from\":\"s\",\"to\":\"x\"}," +
            "{\"key\":\"e2\",\"from\":\"x\",\"to\":\"a\",\"priority\":1,\"condition\":{\"field\":\"days\",\"op\":\"ge\",\"value\":3}}," +
            "{\"key\":\"e3\",\"from\":\"x\",\"to\":\"e\",\"default\":true,\"priority\":9}," +
            "{\"key\":\"e4\",\"from\":\"a\",\"to\":\"e\"}]}");
        process.Publish(0);
        return process;
    }

    private static User UserOf(String name, String display)
    {
        var role = Role.Add("gate-" + name, false, "门禁");
        var user = User.Add(name, "pass1234", role.ID, display);
        user.Enable = true;
        user.Update();
        return user;
    }
}
