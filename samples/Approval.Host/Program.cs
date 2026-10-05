using Approval.Leave;
using NewLife.Cube;
using NewLife.Cube.Approval;
using NewLife.Log;
using ILog = NewLife.Log.ILog;

var builder = WebApplication.CreateBuilder(args);

// Nullable enable 时，非可空 string 会被隐式当成必填。AddApproval 里也会再设一次。
builder.Services.AddControllers(options =>
{
    options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
});

// 请假单在示例程序集里。UseCube 只会为已加载的程序集建表。
_ = typeof(LeaveRequest).Assembly;

builder.Services.AddCube();
builder.Services.AddApproval();

// AddCube 内部会注册返回 null 的 ITracer 工厂，必须在其后再注册。
DefaultTracer.Instance ??= new DefaultTracer();
builder.Services.AddSingleton<ITracer>(_ => DefaultTracer.Instance!);
builder.Services.AddSingleton<ILog>(XTrace.Log);

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
}

var app = builder.Build();

// 不要手写 EntityFactory.InitAll / InitConnection，UseCube 已内置预热。
app.UseCube(app.Environment);
app.UseApproval();
LeaveHost.Attach();
LeaveSample.Seed();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.RoutePrefix = "Swagger";
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "NewLife.Cube API v1");
    });
}

app.Run();

/// <summary>供测试宿主引用入口。</summary>
public partial class Program
{
}
