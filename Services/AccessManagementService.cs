using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProxmoxMCPSharp.Services;

/// <summary>A Proxmox authentication id: "user@realm" or a privilege-separated token "user@realm!tokenid".</summary>
internal sealed record AccessSubject(string UserId, string? TokenName)
{
    public bool IsToken => TokenName is not null;
    public string Id => TokenName is null ? UserId : UserId + "!" + TokenName;
}

/// <summary>One mutation attempt. Never carries upstream response data, which may contain a token secret.</summary>
internal sealed record AccessStep(string Name, string State, string? Error = null);

internal static class AccessValidation
{
    // PVE accepts a wider user name alphabet; this server only creates and manages conservative identifiers.
    private static readonly Regex UserName = new(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,59}\z");
    private static readonly Regex Realm = new(@"\A[A-Za-z][A-Za-z0-9._-]{1,31}\z");
    private static readonly Regex Token = new(@"\A[A-Za-z][A-Za-z0-9._-]{1,63}\z");
    private static readonly Regex Role = new(@"\A[A-Za-z0-9._-]{1,64}\z");
    private static readonly Regex AclPath = new(@"\A/(?:(?:access|nodes|vms|storage|pool|sdn|mapping)(?:/[A-Za-z0-9._-]+){0,4})?\z");

    // Privileges that can mint further privileges or open a root shell. Never granted through this server.
    public static readonly string[] EscalationPrivileges =
        ["Permissions.Modify", "User.Modify", "Realm.Allocate", "Realm.AllocateUser", "Group.Allocate", "Sys.Console"];

    public static string User(string value)
    {
        var at = value?.LastIndexOf('@') ?? -1;
        if (at <= 0 || value!.Length > 64 || !UserName.IsMatch(value[..at]) || !Realm.IsMatch(value[(at + 1)..]))
            throw new ArgumentException("Expected a user id such as 'monitor@pve' (letters, digits, '.', '_', '-'; realm required).");
        return value;
    }

    public static string TokenName(string value)
    {
        if (value is null || !Token.IsMatch(value)) throw new ArgumentException("Expected a token id of 2-64 characters starting with a letter (letters, digits, '.', '_', '-').");
        return value;
    }

    public static AccessSubject Subject(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("An explicit user or token id is required.");
        var bang = value.IndexOf('!');
        return bang < 0 ? new(User(value), null) : new(User(value[..bang]), TokenName(value[(bang + 1)..]));
    }

    public static string RoleId(string value)
    {
        if (value is null || !Role.IsMatch(value)) throw new ArgumentException("Expected an explicit role id such as 'PVEAuditor'.");
        return value;
    }

    public static string Path(string value)
    {
        if (value is null || value.Length > 128 || !AclPath.IsMatch(value) || value.Split('/').Any(s => s is "." or ".."))
            throw new ArgumentException("Expected an explicit ACL path such as '/', '/nodes/pve1', '/vms/100', '/storage/local' or '/pool/name'.");
        return value;
    }

    public static string? Comment(string? value)
    {
        if (value is null) return null;
        if (value.Length > 255 || value.Any(char.IsControl)) throw new ArgumentException("Comment must be at most 255 characters without control characters.");
        return value;
    }

    public static bool IsAudit(string privilege) => privilege.EndsWith(".Audit", StringComparison.Ordinal);
}

public sealed class AccessManagementService(ProxmoxClient pve)
{
    private static readonly JsonSerializerOptions Output = new(JsonSerializerDefaults.Web);
    private const string SecretNotice = "This is the only time the token secret is returned. Store it now; it cannot be read back. Authorize with 'PVEAPIToken=<tokenId>=<secret>'.";

    // ---- Reads ----

    internal async Task<string> ListUsers(CancellationToken ct) => Serialize(new { data = (await Users(ct)).Select(UserView) });

    internal async Task<string> ListRoles(CancellationToken ct) => Serialize(new { data = (await Roles(ct)).Select(r => DiagnosticReads.Project(r, "roleid", "privs", "special")) });

