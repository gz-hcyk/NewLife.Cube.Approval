using Approval.Data.Entities;
using NewLife.Log;
using XCode.Membership;

namespace Approval.Leave;

/// <summary>
/// 写入请假样例：已发布表单、流程，以及代发起要用的学生和辅导员。
/// 已有同名编码或用户时跳过。辅导员账号挂在管理员角色上，只为示例里能登录办理。
/// </summary>
public static class LeaveSample
{
    /// <summary>补分类、请假表单、请假流程和两个演示账号。失败只记日志。</summary>
    public static void Seed()
    {
        try
        {
            var admin = User.FindByName("admin");
            var roleId = admin?.RoleID ?? 0;
            EnsureUser("student", "pass1234", roleId, "学生乙");
            EnsureUser("counselor", "pass1234", roleId, "该生辅导员");

            var category = ApprovalCategory.FindByCode("student-affairs");
            if (category == null)
            {
                category = new ApprovalCategory
                {
                    Code = "student-affairs",
                    Name = "学工",
                    Sort = 10,
                    Enable = true,
                };
                category.Insert();
            }

            const String schema = """{"fields":[{"key":"studentUserId","label":"学生"},{"key":"counselorUserId","label":"该生辅导员"},{"key":"reason","label":"事由","search":true},{"key":"days","label":"天数"}]}""";
            const String graph = """
            {"nodes":[{"key":"s","type":"start","name":"开始","fields":[{"key":"studentUserId","access":"readonly"},{"key":"counselorUserId","access":"editable"},{"key":"reason","access":"editable"},{"key":"days","access":"hidden"}]},{"key":"c","type":"approve","name":"该生辅导员","mode":"any","assignee":{"type":"subjectCounselor","field":"counselorUserId"},"fields":[{"key":"studentUserId","access":"readonly"},{"key":"counselorUserId","access":"readonly"},{"key":"reason","access":"readonly"},{"key":"days","access":"editable"}]},{"key":"e","type":"end","name":"结束"}],"edges":[{"key":"e1","from":"s","to":"c"},{"key":"e2","from":"c","to":"e"}]}
            """;

            var form = ApprovalFormDefinition.FindByCode("leave");
            if (form == null)
            {
                form = new ApprovalFormDefinition
                {
                    Code = "leave",
                    Name = "请假表单",
                    Enable = true,
                    CategoryId = category.Id,
                };
                form.Insert();
            }
            else if (form.CategoryId != category.Id)
            {
                form.CategoryId = category.Id;
                form.Update();
            }

            var publishedForm = form.PublishedVersionId > 0 ? ApprovalFormVersion.FindById(form.PublishedVersionId) : null;
            if (publishedForm == null || string.IsNullOrEmpty(publishedForm.Schema) || !publishedForm.Schema.Contains("\"days\"", StringComparison.Ordinal))
            {
                form.SaveDraft(schema);
                form.Publish(admin?.ID ?? 0);
            }

            var process = ApprovalProcess.FindByCode("leave");
            if (process == null)
            {
                process = new ApprovalProcess
                {
                    Code = "leave",
                    Name = "请假",
                    FormId = form.Id,
                    Enable = true,
                    CategoryId = category.Id,
                };
                process.Insert();
            }
            else if (process.CategoryId != category.Id)
            {
                process.CategoryId = category.Id;
                process.Update();
            }

            var publishedProcess = process.PublishedVersionId > 0 ? ApprovalProcessVersion.FindById(process.PublishedVersionId) : null;
            var stale = publishedProcess == null
                || string.IsNullOrEmpty(publishedProcess.Definition)
                || !publishedProcess.Definition.Contains("\"access\"", StringComparison.Ordinal)
                || publishedProcess.FormVersionId != form.PublishedVersionId;
            if (stale)
            {
                process.SaveDraft(graph);
                process.Publish(admin?.ID ?? 0);
            }
        }
        catch (Exception ex)
        {
            XTrace.WriteException(ex);
        }
    }

    private static void EnsureUser(String name, String password, Int32 roleId, String displayName)
    {
        if (User.FindByName(name) != null) return;
        var user = User.Add(name, password, roleId, displayName);
        user.Enable = true;
        user.Update();
    }
}
