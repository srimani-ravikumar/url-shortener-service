-- Local dev schema (SimpleStrategy RF=1). Use NetworkTopologyStrategy in production.
CREATE KEYSPACE IF NOT EXISTS urlshortener
  WITH replication = {'class': 'SimpleStrategy', 'replication_factor': 1};

USE urlshortener;

CREATE TABLE IF NOT EXISTS url_mappings (
  short_code text PRIMARY KEY,
  long_url   text,
  user_id    text,
  created_at timestamp,
  expires_at timestamp,
  is_custom  boolean
);

CREATE TABLE IF NOT EXISTS urls_by_user (
  user_id    text,
  created_at timestamp,
  short_code text,
  long_url   text,
  expires_at timestamp,
  PRIMARY KEY ((user_id), created_at, short_code)
) WITH CLUSTERING ORDER BY (created_at DESC, short_code ASC);

CREATE TABLE IF NOT EXISTS url_clicks (
  short_code  text PRIMARY KEY,
  click_count counter
);

CREATE TABLE IF NOT EXISTS url_last_access (
  short_code    text PRIMARY KEY,
  last_accessed timestamp
);

CREATE TABLE IF NOT EXISTS users (
  user_id    text PRIMARY KEY,
  email      text,
  created_at timestamp
);

CREATE TABLE IF NOT EXISTS users_by_api_key (
  -- lowercase-hex SHA-256 of the raw API key (UTF-8); the raw key is never stored
  api_key_hash text PRIMARY KEY,
  user_id      text
);

-- Lets rotate and delete find the user's current users_by_api_key row. Added after the first release,
-- hence ALTER ... IF NOT EXISTS (re-running this file must stay safe).
ALTER TABLE users ADD IF NOT EXISTS api_key_hash text;

-- One account per email (lower-cased and trimmed); written with IF NOT EXISTS to claim the address.
CREATE TABLE IF NOT EXISTS users_by_email (
  email   text PRIMARY KEY,
  user_id text
);

-- Pending email verifications. Only the SHA-256 of the emailed token is stored; the row expires by itself.
CREATE TABLE IF NOT EXISTS email_tokens (
  token_hash text PRIMARY KEY,
  purpose    text,
  email      text,
  created_at timestamp
);

CREATE TABLE IF NOT EXISTS id_counter_checkpoint (
  name  text PRIMARY KEY,
  value bigint
);
