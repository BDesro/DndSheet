# Cloud sync design

Status: design approved in brainstorming, awaiting spec review.
Date: 2026-09-29

## 1. Goal and scope

Let a user sync their characters across installations through an optional account backed by Supabase.

**In scope:** email + password accounts, "Stay signed in", password reset by emailed code, background
sync of characters (including deletes) with conflict copies, a keep-alive workflow, a one-time setup guide.

**Out of scope:** OAuth logins (Discord, Google), real-time live updates, sharing characters between
accounts, cloud storage of anything other than characters, a web client.

**Fixed decisions**

| Decision | Choice |
|---|---|
| Model | Local-first. SQLite stays the source of truth; the app works fully offline and without an account. Sync is opt-in by signing in. |
| Backend | Supabase free tier: Auth + Postgres through its REST API (PostgREST). |
| Client | Plain `HttpClient`, no Supabase SDK, no new NuGet packages. |
| Sign-in | Email + password. Email confirmation **off**. |
| Email | Password-reset codes only, sent through Resend over SMTP from the user's Cloudflare-hosted domain. |
| Remember me | "Stay signed in", **checked by default**; refresh token kept in Windows Credential Manager. |
| Sync triggers | Startup, sign-in, every `Sync:IntervalMinutes` (default 5), on close (5 s cap), and manual (clicking the status-bar indicator). **Not** after each save. |
| Conflicts | Last write wins, and the losing version is kept locally as a conflict copy. |
| Deletes | Tombstones in the cloud, purged after 90 days by `pg_cron`. |
| Free-tier pausing | Mitigated by a daily GitHub Actions keep-alive request. |

## 2. Supabase side

One migration file, `supabase/migrations/0001_cloud_sync.sql`, committed to the repo.

```sql
create table public.characters (
  id             uuid primary key,
  user_id        uuid not null default auth.uid() references auth.users on delete cascade,
  name           text not null,
  description    text not null,
  schema_version int  not null,
  data           jsonb,                       -- null once deleted
  deleted_at     timestamptz,                 -- tombstone marker
  revision       bigint not null default 1,
  updated_at     timestamptz not null default now()
);
create index characters_user_updated on public.characters (user_id, updated_at);

-- Server owns revision and updated_at; clients cannot set them.
create function public.characters_bump() returns trigger language plpgsql as $$
begin
  new.revision   := case when tg_op = 'INSERT' then 1 else old.revision + 1 end;
  new.updated_at := now();
  new.user_id    := coalesce(old.user_id, new.user_id);  -- ownership never changes on update
  return new;
end $$;
create trigger characters_bump before insert or update on public.characters
  for each row execute function public.characters_bump();

alter table public.characters enable row level security;
create policy own_select on public.characters for select using (user_id = auth.uid());
create policy own_insert on public.characters for insert with check (user_id = auth.uid());
create policy own_update on public.characters for update using (user_id = auth.uid()) with check (user_id = auth.uid());
-- No delete policy: clients tombstone; only the purge job hard-deletes.

-- Tombstone retention (pg_cron is available on the free tier).
create extension if not exists pg_cron;
select cron.schedule('purge-character-tombstones', '17 3 * * *',
  $$delete from public.characters where deleted_at < now() - interval '90 days'$$);
```

**Auth settings (dashboard):** email confirmation off; minimum password length 8; custom SMTP → Resend;
the "Reset password" email template shows `{{ .Token }}` (a 6-digit code) instead of a link; default rate limits kept.

**Keys:** the project URL and **anon** key ship in `appsettings.json`. They are public by design, and RLS is the gatekeeper.
The service-role key is never used by the app or committed.

## 3. Local side

### 3.1 SQLite upgrade (`db_version` 1 → 2)

A stepwise upgrade in `SqliteCharacterRepository.Initialize`, in one transaction:

```sql
ALTER TABLE characters ADD COLUMN cloud_revision INTEGER;            -- null = never synced
ALTER TABLE characters ADD COLUMN dirty INTEGER NOT NULL DEFAULT 1;  -- changed since last sync
ALTER TABLE characters ADD COLUMN local_revision INTEGER NOT NULL DEFAULT 0;  -- bumped on every save
CREATE TABLE pending_deletes (id TEXT PRIMARY KEY, cloud_revision INTEGER NOT NULL);
```

`meta` gains the keys `sync_account` (the Supabase user id this database is linked to) and `sync_last_pull` (ISO timestamp).

### 3.2 Repository behaviour

- `Save` (every app save) sets `dirty = 1` and increments `local_revision`. It is unchanged otherwise.
- `Delete` also inserts into `pending_deletes` when the row had a `cloud_revision`.
- New sync-only operations, behind an `ISyncLocalStore` interface in Core that `SqliteCharacterRepository` implements:
  - `GetDirty()` returns id, `local_revision`, `cloud_revision` and the serialized document.
  - `MarkPushed(id, localRevision, cloudRevision)` sets `cloud_revision` and clears `dirty` **only if**
    `local_revision` still equals the pushed value. A save that lands during the push stays dirty.
  - `ApplyRemote(row)` upserts a pulled row with `dirty = 0` **only if** the local row is absent or not dirty.
    It returns whether it applied.
  - `ApplyRemoteDelete(id)` deletes locally **only if** the row is not dirty.
  - `GetPendingDeletes()`, `ClearPendingDelete(id)`.
  - `GetSyncMeta()`, `SetSyncMeta()`, and `ResetSyncState(markAllDirty)`, used for account linking.

Each is one SQL statement, so the WAL and busy-timeout guarantees already in place keep the UI thread's saves
and the background sync from corrupting each other.

## 4. Components

| Component | Project | Responsibility |
|---|---|---|
| `SyncOptions` | App/Composition | `Sync:Url`, `Sync:AnonKey`, `Sync:IntervalMinutes` (default 5). Sync is hidden when Url/AnonKey are empty. |
| `ICloudCharacterStore` | Core | `Insert`, `UpdateIfRevision`, `TombstoneIfRevision`, `Get(id)`, `ChangedSince(ts)`, `ListAllIds()`. |
| `ICloudSession` | Core | `GetAccessTokenAsync()` (refreshing as needed), `UserId`, `SignedOut` event. |
| `CloudSync` | Core | The sync pass (§5). Pure logic over `ISyncLocalStore`, `ICloudCharacterStore`, `ICloudSession`, `CharacterMigrator`. |
| `SupabaseAuth` | Infrastructure | GoTrue calls (§6), token lifetime, persistence through `ISecretStore`. Implements `ICloudSession`. |
| `SupabaseCharacterStore` | Infrastructure | PostgREST calls. Implements `ICloudCharacterStore`. |
| `SyncViewModel` | App | Timer, status text, manual sync command, marshalling sync events onto the dispatcher. |
| `AccountDialog` | App | Sign in / create account / forgot password (§7). |

HTTP rules, shared by both Infrastructure classes:
- HTTPS only, and only to the configured Supabase host.
- A 20 s timeout per request.
- Headers are `apikey: <anon>` and `Authorization: Bearer <access token>`.
- Error mapping:
  - network failure, timeout or 5xx → `CloudUnavailable`
  - 401 after one refresh attempt → `SignedOut`
  - anything else → an error with the status code, logged

## 5. Sync pass

`CloudSync.RunAsync` is guarded by a `SemaphoreSlim(1)`. A trigger that arrives while a pass is running is dropped, except the close
trigger, which waits for the running pass and shares the same 5 s cap.

Before the pass, `SyncViewModel` calls `MainViewModel.Save()` on the UI thread, so the open character's edits are on disk and `dirty`.

1. **Session.** Get an access token, refreshing if it has expired. A refresh rejection signs the user out, and the pass stops.
2. **Account link.**
   - If `sync_account` is empty, this is the first link on this machine. Run `ResetSyncState(markAllDirty: true)`
     (all local characters upload, `cloud_revision` cleared), set `sync_account` and clear `sync_last_pull`.
   - If `sync_account` differs from the signed-in user, the pass is paused until the user answers the account-switch prompt (§7).
