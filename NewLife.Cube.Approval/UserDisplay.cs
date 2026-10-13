using Approval.Data.Entities;
using NewLife;
using XCode.Membership;

namespace Approval.Data;

/// <summary>
/// 用户显示名解析。实体只存 UserId，姓名一律在读取时按 UserId 现查，不落库。
/// </summary>
public static class UserDisplay
{
    /// <summary>取用户显示名。显示名为空时退登录名；用户不存在时返回空串。</summary>
    public static String Name(User? user) =>
        user == null ? "" : user.DisplayName.IsNullOrEmpty() ? user.Name : user.DisplayName;

    /// <summary>按用户编号取显示名。用户不存在时返回空串。</summary>
    public static String Name(Int32 userId) => Name(User.FindByID(userId));
}
