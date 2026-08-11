using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthCenter.Application.Interfaces;
using AuthCenter.Application.Models;
using AuthCenter.Domain.Entities;
using AuthCenter.Domain.Enums;
using AuthCenter.Infrastructure.Persistence;
using AuthCenter.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthCenter.Infrastructure.Services;

public sealed class AuthenticationRiskService : IAuthenticationRiskService
{
    private readonly IDbContextFactory<AuthCenterDbContext> _factory;
    private readonly IDateTimeProvider _clock;
    private readonly AdaptiveAuthenticationSettings _settings;

    public AuthenticationRiskService(IDbContextFactory<AuthCenterDbContext> factory, IDateTimeProvider clock, IOptions<AdaptiveAuthenticationSettings> settings)
    {
        _factory = factory;
        _clock = clock;
        _settings = settings.Value;
    }

    public async Task<AuthenticationSignalAssessment> AssessAndRecordAsync(
        Guid userId,
        string? ipAddress,
        string? userAgent,
        decimal? latitude = null,
        decimal? longitude = null,
        CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var networkHash = Hash(NetworkPrefix(ipAddress));
        var deviceHash = Hash(NormalizeDevice(userAgent));
        await using var db = await _factory.CreateDbContextAsync(ct);
        var recent = await db.AuthenticationObservations.AsNoTracking()
            .Where(item => item.UserId == userId && item.ExpiresAt > now)
            .OrderByDescending(item => item.ObservedAt)
            .Take(50)
            .ToListAsync(ct);

        var reasons = new List<string>();
        var risk = AccessRiskLevel.Low;
        if (recent.Count == 0)
            reasons.Add("FIRST_OBSERVATION");
        else
        {
            if (recent.All(item => item.NetworkHash != networkHash)) { reasons.Add("NEW_NETWORK"); risk = AccessRiskLevel.Medium; }
            if (recent.All(item => item.DeviceHash != deviceHash)) { reasons.Add("NEW_DEVICE"); risk = Max(risk, AccessRiskLevel.Medium); }
            if (reasons.Contains("NEW_NETWORK") && reasons.Contains("NEW_DEVICE")) risk = AccessRiskLevel.High;

            var previousGeo = recent.FirstOrDefault(item => item.Latitude.HasValue && item.Longitude.HasValue);
            if (latitude.HasValue && longitude.HasValue && previousGeo is not null)
            {
                var hours = Math.Max((now - previousGeo.ObservedAt).TotalHours, 1d / 60d);
                var speed = DistanceKm((double)previousGeo.Latitude!.Value, (double)previousGeo.Longitude!.Value, (double)latitude.Value, (double)longitude.Value) / hours;
                if (speed > _settings.MaximumTravelSpeedKmh) { reasons.Add("IMPOSSIBLE_TRAVEL"); risk = AccessRiskLevel.Critical; }
            }
        }

        var failedSince = now.AddMinutes(-_settings.FailedEventWindowMinutes);
        var failures = await db.AuditLogs.CountAsync(log => log.UserId == userId && log.CreatedAt >= failedSince &&
            (log.Action.Contains("FAILED") || log.Action.Contains("INVALID")), ct);
        if (failures >= _settings.FailedEventThreshold) { reasons.Add("ANOMALOUS_FAILURE_BURST"); risk = Max(risk, AccessRiskLevel.High); }

        db.AuthenticationObservations.Add(new AuthenticationObservation
        {
            Id = Guid.NewGuid(), UserId = userId, NetworkHash = networkHash, DeviceHash = deviceHash,
            Latitude = latitude.HasValue ? decimal.Round(latitude.Value, 2) : null,
            Longitude = longitude.HasValue ? decimal.Round(longitude.Value, 2) : null,
            RiskLevel = risk, ReasonCodesJson = JsonSerializer.Serialize(reasons), ObservedAt = now,
            ExpiresAt = now.AddDays(_settings.ObservationRetentionDays)
        });
        await db.SaveChangesAsync(ct);
        return new AuthenticationSignalAssessment(risk, reasons);
    }

    private string Hash(string value)
    {
        var key = string.IsNullOrEmpty(_settings.SignalHashKey) ? "development-only-adaptive-auth-key" : _settings.SignalHashKey;
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(value)));
    }

    private static string NetworkPrefix(string? value)
    {
        if (!IPAddress.TryParse(value, out var address)) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4) return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24";
        Array.Clear(bytes, 7, bytes.Length - 7);
        return $"{new IPAddress(bytes)}/56";
    }

    private static string NormalizeDevice(string? value) => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim().ToLowerInvariant()[..Math.Min(value.Trim().Length, 256)];
    private static AccessRiskLevel Max(AccessRiskLevel left, AccessRiskLevel right) => left > right ? left : right;
    private static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double radius = 6371;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return radius * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}