    internal async Task<string> ListAcl(string? path, string? authId, CancellationToken ct)
    {
        if (path is not null) AccessValidation.Path(path);
        var subject = authId is null ? null : AccessValidation.Subject(authId);
        var entries = (await Acl(ct)).Where(e => (path is null || DiagnosticReads.Text(e, "path") == path) && (subject is null || Matches(e, subject)));
        return Serialize(new { data = entries.Select(AclView) });
    }

    internal async Task<string> ListTokens(string userId, CancellationToken ct)
    {
        AccessValidation.User(userId);
        return Serialize(new { userid = userId, data = (await Tokens(userId, ct)).Select(t => TokenView(userId, t)), secrets = "never-returned-by-listing" });
    }

    internal async Task<string> Permissions(string authId, string? path, CancellationToken ct)
    {
        var subject = AccessValidation.Subject(authId);
        if (path is not null) AccessValidation.Path(path);
        var privileges = await Effective(subject, path ?? "/", ct);
        return Serialize(new { authId = subject.Id, path = path ?? "/", privileges, nonAuditPrivileges = privileges.Where(p => !AccessValidation.IsAudit(p)) });
    }

    // ---- Single-object mutations ----

    internal async Task<string> CreateUser(string userId, string? comment, bool dryRun, CancellationToken ct)
    {
        AccessValidation.User(userId);
        AccessValidation.Comment(comment);
        Gate("Access user creation", destroy: false, dryRun);
        var blockers = new List<string>();
        var warnings = new List<string>();
        var realm = await RealmType(userId, ct);
        if (realm is null) blockers.Add("Realm does not exist.");
        else if (realm != "pve") warnings.Add($"Realm type '{realm}' authenticates against an external or host password source; use a 'pve' realm for token-only service identities.");
        if (await UserExists(userId, ct)) blockers.Add("User already exists; nothing is replaced.");
        var steps = new List<AccessStep>();
        if (!dryRun)
        {
            Submittable(blockers);
            steps.Add(await Mutate("create-user", HttpMethod.Post, "access/users", new() { ["userid"] = userId, ["comment"] = comment, ["enable"] = true }, ct));
        }
        return await Report("create-user", userId, dryRun, blockers, steps, warnings, ct, readback: async () => new { userPresent = await UserExists(userId, ct) },
            requested: new { userid = userId, comment, enable = true, password = "not-set" });
    }

    internal async Task<string> CreateToken(string userId, string tokenName, string? comment, long? expireEpoch, bool dryRun, CancellationToken ct)
    {
        var subject = new AccessSubject(AccessValidation.User(userId), AccessValidation.TokenName(tokenName));
        AccessValidation.Comment(comment);
        Expiry(expireEpoch);
        Gate("API token creation", destroy: false, dryRun);
        var blockers = new List<string>();
        if (!await UserExists(userId, ct)) blockers.Add("User does not exist; create it first.");
        else if (await TokenExists(subject, ct)) blockers.Add("Token already exists; it is never rotated or replaced. Revoke it explicitly to create a new one.");
        var steps = new List<AccessStep>();
        string? secret = null;
        if (!dryRun)
        {
            Submittable(blockers);
            (var step, secret) = await CreateTokenStep(subject, comment, expireEpoch, ct);
            steps.Add(step);
        }
        return await Report("create-token", subject.Id, dryRun, blockers, steps, [], ct, secret: secret,
            readback: async () => await TokenReadback(subject, ct),
            requested: new { tokenId = subject.Id, privsep = true, comment, expire = expireEpoch ?? 0 });
    }

