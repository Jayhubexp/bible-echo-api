begin;
create table if not exists public.echo_entries (
 id uuid primary key default gen_random_uuid(),
 kind text not null check(kind in ('media','songs','services')),
 body jsonb not null check(jsonb_typeof(body)='object'),
 revision integer not null default 1,
 owner_hash text,
 created_at timestamptz not null default now(),
 updated_at timestamptz not null default now()
);
-- One armed/live/paused service owns the shared audience output.
create unique index if not exists echo_active_projector on public.echo_entries ((true))
 where kind='services' and owner_hash is not null;
alter table public.echo_entries enable row level security;
revoke all on public.echo_entries from public, anon, authenticated;
grant select, insert, update, delete on public.echo_entries to service_role;
insert into storage.buckets(id,name,public,file_size_limit,allowed_mime_types)
values ('echo-media','echo-media',false,33554432,array['image/webp','image/png','image/jpeg','image/gif','video/mp4','video/webm'])
on conflict(id) do update set public=false,file_size_limit=excluded.file_size_limit,allowed_mime_types=excluded.allowed_mime_types;
commit;
