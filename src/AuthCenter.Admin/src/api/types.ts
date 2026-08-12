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
  createdAt: string;
  lastLoginAt: string | null;
  roles: string[];
  applications: string[];
}

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
