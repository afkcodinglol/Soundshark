# Sounds Hub service

The Windows UI is native WPF. Browsing, sign-up/sign-in, verification-code entry,
password recovery, preview, upload, download to Library, upload removal and reports
all run inside Soundshark. There is no browser handoff.

This folder contains the Supabase database migration and Edge Function. It is not
a deployed service until a project has been connected and configured. Desktop
tests use an offline HTTP fixture; backend tests use mocked Supabase requests.
Those tests do not prove hosted SQL policies, email delivery or public uploads.

## Connect a project

1. Apply `supabase/migrations/202610290001_sounds_hub.sql` to the selected project.
   The migration creates the catalogue, upload reservations, reports and private
   `hub-audio` bucket. Keep the bucket private. No direct client write policies
   are granted: all mutations pass through the Edge Function.
2. Run `supabase/setup-account-flow.sql` once to add private username and recovery
   state. Existing unique display names matching 3–24 letters, numbers or
   underscores become case-insensitive usernames; ambiguous legacy names retain
   email login. New signups use a unique username. Account email changes stay in
   sync through the auth trigger. Do not rerun the original catalogue migration.
   Deploy the complete `supabase/functions/sounds-hub` directory as `sounds-hub`,
   including `auth.mjs`,
   with gateway JWT verification disabled, as in `supabase/config.toml`. The
   function verifies the current user itself for every protected operation.
   Public catalogue/audio routes intentionally allow unauthenticated reads.
   `SUPABASE_URL`, `SUPABASE_ANON_KEY` and `SUPABASE_SERVICE_ROLE_KEY` are provided
   only by the trusted function environment. Never copy a secret/service-role
   key into the Windows app, source archive, logs or a support message.
3. Enable email/password accounts and email confirmation. The desktop accepts
   passwords of 6–12 characters for signup and password resets.
   Set the hosted minimum to 6; the maximum
   of 12 is enforced by the app, not by Supabase. Existing account sign-in is
   not restricted to the new-password range. Configure a production SMTP sender: Supabase's default sender
   only delivers to project team members and is insufficient for public signup.
   Set the confirmation and recovery email templates to show `{{ .Token }}`
   instead of a confirmation link. The app accepts both codes directly. Keep
   Supabase's authentication rate limits enabled.
4. Add `hub-source.json` at the Soundshark workspace root, containing only:

   ```json
   {"Url":"https://YOUR-PROJECT.supabase.co/","PublishableKey":"sb_publishable_YOUR-PUBLIC-KEY"}
   ```

   These are public client settings, not credentials granting administrative
   access. The project copies this file to build/publish output when it exists.
   The Windows client rejects secret keys and plaintext HTTP configurations.
5. With two real test accounts, exercise signup, verification and recovery;
   upload/preview/download; refresh and sign-out; owner removal and reporting.
   Verify that direct REST/storage writes by anonymous and authenticated clients
   fail. Test both accounts' cross-owner removal attempts. Confirm anonymous
   catalogue/audio requests work but hidden sounds are inaccessible. These
   hosted checks must pass before packaging/distribution.

## Login and recovery

The Sounds Hub navigation button opens login before constructing the catalogue.
Email sign-in works with Supabase Auth; username sign-in passes through the Edge
Function so emails are never revealed by a public lookup endpoint. Unknown
usernames or emails cannot trigger recovery messages. Account endpoints are
throttled by persistent database counters.

Remember me is optional. When checked, a Windows DPAPI encrypted session is kept
for a fixed 30 days from successful authentication. Refreshing does not extend
that deadline. Without it, the session lasts only for that Hub visit. Expired,
revoked, and old unbounded remembered sessions require login again. This is an
app remember-me deadline, not a Supabase project-wide token lifetime setting.

Forgot Password is independent of the login field. It closes login and opens a
recovery window with its own username/email field. A debounced server account
check enables a green Send Recovery Code button only for a matching account.
The check returns a boolean, never the account's email address; no mail is sent
until the user clicks the enabled button. The same window then changes to token
entry and, after successful verification, to the new-password fields. Redeploy
the updated Edge Function to enable `/auth/check`; no new SQL is needed if the
account setup script has already been run.

