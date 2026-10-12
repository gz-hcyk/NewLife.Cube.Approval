using Approval.Data;
using Approval.Data.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NewLife.Cube;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using XCode.Membership;

namespace Approval.Tests;

/// <summary>第 11 轮：多租户。写入回填、查询隔离、跨租户办理拒绝、管理后台全量、影子兼容、菜单双可见。</summary>
[Collection("approval")]
public class Round11Tests
{
    private readonly ApprovalWorld _world;

    public Round11Tests(ApprovalWorld world) => _world = world;

    /// <summary>租户上下文作用域。TenantContext 是 AsyncLocal，用完还原，避免污染同进程后续测试。</summary>
    private sealed class TenantScope : IDisposable
    {
        private readonly TenantContext? _old;

        public TenantScope(Int32 tenantId)
        {
            _old = TenantContext.Current;
            TenantContext.Current = new TenantContext { TenantId = tenantId };
        }

        public void Dispose() => TenantContext.Current = _old;
    }

    [Fact]
    public void Insert_backfills_tenant_and_queries_are_scoped()
    {
        var mark = Guid.NewGuid().ToString("N")[..8];

        Int32 aForm;
        using (new TenantScope(9101))
        {
            var form = NewForm(mark, "甲表单");
            form.Insert();
            Assert.Equal(9101, form.TenantId);
            aForm = form.Id;
        }

        Int32 bForm;
        using (new TenantScope(9202))
        {
            // 联合唯一：同一编码在另一租户可以再建，租户各自管理自己的表单
            var form = NewForm(mark, "乙表单");
            form.Insert();
            Assert.Equal(9202, form.TenantId);
            bForm = form.Id;

            // 跨租户按主键、按编码都查不到
            Assert.Null(ApprovalFormDefinition.FindById(aForm));
            Assert.Single(ApprovalFormDefinition.FindAll(ApprovalFormDefinition._.Code == mark));
        }

        // 管理后台（TenantId=0）不过滤，看全量
        using (new TenantScope(0))
        {
            Assert.NotNull(ApprovalFormDefinition.FindById(aForm));
            Assert.NotNull(ApprovalFormDefinition.FindById(bForm));
            Assert.Equal(2, ApprovalFormDefinition.FindAll(ApprovalFormDefinition._.Code == mark).Count);
        }

        // 无租户上下文（影子兼容期）同样不加过滤
        Assert.NotNull(ApprovalFormDefinition.FindById(aForm));
        Assert.Equal(2, ApprovalFormDefinition.FindAll(ApprovalFormDefinition._.Code == mark).Count);

        // 无上下文写入不回填租户，数据留在管理后台空间
        var loose = NewForm(mark + "-x", "无租户表单");
        loose.Insert();
        Assert.Equal(0, loose.TenantId);
    }

    [Fact]
    public void Engine_start_backfills_all_tables_and_scopes_todo()
    {
        var mark = Guid.NewGuid().ToString("N")[..8];
        var approver = UserOf(mark + "-appr", "审批人");
        var applicant = UserOf(mark + "-app", "学生");

        ApprovalInstance inst;
        using (new TenantScope(9101))
        {
            var process = Publish(mark, approver.ID);
            inst = ApprovalInstance.Start(new ApprovalInstance.StartArgs
            {
                ProcessId = process.Id,
                OperatorUserId = applicant.ID,
                Data = "{\"studentUserId\":" + applicant.ID + ",\"reason\":\"回家\"}",
                RequestId = "r11-start-" + mark,
            });
            Assert.Equal(9101, inst.TenantId);
        }

        // 事务外复核落库值：实例、表单值、待办、历史都带上租户
        using (new TenantScope(9101))
        {
            var todo = ApprovalTask.Inbox(approver.ID);
            Assert.Single(todo);
            Assert.Equal(9101, todo[0].TenantId);

            var formData = ApprovalFormData.FindById(inst.Id);
            Assert.NotNull(formData);
            Assert.Equal(9101, formData!.TenantId);

            var history = ApprovalHistory.FindAllByInstanceId(inst.Id);
            Assert.NotEmpty(history);
            Assert.All(history, h => Assert.Equal(9101, h.TenantId));
        }

        // 别的租户看不到这条单，也没有这个待办和历史
        using (new TenantScope(9202))
        {
            Assert.Null(ApprovalInstance.FindById(inst.Id));
            Assert.Empty(ApprovalTask.Inbox(approver.ID));
            Assert.Empty(ApprovalHistory.FindAllByInstanceId(inst.Id));
        }

        // 管理后台全量可见
        using (new TenantScope(0))
        {
            Assert.NotNull(ApprovalInstance.FindById(inst.Id));
            Assert.Single(ApprovalTask.Inbox(approver.ID));
        }
    }

    [Fact]
    public void Cross_tenant_handle_and_withdraw_are_rejected()
    {
        var mark = Guid.NewGuid().ToString("N")[..8];
        var approver = UserOf(mark + "-appr", "审批人");
        var applicant = UserOf(mark + "-app", "学生");

        ApprovalInstance inst;
        Int64 taskId;
        using (new TenantScope(9101))
        {
            var process = Publish(mark, approver.ID);
            inst = ApprovalInstance.Start(new ApprovalInstance.StartArgs
            {
                ProcessId = process.Id,
                OperatorUserId = applicant.ID,
                Data = "{\"studentUserId\":" + applicant.ID + ",\"reason\":\"回家\"}",
                RequestId = "r11-x-" + mark,
            });
            taskId = ApprovalTask.Inbox(approver.ID)[0].Id;
        }

        using (new TenantScope(9202))
        {
            // 跨租户任务和实例都不可见：办理、撤回按不存在拒绝，不泄露存在性
            var agree = Assert.Throws<ApprovalException>(() =>
                ApprovalInstance.Agree(taskId, approver.ID, "同意", "r11-xa-" + mark, 0, null));
            Assert.Equal(4041, agree.Code);

            var withdraw = Assert.Throws<ApprovalException>(() =>
                ApprovalInstance.Withdraw(inst.Id, applicant.ID, "不撤了", "r11-xw-" + mark, 0, null));
            Assert.Equal(4041, withdraw.Code);

            Assert.Null(ApprovalInstance.FindById(inst.Id));
        }

        // 本租户仍能正常办理，证明拒绝来自租户隔离而不是状态问题
        using (new TenantScope(9101))
        {
            var done = ApprovalInstance.Agree(taskId, approver.ID, "同意", "r11-a-" + mark, 0, null);
            Assert.Equal(InstanceStatus.Approved, done.Status);
        }
    }

