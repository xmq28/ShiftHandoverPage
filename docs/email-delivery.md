# Closed-shift report email

Closing a shift creates one PDF report and sends it to every other user in the `Supervisor` role with an email address. The supervisor who closed the shift is excluded. Delivery status and failures are stored per shift; retries do not send a second message. The report remains available from the shift details page even when email delivery fails.

## Gmail SMTP configuration with User Secrets

The application uses Gmail SMTP over STARTTLS. These non-password values have been configured in this web project's .NET User Secrets store:

```powershell
Email:Smtp:Host = smtp.gmail.com
Email:Smtp:Port = 587
Email:Smtp:Security = StartTls
Email:Smtp:UserName = alsaeedmalak8@gmail.com
Email:Smtp:FromAddress = alsaeedmalak8@gmail.com
```

For Gmail, create an App Password in the Google Account after enabling 2-Step Verification. Do not use the regular Google account password. From PowerShell at the repository root, run the following; the secure prompt does not echo the App Password, and the command history contains only the script, not the password:

```powershell
$securePassword = Read-Host "Gmail App Password" -AsSecureString
$pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)
try {
	$appPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
	dotnet user-secrets set --project ShiftHandover.Web/ShiftHandover.Web.csproj "Email:Smtp:Password" $appPassword | Out-Null
	if ($LASTEXITCODE -ne 0) { throw "Could not save the Gmail App Password." }
}
finally {
	[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
	Remove-Variable appPassword, securePassword, pointer -ErrorAction SilentlyContinue
}
```

Enter the App Password without display spaces. The script suppresses the CLI success output and clears its temporary plaintext variable; the value is not placed in PowerShell command history. User Secrets are for local development and are stored outside the repository.

## Supervisor recipient addresses

The current development database contains `supervisor1@example.test` and `supervisor2@example.test`. The delivery service now rejects malformed addresses and reserved `.test`, `.example`, `.invalid`, and `.localhost` domains, so it will not attempt to send to these seed addresses. The provided Gmail address is the SMTP account and sender; it is not assumed to be a report recipient.

For the development seeder, configure a real address for each existing supervisor and target the current Identity row by its ID. This updates only that row's email through `UserManager`; it preserves its Identity ID, password, supervisor role, and linked shift/record history. Obtain the IDs from the existing `AspNetUsers` table before configuring them. Do not delete or recreate the database.

```powershell
dotnet user-secrets set --project ShiftHandover.Web/ShiftHandover.Web.csproj "SeedSupervisors:Supervisor1:UserId" "existing-identity-id-1"
dotnet user-secrets set --project ShiftHandover.Web/ShiftHandover.Web.csproj "SeedSupervisors:Supervisor1:Email" "supervisor-one@real-domain.example"
dotnet user-secrets set --project ShiftHandover.Web/ShiftHandover.Web.csproj "SeedSupervisors:Supervisor2:UserId" "existing-identity-id-2"
dotnet user-secrets set --project ShiftHandover.Web/ShiftHandover.Web.csproj "SeedSupervisors:Supervisor2:Email" "supervisor-two@real-domain.example"
```

Replace the placeholder IDs and email addresses with the existing supervisors' Identity IDs and their valid, real addresses. These settings are applied only in `Development` and update the existing Identity rows without changing IDs, passwords, roles, or record relationships. For non-development hosting, update supervisor email addresses through the deployment's account-administration process.

Until at least one other supervisor has a real email address and the App Password is configured, real delivery is not ready. Closing a shift with no eligible recipient records a failure without attempting SMTP, and the PDF remains downloadable from the closed shift's details page. A successful delivery is not verified until a real SMTP send completes successfully.