Recovery resends wait 60 seconds
on both the client and server. Three incorrect tokens exhaust the server-side
challenge; resending cannot restore attempts. Restarting recovery sends a new
email after the cooldown and invalidates the prior challenge. Challenges expire
after 15 minutes. A successful code reveals new-password and confirmation fields;
the submit button glows green only for matching passwords of 6–12 characters.
Recovery tokens are kept out of normal remembered sessions, and successful reset
returns to login. Supabase validates the actual email code and password change.
Provider `same_password` errors explain that the new password matches the
current password and keep the reset form open for another choice. Full historic
password reuse prevention is not implemented.

Sign-up validates email, username and password before contacting Auth. Valid
submission closes the details window and opens a separate token-only window.
Three rejected code entries close that window and require restarting sign-up;
transport/server failures do not consume an attempt. A correct code signs the
user in. Account errors are red. Login, sign-up and reset password inputs show a
Caps Lock warning while focused; input handlers detach when the window closes.

The SQL and Edge Function changes must be deployed together before username
login or this recovery flow can work on the hosted project. Local fixture tests
and PostgreSQL/WASM tests do not prove hosted SMTP or production auth behavior.

For dashboard deployment, run `scripts/Prepare-HubDeployment.ps1` from the
workspace root. Run the generated `outputs/hub-deployment/AccountSetup.sql` in a
new SQL query once. Then replace the Edge Function's entrypoint content with
`outputs/hub-deployment/sounds-hub.ts` and redeploy. This bundles all four modules
into one pasteable file; the original helper files need no editing. Keep legacy
gateway JWT verification off because protected routes authenticate in the code.

## Limits and moderation

- Audio is converted to MP3 by the desktop app before upload. The server parses
  every MPEG Layer III frame and independently measures duration and size.
- Limits: 60 encoded seconds (100 ms encoder tolerance), 10 MiB per sound,
  five upload attempts per account per hour, 50 active uploads per account,
  and a conservative 500 MiB shared storage cap including recent reservations.
- Only verified accounts may share, remove their own uploads or report. Authors
  are derived from the authenticated user, not a client-provided owner ID.
- Three distinct verified reports hide a sound pending owner review. Reports
  are private and limited to 20 per reporter per day. Use the database dashboard
  to review `hub_reports` and set `hub_sounds.status` to `published` or `hidden`.
  Reporting does not send email; the confirmation and completion text explain
  the threshold and absence of email notifications.
- Upload metadata failures trigger object cleanup. Review storage for orphans
  after an interrupted deletion or unavailable storage service. Removing a Hub
  listing never deletes files already downloaded into other people's Libraries.
- Downloads use the configured service origin, reject redirects, enforce byte
  limits and SHA-256, probe audio before publishing, and choose a fresh filename
  rather than replacing an existing Library sound. Sessions are encrypted with
  Windows DPAPI for the current user. Passwords are never saved.

The service API can be called outside the app by anyone implementing the same
protocol. Having Soundshark installed is not a cryptographic access restriction.

## Local checks

From the workspace root:

```text
dotnet run --no-restore --project tests/Soundshark.Checks -c Release -- outputs/qa/sounds-hub --hub
npm --prefix src/Soundshark.Hub ci
npm --prefix src/Soundshark.Hub test
```

The Windows checks need a normal Windows user profile for DPAPI. The Node checks
use `tools/ffmpeg.exe` to generate real MP3 input; on other systems, set `FFMPEG`
to an installed FFmpeg executable. No tests make real accounts or publish audio.

Provider references:
- https://supabase.com/docs/guides/auth/passwords
- https://supabase.com/docs/guides/auth/auth-email-templates
- https://supabase.com/docs/guides/auth/auth-smtp
- https://supabase.com/docs/guides/functions/auth
- https://supabase.com/docs/guides/storage/security/access-control

## Current Owner/reporting migrations

After `setup-account-flow.sql`, apply `setup-owner-reports.sql`, then `setup-submission-limits.sql`.
For an existing deployment with Owner tools already working, apply only `setup-submission-limits.sql`
and redeploy the regenerated function. These limits distinguish suggestions (5/24h) from problem/sound
reports (6/12h combined), serialize concurrent submissions by account, and preserve the Owner exemption.