    internal async Task<string> SetAcl(string path, string roleId, string authId, bool propagate, bool dryRun, CancellationToken ct)
    {
        AccessValidation.Path(path);
        AccessValidation.RoleId(roleId);
        var subject = AccessValidation.Subject(authId);
        Gate("ACL assignment", destroy: false, dryRun);
        var blockers = new List<string>();
        var warnings = new List<string>();
        var role = await RoleEvidence(roleId, blockers, ct);
        SelfGuard(subject, blockers);
        if (!(subject.IsToken ? await TokenExists(subject, ct) : await UserExists(subject.UserId, ct))) blockers.Add("Subject does not exist.");
        var acl = await Acl(ct);
        var existing = acl.Where(e => DiagnosticReads.Text(e, "path") == path && Matches(e, subject) && DiagnosticReads.Text(e, "roleid") == roleId).ToArray();
        if (existing.Any(e => DiagnosticReads.Flag(e, "propagate") == propagate)) blockers.Add("Identical ACL entry already exists.");
        if (subject.IsToken && !UserCovers(acl, subject.UserId, path))
            warnings.Add("The token's user has no ACL entry on this path or a propagating ancestor. Privilege-separated tokens only receive privileges the user also holds.");
        var steps = new List<AccessStep>();
        if (!dryRun)
        {
            Submittable(blockers);
            steps.Add(await Mutate("set-acl", HttpMethod.Put, "access/acl", AclParameters(path, roleId, [subject], propagate, delete: false), ct));
        }
        return await Report("set-acl", subject.Id, dryRun, blockers, steps, warnings, ct,
            before: new { matchingEntries = existing.Select(AclView) },
            requested: new { path, roleid = roleId, authId = subject.Id, propagate, role },
            readback: async () => new { entries = (await Acl(ct)).Where(e => DiagnosticReads.Text(e, "path") == path && Matches(e, subject)).Select(AclView) });
    }

    internal async Task<string> RemoveAcl(string path, string roleId, string authId, bool dryRun, CancellationToken ct)
    {
        AccessValidation.Path(path);
        AccessValidation.RoleId(roleId);
        var subject = AccessValidation.Subject(authId);
        Gate("ACL removal", destroy: true, dryRun);
        var blockers = new List<string>();
        SelfGuard(subject, blockers);
        var existing = (await Acl(ct)).Where(e => DiagnosticReads.Text(e, "path") == path && Matches(e, subject) && DiagnosticReads.Text(e, "roleid") == roleId).ToArray();
        if (existing.Length == 0) blockers.Add("No matching ACL entry exists at this exact path; inherited entries must be removed where they are defined.");
        var steps = new List<AccessStep>();
        if (!dryRun)
        {
            Submittable(blockers);
            steps.Add(await Mutate("remove-acl", HttpMethod.Put, "access/acl", AclParameters(path, roleId, [subject], propagate: null, delete: true), ct));
        }
        return await Report("remove-acl", subject.Id, dryRun, blockers, steps, [], ct,
            before: new { matchingEntries = existing.Select(AclView) },
            requested: new { path, roleid = roleId, authId = subject.Id },
            readback: async () => new { entries = (await Acl(ct)).Where(e => DiagnosticReads.Text(e, "path") == path && Matches(e, subject)).Select(AclView) });
    }

    internal async Task<string> DeleteToken(string userId, string tokenName, bool dryRun, CancellationToken ct)
    {
        var subject = new AccessSubject(AccessValidation.User(userId), AccessValidation.TokenName(tokenName));
        Gate("API token revocation", destroy: true, dryRun);
        var blockers = new List<string>();
        SelfGuard(subject, blockers);
        var token = (await UserExists(userId, ct) ? await Tokens(userId, ct) : []).FirstOrDefault(t => Same(DiagnosticReads.Text(t, "tokenid"), tokenName));
        if (token.ValueKind == JsonValueKind.Undefined) blockers.Add("Token does not exist.");
        var acl = (await Acl(ct)).Where(e => Matches(e, subject)).Select(AclView).ToArray();
        var steps = new List<AccessStep>();
        if (!dryRun)
        {
            Submittable(blockers);
            steps.Add(await Mutate("delete-token", HttpMethod.Delete, TokenPath(subject), [], ct));
        }
        return await Report("delete-token", subject.Id, dryRun, blockers, steps, [], ct,
            before: new { token = token.ValueKind == JsonValueKind.Undefined ? null : TokenView(userId, token), aclEntries = acl },
            readback: async () => new
            {
                tokenPresent = await TokenExists(subject, ct),
                remainingAclEntries = (await Acl(ct)).Where(e => Matches(e, subject)).Select(AclView)
            });
    }

