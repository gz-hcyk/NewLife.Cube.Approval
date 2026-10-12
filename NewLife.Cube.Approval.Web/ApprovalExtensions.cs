using Approval.Data.Entities;
using Approval.Web.Areas.Approval.Controllers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.DependencyInjection;
using NewLife.Cube;
using NewLife.Log;
using XCode;
using XCode.DataAccessLayer;
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
    /// <param name="enableTenant">
    /// 为 true（默认）时开启魔方内置多租户 <c>CubeSetting.EnableTenant</c>（Shadow 兼容期：
    /// 旧客户端不带租户标识时放行并记录影子日志，切 Enforce 后 fail-closed）。
    /// 审批 12 张表均带 TenantId 列，查询过滤与写入回填由 <see cref="XCode.Membership.TenantInterceptor"/> 自动完成，
    /// 审批菜单按 Admin|Tenant 双可见声明。传 false 时不干预宿主既有配置。
    /// </param>
    /// <returns>同一个服务集合。</returns>
    public static IServiceCollection AddApproval(this IServiceCollection services, Boolean scanLov = true, Boolean enableTenant = true)
    {
        ArgumentNullException.ThrowIfNull(services);

        // UseCube 的 InitAll 只看到已经加载的程序集。
        _ = typeof(ApprovalInstance).Assembly;

        // 多租户默认开启。租户识别由 DataScopeMiddleware 按 X-Tenant 头/查询串/Cookie 解析，
        // 实体层拦截器按 TenantContext.CurrentId 自动过滤查询、回填写入，管理后台（TenantId=0）看全量。
        if (enableTenant) CubeSetting.Current.EnableTenant = true;

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
        DropLegacyUniqueIndexes();
        GrantAdmin();
        return app;
    }

    /// <summary>
    /// 清理 1.0 遗留的全局唯一索引。租户化后 Code、No 改为 (TenantId, Code/No) 联合唯一，
    /// 残留的旧索引会挡住跨租户同编码、同单据号写入。新库没有这些索引，直接空转。
    /// XCode 迁移（Migration.On）只登记不执行删索引，这里主动补齐；失败不阻断启动。
    /// </summary>
    private static void DropLegacyUniqueIndexes()
    {
        var legacy = new (String Table, String Index)[]
        {
            ("ApprovalCategory", "IU_ApprovalCategory_Code"),
            ("ApprovalFormDefinition", "IU_ApprovalFormDefinition_Code"),
            ("ApprovalProcess", "IU_ApprovalProcess_Code"),
            ("ApprovalInstance", "IU_ApprovalInstance_No"),
        };
        try
        {
            var dal = DAL.Create("Approval");
            var meta = dal.Db.CreateMetaData();
            var tables = meta.GetTables();
            foreach (var (table, index) in legacy)
            {
                var dt = tables.FirstOrDefault(e => String.Equals(e.TableName, table, StringComparison.OrdinalIgnoreCase));
                if (dt == null) continue;
                var di = dt.Indexes.FirstOrDefault(e => String.Equals(e.Name, index, StringComparison.OrdinalIgnoreCase));
                if (di == null) continue;
                var sql = meta.GetSchemaSQL(DDLSchema.DropIndex, di);
                if (!sql.IsNullOrEmpty()) dal.Execute(sql);
                XTrace.WriteLine("清理审批遗留唯一索引[{0}]：{1}", index, sql);
            }
        }
        catch (Exception ex)
        {
            // 清理失败不阻断启动；最坏是升级库保留旧索引，留待运维手工处理
            XTrace.WriteException(ex);
        }
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
