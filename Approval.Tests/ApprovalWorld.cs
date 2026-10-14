using Xunit;
using XCode;
using XCode.DataAccessLayer;

namespace Approval.Tests;

/// <summary>整个测试进程共用一份临时库。XCode 的连接名是进程级的。</summary>
public sealed class ApprovalWorld : IDisposable
{
    public String Root { get; }

    public String Membership { get; }

    public String Cube { get; }

    public String Log { get; }

    public String Approval { get; }

    public ApprovalWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "approval-slice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Membership = Conn("Membership.db");
        Cube = Conn("Cube.db");
        Log = Conn("Log.db");
        Approval = Conn("Approval.db");

        XCodeSetting.Current.Migration = Migration.On;
        Register("Membership", Membership);
        Register("Cube", Cube);
        Register("Log", Log);
        Register("Approval", Approval);

        EntityFactory.InitAll();
    }

    private String Conn(String file)
    {
        // BusyTimeout 不能写成 "Busy Timeout"：System.Data.SQLite 对带空格的关键字静默忽略，
        // 退化成约 3 秒默认忙等，宿主启动期（建表/扫菜单）的写锁会让首条写请求直接 database is locked。
        var path = Path.Combine(Root, file).Replace('\\', '/');
        return $"Data Source={path};Provider=sqlite;Journal Mode=Wal;BusyTimeout=30000";
    }

    private static void Register(String name, String connStr)
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__" + name, connStr);
        DAL.AddConnStr(name, connStr, null, null);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, true);
        }
        catch
        {
            // 测试进程退出时连接可能仍占用文件，残留目录不影响结论。
        }
    }
}

[CollectionDefinition("approval")]
public sealed class ApprovalCollection : ICollectionFixture<ApprovalWorld>
{
}