    internal async Task<string> DeleteUser(string userId, bool dryRun, CancellationToken ct)
    {
        AccessValidation.User(userId);
        Gate("Access user deletion", destroy: true, dryRun);
        var subject = new AccessSubject(userId, null);
        var blockers = new List<string>();
        SelfGuard(subject, blockers);
        if (Same(userId, "root@pam")) blockers.Add("root@pam is never deleted.");
        var exists = await UserExists(userId, ct);
        if (!exists) blockers.Add("User does not exist.");
        var tokens = exists ? (await Tokens(userId, ct)).Select(t => TokenView(userId, t)).ToArray() : [];
        var acl = (await Acl(ct)).Where(e => Same(UserOf(e), userId)).Select(AclView).ToArray();
        if (tokens.Length > 0) blockers.Add("User still has API tokens; revoke each with delete_user_token first.");
        if (acl.Length > 0) blockers.Add("User or its tokens still have ACL entries; remove them with remove_access_acl first.");
        var steps = new List<AccessStep>();
        if (!dryRun)
        {
            Submittable(blockers);
            steps.Add(await Mutate("delete-user", HttpMethod.Delete, "access/users/" + DiagnosticReads.Segment(userId), [], ct));
        }
        return await Report("delete-user", userId, dryRun, blockers, steps, [], ct,
            before: new { tokens, aclEntries = acl },
            readback: async () => new { userPresent = await UserExists(userId, ct) });
    }

    // ---- Composite provisioning ----

    internal async Task<string> ProvisionServiceIdentity(string userId, string tokenName, string roleId, string path, bool propagate,
        string? comment, long? expireEpoch, bool dryRun, CancellationToken ct)
    {
        var token = new AccessSubject(AccessValidation.User(userId), AccessValidation.TokenName(tokenName));
        var user = new AccessSubject(userId, null);
        AccessValidation.RoleId(roleId);
        AccessValidation.Path(path);
        AccessValidation.Comment(comment);
        Expiry(expireEpoch);
        Gate("Service identity provisioning", destroy: false, dryRun);
        var blockers = new List<string>();
        var realm = await RealmType(userId, ct);
        if (realm is null) blockers.Add("Realm does not exist.");
        else if (realm != "pve") blockers.Add("Service identities must use a 'pve' realm so the user has no password login.");
        if (await UserExists(userId, ct)) blockers.Add("User already exists; provisioning never reuses, rotates or replaces an existing identity.");
        SelfGuard(user, blockers);
        var role = await RoleEvidence(roleId, blockers, ct);
        var steps = new List<AccessStep>();
        string? secret = null;
        if (!dryRun)
        {
            Submittable(blockers);
            var created = await Mutate("create-user", HttpMethod.Post, "access/users", new() { ["userid"] = userId, ["comment"] = comment, ["enable"] = true }, ct);
            steps.Add(created);
            if (created.State == "completed")
            {
                (var step, secret) = await CreateTokenStep(token, comment, expireEpoch, ct);
                steps.Add(step);
                // The user needs the role too: a privilege-separated token gets the intersection of user and token ACLs.
                steps.Add(step.State == "completed"
                    ? await Mutate("set-acl", HttpMethod.Put, "access/acl", AclParameters(path, roleId, [user, token], propagate, delete: false), ct)
                    : new AccessStep("set-acl", "not-attempted"));
            }
            else
            {
                steps.Add(new AccessStep("create-token", "not-attempted"));
                steps.Add(new AccessStep("set-acl", "not-attempted"));
            }
        }
        return await Report("provision-service-identity", token.Id, dryRun, blockers, steps, [], ct, secret: secret,
            requested: new { userid = userId, tokenId = token.Id, privsep = true, path, roleid = roleId, propagate, aclSubjects = new[] { user.Id, token.Id }, role },
            readback: async () =>
            {
                var tokenState = await TokenReadback(token, ct);
                var entries = (await Acl(ct)).Where(e => DiagnosticReads.Text(e, "path") == path && Same(UserOf(e), userId)).Select(AclView).ToArray();
                object effective;
                try
                {
                    var privileges = await Effective(token, path, ct);
                    effective = new { privileges, nonAuditPrivileges = privileges.Where(p => !AccessValidation.IsAudit(p)) };
                }
                catch (Exception ex) when (Expected(ex)) { effective = new { unavailable = DiagnosticReads.Error(ex) }; }
                return new { userPresent = await UserExists(userId, ct), token = tokenState, aclEntries = entries, tokenEffectivePermissions = effective };
            });
    }

