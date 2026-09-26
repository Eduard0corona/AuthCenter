export interface ApiEnvelope<T> {
  success: boolean;
  data?: T;
  errorCode?: string;
  message?: string;
  details?: string[];
  /** The request's trace, to quote to support. */
  traceId?: string | null;
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
  /** Sent back on update; a newer version on the server answers 409. */
  version?: number;
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
export interface ProfileAttributeDefinition { id: string; version?: number; key: string; displayName: string; description: string | null; dataType: "String" | "Integer" | "Decimal" | "Boolean" | "Date" | "DateTime"; isRequired: boolean; isActive: boolean; defaultValue: string | number | boolean | null; minLength: number | null; maxLength: number | null; minimumNumber: number | null; maximumNumber: number | null; validationPattern: string | null; allowedValues: Array<string | number | boolean>; createdAt: string; updatedAt: string | null; }

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
  /** Sent back on update; a newer version on the server answers 409. */
  version?: number;
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
  /** Sent back on update; a newer version on the server answers 409. */
  version?: number;
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
  /** Sent back on update; a newer version on the server answers 409. */
  version?: number;
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
  /** Sent back on update; a newer version on the server answers 409. */
  version?: number;
  id: string;
  name: string;
  description: string | null;
  isActive: boolean;
  createdAt: string;
  updatedAt: string | null;
  memberCount: number;
  /** Active group rules decide the members; the API refuses manual changes (GROUP_MANAGED_BY_RULES). */
  isRuleManaged?: boolean;
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
  /** The actor's email and name while the account exists. */
  userEmail?: string | null;
  userName?: string | null;
  applicationCode: string | null;
  action: string;
  entityName: string | null;
  entityId: string | null;
  ipAddress?: string | null;
  userAgent?: string | null;
  metadataJson?: string | null;
  traceId: string | null;
  createdAt: string;
}

export type EventDeliveryStatus = "pending" | "delivered" | "dead-letter";

export interface EventDelivery {
  id: string;
  eventId: string;
  eventType: string;
  hookId: string;
  hookName: string;
  status?: EventDeliveryStatus;
  attemptCount: number;
  createdAt?: string;
  nextAttemptAt: string | null;
  deliveredAt: string | null;
  deadLetteredAt: string | null;
  lastError: string | null;
  /** The signed JSON body; only in the detail of one delivery. */
  payload?: string | null;
}

export interface EventHook {
  id: string;
  applicationSystemId: string | null;
  applicationName: string | null;
  name: string;
  url: string;
  eventTypes: string[];
  isVerified: boolean;
  isActive: boolean;
  createdAt: string;
  verifiedAt: string | null;
  version: number;
  /** Until when the secret replaced by the last rotation still signs deliveries. */
  previousSecretExpiresAt: string | null;
}

export interface EventTypeInfo {
  type: string;
  category: string;
}

export interface EventHookSecret {
  id: string;
  secret: string;
  isVerified: boolean;
  previousSecretExpiresAt: string | null;
}

export interface OAuthClientSummary {
  /** Sent back on update; a newer version on the server answers 409. */
  version?: number;
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
  allowedCorsOrigins?: string[];
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
  /** Sent back on update; a newer version on the server answers 409. */
  version?: number;
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

export type GroupRuleScalar = string | number | boolean;
/** A value of the attribute's type; a list of them for the in operator, true for exists. */
export type GroupRuleExpectedValue = GroupRuleScalar | GroupRuleScalar[];

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
  /** OIDC only: the upstream must assert email_verified (otherwise only its routed domains are trusted). */
  requireVerifiedEmail?: boolean;
  /** An MFA reported by the upstream satisfies AuthCenter's MFA. */
  trustUpstreamMfa?: boolean;
  groupsClaim?: string | null;
  groupMappings?: FederationGroupMapping[];
  isActive: boolean;
  version: number;
}

export interface FederationGroupMapping {
  upstreamValue: string;
  directoryGroupId: string;
  directoryGroupName: string | null;
}

export interface FederationServiceProvider {
  oidcCallbackUrl: string | null;
  samlEntityId: string | null;
  samlAssertionConsumerServiceUrl: string | null;
}

export type FederationCheckStatus = "Pass" | "Warning" | "Fail";

export interface FederationConnectionTest {
  providerId: string;
  protocol: FederationProtocol;
  succeeded: boolean;
  checks: { name: string; status: FederationCheckStatus; detail: string }[];
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
  matchedRuleId?: string | null;
  matchedRulePriority?: number | null;
}

export interface AdminMetadata {
  errorCodes: Record<string, string>;
  stepUpPurposes: Record<string, string>;
  operationPermissions: Record<string, string>;
  maximumPageSize: number;
  environmentName: string;
}

export interface VersionManifest {
  version: string;
  commit: string | null;
  adminFrontendBasePath: string;
  contractVersion: number;
}

export interface AdminDashboard {
  generatedAt: string;
  activeUsers: number;
  inactiveUsers: number;
  activeApplications: number;
  activeGroups: number;
  pendingAccessRequests: number;
  activeFederationProviders: number;
  expiringProvisioningTokens: number;
  unverifiedEventHooks: number;
  deadLetterDeliveries: number;
  failedLoginsLast24Hours: number;
  highRiskObservationsLast24Hours: number;
}

export interface ApiScope {
  id: string;
  name: string;
  displayName: string;
  description: string | null;
}

export interface ApiResource {
  /** Sent back on update; a newer version on the server answers 409. */
  version?: number;
  id: string;
  applicationSystemId: string;
  applicationCode: string;
  applicationName: string;
  /** RFC 8707 resource indicator; also the audience of the access tokens. */
  identifier: string;
  displayName: string;
  description: string | null;
  isActive: boolean;
  scopes: ApiScope[];
  createdAt: string;
  updatedAt: string | null;
}
