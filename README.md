# File Library — Angular + ASP.NET Core + SQL Server

A starter application for uploading and downloading Word documents, PDFs and images.

## Included

- Angular 21 standalone frontend with a responsive interface, drag and drop, upload progress, search, file type filters and pagination.
- ASP.NET Core 10 API using Entity Framework Core and Microsoft SQL Server.
- DOC, DOCX, PDF, JPG/JPEG, PNG, GIF and WebP; one file per upload, maximum 20 MiB.
- Server-side filename, size, extension and basic file signature validation.
- Local development stores file contents in SQL Server. Azure stores file metadata in Azure SQL and file contents in a private Blob Storage container.
- Downloads use attachment filenames, server-selected MIME types, byte-range support and `nosniff` headers.

**This is an unauthenticated shared library. Anyone who can reach the deployed site/API can list, upload and download all files. Do not upload private or sensitive health records until Microsoft Entra sign-in and authorization are added.** The project does not include document editing, conversion or malware scanning.

## Requirements

- Node.js 24 LTS (or another version supported by Angular 21) and npm.
- .NET 10 SDK.
- SQL Server or SQL Server Express, with permission to create a dedicated database.

## Run locally

Extract the project, open PowerShell in its folder, and edit `backend/appsettings.json` to use your SQL Server instance. The supplied local connection uses Windows authentication:

```text
Server=.\MSSQLSERVER01;Database=FileLibraryStarter;Integrated Security=true;Encrypt=true;TrustServerCertificate=true
```

Use `Server=localhost` for a default instance, or `Server=(localdb)\MSSQLLocalDB` for an installed LocalDB instance. Keep the database name unique to this app. `TrustServerCertificate=true` is a local development setting; use a trusted SQL certificate in deployment.

Start the API (in development, it creates a new database schema if needed):

```powershell
dotnet restore .\backend\FileLibrary.Api.csproj --configfile .\NuGet.Config
dotnet run --project .\backend -- --urls http://127.0.0.1:5080
```

Initialization uses EF Core `EnsureCreated` for a new, dedicated database. It does not upgrade an existing schema. Introduce EF migrations before making schema changes to an established installation.

In a second terminal:

```powershell
cd frontend
npm ci
npm start
```

Open **http://127.0.0.1:4200**. Angular forwards `/api` requests to port 5080 through `proxy.conf.json`, so the browser uses one origin.

Optional connection override, without putting a password in a committed file:

```powershell
$env:ConnectionStrings__FileLibrary = 'YOUR_CONNECTION_STRING'
```

## API

| Method | URL | Purpose |
| --- | --- | --- |
| GET | `/api/health` | Database connectivity; 503 if unavailable |
| GET | `/api/files?search=report&kind=pdf&page=1` | Metadata list; 20 files per page; kinds: word, pdf, image |
| POST | `/api/files` | Multipart form with exactly one field named `file` |
| GET | `/api/files/{id}/download` | Download file; 404 for an unknown ID |

Files have `id`, `name`, `contentType`, `kind`, `size` (bytes), and `uploadedAt` (UTC). Duplicate names are allowed; GUIDs identify each upload. The API stores new Azure uploads in Blob Storage before saving their metadata in SQL. Older records whose contents are already in the Azure SQL database remain downloadable. Local development continues to store contents in SQL when `BlobServiceUri` is not configured.

## Checks and builds

```powershell
dotnet run --project .\checks
dotnet build .\backend
cd frontend
npx ngc -p tsconfig.json --noEmit
npm run build
```

Validation checks cover accepted headers, wrong extensions, corrupted DOCX archives and macro entries. Signature validation is intentionally basic: it is not complete document parsing or malware detection. Legacy DOC files can contain macros; OLE signatures are shared by other Office formats.

To check the complete workflow after SQL Server is configured: upload a valid file of each supported type, search for it, filter the list, download it and compare SHA-256 hashes using `Get-FileHash`. Confirm an empty file, a file larger than 20 MiB and an executable renamed to `.pdf` are rejected. Restart the API and confirm uploaded files remain available.

## Deploy to Azure

The included deployment provisions Angular on Azure Static Web Apps, the .NET 10 API on Linux App Service, Azure SQL Database, and a private Azure Blob container. App Service uses a system-assigned managed identity for Blob Storage and SQL; no storage keys or SQL passwords are put in the API configuration. The SQL database is restricted to the API's outbound IP addresses.

**Cost and exposure:** the template creates a Basic App Service plan and Basic Azure SQL database, which incur charges. The frontend and API are public and currently have no sign-in; all file endpoints are anonymous. Treat this deployment as a public demo only until authentication/authorization is implemented.

1. Create an Azure Entra application/service principal for GitHub Actions and configure a federated credential for this repository's `main` branch (and/or GitHub Environment if using manual deployment). Grant it `Contributor` and `Role Based Access Control Administrator` on the target subscription or resource group.
2. Add these GitHub repository **Actions variables**:
   - `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`
   - `AZURE_RESOURCE_GROUP`, `AZURE_LOCATION` (for example `eastus2`), and a unique lowercase `AZURE_NAME_PREFIX` of 3–10 characters
   - `SQL_ADMIN_OBJECT_ID` and `SQL_ADMIN_LOGIN` for the Entra user/group that will administer the database
3. Push to `main` or run **Deploy to Azure** from the Actions tab. The workflow builds/tests the app, deploys the infrastructure, and publishes both applications. The database bootstrap is a one-time manual step after the workflow.
4. In Azure Portal, open the deployed SQL database as the configured Entra SQL administrator. Temporarily allow your current client IP in the SQL server networking settings if needed, then run [`infra/sql/bootstrap.sql`](./infra/sql/bootstrap.sql) against `FileLibrary`, replacing `REPLACE_WITH_API_APP_NAME` with the `apiName` output of the `file-library` resource-group deployment. This creates the table and grants the App Service identity only data-reader/data-writer roles. Remove the temporary firewall rule afterwards.
5. Confirm the API health endpoint (`https://<api-name>.azurewebsites.net/api/health`) returns `ready`, then open the Static Web App URL from the deployment output.

The deployment identity needs permission to create resource role assignments. SQL access also depends on the Entra administrator completing the bootstrap step. The template provisions an empty database; it does not copy records from an existing SQL Server. Back up/export and import an existing database to Azure SQL before switching users over. Once its rows are in Azure SQL, existing SQL file contents remain downloadable, and new uploads use Blob Storage, so moving old content into blobs can be done later without downtime. Local development still uses SQL for file contents unless `BlobServiceUri` is set.

Before exposing real wellness records, add user authentication, per-user authorization, upload quotas/rate limits, and malware scanning. The server-side file signature checks are basic and do not replace malware scanning.

## Verification in this session

See `VERIFICATION.md` for executed checks and environment limitations.

## References

- [Angular version compatibility](https://angular.dev/reference/versions)
- [ASP.NET Core file uploads](https://learn.microsoft.com/en-us/aspnet/core/mvc/models/file-uploads?view=aspnetcore-10.0)
