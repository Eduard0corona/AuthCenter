using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Applications;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Requests.Governance;
using AuthCenter.Contracts.Requests.Groups;
using AuthCenter.Contracts.Requests.Roles;
using AuthCenter.Contracts.Requests.Users;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Applications;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Contracts.Responses.Governance;
using AuthCenter.Contracts.Responses.Groups;
using AuthCenter.Contracts.Responses.Roles;
using AuthCenter.Contracts.Responses.Users;
using AuthCenter.Domain.Constants;
using AuthCenter.Domain.Enums;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthCenter.IntegrationTests;

public sealed class AccessGovernanceTests(AuthCenterWebApplicationFactory factory) : IClassFixture<AuthCenterWebApplicationFactory>
{
    private readonly AccessGovernanceScenarios _scenarios = new(factory, AuthCenterWebApplicationFactory.AdminEmail, AuthCenterWebApplicationFactory.AdminPassword);

    [Fact] public Task APortalRequest_IsDecidedByAnOwner_NeverByTheRequester() => _scenarios.APortalRequest_IsDecidedByAnOwner_NeverByTheRequester();
    [Fact] public Task PendingRegistrations_AreRequestsTheOwnersDecide_AndARejectedOneIsNoLongerPending() => _scenarios.PendingRegistrations_AreRequestsTheOwnersDecide_AndARejectedOneIsNoLongerPending();
    [Fact] public Task SeparationOfDuties_RefusesNewCombinations_AndReportsExistingOnes() => _scenarios.SeparationOfDuties_RefusesNewCombinations_AndReportsExistingOnes();
    [Fact] public Task ApprovingARequest_ChecksSeparationOfDuties() => _scenarios.ApprovingARequest_ChecksSeparationOfDuties();
    [Fact] public Task AnAccessReview_IsDecidedByOwners_AndClosedAtTheDueDate() => _scenarios.AnAccessReview_IsDecidedByOwners_AndClosedAtTheDueDate();
    [Fact] public Task RequestsNobodyDecides_Expire() => _scenarios.RequestsNobodyDecides_Expire();
    [Fact] public Task DeletingAUser_RemovesTheirOwnershipsAndPendingRequests() => _scenarios.DeletingAUser_RemovesTheirOwnershipsAndPendingRequests();
}

/// <summary>The same scenarios on SQL Server, where the governance queries are translated for real.</summary>
public sealed class AccessGovernanceRelationalTests
{
    [RelationalFact]
    public async Task GovernanceScenarios_RunOnSqlServer()
    {
        await using var factory = new SqlServerWebApplicationFactory();
        var scenarios = new AccessGovernanceScenarios(factory, SqlServerWebApplicationFactory.AdminEmail, SqlServerWebApplicationFactory.AdminPassword);
        await scenarios.APortalRequest_IsDecidedByAnOwner_NeverByTheRequester();
        await scenarios.PendingRegistrations_AreRequestsTheOwnersDecide_AndARejectedOneIsNoLongerPending();
        await scenarios.SeparationOfDuties_RefusesNewCombinations_AndReportsExistingOnes();
        await scenarios.ApprovingARequest_ChecksSeparationOfDuties();
        await scenarios.AnAccessReview_IsDecidedByOwners_AndClosedAtTheDueDate();
        await scenarios.RequestsNobodyDecides_Expire();
        await scenarios.DeletingAUser_RemovesTheirOwnershipsAndPendingRequests();
    }
}

internal sealed class AccessGovernanceScenarios(WebApplicationFactory<Program> factory, string adminEmail, string adminPassword)
{
    private readonly WebApplicationFactory<Program> _factory = factory;

