-- Apply once with a schema-owner role before starting the application.
-- The runtime role needs SELECT, INSERT, UPDATE on accounts and USAGE on accounts_id_seq.
BEGIN;
CREATE TABLE accounts (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    account_name varchar(64) COLLATE "C" NOT NULL UNIQUE,
    password_salt bytea NOT NULL CHECK (octet_length(password_salt) = 16),
    password_hash bytea NOT NULL CHECK (octet_length(password_hash) = 32),
    password_iterations integer NOT NULL CHECK (password_iterations = 600000),
    auth_epoch bigint NOT NULL DEFAULT 0 CHECK (auth_epoch >= 0),
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CHECK (char_length(account_name) BETWEEN 1 AND 64),
    CHECK (id > 0)
);
COMMIT;