3. **Push deletes.** For each pending delete, `TombstoneIfRevision(id, cloud_revision)`.
   - Success, or the row is already a tombstone or missing: clear the entry.
   - Revision mismatch: the character was edited elsewhere after our last sync. Clear the entry; the pull restores it.
     A delete never destroys a newer remote edit.
4. **Push dirty characters.** For each dirty row:
   - `cloud_revision` null → `Insert`.
     - A 409 where `Get(id)` returns our row → treat it as a revision mismatch.
     - A 409 where `Get(id)` returns nothing → the id belongs to another account (for example after "keep and upload" on
       an account switch). Give the local character a new id and insert again.
   - Otherwise → `UpdateIfRevision(id, cloud_revision, doc)`. This is a PATCH filtered on `id=eq.X&revision=eq.N`
     with `Prefer: return=representation`; an empty result means a mismatch.
   - Success → `MarkPushed`.
   - **Mismatch (conflict)** →
     1. Save the local version as a new character, with a new id and the name "Name (conflict copy, yyyy-MM-dd)".
        It is dirty and uploads next pass.
     2. Fetch the cloud row and apply it over the original id. For this one case `ApplyRemote` is forced, and the
        cloud version wins.
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
7. **Notify.** Raise `Completed(changedIds, deletedIds)`. `SyncViewModel` refreshes the list on the UI thread, and:
   - The open character changed remotely:
     - Session clean → reload it. Status: "Updated from another device".
     - Session dirty (edits typed during the pass) → save the in-memory version as a conflict copy, then reload.
   - The open character deleted remotely:
     - Session clean → close it with a notice.
     - Session dirty → keep it as a new, never-synced character (a conflict copy) and tell the user.

**Status values:** `Synced HH:mm`, `Syncing…`, `Offline`, `Cloud unavailable`, `Signed out – sign in to resume sync`,
`Not signed in`. Failures retry at the next interval. They never block saving, loading or closing.

## 6. Auth and "Stay signed in"

| Operation | Call |
|---|---|
| Create account | `POST /auth/v1/signup {email, password}` returns a session, because confirmation is off |
| Sign in | `POST /auth/v1/token?grant_type=password` |
| Refresh | `POST /auth/v1/token?grant_type=refresh_token` |
| Sign out | `POST /auth/v1/logout` (best effort) |
| Request reset code | `POST /auth/v1/recover {email}` |
| Verify code | `POST /auth/v1/verify {type: "recovery", email, token}` returns a session |
| Set new password | `PUT /auth/v1/user {password}` |

Token handling:
- **Access token:** kept in memory only, and refreshed 60 s before `expires_at` or after a 401.
- **Refresh token:** kept in memory. With "Stay signed in" checked it is also written to `ISecretStore` under the key
  `supabase-refresh`. Supabase rotates it on every refresh, so every new value is written back immediately. Refreshes are
  serialized by a lock, so rotation can never race.
- **Startup:** a stored refresh token → a silent refresh → signed in with no prompt. If the refresh fails with 400/401, the stored
  token is deleted and the state is `Signed out`. If it fails with a network error, the token is kept and the state is `Offline`.
- **Unchecked:** nothing is persisted, and any previously stored token is deleted.
- **Sign out:** best-effort logout, delete the stored token, stop the timer. Local characters and `sync_account` are kept.
- **Secrets:**
  - Passwords are cleared right after the request and never logged.
  - `SecretRedactor` gains a JWT pattern (`eyJ[\w-]+\.[\w-]+\.[\w-]+`) and the pattern `refresh_token`.

## 7. UI

- **Account menu** (next to Help):
  - Signed out: "Sign in…".
  - Signed in: the email (disabled), "Sync now" and "Sign out".
  - Hidden when sync isn't configured.