    public async Task APortalRequest_IsDecidedByAnOwner_NeverByTheRequester()
    {
        using var admin = await CreateAdminClientAsync();
        var application = await CreateApplicationAsync(admin);
        var role = await CreateRoleAsync(admin, application.Id, "Analista");
        var owner = await CreateUserAsync(admin, "Owner");
        var requester = await CreateUserAsync(admin, "Requester");
        var bystander = await CreateUserAsync(admin, "Bystander");

        // Not requestable until the application takes requests.
        using var requesterClient = await SignInAsync(requester);
        Assert.DoesNotContain(await ReadDataAsync<List<RequestableApplicationDto>>(await requesterClient.GetAsync("/api/auth/access-requests/catalog")),
            item => item.Id == application.Id);
        var settings = await ReadDataAsync<ApplicationGovernanceDto>(await admin.PutAsJsonAsync($"/api/governance/applications/{application.Id}",
            new UpdateApplicationGovernanceRequest { AccessRequestsEnabled = true, OwnerUserIds = [owner.Id], Version = 0 }));
        Assert.Equal(owner.Id, Assert.Single(settings.Owners).Id);
        var stale = await admin.PutAsJsonAsync($"/api/governance/applications/{application.Id}",
            new UpdateApplicationGovernanceRequest { AccessRequestsEnabled = true, OwnerUserIds = [owner.Id], Version = 0 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var catalog = await ReadDataAsync<List<RequestableApplicationDto>>(await requesterClient.GetAsync("/api/auth/access-requests/catalog"));
        var requestable = Assert.Single(catalog, item => item.Id == application.Id);
        Assert.False(requestable.HasAccess);
        Assert.Contains(requestable.Roles, item => item.Id == role.Id);

        Assert.Equal("ACCESS_REQUEST_INVALID", await ErrorCodeAsync(await requesterClient.PostAsJsonAsync("/api/auth/access-requests",
            new CreateAccessRequestRequest { ApplicationSystemId = application.Id, Justification = "  " })));
        var request = await ReadDataAsync<AccessRequestDto>(await requesterClient.PostAsJsonAsync("/api/auth/access-requests",
            new CreateAccessRequestRequest { ApplicationSystemId = application.Id, RoleId = role.Id, Justification = "Cierre contable mensual" }));
        Assert.Equal("Pending", request.Status);
        Assert.Equal("Portal", request.Source);
        Assert.NotNull(request.ExpiresAt);
        Assert.Equal("ACCESS_REQUEST_EXISTS", await ErrorCodeAsync(await requesterClient.PostAsJsonAsync("/api/auth/access-requests",
            new CreateAccessRequestRequest { ApplicationSystemId = application.Id, Justification = "Otra vez" })));
        Assert.Contains((await ReadMailAsync()), mail => mail.ToEmail == owner.Email && mail.Kind == "notification" && mail.Secret.Contains("Cierre contable mensual"));

        // The requester cannot decide it, and neither can someone who does not own the application.
        Assert.Equal(HttpStatusCode.Forbidden, (await requesterClient.PostAsJsonAsync($"/api/auth/approvals/requests/{request.Id}/approve", new DecideAccessRequestRequest())).StatusCode);
        using var bystanderClient = await SignInAsync(bystander);
        Assert.Equal("ACCESS_REQUEST_FORBIDDEN", await ErrorCodeAsync(await bystanderClient.PostAsJsonAsync($"/api/auth/approvals/requests/{request.Id}/approve", new DecideAccessRequestRequest())));

        using var ownerClient = await SignInAsync(owner);
        var approvals = await ReadDataAsync<OwnerApprovalsDto>(await ownerClient.GetAsync("/api/auth/approvals"));
        Assert.Contains(approvals.Requests, item => item.Id == request.Id);
        var approved = await ReadDataAsync<AccessRequestDto>(await ownerClient.PostAsJsonAsync($"/api/auth/approvals/requests/{request.Id}/approve",
            new DecideAccessRequestRequest { Comment = "Aprobado para el cierre" }));
        Assert.Equal("Approved", approved.Status);
        Assert.Equal(owner.Id, approved.DecidedBy!.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await ownerClient.PostAsJsonAsync($"/api/auth/approvals/requests/{request.Id}/reject",
            new DecideAccessRequestRequest { Comment = "Tarde" })).StatusCode);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
            Assert.True(await db.UserApplicationAccesses.AnyAsync(access => access.UserId == requester.Id && access.ApplicationSystemId == application.Id && access.IsActive));
            Assert.True(await db.UserRoles.AnyAsync(userRole => userRole.UserId == requester.Id && userRole.RoleId == role.Id));
            Assert.True(await db.AuditLogs.AnyAsync(log => log.Action == "ACCESS_REQUEST_APPROVED" && log.EntityId == request.Id.ToString() && log.UserId == owner.Id));
        }
        Assert.Contains(await ReadMailAsync(), mail => mail.ToEmail == requester.Email && mail.Secret.Contains("was approved"));
        Assert.Equal("ACCESS_ALREADY_ACTIVE", await ErrorCodeAsync(await requesterClient.PostAsJsonAsync("/api/auth/access-requests",
            new CreateAccessRequestRequest { ApplicationSystemId = application.Id, Justification = "Ya tengo acceso" })));
        var mine = await ReadDataAsync<List<AccessRequestDto>>(await requesterClient.GetAsync("/api/auth/access-requests"));
        Assert.Equal("Approved", Assert.Single(mine, item => item.Id == request.Id).Status);
    }

