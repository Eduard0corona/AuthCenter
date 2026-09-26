export interface ApiEnvelope<T> {
  success: boolean;
  data?: T;
  errorCode?: string;
  message?: string;
  details?: string[];
}

export interface PagedResult<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
  totalPages: number;
}

export interface SessionUser {
  id: string;
  name: string | null;
  email: string | null;
  applications: string[];
  roles: string[];
  permissions: string[];
}

export interface SessionResponse {
  user: SessionUser;
  csrfToken: string;
}

export interface UserSummary {
  id: string;
  fullName: string;
  email: string;
  pictureUrl: string | null;
  isActive: boolean;
  isExternalUser: boolean;
  hasLocalPassword: boolean;
  mustChangePassword: boolean;
  mfaEnabled: boolean;
  createdAt: string;
  lastLoginAt: string | null;
  roles: string[];
  applications: string[];
  applicationAccesses: UserApplicationAccess[];
  applicationAssignments: UserApplicationAssignment[];
  roleAssignments: UserRoleAssignment[];
  groupMemberships: UserGroupMembership[];
}

export interface UserApplicationAccess {
  applicationId: string;
  applicationCode: string;
  applicationName: string;
  isActive: boolean;
  createdAt: string;
  revokedAt: string | null;
}

export interface InheritedAccessSource { groupId: string; groupName: string; isActive: boolean; }
export interface UserApplicationAssignment { applicationId: string; applicationCode: string; applicationName: string; isApplicationActive: boolean; isDirect: boolean; directAccessStatus: "Active" | "Pending" | "Revoked" | null; isEffective: boolean; inheritedFromGroups: InheritedAccessSource[]; }
export interface UserRoleAssignment { roleId: string; roleName: string; applicationId: string | null; applicationCode: string | null; isRoleActive: boolean; isSystemRole: boolean; isDirect: boolean; isEffective: boolean; inheritedFromGroups: InheritedAccessSource[]; }
export interface UserGroupMembership { groupId: string; groupName: string; isGroupActive: boolean; addedAt: string; }
export interface UserProfile { userId: string; isValid: boolean; missingRequiredAttributes: string[]; attributes: UserProfileAttributeValue[]; }
export interface UserProfileAttributeValue { key: string; value: string | number | boolean; isDefault: boolean; }
export interface ProfileAttributeDefinition { id: string; key: string; displayName: string; description: string | null; dataType: "String" | "Integer" | "Decimal" | "Boolean" | "Date" | "DateTime"; isRequired: boolean; isActive: boolean; defaultValue: string | number | boolean | null; minLength: number | null; maxLength: number | null; minimumNumber: number | null; maximumNumber: number | null; validationPattern: string | null; allowedValues: Array<string | number | boolean>; createdAt: string; updatedAt: string | null; }

export interface ApplicationBranding {
  applicationCode: string;
  displayName: string;
  primaryColor: string;
  backgroundColor: string;
  logoUrl: string | null;
  supportUrl: string | null;
  privacyUrl: string | null;
  termsUrl: string | null;
}

export type ApplicationRegistrationMode = "Closed" | "Open" | "InviteOnly" | "ApprovalRequired";

export interface ApplicationRegistrationSettings {
  registrationMode: ApplicationRegistrationMode;
  allowGoogleLogin: boolean;
  allowMicrosoftLogin: boolean;
  allowGitHubLogin: boolean;
  allowAppleLogin: boolean;
  allowMagicLink: boolean;
  allowPasswordLogin: boolean;
  requireEmailConfirmation: boolean;
  requireMfa: boolean;
  allowedEmailDomains: string | null;
  defaultRoleId: string | null;
}

export interface ApplicationSummary {
  id: string;
  code: string;
  name: string;
  description: string | null;
  isActive: boolean;
  createdAt: string;
  updatedAt: string | null;
  registrationSettings?: ApplicationRegistrationSettings | null;
  branding: ApplicationBranding | null;
}

export interface RoleSummary {
  id: string;
  name: string;
  description: string | null;
  applicationSystemId: string | null;
  isSystemRole: boolean;
  isActive: boolean;
  createdAt: string;
  permissions: string[];
}

export interface PermissionSummary {
  id: string;
  applicationSystemId: string;
  code: string;
  name: string;
  description: string | null;
  isActive: boolean;
  createdAt: string;
}

export interface DirectoryGroupApplication {
  id: string;
  code: string;
  name: string;
}

export interface DirectoryGroupRole {
  id: string;
  name: string;
  applicationSystemId: string;
  applicationCode: string;
}

export interface DirectoryGroupSummary {
  id: string;
  name: string;
  description: string | null;
  isActive: boolean;
  createdAt: string;
  updatedAt: string | null;
  memberCount: number;
  applications: DirectoryGroupApplication[];
  roles: DirectoryGroupRole[];
}

export interface DirectoryGroupMember {
  userId: string;
  fullName: string;
  email: string;
  isActive: boolean;
  addedAt: string;
}

export interface AuditLogEntry {
  id: string;
  userId: string | null;
  applicationCode: string | null;
  action: string;
  entityName: string | null;
  entityId: string | null;
  traceId: string | null;
  createdAt: string;
}

export interface EventDelivery {
  id: string;
  eventId: string;
  eventType: string;
  hookId: string;
  hookName: string;
  attemptCount: number;
  nextAttemptAt: string | null;
  deliveredAt: string | null;
  deadLetteredAt: string | null;
  lastError: string | null;
}

