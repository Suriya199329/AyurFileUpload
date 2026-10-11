-- Run this against the FileLibrary database after deploying the Azure resources.
-- Replace the value with the App Service name from the Bicep deployment output.
DECLARE @ApiPrincipal sysname = N'REPLACE_WITH_API_APP_NAME';

IF OBJECT_ID(N'dbo.Files', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Files
    (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_Files PRIMARY KEY,
        Name nvarchar(200) NOT NULL,
        ContentType nvarchar(100) NOT NULL,
        Kind nvarchar(10) NOT NULL,
        Size bigint NOT NULL,
        UploadedAt datetimeoffset NOT NULL,
        Content varbinary(max) NOT NULL
    );

    CREATE INDEX IX_Files_UploadedAt ON dbo.Files (UploadedAt);
END;

IF DATABASE_PRINCIPAL_ID(@ApiPrincipal) IS NULL
BEGIN
    DECLARE @CreateUser nvarchar(max) =
        N'CREATE USER ' + QUOTENAME(@ApiPrincipal) + N' FROM EXTERNAL PROVIDER;';
    EXEC sys.sp_executesql @CreateUser;
END;

DECLARE @ReaderRole nvarchar(max) =
    N'ALTER ROLE [db_datareader] ADD MEMBER ' + QUOTENAME(@ApiPrincipal) + N';';
DECLARE @WriterRole nvarchar(max) =
    N'ALTER ROLE [db_datawriter] ADD MEMBER ' + QUOTENAME(@ApiPrincipal) + N';';
EXEC sys.sp_executesql @ReaderRole;
EXEC sys.sp_executesql @WriterRole;