    // ---- Helpers ----

    private void Gate(string operation, bool destroy, bool dryRun)
    {
        if (!pve.Options.EnableAccess) throw new InvalidOperationException("Access tools are disabled.");
        if (dryRun) return;
        if (destroy) pve.EnsureDestroyAllowed(operation); else pve.EnsureWriteAllowed(operation);
        if (!pve.Options.AllowAccessManagement) throw new InvalidOperationException($"{operation} is blocked: set Proxmox:AllowAccessManagement=true to allow access management.");
    }

    private static void Submittable(List<string> blockers)
    {
        if (blockers.Count > 0) throw new InvalidOperationException(string.Join(" ", blockers));
    }

    private static void Expiry(long? expireEpoch)
    {
        if (expireEpoch is null or 0) return;
        if (expireEpoch < DateTimeOffset.UtcNow.ToUnixTimeSeconds() || expireEpoch > 253402300799) throw new ArgumentOutOfRangeException(nameof(expireEpoch), "Expiry must be 0 (never) or a future Unix epoch.");
    }

    private void SelfGuard(AccessSubject subject, List<string> blockers)
    {
        var tokenId = pve.Options.ApiTokenId ?? "";
        var usesToken = tokenId.Trim().Length > 0 && !string.IsNullOrWhiteSpace(pve.Options.ApiTokenSecret);
        var selfUser = usesToken ? tokenId[..Math.Max(0, tokenId.IndexOf('!'))] : pve.Options.Username;
        if (usesToken && Same(subject.Id, tokenId) || !subject.IsToken && Same(subject.UserId, selfUser))
            blockers.Add("This server's own Proxmox identity cannot be changed through itself.");
    }

    private async Task<(AccessStep Step, string? Secret)> CreateTokenStep(AccessSubject token, string? comment, long? expireEpoch, CancellationToken ct)
    {
        var parameters = new Dictionary<string, object?> { ["privsep"] = true, ["comment"] = comment, ["expire"] = expireEpoch is > 0 ? expireEpoch : null };
        try
        {
            var data = DiagnosticReads.Data(await pve.MutateAsync(HttpMethod.Post, TokenPath(token), parameters, ct));
            var secret = DiagnosticReads.Text(data, "value");
            return secret is null || !Same(DiagnosticReads.Text(data, "full-tokenid"), token.Id)
                ? (new AccessStep("create-token", "unknown", "unexpected-response"), null)
                : (new AccessStep("create-token", "completed"), secret);
        }
        catch (Exception ex) when (Expected(ex)) { return (Failed("create-token", ex), null); }
    }

    // Never retries: losing a response does not prove that a mutation failed.
    private async Task<AccessStep> Mutate(string name, HttpMethod method, string path, Dictionary<string, object?> parameters, CancellationToken ct)
    {
        try
        {
            await pve.MutateAsync(method, path, parameters, ct);
            return new AccessStep(name, "completed");
        }
        catch (Exception ex) when (Expected(ex)) { return Failed(name, ex); }
    }

    // A 4xx means the request was refused before any change; everything else may or may not have been applied.
    private static AccessStep Failed(string name, Exception ex) =>
        new(name, ex is ProxmoxApiException { StatusCode: >= (System.Net.HttpStatusCode)400 and < (System.Net.HttpStatusCode)500 } ? "rejected" : "unknown", DiagnosticReads.Error(ex));

    private static bool Expected(Exception ex) => ex is HttpRequestException or ProxmoxApiException or OperationCanceledException or JsonException or ResponseBudgetException or InvalidOperationException or KeyNotFoundException;