    public async Task PendingRegistrations_AreRequestsTheOwnersDecide_AndARejectedOneIsNoLongerPending()
    {
        using var admin = await CreateAdminClientAsync();
        var application = await CreateApplicationAsync(admin, "ApprovalRequired");
        var owner = await CreateUserAsync(admin, "Registration owner");
        await ReadDataAsync<ApplicationGovernanceDto>(await admin.PutAsJsonAsync($"/api/governance/applications/{application.Id}",
            new UpdateApplicationGovernanceRequest { OwnerUserIds = [owner.Id] }));

        var first = await RegisterAsync(application.Code);
        var second = await RegisterAsync(application.Code);
        var requests = await ReadDataAsync<PagedResult<AccessRequestDto>>(await admin.GetAsync($"/api/governance/access-requests?status=Pending&applicationSystemId={application.Id}"));
        Assert.Equal(2, requests.TotalCount);
        Assert.All(requests.Items, item => Assert.Equal("Registration", item.Source));
        Assert.Contains(await ReadMailAsync(), mail => mail.ToEmail == owner.Email && mail.Secret.Contains("waits for approval"));

        // Rejecting needs a reason the user will read; afterwards the access is not pending anymore.
        var firstRequest = requests.Items.Single(item => item.Requester.Id == first);
        Assert.Equal("ACCESS_REQUEST_INVALID", await ErrorCodeAsync(await admin.PostAsJsonAsync($"/api/governance/access-requests/{firstRequest.Id}/reject", new DecideAccessRequestRequest())));
        var rejected = await ReadDataAsync<AccessRequestDto>(await admin.PostAsJsonAsync($"/api/governance/access-requests/{firstRequest.Id}/reject",
            new DecideAccessRequestRequest { Comment = "No trabaja en el área" }));
        Assert.Equal("Rejected", rejected.Status);
        // A revoked access is never approved back by the pending-access shortcut.
        Assert.Equal("ACCESS_NOT_PENDING", await ErrorCodeAsync(await admin.PatchAsync($"/api/users/{first}/applications/{application.Id}/approve", null)));

        // Approving from the user's page approves the request behind the pending access.
        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsync($"/api/users/{second}/applications/{application.Id}/approve", null)).StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        Assert.Equal(AccessRequestStatus.Approved, (await db.AccessRequests.SingleAsync(item => item.UserId == second)).Status);
        Assert.True((await db.UserApplicationAccesses.SingleAsync(item => item.UserId == second && item.ApplicationSystemId == application.Id)).IsActive);
        var firstAccess = await db.UserApplicationAccesses.SingleAsync(item => item.UserId == first && item.ApplicationSystemId == application.Id);
        Assert.False(firstAccess.IsActive);
        Assert.NotNull(firstAccess.RevokedAt);
    }

    public async Task SeparationOfDuties_RefusesNewCombinations_AndReportsExistingOnes()
    {
        using var admin = await CreateAdminClientAsync();
        var application = await CreateApplicationAsync(admin);
        var requester = await CreateRoleAsync(admin, application.Id, "Solicitante de pagos");
        var approver = await CreateRoleAsync(admin, application.Id, "Aprobador de pagos");
        var user = await CreateUserAsync(admin, "Payments", application.Id, [requester.Id]);
        var rule = await ReadDataAsync<SeparationOfDutiesRuleDto>(await admin.PostAsJsonAsync("/api/governance/sod-rules", new SeparationOfDutiesRuleRequest
        {
            Name = $"Pagos {Guid.NewGuid():N}"[..20],
            Description = "Quien solicita un pago no lo aprueba.",
            FirstRoleId = requester.Id,
            SecondRoleId = approver.Id
        }));
        Assert.Equal(0, rule.ViolationCount);
        Assert.Equal("SOD_RULE_EXISTS", await ErrorCodeAsync(await admin.PostAsJsonAsync("/api/governance/sod-rules", new SeparationOfDutiesRuleRequest
        {
            Name = "Invertida",
            FirstRoleId = approver.Id,
            SecondRoleId = requester.Id
        })));

        // Directly, through a new membership, or through a role given to the user's group: refused.
        Assert.Equal("SOD_CONFLICT", await ErrorCodeAsync(await admin.PostAsync($"/api/users/{user.Id}/roles/{approver.Id}", null)));
        var approvers = await CreateGroupAsync(admin, application.Id, [approver.Id]);
        Assert.Equal("SOD_CONFLICT", await ErrorCodeAsync(await admin.PostAsync($"/api/groups/{approvers.Id}/members/{user.Id}", null)));
        var team = await CreateGroupAsync(admin, application.Id, []);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/groups/{team.Id}/members/{user.Id}", null)).StatusCode);
        Assert.Equal("SOD_CONFLICT", await ErrorCodeAsync(await admin.PostAsync($"/api/groups/{team.Id}/roles/{approver.Id}", null)));
        Assert.Equal("SOD_CONFLICT", await ErrorCodeAsync(await admin.PutAsJsonAsync($"/api/groups/{team.Id}/access",
            new SetDirectoryGroupAccessRequest { ApplicationSystemIds = [application.Id], RoleIds = [approver.Id] })));

        // With the rule off the combination is possible; turned on again, it is reported.
        var paused = await ReadDataAsync<SeparationOfDutiesRuleDto>(await admin.PutAsJsonAsync($"/api/governance/sod-rules/{rule.Id}", new SeparationOfDutiesRuleRequest
        {
            Name = rule.Name, Description = rule.Description, FirstRoleId = requester.Id, SecondRoleId = approver.Id, IsActive = false, Version = rule.Version
        }));
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/groups/{approvers.Id}/members/{user.Id}", null)).StatusCode);
        var resumed = await ReadDataAsync<SeparationOfDutiesRuleDto>(await admin.PutAsJsonAsync($"/api/governance/sod-rules/{rule.Id}", new SeparationOfDutiesRuleRequest
        {
            Name = rule.Name, Description = rule.Description, FirstRoleId = requester.Id, SecondRoleId = approver.Id, IsActive = true, Version = paused.Version
        }));
        Assert.Equal(1, resumed.ViolationCount);
        var violations = await ReadDataAsync<PagedResult<SeparationOfDutiesViolationDto>>(await admin.GetAsync($"/api/governance/sod-violations?ruleId={rule.Id}"));
        var violation = Assert.Single(violations.Items);
        Assert.Equal(user.Id, violation.User.Id);
        Assert.True(violation.FirstRole.Direct);
        Assert.Equal(approvers.Name, Assert.Single(violation.SecondRole.Groups));
        Assert.True((await ReadDataAsync<AdminDashboardDto>(await admin.GetAsync("/api/admin-dashboard"))).SeparationOfDutiesViolations >= 1);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        Assert.True(await db.AuditLogs.CountAsync(log => log.Action == "SOD_CONFLICT_BLOCKED" && log.EntityId == user.Id.ToString()) >= 4);
    }

    public async Task ApprovingARequest_ChecksSeparationOfDuties()
    {
        using var admin = await CreateAdminClientAsync();
        var application = await CreateApplicationAsync(admin);
        var buyer = await CreateRoleAsync(admin, application.Id, "Comprador");
        var receiver = await CreateRoleAsync(admin, application.Id, "Receptor");
        var user = await CreateUserAsync(admin, "Buyer", application.Id, [buyer.Id]);
        await ReadDataAsync<SeparationOfDutiesRuleDto>(await admin.PostAsJsonAsync("/api/governance/sod-rules", new SeparationOfDutiesRuleRequest
        {
            Name = $"Compras {Guid.NewGuid():N}"[..20], FirstRoleId = buyer.Id, SecondRoleId = receiver.Id
        }));
        await ReadDataAsync<ApplicationGovernanceDto>(await admin.PutAsJsonAsync($"/api/governance/applications/{application.Id}",
            new UpdateApplicationGovernanceRequest { AccessRequestsEnabled = true }));
        await GrantAuthCenterAccessAsync(admin, user.Id);

        using var client = await SignInAsync(user);
        var request = await ReadDataAsync<AccessRequestDto>(await client.PostAsJsonAsync("/api/auth/access-requests",
            new CreateAccessRequestRequest { ApplicationSystemId = application.Id, RoleId = receiver.Id, Justification = "Recibir mercancía" }));
        var refused = await admin.PostAsJsonAsync($"/api/governance/access-requests/{request.Id}/approve", new DecideAccessRequestRequest());
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("SOD_CONFLICT", await ErrorCodeAsync(refused));
        Assert.Equal("Pending", (await ReadDataAsync<AccessRequestDto>(await admin.GetAsync($"/api/governance/access-requests/{request.Id}"))).Status);
    }

    public async Task AnAccessReview_IsDecidedByOwners_AndClosedAtTheDueDate()
    {
        using var admin = await CreateAdminClientAsync();
        var application = await CreateApplicationAsync(admin);
        var owner = await CreateUserAsync(admin, "Review owner", application.Id);
        var kept = await CreateUserAsync(admin, "Kept", application.Id);
        var unreviewed = await CreateUserAsync(admin, "Unreviewed", application.Id);
        var grouped = await CreateUserAsync(admin, "Grouped");
        var group = await CreateGroupAsync(admin, application.Id, []);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/groups/{group.Id}/members/{grouped.Id}", null)).StatusCode);
        await ReadDataAsync<ApplicationGovernanceDto>(await admin.PutAsJsonAsync($"/api/governance/applications/{application.Id}",
            new UpdateApplicationGovernanceRequest { OwnerUserIds = [owner.Id] }));

        var campaign = await ReadDataAsync<AccessReviewCampaignDto>(await admin.PostAsJsonAsync("/api/governance/access-reviews", new CreateAccessReviewRequest
        {
            Name = "Revisión trimestral", ApplicationSystemId = application.Id, DueAt = DateTime.UtcNow.AddDays(14), RevokeUnreviewed = true, RecurrenceMonths = 3
        }));
        Assert.Equal(4, campaign.TotalItems);
        Assert.Equal("ACCESS_REVIEW_ACTIVE_EXISTS", await ErrorCodeAsync(await admin.PostAsJsonAsync("/api/governance/access-reviews", new CreateAccessReviewRequest
        {
            Name = "Otra", ApplicationSystemId = application.Id, DueAt = DateTime.UtcNow.AddDays(14)
        })));
        Assert.Contains(await ReadMailAsync(), mail => mail.ToEmail == owner.Email && mail.Secret.Contains("4 accesses"));

        await GrantAuthCenterAccessAsync(admin, owner.Id);
        using var ownerClient = await SignInAsync(owner);
        Assert.Contains((await ReadDataAsync<OwnerApprovalsDto>(await ownerClient.GetAsync("/api/auth/approvals"))).Reviews, item => item.Id == campaign.Id);
        var items = (await ReadDataAsync<PagedResult<AccessReviewItemDto>>(await ownerClient.GetAsync($"/api/auth/approvals/reviews/{campaign.Id}/items?pageSize=50"))).Items;
        Assert.False(items.Single(item => item.User.Id == owner.Id).CanDecide);
        var groupedItem = items.Single(item => item.User.Id == grouped.Id);
        Assert.False(groupedItem.HasDirectAccess);
        Assert.Equal(group.Name, Assert.Single(groupedItem.Groups));

        Assert.Equal("SELF_REVIEW_FORBIDDEN", await ErrorCodeAsync(await ownerClient.PostAsJsonAsync($"/api/auth/approvals/reviews/{campaign.Id}/items/{items.Single(item => item.User.Id == owner.Id).Id}",
            new DecideAccessReviewItemRequest { Decision = "Keep" })));
        var keep = await ReadDataAsync<AccessReviewItemDto>(await ownerClient.PostAsJsonAsync($"/api/auth/approvals/reviews/{campaign.Id}/items/{items.Single(item => item.User.Id == kept.Id).Id}",
            new DecideAccessReviewItemRequest { Decision = "Keep", Comment = "Sigue en el equipo" }));
        Assert.Equal("Keep", keep.Decision);
        var revokeGrouped = await ReadDataAsync<AccessReviewItemDto>(await ownerClient.PostAsJsonAsync($"/api/auth/approvals/reviews/{campaign.Id}/items/{groupedItem.Id}",
            new DecideAccessReviewItemRequest { Decision = "Revoke" }));
        Assert.True(revokeGrouped.RemediationRequired);
        Assert.Contains(group.Name, revokeGrouped.Outcome);
        Assert.Equal("ACCESS_REVIEW_ITEM_DECIDED", await ErrorCodeAsync(await ownerClient.PostAsJsonAsync($"/api/auth/approvals/reviews/{campaign.Id}/items/{groupedItem.Id}",
            new DecideAccessReviewItemRequest { Decision = "Keep" })));
        // An administrator decides the owner's own access.
        await ReadDataAsync<AccessReviewItemDto>(await admin.PostAsJsonAsync($"/api/governance/access-reviews/{campaign.Id}/items/{items.Single(item => item.User.Id == owner.Id).Id}/decision",
            new DecideAccessReviewItemRequest { Decision = "Keep" }));

        // The due date closes the campaign: nobody reviewed one access, which is revoked; three months after it started, it runs again.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
            var stored = await db.AccessReviewCampaigns.SingleAsync(item => item.Id == campaign.Id);
            stored.DueAt = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
            Assert.True(await scope.ServiceProvider.GetRequiredService<IAccessReviewService>().RunDueWorkAsync() >= 1);
        }
        var completed = await ReadDataAsync<AccessReviewCampaignDto>(await admin.GetAsync($"/api/governance/access-reviews/{campaign.Id}"));
        Assert.Equal("Completed", completed.Status);
        Assert.Equal(0, completed.PendingItems);
        Assert.Equal(2, completed.RevokedItems);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
            Assert.False((await db.UserApplicationAccesses.SingleAsync(item => item.UserId == unreviewed.Id && item.ApplicationSystemId == application.Id)).IsActive);
            Assert.True((await db.UserApplicationAccesses.SingleAsync(item => item.UserId == kept.Id && item.ApplicationSystemId == application.Id)).IsActive);
            Assert.True((await db.AccessReviewItems.SingleAsync(item => item.CampaignId == campaign.Id && item.UserId == unreviewed.Id)).DecidedAutomatically);

            var stored = await db.AccessReviewCampaigns.SingleAsync(item => item.Id == campaign.Id);
            stored.CreatedAt = DateTime.UtcNow.AddMonths(-3).AddMinutes(-1);
            await db.SaveChangesAsync();
            var service = scope.ServiceProvider.GetRequiredService<IAccessReviewService>();
            Assert.Equal(1, await service.RunDueWorkAsync());
            Assert.Equal(0, await service.RunDueWorkAsync());
        }
        var next = (await ReadDataAsync<PagedResult<AccessReviewCampaignDto>>(await admin.GetAsync($"/api/governance/access-reviews?status=Active&applicationSystemId={application.Id}"))).Items.Single();
        Assert.Equal(campaign.Id, next.PreviousCampaignId);
        Assert.Equal(3, next.RecurrenceMonths);
        // The unreviewed user lost the access; the grouped one still has it through the group.
        Assert.Equal(3, next.TotalItems);
    }

    public async Task RequestsNobodyDecides_Expire()
    {
        using var admin = await CreateAdminClientAsync();
        var application = await CreateApplicationAsync(admin);
        var owner = await CreateUserAsync(admin, "Slow owner");
        var user = await CreateUserAsync(admin, "Waiting");
        await ReadDataAsync<ApplicationGovernanceDto>(await admin.PutAsJsonAsync($"/api/governance/applications/{application.Id}",
            new UpdateApplicationGovernanceRequest { AccessRequestsEnabled = true, OwnerUserIds = [owner.Id] }));
        using var client = await SignInAsync(user);
        var request = await ReadDataAsync<AccessRequestDto>(await client.PostAsJsonAsync("/api/auth/access-requests",
            new CreateAccessRequestRequest { ApplicationSystemId = application.Id, Justification = "Necesito los reportes" }));

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
            var stored = await db.AccessRequests.SingleAsync(item => item.Id == request.Id);
            stored.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
            Assert.True(await scope.ServiceProvider.GetRequiredService<IAccessGovernanceService>().ExpireRequestsAsync() >= 1);
        }
        Assert.Equal("Expired", (await ReadDataAsync<AccessRequestDto>(await admin.GetAsync($"/api/governance/access-requests/{request.Id}"))).Status);
        using var ownerClient = await SignInAsync(owner);
        Assert.Equal(HttpStatusCode.Conflict, (await ownerClient.PostAsJsonAsync($"/api/auth/approvals/requests/{request.Id}/approve", new DecideAccessRequestRequest())).StatusCode);
        Assert.Contains(await ReadMailAsync(), mail => mail.ToEmail == user.Email && mail.ApplicationName!.Contains("expired"));

        // The requester may ask again, and cancel it.
        var again = await ReadDataAsync<AccessRequestDto>(await client.PostAsJsonAsync("/api/auth/access-requests",
            new CreateAccessRequestRequest { ApplicationSystemId = application.Id, Justification = "Sigo necesitándolos" }));
        Assert.Equal("Cancelled", (await ReadDataAsync<AccessRequestDto>(await client.PostAsync($"/api/auth/access-requests/{again.Id}/cancel", null))).Status);
    }

    public async Task DeletingAUser_RemovesTheirOwnershipsAndPendingRequests()
    {
        using var admin = await CreateAdminClientAsync();
        var application = await CreateApplicationAsync(admin);
        var owner = await CreateUserAsync(admin, "Leaving owner");
        var inactive = await CreateUserAsync(admin, "Inactive");
        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsync($"/api/users/{inactive.Id}/deactivate", null)).StatusCode);
        Assert.Equal("GOVERNANCE_INVALID", await ErrorCodeAsync(await admin.PutAsJsonAsync($"/api/governance/applications/{application.Id}",
            new UpdateApplicationGovernanceRequest { OwnerUserIds = [inactive.Id] })));
        await ReadDataAsync<ApplicationGovernanceDto>(await admin.PutAsJsonAsync($"/api/governance/applications/{application.Id}",
            new UpdateApplicationGovernanceRequest { AccessRequestsEnabled = true, OwnerUserIds = [owner.Id] }));
        using (var client = await SignInAsync(owner))
        {
            var other = await CreateApplicationAsync(admin);
            await ReadDataAsync<ApplicationGovernanceDto>(await admin.PutAsJsonAsync($"/api/governance/applications/{other.Id}",
                new UpdateApplicationGovernanceRequest { AccessRequestsEnabled = true }));
            await ReadDataAsync<AccessRequestDto>(await client.PostAsJsonAsync("/api/auth/access-requests",
                new CreateAccessRequestRequest { ApplicationSystemId = other.Id, Justification = "Antes de irme" }));
        }

        await admin.AddReauthenticationProofAsync(adminPassword, "admin.user.delete");
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/users/{owner.Id}")).StatusCode);

        Assert.Empty((await ReadDataAsync<ApplicationGovernanceDto>(await admin.GetAsync($"/api/governance/applications/{application.Id}"))).Owners);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        Assert.Equal(AccessRequestStatus.Cancelled, (await db.AccessRequests.SingleAsync(item => item.UserId == owner.Id)).Status);
    }

    private async Task<HttpClient> CreateAdminClientAsync() =>
        await SignInAsync(new TestUser(Guid.Empty, adminEmail, adminPassword));

    private async Task<HttpClient> SignInAsync(TestUser user)
    {
        var client = _factory.CreateClient();
        var auth = await ReadDataAsync<AuthResponse>(await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = user.Email,
            Password = user.Password,
            ApplicationCode = DomainConstants.SystemCodes.AuthCenter
        }));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    /// <summary>A user who can sign in to AuthCenter (for the portal endpoints), optionally with access to an application.</summary>
    private async Task<TestUser> CreateUserAsync(HttpClient admin, string name, Guid? applicationId = null, IReadOnlyList<Guid>? roleIds = null)
    {
        var email = $"{name.Replace(' ', '-').ToLowerInvariant()}-{Guid.NewGuid():N}@example.com";
        var password = TestSecretGenerator.CreatePassword();
        var user = await ReadDataAsync<UserDto>(await admin.PostAsJsonAsync("/api/users", new CreateUserRequest
        {
            FullName = name,
            Email = email,
            Password = password,
            GrantApplicationAccess = applicationId.HasValue,
            ApplicationSystemId = applicationId,
            RoleIds = roleIds ?? []
        }));
        await GrantAuthCenterAccessAsync(admin, user.Id);
        return new TestUser(user.Id, email, password);
    }

    private async Task GrantAuthCenterAccessAsync(HttpClient admin, Guid userId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var authCenter = await db.ApplicationSystems.Where(item => item.Code == DomainConstants.SystemCodes.AuthCenter).Select(item => item.Id).SingleAsync();
        if (!await db.UserApplicationAccesses.AnyAsync(item => item.UserId == userId && item.ApplicationSystemId == authCenter && item.IsActive))
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/users/{userId}/applications/{authCenter}", null)).StatusCode);
    }

    private static async Task<ApplicationDto> CreateApplicationAsync(HttpClient admin, string registrationMode = "Open")
    {
        var code = $"GOV{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        return await ReadDataAsync<ApplicationDto>(await admin.PostAsJsonAsync("/api/applications", new CreateApplicationRequest
        {
            Code = code,
            Name = $"Aplicación {code}",
            RegistrationMode = registrationMode,
            AllowPasswordLogin = true
        }));
    }

    private static async Task<RoleDto> CreateRoleAsync(HttpClient admin, Guid applicationId, string name) =>
        await ReadDataAsync<RoleDto>(await admin.PostAsJsonAsync("/api/roles", new CreateRoleRequest { Name = name, ApplicationSystemId = applicationId }));

    private static async Task<DirectoryGroupDto> CreateGroupAsync(HttpClient admin, Guid applicationId, IReadOnlyList<Guid> roleIds)
    {
        var group = await ReadDataAsync<DirectoryGroupDto>(await admin.PostAsJsonAsync("/api/groups",
            new CreateDirectoryGroupRequest { Name = $"Grupo {Guid.NewGuid():N}"[..20] }));
        await ReadDataAsync<DirectoryGroupDto>(await admin.PutAsJsonAsync($"/api/groups/{group.Id}/access",
            new SetDirectoryGroupAccessRequest { ApplicationSystemIds = [applicationId], RoleIds = roleIds }));
        return group;
    }

    private async Task<Guid> RegisterAsync(string applicationCode)
    {
        var email = $"registered-{Guid.NewGuid():N}@example.com";
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            FullName = "Registered User",
            Email = email,
            Password = TestSecretGenerator.CreatePassword(),
            ApplicationCode = applicationCode
        });
        Assert.Equal("APPROVAL_REQUIRED", await ErrorCodeAsync(response));
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        return await db.Users.Where(user => user.Email == email).Select(user => user.Id).SingleAsync();
    }

    private async Task<IReadOnlyList<OutboxMail>> ReadMailAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await OutboxMail.ReadAsync(scope.ServiceProvider);
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        Assert.False(response.IsSuccessStatusCode, $"Expected a failure, got {(int)response.StatusCode}.");
        return (await response.Content.ReadFromJsonAsync<ApiResponse<object>>())?.ErrorCode;
    }

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response) where T : class
    {
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<T>>();
        Assert.True(response.IsSuccessStatusCode, $"Expected success but got {(int)response.StatusCode} {body?.ErrorCode}: {body?.Message}");
        Assert.NotNull(body?.Data);
        return body.Data;
    }

    private sealed record TestUser(Guid Id, string Email, string Password);
}
