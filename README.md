# Shift Handover System

An ASP.NET Core MVC application for supervisors to claim scheduled operational shifts, record handover information, close shifts, and distribute a PDF report to other eligible supervisors.

## Table of Contents

- [Overview](#overview)
- [Features And Workflow](#features-and-workflow)
- [Technology And Architecture](#technology-and-architecture)
- [Authentication And Application Rules](#authentication-and-application-rules)
- [Database Schema](#database-schema)
- [Prerequisites And Setup](#prerequisites-and-setup)
- [Configuration](#configuration)
- [Run The Application](#run-the-application)
- [Tests](#tests)
- [Project Structure](#project-structure)
- [Troubleshooting](#troubleshooting)
- [Security Notes](#security-notes)
- [Unverified Details And Future Improvements](#unverified-details-and-future-improvements)

## Overview

The system provides a shared shift schedule and a per-shift handover record. Supervisors can view shift and record information across the team. Only the supervisor who claims a shift can add accident, incident, or manpower entries and close that shift. Closing stores the closed state first, then generates and emails the handover report. A closed shift's PDF is available in the application even if email delivery fails.

Shift times and displayed report times use Bahrain time (UTC+03:00). Shifts are generated for Bahrain calendar dates by a hosted background worker.

## Features And Workflow

1. **Sign in.** A supervisor signs in at `/Account/Login` using their email and password. Unauthenticated requests are redirected to the login page.
2. **View scheduled shifts.** The dashboard (`/`) and shifts list (`/Shifts`) show shift schedule/status. The generator creates Morning Operations (06:00–14:00), Evening Operations (14:00–22:00), and Overnight Operations (22:00–06:00). The overnight shift is stored on the following Bahrain date. Generation runs at startup and then near Bahrain midnight.
3. **Claim an available shift.** The shift details page offers a claim action while the shift is Available. The server assigns the signed-in supervisor; the request cannot choose an owner. A supervisor may claim more than one shift. A competing claim loses through optimistic concurrency handling.
4. **Record the handover.** While a shift is Claimed, its owner can add:
   - Accidents: local occurrence time, location, description, severity, and optional injured person/action taken.
   - Incidents: local occurrence time, category, description, resolved state, and follow-up-required state.
   - Manpower entries: department, role, required/present/absent counts, and optional notes.

   Other supervisors can view the logs but cannot submit changes. The application exposes creation only; it does not provide record edit or delete actions.
5. **Close the shift.** Only its assigned supervisor can close a Claimed shift. The close is persisted before report delivery starts. A closed shift is read-only for record entry.
6. **Generate and download the PDF.** The report contains the shift summary and recorded accident, incident, and manpower data. It is generated on request at `/Shifts/Report/{id}` for a closed shift and downloaded as `shift-{id}-handover.pdf`.
7. **Email the report.** On close, the application attempts to email the PDF to other users in the `Supervisor` role with valid, non-reserved email domains. The closer is excluded. Delivery status and failure reason are stored per shift and displayed on the dashboard/details views. The delivery record is unique per shift; failed or already-pending deliveries are not automatically retried. No manual retry action is implemented.

The conventional MVC route pattern is `{controller=Home}/{action=Index}/{id?}`. Important paths include `/Shifts/Details/{id}`, `/ShiftRecords/Accidents/{id}`, `/ShiftRecords/Incidents/{id}`, `/ShiftRecords/Manpower/{id}`, and `/Account/AccessDenied`. Record routes accept GET and POST requests at the same path; write actions require antiforgery tokens.

## Technology And Architecture

| Area | Implementation |
| --- | --- |
| Framework | ASP.NET Core MVC / .NET 10 (`net10.0`) |
| Persistence | Entity Framework Core 10.0.13 with SQL Server provider |
| Authentication | ASP.NET Core Identity EF Core; cookie authentication |
| Views | Razor MVC views; Bootstrap, jQuery, and jQuery Validation assets are included under `wwwroot/lib` |
| PDF | PdfSharp 6.2.2 |
| SMTP | MailKit 4.18.1 |
| Tests | xUnit 2.9.3, ASP.NET Core MVC testing 10.0.13, Microsoft.NET.Test.Sdk 17.14.1 |

`ShiftHandover.Web` contains the web application. Controllers handle HTTP actions and authorization, model classes contain shift state transitions and domain data, `ApplicationDbContext` configures Identity and application persistence, and services generate schedules, PDFs, and email deliveries. `ShiftHandover.Tests` references the web project and tests controllers through `WebApplicationFactory`, domain behavior, SQL Server concurrency, PDF output, and delivery behavior.

## Authentication And Application Rules

- Every endpoint requires an authenticated user by the fallback authorization policy unless it is explicitly marked `[AllowAnonymous]`. The dashboard, shifts, and shift-record controllers additionally require the `Supervisor` role.
- The login and access-denied pages are anonymous. Login uses email, returns a generic invalid-credentials message, and honors only local return URLs. Identity requires a 12-character password with upper/lowercase, digit, and non-alphanumeric characters; lockout begins after five failed attempts for 15 minutes. Unique email is required by Identity configuration.
- The authentication cookie is HttpOnly, Secure, SameSite Lax, expires after eight hours, and uses sliding expiration. HTTPS redirection is enabled.
- Claim and close actions derive the supervisor ID from the authenticated principal. Claim requires Available state; close requires Claimed state and matching owner. Invalid claim state returns a message and redirects; a concurrent claim/close conflict is handled as a user-facing message. Missing shifts return 404.
- `Shift.RowVersion` is a SQL Server rowversion concurrency token. This protects claim/close updates from stale writes. Record insertion checks that the shift is still open and owned by the current supervisor, but record insertion and shift closure are not one atomic transaction; the code does not provide a separate concurrency retry mechanism for a record-write/close race.
- MVC input models and database constraints validate required values, string lengths, enum values, and non-negative manpower counts. Text is trimmed and blank optional text is saved as null. Neither model nor database enforces that `Present + Absent` equals `Required`.
- Closed shifts remain visible and their existing records remain readable by supervisors. The UI and POST ownership checks prevent adding records or closing again.
- In Development, the app enables the EF migrations exception endpoint and runs a development supervisor seeder. Outside Development it uses `/Home/Error` and HSTS. PDF generation and email failures are caught and reported on the shift details page; the shift stays closed if report creation or email delivery fails.

## Database Schema

The EF model in `ApplicationDbContext` and the latest migration/snapshot define the schema. The migration sequence is `20261016114904_InitialCreate` followed by `20261016115005_RestrictShiftRecordDeletes`. The latter changes the shift-to-accident, shift-to-incident, and shift-to-manpower foreign keys from cascade delete to restrict. Application data is protected from accidental deletion through those relationships.

### Application Tables

| Table | Columns and constraints | Relationships and indexes |
| --- | --- | --- |
| `Shifts` | `Id` (int PK), `Title` (required, nvarchar(120)), `ShiftDate` (date), `StartTime`/`EndTime` (time), `Status` (nvarchar(20), enum stored as text), nullable `ClaimedBySupervisorId`, `ClaimedAt`, `ClosedAt`, and SQL Server `RowVersion`. `CK_Shifts_StatusClaimState` requires Available shifts to have no claimant/timestamps, Claimed shifts to have claimant and claim time but no close time, and Closed shifts to have claimant and both timestamps. | Optional claimant FK to `AspNetUsers`, delete restricted. Indexes on `(Status, ShiftDate)` and `ClaimedBySupervisorId`. One shift has many accidents, incidents, and manpower rows, and at most one delivery row. |
| `Accidents` | `Id`, `ShiftId`, `OccurredAt`, required `Location` (160), required `Description` (2000), `Severity` (text, max 20), nullable `InjuredPerson` (160), nullable `ActionTaken` (2000), and required `RecordedBySupervisorId`. | Required FKs to `Shifts` and `AspNetUsers`, both delete restricted. Indexes on `(ShiftId, OccurredAt)` and recorder ID. |
| `Incidents` | `Id`, `ShiftId`, `OccurredAt`, required `Category` (120), required `Description` (2000), `Resolved`, `FollowUpRequired`, and required `RecordedBySupervisorId`. | Required FKs to `Shifts` and `AspNetUsers`, both delete restricted. Indexes on `(ShiftId, OccurredAt)` and recorder ID. |
| `ManpowerRecords` | `Id`, `ShiftId`, required `Department` (120), required `Role` (120), `Required`, `Present`, `Absent`, nullable `Notes` (1000), and required `RecordedBySupervisorId`. `CK_ManpowerRecords_NonNegativeCounts` requires all three counts to be at least zero. | Required FKs to `Shifts` and `AspNetUsers`, both delete restricted. Indexes on `ShiftId` and recorder ID. |
| `ShiftReportDeliveries` | `Id`, `ShiftId`, `Status` (text, max 20), `CreatedAt`, nullable `SentAt`, and nullable `FailureReason` (1000). | Required one-to-one FK to `Shifts`, delete restricted. `ShiftId` has a unique index. |

Each application table has a generated integer `Id` primary key. `ShiftReportDeliveries` additionally has a unique `ShiftId`, enforcing one delivery record per shift. Application enum properties (`Shift.Status`, `Accident.Severity`, `ShiftReportDelivery.Status`) are stored as strings.

### ASP.NET Core Identity Tables

The migration creates the standard seven-table Identity schema:

| Table | Columns / keys |
| --- | --- |
| `AspNetUsers` | `Id` (nvarchar(450) PK), `FullName` (required, nvarchar(160)), and Identity user fields: `UserName`, `NormalizedUserName`, `Email`, `NormalizedEmail`, `EmailConfirmed`, `PasswordHash`, `SecurityStamp`, `ConcurrencyStamp`, `PhoneNumber`, `PhoneNumberConfirmed`, `TwoFactorEnabled`, `LockoutEnd`, `LockoutEnabled`, and `AccessFailedCount`. |
| `AspNetRoles` | `Id` (nvarchar(450) PK), nullable `Name` and `NormalizedName` (nvarchar(256)), and `ConcurrencyStamp`. |
| `AspNetUserRoles` | Composite PK `(UserId, RoleId)`; FKs to users and roles. |
| `AspNetUserClaims` | `Id` (int identity PK), `UserId`, nullable `ClaimType` and `ClaimValue`; FK to users. |
| `AspNetRoleClaims` | `Id` (int identity PK), `RoleId`, nullable `ClaimType` and `ClaimValue`; FK to roles. |
| `AspNetUserLogins` | Composite PK `(LoginProvider, ProviderKey)`, nullable `ProviderDisplayName`, `UserId`; FK to users. |
| `AspNetUserTokens` | Composite PK `(UserId, LoginProvider, Name)`, nullable `Value`; FK to users. |

Identity-owned dependent tables use cascade delete to their Identity user or role. Identity has a filtered unique index on non-null `NormalizedUserName`, a filtered unique index on non-null role `NormalizedName`, and a non-unique index on `NormalizedEmail`. The application config requires unique email during Identity operations, but the migration's email index itself is not unique. No application login requirement for confirmed email or two-factor authentication is configured.

## Prerequisites And Setup

- Windows with the .NET 10 SDK. Confirm with `dotnet --info`.
- SQL Server accessible through the configured SQL Server connection string. SQL Server LocalDB is used by the current test fixtures and is a convenient Windows development option.
- PowerShell for the examples below. Commands are shown from the repository root.

Restore dependencies and the repository-pinned EF command-line tool:

```powershell
dotnet restore ShiftHandover.slnx
dotnet tool restore
```

Configure a development connection string through User Secrets rather than committing credentials. For LocalDB, a suitable value has this form (adjust the database name or server for your installation):

```powershell
dotnet user-secrets set --project ShiftHandover.Web/ShiftHandover.Web.csproj "ConnectionStrings:DefaultConnection" "Server=(localdb)\MSSQLLocalDB;Database=ShiftHandover;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true"
```

Apply the checked-in EF migrations to the configured database:

```powershell
dotnet ef database update --project ShiftHandover.Web/ShiftHandover.Web.csproj --startup-project ShiftHandover.Web/ShiftHandover.Web.csproj
```

The database principal must be able to create/update the target database and schema, or the database must be provisioned and the principal granted appropriate migration rights. The app does not apply migrations automatically on startup. The Development shift generator expects the schema to be ready when the hosted worker runs.

For new Development supervisor accounts, configure the seeder's settings in User Secrets or environment variables. Its supported keys are:

| Key | Purpose |
| --- | --- |
| `SeedSupervisors:Supervisor1:Email` / `SeedSupervisors:Supervisor2:Email` | Email used to find/create each development account or update an existing account's email. |
| `SeedSupervisors:Supervisor1:Password` / `SeedSupervisors:Supervisor2:Password` | Required only when creating a new account. Must satisfy the configured Identity password policy. |
| `SeedSupervisors:Supervisor1:UserId` / `SeedSupervisors:Supervisor2:UserId` | Optional existing Identity ID; when supplied, the seeder updates that existing user's email instead of creating another account. |
| `SeedSupervisors:Supervisor1:FullName` / `SeedSupervisors:Supervisor2:FullName` | Optional display name; defaults to the corresponding key name. |

Example syntax (replace values locally and do not commit them):

```powershell
dotnet user-secrets set --project ShiftHandover.Web/ShiftHandover.Web.csproj "SeedSupervisors:Supervisor1:Email" "supervisor-one@your-domain.example"
dotnet user-secrets set --project ShiftHandover.Web/ShiftHandover.Web.csproj "SeedSupervisors:Supervisor1:Password" "use-a-private-strong-password"
```

The seeder runs only in Development, creates the `Supervisor` role, and handles two configured users. No production account-administration interface is implemented; provision production Identity users and their `Supervisor` role through a controlled operational process.

## Configuration

Configuration is read through ASP.NET Core configuration providers. `Program.cs` requires `ConnectionStrings:DefaultConnection`. SMTP options bind from `Email:Smtp`:

| Key | Meaning | Default / requirement |
| --- | --- | --- |
| `Email:Smtp:Host` | SMTP server host | Required |
| `Email:Smtp:Port` | SMTP port | Defaults to `587`; must be a valid port |
| `Email:Smtp:UserName` | SMTP authentication user | Required |
| `Email:Smtp:Password` | SMTP authentication password or provider token | Required |
| `Email:Smtp:FromAddress` | Sender email address | Required |
| `Email:Smtp:FromName` | Sender display name | Defaults to `Shift Handover` |
| `Email:Smtp:Security` | TLS mode | Defaults to `StartTls`; supported values are `StartTls` and `SslOnConnect` |

These settings can be supplied with User Secrets for local Development or deployment-managed environment variables such as `Email__Smtp__Host` and `Email__Smtp__Password`. The repository's email-delivery guide, [docs/email-delivery.md](docs/email-delivery.md), describes local SMTP and development recipient configuration. Email readiness depends on the deployment's SMTP account and eligible supervisor email addresses; configuration alone does not establish that delivery works.

The checked-in Development settings include the connection-string configuration key. This README intentionally does not reproduce any configured connection-string values or credentials. Inspect and replace local configuration as needed without committing secrets.

## Run The Application

After applying migrations and configuring a supervisor account:

```powershell
dotnet run --project ShiftHandover.Web/ShiftHandover.Web.csproj --launch-profile https
```

The checked-in HTTPS launch profile uses `https://localhost:7221` (and also binds `http://localhost:5286`). The HTTP profile uses port `5286`. HTTPS may present the standard development-certificate prompt; trust a local development certificate with `dotnet dev-certs https --trust` if required. Use the Development profile only for local development. The seeder does not run outside Development.

## Tests

Run the test project with:

```powershell
dotnet test ShiftHandover.Tests/ShiftHandover.Tests.csproj
```

The integration-test factories use unique SQL Server LocalDB database names, create schema with `EnsureCreated`, seed test roles/users, and call `EnsureDeleted` during fixture disposal. The SQL Server concurrency test also creates and deletes a unique LocalDB database. Run these tests only where LocalDB is installed and where creating/deleting those isolated test databases is acceptable. They do not exercise the checked-in migration sequence, because the test fixtures use `EnsureCreated`.

The existing tests cover these workflows and failure cases:

| Test file | Coverage |
| --- | --- |
| `AuthenticationTests.cs` | Login redirect, valid/invalid credentials, secure cookie, and logout. |
| `ShiftDomainTests.cs` | Shift state transitions and domain rules. |
| `ShiftManagementTests.cs` | Authentication/role access, competing and unsuccessful claims, ownership checks, unauthorized record writes/close, closed-shift restrictions, validation, and shift generation. |
| `ShiftReportDeliveryTests.cs` | PDF content, delivery to other eligible supervisors, filtering reserved test domains, PDF-generation failure, SMTP failure, missing SMTP settings, and no automatic retry. |
| `ShiftReportPdfLayoutTests.cs` | PDF wrapping, row spacing, pagination, repeated headers, page numbers, and Bahrain time labeling. |
| `SqlServerConcurrencyTests.cs` | SQL Server rowversion behavior for competing claims. |

Run a focused group with the test SDK's fully-qualified-name filter, for example:

```powershell
dotnet test ShiftHandover.Tests/ShiftHandover.Tests.csproj --filter "FullyQualifiedName~ShiftManagementTests"
dotnet test ShiftHandover.Tests/ShiftHandover.Tests.csproj --filter "FullyQualifiedName~ShiftReportDeliveryTests"
```

Tests were not run while preparing this README; no test result is claimed here.

## Project Structure

```text
ShiftHandoverPage/
|-- ShiftHandover.slnx
|-- dotnet-tools.json
|-- docs/
|   `-- email-delivery.md
|-- ShiftHandover.Web/
|   |-- Areas/Identity/Pages/       # Identity-area Razor view start
|   |-- Controllers/                # Account, dashboard, shifts, and shift records
|   |-- Data/                       # EF context, migrations, schedule generator, dev seeder
|   |   `-- Migrations/             # Initial schema, delete behavior, model snapshot
|   |-- Models/                     # Entities, enums, and MVC view/input models
|   |-- Properties/                 # Launch profiles
|   |-- Services/                   # Bahrain time, hosted schedule worker, PDF and SMTP delivery
|   |-- Views/                      # Account, dashboard, shifts, records, shared layouts/errors
|   |-- wwwroot/                    # CSS, JavaScript, favicon, and vendored libraries
|   |-- Program.cs                  # Service registration and HTTP pipeline
|   |-- ShiftHandover.Web.csproj
|   |-- appsettings.json
|   `-- appsettings.Development.json
|-- ShiftHandover.Tests/
|   |-- AuthenticationTests.cs
|   |-- ShiftDomainTests.cs
|   |-- ShiftManagementTests.cs
|   |-- ShiftReportDeliveryTests.cs
|   |-- ShiftReportPdfLayoutTests.cs
|   |-- SqlServerConcurrencyTests.cs
|   `-- ShiftHandover.Tests.csproj
`-- ERD.pdf
```

`bin` and `obj` directories are generated build artifacts and are not source. `app.db` may be present locally but is not configured as the application's provider; `Program.cs` uses SQL Server. Its contents and purpose are not verified in this documentation.

## Troubleshooting

| Symptom | Checks |
| --- | --- |
| Startup reports a missing `DefaultConnection` | Set `ConnectionStrings:DefaultConnection` for the active environment. Confirm the key spelling and that the intended profile/environment is selected. |
| SQL Server connection or schema errors | Verify the server is reachable, the connection's authentication method is valid, and migrations were applied using the same target database. Confirm the database principal has the required migration rights. |
| Login redirects to Access Denied | The account must be in the `Supervisor` role to access shift, dashboard, and record controllers. The Development seeder runs only in Development and only for its two configured users. |
| A claim says the shift is no longer available | Another supervisor may have claimed it first. Reload the schedule/details and choose an Available shift. |
| Record forms are unavailable or POSTs return not found | Confirm the shift is still Claimed and owned by the signed-in supervisor. Closed or other-owner shifts are read-only. |
| Report email is failed or pending | Check the stored delivery status, valid recipient addresses for other supervisors, SMTP host/port/security mode, authentication credentials, and network/TLS access. A stored failure is not automatically retried; the PDF can still be downloaded. |
| Tests cannot connect or fail to create a database | Install/start SQL Server LocalDB and ensure the test process can create/drop its isolated test databases. Remember the tests use `EnsureCreated`, not migrations. |

## Security Notes

- Never commit database credentials, SMTP passwords/app passwords, or supervisor passwords. Use .NET User Secrets for local Development and a protected secret provider or environment variables in deployed environments. Do not put secrets in command examples, source control, logs, or screenshots.
- Restrict database accounts to the rights needed by the application and deployment/migration process. Keep production credentials separate from development configuration.
- Production must use HTTPS and secure deployment-managed configuration. The authentication cookie is configured Secure and HttpOnly; do not weaken these settings to accommodate an insecure deployment.
- Supervisor onboarding outside Development must be handled operationally; the Development seeder is not a production user-management facility.
- Email is sent to other `Supervisor` role members only when their address parses and is not under `.test`, `.example`, `.invalid`, or `.localhost`. Ensure recipient records are maintained and validated.
- The application records SMTP/PDF exception text in delivery failure details and displays failure messages to supervisors. Review operational logging and error-detail exposure before production use.

## Unverified Details And Future Improvements

The following details cannot be established from repository source alone: the installed .NET SDK/SQL Server versions on another developer's machine, availability of LocalDB, whether configured SMTP credentials and real recipient mailboxes work, and whether a particular database has had the migrations applied. Verify these in the target environment. The development SMTP guide notes that successful delivery must be confirmed by a real send.

Possible future work, not implemented in the current application:

- Add a controlled production user/role administration and supervisor onboarding process.
- Add an audited, explicitly authorized retry workflow for failed or interrupted report delivery, with idempotent delivery semantics.
- Make record creation and shift closure atomic or otherwise close the write/close race.
- Add edit/history or correction workflows for handover records while preserving an audit trail.
- Add broader deployment automation, health checks, monitoring, and automated migration verification.
- Add stronger cross-field manpower validation if the operational rules require `Present + Absent` to match `Required`.