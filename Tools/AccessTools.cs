using System.ComponentModel;
using ModelContextProtocol.Server;
using ProxmoxMCPSharp.Services;

namespace ProxmoxMCPSharp.Tools;

[McpServerToolType]
public static class AccessTools
{
    [McpServerTool(Name = "list_access_users", ReadOnly = true, Idempotent = true, OpenWorld = true),
     Description("List Proxmox users visible to this server's identity: userid, enable, expire, comment, groups and realm type.")]
    public static Task<string> ListUsers(ProxmoxClient pve, CancellationToken ct = default) =>
        new AccessManagementService(pve).ListUsers(ct);

    [McpServerTool(Name = "list_access_roles", ReadOnly = true, Idempotent = true, OpenWorld = true),
     Description("List Proxmox roles and their privileges.")]
    public static Task<string> ListRoles(ProxmoxClient pve, CancellationToken ct = default) =>
        new AccessManagementService(pve).ListRoles(ct);

    [McpServerTool(Name = "list_access_acl", ReadOnly = true, Idempotent = true, OpenWorld = true),
     Description("List ACL entries (path, type, ugid, roleid, propagate). Optionally filter to an exact path and/or a user ('user@realm') or token ('user@realm!tokenid').")]
    public static Task<string> ListAcl(ProxmoxClient pve,
        [Description("Exact ACL path such as '/' or '/nodes/pve1'.")] string? path = null,
        [Description("User 'user@realm' or token 'user@realm!tokenid'.")] string? authId = null,
        CancellationToken ct = default) =>
        new AccessManagementService(pve).ListAcl(path, authId, ct);

    [McpServerTool(Name = "list_user_tokens", ReadOnly = true, Idempotent = true, OpenWorld = true),
     Description("List a user's API token metadata (token id, comment, expiry, privilege separation). Secrets are never returned.")]
    public static Task<string> ListTokens(ProxmoxClient pve,
        [Description("User id, e.g. 'monitor@pve'.")] string userId,
        CancellationToken ct = default) =>
        new AccessManagementService(pve).ListTokens(userId, ct);

    [McpServerTool(Name = "get_access_permissions", ReadOnly = true, Idempotent = true, OpenWorld = true),
     Description("Effective privileges of a user or token at an ACL path (default '/'), with non-.Audit privileges listed separately.")]
    public static Task<string> GetPermissions(ProxmoxClient pve,
        [Description("User 'user@realm' or token 'user@realm!tokenid'.")] string authId,
        [Description("ACL path; default '/'.")] string? path = null,
        CancellationToken ct = default) =>
        new AccessManagementService(pve).Permissions(authId, path, ct);

    [McpServerTool(Name = "create_access_user", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true),
     Description("Preview or create an enabled Proxmox user without a password (for token-only service identities). Refuses existing users. Defaults to dryRun=true; submission needs ReadOnly=false and AllowAccessManagement=true.")]
    public static Task<string> CreateUser(ProxmoxClient pve,
        [Description("New user id, e.g. 'monitor@pve'.")] string userId,
        string? comment = null, bool dryRun = true, CancellationToken ct = default) =>
        new AccessManagementService(pve).CreateUser(userId, comment, dryRun, ct);

    [McpServerTool(Name = "create_user_token", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true),
     Description("Preview or create a privilege-separated API token. The secret is returned exactly once in the successful response and never logged or listed. Never rotates an existing token. Defaults to dryRun=true; submission needs ReadOnly=false and AllowAccessManagement=true.")]
    public static Task<string> CreateToken(ProxmoxClient pve,
        [Description("Existing user id, e.g. 'monitor@pve'.")] string userId,
        [Description("Token id, e.g. 'monitor'; the full id becomes 'user@realm!tokenid'.")] string tokenId,
        string? comment = null,
        [Description("Optional Unix epoch expiry; 0 or omitted means never.")] long? expireEpoch = null,
        bool dryRun = true, CancellationToken ct = default) =>
        new AccessManagementService(pve).CreateToken(userId, tokenId, comment, expireEpoch, dryRun, ct);

    [McpServerTool(Name = "set_access_acl", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true),
     Description("Preview or grant an explicit role to one user or token at an explicit ACL path. Roles with access-administration or console privileges are refused, as is this server's own identity. Defaults to dryRun=true; submission needs ReadOnly=false and AllowAccessManagement=true.")]
    public static Task<string> SetAcl(ProxmoxClient pve,
        [Description("ACL path such as '/', '/nodes/pve1', '/vms/100', '/storage/local'.")] string path,
        [Description("Existing role id such as 'PVEAuditor'.")] string roleId,
        [Description("User 'user@realm' or token 'user@realm!tokenid'.")] string authId,
        bool propagate = true, bool dryRun = true, CancellationToken ct = default) =>
        new AccessManagementService(pve).SetAcl(path, roleId, authId, propagate, dryRun, ct);

    [McpServerTool(Name = "remove_access_acl", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true),
     Description("Preview or remove one existing ACL entry (exact path, role and user/token). Defaults to dryRun=true; submission needs ReadOnly=false, AllowAccessManagement=true and AllowDestroy=true.")]
    public static Task<string> RemoveAcl(ProxmoxClient pve, string path, string roleId,
        [Description("User 'user@realm' or token 'user@realm!tokenid'.")] string authId,
        bool dryRun = true, CancellationToken ct = default) =>
        new AccessManagementService(pve).RemoveAcl(path, roleId, authId, dryRun, ct);

    [McpServerTool(Name = "delete_user_token", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true),
     Description("Preview or revoke an API token, reporting its ACL entries before and after. Defaults to dryRun=true; submission needs ReadOnly=false, AllowAccessManagement=true and AllowDestroy=true.")]
    public static Task<string> DeleteToken(ProxmoxClient pve, string userId, string tokenId, bool dryRun = true, CancellationToken ct = default) =>
        new AccessManagementService(pve).DeleteToken(userId, tokenId, dryRun, ct);

    [McpServerTool(Name = "delete_access_user", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true),
     Description("Preview or delete a user that has no remaining API tokens or ACL entries. root@pam and this server's own identity are refused. Defaults to dryRun=true; submission needs ReadOnly=false, AllowAccessManagement=true and AllowDestroy=true.")]
    public static Task<string> DeleteUser(ProxmoxClient pve, string userId, bool dryRun = true, CancellationToken ct = default) =>
        new AccessManagementService(pve).DeleteUser(userId, dryRun, ct);

    [McpServerTool(Name = "provision_service_identity", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true),
     Description("Preview or provision a new 'pve'-realm user, a privilege-separated token and one explicit role at one explicit path for both (tokens get the intersection of user and token privileges). Reports every step, the token's effective privileges and any partial state; nothing is retried or rolled back. The secret is returned once. Defaults to dryRun=true; submission needs ReadOnly=false and AllowAccessManagement=true.")]
    public static Task<string> Provision(ProxmoxClient pve,
        [Description("New user id in a 'pve' realm, e.g. 'monitor@pve'.")] string userId,
        [Description("Token id, e.g. 'monitor'.")] string tokenId,
        [Description("Existing role id, e.g. 'PVEAuditor'.")] string roleId,
        [Description("ACL path, e.g. '/'.")] string path,
        bool propagate = true, string? comment = null,
        [Description("Optional Unix epoch token expiry; 0 or omitted means never.")] long? expireEpoch = null,
        bool dryRun = true, CancellationToken ct = default) =>
        new AccessManagementService(pve).ProvisionServiceIdentity(userId, tokenId, roleId, path, propagate, comment, expireEpoch, dryRun, ct);
}