    [Fact]
    public void Design_state_is_tenant_scoped()
    {
        var mark = Guid.NewGuid().ToString("N")[..8];
        var approver = UserOf(mark + "-appr", "审批人");

        Int32 formId;
        Int32 processId;
        Int32 versionId;
        using (new TenantScope(9101))
        {
            var process = Publish(mark, approver.ID);
            formId = process.FormId;
            processId = process.Id;
            versionId = process.PublishedVersionId;
        }

        using (new TenantScope(9202))
        {
            Assert.Null(ApprovalFormDefinition.FindById(formId));
            Assert.Null(ApprovalProcess.FindById(processId));
            Assert.Null(ApprovalProcessVersion.FindById(versionId));
            Assert.Empty(ApprovalProcess.FindAll(ApprovalProcess._.Code == mark));
        }

        // 管理后台看全量，可跨租户审计
        using (new TenantScope(0))
        {
            Assert.NotNull(ApprovalFormDefinition.FindById(formId));
            Assert.NotNull(ApprovalProcess.FindById(processId));
            Assert.NotNull(ApprovalProcessVersion.FindById(versionId));
        }
    }

    [Fact]
    public void Menus_declare_admin_and_tenant_visibility()
    {
        foreach (var type in new[]
        {
            typeof(Approval.Web.Areas.Approval.Controllers.RuntimeController),
            typeof(Approval.Web.Areas.ApprovalAdmin.Controllers.FormDefinitionController),
            typeof(Approval.Web.Areas.ApprovalAdmin.Controllers.ProcessController),
            typeof(Approval.Web.Areas.Approval.ApprovalArea),
            typeof(Approval.Web.Areas.ApprovalAdmin.ApprovalAdminArea),
        })
        {
            var att = type.GetCustomAttribute<MenuAttribute>();
            Assert.NotNull(att);
            Assert.True(att!.Mode.HasFlag(MenuModes.Admin), type.Name + " 未声明管理后台可见");
            Assert.True(att.Mode.HasFlag(MenuModes.Tenant), type.Name + " 未声明租户可见");
        }
    }

    [Fact]
    public async Task Invalid_tenant_header_is_rejected_before_auth()
    {
        await using var factory = new ApprovalApiFactory(_world);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/Approval/Runtime/Me");
        request.Headers.Add("X-Tenant", "no-such-" + Guid.NewGuid().ToString("N")[..8]);
        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, text);
        var node = JsonNode.Parse(text)!;
        Assert.Equal(-1, node["code"]!.GetValue<Int32>());
        Assert.Equal("租户不存在或已禁用", node["message"]!.GetValue<String>());
    }

    private static ApprovalFormDefinition NewForm(String code, String name) => new()
    {
        Code = code,
        Name = name,
        Enable = true,
    };

    private static ApprovalProcess Publish(String mark, Int32 approverId)
    {
        var form = new ApprovalFormDefinition { Code = mark + "-form", Name = mark + "表单", Enable = true };
        form.Insert();
        form.SaveDraft("""{"fields":[{"key":"studentUserId"},{"key":"reason"}]}""");
        form.Publish(0);
        var process = new ApprovalProcess { Code = mark, Name = mark + "流程", FormId = form.Id, Enable = true };
        process.Insert();
        process.SaveDraft(Graph(approverId));
        process.Publish(0);
        return process;
    }

    private static String Graph(Int32 approverId) =>
        "{\"nodes\":[" +
        "{\"key\":\"s\",\"type\":\"start\",\"name\":\"开始\"}," +
        "{\"key\":\"a\",\"type\":\"approve\",\"name\":\"审批\",\"mode\":\"any\",\"assignee\":{\"type\":\"user\",\"userIds\":[" + approverId + "]}}," +
        "{\"key\":\"e\",\"type\":\"end\",\"name\":\"结束\"}]," +
        "\"edges\":[" +
        "{\"key\":\"e1\",\"from\":\"s\",\"to\":\"a\"}," +
        "{\"key\":\"e2\",\"from\":\"a\",\"to\":\"e\"}]}";

    private static User UserOf(String name, String display)
    {
        var role = Role.Add("切片填充", false, "切片");
        var user = User.Add(name, "pass1234", role.ID, display);
        user.Enable = true;
        user.Update();
        return user;
    }

    private sealed class ApprovalApiFactory : WebApplicationFactory<Program>
    {
        private readonly ApprovalWorld _world;

        public ApprovalApiFactory(ApprovalWorld world) => _world = world;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<String, String?>
                {
                    ["ConnectionStrings:Membership"] = _world.Membership,
                    ["ConnectionStrings:Cube"] = _world.Cube,
                    ["ConnectionStrings:Log"] = _world.Log,
                    ["ConnectionStrings:Approval"] = _world.Approval,
                });
            });
        }
    }
}
