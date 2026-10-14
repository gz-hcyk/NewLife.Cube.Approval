using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.Serialization;
using System.Web.Script.Serialization;
using System.Xml.Serialization;
using NewLife;
using NewLife.Data;
using XCode;
using XCode.Cache;
using XCode.Configuration;
using XCode.Membership;
using XCode.DataAccessLayer;

namespace Approval.Data.Entities;

/// <summary>委托代理范围。框架 PrincipalAgent 委托在审批侧生效的流程或分类，无范围行表示全部待办。</summary>
[Serializable]
[DataObject]
[Description("委托代理范围。框架 PrincipalAgent 委托在审批侧生效的流程或分类，无范围行表示全部待办")]
[BindIndex("IX_ApprovalAgentScope_TenantId", false, "TenantId")]
[BindIndex("IX_ApprovalAgentScope_AgentId", false, "AgentId")]
[BindIndex("IU_ApprovalAgentScope_AgentId_ScopeType_ScopeId", true, "AgentId,ScopeType,ScopeId")]
[BindTable("ApprovalAgentScope", Description = "委托代理范围。框架 PrincipalAgent 委托在审批侧生效的流程或分类，无范围行表示全部待办", ConnName = "Approval", DbType = DatabaseType.None)]
public class ApprovalAgentScope : Entity<ApprovalAgentScope>, ITenantScope
{
    static ApprovalAgentScope()
    {
        Meta.Interceptors.Add(new UserInterceptor { AllowEmpty = true });
        Meta.Interceptors.Add<TimeInterceptor>();
        Meta.Interceptors.Add(new IPInterceptor { AllowEmpty = true });
        Meta.Interceptors.Add<TenantInterceptor>();
    }

    #region 属性
    private Int32 _Id;
    /// <summary>编号</summary>
    [DisplayName("编号")]
    [Description("编号")]
    [DataObjectField(true, true, false, 0)]
    [BindColumn("Id", "编号", "")]
    public Int32 Id { get => _Id; set { if (OnPropertyChanging(nameof(Id), value)) { _Id = value; OnPropertyChanged(nameof(Id)); } } }
    private Int32 _TenantId;
    /// <summary>租户</summary>
    [DisplayName("租户")]
    [Description("租户")]
    [DataObjectField(false, false, false, 0)]
    [BindColumn("TenantId", "租户", "")]
    public Int32 TenantId { get => _TenantId; set { if (OnPropertyChanging(nameof(TenantId), value)) { _TenantId = value; OnPropertyChanged(nameof(TenantId)); } } }

    private Int32 _AgentId;
    /// <summary>委托编号。框架 PrincipalAgent.Id</summary>
    [DisplayName("委托编号")]
    [Description("委托编号。框架 PrincipalAgent.Id")]
    [DataObjectField(false, false, false, 0)]
    [BindColumn("AgentId", "委托编号", "")]
    public Int32 AgentId { get => _AgentId; set { if (OnPropertyChanging(nameof(AgentId), value)) { _AgentId = value; OnPropertyChanged(nameof(AgentId)); } } }

    private Approval.Data.Entities.AgentScope _ScopeType;
    /// <summary>范围类型</summary>
    [DisplayName("范围类型")]
    [Description("范围类型")]
    [DataObjectField(false, false, false, 0)]
    [BindColumn("ScopeType", "范围类型", "")]
    public Approval.Data.Entities.AgentScope ScopeType { get => _ScopeType; set { if (OnPropertyChanging(nameof(ScopeType), value)) { _ScopeType = value; OnPropertyChanged(nameof(ScopeType)); } } }

    private Int32 _ScopeId;
    /// <summary>范围编号。流程或分类的 Id</summary>
    [DisplayName("范围编号")]
    [Description("范围编号。流程或分类的 Id")]
    [DataObjectField(false, false, false, 0)]
    [BindColumn("ScopeId", "范围编号", "")]
    public Int32 ScopeId { get => _ScopeId; set { if (OnPropertyChanging(nameof(ScopeId), value)) { _ScopeId = value; OnPropertyChanged(nameof(ScopeId)); } } }
    #endregion

    /// <summary>获取或设置字段值。插入和更新都走这里。</summary>
    public override Object? this[String name]
    {
        get => name switch
        {
            "Id" => _Id,
            "TenantId" => _TenantId,
            "AgentId" => _AgentId,
            "ScopeType" => _ScopeType,
            "ScopeId" => _ScopeId,
            _ => base[name]
        };
        set
        {
            switch (name)
            {
                case "Id": _Id = value.ToInt(); break;
                case "TenantId": _TenantId = value.ToInt(); break;
                case "AgentId": _AgentId = value.ToInt(); break;
                case "ScopeType": _ScopeType = (Approval.Data.Entities.AgentScope)value.ToInt(); break;
                case "ScopeId": _ScopeId = value.ToInt(); break;
                default: base[name] = value; break;
            }
        }
    }

    #region 扩展查询
    /// <summary>按编号查找。</summary>
    public static ApprovalAgentScope? FindById(Int32 id)
    {
        if (id <= 0) return null;
        Meta.Cache?.Clear("find", true);
        return FindAll().FirstOrDefault(e => e.Id == id);
    }

    /// <summary>某条委托的全部范围行。</summary>
    public static IList<ApprovalAgentScope> FindAllByAgentId(Int32 agentId)
    {
        if (agentId <= 0) return [];
        return FindAll(_.AgentId == agentId);
    }
    #endregion

    #region 字段名
    /// <summary>取得委托代理范围字段信息的快捷方式</summary>
    public partial class _
    {
        /// <summary>编号</summary>
        public static readonly Field Id = FindByName("Id");

        /// <summary>租户</summary>
        public static readonly Field TenantId = FindByName("TenantId");

        /// <summary>委托编号。框架 PrincipalAgent.Id</summary>
        public static readonly Field AgentId = FindByName("AgentId");

        /// <summary>范围类型</summary>
        public static readonly Field ScopeType = FindByName("ScopeType");

        /// <summary>范围编号。流程或分类的 Id</summary>
        public static readonly Field ScopeId = FindByName("ScopeId");

        static Field FindByName(String name) => Meta.Table.FindByName(name)!;
    }

    /// <summary>取得委托代理范围字段名称的快捷方式</summary>
    public partial class __
    {
        /// <summary>编号</summary>
        public const String Id = "Id";

        /// <summary>租户</summary>
        public const String TenantId = "TenantId";

        /// <summary>委托编号。框架 PrincipalAgent.Id</summary>
        public const String AgentId = "AgentId";

        /// <summary>范围类型</summary>
        public const String ScopeType = "ScopeType";

        /// <summary>范围编号。流程或分类的 Id</summary>
        public const String ScopeId = "ScopeId";
    }
    #endregion
}
