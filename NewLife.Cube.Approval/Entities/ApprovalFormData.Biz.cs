using XCode;
using XCode.Membership;

namespace Approval.Data.Entities;

public partial class ApprovalFormData : Entity<ApprovalFormData>
{
    static ApprovalFormData()
    {
        Meta.Interceptors.Add<TenantInterceptor>();
    }
}
