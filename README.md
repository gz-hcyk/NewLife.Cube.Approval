# NewLife.Cube.Approval

> 给任意 NewLife.Cube WebApi 宿主用的通用审批扩展。引用包并注册，即可得到审批菜单、实体、办理接口和设计接口。用户、角色、权限、部门和审计仍用魔方，不另建账号体系。

第 0 轮到第 10 轮的引擎行为都保留。请假只是示例业务，不在核心包里。实现记录见 [IMPLEMENTATION.md](IMPLEMENTATION.md)。

## 包

| 包 | 版本 | 作用 |
|----|------|------|
| `NewLife.Cube.Approval` | 1.0.0 | 审批实体、发布和运行时。连接名 `Approval` |
| `NewLife.Cube.Approval.Web` | 1.0.0 | 区域、菜单、办理/设计控制器，以及 `AddApproval` / `UseApproval`。依赖上一行 |

本地打包（两个 nupkg 都要放进源，Web 包会依赖引擎包）：

```bash
dotnet pack Approval.sln -c Release -o artifacts
```

## 绿色项目怎么接

已有 Cube WebApi 宿主时，加上 Web 包即可，不必复制控制器或实体。

```xml
<PackageReference Include="NewLife.Cube.Approval.Web" Version="1.0.0" />
```

连接名是 `Approval`。可以单独一个库，也可以和宿主库指向同一个文件。用户和角色仍在 `Membership`。

```json
"ConnectionStrings": {
  "Membership": "Data Source=Data/Membership.db;Provider=sqlite;Journal Mode=Wal;Busy Timeout=30000",
  "Cube": "Data Source=Data/Cube.db;Provider=sqlite;Journal Mode=Wal;Busy Timeout=30000",
  "Log": "Data Source=Data/Log.db;Provider=sqlite;Journal Mode=Wal;Busy Timeout=30000",
  "Approval": "Data Source=Data/Membership.db;Provider=sqlite;Journal Mode=Wal;Busy Timeout=30000"
}
```

`Program.cs` 在原来的 `AddCube` / `UseCube` 旁边各加一行：

```csharp
using NewLife.Cube;
using NewLife.Cube.Approval;
using NewLife.Log;
using ILog = NewLife.Log.ILog;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers(options =>
{
    options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
});
builder.Services.AddCube();
builder.Services.AddApproval();

DefaultTracer.Instance ??= new DefaultTracer();
builder.Services.AddSingleton<ITracer>(_ => DefaultTracer.Instance!);
builder.Services.AddSingleton<ILog>(XTrace.Log);

var app = builder.Build();
app.UseCube(app.Environment);
app.UseApproval();
app.Run();
```

`AddApproval` 会加载审批程序集、挂上控制器，并扫描 `Approval.Data.Entities` 里的枚举 LOV。`UseApproval` 会调用 `UseCubeLov`，并把审批菜单上的自定义权限位授给 `admin` 的角色。这些位超出 `PermissionFlags.All`，不授的话管理员打开待办会 403。

宿主如果已经为自己的枚举调用过 `AddCubeLov` / `UseCubeLov`，改成 `AddApproval(scanLov: false)` 和 `UseApproval(useLov: false)`，并把 `Approval.Data.Entities` 加进原来的 `ScanNamespace`。

启动后菜单来自 `GET /api/Admin/Index/GetMenuTree`：审批办理、表单定义、流程定义。办理接口在 `/api/Approval/Runtime/*`，设计接口在 `/api/ApprovalAdmin/FormDefinition/*` 和 `/api/ApprovalAdmin/Process/*`。

业务单据不要放进核心包。自己的表订阅状态变化，按实例编号回写：

```csharp
using Approval.Data;

ApprovalHost.InstanceChanged += instanceId =>
{
    var inst = ApprovalInstance.FindById(instanceId);
    if (inst == null) return;
    // 按 inst.Status 回写自己的业务单
};
```

同意、驳回、撤回、重新提交（含同一请求号重放）都会触发。引擎里的业务主体仍是魔方用户编号，字段键 `studentUserId`、办理人规则 `subjectCounselor` 是第 0–10 轮的引擎契约，不是请假表。

## 可选的 TDesign 页面

NuGet 包不带前端。`samples/web` 是一份完整的魔方 TDesign 壳，用来演示本仓库的宿主。只要办理台和两个设计页时，把 `samples/web/src/pages/approval/` 拷进自己的前端，并在专用控制器表登记这三行（菜单仍由后端下发，这里只做页面映射）：

- `Approval/Runtime` → 办理台
- `ApprovalAdmin/FormDefinition` → 表单设计
- `ApprovalAdmin/Process` → 流程设计

没有这三页时，接口仍然可用。办理控制器没有 `GetPage`，通用实体列表打不开办理台。

## 本仓库的示例

`samples/Approval.Host` 只引用扩展包，并挂上请假示例 `samples/Approval.Leave`（`LeaveRequest`、`LeaveHost`、启动时写入的请假流程）。核心程序集里没有这些类型。

需要 .NET 8 SDK。API 监听 `http://127.0.0.1:5052`。要看页面再加 Node.js 22，管理端是 `http://127.0.0.1:3002`。请写 IP，不要写 `localhost`。

```bash
dotnet build
dotnet test
dotnet run --project samples/Approval.Host
```

另开一个终端：

```bash
cd samples/web
npm install --no-audit --no-fund
npm run dev
```

`samples/web/.npmrc` 里 `engine-strict` 为 false。开发环境 Swagger 在 `http://127.0.0.1:5052/Swagger`。`Config/Cube.config` 里的 `JwtSecret` 只给示例，不能用于生产。

| 账号 | 密码 | 说明 |
|------|------|------|
| `admin` | `admin` | 魔方种子管理员。`UseApproval` 会把审批菜单上的权限位授给该管理员所属角色 |
| `student` | `pass1234` | 学生乙，用于本人发起 |
| `counselor` | `pass1234` | 该生辅导员，用于办理待办 |

三个账号在示例里都挂在管理员角色上，只为能登录办理。

## 目录

| 路径 | 作用 |
|------|------|
| `NewLife.Cube.Approval` | 可打包的引擎 |
| `NewLife.Cube.Approval.Web` | 可打包的 WebApi 扩展 |
| `samples/Approval.Host` | 薄宿主，只注册扩展并播种请假 |
| `samples/Approval.Leave` | 请假单和状态回写，不进核心包 |
| `samples/web` | 可选 TDesign 页面 |
| `Approval.Tests` | 第 0 轮到第 10 轮测试 |

## 有意不做

- 完整学籍系统。不建学籍、班级和辅导员档案。
- 令牌表。并行汇聚看任务状态，排他选择记在审批历史的 `choose` 上。
- 流程图上拖拽改线。设计页和办理详情里的图只读。
