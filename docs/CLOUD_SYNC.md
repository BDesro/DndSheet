# Cloud sync setup

Cloud sync is optional. Without `Sync:Url` and `Sync:AnonKey` the app hides the Account menu and works exactly as before.
How a sync pass works: see the last section.

The shipped `appsettings.json` already points at the project's Supabase instance. Follow the setup below only to create your own
project, for example for a fork, and then replace those two values.

## One-time setup

1. **Create the project.** At supabase.com, create a free project and pick the region closest to your players.
2. **Create the schema.** In the SQL editor, run `supabase/migrations/0001_cloud_sync.sql`. If `create extension pg_cron` fails, enable
   **pg_cron** under Database → Extensions and run the file again.
3. **Configure authentication.** Go to Authentication → Sign In / Providers, then click the **Email** row to expand it:
   - Email provider: **on**.
   - **Confirm email: off**, then Save. Sign-up is then instant and sends no email. The app expects a session straight from sign-up,
     so it can't complete a confirmation-link flow.
   - Minimum password length: **8**. If your plan offers leaked-password protection, turn it on.
4. **Set up Resend on your Cloudflare domain:**
   - Create a Resend account and choose Domains → Add domain (for example `mail.yourdomain.com`).
   - In Cloudflare DNS, add every record Resend lists, with the proxy **off** ("DNS only"). Then click Verify in Resend.
   - Create a Resend API key (API Keys → Create API Key) with **Sending access**, limited to that domain. Resend shows it only once.
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
7. **Configure the app.** Copy the **Project URL** and the **publishable** key into `src/Hearthsheet.App/appsettings.json` under
   `Sync:Url` and `Sync:AnonKey`, then commit. Both are public by design.
   **Never** put the secret or `service_role` key in the app or the repository.
   - The **Connect** button in the project's top bar shows both. The URL is also under Project Settings → Data API, and the keys are under
     Project Settings → API Keys (the publishable key is `sb_publishable_…`; the old `anon` key on the Legacy API Keys tab also works).
   - Use the **bare project URL**, `https://<ref>.supabase.co`, with no trailing slash and no `/rest/v1/`. The app and the keep-alive
     workflow add their own paths, so a suffix makes every request fail with a 404.
8. **Keep the project awake.** In GitHub, go to Settings → Secrets and variables → Actions → **Variables** → New repository variable, and
   add `SUPABASE_URL` (the bare project URL) and `SUPABASE_ANON_KEY` (the publishable key). They are repository variables, not secrets and not
   environment variables. Then open Actions → **Supabase keep-alive** → **Run workflow** (or `gh workflow run "Supabase keep-alive"`).
   The workflow only appears there once it is on the default branch. A green run means the request reached the database. GitHub disables
   scheduled workflows after 60 days without repository activity; if that happens, re-enable it from the Actions tab.

## Manual test script

Use two data folders on one PC as two "installations":

```powershell
dotnet run --project src/Hearthsheet.App -- --Application:DataDirectory=$env:TEMP\hs-a
```

```powershell
dotnet run --project src/Hearthsheet.App -- --Application:DataDirectory=$env:TEMP\hs-b
```

Both installations share one Windows Credential Manager entry (`Hearthsheet/supabase-session`), so for steps 6–8 close the other installation first.

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
| 11 | With more than 500 characters in the account, sign in from a fresh installation (a new data folder). | Every character is pulled, not just the first 500. |

**Row-level security check** (SQL editor; replace the ids with two real user ids from Authentication → Users):

```sql
begin;
set local role authenticated;
set local request.jwt.claims = '{"sub":"<user B id>"}';
select count(*) from public.characters where user_id = '<user A id>';  -- must be 0
rollback;
```

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Account dialog: "The sign-in service did not return a session" | **Confirm email** is still on (step 3). Turn it off, then delete the half-created user under Authentication → Users and create the account again. |
| Keep-alive fails with exit code 22 | curl got an HTTP error. Check that `SUPABASE_URL` is the bare project URL (no `/rest/v1/`), that `SUPABASE_ANON_KEY` is the publishable key, and that the migration has been run. |
| Reset email doesn't arrive | Check the spam folder and Resend's Emails log. If the log is empty, recheck the SMTP settings in step 5. |
| Reset email contains a link instead of a code | The Reset password template still has the default body. Use the one in step 6. |

## How a sync pass works

`CloudSync.RunAsync` is guarded by a `SemaphoreSlim(1)`. A trigger that arrives while a pass is running is dropped, except the close
trigger, which waits for the running pass and shares the same 5 s cap.

