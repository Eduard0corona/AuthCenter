using AuthCenter.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Infrastructure.Services;

internal static class SignInRecording
{
    /// <summary>
    /// Records a successful sign-in on the account. Concurrent sign-ins of one account race on its
    /// concurrency stamp: the loser reloads the row and applies its change again, instead of
    /// leaving a stale modified user for the next save of the request (which would then fail the
    /// whole sign-in).
    /// </summary>
    public static async Task RecordSignInAsync(
        this UserManager<ApplicationUser> users,
        DbContext db,
        ApplicationUser user,
        DateTime now,
        CancellationToken ct,
        Action<ApplicationUser>? change = null)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            user.LastLoginAt = now;
            user.UpdatedAt = now;
            change?.Invoke(user);
            if ((await users.UpdateAsync(user)).Succeeded)
                return;
            await db.Entry(user).ReloadAsync(ct);
        }
    }
}
