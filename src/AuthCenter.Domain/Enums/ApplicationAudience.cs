namespace AuthCenter.Domain.Enums;

/// <summary>
/// Who signs in to an application. It sets the hosted login's wording: consumers are offered an
/// account of their own, employees are asked for the one their organization gave them.
/// </summary>
public enum ApplicationAudience
{
    Employees = 0,
    Consumers = 1
}
