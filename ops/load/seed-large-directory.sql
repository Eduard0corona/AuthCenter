-- Seeds a large synthetic directory for capacity and load tests. NEVER run it against production:
-- it inserts users that can hold access to a real application.
--
-- Users get the email "<prefix>-<n>@load.test", have no password (they cannot sign in) and have
-- direct access to the application; groups "<prefix> group <n>" grant the same application, and
-- each user joins several groups. Running it again with the same prefix adds nothing.
--
-- sqlcmd -S <server> -d <database> -G -i ops/load/seed-large-directory.sql
-- The test DirectoryScaleRelationalTests edits the values below before running it.
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Users int = 100000;
DECLARE @Groups int = 1000;
DECLARE @MembershipsPerUser int = 3;
DECLARE @ApplicationCode nvarchar(50) = N'AUTHCENTER';
DECLARE @Prefix nvarchar(20) = N'scale';

DECLARE @ApplicationId uniqueidentifier = (SELECT Id FROM ApplicationSystems WHERE Code = @ApplicationCode);
IF @ApplicationId IS NULL
    THROW 50000, 'The application does not exist.', 1;
IF EXISTS (SELECT 1 FROM AspNetUsers WHERE NormalizedEmail = UPPER(CONCAT(@Prefix, N'-1@load.test')))
BEGIN
    PRINT 'The directory with this prefix is already seeded.';
    RETURN;
END;

DECLARE @Now datetime2 = SYSUTCDATETIME();

BEGIN TRANSACTION;

;WITH Numbers AS (
    SELECT TOP (@Users) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS N
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT INTO AspNetUsers (Id, FullName, UserName, NormalizedUserName, Email, NormalizedEmail, EmailConfirmed, SecurityStamp, ConcurrencyStamp,
    PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnabled, AccessFailedCount, IsActive, IsExternalUser, HasLocalPassword, MustChangePassword, CreatedAt, Version)
SELECT NEWID(), CONCAT(N'Usuario ', @Prefix, N' ', N), CONCAT(@Prefix, N'-', N, N'@load.test'), UPPER(CONCAT(@Prefix, N'-', N, N'@load.test')),
    CONCAT(@Prefix, N'-', N, N'@load.test'), UPPER(CONCAT(@Prefix, N'-', N, N'@load.test')), 1, CONVERT(nvarchar(36), NEWID()), CONVERT(nvarchar(36), NEWID()),
    0, 0, 1, 0, 1, 1, 0, 0, DATEADD(second, -N, @Now), 0
FROM Numbers;

SELECT Id, ROW_NUMBER() OVER (ORDER BY CreatedAt DESC) AS N
INTO #SeededUsers
FROM AspNetUsers
WHERE NormalizedEmail LIKE UPPER(CONCAT(@Prefix, N'-%@LOAD.TEST'));

INSERT INTO UserApplicationAccesses (Id, UserId, ApplicationSystemId, IsActive, CreatedAt)
SELECT NEWID(), Id, @ApplicationId, 1, @Now FROM #SeededUsers;

;WITH Numbers AS (
    SELECT TOP (@Groups) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS N FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT INTO DirectoryGroups (Id, Name, NormalizedName, Description, IsActive, CreatedAt, Version)
SELECT NEWID(), CONCAT(@Prefix, N' group ', N), UPPER(CONCAT(@Prefix, N' group ', N)), N'Synthetic group for load tests', 1, @Now, 0
FROM Numbers;

SELECT Id, ROW_NUMBER() OVER (ORDER BY NormalizedName) - 1 AS N
INTO #SeededGroups
FROM DirectoryGroups
WHERE NormalizedName LIKE UPPER(CONCAT(@Prefix, N' GROUP %'));

INSERT INTO GroupApplicationAssignments (GroupId, ApplicationSystemId, CreatedAt)
SELECT Id, @ApplicationId, @Now FROM #SeededGroups;

;WITH Slots AS (
    SELECT TOP (@MembershipsPerUser) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS K FROM sys.all_objects
)
INSERT INTO UserGroupMemberships (GroupId, UserId, CreatedAt)
SELECT DISTINCT g.Id, u.Id, @Now
FROM #SeededUsers u
CROSS JOIN Slots s
JOIN #SeededGroups g ON g.N = (u.N + s.K * 7919) % (SELECT COUNT(*) FROM #SeededGroups);

COMMIT TRANSACTION;

SELECT
    (SELECT COUNT(*) FROM #SeededUsers) AS Users,
    (SELECT COUNT(*) FROM #SeededGroups) AS Groups,
    (SELECT COUNT(*) FROM UserGroupMemberships m JOIN #SeededGroups g ON g.Id = m.GroupId) AS Memberships;
