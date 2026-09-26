using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Profiles;
using AuthCenter.Contracts.Responses.Profiles;
using AuthCenter.Domain.Entities;
using AuthCenter.Domain.Enums;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Infrastructure.Services;

public sealed class UserProfileService : IUserProfileService
{
    private const int MaxDefinitions = 100;
    private const int MaxAllowedValues = 100;
    private const int MaxValueJsonLength = 4000;
    private static readonly Regex KeyPattern = new(
        "^[a-z][a-z0-9_.-]{1,99}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(100));
    private static readonly HashSet<string> ReservedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "email", "username", "fullname", "pictureurl", "isactive", "createdat", "updatedat"
    };

    private readonly AuthCenterDbContext _db;
    private readonly IDateTimeProvider _clock;
    private readonly ICurrentUserService _currentUser;
    private readonly DynamicGroupMembershipService _dynamicGroups;

    public UserProfileService(AuthCenterDbContext db, IDateTimeProvider clock, ICurrentUserService currentUser, DynamicGroupMembershipService dynamicGroups)
    {
        _db = db;
        _clock = clock;
        _currentUser = currentUser;
        _dynamicGroups = dynamicGroups;
    }

    public async Task<IReadOnlyList<ProfileAttributeDefinitionDto>> GetSchemaAsync(
        bool includeInactive,
        CancellationToken ct = default)
    {
        var query = _db.UserProfileAttributeDefinitions.AsNoTracking();
        if (!includeInactive)
            query = query.Where(definition => definition.IsActive);

        return (await query.OrderBy(definition => definition.Key).ToListAsync(ct))
            .Select(MapDefinition)
            .ToList();
    }

    public async Task<OperationResult<ProfileAttributeDefinitionDto>> CreateDefinitionAsync(
        CreateProfileAttributeDefinitionRequest request,
        CancellationToken ct = default)
    {
        var key = request.Key.Trim().ToLowerInvariant();
        if (!KeyPattern.IsMatch(key) || ReservedKeys.Contains(key))
            return Failure("INVALID_PROFILE_ATTRIBUTE_KEY", "Attribute keys must be 2-100 lowercase characters and cannot use a built-in profile field.");

        if (await _db.UserProfileAttributeDefinitions.CountAsync(ct) >= MaxDefinitions)
            return Failure("PROFILE_SCHEMA_LIMIT_REACHED", $"The profile schema supports at most {MaxDefinitions} custom attributes.");

        if (!TryParseDataType(request.DataType, out var dataType))
            return Failure("INVALID_PROFILE_ATTRIBUTE_TYPE", "DataType must be String, Integer, Decimal, Boolean, Date, or DateTime.");

        var validation = ValidateDefinition(
            request.DisplayName, dataType, request.IsRequired, request.DefaultValue,
            request.MinLength, request.MaxLength, request.MinimumNumber, request.MaximumNumber,
            request.ValidationPattern, request.AllowedValues);
        if (!validation.IsSuccess)
            return Failure(validation.ErrorCode, validation.Message);

        var now = _clock.UtcNow;
        var definition = new UserProfileAttributeDefinition
        {
            Id = Guid.NewGuid(),
            Key = key,
            DisplayName = request.DisplayName.Trim(),
            Description = NormalizeOptional(request.Description),
            DataType = dataType,
            IsRequired = request.IsRequired,
            DefaultValueJson = validation.DefaultValueJson,
            MinLength = request.MinLength,
            MaxLength = request.MaxLength,
            MinimumNumber = request.MinimumNumber,
            MaximumNumber = request.MaximumNumber,
            ValidationPattern = NormalizeOptional(request.ValidationPattern),
            AllowedValuesJson = validation.AllowedValuesJson,
            IsActive = true,
            CreatedAt = now
        };
        _db.UserProfileAttributeDefinitions.Add(definition);
        AddAudit("PROFILE_ATTRIBUTE_DEFINITION_CREATED", definition);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return Failure("PROFILE_ATTRIBUTE_KEY_TAKEN", "A profile attribute with this key already exists.");
        }