Before the pass, `SyncViewModel` calls `MainViewModel.Save()` on the UI thread, so the open character's edits are on disk and `dirty`.

1. **Session.** Get an access token, refreshing if it has expired. A refresh rejection signs the user out, and the pass stops.
2. **Account link.**
   - If `sync_account` is empty, this is the first link on this machine. Run `ResetSyncState(markAllDirty: true)`
     (all local characters upload, `cloud_revision` cleared), set `sync_account` and clear `sync_last_pull`.
   - If `sync_account` differs from the signed-in user, the pass is paused until the user answers the account-switch prompt.
3. **Push deletes.** For each pending delete, `TombstoneIfRevision(id, cloud_revision)`.
   - Success, or the row is already a tombstone or missing: clear the entry.
   - Revision mismatch: the character was edited elsewhere after our last sync. Clear the entry; the pull restores it.
     A delete never destroys a newer remote edit.
4. **Push dirty characters.** For each dirty row:
   - `cloud_revision` null → `Insert`.
     - A 409 where `Get(id)` returns our row → treat it as a revision mismatch.
     - A 409 where `Get(id)` returns nothing → the id is used by another account. Log it and leave the character dirty.
       This should not happen, because an account switch gives every local character a new id first.
   - Otherwise → `UpdateIfRevision(id, cloud_revision, doc)`. This is a PATCH filtered on `id=eq.X&revision=eq.N`
     with `Prefer: return=representation`; an empty result means a mismatch.
   - Success → `MarkPushed`.
   - **Mismatch (conflict)** →
     1. Save the local version as a new character, with a new id and the name "Name (conflict copy, yyyy-MM-dd)".
        It is dirty and uploads next pass.
     2. Fetch the cloud row and apply it over the original id, provided nothing was saved since the push started
        (the `local_revision` guard). The cloud version wins.
     - If the cloud row is a tombstone, the edit beats the delete: rebase onto the tombstone's revision and stay dirty,
       so the next pass revives it. If the cloud row is gone (purged), reset to never-synced so it is inserted again.
   - A document over 10 MB is skipped with a warning and stays dirty.
5. **Pull.** `ChangedSince(sync_last_pull − 2 min)`, or a full pull when `sync_last_pull` is empty.
   - Rows whose `revision` equals the local `cloud_revision` are skipped, which makes the pull idempotent.
   - A tombstone → `ApplyRemoteDelete`.
   - Otherwise the row goes through the **untrusted-input pipeline** used by imports:
     1. 10 MB cap
     2. JSON depth 32
     3. a newer `schema_version` → skip, log, "Update Hearthsheet to sync <name>"
     4. migration
     5. typed deserialization
     6. clamping
     7. `Normalize()`

     Then `ApplyRemote`, keeping the cloud id.
   - A corrupt row is skipped with a warning; the others continue.
   - On success, set `sync_last_pull` to the largest `updated_at` seen.
6. **Stale machine** (checked before step 5 runs). If `sync_last_pull` is older than 80 days, tombstones may already have been purged. The pull becomes a
   full pull. Local characters with a `cloud_revision` whose id is absent from `ListAllIds()` are reset to never-synced and dirty,
   and re-upload on the next pass. Consequence: a character deleted elsewhere more than 90 days ago comes back on a machine that
   was offline that whole time. That is an accepted un-delete, never data loss.
7. **The open character is never written by the background pass.** Its autosave could otherwise write the stale
   in-memory model over a freshly applied remote version and then push it, silently discarding the remote edit.
   Remote changes and deletes for the open character's id are returned as **deferred changes** instead, and applied
   on the UI thread, where no save can interleave:
   - If the session has unsaved edits, or the row was saved since the last push, the in-memory version is first saved
     as a conflict copy.
   - The remote change is then applied (or the row deleted) unconditionally, and the view reloads.
   - Status: "Updated from another device" (plus "your edits were kept as a conflict copy"), or "Deleted on another
     device" (opening the kept copy if there was one).
   - If the user switched away from that character before the deferred change is applied, it is applied with the
     normal dirty guard instead.
8. **Notify.** Return `SyncResult(outcome, changedIds, deletedIds, deferred, conflictCopies, warnings)`. `SyncViewModel`
   applies the deferred changes and refreshes the list on the UI thread.

**Status values:** `Synced HH:mm`, `Syncing…`, `Offline` (network failure), `Cloud unavailable` (timeout or 5xx), `Signed out – sign in to resume sync`,
`Not signed in`. Failures retry at the next interval. They never block saving, loading or closing.

