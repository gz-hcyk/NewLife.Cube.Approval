# 通用审批

基于 NewLife.Cube WebApi 的通用审批产品。登录、用户、角色、部门和菜单沿用魔方，不另建一套账号体系。审批负责表单与流程发布、本人发起和代发起、待办办理，以及请假这条示例宿主。

本示例最初在 [cube-webapi-skills](https://github.com/gz-hcyk/cube-webapi-skills) 分支 `cursor/approval-runtime-slice-ea37` 的 `examples/approval` 里开发，现作为独立仓库维护。第 0 轮到第 10 轮的行为都保留。实现细节、接口和有意不做的项见 [IMPLEMENTATION.md](IMPLEMENTATION.md)。

## 怎样运行

需要 .NET 8 SDK 与 Node.js 22。API 监听 `http://127.0.0.1:5052`，管理端开发服务器是 `http://127.0.0.1:3002`，并把 `/api`、`/Auth` 等请求代理到 API。请写 IP，不要写 `localhost`。

```bash
dotnet build
dotnet test
dotnet run --project Approval.Web
```

另开一个终端：

```bash
cd web
npm install --no-audit --no-fund
npm run dev
```

生产构建：

```bash
cd web
npm run build
```

`web/.npmrc` 里 `engine-strict` 为 false。脚手架里的 `lint-staged` 要求的 Node 版本高于部分 22.14 环境，关闭严格引擎检查后可以直接安装。

开发环境 Swagger 在 `http://127.0.0.1:5052/Swagger`。`Config/Cube.config` 里的 `JwtSecret` 只给示例，不能用于生产。

## 账号

进程启动后会写入请假样例，并补两个演示账号。已有同名用户则跳过。

| 账号 | 密码 | 说明 |
|------|------|------|
| `admin` | `admin` | 魔方种子管理员。启动时会把审批菜单上声明的权限位授给该管理员所属角色 |
| `student` | `pass1234` | 学生乙，用于本人发起 |
| `counselor` | `pass1234` | 该生辅导员，用于办理待办 |

三个账号在示例里都挂在管理员角色上，只为能登录办理。

## 目录

| 路径 | 作用 |
|------|------|
| `Approval.sln` | 解决方案 |
| `Approval.Data` | 审批实体、发布与运行时 |
| `Approval.Web` | Cube WebApi 宿主与审批控制器 |
| `Approval.Tests` | 第 0 轮到第 10 轮测试 |
| `web` | TDesign 管理端 |

## 有意不做

- 完整学籍系统。不建学籍、班级和辅导员档案。业务主体仍是魔方用户编号，宿主只有 `LeaveRequest`。
- 令牌表。并行汇聚看任务状态，排他选择记在审批历史的 `choose` 上。
- 流程图上拖拽改线。设计页和办理详情里的图只读；改节点、连线和条件仍走抽屉和对话框。