        return OperationResult<ProfileAttributeDefinitionDto>.Success(MapDefinition(definition));
    }

    public async Task<OperationResult<ProfileAttributeDefinitionDto>> UpdateDefinitionAsync(
        Guid definitionId,
        UpdateProfileAttributeDefinitionRequest request,
        CancellationToken ct = default)
    {
        var definition = await _db.UserProfileAttributeDefinitions
            .Include(candidate => candidate.Values)
            .FirstOrDefaultAsync(candidate => candidate.Id == definitionId, ct);
        if (definition is null)
            return Failure("PROFILE_ATTRIBUTE_NOT_FOUND", "Profile attribute definition not found.");

        if (!TryParseDataType(request.DataType, out var dataType))
            return Failure("INVALID_PROFILE_ATTRIBUTE_TYPE", "DataType must be String, Integer, Decimal, Boolean, Date, or DateTime.");

        var validation = ValidateDefinition(
            request.DisplayName, dataType, request.IsRequired, request.DefaultValue,
            request.MinLength, request.MaxLength, request.MinimumNumber, request.MaximumNumber,
            request.ValidationPattern, request.AllowedValues);
        if (!validation.IsSuccess)
            return Failure(validation.ErrorCode, validation.Message);

        var proposed = new UserProfileAttributeDefinition
        {
            DataType = dataType,
            MinLength = request.MinLength,
            MaxLength = request.MaxLength,
            MinimumNumber = request.MinimumNumber,
            MaximumNumber = request.MaximumNumber,
            ValidationPattern = NormalizeOptional(request.ValidationPattern),
            AllowedValuesJson = validation.AllowedValuesJson
        };
        foreach (var value in definition.Values)
        {
            using var document = JsonDocument.Parse(value.ValueJson);
            var normalized = NormalizeValue(proposed, document.RootElement);
            if (!normalized.IsSuccess)
                return Failure(
                    "PROFILE_EXISTING_VALUES_INVALID",
                    "The schema change would invalidate existing profile values. Update those profiles before changing the definition.");
        }
        if (!definition.TryAdvance(request.Version))
            return Failure(VersionedUpdates.ConflictCode, "The attribute definition changed after it was loaded.");

        definition.DisplayName = request.DisplayName.Trim();
        definition.Description = NormalizeOptional(request.Description);
        definition.DataType = dataType;
        definition.IsRequired = request.IsRequired;
        definition.IsActive = request.IsActive;
        definition.DefaultValueJson = validation.DefaultValueJson;
        definition.MinLength = request.MinLength;
        definition.MaxLength = request.MaxLength;
        definition.MinimumNumber = request.MinimumNumber;
        definition.MaximumNumber = request.MaximumNumber;
        definition.ValidationPattern = NormalizeOptional(request.ValidationPattern);
        definition.AllowedValuesJson = validation.AllowedValuesJson;
        definition.UpdatedAt = _clock.UtcNow;
        AddAudit("PROFILE_ATTRIBUTE_DEFINITION_UPDATED", definition);
        await _db.SaveChangesAsync(ct);
        return OperationResult<ProfileAttributeDefinitionDto>.Success(MapDefinition(definition));
    }

    public async Task<OperationResult> DeactivateDefinitionAsync(Guid definitionId, CancellationToken ct = default)
    {
        var definition = await _db.UserProfileAttributeDefinitions.FindAsync([definitionId], ct);
        if (definition is null)
            return OperationResult.Failure("PROFILE_ATTRIBUTE_NOT_FOUND", "Profile attribute definition not found.");

        if (!definition.IsActive)
            return OperationResult.Success();

        definition.IsActive = false;
        definition.UpdatedAt = _clock.UtcNow;
        AddAudit("PROFILE_ATTRIBUTE_DEFINITION_DEACTIVATED", definition);
        await _db.SaveChangesAsync(ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult<UserProfileDto>> GetUserProfileAsync(Guid userId, CancellationToken ct = default)
    {
        if (!await _db.Users.AnyAsync(user => user.Id == userId, ct))
            return OperationResult<UserProfileDto>.Failure("USER_NOT_FOUND", "User not found.");

        return OperationResult<UserProfileDto>.Success(await BuildProfileAsync(userId, ct));
    }

    public Task<OperationResult<UserProfileDto>> UpdateUserProfileAsync(
        Guid userId,
        UpdateUserProfileRequest request,
        CancellationToken ct = default) => UpdateUserProfileCoreAsync(userId, request, null, ct);

    public Task<OperationResult<UserProfileDto>> UpdateUserProfileFromSourceAsync(
        Guid userId,
        UpdateUserProfileRequest request,
        string sourceSystem,
        CancellationToken ct = default) => UpdateUserProfileCoreAsync(userId, request, sourceSystem, ct);

    private async Task<OperationResult<UserProfileDto>> UpdateUserProfileCoreAsync(
        Guid userId,
        UpdateUserProfileRequest request,
        string? sourceSystem,
        CancellationToken ct)
    {
        if (request.Attributes.Count > MaxDefinitions)
            return OperationResult<UserProfileDto>.Failure("TOO_MANY_PROFILE_ATTRIBUTES", $"At most {MaxDefinitions} attributes can be updated at once.");
        if (!await _db.Users.AnyAsync(user => user.Id == userId, ct))
            return OperationResult<UserProfileDto>.Failure("USER_NOT_FOUND", "User not found.");

        var definitions = await _db.UserProfileAttributeDefinitions
            .Where(definition => definition.IsActive)
            .ToListAsync(ct);
        var byKey = definitions.ToDictionary(definition => definition.Key, StringComparer.OrdinalIgnoreCase);
        var unknown = request.Attributes.Keys.Where(key => !byKey.ContainsKey(key)).OrderBy(key => key).ToList();
        if (unknown.Count > 0)
            return OperationResult<UserProfileDto>.Failure("UNKNOWN_PROFILE_ATTRIBUTE", $"Unknown or inactive profile attributes: {string.Join(", ", unknown)}.");

        var requestedDefinitionIds = request.Attributes.Keys.Select(key => byKey[key].Id).ToArray();
        var authoritativeMappings = await _db.ProfileMappings.AsNoTracking()
            .Where(mapping => mapping.IsActive && mapping.IsAuthoritative && requestedDefinitionIds.Contains(mapping.TargetAttributeDefinitionId))
            .ToListAsync(ct);
        if (authoritativeMappings.Any(mapping => !string.Equals(mapping.SourceSystem, sourceSystem, StringComparison.OrdinalIgnoreCase)))
            return OperationResult<UserProfileDto>.Failure("AUTHORITATIVE_PROFILE_SOURCE", "One or more attributes can only be updated by their configured authoritative source.");

        var normalized = new Dictionary<Guid, string?>();
        foreach (var (key, value) in request.Attributes)
        {
            var definition = byKey[key];
            if (!value.HasValue || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                normalized[definition.Id] = null;
                continue;
            }

            var result = NormalizeValue(definition, value.Value);
            if (!result.IsSuccess)
                return OperationResult<UserProfileDto>.Failure(result.ErrorCode, $"{definition.Key}: {result.Message}");
            normalized[definition.Id] = result.ValueJson;
        }

        var existing = await _db.UserProfileAttributeValues
            .Where(value => value.UserId == userId)
            .ToDictionaryAsync(value => value.AttributeDefinitionId, ct);
        var now = _clock.UtcNow;
        foreach (var (definitionId, valueJson) in normalized)
        {
            if (valueJson is null)
            {
                if (existing.TryGetValue(definitionId, out var toRemove))
                    _db.UserProfileAttributeValues.Remove(toRemove);
                continue;
            }

            if (existing.TryGetValue(definitionId, out var current))
            {
                current.ValueJson = valueJson;
                current.UpdatedAt = now;
            }
            else
            {
                _db.UserProfileAttributeValues.Add(new UserProfileAttributeValue
                {
                    UserId = userId,
                    AttributeDefinitionId = definitionId,
                    ValueJson = valueJson,
                    CreatedAt = now
                });
            }
        }

        var missingRequired = new List<string>();
        foreach (var definition in definitions.Where(candidate => candidate.IsRequired))
        {
            var hasReplacement = normalized.TryGetValue(definition.Id, out var replacement);
            var hasEffectiveValue = hasReplacement
                ? replacement is not null || definition.DefaultValueJson is not null
                : existing.ContainsKey(definition.Id) || definition.DefaultValueJson is not null;
            if (!hasEffectiveValue)
                missingRequired.Add(definition.Key);
        }
        missingRequired.Sort(StringComparer.Ordinal);
        if (missingRequired.Count > 0)
            return OperationResult<UserProfileDto>.Failure(
                "REQUIRED_PROFILE_ATTRIBUTES_MISSING",
                $"Required profile attributes are missing: {string.Join(", ", missingRequired)}.");

        _db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            UserId = _currentUser.UserId,
            Action = "USER_PROFILE_UPDATED",
            EntityName = nameof(ApplicationUser),
            EntityId = userId.ToString(),
            MetadataJson = JsonSerializer.Serialize(new { keys = request.Attributes.Keys.OrderBy(key => key), sourceSystem }),
            CreatedAt = now
        });
        await _db.SaveChangesAsync(ct);
        // Rule-managed groups follow the new values right away, not only after the next SCIM update.
        if (await _dynamicGroups.SynchronizeUserAsync(userId, ct) > 0)
            await _db.SaveChangesAsync(ct);
        return OperationResult<UserProfileDto>.Success(await BuildProfileAsync(userId, ct));
    }

    private async Task<UserProfileDto> BuildProfileAsync(Guid userId, CancellationToken ct)
    {
        var definitions = await _db.UserProfileAttributeDefinitions
            .AsNoTracking()
            .Where(definition => definition.IsActive)
            .OrderBy(definition => definition.Key)
            .ToListAsync(ct);
        var values = await _db.UserProfileAttributeValues
            .AsNoTracking()
            .Where(value => value.UserId == userId)
            .ToDictionaryAsync(value => value.AttributeDefinitionId, value => value.ValueJson, ct);

        var attributes = new List<UserProfileAttributeValueDto>();
        var missing = new List<string>();
        foreach (var definition in definitions)
        {
            var isDefault = !values.TryGetValue(definition.Id, out var json);
            json ??= definition.DefaultValueJson;
            if (json is null)
            {
                if (definition.IsRequired)
                    missing.Add(definition.Key);
                continue;
            }

            attributes.Add(new UserProfileAttributeValueDto
            {
                Key = definition.Key,
                Value = ParseJson(json),
                IsDefault = isDefault
            });
        }

        return new UserProfileDto
        {
            UserId = userId,
            IsValid = missing.Count == 0,
            MissingRequiredAttributes = missing,
            Attributes = attributes
        };
    }

    private static DefinitionValidation ValidateDefinition(
        string displayName,
        ProfileAttributeDataType dataType,
        bool isRequired,
        JsonElement? defaultValue,
        int? minLength,
        int? maxLength,
        decimal? minimumNumber,
        decimal? maximumNumber,
        string? validationPattern,
        IReadOnlyList<JsonElement>? allowedValues)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 200)
            return DefinitionValidation.Failure("INVALID_PROFILE_DISPLAY_NAME", "DisplayName is required and must not exceed 200 characters.");
        if (minLength is < 0 || maxLength is < 1 || minLength > maxLength)
            return DefinitionValidation.Failure("INVALID_PROFILE_LENGTH_RANGE", "MinLength and MaxLength must define a valid non-negative range.");
        if ((minLength.HasValue || maxLength.HasValue || !string.IsNullOrWhiteSpace(validationPattern)) && dataType != ProfileAttributeDataType.String)
            return DefinitionValidation.Failure("INVALID_PROFILE_STRING_CONSTRAINT", "Length and pattern constraints are only valid for String attributes.");
        if (minimumNumber > maximumNumber)
            return DefinitionValidation.Failure("INVALID_PROFILE_NUMBER_RANGE", "MinimumNumber cannot exceed MaximumNumber.");
        if ((minimumNumber.HasValue || maximumNumber.HasValue) && dataType is not (ProfileAttributeDataType.Integer or ProfileAttributeDataType.Decimal))
            return DefinitionValidation.Failure("INVALID_PROFILE_NUMBER_CONSTRAINT", "Numeric constraints are only valid for Integer and Decimal attributes.");
        if (validationPattern?.Length > 500)
            return DefinitionValidation.Failure("INVALID_PROFILE_PATTERN", "ValidationPattern must not exceed 500 characters.");
        if (!string.IsNullOrWhiteSpace(validationPattern))
        {
            try
            {
                _ = new Regex(validationPattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException)
            {
                return DefinitionValidation.Failure("INVALID_PROFILE_PATTERN", "ValidationPattern must be a valid non-backtracking regular expression.");
            }
        }

        if ((allowedValues?.Count ?? 0) > MaxAllowedValues)
            return DefinitionValidation.Failure("TOO_MANY_PROFILE_ALLOWED_VALUES", $"AllowedValues supports at most {MaxAllowedValues} entries.");

        var probe = new UserProfileAttributeDefinition
        {
            DataType = dataType,
            MinLength = minLength,
            MaxLength = maxLength,
            MinimumNumber = minimumNumber,
            MaximumNumber = maximumNumber,
            ValidationPattern = NormalizeOptional(validationPattern)
        };
        var normalizedAllowed = new List<string>();
        foreach (var allowed in allowedValues ?? [])
        {
            var result = NormalizeValue(probe, allowed, checkAllowedValues: false);
            if (!result.IsSuccess)
                return DefinitionValidation.Failure(result.ErrorCode, $"AllowedValues contains an invalid value: {result.Message}");
            normalizedAllowed.Add(result.ValueJson!);
        }
        probe.AllowedValuesJson = normalizedAllowed.Count == 0 ? null : $"[{string.Join(',', normalizedAllowed.Distinct(StringComparer.Ordinal))}]";

        string? defaultJson = null;
        if (defaultValue.HasValue && defaultValue.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            var result = NormalizeValue(probe, defaultValue.Value);
            if (!result.IsSuccess)
                return DefinitionValidation.Failure(result.ErrorCode, $"DefaultValue is invalid: {result.Message}");
            defaultJson = result.ValueJson;
        }
        if (isRequired && defaultJson is null)
            return DefinitionValidation.Failure("PROFILE_REQUIRED_DEFAULT_MISSING", "Required custom attributes must define a valid DefaultValue so existing profiles remain valid.");

        return DefinitionValidation.Success(defaultJson, probe.AllowedValuesJson);
    }

    internal static ValueValidation NormalizeValue(
        UserProfileAttributeDefinition definition,
        JsonElement value,
        bool checkAllowedValues = true)
    {
        try
        {
            object canonical = definition.DataType switch
            {
                ProfileAttributeDataType.String when value.ValueKind == JsonValueKind.String => value.GetString()!,
                // TryGet* throw on other kinds of value (a number in quotes is not a number).
                ProfileAttributeDataType.Integer when value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var integer) => integer,
                ProfileAttributeDataType.Decimal when value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) => number,
                ProfileAttributeDataType.Boolean when value.ValueKind is JsonValueKind.True or JsonValueKind.False => value.GetBoolean(),
                ProfileAttributeDataType.Date when value.ValueKind == JsonValueKind.String &&
                    DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ProfileAttributeDataType.DateTime when value.ValueKind == JsonValueKind.String &&
                    HasExplicitOffset(value.GetString()) &&
                    DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dateTime) => dateTime.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                _ => throw new FormatException()
            };

            if (canonical is string text)
            {
                if (definition.MinLength.HasValue && text.Length < definition.MinLength.Value ||
                    definition.MaxLength.HasValue && text.Length > definition.MaxLength.Value)
                    return ValueValidation.Failure("PROFILE_VALUE_LENGTH_INVALID", "The value does not satisfy the configured length range.");
                if (!string.IsNullOrWhiteSpace(definition.ValidationPattern) &&
                    !Regex.IsMatch(text, definition.ValidationPattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100)))
                    return ValueValidation.Failure("PROFILE_VALUE_PATTERN_MISMATCH", "The value does not match the configured validation pattern.");
            }

            if (canonical is long integerValue &&
                (definition.MinimumNumber.HasValue && integerValue < definition.MinimumNumber.Value ||
                 definition.MaximumNumber.HasValue && integerValue > definition.MaximumNumber.Value))
                return ValueValidation.Failure("PROFILE_VALUE_OUT_OF_RANGE", "The value is outside the configured numeric range.");
            if (canonical is decimal decimalValue &&
                (definition.MinimumNumber.HasValue && decimalValue < definition.MinimumNumber.Value ||
                 definition.MaximumNumber.HasValue && decimalValue > definition.MaximumNumber.Value))
                return ValueValidation.Failure("PROFILE_VALUE_OUT_OF_RANGE", "The value is outside the configured numeric range.");

            var json = JsonSerializer.Serialize(canonical);
            if (json.Length > MaxValueJsonLength)
                return ValueValidation.Failure("PROFILE_VALUE_TOO_LARGE", $"Profile values must not exceed {MaxValueJsonLength} serialized characters.");

            if (checkAllowedValues && !string.IsNullOrWhiteSpace(definition.AllowedValuesJson))
            {
                using var allowedDocument = JsonDocument.Parse(definition.AllowedValuesJson);
                var allowed = allowedDocument.RootElement.EnumerateArray().Any(candidate => candidate.GetRawText() == json);
                if (!allowed)
                    return ValueValidation.Failure("PROFILE_VALUE_NOT_ALLOWED", "The value is not included in AllowedValues.");
            }

            return ValueValidation.Success(json);
        }
        catch (FormatException)
        {
            return ValueValidation.Failure("PROFILE_VALUE_TYPE_MISMATCH", $"The value is not a valid {definition.DataType}.");
        }
        catch (ArgumentException)
        {
            return ValueValidation.Failure("INVALID_PROFILE_PATTERN", "The configured validation pattern cannot be evaluated safely.");
        }
    }

    private void AddAudit(string action, UserProfileAttributeDefinition definition)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            UserId = _currentUser.UserId,
            Action = action,
            EntityName = nameof(UserProfileAttributeDefinition),
            EntityId = definition.Id.ToString(),
            MetadataJson = JsonSerializer.Serialize(new
            {
                definition.Key,
                dataType = definition.DataType.ToString(),
                definition.IsRequired,
                definition.IsActive
            }),
            CreatedAt = _clock.UtcNow
        });
    }

    private static ProfileAttributeDefinitionDto MapDefinition(UserProfileAttributeDefinition definition) => new()
    {
        Version = definition.Version,
        Id = definition.Id,
        Key = definition.Key,
        DisplayName = definition.DisplayName,
        Description = definition.Description,
        DataType = definition.DataType.ToString(),
        IsRequired = definition.IsRequired,
        IsActive = definition.IsActive,
        DefaultValue = definition.DefaultValueJson is null ? null : ParseJson(definition.DefaultValueJson),
        MinLength = definition.MinLength,
        MaxLength = definition.MaxLength,
        MinimumNumber = definition.MinimumNumber,
        MaximumNumber = definition.MaximumNumber,
        ValidationPattern = definition.ValidationPattern,
        AllowedValues = ParseJsonArray(definition.AllowedValuesJson),
        CreatedAt = definition.CreatedAt,
        UpdatedAt = definition.UpdatedAt
    };

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static IReadOnlyList<JsonElement> ParseJsonArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Select(value => value.Clone()).ToList();
    }

    private static bool TryParseDataType(string value, out ProfileAttributeDataType dataType) =>
        Enum.TryParse(value, true, out dataType) && Enum.IsDefined(dataType);

    private static bool HasExplicitOffset(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.EndsWith('Z') || Regex.IsMatch(value, "[+-][0-9]{2}:[0-9]{2}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking));

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static OperationResult<ProfileAttributeDefinitionDto> Failure(string? code, string? message) =>
        OperationResult<ProfileAttributeDefinitionDto>.Failure(code ?? "PROFILE_SCHEMA_INVALID", message ?? "The profile schema is invalid.");

    private sealed record DefinitionValidation(bool IsSuccess, string? ErrorCode, string? Message, string? DefaultValueJson, string? AllowedValuesJson)
    {
        public static DefinitionValidation Success(string? defaultJson, string? allowedJson) => new(true, null, null, defaultJson, allowedJson);
        public static DefinitionValidation Failure(string code, string message) => new(false, code, message, null, null);
    }

    internal sealed record ValueValidation(bool IsSuccess, string ErrorCode, string Message, string? ValueJson)
    {
        public static ValueValidation Success(string json) => new(true, string.Empty, string.Empty, json);
        public static ValueValidation Failure(string code, string message) => new(false, code, message, null);
    }
}
