-- Hearthsheet cloud sync. Run once in the Supabase SQL editor (see docs/CLOUD_SYNC.md).

create table public.characters (
  id             uuid primary key,
  user_id        uuid not null default auth.uid() references auth.users on delete cascade,
  name           text not null,
  description    text not null,
  schema_version int  not null,
  data           jsonb,                        -- null once deleted
  deleted_at     timestamptz,                  -- tombstone marker
  revision       bigint not null default 1,
  updated_at     timestamptz not null default now()
);
create index characters_user_updated on public.characters (user_id, updated_at);

-- The server owns revision, updated_at and ownership; clients cannot set them.
create function public.characters_bump() returns trigger
language plpgsql set search_path = '' as $$
begin
  new.revision   := case when tg_op = 'INSERT' then 1 else old.revision + 1 end;
  new.updated_at := now();
  if tg_op = 'UPDATE' then new.user_id := old.user_id; end if;
  return new;
end $$;

create trigger characters_bump before insert or update on public.characters
  for each row execute function public.characters_bump();

alter table public.characters enable row level security;
create policy own_select on public.characters for select to authenticated using (user_id = (select auth.uid()));
create policy own_insert on public.characters for insert to authenticated with check (user_id = (select auth.uid()));
create policy own_update on public.characters for update to authenticated
  using (user_id = (select auth.uid())) with check (user_id = (select auth.uid()));
-- No delete policy: clients tombstone, and only the purge job removes rows.

grant select, insert, update on public.characters to authenticated;

-- Tombstone retention: 90 days (the app treats a machine as stale after 80).
create extension if not exists pg_cron;
select cron.schedule('purge-character-tombstones', '17 3 * * *',
  $$delete from public.characters where deleted_at < now() - interval '90 days'$$);