    private async Task<string> Report(string action, string subject, bool dryRun, List<string> blockers, List<AccessStep> steps, List<string> warnings,
        CancellationToken ct, Func<Task<object>>? readback = null, object? before = null, object? requested = null, string? secret = null)
    {
        object? observed = null;
        if (!dryRun && !ct.IsCancellationRequested && readback is not null)
        {
            try { observed = await readback(); }
            catch (Exception ex) when (Expected(ex)) { warnings.Add("Readback unavailable: " + DiagnosticReads.Error(ex)); }
        }
        var outcome = dryRun ? "preview" : steps.All(s => s.State == "completed") ? "completed"
            : steps.Any(s => s.State == "unknown") ? "unknown" : steps.Any(s => s.State == "completed") ? "partial" : "rejected";
        if (outcome is "unknown" or "partial") warnings.Add("No step was retried. Reconcile the readback before any further request; completed steps were not rolled back.");
        if (steps.Any(s => s.Name == "create-token" && s.State == "unknown"))
            warnings.Add("If the token now exists, its secret was not received and cannot be recovered; revoke it with delete_user_token and create a new one.");
        Serilog.Log.Information("Access {Action} for {Subject}: preview={Preview}, outcome={Outcome}, secretIssued={SecretIssued}", action, subject, dryRun, outcome, secret is not null);
        return Serialize(new
        {
            schemaVersion = 1,
            observedAtUtc = DateTimeOffset.UtcNow,
            action,
            subject,
            dryRun,
            canSubmit = blockers.Count == 0,
            blockers,
            outcome,
            before,
            requested,
            steps,
            tokenId = secret is null ? null : subject,
            secret,
            secretNotice = secret is null ? null : SecretNotice,
            readback = observed,
            warnings,
            nextAction = dryRun ? "Review blockers and the requested change, then repeat with dryRun=false." :
                outcome == "completed" ? "Verify the readback. Nothing else was changed." : "Inspect steps and readback before deciding on any manual correction."
        });
    }

    private static Dictionary<string, object?> AclParameters(string path, string roleId, AccessSubject[] subjects, bool? propagate, bool delete)
    {
        var parameters = new Dictionary<string, object?> { ["path"] = path, ["roles"] = roleId, ["propagate"] = propagate, ["delete"] = delete ? true : null };
        var users = subjects.Where(s => !s.IsToken).Select(s => s.Id).ToArray();
        var tokens = subjects.Where(s => s.IsToken).Select(s => s.Id).ToArray();
        if (users.Length > 0) parameters["users"] = string.Join(',', users);
        if (tokens.Length > 0) parameters["tokens"] = string.Join(',', tokens);
        return parameters;
    }

