using System.ComponentModel;
using System.Reflection;
using Approval.Data;
using Approval.Data.Entities;
using Approval.Leave;
using Approval.Web.Areas.Approval.Controllers;
using Xunit;

namespace Approval.Tests;

/// <summary>核心包不包含请假单，业务回写走宿主事件。</summary>
public class ApprovalPackageTests
{
    [Fact]
    [DisplayName("核心程序集没有请假类型")]
    public void Core_assembly_has_no_leave_types()
    {
        var names = typeof(ApprovalInstance).Assembly.GetTypes().Select(type => type.Name).ToHashSet();
        Assert.DoesNotContain("LeaveRequest", names);
        Assert.DoesNotContain("LeaveHost", names);
        Assert.DoesNotContain("LeaveSample", names);
    }

    [Fact]
    [DisplayName("Web 扩展带有办理控制器，请假单在示例程序集")]
    public void Web_extension_hosts_runtime_and_leave_stays_in_sample()
    {
        Assert.Equal("NewLife.Cube.Approval.Web", typeof(RuntimeController).Assembly.GetName().Name);
        Assert.Equal("Approval.Leave", typeof(LeaveRequest).Assembly.GetName().Name);
        Assert.NotNull(typeof(ApprovalHost).GetEvent("InstanceChanged"));
    }
}
