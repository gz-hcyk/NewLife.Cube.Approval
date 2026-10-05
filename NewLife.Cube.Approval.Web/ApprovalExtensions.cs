using Approval.Data.Entities;
using Approval.Web.Areas.Approval.Controllers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.DependencyInjection;
using NewLife.Cube;
using NewLife.Log;
using XCode.Membership;

namespace NewLife.Cube.Approval;

/// <summary>把通用审批注册进 Cube WebApi 宿主。</summary>
public static class ApprovalExtensions
{
    /// <summary>
    /// 加载审批实体，挂上办理和设计控制器，并扫描审批枚举的 LOV。
    /// 调用前先 <c>AddCube()</c>。宿主若已经为自己的枚举调用过 <c>AddCubeLov</c>，把 <paramref name="scanLov"/> 设为 false，并把 <c>Approval.Data.Entities</c> 加进原来的扫描命名空间。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="scanLov">为 true 时扫描审批枚举命名空间。</param>
    /// <returns>同一个服务集合。</returns>
    public static IServiceCollection AddApproval(this IServiceCollection services, Boolean scanLov = true)
    {
        ArgumentNullException.ThrowIfNull(services);

        // UseCube 的 InitAll 只看到已经加载的程序集。
        _ = typeof(ApprovalInstance).Assembly;

        var mvc = services.AddControllers(options =>
        {
            options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
        });
        var assembly = typeof(RuntimeController).Assembly;
        if (!mvc.PartManager.ApplicationParts.OfType<AssemblyPart>().Any(part => part.Assembly == assembly))
            mvc.PartManager.ApplicationParts.Add(new AssemblyPart(assembly));

        if (scanLov)
            services.AddCubeLov(options => options.ScanNamespace(typeof(InstanceStatus).Namespace!));

        return services;
    }

    /// <summary>
    /// 挂上 LOV 中间件，并把审批菜单上的权限位授给管理员角色。
    /// 这些自定义位超出 <c>PermissionFlags.All</c>，系统角色不会自动带上。调用前先 <c>UseCube</c>。
    /// 宿主若已经调用过 <c>UseCubeLov</c>，把 <paramref name="useLov"/> 设为 false。
    /// </summary>
    /// <param name="app">应用。</param>
    /// <param name="useLov">为 true 时调用 <c>UseCubeLov</c>。</param>
    /// <returns>同一个应用。</returns>
    public static WebApplication UseApproval(this WebApplication app, Boolean useLov = true)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (useLov) app.UseCubeLov();
        GrantAdmin();
        return app;
    }

    /// <summary>把名称或地址里带 Approval 的菜单权限授给 admin 的角色。</summary>
    private static void GrantAdmin()
    {
        try
        {
            var admin = User.FindByName("admin");
            if (admin == null || admin.RoleID <= 0) return;
            var role = Role.FindByID(admin.RoleID);
            if (role == null) return;
            var changed = false;
            foreach (var menu in Menu.FindAll())
            {
                var url = (menu.Url ?? "") + (menu.FullName ?? "") + (menu.Name ?? "");
                if (url.IndexOf("Approval", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var mask = 0;
                foreach (var part in (menu.Permission ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var head = part.Split('#')[0];
                    if (Int32.TryParse(head, out var bit)) mask |= bit;
                }

                if (mask == 0) continue;
                role.Set(menu.ID, (PermissionFlags)mask);
                changed = true;
            }

            if (!changed) return;
            role.Update();
            Role.Meta.Cache?.Clear("grant", true);
            Role.Meta.SingleCache.Clear("grant");
            User.Meta.Cache?.Clear("grant", true);
            User.Meta.SingleCache.Clear("grant");
        }
        catch (Exception ex)
        {
            XTrace.WriteException(ex);
        }
    }
}