    private async Task<object?> RoleEvidence(string roleId, List<string> blockers, CancellationToken ct)
    {
        var role = (await Roles(ct)).FirstOrDefault(r => DiagnosticReads.Text(r, "roleid") == roleId);
        if (role.ValueKind == JsonValueKind.Undefined) { blockers.Add("Role does not exist."); return null; }
        var privileges = (DiagnosticReads.Text(role, "privs") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var escalation = privileges.Intersect(AccessValidation.EscalationPrivileges, StringComparer.Ordinal).ToArray();
        if (escalation.Length > 0) blockers.Add("Role grants access-administration or console privileges (" + string.Join(", ", escalation) + "); it is never assigned through this server.");
        return new { roleid = roleId, privileges, nonAuditPrivileges = privileges.Where(p => !AccessValidation.IsAudit(p)), readOnly = privileges.All(AccessValidation.IsAudit) };
    }

    private async Task<object> TokenReadback(AccessSubject token, CancellationToken ct)
    {
        var observed = (await UserExists(token.UserId, ct) ? await Tokens(token.UserId, ct) : []).FirstOrDefault(t => Same(DiagnosticReads.Text(t, "tokenid"), token.TokenName));
        return observed.ValueKind == JsonValueKind.Undefined
            ? new { tokenPresent = false, privsep = (bool?)null }
            : new { tokenPresent = true, privsep = DiagnosticReads.Flag(observed, "privsep") };
    }

    private async Task<string[]> Effective(AccessSubject subject, string path, CancellationToken ct)
    {
        if (!pve.Options.EnableAccess) throw new InvalidOperationException("Access tools are disabled.");
        var data = DiagnosticReads.Data(await pve.ReadAsync("access/permissions", new Dictionary<string, object?> { ["userid"] = subject.Id, ["path"] = path }, ct, 1024 * 1024));
        return data.TryGetProperty(path, out var privileges) && privileges.ValueKind == JsonValueKind.Object
            ? privileges.EnumerateObject().Where(p => p.Value.ToString() is "1" or "true" or "True").Select(p => p.Name).Order(StringComparer.Ordinal).ToArray()
            : [];
    }

    private static bool UserCovers(JsonElement[] acl, string userId, string path) => acl.Any(e =>
        DiagnosticReads.Text(e, "type") == "user" && Same(DiagnosticReads.Text(e, "ugid"), userId) && DiagnosticReads.Text(e, "path") is { } p &&
        (p == path || (DiagnosticReads.Flag(e, "propagate") != false && (p == "/" || path.StartsWith(p + "/", StringComparison.Ordinal)))));

    private static bool Matches(JsonElement entry, AccessSubject subject) =>
        DiagnosticReads.Text(entry, "type") == (subject.IsToken ? "token" : "user") && Same(DiagnosticReads.Text(entry, "ugid"), subject.Id);

    private static string? UserOf(JsonElement entry)
    {
        var id = DiagnosticReads.Text(entry, "ugid");
        var bang = id?.IndexOf('!') ?? -1;
        return DiagnosticReads.Text(entry, "type") switch { "user" => id, "token" when bang > 0 => id![..bang], _ => null };
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string TokenPath(AccessSubject token) => $"access/users/{DiagnosticReads.Segment(token.UserId)}/token/{DiagnosticReads.Segment(token.TokenName!)}";

    private async Task<string?> RealmType(string userId, CancellationToken ct)
    {
        var realm = userId[(userId.LastIndexOf('@') + 1)..];
        var domain = (await List("access/domains", ct)).FirstOrDefault(d => Same(DiagnosticReads.Text(d, "realm"), realm));
        return domain.ValueKind == JsonValueKind.Undefined ? null : DiagnosticReads.Text(domain, "type") ?? "unknown";
    }

    private async Task<bool> UserExists(string userId, CancellationToken ct) => (await Users(ct)).Any(u => Same(DiagnosticReads.Text(u, "userid"), userId));
    private async Task<bool> TokenExists(AccessSubject token, CancellationToken ct) =>
        await UserExists(token.UserId, ct) && (await Tokens(token.UserId, ct)).Any(t => Same(DiagnosticReads.Text(t, "tokenid"), token.TokenName));

    private Task<JsonElement[]> Users(CancellationToken ct) => List("access/users", ct);
    private Task<JsonElement[]> Roles(CancellationToken ct) => List("access/roles", ct);
    private Task<JsonElement[]> Acl(CancellationToken ct) => List("access/acl", ct);
    private Task<JsonElement[]> Tokens(string userId, CancellationToken ct) => List($"access/users/{DiagnosticReads.Segment(userId)}/token", ct);

    private async Task<JsonElement[]> List(string path, CancellationToken ct)
    {
        if (!pve.Options.EnableAccess) throw new InvalidOperationException("Access tools are disabled.");
        var data = DiagnosticReads.Data(await pve.ReadAsync(path, ct: ct, maxBytes: 1024 * 1024));
        if (data.ValueKind != JsonValueKind.Array) throw new JsonException("Expected an array.");
        return data.EnumerateArray().ToArray();
    }

    private static Dictionary<string, JsonElement> UserView(JsonElement user) => DiagnosticReads.Project(user, "userid", "enable", "expire", "comment", "groups", "realm-type");
    private static Dictionary<string, JsonElement> AclView(JsonElement entry) => DiagnosticReads.Project(entry, "path", "type", "ugid", "roleid", "propagate");
    private static object TokenView(string userId, JsonElement token) => new
    {
        tokenId = userId + "!" + DiagnosticReads.Text(token, "tokenid"),
        comment = DiagnosticReads.Text(token, "comment"),
        expire = DiagnosticReads.Number(token, "expire"),
        privsep = DiagnosticReads.Flag(token, "privsep")
    };

    private static string Serialize(object value) => JsonSerializer.Serialize(value, Output);
}