- **Status bar:** the sync indicator. Clicking it means "Sync now".
- **AccountDialog** has three modes, with shared styling from the GitHub token dialog, `AutomationProperties.Name` on every
  input, and inline errors instead of message boxes:
  - **Sign in:** email, password, "Stay signed in" (checked), a "Create account" link, a "Forgot password?" link.
  - **Create account:** email, password, confirm (with a mismatch check before any request).
  - **Forgot password:**
    1. Email, then "Send code".
    2. Code, new password and confirm, then "Reset". This verifies the code, sets the password and signs in.
- **Account-switch prompt**, shown when signing in as a different user than `sync_account`:
  - "This computer's characters were synced with another account."
  - **Upload them to this account:** `ResetSyncState(markAllDirty: true)`. The id-collision rule in §5.4 handles any ids that
    already exist under the other account.
  - **Keep them on this computer only:** `ResetSyncState(markAllDirty: false)`, after which only characters created or edited
    from now on upload.
  - **Cancel:** sign out.
  - Nothing is deleted in any case.

## 8. Keep-alive workflow

`.github/workflows/supabase-keepalive.yml`:
- Runs daily on a cron schedule, plus `workflow_dispatch`.
- Makes one `curl --fail` GET to `$SUPABASE_URL/rest/v1/characters?select=id&limit=1` with the anon key.
- The request touches the database. Under RLS it returns `[]`.
- `SUPABASE_URL` and `SUPABASE_ANON_KEY` are repository variables.

## 9. Documentation

- `docs/CLOUD_SYNC.md`, a one-time setup checklist:
  - create the project and run the migration
  - auth settings
  - Resend domain verification through Cloudflare DNS, and the SMTP settings
  - the recovery email template
  - the repository variables for the keep-alive workflow
  - the manual test script (§10.2)
- `docs/ARCHITECTURE.md`:
  - new §"Cloud sync"
  - update the security table, the dependency paragraph (still no new packages) and the known limitations
    (pausing, the 90-day un-delete, no live updates)

## 10. Testing

### 10.1 Automated (xUnit, no network)

- **`CloudSync`**, run against an in-memory `ICloudCharacterStore` fake and a real SQLite repository in a temp folder:
  - push new, push changed, pull new
  - a pull skips dirty rows
  - a save during a push stays dirty (`local_revision` guard)
  - a remote tombstone deletes the local copy; an offline delete reaches the cloud
  - a delete vs a newer remote edit keeps the edit
  - a conflict gives exactly one conflict copy, which uploads next pass
  - an insert id collision with another account re-ids the character
  - first link uploads everything; account switch in both modes
  - a newer-schema row is skipped; a corrupt or oversized row is skipped while the others sync
  - the stale-machine full pull re-uploads missing characters
  - passes don't overlap
  - a pass interrupted mid-way is resumed correctly by the next one
- **`SupabaseAuth` / `SupabaseCharacterStore`**, run with a fake `HttpMessageHandler`:
  - URLs, headers and the revision filter
  - a rotated refresh token is persisted
  - unchecking "Stay signed in" deletes the stored token
  - refresh 400 → signed out and the token deleted; network failure → offline and the token kept
  - 5xx and timeouts → `CloudUnavailable`
  - refusal of a non-HTTPS or foreign host
- **SQLite upgrade 1 → 2:** rows are kept and marked never-synced and dirty; version 2 opens cleanly.
- **`SecretRedactor`:** JWTs and refresh tokens are redacted.

### 10.2 Manual, against a real project (in `docs/CLOUD_SYNC.md`)

- **Two installations** on one PC through `--Application:DataDirectory`:
  - an edit syncs A → B
  - offline edits on both give a conflict copy
  - a delete on A disappears on B
  - a remote change to the character open on B
- **RLS:** a second account sees none of the first account's rows (SQL snippet provided).
- **Sessions:**
  - "Stay signed in" survives a restart; unchecked doesn't.
  - Sign out revokes the session.
  - Forgot password works end to end through Resend.
- **Keep-alive:** a `workflow_dispatch` run succeeds.
