# Cloud sync setup

Cloud sync is optional. Without `Sync:Url` and `Sync:AnonKey` the app hides the Account menu and works exactly as before.
Design: `docs/superpowers/specs/2026-09-29-cloud-sync-design.md`.

## One-time setup

1. **Create the project.** At supabase.com, create a free project and pick the region closest to your players.
2. **Create the schema.** In the SQL editor, run `supabase/migrations/0001_cloud_sync.sql`. If `create extension pg_cron` fails, enable
   **pg_cron** under Database → Extensions and run the file again.
3. **Configure authentication** (Authentication → Sign In / Providers → Email):
   - Email provider: **on**.
   - **Confirm email: off**. Sign-up is then instant and sends no email.
   - Minimum password length: **8**. If your plan offers leaked-password protection, turn it on.
4. **Set up Resend on your Cloudflare domain:**
   - Create a Resend account and choose Domains → Add domain (for example `mail.yourdomain.com`).
   - In Cloudflare DNS, add every record Resend lists, with the proxy **off** ("DNS only"). Then click Verify in Resend.
   - Create a Resend API key with sending access.
5. **Point Supabase at Resend** (Authentication → Emails → SMTP settings). Enable custom SMTP with:
   - Host `smtp.resend.com`
   - Port `465`
   - Username `resend`
   - Password: the Resend API key
   - Sender `noreply@mail.yourdomain.com`, name `Hearthsheet`
6. **Change the reset email** (Authentication → Emails → Templates → Reset password):
   - Subject: `Your Hearthsheet reset code`
   - Body:
     ```html
     <p>Your Hearthsheet password reset code is:</p>
     <h2>{{ .Token }}</h2>
     <p>Enter it in Hearthsheet. If you didn't ask for this, you can ignore this email.</p>
     ```
7. **Configure the app.** From Project Settings → API, copy the **Project URL** and the **publishable** key (`sb_publishable_…`, or the
   legacy `anon` key) into `src/Hearthsheet.App/appsettings.json` under `Sync:Url` and `Sync:AnonKey`, then commit. Both are public by design.
   **Never** put the secret or `service_role` key in the app or the repository.
8. **Keep the project awake.** In GitHub, go to Settings → Secrets and variables → Actions → **Variables** and add `SUPABASE_URL` and
   `SUPABASE_ANON_KEY`. Then run **Supabase keep-alive** once from the Actions tab. GitHub disables scheduled workflows after
   60 days without repository activity; if that happens, re-enable it from the Actions tab.

## Manual test script

Use two data folders on one PC as two "installations":

```powershell
dotnet run --project src/Hearthsheet.App -- --Application:DataDirectory=$env:TEMP\hs-a
```

```powershell
dotnet run --project src/Hearthsheet.App -- --Application:DataDirectory=$env:TEMP\hs-b
```

| # | Steps | Expected |
|---|---|---|
| 1 | A: create an account, create "Arannis". B: sign in. | B lists Arannis after sign-in. |
| 2 | A: rename, then click the sync indicator. B: click the sync indicator. | B shows the new name. |
| 3 | Disconnect the network. Edit Arannis differently in A and B. Reconnect, then sync A, then sync B. | B keeps A's version and gains "Arannis… (conflict copy, date)". The next sync uploads the copy. |
| 4 | A: delete Arannis, sync. B: sync. | Arannis disappears from B. |
| 5 | Open a character in B. Edit it in A and sync. Sync B. | B reloads it with "Updated from another device". |
| 6 | Close A and reopen it. | Still signed in, with no prompt. |
| 7 | Sign in with "Stay signed in" unchecked, then restart. | Signed out. |
| 8 | Sign out, then check Control Panel → Credential Manager. | No `Hearthsheet/supabase-session` entry. |
| 9 | Forgot password → code → new password. | The email arrives from your domain and the new password works. |
| 10 | Sign in to A as a second account. | The account-switch prompt appears, and both choices keep every local character. |

**Row-level security check** (SQL editor; replace the ids with two real user ids from Authentication → Users):

```sql
begin;
set local role authenticated;
set local request.jwt.claims = '{"sub":"<user B id>"}';
select count(*) from public.characters where user_id = '<user A id>';  -- must be 0
rollback;
```