export interface OAuthClientSummary {
  id: string;
  applicationSystemId: string;
  applicationCode: string;
  applicationName: string;
  clientId: string;
  displayName: string;
  clientType: 0 | 1;
  redirectUris: string[];
  allowedScopes: string[];
  grantTypes: string[];
  loginUrl: string;
  postLogoutRedirectUris?: string[];
  backchannelLogoutUri?: string | null;
  backchannelLogoutSessionRequired?: boolean;
  accessTokenLifetimeSeconds: number;
  requirePkce: boolean;
  autoConsent: boolean;
  isActive: boolean;
  createdAt: string;
  updatedAt: string | null;
}

export interface OAuthClientCreated {
  client: OAuthClientSummary;
  clientSecret: string | null;
}

export interface OAuthClientSecret {
  clientSecret: string;
}

export type ProvisioningTokenStatus = "active" | "expired" | "revoked";

export interface ProvisioningTokenMetadata {
  id: string;
  applicationSystemId: string;
  applicationName: string;
  name: string;
  scopes: string[];
  status: ProvisioningTokenStatus;
  createdAt: string;
  expiresAt: string;
  lastUsedAt: string | null;
  revokedAt: string | null;
}

export interface ProvisioningTokenCreated {
  id: string;
  token: string;
  scopes: string[];
  expiresAt: string;
}

export interface AccessPolicyVersion {
  id: string;
  applicationSystemId: string;
  versionNumber: number;
  status: "Draft" | "Published" | "Archived";
  ruleCount: number;
  createdAt: string;
  publishedAt: string | null;
}

export interface AccessPolicyRule {
  id: string;
  applicationSystemId: string;
  policyVersionId: string;
  policyVersionNumber: number;
  policyVersionStatus: "Draft" | "Published" | "Archived";
  applicationCode: string;
  userId: string | null;
  userEmail: string | null;
  directoryGroupId: string | null;
  directoryGroupName: string | null;
  name: string;
  priority: number;
  action: "Allow" | "Deny";
  mfaRequirement: "Optional" | "Required";
  allowTrustedDeviceBypass: boolean;
  includedIpCidrs: string[];
  excludedIpCidrs: string[];
  activeFromUtc: string | null;
  activeUntilUtc: string | null;
  activeDaysUtc: string[];
  dailyStartTimeUtc: string | null;
  dailyEndTimeUtc: string | null;
  minimumRiskLevel: string | null;
  maximumRiskLevel: string | null;
  requiredAssuranceLevel: "Password" | "Mfa" | "PhishingResistant";
  isActive: boolean;
  createdAt: string;
  updatedAt: string | null;
}

export interface AccessPolicySimulation {
  isAllowed: boolean;
  requireMfa: boolean;
  allowTrustedDeviceBypass: boolean;
  requiredAssuranceLevel: string;
  matchedRuleId: string | null;
  matchedRuleName: string | null;
  decisionReason: string;
  policyVersionId: string | null;
  policyVersionNumber: number | null;
  policyVersionStatus: string | null;
  ruleEvaluations: Array<{ ruleId: string; ruleName: string; priority: number; matched: boolean; reasons: string[] }>;
}

export interface ProfileMapping {
  id: string;
  applicationSystemId: string;
  applicationName: string;
  sourceSystem: string;
  sourcePath: string;
  targetAttributeDefinitionId: string;
  targetAttributeName: string;
  isAuthoritative: boolean;
  isActive: boolean;
  createdAt: string;
  version: number;
}

export interface ProfileMappingSimulation {
  isValid: boolean;
  sourcePath: string;
  targetAttributeName: string;
  value: unknown;
  errors: string[];
}

export type GroupRuleExpectedValue = string | number | boolean;

export interface DynamicGroupRule {
  id: string;
  directoryGroupId: string;
  groupName: string;
  profileAttributeDefinitionId: string;
  attributeName: string;
  operator: string;
  expectedValue: GroupRuleExpectedValue;
  isActive: boolean;
  createdAt: string;
  version: number;
}

export interface GroupRulePreviewUser {
  id: string;
  email: string;
  fullName: string;
}

export interface GroupRulePreview {
  ruleId: string;
  users: PagedResult<GroupRulePreviewUser>;
}

export type FederationProtocol = "Oidc" | "Saml2";
export type AccountLinkingMode = "Disabled" | "VerifiedEmail";

export interface FederationProvider {
  id: string;
  applicationSystemId: string;
  name: string;
  protocol: FederationProtocol;
  issuer: string;
  discoveryEndpoint: string | null;
  clientId: string | null;
  oidcCallbackUrl: string | null;
  hasClientSecret: boolean;
  samlSingleSignOnUrl: string | null;
  samlSigningCertificateThumbprint: string | null;
  jitProvisioningEnabled: boolean;
  accountLinkingMode: AccountLinkingMode;
  isActive: boolean;
  version: number;
}

export interface FederationRoutingRule {
  id: string;
  federationProviderId: string;
  providerName: string;
  applicationSystemId: string;
  priority: number;
  emailDomain: string | null;
  directoryGroupId: string | null;
  profileAttributeDefinitionId: string | null;
  expectedProfileValueJson: string | null;
  isActive: boolean;
  version: number;
}

export interface FederationRouteResult {
  providerId: string;
  providerName: string;
  protocol: FederationProtocol;
}
